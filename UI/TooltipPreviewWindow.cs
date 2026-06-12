using BetterTips.Tooltips;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;

namespace BetterTips.UI;

/// <summary>
///     The standalone mock tooltip: its own native window (separate from the control/catalog window) whose
///     content is rendered with the game's own node types so it reads like a real <c>ItemDetail</c> tooltip
///     — the real <c>WindowF_Bg</c> backdrop, game-styled section cards (<see cref="TooltipContentBlock" />),
///     and two-column body rows filled from a representative item (<see cref="SampleItemData" />). Sections
///     are <b>dragged on the Y axis</b> to reorder; the others reflow to open a gap so nothing overlaps, and
///     the dragged card snaps into its slot on release. Reorders write
///     <see cref="Configuration.Configuration.SectionOrder" /> and fire <c>onChanged</c> (save + relayout
///     rebuild). The control window calls <see cref="Refresh" /> when an add/remove toggle changes config.
///     <para>
///         Crash-safety mirrors the gear-set block: nodes are built in <see cref="OnSetup" />, owned by the
///         addon (freed on finalize); we only null our references in <see cref="OnFinalize" /> after
///         disabling each card's edit mode. Every native access is guarded.
///     </para>
/// </summary>
public sealed unsafe class TooltipPreviewWindow : NativeAddon
{
    private const float PanelPad = 12f;
    private const float HeaderTopPad = 12f;
    private const float NameHeight = 22f;
    private const float CategoryHeight = 18f;
    private const float HeaderGap = 8f;
    private const float CardGap = 4f;
    private const float ControlGap = 8f;
    private const float ControlHeight = 20f;
    private const float BodyLineHeight = 16f;
    private const float BodyBottomPad = 6f;
    private const int MaxBodyLines = 4;

    // Damage/Defense columns: a tan label over a large right-aligned value with an underline bar.
    private const uint BigFontSize = 22;
    private const float ColumnWidth = 116f;
    private const float ColumnGap = 6f;
    private const float ColumnLabelHeight = 18f;
    private const float ColumnUnderlineY = 38f;     // underline tucked right under the number
    private const float ColumnUnderlineHeight = 10f; // a touch thicker than before
    private const float ColumnBodyHeight = 50f;

    // Item Level banner: an uppercase line on a dark full-width bar (the game's ItemDetail texture, darkened).
    private const float ItemLevelBarHeight = 18f;
    private static readonly Vector3 ItemLevelBarMultiply = new(28f, 28f, 34f);

    // The ItemDetail UI atlas; part 0 (112×12) is the horizontal bar used for the damage underline and the
    // item-level banner.
    private const string ItemDetailTexture = "ui/uld/ItemDetail.tex";

    // The small item-condition gear sprite shown beside the "Crafting & Repairs" header in-game.
    private const uint ConditionIconId = 62111;
    private const float ConditionIconSize = 20f;

    // Gear Sets: a representative row of job icons (62100 + ClassJob), like the real gear-set block.
    private const uint JobIconBase = 62100;
    private const float JobIconSize = 20f;
    private const float JobIconGap = 4f;
    private static readonly int[] SampleJobs = [19, 21, 32, 24, 28]; // PLD, WAR, DRK, WHM, SCH

    private const float IconSize = 40f;
    private const float HeaderTextInset = PanelPad + IconSize + 8f; // name/category sit right of the icon

    private const uint NameFontSize = 14;
    private const uint BodyFontSize = 12;

    // The body's value column offset (relative to the card's body inset) — matches the game's label/value split.
    private const float ValueColumn = 126f;

