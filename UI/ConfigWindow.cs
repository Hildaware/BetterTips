using BetterTips.Tooltips;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BetterTips.UI;

/// <summary>
///     BetterTips' only window: a visual tooltip editor. The left <b>catalog</b> pane is the add/remove
///     list — which sections appear in the tooltip (the movable blocks plus a few finer detail hides). The
///     right pane is a live <b>mock tooltip</b> whose visible blocks are dragged on the Y axis to reorder
///     them. Every change funnels through <c>onChanged</c> (a guarded save-and-refresh), so the next hover
///     reflects the new state. This window only does ImGui work — no native memory — so it cannot crash the
///     game; Dalamud also isolates draw exceptions.
/// </summary>
public sealed class ConfigWindow : Window
{
    // Mock-tooltip palette (RGBA floats), loosely mirroring the game's dark-blue tooltip.
    private static readonly Vector4 MockBg = new(0.05f, 0.06f, 0.10f, 0.97f);
    private static readonly Vector4 NameColor = new(0.96f, 0.93f, 0.82f, 1f);
    private static readonly Vector4 BlockColor = new(0.16f, 0.18f, 0.24f, 1f);
    private static readonly Vector4 BlockHover = new(0.24f, 0.28f, 0.38f, 1f);
    private static readonly Vector4 BlockActive = new(0.30f, 0.36f, 0.50f, 1f);
    private static readonly Vector4 ControlColor = new(0.10f, 0.11f, 0.15f, 1f);

    private readonly Configuration.Configuration _config;
    private readonly Action _onChanged;

    // A reorder drag has moved a block since the mouse went down. We mutate SectionOrder live (so the mock
    // re-renders instantly) but defer the persist (save + relayout rebuild) until the mouse is released —
    // saving on every drag frame would hammer the disk and rebuild the relayout 60×/sec.
    private bool _orderTouched;

    public ConfigWindow(Configuration.Configuration config, Action onChanged)
        : base("BetterTips Settings###BetterTipsConfig")
    {
        _config = config;
        _onChanged = onChanged;

        Size = new Vector2(580f, 440f);
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

        using (ImRaii.Disabled(!_config.Enabled))
        {
            var avail = ImGui.GetContentRegionAvail();
            var catalogWidth = Math.Max(200f, avail.X * 0.42f);

            // Left: the add/remove catalog.
            if (ImGui.BeginChild("##catalog", new Vector2(catalogWidth, 0f), true))
                changed |= DrawCatalog();
            ImGui.EndChild();

            ImGui.SameLine();

            // Right: the live mock tooltip (drag to reorder). The ChildBg push gives it the tooltip backdrop.
            using (ImRaii.PushColor(ImGuiCol.ChildBg, MockBg))
            {
                if (ImGui.BeginChild("##mock", new Vector2(0f, 0f), true))
                    DrawMockTooltip();
                ImGui.EndChild();
            }
        }

        // Persist a reorder once, when the drag ends — see _orderTouched.
        if (_orderTouched && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _orderTouched = false;
            changed = true;
        }

        if (changed)
            _onChanged();
    }

    /// <summary>The catalog pane: a "shown" checkbox per movable block, then per detail hide.</summary>
    private bool DrawCatalog()
    {
        var changed = false;

        ImGui.TextDisabled("Sections (unchecked = removed):");
        ImGui.Spacing();

        // The movable blocks — these are the draggable cards in the mock. Checked = shown.
        foreach (var info in TooltipLayout.Sections)
        {
            var shown = SectionVisibility.IsShown(_config, info.Id);
            if (ImGui.Checkbox($"{info.Label}##blk_{(int)info.Id}", ref shown))
            {
                SectionVisibility.SetShown(_config, info.Id, shown);
                changed = true;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Details:");
        ImGui.Spacing();

        // The finer, non-block hides (header category line, extract flags, advanced-melding notice, control
        // hints). These are toggles only — they aren't draggable blocks in the mock.
        foreach (var section in SectionVisibility.DetailSections)
        {
            var info = FindDetail(section);
            if (info is null) continue;

            var shown = !_config.HiddenSections.Contains(section);
            if (ImGui.Checkbox($"{info.Label}##dtl_{(int)section}", ref shown))
            {
                if (shown) _config.HiddenSections.Remove(section);
                else _config.HiddenSections.Add(section);
                changed = true;
            }

            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(info.Description))
                ImGui.SetTooltip(info.Description);
        }

        // Curated enhancement toggles (shares EnhancementCatalog with the native editor so the two can't
        // drift). Empty until the first one ships, so the whole section is skipped while there are none.
        if (EnhancementCatalog.All.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("Enhancements:");
            ImGui.Spacing();

            foreach (var info in EnhancementCatalog.All)
            {
                var enabled = EnhancementCatalog.IsEnabled(_config, info.Id);
                if (ImGui.Checkbox($"{info.Label}##enh_{(int)info.Id}", ref enabled))
                {
                    EnhancementCatalog.SetEnabled(_config, info.Id, enabled);
                    changed = true;
                }

                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(info.Description))
                    ImGui.SetTooltip(info.Description);
            }
        }

        return changed;
    }

