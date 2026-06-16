using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace FfxivCollections;

/// <summary>What kind of collection entry an item unlocks (when it unlocks one).</summary>
public enum CollectibleKind
{
    None,
    Mount,
    Minion,
    TripleTriadCard
}

/// <summary>Which kind of place an item is held in (drives the display label + grouping).</summary>
public enum OwnershipSourceKind
{
    Inventory,
    ArmouryChest,
    Equipped,
    Saddlebag,
    Retainer,
    GlamourDresser,
    Armoire
}

/// <summary>
///     One place an item is held: the kind of source, a display label ("Inventory", "Retainer: Bob"), how many
///     sit there, and — for snapshotted sources the game wasn't keeping live — when it was captured.
/// </summary>
public readonly record struct ItemLocation(OwnershipSourceKind Kind, string Label, int Quantity, bool IsLive,
    DateTimeOffset? CapturedAt);

/// <summary>How many of an item the player holds across everything we can see, and where each lives.</summary>
public readonly record struct OwnershipInfo(int TotalCount, IReadOnlyList<ItemLocation> Locations);

/// <summary>Whether an item is in a storage source, and (for a cached source) how fresh that answer is.</summary>
public readonly record struct StorageStatus(bool Present, bool IsLive, DateTimeOffset? CapturedAt);

/// <summary>Whether an item grants a collectible (mount/minion/card) and, if so, whether it's already unlocked.</summary>
public readonly record struct CollectibleStatus(CollectibleKind Kind, bool Unlocked);

/// <summary>
///     Reads a player's item ownership / collection state. Some of it is always resident in memory and read
///     <b>live</b> on demand (bags, armoury chest, equipped gear, and the account-wide mount/minion/card unlock
///     state); the rest the game only keeps in memory while the source is open (retainers, the Glamour Dresser,
///     the Armoire, saddlebags), so this service <b>captures</b> each one into a persisted
///     <see cref="OwnershipStore" /> whenever it's loaded and <b>merges</b> those snapshots into the live answer
///     at query time, tagged with how stale they are.
///     <para>
///         Construct one per process and dispose it on unload. It subscribes to <see cref="IFramework" /> and,
///         on a throttle, captures whatever cache-on-view source is currently loaded for the logged-in
///         character. The store lives at a host-supplied <b>shared</b> directory so several plugins read/write
///         one file. All native access goes through <c>*.Instance()</c> and is null/bounds-guarded — an access
///         violation is uncatchable, so the checks are the protection. Everything runs on the framework thread.
///     </para>
/// </summary>
public sealed unsafe class ItemCollectionService : IDisposable
{
    // ItemAction "type" values for the unlockable collectibles an item can grant. In this Lumina version the
    // sheet's type column is surfaced as the ItemAction.Action RowRef, whose RowId is the raw type number;
    // Data[0] then carries the collectible's row id. Well-known mappings; want an in-game confirmation.
    private const uint MountActionType = 1322;
    private const uint MinionActionType = 853;
    private const uint TripleTriadCardActionType = 3357;

    // HQ items carry their id + this offset in some game tables; mask it off when comparing item ids.
    private const uint HqOffset = 1_000_000;

    // Capture at most this often (ms) — capturing is a cheap container scan, but there's no need per frame.
    private const long CaptureIntervalMs = 2000;

