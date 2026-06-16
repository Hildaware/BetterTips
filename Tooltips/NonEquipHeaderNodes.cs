using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;

namespace BetterTips.Tooltips;

/// <summary>
///     The <b>non-equipment</b> item-header section's node tree, built once and <see cref="Update" />-d in place
///     from a <see cref="NonEquipHeaderData" />. Counterpart to <see cref="UnifiedHeaderNodes" /> (the gear
///     header). Two layouts, chosen per item:
///     <list type="bullet">
///         <item><b>Compact</b> (no recast): icon on the left, with the item <b>type above the name</b> to its
///         right. Clean header for ordinary items (materials, cards, …).</item>
///         <item><b>Banner</b> (has a recast cooldown): mirrors the gear header — a centered name + divider on
///         top, then the icon + type and a big <b>recast</b> value (with a "Recast" label) on the right. No item
///         level / required level / job icons (non-equipment has none).</item>
///     </list>
///     Nodes are created once and reused (lifetime-safe; the parent owns them). Tune geometry here.
/// </summary>
public sealed class NonEquipHeaderNodes
{
    public const float HPad = 16f;
    public const uint NameFontSize = 16;
    public const uint TypeFontSize = 12;
    public const uint RecastFontSize = 18;
    public const uint LabelFontSize = 12;
    public const float IconBox = 40f;
    public const float IconX = HPad;
    public const float IconDropY = 4f;
    public const float IconFrameOutset = 4.5f;
    public const float TopPad = 8f;           // top padding for the compact layout
    public const float RecastColWidth = 84f;  // right-aligned recast value/label column

    private const string IconFrameTexture = "ui/uld/IconA_Frame.tex";
    private const string BarTexture = "ui/uld/ItemDetail.tex";