    /// <summary>
    ///     The mock pane: a faux header, the visible blocks in the user's order (each draggable on the Y
    ///     axis to reorder), and the control-hints row pinned to the bottom. Reorders mutate
    ///     <see cref="Configuration.Configuration.SectionOrder" /> live; the persist is deferred to release
    ///     (handled in <see cref="Draw" /> via <see cref="_orderTouched" />).
    /// </summary>
    private void DrawMockTooltip()
    {
        // Faux header: item name + (optional) category line, then a divider — the relayout's stable anchor.
        ImGui.TextColored(NameColor, "Sample Item");
        if (!_config.HiddenSections.Contains(TooltipSection.ItemCategory))
            ImGui.TextDisabled("Arm Armor");
        ImGui.Separator();
        ImGui.Spacing();

        // The visible blocks, in the user's order (Gear Sets included — it's a real, movable block).
        var visible = new List<LayoutSection>();
        foreach (var id in _config.SectionOrder)
            if (TooltipLayout.Find(id) is not null && SectionVisibility.IsShown(_config, id))
                visible.Add(id);

        var rowHeight = ImGui.GetTextLineHeight() + 10f;
        (LayoutSection Dragged, LayoutSection Neighbor, int Dir)? move = null;

        using (ImRaii.PushColor(ImGuiCol.Header, BlockColor))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, BlockHover))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, BlockActive))
        {
            for (var vi = 0; vi < visible.Count; vi++)
            {
                var id = visible[vi];
                var label = TooltipLayout.Find(id)?.Label ?? id.ToString();

                // A stable per-section id (##mock_<enum>) — not per-index — so the active card follows the
                // section as the data shuffles under it while dragging.
                ImGui.Selectable($"  {label}##mock_{(int)id}", true, ImGuiSelectableFlags.None,
                    new Vector2(ImGui.GetContentRegionAvail().X, rowHeight));

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Drag up or down to reorder. Remove it from the catalog on the left.");

                // While this card is held but the cursor has left its rect, move it one slot toward the
                // cursor. Position-based (cursor vs. the card's own rect) so it's robust to mouse jitter.
                if (ImGui.IsItemActive() && !ImGui.IsItemHovered())
                {
                    var mouseY = ImGui.GetIO().MousePos.Y;
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    var dir = mouseY < min.Y ? -1 : mouseY > max.Y ? 1 : 0;
                    if (dir != 0)
                    {
                        var j = vi + dir;
                        if (j >= 0 && j < visible.Count)
                            move = (id, visible[j], dir);
                    }
                }
            }
        }

        if (move is { } m)
        {
            MoveBlock(m.Dragged, m.Neighbor, m.Dir);
            _orderTouched = true;
        }

        // Pin the control-hints row to the bottom (it sits below all content in the real tooltip). Inert: a
        // disabled, non-draggable bar — only the catalog toggles it.
        if (!_config.HiddenSections.Contains(TooltipSection.ControlHints))
        {
            var bottomY = ImGui.GetWindowContentRegionMax().Y - rowHeight;
            if (ImGui.GetCursorPosY() < bottomY)
                ImGui.SetCursorPosY(bottomY);

            using (ImRaii.Disabled(true))
            using (ImRaii.PushColor(ImGuiCol.Header, ControlColor))
                ImGui.Selectable("  Control hints##mock_control", true, ImGuiSelectableFlags.None,
                    new Vector2(ImGui.GetContentRegionAvail().X, rowHeight));
        }
    }

    /// <summary>
    ///     Move <paramref name="dragged" /> next to <paramref name="neighbor" /> in the full
    ///     <see cref="Configuration.Configuration.SectionOrder" /> — above it when dragging up, below when
    ///     dragging down. Reinserting relative to the neighbor (rather than swapping indices) stays correct
    ///     even when removed/hidden blocks sit between two visible ones.
    /// </summary>
    private void MoveBlock(LayoutSection dragged, LayoutSection neighbor, int dir)
    {
        var order = _config.SectionOrder;
        if (!order.Remove(dragged)) return;

        var idx = order.IndexOf(neighbor);
        if (idx < 0)
        {
            order.Add(dragged);
            return;
        }

        order.Insert(dir < 0 ? idx : idx + 1, dragged);
    }

    private static TooltipSectionInfo? FindDetail(TooltipSection section)
        => TooltipFieldMap.Sections.FirstOrDefault(s => s.Section == section);
}
