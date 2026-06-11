using BetterTips.Tooltips;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BetterTips.UI;

/// <summary>
///     BetterTips' only window: a master switch and a "show this section" checkbox per
///     <see cref="TooltipSection" />. On any change we invoke <c>onChanged</c>, which the plugin wires to
///     a guarded save-and-refresh so the next hover reflects the new state. This window only does ImGui
///     work — no native memory — so it cannot crash the game; Dalamud also isolates draw exceptions.
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration.Configuration _config;
    private readonly Action _onChanged;

    public ConfigWindow(Configuration.Configuration config, Action onChanged)
        : base("BetterTips Settings###BetterTipsConfig")
    {
        _config = config;
        _onChanged = onChanged;

        Size = new Vector2(460f, 360f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var changed = false;

        var enabled = _config.Enabled;
        if (ImGui.Checkbox("Enable BetterTips", ref enabled))
        {
            _config.Enabled = enabled;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Tooltip sections to show (unchecked = hidden):");
        ImGui.Spacing();

        using (ImRaii.Disabled(!_config.Enabled))
        {
            foreach (var section in TooltipFieldMap.Sections)
            {
                var show = !_config.HiddenSections.Contains(section.Section);
                if (ImGui.Checkbox($"{section.Label}##sect_{section.Section}", ref show))
                {
                    if (show)
                        _config.HiddenSections.Remove(section.Section);
                    else
                        _config.HiddenSections.Add(section.Section);
                    changed = true;
                }

                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(section.Description))
                    ImGui.SetTooltip(section.Description);
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled("Extra info to add:");
        ImGui.Spacing();

        using (ImRaii.Disabled(!_config.Enabled))
        {
            var showGearSets = _config.ShowGearSets;
            if (ImGui.Checkbox("Gear Sets##add_gearsets", ref showGearSets))
            {
                _config.ShowGearSets = showGearSets;
                changed = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Append a row of job icons for each of your gear sets that contains this item.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Section order (preview — a drag editor is coming; use the arrows for now):");
        ImGui.Spacing();

        using (ImRaii.Disabled(!_config.Enabled))
        {
            var order = _config.SectionOrder;

            // Every section is reorderable, including the added "Gear Sets" block. Defer the swap so we
            // never mutate the list mid-render.
            var swapA = -1;
            var swapB = -1;
            for (var i = 0; i < order.Count; i++)
            {
                ImGui.PushID(i);

                if (ImGui.ArrowButton("##up", ImGuiDir.Up) && i > 0)
                {
                    swapA = i;
                    swapB = i - 1;
                }

                ImGui.SameLine();
                if (ImGui.ArrowButton("##down", ImGuiDir.Down) && i < order.Count - 1)
                {
                    swapA = i;
                    swapB = i + 1;
                }

                ImGui.SameLine();
                ImGui.Text(TooltipLayout.Find(order[i])?.Label ?? order[i].ToString());

                ImGui.PopID();
            }

            if (swapA >= 0)
            {
                (order[swapA], order[swapB]) = (order[swapB], order[swapA]);
                changed = true;
            }
        }

        if (changed)
            _onChanged();
    }
}
