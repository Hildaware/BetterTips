using BetterTips.Tooltips;

namespace BetterTips.UI;

/// <summary>
///     Shared add/remove predicate for the editor UIs. A movable block is "shown" unless removed; Gear Sets
///     is the exception — its visibility rides on <see cref="Configuration.Configuration.ShowGearSets" /> —
///     so this wraps both behind one pair of helpers used by the native and ImGui editors alike.
/// </summary>
internal static class SectionVisibility
{
    /// <summary>The non-block detail hides surfaced in the catalog (the rest of TooltipSection is dormant).</summary>
    public static readonly TooltipSection[] DetailSections =
    [
        TooltipSection.ItemCategory,
        TooltipSection.ExtractProjectDesynth,
        TooltipSection.AdvancedMelding,
        TooltipSection.ControlHints
    ];

    public static bool IsShown(Configuration.Configuration config, LayoutSection section)
        => section switch
        {
            LayoutSection.GearSets => config.ShowGearSets,
            LayoutSection.Glamour => config.ShowGlamour,
            LayoutSection.Condition => config.ShowCondition,
            LayoutSection.Ownership => config.ShowOwnership,
            _ => !config.HiddenLayoutSections.Contains(section)
        };

    public static void SetShown(Configuration.Configuration config, LayoutSection section, bool show)
    {
        switch (section)
        {
            case LayoutSection.GearSets:
                config.ShowGearSets = show;
                return;
            case LayoutSection.Glamour:
                config.ShowGlamour = show;
                return;
            case LayoutSection.Condition:
                config.ShowCondition = show;
                return;
            case LayoutSection.Ownership:
                config.ShowOwnership = show;
                return;
        }

        if (show) config.HiddenLayoutSections.Remove(section);
        else config.HiddenLayoutSections.Add(section);
    }
}
