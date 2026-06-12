using BetterTips.Tooltips;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Dalamud.Plugin.Services;

namespace BetterTips.UI;

/// <summary>
///     The native control window: the master <c>Enable</c> switch and the add/remove <b>catalog</b> (a
///     checkbox per movable block, then the detail hides), separate from the <see cref="TooltipPreviewWindow" />
///     it drives. Opening this window opens the preview alongside it; toggling a catalog checkbox writes the
///     shared <see cref="Configuration.Configuration" />, fires <c>onChanged</c> (save + relayout rebuild),
///     and refreshes the preview so the mock tooltip updates live.
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
            BuildCatalog(ContentStartPosition.X, ContentStartPosition.Y, ContentSize.X);

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
    }

    private void BuildCatalog(float x, float yStart, float width)
    {
        var y = yStart;
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
        y += RowHeight + 6f;

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
                SectionVisibility.SetShown(_config, section, isChecked);
                check.IsChecked = isChecked;
                _onChanged();
                _preview.Refresh();
            };
            check.AttachNode(this);
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
                if (isChecked) _config.HiddenSections.Remove(detail);
                else _config.HiddenSections.Add(detail);
                check.IsChecked = isChecked;
                _onChanged();
                _preview.Refresh();
            };
            check.AttachNode(this);
            y += RowHeight;
        }
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

    private static TooltipSectionInfo? FindDetail(TooltipSection section)
        => TooltipFieldMap.Sections.FirstOrDefault(s => s.Section == section);
}
