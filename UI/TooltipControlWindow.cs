using BetterTips.Tooltips;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Dalamud.Plugin.Services;

namespace BetterTips.UI;

/// <summary>
///     The native control window — a single flat layout (no tabs). The app-level switches sit at the top
///     (Enable, "Show tooltip preview", and the <b>"Enhanced tooltip"</b> switch = <see cref="Configuration.Configuration.EnhancedMode" />),
///     then a <b>Structure</b> sub-group: the modifier's show/hide catalog (a <see cref="CheckboxNode" /> per
///     movable block) plus the finer detail hides. While the Enhanced switch is on its curated layout
///     disregards the structure config, so the Structure sub-group is <b>locked</b> (its checkboxes dimmed +
///     click-suppressed). Opening this window opens the <see cref="TooltipPreviewWindow" /> alongside it;
///     toggling any control writes the shared <see cref="Configuration.Configuration" />, fires
///     <c>onChanged</c> (save + relayout rebuild), and refreshes the preview so the mock tooltip updates live.
/// </summary>
public sealed unsafe class TooltipControlWindow : NativeAddon
{
    private const float RowHeight = 26f;
    private const float CheckHeight = 24f;

    private readonly Configuration.Configuration _config;
    private readonly Action _onChanged;
    private readonly TooltipPreviewWindow _preview;
    private readonly TooltipDockWindow _dock;
    private readonly IPluginLog _log;

    private CheckboxNode? _showPreview;
    private CheckboxNode? _enhancedCheck;

    // The "Move tooltip" switch: while on, the draggable dock window is shown so the user can position the
    // tooltip's home; turning it off captures that position as the fixed dock origin.
    private CheckboxNode? _moveTooltip;

    // Every Structure-group checkbox, kept so we can lock them (dimmed + click-suppressed) while the Enhanced
    // tooltip is on — its curated layout disregards the structure config, so editing it would be misleading.
    private readonly List<CheckboxNode> _structureChecks = [];
    private bool _suppressToggle;

    // The Structure sub-group is collapsible (default collapsed — it's vertically tall). We track every body
    // node to show/hide it, plus the window height for each state (the window is resized on toggle so the
    // collapsed window is short rather than mostly-empty).
    private CheckboxNode? _structureToggle;
    private readonly List<NodeBase> _structureBody = [];

    // Every content node (toggles, labels, hints, dropdown, section/detail checks). SetWindowSize →
    // AtkUnitBase.SetSize rescales each child's Y by the window-height ratio (the game's anchor recalc), which
    // would compress our absolutely-placed layout. We snapshot these nodes' intended Y and restore it around
    // every SetWindowSize so the collapse/expand resize can't move the content.
    private readonly List<NodeBase> _contentNodes = [];
    private bool _structureExpanded;
    private float _collapsedHeight, _expandedHeight;
    private const float HintLineHeight = 16f;
    private static readonly Vector4 LockedLabelColor = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 NormalLabelColor = ColorHelper.GetColor(8);

