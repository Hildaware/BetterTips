using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own "Description" content block — a simple, <b>headerless</b>, left-aligned render of
///     the item's lore description (from <see cref="DescriptionData" />, i.e. Lumina, so dyes and the
///     "Advanced Melding Forbidden" notice never appear). Like the other added blocks it is a <b>passive
///     provider</b>: it builds/measures/shows the block but never positions or anchors anything —
///     <see cref="TooltipRelayoutController" /> drives it as the Enhanced-only <see cref="LayoutSection.EnhancedDescription" />
///     section, placed just above Glamour.
///     <para>
///         The lore text is word-wrapped to the block width here (greedy, measured with
///         <see cref="TextNode.GetTextDrawSize(bool)" />) into one pooled <see cref="TextNode" /> per line.
///         Node lifetime mirrors the other added blocks: created once under the header, reused for the addon's
///         life, disposed on <see cref="OnPreFinalize" /> and plugin <see cref="Dispose" />.
///     </para>
/// </summary>
public sealed unsafe class DescriptionBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    private const uint BodyFontSize = 12;
    private const float LineHeight = 18f;
    private const float SidePad = 16f;        // symmetric wrap margin (the text is centered between them)
    private const float BlockBottomPad = 6f;  // breathing room below the block

    private static readonly Vector4 TextColor = new(0xE0 / 255f, 0xE0 / 255f, 0xE0 / 255f, 1f); // light grey
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private TextNode? _measure;            // hidden node used only to measure candidate line widths
    private readonly List<TextNode> _lines = [];

    private AtkUnitBase* _attachedAddon;

    public DescriptionBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the feature is on. The custom description is part of the all-or-nothing Enhanced
    /// tooltip only — in modifier mode the native description block (<c>#40</c>) is used instead.</summary>
    public bool Enabled => _config.Enabled && _config.EnhancedMode;

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the item has no lore
    ///     description. The caller positions it via <see cref="PlaceAt" /> + <see cref="Show" />.
    /// </summary>
    public bool TryMeasure(AddonItemDetail* addon, float width, out float height)
    {
        height = 0f;
        try
        {
            if (addon is null || !Enabled)
            {
                Hide();
                return false;
            }

            var data = DescriptionData.FromHovered(_gameGui, _data);
            if (data is null)
            {
                Hide();
                return false;
            }

            if (!EnsureAttached(addon))
            {
                Hide();
                return false;
            }

            var block = _block!;
            Hide(); // keep hidden while we measure

            var maxWidth = width - 2f * SidePad;
            var lines = WrapText(data.Text, maxWidth);

            var y = block.BodyTop;
            for (var i = 0; i < lines.Count; i++)
            {
                var node = GetOrCreateLine(i);
                node.String = TooltipText.ColorNumbers(lines[i]); // numbers (durations, %, point caps) in green
                // Full-width, center-aligned → each line is centered in the tooltip.
                node.Size = new Vector2(width, LineHeight);
                node.Position = new Vector2(0f, y);
                node.IsVisible = true;
                y += LineHeight;
            }

            for (var i = lines.Count; i < _lines.Count; i++)
                _lines[i].IsVisible = false;

            height = block.Resize(width, y - block.BodyTop) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring description block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> (header-relative; the header is at y=0, so this is
    /// effectively root-absolute).</summary>
    public void PlaceAt(float y)
    {
        if (_block is null) return;
        _block.X = 0;
        _block.Y = y;
    }

    public void Show()
    {
        if (_block is not null) _block.IsVisible = true;
    }

    public void Hide()
    {
        if (_block is not null) _block.IsVisible = false;
    }

    /// <summary>Greedy word-wrap to <paramref name="maxWidth" /> px, honoring explicit <c>\n</c> breaks in the
    /// source. A single word wider than the limit overflows on its own line (rare for lore text).</summary>
    private List<string> WrapText(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty); // preserve blank lines between paragraphs
                continue;
            }

            var current = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                if (word.Length == 0) continue;
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(candidate) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0) lines.Add(current);
        }

        return lines;
    }

    private float Measure(string text)
        => _measure is null ? 0f : _measure.GetTextDrawSize(text, considerScale: false).X;

    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: description block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { IsVisible = false };
            _block.SetHeaderless(); // simple body-only section, no title/divider

            // Attach under the header (#17), like the other added blocks: the game's content reflow leaves the
            // header's children alone, so the block stays put, and the header sits at y=0 so block-relative Y
            // is effectively root-absolute.
            var parent = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            if (parent is null)
                foreach (var id in TooltipLayout.HeaderFallbackIds)
                {
                    parent = addon->GetNodeById(id);
                    if (parent is not null) break;
                }

            if (parent is not null) _block.AttachNode(parent);
            else _block.AttachNode(ptr);

            _measure = new TextNode
            {
                FontType = FontType.Axis,
                FontSize = BodyFontSize,
                AlignmentType = AlignmentType.TopLeft,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                IsVisible = false
            };
            _measure.AttachNode(_block);
        }

        return true;
    }

    /// <summary>Get or lazily create the pooled text node for wrapped line <paramref name="index" />.</summary>
    private TextNode GetOrCreateLine(int index)
    {
        if (index < _lines.Count) return _lines[index];

        var node = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.Center, // horizontally centered within its full-width Size
            TextColor = TextColor,
            TextOutlineColor = OutlineColor,
            IsVisible = false
        };
        node.AttachNode(_block!);
        _lines.Add(node);
        return node;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (_attachedAddon is null || (AtkUnitBase*)args.Addon.Address != _attachedAddon) return;

            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error detaching description block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (the measure node + line nodes) too.
        _block?.Dispose();
        _block = null;
        _measure = null;
        _lines.Clear();
    }

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error disposing description block.");
        }
    }
}
