using BetterTips.Tooltips;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Dalamud.Plugin.Services;

namespace BetterTips.UI;

/// <summary>
///     The native control window. The master <c>Enable</c> switch and the preview toggle sit above a
///     <see cref="TabBarNode" /> with two tabs: <b>Structure</b> (the show/hide catalog + the detail hides;
///     reorder happens by dragging the preview) and <b>Enhancements</b> (curated "more streamlined / cleaner"
///     toggles). Each tab's controls live in their own container node, swapped by the tab bar. Opening this
///     window opens the <see cref="TooltipPreviewWindow" /> alongside it; toggling any control writes the
///     shared <see cref="Configuration.Configuration" />, fires <c>onChanged</c> (save + relayout rebuild),
///     and refreshes the preview so the mock tooltip updates live.
/// </summary>
public sealed unsafe class TooltipControlWindow : NativeAddon
{
    private const float RowHeight = 26f;
    private const float CheckHeight = 24f;
    private const float TabBarHeight = 28f;

    private readonly Configuration.Configuration _config;
    private readonly Action _onChanged;
    private readonly TooltipPreviewWindow _preview;
    private readonly IPluginLog _log;

    private CheckboxNode? _showPreview;
    private ResNode? _structureTab;
    private ResNode? _enhancementsTab;
    private TabBarNode? _tabBar;

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
            BuildTabBar(x, ref y, width);

            // Both tabs' controls live in their own container, laid out from the same top; the tab bar
            // toggles which is visible. Containers sit at the window origin so child positions stay absolute.
            var contentTop = y;
            _structureTab = AddTabContainer(width, visible: true);
            _enhancementsTab = AddTabContainer(width, visible: false);

            BuildStructureTab(_structureTab, x, contentTop, width);
            BuildEnhancementsTab(_enhancementsTab, x, contentTop, width);

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
        _structureTab = null;
        _enhancementsTab = null;
        _tabBar = null;
    }

    /// <summary>App-level toggles, above the tabs and always visible.</summary>
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
        y += RowHeight + 6f;
    }

    private void BuildTabBar(float x, ref float y, float width)
    {
        _tabBar = new TabBarNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, TabBarHeight)
        };
        // TabBarNode auto-selects the first tab but doesn't fire its callback, so the containers set their
        // own initial visibility (Structure visible, Enhancements hidden) at creation.
        _tabBar.AddTab("Structure", () => ShowTab(structure: true));
        _tabBar.AddTab("Enhancements", () => ShowTab(structure: false));
        _tabBar.AttachNode(this);
        y += TabBarHeight + 10f;
    }

    private ResNode AddTabContainer(float width, bool visible)
    {
        // A plain Res node doesn't clip, so children keep their absolute positions; we only toggle its
        // visibility to switch tabs.
        var container = new ResNode
        {
            Position = Vector2.Zero,
            Size = new Vector2(width, ContentSize.Y),
            IsVisible = visible
        };
        container.AttachNode(this);
        return container;
    }

    private void ShowTab(bool structure)
    {
        if (_structureTab is not null) _structureTab.IsVisible = structure;
        if (_enhancementsTab is not null) _enhancementsTab.IsVisible = !structure;
    }

    /// <summary>The show/hide catalog (movable blocks) + the finer detail hides.</summary>
    private void BuildStructureTab(ResNode parent, float x, float yStart, float width)
    {
        var y = yStart;
        var size = new Vector2(width, CheckHeight);

        AddHint(parent, "* Checked sections are kept in the tooltip; uncheck one to remove it.", x, ref y, width);
        AddLabel(parent, "Sections", x, ref y, width);
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
            check.AttachNode(parent);
            y += RowHeight;
        }

        y += 8f;
        AddLabel(parent, "Details", x, ref y, width);
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
            check.AttachNode(parent);
            y += RowHeight;
        }
    }

    /// <summary>The curated enhancement toggles (empty until the first one ships — shows a placeholder).</summary>
    private void BuildEnhancementsTab(ResNode parent, float x, float yStart, float width)
    {
        var y = yStart;
        var size = new Vector2(width, CheckHeight);

        if (EnhancementCatalog.All.Length == 0)
        {
            AddLabel(parent, "No enhancements available yet.", x, ref y, width);
            return;
        }

        foreach (var info in EnhancementCatalog.All)
        {
            var enhancement = info.Id;
            var check = new CheckboxNode
            {
                String = info.Label,
                IsChecked = EnhancementCatalog.IsEnabled(_config, enhancement),
                Position = new Vector2(x, y),
                Size = size
            };
            check.DisableAutoResize = true;
            check.OnClick = isChecked =>
            {
                EnhancementCatalog.SetEnabled(_config, enhancement, isChecked);
                check.IsChecked = isChecked;
                _onChanged();
                _preview.Refresh();
            };
            check.AttachNode(parent);
            y += RowHeight;
        }
    }

    private void AddLabel(ResNode parent, string text, float x, ref float y, float width)
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
        label.AttachNode(parent);
        y += 22f;
    }

    /// <summary>A smaller, dimmer instructional line (e.g. "checked = shown").</summary>
    private void AddHint(ResNode parent, string text, float x, ref float y, float width)
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
        hint.AttachNode(parent);
        y += 20f;
    }

    private static TooltipSectionInfo? FindDetail(TooltipSection section)
        => TooltipFieldMap.Sections.FirstOrDefault(s => s.Section == section);
}