    // Body colors read from a live ItemDetail dump: tan labels (#C3BCA5), white values, light-grey item-level
    // lines (#E0E0E0), green class/job names (#8CFF5A), black outline.
    private static readonly Vector4 LabelColor = new(0xC3 / 255f, 0xBC / 255f, 0xA5 / 255f, 1f);
    private static readonly Vector4 ValueColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 LightGreyColor = new(0xE0 / 255f, 0xE0 / 255f, 0xE0 / 255f, 1f);
    private static readonly Vector4 GreenColor = new(0x8C / 255f, 0xFF / 255f, 0x5A / 255f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private readonly Configuration.Configuration _config;
    private readonly SampleItemData _sample;
    private readonly Action _onChanged;
    private readonly IPluginLog _log;

    private ResNode? _previewRoot;
    private IconImageNode? _iconNode;
    private TextNode? _nameNode;
    private TextNode? _categoryNode;
    private TextNode? _controlHints;
    private readonly Dictionary<LayoutSection, TooltipContentBlock> _cards = new();

    // The current absolute slot Y of each visible card. A card whose live Y has drifted from its slot is the
    // one the user is dragging — see ReflowDuringDrag.
    private readonly Dictionary<LayoutSection, float> _slotY = new();

    public TooltipPreviewWindow(Configuration.Configuration config, SampleItemData sample, Action onChanged,
        IPluginLog log)
    {
        _config = config;
        _sample = sample;
        _onChanged = onChanged;
        _log = log;
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        try
        {
            _cards.Clear();
            BuildPreview(ContentStartPosition, ContentSize);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: failed to build the tooltip preview window.");
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        foreach (var card in _cards.Values)
            card.EnableMoving = false;

        _cards.Clear();
        _slotY.Clear();
        _previewRoot = null;
        _iconNode = null;
        _nameNode = null;
        _categoryNode = null;
        _controlHints = null;
    }

    protected override void OnDraw(AtkUnitBase* addon)
    {
        try
        {
            ReflowDuringDrag();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error reflowing the tooltip preview.");
        }
    }

    /// <summary>Re-stack the preview from current config. Called by the control window after an add/remove
    /// toggle; a no-op when the window is closed.</summary>
    public void Refresh()
    {
        if (_previewRoot is null) return;
        try
        {
            RefreshPreview();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error refreshing the tooltip preview.");
        }
    }

    private void BuildPreview(Vector2 position, Vector2 size)
    {
        _previewRoot = new ResNode
        {
            Position = position,
            Size = size
        };
        _previewRoot.AttachNode(this);

        // No custom backdrop — the standard KTK window background shows through; we just lay the real
        // tooltip sections on top of it.
        if (_sample.Icon != 0)
        {
            _iconNode = new IconImageNode
            {
                FitTexture = true,
                IconId = _sample.Icon,
                Size = new Vector2(IconSize, IconSize),
                Position = new Vector2(PanelPad, HeaderTopPad)
            };
            _iconNode.AttachNode(_previewRoot);
        }

        _nameNode = new TextNode
        {
            String = _sample.Name,
            FontType = FontType.Axis,
            FontSize = NameFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ColorHelper.GetColor(8),
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize
        };
        _nameNode.AttachNode(_previewRoot);

        _categoryNode = new TextNode
        {
            String = _sample.Category,
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ColorHelper.GetColor(3),
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize
        };
        _categoryNode.AttachNode(_previewRoot);

        var cardWidth = size.X - PanelPad * 2f;
        foreach (var info in TooltipLayout.Sections)
        {
            var section = info.Id;
            var card = BuildCard(section, cardWidth);
            card.EnableMoving = true;
            card.OnMoveComplete = node => OnCardDropped(section, node);
            card.AttachNode(_previewRoot);
            _cards[section] = card;
        }

        _controlHints = new TextNode
        {
            String = "Equip      Cast      Discard",
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.Center,
            TextColor = ColorHelper.GetColor(3),
            TextOutlineColor = OutlineColor,
            Size = new Vector2(cardWidth, ControlHeight)
        };
        _controlHints.AttachNode(_previewRoot);
    }

    /// <summary>Sections the game gives a grey title + divider to. The rest (damage/defense, item-level,
    /// description, price) are headerless — just their content.</summary>
    private static bool IsHeaded(LayoutSection section) => section
        is LayoutSection.AttributeBonuses
        or LayoutSection.Materia
        or LayoutSection.CraftingRepairs
        or LayoutSection.Requirements
        or LayoutSection.Effects
        or LayoutSection.GearSets;

    private TooltipContentBlock BuildCard(LayoutSection section, float width)
    {
        var card = new TooltipContentBlock();
        if (IsHeaded(section))
            card.HeaderText = TooltipLayout.Find(section)?.Label ?? section.ToString();
        else
            card.SetHeaderless();

        var bodyHeight = section switch
        {
            LayoutSection.DamageDefense => BuildColumns(card, width),
            LayoutSection.ItemLevelClassJob => BuildItemLevel(card, width),
            LayoutSection.AttributeBonuses => BuildBonuses(card, width),
            LayoutSection.CraftingRepairs => BuildCrafting(card),
            LayoutSection.GearSets => BuildGearSets(card),
            _ => BuildRows(card, section)
        };

        if (bodyHeight <= 0f)
            bodyHeight = BodyLineHeight * 0.5f;

        card.Resize(width, bodyHeight + BodyBottomPad);
        return card;
    }

    /// <summary>The generic label/value body (Materia, Effects, Requirements, Description, Vendor/Market,
    /// Gear Sets): a tan label on the left, white value in the value column; a blank label spans full width.</summary>
    private float BuildRows(TooltipContentBlock card, LayoutSection section)
    {
        var rows = _sample.BodyRows(section);
        var shown = Math.Min(rows.Length, MaxBodyLines);
        var y = card.BodyTop;

        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];
            var truncated = i == MaxBodyLines - 1 && rows.Length > MaxBodyLines;
            var value = truncated ? row.Value + "  …" : row.Value;

            if (!string.IsNullOrEmpty(row.Label))
                AddBodyText(card, row.Label, LabelColor, TooltipContentBlock.BodyInsetX, y);

            var valueX = string.IsNullOrEmpty(row.Label)
                ? TooltipContentBlock.BodyInsetX
                : TooltipContentBlock.BodyInsetX + ValueColumn;
            AddBodyText(card, value, ValueColor, valueX, y);

            y += BodyLineHeight;
        }

        return shown * BodyLineHeight;
    }

