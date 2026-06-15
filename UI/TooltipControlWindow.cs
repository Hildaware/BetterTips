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
    private readonly IPluginLog _log;

    private CheckboxNode? _showPreview;
    private CheckboxNode? _enhancedCheck;

    // Every Structure-group checkbox, kept so we can lock them (dimmed + click-suppressed) while the Enhanced
    // tooltip is on — its curated layout disregards the structure config, so editing it would be misleading.
    private readonly List<CheckboxNode> _structureChecks = [];
    private bool _suppressToggle;
    private static readonly Vector4 LockedLabelColor = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 NormalLabelColor = ColorHelper.GetColor(8);

    public TooltipControlWindow(Configuration.Configuration config, Action onChanged,
        TooltipPreviewWindow preview, IPluginLog log)
    {
        _config = config;
        _onChanged = onChanged;
        _preview = preview;
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

            // The preview opens alongside the controls (and closes with them, in OnFinalize).
            _preview.Open();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: failed to build the control window.");
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _preview.Close();
        _showPreview = null;
        _enhancedCheck = null;
        _structureChecks.Clear();
    }

    /// <summary>The app-level switches at the top of the window.</summary>
    private void BuildGlobalToggles(float x, ref float y, float width)
    {
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
        y += RowHeight;

        _showPreview = new CheckboxNode
        {
            String = "Show tooltip preview",
            IsChecked = true,
            Position = new Vector2(x, y),
            Size = size
        };
        _showPreview.DisableAutoResize = true;
        _showPreview.OnClick = isChecked =>
        {
            if (isChecked) _preview.Open();
            else _preview.Close();
        };
        _showPreview.AttachNode(this);
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
        y += RowHeight + 8f;
    }

    /// <summary>The modifier's show/hide catalog (movable blocks) + the finer detail hides, as a sub-group of
    /// the main window. Locked (dimmed + click-suppressed) while the Enhanced tooltip is on.</summary>
    private void BuildStructureGroup(float x, ref float y, float width)
    {
        _structureChecks.Clear();
        var size = new Vector2(width, CheckHeight);

        AddLabel("Structure", x, ref y, width);
        AddHint("Add / remove / reorder the base tooltip (reorder by dragging in the preview).", x, ref y, width);
        AddHint("Disabled while the Enhanced tooltip is on.", x, ref y, width);
        y += 4f;

        AddLabel("Sections", x, ref y, width);
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
            y += RowHeight;
        }

        y += 8f;
        AddLabel("Details", x, ref y, width);
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
            y += RowHeight;
        }

        // Reflect the lock from the moment the window opens.
        ApplyStructureLock();
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

    private void AddLabel(string text, float x, ref float y, float width)
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
        y += 22f;
    }

    /// <summary>A smaller, dimmer instructional line.</summary>
    private void AddHint(string text, float x, ref float y, float width)
    {
        var hint = new TextNode
        {
            String = text,
            FontType = FontType.Axis,
            FontSize = 12,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7),
            Position = new Vector2(x, y),
            Size = new Vector2(width, 18f)
        };
        hint.AttachNode(this);
        y += 20f;
    }

    private static TooltipSectionInfo? FindDetail(TooltipSection section)
        => TooltipFieldMap.Sections.FirstOrDefault(s => s.Section == section);
}
