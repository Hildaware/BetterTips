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

    public int Version { get; set; } = 3;

    public void Save(IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
    }
}
