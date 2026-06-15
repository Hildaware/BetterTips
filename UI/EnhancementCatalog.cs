using BetterTips.Tooltips;

namespace BetterTips.UI;

/// <summary>Display metadata for one <see cref="Enhancement" /> toggle in the editor's Enhancements tab.</summary>
public sealed record EnhancementInfo(Enhancement Id, string Label, string Description);

/// <summary>
///     The single registry of curated <see cref="Enhancement" /> toggles, shared by the native editor and
///     the ImGui fallback so the two can't drift (mirrors <see cref="SectionVisibility" />). To add an
///     enhancement: append an <see cref="Enhancement" /> member, add an <see cref="EnhancementInfo" /> row
///     to <see cref="All" />, then wire its effect in the relayout/preview path. The UI renders one checkbox
///     per entry; an empty list shows a placeholder.
/// </summary>
internal static class EnhancementCatalog
{
    /// <summary>Every curated enhancement, in display order.</summary>
    public static readonly EnhancementInfo[] All =
    [
        new(Enhancement.UnifiedItemHeader, "Unified item header",
            "Merge the icon, name, item level, and damage/defense into one redesigned section at the top."),
        new(Enhancement.UnifiedBonusesMateria, "Unified bonuses & materia",
            "Merge Bonuses and Materia into one two-column section: attributes (green / pink / gold), then the melded materia in the same form.")
    ];

    public static bool IsEnabled(Configuration.Configuration config, Enhancement enhancement)
        => config.EnabledEnhancements.Contains(enhancement);

    public static void SetEnabled(Configuration.Configuration config, Enhancement enhancement, bool enabled)
    {
        if (enabled) config.EnabledEnhancements.Add(enhancement);
        else config.EnabledEnhancements.Remove(enhancement);
    }
}
