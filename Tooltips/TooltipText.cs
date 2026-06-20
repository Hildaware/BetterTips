using System.Text.RegularExpressions;
using KamiToolKit.Nodes;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace BetterTips.Tooltips;

/// <summary>Shared text helpers for the custom tooltip sections.</summary>
internal static class TooltipText
{
    /// <summary>
    ///     Shrink <paramref name="node" />'s font size — starting at <paramref name="baseFontSize" />, stepping
    ///     down no lower than <paramref name="minFontSize" /> — until <paramref name="content" /> fits within
    ///     <paramref name="availableWidth" /> (so an over-long item name shrinks rather than overflowing/clipping
    ///     the tooltip). Measures with the node's own params via
    ///     <see cref="TextNode.GetTextDrawSize(ReadOnlySeString, bool)" />, so <paramref name="content" /> should
    ///     be the same text the node displays. Sets the node's <see cref="TextNode.FontSize" /> and returns the
    ///     chosen size.
    /// </summary>
    public static uint FitFontSize(TextNode node, ReadOnlySeString content, uint baseFontSize, uint minFontSize,
        float availableWidth)
    {
        var size = baseFontSize;
        while (true)
        {
            node.FontSize = size;
            if (size <= minFontSize) break;                                              // legibility floor
            if (availableWidth <= 0f) break;                                             // no width to fit to
            if (node.GetTextDrawSize(content, considerScale: false).X <= availableWidth) // fits at this size
                break;
            size--;
        }

        return size;
    }

    // Numbers are coloured this green (matches the class-name green used elsewhere) to stand out.
    private const byte GreenR = 0x8C, GreenG = 0xFF, GreenB = 0x5A;

    // Number runs: optional sign, digits with thousands separators/decimals, optional percent.
    private static readonly Regex NumberRegex = new(@"[+-]?\d[\d,\.]*%?", RegexOptions.Compiled);

    /// <summary>
    ///     Build a SeString for <paramref name="line" /> with number runs coloured green (the rest renders in
    ///     the node's own text colour). Glyph widths are unchanged, so any prior wrap/alignment still holds.
    /// </summary>
    public static ReadOnlySeString ColorNumbers(string line)
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
}