    /// <summary>Damage / Defense: right-aligned columns of a tan label over a large value, each on an
    /// underline bar tucked beneath the number (the game's stat block, top-right of the tooltip).</summary>
    private float BuildColumns(TooltipContentBlock card, float width)
    {
        var rows = _sample.BodyRows(LayoutSection.DamageDefense);
        if (rows.Length == 0) return 0f;

        // Right-align the whole column group against the card's right edge.
        var totalWidth = rows.Length * ColumnWidth + (rows.Length - 1) * ColumnGap;
        var x = width - PanelPad - totalWidth;
        var top = card.BodyTop;

        foreach (var row in rows)
        {
            AddBodyTextRight(card, row.Label, LabelColor, x, top, ColumnWidth, BodyFontSize);
            AddBodyTextRight(card, row.Value, ValueColor, x, top + ColumnLabelHeight, ColumnWidth, BigFontSize);
            MakeBar(card, x, top + ColumnUnderlineY, ColumnWidth, ColumnUnderlineHeight);
            x += ColumnWidth + ColumnGap;
        }

        return ColumnBodyHeight;
    }

    /// <summary>Item Level / Class / Job: an uppercase item-level line on a dark full-width bar, then the
    /// green class names and grey required level (plain lines).</summary>
    private float BuildItemLevel(TooltipContentBlock card, float width)
    {
        var rows = _sample.BodyRows(LayoutSection.ItemLevelClassJob);
        var shown = Math.Min(rows.Length, MaxBodyLines);
        var y = card.BodyTop;

        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];

