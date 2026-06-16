using Dalamud.Plugin.Services;
using FfxivCollections;

namespace BetterTips.Tooltips;

/// <summary>One row of the "Possessions" section: a leading icon, the text, the row's colour, and an optional
/// trailing status icon drawn after the text (0 = none — e.g. the "collected" checkmark).</summary>
public sealed record OwnershipEntry(uint IconId, string Text, Vector4 Color, uint TrailingIcon = 0);

/// <summary>
///     The data the "Possessions" section renders: where the hovered item is already in the player's
///     possession. It comes from the shared <see cref="ItemCollectionService" /> — live for the always-resident
///     sources (bags, armoury, equipped, and mount/minion/card unlock state) and from persisted snapshots for
///     the cache-on-view sources (retainers, Glamour Dresser, Armoire, saddlebags). Each row is formatted
///     <c>"{type}: {count}"</c> and coloured per source type, with each retainer getting its own colour.
/// </summary>
public sealed record OwnershipData(IReadOnlyList<OwnershipEntry> Entries)
{
    // The game icon ids for each row type (all user-chosen).
    public const uint InventoryIcon = 60911;
    public const uint ArmouryIcon = 60459;
    public const uint EquippedIcon = 60415;
    public const uint SaddlebagIcon = 60581;
    public const uint RetainerIcon = 60425;
    public const uint GlamourDresserIcon = 60473;
    public const uint ArmoireIcon = 60460;
    public const uint CollectibleIcon = 60404;

    /// <summary>Drawn after a collectible's type: a checkmark when collected, a cross when not.</summary>
    public const uint CollectedIcon = 230419;
    public const uint NotCollectedIcon = 230420;

    // One colour per source type (Inventory is the light yellow the user asked for). Retainers cycle through
    // their own palette (max ~7 retainers), so each reads distinctly.
    public static readonly Vector4 InventoryColor = new(1f, 0.96f, 0.55f, 1f);      // light yellow
    public static readonly Vector4 ArmouryColor = new(0.62f, 0.82f, 1f, 1f);        // light blue
    public static readonly Vector4 EquippedColor = new(0.60f, 1f, 0.65f, 1f);       // light green
    public static readonly Vector4 SaddlebagColor = new(0.95f, 0.78f, 0.50f, 1f);   // tan
    public static readonly Vector4 DresserColor = new(1f, 0.70f, 0.88f, 1f);        // pink
    public static readonly Vector4 ArmoireColor = new(0.60f, 0.92f, 0.90f, 1f);     // teal
    public static readonly Vector4 CollectibleColor = new(0.80f, 0.74f, 1f, 1f);    // lavender

    /// <summary>Distinct colours for each retainer (cycled by retainer index; FFXIV allows up to ~7).</summary>
    public static readonly Vector4[] RetainerColors =
    [
        new(1f, 0.62f, 0.55f, 1f),    // salmon
        new(0.72f, 0.85f, 0.45f, 1f), // lime
        new(0.55f, 0.80f, 0.95f, 1f), // sky
        new(0.95f, 0.80f, 0.40f, 1f), // amber
        new(0.85f, 0.65f, 0.95f, 1f), // violet
        new(0.50f, 0.90f, 0.78f, 1f), // mint
        new(0.95f, 0.60f, 0.78f, 1f)  // rose
    ];

    /// <summary>Whether the section has anything to show.</summary>
    public bool HasContent => Entries.Count > 0;

    /// <summary>
    ///     Build the rows for the current hover, or <c>null</c> when there's nothing to report. Reads live +
    ///     persisted state via <paramref name="collections" />; the hovered item comes from
    ///     <see cref="IGameGui.HoveredItem" /> with the HQ offset masked off. Capture timestamps are
    ///     deliberately not shown.
    /// </summary>
    public static OwnershipData? FromHovered(IGameGui gameGui, ItemCollectionService collections)
    {
        var hovered = gameGui.HoveredItem;
        if (hovered == 0) return null;

        var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
        if (itemId == 0) return null;

        var entries = new List<OwnershipEntry>(6);

        if (collections.TryGetOwnership(itemId, out var owned))
        {
            var retainerIndex = 0;
            foreach (var loc in owned.Locations)
            {
                var color = loc.Kind == OwnershipSourceKind.Retainer
                    ? RetainerColors[retainerIndex++ % RetainerColors.Length]
                    : ColorForKind(loc.Kind);
                entries.Add(new OwnershipEntry(IconForKind(loc.Kind), $"{loc.Label}: {loc.Quantity}", color));
            }
        }

        if (collections.GetGlamourDresserStatus(itemId).Present)
            entries.Add(new OwnershipEntry(GlamourDresserIcon, "Glamour Dresser", DresserColor));

        if (collections.GetArmoireStatus(itemId).Present)
            entries.Add(new OwnershipEntry(ArmoireIcon, "Armoire", ArmoireColor));

        var collectible = collections.GetCollectibleStatus(itemId);
        if (collectible.Kind != CollectibleKind.None)
        {
            var label = collectible.Kind switch
            {
                CollectibleKind.Mount => "Mount",
                CollectibleKind.Minion => "Minion",
                CollectibleKind.TripleTriadCard => "Card",
                _ => "Collectible"
            };
            // Trailing icon after the type: checkmark when collected, cross when not.
            entries.Add(new OwnershipEntry(CollectibleIcon, $"{label}:", CollectibleColor,
                collectible.Unlocked ? CollectedIcon : NotCollectedIcon));
        }

        return entries.Count > 0 ? new OwnershipData(entries) : null;
    }

    /// <summary>The row icon for a source kind.</summary>
    public static uint IconForKind(OwnershipSourceKind kind) => kind switch
    {
        OwnershipSourceKind.Inventory => InventoryIcon,
        OwnershipSourceKind.ArmouryChest => ArmouryIcon,
        OwnershipSourceKind.Equipped => EquippedIcon,
        OwnershipSourceKind.Saddlebag => SaddlebagIcon,
        OwnershipSourceKind.Retainer => RetainerIcon,
        OwnershipSourceKind.GlamourDresser => GlamourDresserIcon,
        OwnershipSourceKind.Armoire => ArmoireIcon,
        _ => InventoryIcon
    };

    /// <summary>The fixed colour for a non-retainer source kind.</summary>
    public static Vector4 ColorForKind(OwnershipSourceKind kind) => kind switch
    {
        OwnershipSourceKind.Inventory => InventoryColor,
        OwnershipSourceKind.ArmouryChest => ArmouryColor,
        OwnershipSourceKind.Equipped => EquippedColor,
        OwnershipSourceKind.Saddlebag => SaddlebagColor,
        OwnershipSourceKind.GlamourDresser => DresserColor,
        OwnershipSourceKind.Armoire => ArmoireColor,
        _ => InventoryColor
    };
}
