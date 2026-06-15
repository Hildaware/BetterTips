using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>
///     The data BetterTips' own "Description" section renders: the item's lore description, sourced from the
///     Lumina <see cref="Item" /> sheet (<see cref="Item.Description" />). Because it's the <b>static</b> sheet
///     text, it naturally contains only the lore — none of the per-instance noise the game overlays onto the
///     native description block (<c>#40</c>) at runtime (applied dyes, the "Advanced Melding Forbidden"
///     notice). So no filtering is needed: the source is clean by construction.
/// </summary>
public sealed class DescriptionData
{
    /// <summary>The lore text (plain, payloads stripped; may contain <c>\n</c> line breaks from the sheet).</summary>
    public string Text { get; }

    private DescriptionData(string text) => Text = text;

    /// <summary>
    ///     Build the description data for the currently hovered item, or <c>null</c> when nothing resolvable is
    ///     hovered or the item has no description. Mirrors the other providers' HQ-offset handling.
    /// </summary>
    public static DescriptionData? FromHovered(IGameGui gameGui, IDataManager data)
    {
        var hovered = gameGui.HoveredItem;
        if (hovered == 0) return null;

        var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return null;

        var text = item.Description.ExtractText();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return new DescriptionData(NormalizeLineBreaks(text));
    }

    /// <summary>Build from explicit text (the editor preview's sample).</summary>
    public static DescriptionData? FromText(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : new DescriptionData(NormalizeLineBreaks(text));

    /// <summary>Collapse <c>\r\n</c>/<c>\r</c> to <c>\n</c> so the renderer can split on a single character.</summary>
    private static string NormalizeLineBreaks(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