            if (i == 0)
            {
                // Item level: an uppercase line over a darkened full-width ItemDetail banner.
                MakeBar(card, 3f, y, width - 6f, ItemLevelBarHeight, ItemLevelBarMultiply);
                var text = $"{row.Label} {row.Value}".ToUpperInvariant();
                AddBodyText(card, text, LightGreyColor, TooltipContentBlock.BodyInsetX, y + 2f);
                y += ItemLevelBarHeight + 2f;
            }
            else
            {
                var text = string.IsNullOrEmpty(row.Label) ? row.Value : $"{row.Label} {row.Value}";
                var color = i == 1 ? GreenColor : LightGreyColor; // the class/job line is rendered green in-game
                AddBodyText(card, text, color, TooltipContentBlock.BodyInsetX, y);
                y += BodyLineHeight;
            }
        }

        return y - card.BodyTop;
    }

    /// <summary>Bonuses: two attributes per row as white "Stat +N" cells.</summary>
    private float BuildBonuses(TooltipContentBlock card, float width)
    {
        var rows = _sample.BodyRows(LayoutSection.AttributeBonuses);
        var rightColumnX = TooltipContentBlock.BodyInsetX + (width - TooltipContentBlock.BodyInsetX - PanelPad) / 2f;
        var y = card.BodyTop;
        var height = 0f;

        for (var i = 0; i < rows.Length && height < MaxBodyLines * BodyLineHeight; i += 2)
        {
            AddBodyText(card, $"{rows[i].Label}  {rows[i].Value}", ValueColor, TooltipContentBlock.BodyInsetX, y);
            if (i + 1 < rows.Length)
                AddBodyText(card, $"{rows[i + 1].Label}  {rows[i + 1].Value}", ValueColor, rightColumnX, y);

            y += BodyLineHeight;
            height += BodyLineHeight;
        }

        return height;
    }

    /// <summary>Gear Sets: a representative row of job icons (like the real gear-set block we add in-game).</summary>
    private float BuildGearSets(TooltipContentBlock card)
    {
        var x = TooltipContentBlock.BodyInsetX;
        var y = card.BodyTop;

        foreach (var job in SampleJobs)
        {
            var icon = new IconImageNode
            {
                FitTexture = true,
                IconId = JobIconBase + (uint)job,
                Size = new Vector2(JobIconSize, JobIconSize),
                Position = new Vector2(x, y)
            };
            icon.AttachNode(card);
            x += JobIconSize + JobIconGap;
        }

        return JobIconSize;
    }

    /// <summary>Crafting & Repairs: the small condition gear icon at the left, then the tan/white rows.</summary>
    private float BuildCrafting(TooltipContentBlock card)
    {
        var icon = new IconImageNode
        {
            FitTexture = true,
            IconId = ConditionIconId,
            Size = new Vector2(ConditionIconSize, ConditionIconSize),
            Position = new Vector2(2f, card.BodyTop)
        };
        icon.AttachNode(card);

        return BuildRows(card, LayoutSection.CraftingRepairs);
    }

    private static void AddBodyText(NodeBase parent, string text, Vector4 color, float x, float y,
        uint fontSize = BodyFontSize)
    {
        var node = new TextNode
        {
            String = text,
            FontType = FontType.Axis,
            FontSize = fontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = color,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
            Position = new Vector2(x, y)
        };
        node.AttachNode(parent);
    }

    /// <summary>A right-aligned text cell of fixed <paramref name="width" /> (for the damage/defense columns).</summary>
    private static void AddBodyTextRight(NodeBase parent, string text, Vector4 color, float x, float y,
        float width, uint fontSize)
    {
        var node = new TextNode
        {
            String = text,
            FontType = FontType.Axis,
            FontSize = fontSize,
            AlignmentType = AlignmentType.TopRight,
            TextColor = color,
            TextOutlineColor = OutlineColor,
            Size = new Vector2(width, fontSize + 6f),
            Position = new Vector2(x, y)
        };
        node.AttachNode(parent);
    }

    /// <summary>The game's horizontal ItemDetail bar (part 0), stretched to <paramref name="width" /> — used
    /// as the damage-column underline and the item-level banner background. Attached behind later content.</summary>
    private static void MakeBar(NodeBase parent, float x, float y, float width, float height,
        Vector3? multiply = null)
    {
        var bar = new SimpleImageNode
        {
            TexturePath = ItemDetailTexture,
            TextureCoordinates = Vector2.Zero,
            TextureSize = new Vector2(112f, 12f),
            WrapMode = WrapMode.Stretch,
            Size = new Vector2(width, height),
            Position = new Vector2(x, y)
        };
        if (multiply is { } m)
            bar.MultiplyColor = m;
        bar.AttachNode(parent);
    }

    /// <summary>
    ///     Re-stack the visible cards in the user's order and toggle the header/control extras, recording
    ///     each card's slot Y. When <paramref name="floating" /> is set (a card being dragged), its slot is
    ///     reserved (a gap) but its position is left alone so it can keep following the cursor.
    /// </summary>
    private void RefreshPreview(LayoutSection? floating = null)
    {
        if (_previewRoot is null) return;

        // Header: icon top-left; the name is vertically centered against the icon to its right; the category
        // ("Gladiator's Arm") sits below the icon, left-aligned with it (no item level here).
        var hasIcon = _iconNode is not null;
        if (_nameNode is not null)
        {
            var nameX = hasIcon ? HeaderTextInset : PanelPad;
            var nameY = hasIcon ? HeaderTopPad + (IconSize - (float)NameFontSize - 4f) / 2f : HeaderTopPad;
            _nameNode.Position = new Vector2(nameX, nameY);
            _nameNode.IsVisible = true;
        }

        var iconBottom = HeaderTopPad + (hasIcon ? IconSize : NameHeight);
        var showCategory = !_config.HiddenSections.Contains(TooltipSection.ItemCategory);
        if (_categoryNode is not null)
        {
            _categoryNode.IsVisible = showCategory;
            if (showCategory)
                _categoryNode.Position = new Vector2(PanelPad, iconBottom + 2f);
        }

        var top = (showCategory ? iconBottom + 2f + CategoryHeight : iconBottom) + HeaderGap;

        var y = top;
        foreach (var section in _config.SectionOrder)
        {
            if (!_cards.TryGetValue(section, out var card)) continue;

            if (SectionVisibility.IsShown(_config, section))
            {
                card.IsVisible = true;
                _slotY[section] = y;
                if (section != floating)
                    card.Position = new Vector2(PanelPad, y);
                y += card.Height + CardGap;
            }
            else
            {
                card.IsVisible = false;
                _slotY.Remove(section);
            }
        }

        var showControl = !_config.HiddenSections.Contains(TooltipSection.ControlHints);
        if (_controlHints is not null)
        {
            _controlHints.IsVisible = showControl;
            if (showControl)
                _controlHints.Position = new Vector2(PanelPad, y + ControlGap);
        }
    }

    /// <summary>
    ///     If a card has been dragged off its slot, recompute where it should insert among the others and
    ///     reflow them to open a gap there (so nothing overlaps). The dragged card is left floating under the
    ///     cursor until release.
    /// </summary>
    private void ReflowDuringDrag()
    {
        if (_previewRoot is null || _slotY.Count == 0) return;

        LayoutSection? dragging = null;
        foreach (var (section, card) in _cards)
        {
            if (!card.IsVisible || !_slotY.TryGetValue(section, out var slot)) continue;
            if (Math.Abs(card.Y - slot) > 3f) { dragging = section; break; }
        }

        if (dragging is null) return;

        var draggedCard = _cards[dragging.Value];
        var center = draggedCard.Y + draggedCard.Height / 2f;

        var visible = VisibleOrder();
        var others = visible.Where(s => s != dragging.Value).ToList();
        var index = DropIndex(others, center);

        var newVisible = new List<LayoutSection>(others);
        newVisible.Insert(index, dragging.Value);
        if (newVisible.SequenceEqual(visible)) return;

        ReorderVisible(newVisible);
        RefreshPreview(dragging.Value);
    }

    /// <summary>
    ///     A card was released: snap it (and everyone) back into clean slots in the now-current order, then
    ///     persist once. The live reflow already maintained the order, so this just commits it.
    /// </summary>
    private void OnCardDropped(LayoutSection dragged, NodeBase node)
    {
        try
        {
            var center = node.Y + node.Height / 2f;
            var others = VisibleOrder().Where(s => s != dragged).ToList();
            var index = DropIndex(others, center);
            others.Insert(index, dragged);
            ReorderVisible(others);

            RefreshPreview();
            _onChanged();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error reordering a tooltip section.");
            RefreshPreview();
        }
    }

    private List<LayoutSection> VisibleOrder()
    {
        var visible = new List<LayoutSection>();
        foreach (var section in _config.SectionOrder)
            if (SectionVisibility.IsShown(_config, section))
                visible.Add(section);
        return visible;
    }

    private int DropIndex(List<LayoutSection> others, float center)
    {
        var index = 0;
        foreach (var section in others)
            if (_slotY.TryGetValue(section, out var slot) && _cards.TryGetValue(section, out var card) &&
                slot + card.Height / 2f < center)
                index++;
        return Math.Clamp(index, 0, others.Count);
    }

    /// <summary>Write <paramref name="newVisible" /> into the visible slots of SectionOrder, in order,
    /// leaving removed (hidden) blocks untouched in their positions.</summary>
    private void ReorderVisible(List<LayoutSection> newVisible)
    {
        var order = _config.SectionOrder;
        var queue = new Queue<LayoutSection>(newVisible);
        for (var i = 0; i < order.Count && queue.Count > 0; i++)
            if (SectionVisibility.IsShown(_config, order[i]))
                order[i] = queue.Dequeue();
    }
}