    public TooltipControlWindow(Configuration.Configuration config, Action onChanged,
        TooltipPreviewWindow preview, TooltipDockWindow dock, IPluginLog log)
    {
        _config = config;
        _onChanged = onChanged;
        _preview = preview;
        _dock = dock;
        _log = log;
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        try
        {
            var x = ContentStartPosition.X;
            var width = ContentSize.X;
            var y = ContentStartPosition.Y;

            BuildGlobalToggles(x, ref y, width);
            BuildStructureGroup(x, ref y, width);

            // The preview and the (hidden-by-default) dock window open alongside the controls and close with
            // them (OnFinalize). Companions — never independently finalized mid-session.
            _preview.Open();
            _dock.Open();
            _dock.SetVisible(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: failed to build the control window.");
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        // If the user left "Move tooltip" on, lock in the dock position before closing so the choice isn't lost.
        if (_moveTooltip is { IsChecked: true }) _dock.CaptureOrigin();
        _preview.Close();
        _dock.Close();
        _showPreview = null;
        _enhancedCheck = null;
        _moveTooltip = null;
        _structureToggle = null;
        _structureChecks.Clear();
        _structureBody.Clear();
        _contentNodes.Clear();
    }

    /// <summary>The app-level switches at the top of the window.</summary>
    private void BuildGlobalToggles(float x, ref float y, float width)
    {
        _contentNodes.Clear();
        var size = new Vector2(width, CheckHeight);

        var enableCheck = new CheckboxNode
        {
            String = "Enable BetterTips",
            IsChecked = _config.Enabled,
            Position = new Vector2(x, y),
            Size = size
        };
        enableCheck.DisableAutoResize = true;
        enableCheck.OnClick = isChecked =>
        {
            _config.Enabled = isChecked;
            enableCheck.IsChecked = isChecked;
            _onChanged();
        };
        enableCheck.AttachNode(this);
        _contentNodes.Add(enableCheck);
        y += RowHeight;

        _showPreview = new CheckboxNode
        {
            String = "Show tooltip preview",
            IsChecked = true,
            Position = new Vector2(x, y),
            Size = size
        };
        _showPreview.DisableAutoResize = true;
        // Show/hide the preview WITHOUT closing it — the preview is a companion we open once (OnSetup) and only
        // ever close in OnFinalize. Independently closing/finalizing it mid-session crashes the game.
        _showPreview.OnClick = isChecked => _preview.SetVisible(isChecked);
        _showPreview.AttachNode(this);
        _contentNodes.Add(_showPreview);
        y += RowHeight;

        // The product selector: Enhanced (curated, all-or-nothing) vs the modifier's Structure sub-group.
        // While on, the structure config is disregarded and the sub-group is locked.
        _enhancedCheck = new CheckboxNode
        {
            String = "Enhanced tooltip",
            IsChecked = _config.EnhancedMode,
            Position = new Vector2(x, y),
            Size = size
        };
        _enhancedCheck.DisableAutoResize = true;
        _enhancedCheck.OnClick = isChecked =>
        {
            _config.EnhancedMode = isChecked;
            _enhancedCheck.IsChecked = isChecked;
            _onChanged();
            ApplyStructureLock();
            _preview.Refresh();
        };
        _enhancedCheck.AttachNode(this);
        _contentNodes.Add(_enhancedCheck);
        y += RowHeight + 8f;

        // "Move tooltip": while on, show the draggable dock window (with the corner dropdown + how-to). Turning
        // it off captures that window's position as the tooltip's fixed dock origin.
        _moveTooltip = new CheckboxNode
        {
            String = "Move tooltip (set where it docks)",
            IsChecked = false,
            Position = new Vector2(x, y),
            Size = size
        };
        _moveTooltip.DisableAutoResize = true;
        _moveTooltip.OnClick = isChecked =>
        {
            if (isChecked)
            {
                _dock.SetVisible(true);
            }
            else
            {
                _dock.CaptureOrigin();   // lock: this window's chosen corner becomes the dock origin
                _dock.SetVisible(false);
                _onChanged();            // re-dock the live tooltip immediately
            }
        };
        _moveTooltip.AttachNode(this);
        _contentNodes.Add(_moveTooltip);
        y += RowHeight + 8f;
    }

    /// <summary>The modifier's show/hide catalog (movable blocks) + the finer detail hides, as a sub-group of
    /// the main window. Locked (dimmed + click-suppressed) while the Enhanced tooltip is on.</summary>
    private void BuildStructureGroup(float x, ref float y, float width)
    {
        _structureChecks.Clear();
        _structureBody.Clear();
        var size = new Vector2(width, CheckHeight);

        // Collapsible header: a checkbox toggle (checked = expanded). Collapsed by default since the body is tall.
        _structureToggle = new CheckboxNode
        {
            String = "Structure (add / remove / reorder sections)",
            IsChecked = _structureExpanded,
            Position = new Vector2(x, y),
            Size = size
        };
        _structureToggle.DisableAutoResize = true;
        _structureToggle.OnClick = isChecked =>
        {
            _structureExpanded = isChecked;
            ApplyStructureExpansion();
        };
        _structureToggle.AttachNode(this);
        _contentNodes.Add(_structureToggle);
        y += RowHeight + 4f;

        // Window height when only the toggle shows. Content y already starts below the title bar, so the window
        // height that ends the content at `y` is `y + ContentPadding.Y` (+ a little breathing room).
        _collapsedHeight = y + ContentPadding.Y + 4f;

        // --- body (hidden when collapsed) ---
        _structureBody.Add(AddWrappedHint(
            "Add / remove / reorder the base tooltip (reorder by dragging in the preview).", x, ref y, width));
        _structureBody.Add(AddWrappedHint(
            "Disabled while the Enhanced tooltip is on.", x, ref y, width));
        y += 4f;

        _structureBody.Add(AddLabel("Sections", x, ref y, width));
        foreach (var info in TooltipLayout.Sections)
        {
            var section = info.Id;
            var check = new CheckboxNode
            {
                String = info.Label,
                IsChecked = SectionVisibility.IsShown(_config, section),
                Position = new Vector2(x, y),
                Size = size
            };
            check.DisableAutoResize = true;
            check.OnClick = isChecked =>
            {
                if (_suppressToggle) return;
                // The Enhanced tooltip disregards the structure config, so it can't be edited while on —
                // snap the checkbox back to the stored value.
                if (_config.EnhancedMode)
                {
                    SetCheckedSilent(check, SectionVisibility.IsShown(_config, section));
                    return;
                }

                SectionVisibility.SetShown(_config, section, isChecked);
                check.IsChecked = isChecked;
                _onChanged();
                _preview.Refresh();
            };
            check.AttachNode(this);
            _structureChecks.Add(check);
            _structureBody.Add(check);
            _contentNodes.Add(check);
            y += RowHeight;
        }

        y += 8f;
        _structureBody.Add(AddLabel("Details", x, ref y, width));
        foreach (var ts in SectionVisibility.DetailSections)
        {
            var info = FindDetail(ts);
            if (info is null) continue;

            var detail = ts;
            var check = new CheckboxNode
            {
                String = info.Label,
                IsChecked = !_config.HiddenSections.Contains(detail),
                Position = new Vector2(x, y),
                Size = size
            };
            check.DisableAutoResize = true;
            check.OnClick = isChecked =>
            {
                if (_suppressToggle) return;
                if (_config.EnhancedMode)
                {
                    SetCheckedSilent(check, !_config.HiddenSections.Contains(detail));
                    return;
                }

                if (isChecked) _config.HiddenSections.Remove(detail);
                else _config.HiddenSections.Add(detail);
                check.IsChecked = isChecked;
                _onChanged();
                _preview.Refresh();
            };
            check.AttachNode(this);
            _structureChecks.Add(check);
            _structureBody.Add(check);
            _contentNodes.Add(check);
            y += RowHeight;
        }

        _expandedHeight = y + ContentPadding.Y + 4f;

        // Reflect the lock + the (default collapsed) expansion state from the moment the window opens.
        ApplyStructureLock();
        ApplyStructureExpansion();
    }

    /// <summary>Show/hide the Structure body and resize the window to match the (collapsed/expanded) state.</summary>
    private void ApplyStructureExpansion()
    {
        foreach (var node in _structureBody)
            node.IsVisible = _structureExpanded;
        if (_structureToggle is not null)
            _structureToggle.IsChecked = _structureExpanded;

        // SetWindowSize → AtkUnitBase.SetSize rescales every child's Y by newHeight/oldHeight (the game's anchor
        // recalc), which would compress our absolutely-placed content. Snapshot each content node's intended Y,
        // resize, then restore it so the window-height change leaves the layout untouched. (X is unaffected —
        // the width never changes.)
        var intendedY = new float[_contentNodes.Count];
        for (var i = 0; i < _contentNodes.Count; i++)
            intendedY[i] = _contentNodes[i].Position.Y;

        SetWindowSize(Size.X, _structureExpanded ? _expandedHeight : _collapsedHeight);

        for (var i = 0; i < _contentNodes.Count; i++)
            _contentNodes[i].Position = new Vector2(_contentNodes[i].Position.X, intendedY[i]);
    }

    /// <summary>Dim + click-suppress (via the per-handler <see cref="Configuration.Configuration.EnhancedMode" />
    /// guard) the Structure-group checkboxes when the Enhanced tooltip is on; restore them when it's off.</summary>
    private void ApplyStructureLock()
    {
        var color = _config.EnhancedMode ? LockedLabelColor : NormalLabelColor;
        foreach (var check in _structureChecks)
            check.Label.TextColor = color;
    }

    /// <summary>Set a checkbox's state without firing its <c>OnClick</c> (avoids the snap-back recursing).</summary>
    private void SetCheckedSilent(CheckboxNode check, bool value)
    {
        _suppressToggle = true;
        check.IsChecked = value;
        _suppressToggle = false;
    }

    private TextNode AddLabel(string text, float x, ref float y, float width)
    {
        var label = new TextNode
        {
            String = text,
            FontType = FontType.Axis,
            FontSize = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(3),
            TextOutlineColor = ColorHelper.GetColor(7),
            Position = new Vector2(x, y),
            Size = new Vector2(width, 20f)
        };
        label.AttachNode(this);
        _contentNodes.Add(label);
        y += 22f;
        return label;
    }

    /// <summary>A smaller, dimmer instructional line, <b>word-wrapped</b> to the content width so long copy
    /// stays inside the window instead of overflowing. Returns the node; advances <paramref name="y" /> by the
    /// wrapped height.</summary>
    private TextNode AddWrappedHint(string text, float x, ref float y, float width)
    {
        var hint = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = (uint)HintLineHeight,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7),
            Position = new Vector2(x, y)
        };
        // MultiLine renders our explicit '\n' breaks; WordWrap is a safety net if a single token still overflows.
        hint.AddTextFlags(TextFlags.MultiLine, TextFlags.WordWrap);
        hint.AttachNode(this);

        var lines = WrapText(hint, text, width);
        hint.String = string.Join('\n', lines);
        hint.Size = new Vector2(width, lines.Count * HintLineHeight);
        _contentNodes.Add(hint);
        y += lines.Count * HintLineHeight + 2f;
        return hint;
    }

