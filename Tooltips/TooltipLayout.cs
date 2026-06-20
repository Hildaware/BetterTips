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
    GearSets,

    // The crafter-signature line (#4, a bare Text node showing the crafter's name on signed/HQ player-crafted
    // items). Appended last to keep existing serialized enum values stable; its slot in the default top-to-
    // bottom order is set by its position in the Sections list below, not here.
    CrafterSignature,

    // BetterTips' own "Glamour" section (no native block — positioned specially, like GearSets): the
    // glamoured-appearance name + applied dye channels. Append-only.
    Glamour,

    // BetterTips' own "Condition" section (no native block — positioned specially, like GearSets/Glamour): a
    // single horizontal row of durability + spiritbond (per-instance, read from the gauge's own value nodes)
    // + sell price (Lumina), each an icon with its value. Replaces the native durability/spiritbond gauge
    // bars (#7), which the relayout hides when this section shows. Append-only.
    Condition,

    // BetterTips' own "Description" section — Enhanced-only (not in the modifier Sections list). A simple,
    // headerless, left-aligned render of the item's lore description sourced from Lumina (Item.Description),
    // which naturally excludes the per-instance noise the native description block (#40) overlays — applied
    // dyes and the "Advanced Melding Forbidden" notice. Positioned specially like the other custom sections.
    // Append-only.
    EnhancedDescription,

    // BetterTips' own "Ownership" section (no native block — positioned specially, like GearSets/Glamour):
    // where the hovered item is already in the player's possession (owned count + location, Glamour Dresser,
    // Armoire, and whether its mount/minion/Triple Triad card is unlocked). Live game state read via the
    // shared FfxivCollections library. Append-only.
    Ownership,

    // BetterTips' own Enhanced-only "Effects" section — a headerless, tight render of the item's effect lines
    // (food/potion) with numbers coloured green, scraped from the native Effects block (#49, which Enhanced
    // hides). Distinct from the native LayoutSection.Effects. Positioned specially, above Description.
    // Append-only.
    EnhancedEffects
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

    /// <summary>The crafter-signature line (<c>#4</c>, a bare Text node showing the crafter's name on signed/HQ
    /// player-crafted items) — managed as the <see cref="LayoutSection.CrafterSignature" /> section.</summary>
    public const uint CrafterSignatureBlockId = 4;

    /// <summary>The description block (<c>#40</c>), where the game prints dye channels for dyeable gear. The
    /// "Glamour" section folds those in, so the relayout hides this block when the dye marker is present.</summary>
    public const uint DescriptionBlockId = 40;

    /// <summary>The marker the dye lines begin with in the description block (English-only, case-sensitive on
    /// the game's own text) — used to hide #40 only when it's showing dyes (not real lore).</summary>
    public const string DyeLineMarker = "Dye 1:";

    /// <summary>The async placeholder the game shows in <see cref="CrafterSignatureBlockId" /> while it resolves
    /// the crafter's name; the relayout suppresses the line until it becomes the real name. Matched
    /// case-insensitively (English-only, like the rest of the version-specific text matching).</summary>
    public const string SignaturePlaceholder = "Obtaining signature";

    /// <summary>
    ///     Sub-nodes of the header (<c>#17</c>) the "Unified item header" enhancement hides when it replaces
    ///     the header: the name (<c>#33</c>), icon component (<c>#32</c>), quantity "(Total: n)" (<c>#34</c>),
    ///     and the category line (<c>#35</c>). The binding/untradable/unique line
    ///     (<see cref="BindingLineBlockId" />) is deliberately left alone, and so are the top-right
    ///     storage-location indicators (<c>#24</c> — crest-applicable / Glamour Dresser / Armoire / Cabinet
    ///     icons): the redesigned header doesn't re-render them, so hiding them just dropped them entirely.
    /// </summary>
    public static readonly uint[] UnifiedHeaderHiddenNodeIds = [32, 33, 34, 35];

    /// <summary>The native item-name text node (<c>#33</c>) inside the header. The unified header reads its
    /// <b>rendered</b> SeString (with the game's payload glyphs — HQ mark, etc.) so its own name line shows
    /// them too, rather than the payload-free Lumina name.</summary>
    public const uint ItemNameNodeId = 33;

    /// <summary>The header's binding/untradable/unique line (<c>#20</c>) — kept visible; the unified block is
    /// placed just below it.</summary>
    public const uint BindingLineBlockId = 20;

    /// <summary>The durability/spiritbond gauge block (<c>#7</c>, two vertical bars). The relayout <b>hides</b>
    /// it when the "Condition" section (its redesigned replacement) or the unified item header is active — it's
    /// never moved.</summary>
    public const uint GaugeBlockId = 7;

    public static readonly IReadOnlyList<LayoutSectionInfo> Sections =
    [
        // List order is the default top-to-bottom (best-effort match for the game's natural order). It only
        // drives the UI's starting order + the "is it customized?" check — while the saved order equals this,
        // the engine doesn't reorder at all. Enum *values* (used for serialization) are independent of this.
        new(LayoutSection.DamageDefense,     "Damage / Defense",            [36]),
        new(LayoutSection.ItemLevelClassJob, "Item Level, Class/Job & Lv.", [62]),
        new(LayoutSection.Description,        "Description",                 [40]),
        // No block ids — BetterTips' own Glamour block (appearance name + dyes), built by GlamourBlockProvider
        // and laid out by the relayout at this slot. Sits after Description since its dyes come from #40.
        new(LayoutSection.Glamour,            "Glamour",                     []),
        new(LayoutSection.AttributeBonuses,  "Bonuses",                     [97]),
        new(LayoutSection.Materia,            "Materia",                     [93]),
        new(LayoutSection.Effects,            "Effects",                     [49]),
        new(LayoutSection.CraftingRepairs,    "Crafting & Repairs",          [68]),
        // No block ids — BetterTips' own Possessions list (owned count + location, dresser, armoire,
        // collectible), built by OwnershipBlockProvider and laid out by the relayout at this slot. Sits above
        // Condition.
        new(LayoutSection.Ownership,          "Possessions",                 []),
        // No block ids — BetterTips' own Condition row (durability + spiritbond + sell price), built by
        // ConditionBlockProvider and laid out by the relayout at this slot. Replaces the native gauge bars (#7).
        new(LayoutSection.Condition,          "Condition",                   []),
        // Requirements (block #53) is intentionally omitted: equip requirements already live in the Item
        // Level / Class block, and the native #53 "Requirements" block (Base Item / Catalyst) is a niche
        // crafting-material line, not a real reorderable section for the user. The enum member stays for
        // serialization safety.
        new(LayoutSection.VendorMarket,       "Vendor / Market",             [43, 47]),
        // The crafter signature (#4): a bare Text node with the crafter's name, sitting at the very bottom of
        // the natural layout. A managed section so the relayout restacks it inside the resized window instead
        // of letting it float below (otherwise the placeholder "Obtaining signature…" / name renders outside).
        new(LayoutSection.CrafterSignature,   "Crafter Signature",           [CrafterSignatureBlockId]),
        // No block ids — this is BetterTips' own gear-set block, built by GearSetBlockProvider and laid out
        // by TooltipRelayoutController at this order slot (not a native node addressed by id).
        new(LayoutSection.GearSets,           "Gear Sets",                   [])
    ];

    /// <summary>Our own non-native sections (positioned by their controllers, not the reorder pass).</summary>
    public static bool IsCustom(LayoutSection id)
        => id is LayoutSection.GearSets or LayoutSection.Glamour or LayoutSection.Condition
            or LayoutSection.EnhancedDescription or LayoutSection.Ownership or LayoutSection.EnhancedEffects;

    /// <summary>
    ///     The fixed body order of the <b>Enhanced</b> tooltip (used when
    ///     <see cref="Configuration.Configuration.EnhancedMode" /> is on). The unified item header is the
    ///     anchor at the top (placed below the kept binding line, like the live header), so it isn't listed
    ///     here; below it come the unified bonuses &amp; materia (laid out at the <see cref="LayoutSection.AttributeBonuses" />
    ///     slot), then the glamour, gear-set, and condition custom sections — matching the catalog the editor's
    ///     Enhanced tab advertises.
    /// </summary>
    public static readonly LayoutSection[] EnhancedBodyOrder =
    [
        LayoutSection.AttributeBonuses, LayoutSection.EnhancedEffects, LayoutSection.EnhancedDescription,
        LayoutSection.Glamour, LayoutSection.GearSets, LayoutSection.Ownership, LayoutSection.Condition
    ];

    /// <summary>
    ///     Every native content block the Enhanced tooltip hides — it shows only its five custom sections plus
    ///     the header's preserved binding/untradable line (<see cref="BindingLineBlockId" />) and storage icons
    ///     (<c>#24</c>). Damage/Defense (<c>#36</c>), Item Level (<c>#62</c>), Description (<c>#40</c>),
    ///     Bonuses (<c>#97</c>), Materia (<c>#93</c>), Effects (<c>#49</c>), Crafting &amp; Repairs (<c>#68</c>),
    ///     Vendor/Market (<c>#43</c>,<c>#47</c>), Requirements (<c>#53</c>), Crafter Signature (<c>#4</c>), and
    ///     the control-hints row (<c>#2</c>, the keybind reminders — clutter in the curated layout, and the
    ///     bottom space it would otherwise leave). The
    ///     native header name/icon/category sub-nodes are <b>not</b> here — the unified header hides those itself
    ///     (only when it shows), so a non-equippable hover keeps its native name. The native Effects block
    ///     (<c>#49</c>) is hidden here, but our own <see cref="LayoutSection.EnhancedEffects" /> section scrapes
    ///     it (before this hide) and re-renders it above Description with green numbers.
    /// </summary>
    public static readonly uint[] EnhancedHiddenBlockIds =
        [36, 62, 40, 97, 93, 49, 68, 43, 47, 53, 4, ControlHintsBlockId];

    /// <summary>The default top-to-bottom order (best-effort match for the game's natural order).</summary>
    public static readonly LayoutSection[] DefaultOrder = Sections.Select(s => s.Id).ToArray();

    public static LayoutSectionInfo? Find(LayoutSection id)
        => Sections.FirstOrDefault(s => s.Id == id);
}