    private static readonly Vector4 CreamColor = new(0xE8 / 255f, 0xDD / 255f, 0xC4 / 255f, 1f);
    private static readonly Vector4 MidGreyColor = new(0xB0 / 255f, 0xB0 / 255f, 0xB0 / 255f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private NodeBase? _parent;
    private TextNode _name = null!, _type = null!, _recastValue = null!, _recastLabel = null!;
    private IconImageNode _icon = null!;
    private SimpleImageNode _frame = null!, _nameDivider = null!, _bottomDivider = null!;

    /// <summary>Create the persistent nodes attached to <paramref name="parent" /> (call once).</summary>
    public void Build(NodeBase parent)
    {
        _parent = parent;

        _name = MakeText(NameFontSize, AlignmentType.TopLeft, CreamColor, autoAdjust: true);
        _nameDivider = MakeBar();

        _icon = new IconImageNode { FitTexture = true, IsVisible = false };
        _icon.AttachNode(parent);
        _frame = new SimpleImageNode
        {
            TexturePath = IconFrameTexture,
            TextureCoordinates = Vector2.Zero,
            TextureSize = new Vector2(48f, 48f),
            WrapMode = WrapMode.Stretch,
            IsVisible = false
        };
        _frame.AttachNode(parent);

        _type = MakeText(TypeFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _recastValue = MakeText(RecastFontSize, AlignmentType.TopRight, CreamColor, autoAdjust: false);
        _recastLabel = MakeText(LabelFontSize, AlignmentType.TopRight, MidGreyColor, autoAdjust: false);
        _bottomDivider = MakeBar();
    }

    /// <summary>Fill the nodes from <paramref name="data" /> and lay them out within <paramref name="width" />;
    /// returns the block's total height. Call when the data changes (not every frame).</summary>
    public float Update(NonEquipHeaderData data, float width)
        => data.HasRecast ? UpdateBanner(data, width) : UpdateCompact(data, width);

    /// <summary>Compact layout: icon left, type above name to its right. No banner/divider, no recast.</summary>
    private float UpdateCompact(NonEquipHeaderData data, float width)
    {
        _nameDivider.IsVisible = false;
        _recastValue.IsVisible = false;
        _recastLabel.IsVisible = false;

        var iconY = TopPad;
        ShowIcon(data, iconY);

        var statX = IconX + IconBox + 14f;
        SetText(_type, data.Type, MidGreyColor, statX, iconY + 2f);
        SetName(data, statX, iconY + 18f, AlignmentType.TopLeft, width - statX - HPad);

        var contentBottom = Math.Max(iconY + IconBox, iconY + 18f + NameFontSize + 4f);
        return Finish(width, contentBottom);
    }

    /// <summary>Banner layout: centered name + divider, then icon + type and the big recast value/label.</summary>
    private float UpdateBanner(NonEquipHeaderData data, float width)
    {
        // Centered name + thick divider (like the gear header).
        SetName(data, 0f, 0f, AlignmentType.Center, width);

        var nameDividerY = NameFontSize + 10f;
        _nameDivider.IsVisible = true;
        _nameDivider.Size = new Vector2(width - HPad * 2f, 6f);
        _nameDivider.Position = new Vector2(HPad, nameDividerY);

        var bodyTop = nameDividerY + 12f;
        var iconY = bodyTop + IconDropY;
        ShowIcon(data, iconY);

        // Type sits next to the icon, vertically centered against it.
        var statX = IconX + IconBox + 14f;
        SetText(_type, data.Type, MidGreyColor, statX, iconY + (IconBox - TypeFontSize) / 2f);

        // Big recast value + "Recast" label, right-aligned.
        var rightEdge = width - HPad;
        var recastX = rightEdge - RecastColWidth;
        _recastValue.IsVisible = true;
        _recastValue.String = data.RecastText;
        _recastValue.Size = new Vector2(RecastColWidth, RecastFontSize + 6f);
        _recastValue.Position = new Vector2(recastX, iconY);
        _recastLabel.IsVisible = true;
        _recastLabel.String = "Recast";
        _recastLabel.Size = new Vector2(RecastColWidth, LabelFontSize + 6f);
        _recastLabel.Position = new Vector2(recastX, iconY + RecastFontSize + 4f);

        var contentBottom = Math.Max(iconY + IconBox, iconY + RecastFontSize + 4f + LabelFontSize + 4f);
        return Finish(width, contentBottom);
    }

    /// <summary>Place the bottom divider below the content and return the block's total height.</summary>
    private float Finish(float width, float contentBottom)
    {
        var bottomDividerY = contentBottom + 8f;
        _bottomDivider.Size = new Vector2(width - HPad * 2f, 4f);
        _bottomDivider.Position = new Vector2(HPad, bottomDividerY);
        return bottomDividerY + 8f;
    }

    private void ShowIcon(NonEquipHeaderData data, float iconY)
    {
        var hasIcon = data.IconId != 0;
        _icon.IsVisible = hasIcon;
        _frame.IsVisible = hasIcon;
        if (!hasIcon) return;

        _icon.IconId = data.IconId;
        _icon.Size = new Vector2(IconBox, IconBox);
        _icon.Position = new Vector2(IconX, iconY);
        _frame.Size = new Vector2(IconBox + IconFrameOutset * 2f, IconBox + IconFrameOutset * 2f);
        _frame.Position = new Vector2(IconX - IconFrameOutset, iconY - IconFrameOutset);
    }

    /// <summary>Set the name node (rarity-coloured; prefers the rendered native name's raw SeString for payload
    /// glyphs like the HQ mark), with the given alignment + width.</summary>
    private void SetName(NonEquipHeaderData data, float x, float y, AlignmentType align, float width)
    {
        _name.String = data.NameRaw is not null ? new ReadOnlySeString(data.NameRaw) : data.Name;
        _name.TextColor = RarityColor(data.Rarity);
        _name.AlignmentType = align;
        _name.TextFlags = align == AlignmentType.Center ? 0 : TextFlags.AutoAdjustNodeSize;
        _name.Size = new Vector2(width, NameFontSize + 8f);
        _name.Position = new Vector2(x, y);
        _name.IsVisible = true;
    }

    /// <summary>Drop references after the parent (and its children) have been disposed.</summary>
    public void Reset() => _parent = null;

    private static Vector4 RarityColor(byte rarity) => rarity switch
    {
        2 => new Vector4(0x4B / 255f, 0xD6 / 255f, 0x6B / 255f, 1f),  // green / uncommon
        3 => new Vector4(0x5B / 255f, 0xA3 / 255f, 0xE0 / 255f, 1f),  // blue / rare
        4 => new Vector4(0xC5 / 255f, 0x8B / 255f, 0xE0 / 255f, 1f),  // purple / relic
        7 => new Vector4(0xE0 / 255f, 0xA0 / 255f, 0xC8 / 255f, 1f),  // pink / aetherial
        _ => new Vector4(1f, 1f, 1f, 1f)                               // white / normal
    };

    private TextNode MakeText(uint fontSize, AlignmentType align, Vector4 color, bool autoAdjust)
    {
        var node = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = fontSize,
            AlignmentType = align,
            TextColor = color,
            TextOutlineColor = OutlineColor
        };
        if (autoAdjust) node.TextFlags = TextFlags.AutoAdjustNodeSize;
        node.AttachNode(_parent!);
        return node;
    }

    private SimpleImageNode MakeBar()
    {
        var bar = new SimpleImageNode
        {
            TexturePath = BarTexture,
            TextureCoordinates = Vector2.Zero,
            TextureSize = new Vector2(112f, 12f),
            WrapMode = WrapMode.Stretch
        };
        bar.AttachNode(_parent!);
        return bar;
    }

    private static void SetText(TextNode node, string text, Vector4 color, float x, float y)
    {
        node.String = text;
        node.TextColor = color;
        node.Position = new Vector2(x, y);
        node.IsVisible = true;
    }
}