    // Live, always-resident containers (the current character's; read on every query).
    private static readonly InventoryType[] BagContainers =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private static readonly InventoryType[] ArmouryContainers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings
    ];

    private static readonly InventoryType[] SaddlebagContainers =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2
    ];

    // A retainer's stored inventory (the seven bag pages); equipped/market/crystals are excluded.
    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7
    ];

    private readonly IFramework _framework;
    private readonly IDataManager _data;
    private readonly OwnershipStore _store;

    private long _lastCaptureTick;

    public ItemCollectionService(IFramework framework, IDataManager data, string sharedDirectory,
        Action<string, Exception>? onError = null)
    {
        _framework = framework;
        _data = data;
        _store = new OwnershipStore(sharedDirectory, onError);

        _framework.Update += OnFrameworkUpdate;
    }

    // --- Capture (framework tick) ---------------------------------------------------------------------

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastCaptureTick < CaptureIntervalMs)
            {
                _store.SaveIfDirty(now);
                return;
            }

            _lastCaptureTick = now;

            var ownerId = LocalContentId();
            if (ownerId != 0)
                CaptureLoadedSources(ownerId);

            _store.SaveIfDirty(now);
        }
        catch
        {
            // Capture is best-effort; never throw out of the framework tick.
        }
    }

    /// <summary>Snapshot whichever cache-on-view sources are currently loaded for the logged-in character.</summary>
    private void CaptureLoadedSources(ulong ownerContentId)
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var mgr = InventoryManager.Instance();
        if (mgr is null) return;

        // The currently-summoned retainer's bags (only one retainer is in memory at a time).
        var retainerMgr = RetainerManager.Instance();
        if (retainerMgr is not null)
        {
            var active = retainerMgr->GetActiveRetainer();
            if (active is not null && active->RetainerId != 0)
            {
                var stacks = CollectStacks(mgr, RetainerContainers);
                if (stacks is not null)
                    // Label is the bare retainer name; the display layer gives each retainer its own colour
                    // and Kind == Retainer marks what it is.
                    _store.Capture($"retainer:{active->RetainerId}", OwnershipSourceKind.Retainer,
                        active->NameString, ownerContentId, stacks, nowUnix);
            }
        }

        // Saddlebags (regular + premium) — loaded once opened.
        var saddle = CollectStacks(mgr, SaddlebagContainers);
        if (saddle is not null)
            _store.Capture($"saddlebag:{ownerContentId}", OwnershipSourceKind.Saddlebag, "Saddlebag",
                ownerContentId, saddle, nowUnix);

        // Glamour Dresser (Prism Box) — store each held item id exactly as the box reports it (qty 1; the
        // dresser holds singles). We deliberately do NOT expand "outfit"/set items into their component pieces:
        // the box exposes no per-set "which pieces are present" mask, so a partially-deposited set (e.g. 2/7)
        // would be over-reported as the full set. See the dresser-outfit note in CLAUDE.md.
        var mirage = MirageManager.Instance();
        if (mirage is not null)
        {
            var ids = mirage->PrismBoxItemIds;
            List<OwnershipStore.StoredStack>? dresser = null;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] == 0) continue;
                (dresser ??= []).Add(new OwnershipStore.StoredStack { ItemId = Mask(ids[i]), Quantity = 1 });
            }

            if (dresser is not null)
                _store.Capture($"dresser:{ownerContentId}", OwnershipSourceKind.GlamourDresser, "Glamour Dresser",
                    ownerContentId, dresser, nowUnix);
        }

        // Armoire (Cabinet) — store every cabinet item the player has deposited.
        var uiState = UIState.Instance();
        if (uiState is not null)
        {
            var cabinet = &uiState->Cabinet;
            if (cabinet->IsCabinetLoaded())
            {
                List<OwnershipStore.StoredStack>? armoire = null;
                foreach (var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
                {
                    if (row.Item.RowId == 0) continue;
                    if (!cabinet->IsItemInCabinet(row.RowId)) continue;
                    (armoire ??= []).Add(new OwnershipStore.StoredStack { ItemId = row.Item.RowId, Quantity = 1 });
                }

                if (armoire is not null)
                    _store.Capture($"armoire:{ownerContentId}", OwnershipSourceKind.Armoire, "Armoire",
                        ownerContentId, armoire, nowUnix);
            }
        }
    }

    /// <summary>Sum item stacks across the given containers, or <c>null</c> if none are loaded (so we don't
    /// overwrite a snapshot with an empty one just because the source isn't in memory right now).</summary>
    private static List<OwnershipStore.StoredStack>? CollectStacks(InventoryManager* mgr, InventoryType[] types)
    {
        List<OwnershipStore.StoredStack>? stacks = null;
        var anyLoaded = false;

        foreach (var type in types)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded) continue;
            anyLoaded = true;

            var size = container->Size;
            for (var i = 0; i < size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0) continue;
                (stacks ??= []).Add(new OwnershipStore.StoredStack
                    { ItemId = Mask(slot->ItemId), Quantity = slot->Quantity });
            }
        }

        if (!anyLoaded) return null;
        return stacks ?? [];
    }

    // --- Query ----------------------------------------------------------------------------------------

    /// <summary>
    ///     Where the player holds <paramref name="itemId" />: the always-live containers read now, plus the
    ///     captured retainer / saddlebag snapshots merged in (tagged with their capture time). Each
    ///     <see cref="ItemLocation" /> is one source with its quantity summed. Returns <c>false</c> when the
    ///     item is held nowhere.
    /// </summary>
    public bool TryGetOwnership(uint itemId, out OwnershipInfo info)
    {
        info = default;
        if (itemId == 0) return false;

        var locations = new List<ItemLocation>(4);
        var total = 0;

        // Live, always-resident containers for the current character.
        var bags = CountLive(itemId, BagContainers);
        if (bags > 0) { locations.Add(new ItemLocation(OwnershipSourceKind.Inventory, "Inventory", bags, true, null)); total += bags; }

        var armoury = CountLive(itemId, ArmouryContainers);
        if (armoury > 0) { locations.Add(new ItemLocation(OwnershipSourceKind.ArmouryChest, "Armoury Chest", armoury, true, null)); total += armoury; }

        var equipped = CountLive(itemId, [InventoryType.EquippedItems]);
        if (equipped > 0) { locations.Add(new ItemLocation(OwnershipSourceKind.Equipped, "Equipped", equipped, true, null)); total += equipped; }

        // Saddlebag: live if it's loaded right now, otherwise fall back to its snapshot.
        var saddleLive = SaddlebagLoaded();
        if (saddleLive)
        {
            var saddle = CountLive(itemId, SaddlebagContainers);
            if (saddle > 0) { locations.Add(new ItemLocation(OwnershipSourceKind.Saddlebag, "Saddlebag", saddle, true, null)); total += saddle; }
        }

        // Stored snapshots: retainers always (never live here), saddlebag only if it wasn't read live above.
        foreach (var source in _store.Sources)
        {
            if (source.Kind == OwnershipSourceKind.Retainer || (source.Kind == OwnershipSourceKind.Saddlebag && !saddleLive))
            {
                var qty = SumStored(source, itemId);
                if (qty <= 0) continue;
                locations.Add(new ItemLocation(source.Kind, source.Label, qty, false,
                    DateTimeOffset.FromUnixTimeSeconds(source.CapturedAtUnix)));
                total += qty;
            }
        }

        if (total <= 0) return false;
        info = new OwnershipInfo(total, locations);
        return true;
    }

    /// <summary>Whether <paramref name="itemId" /> is in the Glamour Dresser — live if loaded, else from the
    /// last snapshot.</summary>
    public StorageStatus GetGlamourDresserStatus(uint itemId)
    {
        if (itemId == 0) return default;

        var mirage = MirageManager.Instance();
        if (mirage is not null)
        {
            var ids = mirage->PrismBoxItemIds;
            var loaded = false;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] == 0) continue;
                loaded = true;
                if (Mask(ids[i]) == itemId) return new StorageStatus(true, true, null);
            }

            if (loaded) return new StorageStatus(false, true, null); // dresser is loaded and doesn't hold it
        }

        return StoredStatus(OwnershipSourceKind.GlamourDresser, itemId);
    }

    /// <summary>Whether <paramref name="itemId" /> is in the Armoire — live if the cabinet is loaded, else from
    /// the last snapshot.</summary>
    public StorageStatus GetArmoireStatus(uint itemId)
    {
        if (itemId == 0) return default;

        var uiState = UIState.Instance();
        if (uiState is not null)
        {
            var cabinet = &uiState->Cabinet;
            if (cabinet->IsCabinetLoaded())
            {
                foreach (var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
                    if (row.Item.RowId == itemId)
                        return new StorageStatus(cabinet->IsItemInCabinet(row.RowId), true, null);
                return new StorageStatus(false, true, null); // not an armoire-eligible item
            }
        }

        return StoredStatus(OwnershipSourceKind.Armoire, itemId);
    }

    /// <summary>
    ///     Whether <paramref name="itemId" /> grants a mount, minion, or Triple Triad card and, if so, whether
    ///     it's already unlocked (account-wide state, always live). Returns <see cref="CollectibleKind.None" />
    ///     for ordinary items.
    /// </summary>
    public CollectibleStatus GetCollectibleStatus(uint itemId)
    {
        var none = new CollectibleStatus(CollectibleKind.None, false);
        if (itemId == 0) return none;

        if (!_data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return none;

        var action = item.ItemAction;
        if (action.RowId == 0 || !action.IsValid) return none;

        var row = action.Value;
        var actionData = row.Data;
        if (actionData.Count == 0) return none;
        var collectibleId = actionData[0];

        switch (row.Action.RowId)
        {
            case MountActionType:
            {
                var ps = PlayerState.Instance();
                var unlocked = ps is not null && ps->IsMountUnlocked(collectibleId);
                return new CollectibleStatus(CollectibleKind.Mount, unlocked);
            }
            case MinionActionType:
            {
                var ui = UIState.Instance();
                var unlocked = ui is not null && ui->IsCompanionUnlocked(collectibleId);
                return new CollectibleStatus(CollectibleKind.Minion, unlocked);
            }
            case TripleTriadCardActionType:
            {
                var ui = UIState.Instance();
                var unlocked = ui is not null && ui->IsTripleTriadCardUnlocked(collectibleId);
                return new CollectibleStatus(CollectibleKind.TripleTriadCard, unlocked);
            }
            default:
                return none;
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static uint Mask(uint id) => id >= HqOffset ? id - HqOffset : id;

    /// <summary>The logged-in character's content id (0 when not logged in), used to key per-character
    /// snapshots and tag retainer owners.</summary>
    private static ulong LocalContentId()
    {
        var player = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
        return player is null ? 0 : player->ContentId;
    }

    /// <summary>Sum how many of <paramref name="itemId" /> sit across the given live containers (HQ masked).</summary>
    private static int CountLive(uint itemId, InventoryType[] types)
    {
        var mgr = InventoryManager.Instance();
        if (mgr is null) return 0;

        var total = 0;
        foreach (var type in types)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded) continue;

            var size = container->Size;
            for (var i = 0; i < size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0) continue;
                if (Mask(slot->ItemId) == itemId) total += slot->Quantity;
            }
        }

        return total;
    }

    private static bool SaddlebagLoaded()
    {
        var mgr = InventoryManager.Instance();
        if (mgr is null) return false;
        foreach (var type in SaddlebagContainers)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container is not null && container->IsLoaded) return true;
        }

        return false;
    }

    private static int SumStored(OwnershipStore.StoredSource source, uint itemId)
    {
        var total = 0;
        foreach (var stack in source.Stacks)
            if (stack.ItemId == itemId)
                total += stack.Quantity;
        return total;
    }

    /// <summary>Look up the first stored source of a given kind that holds the item (for dresser/armoire, which
    /// are one snapshot per character).</summary>
    private StorageStatus StoredStatus(OwnershipSourceKind kind, uint itemId)
    {
        foreach (var source in _store.Sources)
        {
            if (source.Kind != kind) continue;
            foreach (var stack in source.Stacks)
                if (stack.ItemId == itemId)
                    return new StorageStatus(true, false, DateTimeOffset.FromUnixTimeSeconds(source.CapturedAtUnix));
        }

        return default;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _store.Save();
    }
}
