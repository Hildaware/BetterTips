using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     The primary, signature-free hide path. Listens to the <c>ItemDetail</c> addon through Dalamud's
///     <see cref="IAddonLifecycle" /> and toggles the visibility of the named node groups
///     (<see cref="ItemDetailGroup" />) for whichever sections the user has hidden. No game-function
///     signature is involved — this is the same technique HUDUnlimited/KamiToolKit use. We re-apply after
///     every requested-update and refresh because the game re-shows groups per hovered item.
///     <para>
///         Crash-safety: the callback runs on the framework thread (same as <see cref="Dispose" />), so no
///         in-flight call can race teardown. Every native access is null-checked first — and note that the
///         group pointers are *legitimately* null for items lacking that group (a potion has no durability
///         group), so the checks are normal control flow, not merely defensive. An access violation can't
///         be caught, so these checks, not the try/catch, are what keep the client alive.
///     </para>
/// </summary>
public sealed unsafe class ItemTooltipNodeController : IDisposable
{
    private const string AddonName = "ItemDetail";

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    // Recomputed on config change; replaced as a whole reference (atomic) so the callback never sees a
    // half-mutated collection.
    private ItemDetailGroup[] _hiddenGroups = [];

    public ItemTooltipNodeController(IAddonLifecycle addonLifecycle, Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _config = config;
        _log = log;
        Rebuild();

        _addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPostUpdate);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPostUpdate);
    }

    /// <summary>Recompute which node groups to hide from the current config. Call after any change.</summary>
    public void Rebuild()
    {
        _hiddenGroups = TooltipFieldMap.GroupsFor(_config.HiddenSections);
    }

    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (!_config.Enabled) return;

        var groups = _hiddenGroups;
        if (groups.Length == 0) return;

        try
        {
            var addon = (AddonItemDetail*)args.Addon.Address;
            if (addon is null) return;

            foreach (var group in groups)
            {
                var node = ResolveGroupNode(addon, group);
                if (node is not null)
                    node->ToggleVisibility(false);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error hiding tooltip node group (tooltip left as-is).");
        }
    }

    private static AtkResNode* ResolveGroupNode(AddonItemDetail* addon, ItemDetailGroup group) => group switch
    {
        ItemDetailGroup.HeaderStats => addon->HeaderStatsGroup,
        ItemDetailGroup.SpiritbondConditionCrest => addon->SpiritbondConditionCrestGroup,
        ItemDetailGroup.EquipRestriction => addon->EquipRestrictionGroup,
        ItemDetailGroup.Materialize => (AtkResNode*)addon->MaterializeText,
        _ => null
    };

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPostUpdate);
            _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPostUpdate);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error unregistering tooltip node listeners.");
        }
    }
}
