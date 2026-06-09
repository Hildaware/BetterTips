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
        TooltipSection.DurabilitySpiritbondRepair
    ];

    public int Version { get; set; } = 1;

    public void Save(IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
    }
}
