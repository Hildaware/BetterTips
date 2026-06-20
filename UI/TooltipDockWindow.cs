using BetterTips.Tooltips;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace BetterTips.UI;

/// <summary>
///     The "Move tooltip" dock window: a small, <b>draggable</b> native window the user positions to set where
///     item tooltips dock. It holds a plain-language explanation plus the grow-from-corner dropdown. When the
///     user turns "Move tooltip" off in the control window, that window reads this one's chosen-corner screen
///     position (<see cref="CaptureOrigin" />) and stores it as the fixed dock origin
///     (<see cref="Configuration.Configuration.DockOriginX" />/<c>DockOriginY</c>) — which the relayout then
///     uses to place the tooltip by a pure formula (no live-position read → no drift).
///     <para>
///         Like the preview, this is a <b>companion</b> of the control window: opened once and finalized only
///         when the control window closes (the safe dispose path); shown/hidden via <see cref="SetVisible" />,
///         and its close button is removed so it can't be independently finalized mid-session (that crashes).
///     </para>
/// </summary>
public sealed unsafe class TooltipDockWindow : NativeAddon
{
    private const float RowHeight = 26f;
    private const float HintLineHeight = 16f;

    private readonly Configuration.Configuration _config;
    private readonly Action _onChanged;
    private readonly IPluginLog _log;

    private EnumDropDownNode<TooltipAnchor>? _anchorDropdown;

    public TooltipDockWindow(Configuration.Configuration config, Action onChanged, IPluginLog log)
    {
        _config = config;
        _onChanged = onChanged;
        _log = log;
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        try
        {
            // Companion window — remove the close button so it can't be independently closed/finalized
            // mid-session (KTK Close() deallocates, which crashes after interaction). The control window owns
            // its lifetime; the title bar still drags.
            if (WindowNode is KamiToolKit.Nodes.WindowNode windowNode)
                windowNode.ShowCloseButton = false;

            var x = ContentStartPosition.X;
            var width = ContentSize.X;
            var y = ContentStartPosition.Y;

            AddWrappedHint("Drag this window by its title bar to where you'd like item tooltips to appear.",
                x, ref y, width);
            AddWrappedHint("Pick which corner stays pinned as a tooltip grows or shrinks:", x, ref y, width);
            y += 2f;

            _anchorDropdown = new EnumDropDownNode<TooltipAnchor>
            {
                Position = new Vector2(x, y),
                Size = new Vector2(width, 24f),
                MaxListOptions = 4,
                Options = Enum.GetValues<TooltipAnchor>().ToList(),
                SelectedOption = _config.Anchor
            };
            _anchorDropdown.OnOptionSelected = anchor =>
            {
                _config.Anchor = anchor;
                _onChanged();
            };
            _anchorDropdown.AttachNode(this);
            y += RowHeight + 8f;

            AddWrappedHint("When it's where you want it, switch \"Move tooltip\" off in the settings window to " +
                           "lock it in. That spot becomes the tooltip's home.", x, ref y, width);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: failed to build the move-tooltip window.");
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _anchorDropdown = null;
    }

    /// <summary>Show or hide the window without closing/finalizing it (toggled by the control window's "Move
    /// tooltip" switch). See the companion-lifetime note on the class.</summary>
    public void SetVisible(bool visible)
    {
        if (InternalAddon is not null)
            InternalAddon->IsVisible = visible;
    }

    /// <summary>
    ///     Capture this window's <see cref="Configuration.Configuration.Anchor" />-corner screen position and
    ///     store it as the fixed dock origin. Called when the user locks the dock (turns "Move tooltip" off).
    ///     The relayout then docks the tooltip's matching corner at this exact point.
    /// </summary>
    public void CaptureOrigin()
    {
        if (InternalAddon is null) return;
        var root = InternalAddon->RootNode;
        if (root is null) return;

        var scale = InternalAddon->Scale;
        var winX = (float)InternalAddon->X;
        var winY = (float)InternalAddon->Y;
        var w = root->Width * scale;
        var h = root->Height * scale;
        var left = _config.Anchor is TooltipAnchor.TopLeft or TooltipAnchor.BottomLeft;
        var top = _config.Anchor is TooltipAnchor.TopLeft or TooltipAnchor.TopRight;

        _config.DockOriginX = left ? winX : winX + w;
        _config.DockOriginY = top ? winY : winY + h;
        _config.DockSet = true;
        _onChanged();
    }

    /// <summary>A small, dimmer instructional line, word-wrapped to the content width. Advances
    /// <paramref name="y" /> by the wrapped height.</summary>
    private void AddWrappedHint(string text, float x, ref float y, float width)
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
        hint.AddTextFlags(TextFlags.MultiLine, TextFlags.WordWrap);
        hint.AttachNode(this);

        var lines = WrapText(hint, text, width);
        hint.String = string.Join('\n', lines);
        hint.Size = new Vector2(width, lines.Count * HintLineHeight);
        y += lines.Count * HintLineHeight + 4f;
    }

    /// <summary>Greedy word-wrap of <paramref name="text" /> to <paramref name="maxWidth" />, measuring with the
    /// node's font (char-width fallback while the text engine isn't ready in OnSetup).</summary>
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

    private static float MeasureWidth(TextNode probe, string text)
    {
        var w = probe.GetTextDrawSize(text, considerScale: false).X;
        return w > 0f ? w : text.Length * probe.FontSize * 0.5f;
    }
}
