using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own <b>Effects</b> section — a headerless, tightly-packed list of the item's effect
///     lines (food stat bonuses, potion HP restore, …) with all <b>numbers coloured green</b> so they stand
///     out. Enhanced-only; replaces the native Effects block (<c>#49</c>, which the relayout hides) and is laid
///     out just above the Description section.
///     <para>
///         The lines come from scraping <c>#49</c> (<see cref="EffectsData" />). Since the relayout hides
///         <c>#49</c> once we show, a later same-item scrape returns nothing — so the lines are <b>cached per
///         hovered item</b> (only a non-null scrape replaces the cache), the same trick the Condition/materia
///         blocks use. Node lifetime mirrors the other added blocks; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class EffectsBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    public const float LineHeight = 16f;
    public const float TopPad = 2f;          // small top padding (headerless, kept tight)
    public const float ColumnGap = 8f;       // gap between the two columns (stat-based effects)
    public const float TwoColInset = 16f;    // side inset for the two-column layout (tighter than BodyInsetX
                                             // to give stat lines room to fit)
    public const uint BodyFontSize = 12;
    private const float BlockBottomPad = 2f;

    // A stat-based effect line ("Critical Hit +8% (Max 60)", "Vitality +1") has a "+N"; a sentence effect
    // ("Restores up to 30% of HP …") doesn't. Stat-based sets render two-up; sentences stay full-width.
    private static readonly Regex StatLineRegex = new(@"\+\d", RegexOptions.Compiled);

    private static readonly Vector4 BaseColor = new(1f, 1f, 1f, 1f);   // non-number text (white)
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);
    // Numbers are coloured this green (matches the class-name green used elsewhere) to stand out.
    private const byte GreenR = 0x8C, GreenG = 0xFF, GreenB = 0x5A;

    // Number runs: optional sign, digits with thousands separators/decimals, optional percent.
    private static readonly Regex NumberRegex = new(@"[+-]?\d[\d,\.]*%?", RegexOptions.Compiled);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly EffectsSource _source;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private readonly List<TextNode> _lines = [];
    private AtkUnitBase* _attachedAddon;

    // #49 is hidden once our block shows, so a later scrape comes back empty — cache the lines per hovered
    // item; only a non-null scrape replaces the cache, and it resets on an item change.
    private ulong _lastHovered = ulong.MaxValue;
    private IReadOnlyList<string>? _cachedLines;

    public EffectsBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, EffectsSource source,
        Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _source = source;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Enhanced-only (ships with the curated layout), like the custom Description section.</summary>
    public bool Enabled => _config.Enabled && _config.EnhancedMode;

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the item has no
    ///     effects. Must run <em>before</em> the relayout hides <c>#49</c> (it's scraped from there).
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

            // Read effects (text from the string array via the hook; gated on #49 being visible before the
            // relayout hides it) and cache per hover so the hide can't wipe the gate.
            var data = EffectsData.FromHovered(addon, _source);
            var hovered = _gameGui.HoveredItem;
            if (hovered != _lastHovered)
            {
                _lastHovered = hovered;
                _cachedLines = data?.Lines;
            }
            else if (data is not null)
            {
                _cachedLines = data.Lines;
            }

            if (_cachedLines is null || _cachedLines.Count == 0)
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

            var lines = _cachedLines;
            var baseY = block.BodyTop + TopPad;

            // Two columns only when every line is stat-based AND each fits a half-width column (else the long
            // ones would overlap) — otherwise a single full-width column (sentence effects, or long stat names).
            var twoColWidth = (width - TwoColInset * 2f - ColumnGap) / 2f;
            var twoColumn = TwoColumn(lines, twoColWidth);

            int rows;
            if (twoColumn)
            {
                // Left column left-aligned at the inset; right column right-aligned so its right edge lands at
                // (width - TwoColInset) — the same padding as the left column's left edge.
                var leftX = TwoColInset;
                var rightX = TwoColInset + twoColWidth + ColumnGap;
                for (var i = 0; i < lines.Count; i++)
                {
                    var node = GetOrCreate(i);
                    node.String = ColorNumbers(lines[i]);
                    var rowY = baseY + i / 2 * LineHeight;

                    if (i % 2 == 0)
                    {
                        node.AlignmentType = AlignmentType.TopLeft;
                        node.TextFlags = TextFlags.AutoAdjustNodeSize;
                        node.Position = new Vector2(leftX, rowY);
                    }
                    else
                    {
                        // Right-aligned: fixed-width node (no auto-adjust) so the text hugs the right edge.
                        node.AlignmentType = AlignmentType.TopRight;
                        node.TextFlags = 0;
                        node.Size = new Vector2(twoColWidth, LineHeight);
                        node.Position = new Vector2(rightX, rowY);
                    }

                    node.IsVisible = true;
                }

                rows = (lines.Count + 1) / 2;
            }
            else
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var node = GetOrCreate(i);
                    node.String = ColorNumbers(lines[i]);
                    // Reset to left-aligned auto-size (a node may have been a right column cell last render).
                    node.AlignmentType = AlignmentType.TopLeft;
                    node.TextFlags = TextFlags.AutoAdjustNodeSize;
                    node.Position = new Vector2(TooltipContentBlock.BodyInsetX, baseY + i * LineHeight);
                    node.IsVisible = true;
                }

                rows = lines.Count;
            }

            for (var i = lines.Count; i < _lines.Count; i++)
                _lines[i].IsVisible = false;

            height = block.Resize(width, TopPad + rows * LineHeight) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring effects block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> (header-relative; the header is at y=0).</summary>
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

    /// <summary>Build a SeString for one effect line with number runs coloured green (the rest renders in the
    /// node's base colour).</summary>
    private static ReadOnlySeString ColorNumbers(string line)
    {
        var sb = new SeStringBuilder();
        var last = 0;
        foreach (Match m in NumberRegex.Matches(line))
        {
            if (m.Index > last) sb.Append(line.AsSpan(last, m.Index - last));
            sb.PushColorRgba(GreenR, GreenG, GreenB, 0xFF);
            sb.Append(line.AsSpan(m.Index, m.Length));
            sb.PopColor();
            last = m.Index + m.Length;
        }

        if (last < line.Length) sb.Append(line.AsSpan(last));
        return sb.ToReadOnlySeString();
    }

    /// <summary>Whether to render the lines two-up: at least two lines, all stat-based, and each fits a
    /// half-width column (measured) so they won't overlap.</summary>
    private bool TwoColumn(IReadOnlyList<string> lines, float colWidth)
    {
        if (lines.Count < 2) return false;

        foreach (var line in lines)
            if (!StatLineRegex.IsMatch(line))
                return false;

        // Measure each line against the column (the gap is already excluded from colWidth); a node is needed
        // for GetTextDrawSize.
        var probe = GetOrCreate(0);
        foreach (var line in lines)
            if (probe.GetTextDrawSize(line, considerScale: false).X > colWidth)
                return false;

        return true;
    }

    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: effects block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { IsVisible = false };
            _block.SetHeaderless(); // no title/divider — just the effect lines

            var parent = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            if (parent is null)
                foreach (var id in TooltipLayout.HeaderFallbackIds)
                {
                    parent = addon->GetNodeById(id);
                    if (parent is not null) break;
                }

            if (parent is not null) _block.AttachNode(parent);
            else _block.AttachNode(ptr);
        }

        return true;
    }

    private TextNode GetOrCreate(int index)
    {
        if (index < _lines.Count) return _lines[index];

        var node = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = BaseColor,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
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
            _log.Error(ex, "BetterTips: error detaching effects block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        _block?.Dispose();
        _block = null;
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
            _log.Error(ex, "BetterTips: error disposing effects block.");
        }
    }
}
