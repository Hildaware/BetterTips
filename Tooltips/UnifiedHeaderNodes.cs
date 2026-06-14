using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace BetterTips.Tooltips;

/// <summary>
///     The unified item-header section's node tree, built once and <see cref="Update" />-d in place from a
///     <see cref="UnifiedHeaderData" />. Shared by the live block (<see cref="UnifiedHeaderBlockProvider" />)
///     and the editor preview so both render the <b>identical</b> layout from one place — tune the geometry
///     constants here and both surfaces move together.
///     <para>
///         Nodes are created once and reused for the parent's whole life (text/icon/position updated in
///         place, never detached/re-attached) — the lifetime-safe pattern the gear-set block established.
///         The parent owns the nodes; disposing it frees them, after which <see cref="Reset" /> drops the
///         job-icon references.
///     </para>
/// </summary>
public sealed class UnifiedHeaderNodes
{
    // Geometry — the single source of truth for the unified header's layout (live + preview). Tune here.
    public const float HPad = 16f;               // horizontal padding from the tooltip edges (matches natives)
    public const uint NameFontSize = 16;
    public const uint PrimaryFontSize = 28;      // the big primary-stat number
    public const uint ItemLevelFontSize = 22;
    public const uint ReqFontSize = 13;
    public const uint LabelFontSize = 12;        // descriptor / Item Level / class-job-text labels
    public const uint CategoryFontSize = 10;     // the item-category line
    public const float IconBox = 48f;
    public const float IconX = 24f;              // icon left edge — clears the durability/spiritbond gauges
    public const float IconDropY = 10f;          // the icon (+ frame) sits this far below the name divider
    public const float IconFrameOutset = 4.5f;   // the frame overlay extends this far past the icon
    public const float JobIconSize = 24f;
    public const float JobIconGap = 4f;
    public const float JobGapAbove = 18f;        // breathing room above the job-icon row
    public const float JobRowX = 100f;           // left edge of the job-icon row
    public const float IlvlTextWidth = 62f;      // ~width of the "Item Level" label (drives its divider)

    /// <summary>The icon's Y offset from the block top (the controller aligns the native gauges to it).</summary>
    public const float IconOffsetY = NameFontSize + 22f + IconDropY;

    private const string IconFrameTexture = "ui/uld/IconA_Frame.tex";
    private const string BarTexture = "ui/uld/ItemDetail.tex";

    private static readonly Vector4 CreamColor = new(0xE8 / 255f, 0xDD / 255f, 0xC4 / 255f, 1f);
    private static readonly Vector4 MidGreyColor = new(0xB0 / 255f, 0xB0 / 255f, 0xB0 / 255f, 1f);
    private static readonly Vector4 ItemLevelColor = new(0xE0 / 255f, 0xA8 / 255f, 0x60 / 255f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);
    private const float BorderOutset = 2f;  // the current-job outline extends this far past the icon
    private static readonly Vector4 CurrentJobTextColor = new(0x5A / 255f, 0xE6 / 255f, 0x5A / 255f, 1f);

    // MultiplyColor is the native 0-100 scale (100 = neutral), so these tint the light bar texture.
    private static readonly Vector3 CurrentJobBorderTint = new(15f, 100f, 25f);   // green outline
    private static readonly Vector3 DurabilityBarTint = new(100f, 78f, 30f);      // gold-ish (mock gauge)
    private static readonly Vector3 SpiritbondBarTint = new(40f, 78f, 100f);      // cyan-ish (mock gauge)

    private NodeBase? _parent;
    private bool _mockBars;
    private TextNode _name = null!, _category = null!, _primary = null!, _descriptor = null!;
    private TextNode _ilvlLabel = null!, _ilvlValue = null!, _req = null!, _jobText = null!;
    private IconImageNode _icon = null!;
    private SimpleImageNode _frame = null!, _nameDivider = null!, _ilvlDivider = null!, _bottomDivider = null!;
    private SimpleImageNode _jobBorder = null!;
    private SimpleImageNode? _durabilityBar, _spiritbondBar;
    private readonly List<IconImageNode> _jobIcons = [];