    /// <summary>Greedy word-wrap of <paramref name="text" /> to <paramref name="maxWidth" />, measuring each
    /// candidate line with <paramref name="probe" />'s font (via <c>GetTextDrawSize</c>). A single over-long
    /// word is kept on its own line (the node's WordWrap flag then breaks it at render time).</summary>
    private static List<string> WrapText(TextNode probe, string text, float maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length != 0 && MeasureWidth(probe, candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length != 0) lines.Add(current);
        if (lines.Count == 0) lines.Add(string.Empty);
        return lines;
    }

    /// <summary>Drawn width of <paramref name="text" /> at the probe's font, with a char-width fallback for when
    /// the text engine isn't ready yet (<c>GetTextDrawSize</c> can return 0 during <c>OnSetup</c>) — without it a
    /// 0 measurement would collapse every wrap onto one line and under-reserve the node's height.</summary>
    private static float MeasureWidth(TextNode probe, string text)
    {
        var w = probe.GetTextDrawSize(text, considerScale: false).X;
        return w > 0f ? w : text.Length * probe.FontSize * 0.5f; // ~0.5em per char is a safe over-estimate
    }

    private static TooltipSectionInfo? FindDetail(TooltipSection section)
        => TooltipFieldMap.Sections.FirstOrDefault(s => s.Section == section);
}
