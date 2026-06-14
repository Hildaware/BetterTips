namespace BetterTips.Tooltips;

/// <summary>
///     A reorderable top-level tooltip section. Each maps to one or more stacked block nodes (direct
///     children of root <c>#1</c>). The fixed header (name/icon) and the pinned control-hints row are not
///     here — only the content blocks between them, which the user may reorder.
/// </summary>
public enum LayoutSection
{
    DamageDefense,
    ItemLevelClassJob,
    AttributeBonuses,
    Effects,
    Description,
    Materia,
    CraftingRepairs,
    Requirements,
    VendorMarket,

    // Our own added sections (no native block — positioned specially). Append-only, like the rest.
    GearSets
}

/// <summary>Display label + the block node id(s) a <see cref="LayoutSection" /> moves as a unit.</summary>
public sealed record LayoutSectionInfo(LayoutSection Id, string Label, uint[] BlockIds);

/// <summary>
///     The reorderable content sections and their block node ids, read from live <c>/btips dumpnodes</c>
///     output. Node ids are version-specific (same caveat as <see cref="TooltipFieldMap" />) — re-dump and
///     update if a major UI patch lands. Blocks here are the top-level children of root <c>#1</c> that the
///     relayout engine restacks; the header (<c>#17</c>/<c>#7</c>) and control hints (<c>#2</c>) are excluded.
/// </summary>
public static class TooltipLayout
{
    /// <summary>
    ///     The fixed header block (item name + category line). The relayout uses its bottom edge
    ///     (<c>Y + Height</c>) as the stable anchor below which all content is stacked — it's never moved,
    ///     so it can't drift. Version-specific (read from <c>/btips dumpnodes</c>); if it can't be resolved
    ///     the relayout bails and leaves the tooltip untouched rather than laying out from a bad anchor.
    /// </summary>
    public const uint HeaderBlockId = 17;

    /// <summary>Alternate header ids to try if <see cref="HeaderBlockId" /> isn't present this patch.</summary>
    public static readonly uint[] HeaderFallbackIds = [7];

    /// <summary>The control-hints (keybind) row, pinned to the bottom; the relayout places it last.</summary>
    public const uint ControlHintsBlockId = 2;

    /// <summary>
    ///     Sub-nodes of the header (<c>#17</c>) the "Unified item header" enhancement hides when it replaces
    ///     the header: the name (<c>#33</c>), icon component (<c>#32</c>), quantity "(Total: n)" (<c>#34</c>),
    ///     category line (<c>#35</c>), and the put-in indicators (<c>#24</c>). The binding/untradable/unique
    ///     line (<see cref="BindingLineBlockId" />) is deliberately left alone.
    /// </summary>
    public static readonly uint[] UnifiedHeaderHiddenNodeIds = [32, 33, 34, 35, 24];

    /// <summary>The header's binding/untradable/unique line (<c>#20</c>) — kept visible; the unified block is
    /// placed just below it.</summary>
    public const uint BindingLineBlockId = 20;

    /// <summary>The durability/spiritbond gauge block (<c>#7</c>, two vertical bars). The Unified item header
    /// re-positions it to sit directly left of its icon (it's otherwise left floating at the old header top).</summary>
    public const uint GaugeBlockId = 7;

    public static readonly IReadOnlyList<LayoutSectionInfo> Sections =
    [
        // List order is the default top-to-bottom (best-effort match for the game's natural order). It only
        // drives the UI's starting order + the "is it customized?" check — while the saved order equals this,
        // the engine doesn't reorder at all. Enum *values* (used for serialization) are independent of this.
        new(LayoutSection.DamageDefense,     "Damage / Defense",            [36]),
        new(LayoutSection.ItemLevelClassJob, "Item Level, Class/Job & Lv.", [62]),
        new(LayoutSection.Description,        "Description",                 [40]),
        new(LayoutSection.AttributeBonuses,  "Bonuses",                     [97]),
        new(LayoutSection.Materia,            "Materia",                     [93]),
        new(LayoutSection.Effects,            "Effects",                     [49]),
        new(LayoutSection.CraftingRepairs,    "Crafting & Repairs",          [68]),
        // Requirements (block #53) is intentionally omitted: equip requirements already live in the Item
        // Level / Class block, and the native #53 "Requirements" block (Base Item / Catalyst) is a niche
        // crafting-material line, not a real reorderable section for the user. The enum member stays for
        // serialization safety.
        new(LayoutSection.VendorMarket,       "Vendor / Market",             [43, 47]),
        // No block ids — this is BetterTips' own gear-set block, built by GearSetBlockProvider and laid out
        // by TooltipRelayoutController at this order slot (not a native node addressed by id).
        new(LayoutSection.GearSets,           "Gear Sets",                   [])
    ];

    /// <summary>Our own non-native sections (positioned by their controllers, not the reorder pass).</summary>
    public static bool IsCustom(LayoutSection id) => id is LayoutSection.GearSets;

    /// <summary>The default top-to-bottom order (best-effort match for the game's natural order).</summary>
    public static readonly LayoutSection[] DefaultOrder = Sections.Select(s => s.Id).ToArray();

    public static LayoutSectionInfo? Find(LayoutSection id)
        => Sections.FirstOrDefault(s => s.Id == id);
}
