using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;

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
    public const uint MinNameFontSize = 11;      // floor the name shrinks to when it won't fit the width
    public const uint PrimaryFontSize = 18;      // the big primary-stat number
    public const uint ItemLevelFontSize = 16;    // the Item Level / Req Level numbers
    public const uint LabelFontSize = 12;        // descriptor / Item Level / class-job-text labels
    public const uint CategoryFontSize = 10;     // the item-category line
    public const float IconBox = 40f;
    public const float IconX = HPad;             // icon left edge — normal tooltip padding (the gauge bars are
                                                 // hidden now, so the icon no longer needs to clear them)
    public const float IconDropY = 4f;           // the icon (+ frame) sits this far below the name divider
    public const float IconFrameOutset = 4.5f;   // the frame overlay extends this far past the icon
    public const float JobIconSize = 24f;
    public const float JobIconGap = 4f;
    public const float JobRowOffsetY = -26f;     // job-row offset from content bottom (negative lifts it up)
    public const float IlvlTextWidth = 62f;      // ~width of the "Item Level" label (drives its divider)
    public const float ReqTextWidth = 56f;       // ~width of the "Req Level" label (drives its divider)

    private const string IconFrameTexture = "ui/uld/IconA_Frame.tex";
    private const string BarTexture = "ui/uld/ItemDetail.tex";

    private static readonly Vector4 CreamColor = new(0xE8 / 255f, 0xDD / 255f, 0xC4 / 255f, 1f);
    private static readonly Vector4 MidGreyColor = new(0xB0 / 255f, 0xB0 / 255f, 0xB0 / 255f, 1f);
    private static readonly Vector4 ItemLevelColor = new(0xE0 / 255f, 0xA8 / 255f, 0x60 / 255f, 1f);
    private static readonly Vector4 RequirementUnmetColor = new(0xE0 / 255f, 0x3C / 255f, 0x32 / 255f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);
    private const float BorderOutset = -1f;    // the current-job frame sits this far outside the icon edge (negative insets it, shrinking the frame 4px on X/Y)
    private const float BorderThickness = 2f;  // thickness of each of the four green frame bars
    private const int JobIconPoolSize = 16;    // matches UnifiedHeaderData.MaxJobIcons (never more icons than this)
    private static readonly Vector4 CurrentJobTextColor = new(0x5A / 255f, 0xE6 / 255f, 0x5A / 255f, 1f);
    private static readonly Vector4 CurrentJobBorderColor = new(0x4B / 255f, 0xE6 / 255f, 0x5A / 255f, 0.5f); // green outline, 50% opacity

    private NodeBase? _parent;
    private TextNode _name = null!, _category = null!, _primary = null!, _descriptor = null!;
    private TextNode _ilvlLabel = null!, _ilvlValue = null!, _reqLabel = null!, _req = null!, _jobText = null!;
    private IconImageNode _icon = null!;
    private SimpleImageNode _frame = null!, _nameDivider = null!, _ilvlDivider = null!, _reqDivider = null!, _bottomDivider = null!;
    private readonly ColorImageNode[] _jobBorderBars = new ColorImageNode[4];  // top, bottom, left, right
    private readonly List<IconImageNode> _jobIcons = [];

    /// <summary>Create the persistent nodes attached to <paramref name="parent" /> (call once).</summary>
    public void Build(NodeBase parent)
    {
        _parent = parent;

        _name = MakeText(NameFontSize, AlignmentType.Center, CreamColor, autoAdjust: false);
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
        _category = MakeText(CategoryFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _primary = MakeText(PrimaryFontSize, AlignmentType.TopLeft, CreamColor, autoAdjust: true);
        _descriptor = MakeText(LabelFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _ilvlLabel = MakeText(LabelFontSize, AlignmentType.TopRight, MidGreyColor, autoAdjust: false);
        _ilvlDivider = MakeBar();
        _ilvlValue = MakeText(ItemLevelFontSize, AlignmentType.TopRight, ItemLevelColor, autoAdjust: false);
        _reqLabel = MakeText(LabelFontSize, AlignmentType.TopRight, MidGreyColor, autoAdjust: false);
        _reqDivider = MakeBar();
        _req = MakeText(ItemLevelFontSize, AlignmentType.TopRight, CreamColor, autoAdjust: false);
        _jobText = MakeText(LabelFontSize, AlignmentType.TopLeft, MidGreyColor, autoAdjust: true);
        _bottomDivider = MakeBar();

        // Pre-create the job-icon pool, THEN the border bars, so the bars are the last-attached siblings and
        // therefore always render ON TOP of every job icon (in KTK, a later-attached sibling draws on top — the
        // same rule that puts the item _frame over _icon). The pool is fixed because ResolveJobIcons never
        // returns more than its MaxJobIcons (it falls back to text beyond that), so a lazy path can't outrun it.
        // The border is four thin SOLID-COLOR bars forming a frame around the current job icon — ColorImageNode
        // renders a flat fill regardless of texture (the old MultiplyColor-tinted bar texture rendered nothing
        // when stretched into the thin vertical left/right bars, so the outline never appeared in-game).
        for (var i = 0; i < JobIconPoolSize; i++)
            _jobIcons.Add(MakeJobIcon());

        for (var i = 0; i < _jobBorderBars.Length; i++)
        {
            var bar = new ColorImageNode { Color = CurrentJobBorderColor, IsVisible = false };
            bar.AttachNode(parent);
            _jobBorderBars[i] = bar;
        }
    }

    /// <summary>Fill the nodes from <paramref name="data" /> and lay them out within <paramref name="width" />;
    /// returns the block's total height. Call when the data changes (not every frame).</summary>
    public float Update(UnifiedHeaderData data, float width)
    {
        // Name (centered, rarity-colored) + thick divider. Prefer the rendered native name (raw SeString)
        // so the game's payload glyphs (HQ mark, etc.) show; fall back to the plain Lumina name (preview).
        ReadOnlySeString nameContent = data.NameRaw is not null ? new ReadOnlySeString(data.NameRaw) : data.Name;
        _name.String = nameContent;
        _name.TextColor = RarityColor(data.Rarity);
        _name.Size = new Vector2(width, NameFontSize + 8f);
        _name.Position = new Vector2(0f, 0f);
        // Shrink the font (down to the floor) if the name would overflow the tooltip's inner width, so long
        // names stay on one line rather than clipping. The divider/body below anchor off NameFontSize (the box
        // height is unchanged), so the layout doesn't shift when the name shrinks.
        TooltipText.FitFontSize(_name, nameContent, NameFontSize, MinNameFontSize, width - HPad * 2f);

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

        // Category / big primary stat / descriptor (right of the icon).
        var statX = IconX + IconBox + 14f;
        SetText(_category, data.Category, statX, bodyTop - 1f);
        SetText(_primary, data.PrimaryValue, statX, bodyTop + 12f);
        SetText(_descriptor, data.PrimaryLabel, statX, bodyTop + 33f);

        // Item / Required Level share this tight stacked formatting (label, thin divider, value).
        const float headerToDivider = 16f;   // label top → divider (trimmed from 20)
        const float dividerHeight = 3f;       // divider thickness (trimmed from 5)
        const float dividerToValue = 2f;      // divider → value (trimmed by the same amount as the header gap)

        // Item Level (right column): label, divider the width of the label text, value — all right-aligned.
        var rightEdge = width - HPad;
        var ilvlX = rightEdge - IlvlTextWidth;
        var ilvlLabelY = bodyTop - 1f;  // top-aligned with the category line
        _ilvlLabel.String = "Item Level";
        _ilvlLabel.Size = new Vector2(IlvlTextWidth, LabelFontSize + 6f);
        _ilvlLabel.Position = new Vector2(ilvlX, ilvlLabelY);
        _ilvlDivider.Size = new Vector2(IlvlTextWidth, dividerHeight);
        _ilvlDivider.Position = new Vector2(ilvlX, ilvlLabelY + headerToDivider);
        var ilvlValueY = ilvlLabelY + headerToDivider + dividerToValue;
        _ilvlValue.String = data.ItemLevel > 0 ? data.ItemLevel.ToString() : "—";
        _ilvlValue.Size = new Vector2(IlvlTextWidth, ItemLevelFontSize + 6f);
        _ilvlValue.Position = new Vector2(ilvlX, ilvlValueY);

        // Req Level (right column, directly below Item Level): same label/divider/value formatting. The 8px
        // inter-block gap (down from 12) nudges this section up an extra 4px → 6px above its prior spot.
        var reqLabelY = ilvlValueY + ItemLevelFontSize + 8f;
        var reqX = rightEdge - ReqTextWidth;
        _reqLabel.String = "Req Level";
        _reqLabel.Size = new Vector2(ReqTextWidth, LabelFontSize + 6f);
        _reqLabel.Position = new Vector2(reqX, reqLabelY);
        _reqDivider.Size = new Vector2(ReqTextWidth, dividerHeight);
        _reqDivider.Position = new Vector2(reqX, reqLabelY + headerToDivider);
        var reqValueY = reqLabelY + headerToDivider + dividerToValue;
        _req.String = data.RequiredLevel > 0 ? data.RequiredLevel.ToString() : "—";
        _req.TextColor = data.MeetsLevelRequirement ? CreamColor : RequirementUnmetColor;
        _req.Size = new Vector2(ReqTextWidth, ItemLevelFontSize + 6f);
        _req.Position = new Vector2(reqX, reqValueY);

        // The bottom row sits below the taller of the icon and the (Item + Required Level) right column.
        var iconBottom = iconY + IconBox;
        var rightColumnBottom = reqValueY + ItemLevelFontSize + 6f;
        var contentBottom = Math.Max(iconBottom, rightColumnBottom);

        // Bottom row: equippable-job icons (full-width on the left, wrapping), or class/job text. Lifted up
        // (negative offset) to sit beside the lower right column — they share no horizontal space with it.
        var bottomY = contentBottom + JobRowOffsetY;
        var jobRowBottom = LayoutJobs(data, width, bottomY);

        var bottomDividerY = jobRowBottom + 8f;
        _bottomDivider.Size = new Vector2(width - HPad * 2f, 4f);
        _bottomDivider.Position = new Vector2(HPad, bottomDividerY);

        return bottomDividerY + 8f;
    }

    /// <summary>The job-icon row (full-width, left-aligned, wrapping when it overflows; a green outline marks
    /// the current job), or the class/job text fallback for a broad category (greened when the current job can
    /// equip it). Returns the row's bottom Y.</summary>
    private float LayoutJobs(UnifiedHeaderData data, float width, float bottomY)
    {
        if (data.JobIconIds.Count == 0)
        {
            HideJobBorder();
            foreach (var icon in _jobIcons) icon.IsVisible = false;

            _jobText.IsVisible = true;
            _jobText.String = data.ClassJobText;
            _jobText.TextColor = data.CurrentJobEquippable ? CurrentJobTextColor : MidGreyColor;
            _jobText.Position = new Vector2(HPad, bottomY);
            return bottomY + JobIconSize;
        }

        _jobText.IsVisible = false;

        var rightLimit = width - HPad;
        var jobX = HPad;
        var jobY = bottomY;
        var borderPlaced = false;
        for (var i = 0; i < data.JobIconIds.Count; i++)
        {
            // Wrap to a new row once the next icon would overflow the right edge.
            if (jobX > HPad && jobX + JobIconSize > rightLimit)
            {
                jobX = HPad;
                jobY += JobIconSize + JobIconGap;
            }

            var iconId = data.JobIconIds[i];
            var icon = GetOrCreateJobIcon(i);
            icon.IconId = iconId;
            icon.Size = new Vector2(JobIconSize, JobIconSize);
            icon.Position = new Vector2(jobX, jobY);
            icon.IsVisible = true;

            if (!borderPlaced && data.CurrentJobIconId != 0 && iconId == data.CurrentJobIconId)
            {
                PlaceJobBorder(jobX, jobY);
                borderPlaced = true;
            }

            jobX += JobIconSize + JobIconGap;
        }

        if (!borderPlaced) HideJobBorder();
        for (var i = data.JobIconIds.Count; i < _jobIcons.Count; i++)
            _jobIcons[i].IsVisible = false;

        return jobY + JobIconSize;
    }

    /// <summary>Position the four green frame bars around the job icon at (<paramref name="iconX" />,
    /// <paramref name="iconY" />) and show them. The bars straddle the icon edge (<see cref="BorderOutset" />
    /// outside) so the frame hugs the icon.</summary>
    private void PlaceJobBorder(float iconX, float iconY)
    {
        var ox = iconX - BorderOutset;
        var oy = iconY - BorderOutset;
        var span = JobIconSize + BorderOutset * 2f;

        SetBar(_jobBorderBars[0], ox, oy, span, BorderThickness);                          // top
        SetBar(_jobBorderBars[1], ox, oy + span - BorderThickness, span, BorderThickness); // bottom
        SetBar(_jobBorderBars[2], ox, oy, BorderThickness, span);                          // left
        SetBar(_jobBorderBars[3], ox + span - BorderThickness, oy, BorderThickness, span); // right
    }

    private static void SetBar(NodeBase bar, float x, float y, float w, float h)
    {
        bar.Size = new Vector2(w, h);
        bar.Position = new Vector2(x, y);
        bar.IsVisible = true;
    }

    private void HideJobBorder()
    {
        foreach (var bar in _jobBorderBars) bar.IsVisible = false;
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
        // The pool (built in Build, before the border bars) normally covers every index. A lazily-created
        // extra would attach after the bars and sit above them, but ResolveJobIcons caps the count at the pool
        // size, so this fallback is only a safety net and never runs in practice.
        if (index < _jobIcons.Count) return _jobIcons[index];

        var icon = MakeJobIcon();
        _jobIcons.Add(icon);
        return icon;
    }

    private IconImageNode MakeJobIcon()
    {
        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_parent!);
        return icon;
    }

    private static void SetText(TextNode node, string text, float x, float y)
    {
        node.String = text;
        node.Position = new Vector2(x, y);
    }
}
