using BetterTips.Tooltips;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace BetterTips.Configuration;

/// <summary>
///     BetterTips configuration. A master switch plus the sections the user has chosen to hide. Hiding is
///     split across two collections by granularity: whole movable blocks live in
///     <see cref="HiddenLayoutSections" /> (toggled from the visual editor's catalog), and the finer
///     sub-line/detail hides live in <see cref="HiddenSections" />. The defaults below match the
///     out-of-the-box "hide the noisiest lines" behavior.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Master switch. When false, tooltips are left exactly as the game renders them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Which of the two products is active. When <c>true</c> the <b>Enhanced</b> tooltip is shown: an
    ///     all-or-nothing curated layout of BetterTips' custom sections in a fixed order (unified header,
    ///     unified bonuses &amp; materia, glamour, gear sets, condition) — every other native section is
    ///     hidden (only the header's binding/untradable line and storage icons are kept). While it's on the
    ///     <b>structure</b> config below (<see cref="HiddenSections" /> / <see cref="HiddenLayoutSections" /> /
    ///     <see cref="SectionOrder" />) is disregarded and the editor's Structure tab is locked. When
    ///     <c>false</c> the <b>modifier</b> tooltip is shown: the structure config governs (add / remove /
    ///     reorder native sections, plus the gear-set / glamour / condition additions). Defaults to
    ///     <c>true</c> (Enhanced is the out-of-the-box product); a missing field in an old config keeps this
    ///     initializer, so no migration is needed.
    /// </summary>
    public bool EnhancedMode { get; set; } = true;

    /// <summary>
    ///     Which corner of the tooltip stays fixed as BetterTips resizes it (<see cref="TooltipAnchor" />).
    ///     Default <see cref="TooltipAnchor.BottomLeft" />: the tooltip keeps its bottom edge where the game
    ///     placed it and grows/shrinks upward (the window is moved down by however much we trimmed). A Top
    ///     corner is the game's natural top-left growth (no window move). Left vs. right is reserved — the
    ///     relayout never changes width today. A missing field in an old config keeps this initializer, so no
    ///     migration is needed.
    /// </summary>
    public TooltipAnchor Anchor { get; set; } = TooltipAnchor.BottomLeft;

    /// <summary>
    ///     Whether the user has set a fixed dock origin via the "Move tooltip" window. When <c>true</c>, the
    ///     tooltip is docked so its <see cref="Anchor" /> corner sits at (<see cref="DockOriginX" />,
    ///     <see cref="DockOriginY" />) on screen — a value we control, never read back from the live tooltip, so
    ///     it can't drift. When <c>false</c> the game's natural (cursor-relative) placement is left untouched.
    /// </summary>
    public bool DockSet { get; set; }

    /// <summary>Screen X of the dock origin (the <see cref="Anchor" /> corner's pinned point). Only meaningful
    /// when <see cref="DockSet" /> is true.</summary>
    public float DockOriginX { get; set; }

    /// <summary>Screen Y of the dock origin. Only meaningful when <see cref="DockSet" /> is true.</summary>
    public float DockOriginY { get; set; }

    /// <summary>
    ///     The finer, non-block detail hides (the category line inside the header, the extract/project
    ///     flags line, the "Advanced Melding Forbidden" notice, the control-hints row). Whole movable
    ///     blocks are hidden via <see cref="HiddenLayoutSections" /> instead. Anything not listed here is
    ///     shown. (The other <see cref="TooltipSection" /> members are dormant — kept for the string-hook
    ///     fallback and serialization safety — and the v3 migration moves their block-level hides into
    ///     <see cref="HiddenLayoutSections" />.)
    /// </summary>
    public HashSet<TooltipSection> HiddenSections { get; set; } =
    [
        TooltipSection.ExtractProjectDesynth,
        TooltipSection.AdvancedMelding
    ];

    /// <summary>
    ///     The movable content blocks the user has removed from the tooltip (see <see cref="LayoutSection" />).
    ///     The relayout engine hides each removed block's node ids; the order walk then skips them. Gear Sets
    ///     is the exception — its visibility stays on <see cref="ShowGearSets" /> — so the visual editor wraps
    ///     both behind one predicate. Anything not listed here is shown. The default mirrors the old
    ///     "hide Crafting &amp; Repairs" behavior (previously <see cref="TooltipSection.DurabilitySpiritbondRepair" />).
    /// </summary>
    public HashSet<LayoutSection> HiddenLayoutSections { get; set; } = [LayoutSection.CraftingRepairs];

    /// <summary>
    ///     User-defined top-to-bottom order of the reorderable content sections (see
    ///     <see cref="TooltipLayout" />). Defaults to the natural order; while it equals the default the
    ///     relayout engine leaves the game's order untouched. (A missing value in an old config keeps this
    ///     initializer, so no migration is needed.)
    /// </summary>
    public List<LayoutSection> SectionOrder { get; set; } = new(TooltipLayout.DefaultOrder);

    /// <summary>
    ///     Append a "Gear Sets" row at the bottom of the tooltip showing one job icon per distinct job
    ///     whose gear set contains the item. Unlike the hide options this <em>adds</em> content; it still
    ///     honors <see cref="Enabled" /> (off when the plugin is fully disabled).
    /// </summary>
    public bool ShowGearSets { get; set; } = true;

    /// <summary>
    ///     Append a "Glamour" section showing the glamoured-appearance name and the applied dye channels
    ///     (with color swatches). Like Gear Sets this <em>adds</em> content and honors <see cref="Enabled" />;
    ///     it only appears when the hovered item actually has a glamour and/or dye. A missing field in an old
    ///     config keeps this initializer, so no migration is needed.
    /// </summary>
    public bool ShowGlamour { get; set; } = true;

    /// <summary>
    ///     Append a "Condition" section showing the item's durability and spiritbond (read per-instance from
    ///     the native gauge's value nodes) plus its sell price, each as an icon with its value on a single
    ///     row. When shown it replaces the native durability/spiritbond gauge bars (<c>#7</c>), which the
    ///     relayout hides. Like Gear Sets/Glamour this <em>adds</em> content and honors <see cref="Enabled" />;
    ///     it only appears when the hovered item has at least one of those values. A missing field in an old
    ///     config keeps this initializer, so no migration is needed.
    /// </summary>
    public bool ShowCondition { get; set; } = true;

    /// <summary>
    ///     Append an "Ownership" section showing where the hovered item is already in the player's possession
    ///     (owned count + location, Glamour Dresser, Armoire, and whether its mount/minion/Triple Triad card is
    ///     unlocked). Live game state read via the shared FfxivCollections library. Like Gear Sets/Glamour this
    ///     <em>adds</em> content and honors <see cref="Enabled" />; it only appears when the item is owned
    ///     somewhere or grants a tracked collectible. A missing field in an old config keeps this initializer,
    ///     so no migration is needed.
    /// </summary>
    public bool ShowOwnership { get; set; } = true;

    /// <summary>
    ///     The curated <see cref="Enhancement" /> toggles the user has enabled (the editor's "Enhancements"
    ///     tab). Opt-in: anything not listed here is off, so an old config (missing the field) starts empty
    ///     and needs no migration. Serialized by enum value, so <see cref="Enhancement" /> members are
    ///     append-only (see <see cref="UI.EnhancementCatalog" />).
    /// </summary>
    public HashSet<Enhancement> EnabledEnhancements { get; set; } = [];

    public int Version { get; set; } = 3;

    public void Save(IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
    }
}
