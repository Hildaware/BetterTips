using BetterTips.Tooltips;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace BetterTips.Configuration;

/// <summary>
///     BetterTips configuration. A master switch plus the set of tooltip sections the user has chosen to
///     hide. Sections not in <see cref="HiddenSections" /> are shown; the two defaults below match the
///     out-of-the-box "hide the noisiest lines" behavior.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Master switch. When false, tooltips are left exactly as the game renders them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sections to strip from item tooltips. Anything not listed here stays visible.</summary>
    public HashSet<TooltipSection> HiddenSections { get; set; } =
    [
        TooltipSection.ExtractProjectDesynth,
        TooltipSection.DurabilitySpiritbondRepair,
        TooltipSection.AdvancedMelding
    ];

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

    public int Version { get; set; } = 2;

    public void Save(IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
    }
}
