using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace BetterTips.Tooltips;

/// <summary>
///     The unified bonuses/materia section's node tree, built once and <see cref="Update" />-d in place from a
///     <see cref="UnifiedBonusesData" />. Shared by the live block (<see cref="UnifiedBonusesBlockProvider" />)
///     and the editor preview so both render the <b>identical</b> layout from one place — tune the geometry
///     constants here and both surfaces move together (the same pattern as <see cref="UnifiedHeaderNodes" />).
///     <para>
///         No header or divider — the attribute bonuses lead, laid out in two columns (green physical / pink
///         mental / gold secondary), then — after a little vertical
///         space — the melded materia in the same two-column form (icon + colored value, no name). The stat
///         and materia nodes are pooled and reused for the parent's whole life (created on demand, hidden when
///         unused, never detached/re-attached) — the lifetime-safe pattern the other blocks established.
///     </para>
/// </summary>
public sealed class UnifiedBonusesNodes
{
    // Geometry — the single source of truth for the section's layout (live + preview). Tune here.
    public const float HPad = 16f;                  // horizontal padding from the section edges
    public const float BodyInsetX = 24f;            // left inset of body content
    public const uint StatFontSize = 12;            // a bonus / materia stat line
    public const float BodyTop = 4f;                // body content top (no header/divider — content starts here)
    public const float StatLineHeight = 18f;        // height of one bonus row
    public const float MateriaGap = 10f;            // vertical space between the bonuses and the materia
    public const float MateriaIconSize = 18f;
    public const float MateriaRowHeight = 22f;      // height of one materia row (icon + value)
    public const float MateriaValueGap = 4f;        // gap between a materia icon and its value text
    public const float BottomPad = 6f;

    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    // The stat palette the user specified — green physical, pink mental, gold secondary. Approximate; tune
    // against the game.
    private static readonly Vector4 GreenColor = new(0x6F / 255f, 0xE3 / 255f, 0x6F / 255f, 1f);
    private static readonly Vector4 PinkColor = new(0xF5 / 255f, 0x8C / 255f, 0xC8 / 255f, 1f);
    private static readonly Vector4 GoldColor = new(0xE8 / 255f, 0xD2 / 255f, 0x7A / 255f, 1f);

    // An empty materia socket — the game's own socket sprite (ItemDetail.tex part 2), read from a live dump of
    // an unmelded slot (the filled orb overlays part 27 on top of this same socket). Drawn at the materia-icon
    // size to line up with the filled-slot icons.
    private const string EmptySocketTexture = "ui/uld/ItemDetail.tex";
    private static readonly Vector2 EmptySocketUv = new(0f, 12f);
    private static readonly Vector2 EmptySocketSize = new(24f, 24f);

    private NodeBase? _parent;
    private readonly List<TextNode> _statTexts = [];
    private readonly List<IconImageNode> _materiaIcons = [];
    private readonly List<TextNode> _materiaValues = [];
    private readonly List<SimpleImageNode> _materiaSockets = [];

    /// <summary>Create the persistent nodes attached to <paramref name="parent" /> (call once). The section has
    /// no header or divider — the stat content starts at the top.</summary>
    public void Build(NodeBase parent)
    {
        _parent = parent;
    }

