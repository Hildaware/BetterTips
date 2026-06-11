using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     A reusable "content block" that mirrors the structure of a native item-tooltip section (e.g. the
///     "Bonuses" block): a styled <b>header</b> label, a full-width <b>divider</b> line beneath it, then a
///     <b>body</b> area for content. The header style is matched to the game's section headers (read from a
///     live <c>ItemDetail</c> dump — Axis 12, light grey <c>#C6C6C6</c>, black outline); the divider is
///     KamiToolKit's <see cref="HorizontalLineNode" /> (the game's <c>WindowA_Line</c> texture).
///     <para>
///         Attach body content (icons, text, …) as children of this node and position it at/below
///         <see cref="BodyTop" /> from <see cref="BodyInsetX" />; then call <see cref="Resize" /> with the
///         measured body height. The header and divider are owned here; callers only fill the body.
///     </para>
/// </summary>
/// <remarks>
///     Layout values come from a live ItemDetail section (header x=16, divider x=15 w≈width, body indented).
///     Re-dump (<c>/btips dumpnodes</c>) and adjust if SE revises the tooltip ULD or styling.
/// </remarks>
public sealed unsafe class TooltipContentBlock : ResNode
{
    /// <summary>Left inset of the header text (matches the game's section labels).</summary>
    public const float HeaderInsetX = 16f;

    /// <summary>Left inset of body content — indented past the header so the body reads as nested.</summary>
    public const float BodyInsetX = 24f;

    private const float HeaderInsetY = 4f;   // header text top (game value)
    private const float DividerInsetX = 15f; // divider left inset (game value)
    private const float DividerRightInset = 15f;
    private const float DividerY = 18f;      // divider top (game value: header at y=4, divider at y=18)
    private const float DividerHeight = 4f;
    private const float BodyY = 26f;         // body content top (just below the divider)

    // The game's tooltip section headers: Axis font, size 12, light grey #C6C6C6 with a black outline.
    private const uint HeaderFontSize = 12;
    private static readonly Vector4 HeaderColor = new(0xC6 / 255f, 0xC6 / 255f, 0xC6 / 255f, 1f);
    private static readonly Vector4 HeaderOutline = new(0f, 0f, 0f, 1f);

    private readonly TextNode _header;
    private readonly HorizontalLineNode _divider;

    public TooltipContentBlock()
    {
        _header = new TextNode
        {
            Position = new Vector2(HeaderInsetX, HeaderInsetY),
            FontType = FontType.Axis,
            FontSize = HeaderFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = HeaderColor,
            TextOutlineColor = HeaderOutline,
            TextFlags = TextFlags.AutoAdjustNodeSize
        };
        _header.AttachNode(this);

        _divider = new HorizontalLineNode
        {
            Height = DividerHeight,
            Position = new Vector2(DividerInsetX, DividerY)
        };
        _divider.AttachNode(this);
    }

    /// <summary>The body content's top Y, relative to this block. Position body children at/below it.</summary>
    public float BodyTop => BodyY;

    /// <summary>Sets the header label text.</summary>
    public string HeaderText
    {
        set => _header.String = value;
    }

    /// <summary>
    ///     Size the block to <paramref name="width" /> with a body of <paramref name="bodyHeight" />,
    ///     stretching the divider to span the width. Returns the block's total height.
    /// </summary>
    public float Resize(float width, float bodyHeight)
    {
        _divider.Width = width - DividerInsetX - DividerRightInset;

        var total = BodyY + bodyHeight;
        Size = new Vector2(width, total);
        return total;
    }
}