    /// <summary>Create the persistent nodes attached to <paramref name="parent" /> (call once). The preview
    /// passes <paramref name="mockDurabilityBars" /> = true to draw stand-in gauges; the live tooltip uses the
    /// game's own gauge node instead.</summary>
    public void Build(NodeBase parent, bool mockDurabilityBars = false)
    {
        _parent = parent;
        _mockBars = mockDurabilityBars;

        _name = MakeText(NameFontSize, AlignmentType.Center, CreamColor, autoAdjust: false);
        _nameDivider = MakeBar();

        if (_mockBars)
        {
            _durabilityBar = MakeBar();
            _durabilityBar.MultiplyColor = DurabilityBarTint;
            _spiritbondBar = MakeBar();
            _spiritbondBar.MultiplyColor = SpiritbondBarTint;
        }

        // The current-job outline sits behind the job icons (which are created lazily later), so it reads as
        // a border around the icon on top of it.
        _jobBorder = MakeBar();
        _jobBorder.MultiplyColor = CurrentJobBorderTint;
        _jobBorder.IsVisible = false;

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
        _category = MakeText(CategoryFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _primary = MakeText(PrimaryFontSize, AlignmentType.TopLeft, CreamColor, autoAdjust: true);
        _descriptor = MakeText(LabelFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _ilvlLabel = MakeText(LabelFontSize, AlignmentType.TopRight, MidGreyColor, autoAdjust: false);
        _ilvlDivider = MakeBar();
        _ilvlValue = MakeText(ItemLevelFontSize, AlignmentType.TopRight, ItemLevelColor, autoAdjust: false);
        _req = MakeText(ReqFontSize, AlignmentType.TopLeft, CreamColor, autoAdjust: true);
        _jobText = MakeText(LabelFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _bottomDivider = MakeBar();
    }

    /// <summary>Fill the nodes from <paramref name="data" /> and lay them out within <paramref name="width" />;
    /// returns the block's total height. Call when the data changes (not every frame).</summary>
    public float Update(UnifiedHeaderData data, float width)
    {
        // Name (centered, rarity-colored) + thick divider.
        _name.String = data.Name;
        _name.TextColor = RarityColor(data.Rarity);
        _name.Size = new Vector2(width, NameFontSize + 8f);
        _name.Position = new Vector2(0f, 0f);

        var nameDividerY = NameFontSize + 10f;
        _nameDivider.Size = new Vector2(width - HPad * 2f, 6f);
        _nameDivider.Position = new Vector2(HPad, nameDividerY);

        var bodyTop = nameDividerY + 12f;
        var iconY = bodyTop + IconDropY;

        // Item icon + framed border/gloss overlay.
        var hasIcon = data.IconId != 0;
        _icon.IsVisible = hasIcon;
        _frame.IsVisible = hasIcon;
        if (hasIcon)
        {
            _icon.IconId = data.IconId;
            _icon.Size = new Vector2(IconBox, IconBox);
            _icon.Position = new Vector2(IconX, iconY);
            _frame.Size = new Vector2(IconBox + IconFrameOutset * 2f, IconBox + IconFrameOutset * 2f);
            _frame.Position = new Vector2(IconX - IconFrameOutset, iconY - IconFrameOutset);
        }

        // Stand-in durability/spiritbond gauges (preview only) — left of the icon, aligned with it.
        if (_mockBars && _durabilityBar is not null && _spiritbondBar is not null)
        {
            _durabilityBar.Size = new Vector2(5f, IconBox);
            _durabilityBar.Position = new Vector2(6f, iconY);
            _spiritbondBar.Size = new Vector2(5f, IconBox);
            _spiritbondBar.Position = new Vector2(12f, iconY);
        }

        // Category / big primary stat / descriptor (right of the icon).
        var statX = IconX + IconBox + 14f;
        SetText(_category, data.Category, statX, bodyTop);
        SetText(_primary, data.PrimaryValue, statX, bodyTop + 14f);
        SetText(_descriptor, data.PrimaryLabel, statX, bodyTop + 46f);
        var midBottom = bodyTop + Math.Max(IconDropY + IconBox, 58f);

        // Item Level (right column): label, divider the width of the label text, value — all right-aligned.
        var rightEdge = width - HPad;
        var ilvlX = rightEdge - IlvlTextWidth;
        _ilvlLabel.String = "Item Level";
        _ilvlLabel.Size = new Vector2(IlvlTextWidth, LabelFontSize + 6f);
        _ilvlLabel.Position = new Vector2(ilvlX, bodyTop + 2f);
        _ilvlDivider.Size = new Vector2(IlvlTextWidth, 5f);
        _ilvlDivider.Position = new Vector2(ilvlX, bodyTop + 22f);
        _ilvlValue.String = data.ItemLevel > 0 ? data.ItemLevel.ToString() : "—";
        _ilvlValue.Size = new Vector2(IlvlTextWidth, ItemLevelFontSize + 6f);
        _ilvlValue.Position = new Vector2(ilvlX, bodyTop + 28f);

        // Bottom row: Req. Lv (left) + equippable-job icons, or the class/job text for a broad category.
        var bottomY = midBottom + JobGapAbove;
        SetText(_req, $"Req. Lv {data.RequiredLevel}", HPad, bottomY);
        LayoutJobs(data, bottomY);

        var bottomDividerY = bottomY + JobIconSize + 8f;
        _bottomDivider.Size = new Vector2(width - HPad * 2f, 4f);
        _bottomDivider.Position = new Vector2(HPad, bottomDividerY);

        return bottomDividerY + 8f;
    }

    /// <summary>The job-icon row (with a green outline on the current job), or the class/job text fallback for
    /// a broad category (greened when the current job can equip it).</summary>
    private void LayoutJobs(UnifiedHeaderData data, float bottomY)
    {
        if (data.JobIconIds.Count == 0)
        {
            _jobBorder.IsVisible = false;
            foreach (var icon in _jobIcons) icon.IsVisible = false;

            _jobText.IsVisible = true;
            _jobText.String = data.ClassJobText;
            _jobText.TextColor = data.CurrentJobEquippable ? CurrentJobTextColor : MidGreyColor;
            _jobText.Position = new Vector2(JobRowX, bottomY);
            return;
        }

        _jobText.IsVisible = false;

        var jobX = JobRowX;
        var jobY = bottomY - 4f;
        var borderPlaced = false;
        for (var i = 0; i < data.JobIconIds.Count; i++)
        {
            var iconId = data.JobIconIds[i];
            var icon = GetOrCreateJobIcon(i);
            icon.IconId = iconId;
            icon.Size = new Vector2(JobIconSize, JobIconSize);
            icon.Position = new Vector2(jobX, jobY);
            icon.IsVisible = true;

            if (!borderPlaced && data.CurrentJobIconId != 0 && iconId == data.CurrentJobIconId)
            {
                _jobBorder.Size = new Vector2(JobIconSize + BorderOutset * 2f, JobIconSize + BorderOutset * 2f);
                _jobBorder.Position = new Vector2(jobX - BorderOutset, jobY - BorderOutset);
                _jobBorder.IsVisible = true;
                borderPlaced = true;
            }

            jobX += JobIconSize + JobIconGap;
        }

        if (!borderPlaced) _jobBorder.IsVisible = false;
        for (var i = data.JobIconIds.Count; i < _jobIcons.Count; i++)
            _jobIcons[i].IsVisible = false;
    }

    /// <summary>Drop the job-icon references after the parent (and its children) have been disposed.</summary>
    public void Reset()
    {
        _jobIcons.Clear();
        _parent = null;
    }

    /// <summary>Item name colors by rarity (1 normal … 7 aetherial). Approximate — tune against the game.</summary>
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

    private IconImageNode GetOrCreateJobIcon(int index)
    {
        if (index < _jobIcons.Count) return _jobIcons[index];

        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_parent!);
        _jobIcons.Add(icon);
        return icon;
    }

    private static void SetText(TextNode node, string text, float x, float y)
    {
        node.String = text;
        node.Position = new Vector2(x, y);
    }
}