    /// <summary>Fill the nodes from <paramref name="data" /> and lay them out within <paramref name="width" />;
    /// returns the section's total height. Call when the data changes (not every frame).</summary>
    public float Update(UnifiedBonusesData data, float width)
    {
        var col0X = BodyInsetX;
        var rightEdge = width - BodyInsetX;             // same inset on the right as the left column
        var col1X = BodyInsetX + (rightEdge - BodyInsetX) / 2f;
        var rightColWidth = rightEdge - col1X;

        // --- Bonuses: two columns, "Name +Value" colored by group. Left column left-aligned, right column
        //     right-aligned to the section edge (matches the native Bonuses block). ---
        var y = BodyTop;
        for (var i = 0; i < data.Bonuses.Count; i++)
        {
            var entry = data.Bonuses[i];
            var node = GetOrCreateStat(i);
            node.String = $"{entry.Name}  {entry.Value}";
            node.TextColor = ColorFor(entry.Color);
            var rowY = y + i / 2 * StatLineHeight;

            if (i % 2 == 0)
            {
                node.AlignmentType = AlignmentType.TopLeft;
                node.TextFlags = TextFlags.AutoAdjustNodeSize;
                node.Position = new Vector2(col0X, rowY);
            }
            else
            {
                // Fixed-width right-aligned cell (no auto-adjust), spanning to the section's right edge.
                node.AlignmentType = AlignmentType.TopRight;
                node.TextFlags = default;
                node.Size = new Vector2(rightColWidth, StatLineHeight);
                node.Position = new Vector2(col1X, rowY);
            }

            node.IsVisible = true;
        }

        for (var i = data.Bonuses.Count; i < _statTexts.Count; i++)
            _statTexts[i].IsVisible = false;

        var bonusRows = (data.Bonuses.Count + 1) / 2;
        var contentBottom = y + bonusRows * StatLineHeight;

        // --- Materia: same two columns. Filled = icon + "Stat +Value"; empty = an empty socket (no text). ---
        var materiaTop = data.Bonuses.Count > 0 && data.Materia.Count > 0
            ? contentBottom + MateriaGap
            : contentBottom;

        for (var i = 0; i < data.Materia.Count; i++)
        {
            var entry = data.Materia[i];
            var (icon, value, socket) = GetOrCreateMateria(i);
            var rightColumn = i % 2 == 1;
            var cellY = materiaTop + i / 2 * MateriaRowHeight;

            if (entry.IsEmpty)
            {
                icon.IsVisible = false;
                value.IsVisible = false;
                socket.Size = new Vector2(MateriaIconSize, MateriaIconSize);
                socket.Position = new Vector2(rightColumn ? rightEdge - MateriaIconSize : col0X, cellY);
                socket.IsVisible = true;
                continue;
            }

            socket.IsVisible = false;

            var text = $"{entry.Name}  {entry.Value}";
            value.String = text;
            value.TextColor = ColorFor(entry.Color);
            value.AlignmentType = AlignmentType.TopLeft;
            value.TextFlags = TextFlags.AutoAdjustNodeSize;
            var valueY = cellY + (MateriaIconSize - StatFontSize) / 2f;

            var hasIcon = entry.IconId != 0;
            icon.IsVisible = hasIcon;
            if (hasIcon)
            {
                icon.IconId = entry.IconId;
                icon.Size = new Vector2(MateriaIconSize, MateriaIconSize);
            }

            if (rightColumn)
            {
                // Right-align the icon + value group to the section's right edge (measure the text so the icon
                // tucks just left of it, mirroring the left column's icon-then-text order). Measure unscaled
                // (considerScale: false) — the live ItemDetail addon renders at the user's global UI scale, and
                // a scaled width here wouldn't match width/rightEdge (node-local units), throwing off the
                // alignment in-game while looking fine in the unscaled preview.
                var textX = rightEdge - value.GetTextDrawSize(text, considerScale: false).X;
                if (hasIcon)
                {
                    icon.Position = new Vector2(textX - MateriaValueGap - MateriaIconSize, cellY);
                    // If the group would overflow past the column midpoint, clamp the icon to the column start.
                    if (icon.X < col1X) icon.Position = new Vector2(col1X, cellY);
                }

                value.Position = new Vector2(textX, valueY);
            }
            else
            {
                if (hasIcon) icon.Position = new Vector2(col0X, cellY);
                value.Position = new Vector2(col0X + (hasIcon ? MateriaIconSize + MateriaValueGap : 0f), valueY);
            }

            value.IsVisible = true;
        }

        for (var i = data.Materia.Count; i < _materiaIcons.Count; i++)
        {
            _materiaIcons[i].IsVisible = false;
            _materiaValues[i].IsVisible = false;
            _materiaSockets[i].IsVisible = false;
        }

        var materiaRows = (data.Materia.Count + 1) / 2;
        if (data.Materia.Count > 0)
            contentBottom = materiaTop + materiaRows * MateriaRowHeight;

        return contentBottom + BottomPad;
    }

    /// <summary>Drop the pooled references after the parent (and its children) have been disposed.</summary>
    public void Reset()
    {
        _statTexts.Clear();
        _materiaIcons.Clear();
        _materiaValues.Clear();
        _materiaSockets.Clear();
        _parent = null;
    }

    private static Vector4 ColorFor(BonusColor color) => color switch
    {
        BonusColor.Physical => GreenColor,
        BonusColor.Mental => PinkColor,
        _ => GoldColor
    };

    private TextNode GetOrCreateStat(int index)
    {
        if (index < _statTexts.Count) return _statTexts[index];

        var node = MakeStatText();
        _statTexts.Add(node);
        return node;
    }

    private (IconImageNode Icon, TextNode Value, SimpleImageNode Socket) GetOrCreateMateria(int index)
    {
        if (index < _materiaIcons.Count)
            return (_materiaIcons[index], _materiaValues[index], _materiaSockets[index]);

        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_parent!);
        var value = MakeStatText();
        var socket = new SimpleImageNode
        {
            TexturePath = EmptySocketTexture,
            TextureCoordinates = EmptySocketUv,
            TextureSize = EmptySocketSize,
            WrapMode = WrapMode.Stretch,
            IsVisible = false
        };
        socket.AttachNode(_parent!);
        _materiaIcons.Add(icon);
        _materiaValues.Add(value);
        _materiaSockets.Add(socket);
        return (icon, value, socket);
    }

    private TextNode MakeStatText()
    {
        var node = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = StatFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = GoldColor,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
            IsVisible = false
        };
        node.AttachNode(_parent!);
        return node;
    }
}
