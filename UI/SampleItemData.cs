using BetterTips.Tooltips;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BetterTips.UI;

/// <summary>
///     A representative item reconstructed from Lumina game data, used to fill the native editor's mock
///     tooltip with realistic content so it reads like a genuine tooltip. We pick one mid/high-level gear
///     piece (has stats, attribute bonuses, materia slots) and turn its sheet fields into per-section body
///     rows. Each row is a <c>(Label, Value)</c> pair matching the game's two-column body layout (tan label
///     on the left, white value on the right); a blank label renders the value full-width (e.g. flavor
///     text). Per-instance state the master sheet can't provide — melded materia, durability, spiritbond,
///     live market price — is shown as representative placeholders.
/// </summary>
public sealed class SampleItemData
{
    /// <summary>A single body line: a left-hand label (tan) and a right-hand value (white). A blank
    /// <see cref="Label" /> renders the value across the full width.</summary>
    public readonly record struct Row(string Label, string Value);

    private static readonly Row[] NoRows = [];

    /// <summary>Representative flavor text shown when the chosen item has no real description.</summary>
    private static readonly Row[] MockFlavorText =
    [
        new Row("", "A weathered blade said to have seen a hundred"),
        new Row("", "duels in the Coliseum of Ul'dah.")
    ];

    private readonly Dictionary<LayoutSection, Row[]> _body;

    private SampleItemData(string name, string category, uint itemLevel, ushort icon,
        Row primaryStat, uint requiredLevel, byte rarity, Dictionary<LayoutSection, Row[]> body,
        UnifiedBonusesData unifiedBonuses, ConditionData conditionSample, OwnershipData ownershipSample)
    {
        Name = name;
        Category = category;
        ItemLevel = itemLevel;
        Icon = icon;
        PrimaryStat = primaryStat;
        RequiredLevel = requiredLevel;
        Rarity = rarity;
        _body = body;
        UnifiedBonuses = unifiedBonuses;
        ConditionSample = conditionSample;
        OwnershipSample = ownershipSample;
    }

    private SampleItemData()
    {
        Name = "Sample Item";
        Category = "Arm Armor";
        ItemLevel = 0;
        Icon = 0;
        PrimaryStat = new Row("Physical Attack", "0");
        RequiredLevel = 0;
        Rarity = 2;
        _body = new Dictionary<LayoutSection, Row[]>();
        UnifiedBonuses = new UnifiedBonusesData([], []);
        ConditionSample = new ConditionData("100%", "0%", "12,345");
        OwnershipSample = SampleOwnership;
    }

    /// <summary>Representative ownership rows for the editor preview (the live values are per-character).</summary>
    private static readonly OwnershipData SampleOwnership = new(
    [
        new OwnershipEntry(OwnershipData.InventoryIcon, "Inventory: 2", OwnershipData.InventoryColor),
        new OwnershipEntry(OwnershipData.RetainerIcon, "Celestianna: 3", OwnershipData.RetainerColors[0]),
        new OwnershipEntry(OwnershipData.GlamourDresserIcon, "Glamour Dresser", OwnershipData.DresserColor),
        new OwnershipEntry(OwnershipData.CollectibleIcon, "Card:", OwnershipData.CollectibleColor,
            OwnershipData.CollectedIcon)
    ]);

    /// <summary>The item's display name (header line).</summary>
    public string Name { get; }

    /// <summary>The item's icon id (for the header), or 0 if unknown.</summary>
    public ushort Icon { get; }

    /// <summary>The item-category line (e.g. "Arm Armor"), shown under the name when not hidden.</summary>
    public string Category { get; }

    /// <summary>The item level (for the header), or 0 if unknown.</summary>
    public uint ItemLevel { get; }

    /// <summary>
    ///     The primary stat shown large in the unified header — a <c>(descriptor, value)</c> pair. Physical
    ///     Attack / Magic Damage for weapons (whichever damage is higher), or the higher of Defense / Magic
    ///     Defense for armor.
    /// </summary>
    public Row PrimaryStat { get; }

    /// <summary>The required equip level (<c>LevelEquip</c>), shown as "REQ LV n" in the unified header.</summary>
    public uint RequiredLevel { get; }

    /// <summary>The item rarity (1 normal … 7 aetherial) — drives the unified header's name color.</summary>
    public byte Rarity { get; }

    /// <summary>The sample data for the "Unified bonuses &amp; materia" enhancement's section — the attribute
    /// bonuses (colored by group) plus a representative set of melded materia. Built from Lumina so the preview
    /// renders the same design the live block will.</summary>
    public UnifiedBonusesData UnifiedBonuses { get; }

    /// <summary>The sample data for the "Condition" section — representative durability/spiritbond plus the
    /// item's real sell price (the live values are per-instance; the preview just needs realistic content).</summary>
    public ConditionData ConditionSample { get; }

    /// <summary>The sample data for the "Ownership" section — representative owned/dresser/collectible rows (the
    /// live values are per-character; the preview just needs realistic content).</summary>
    public OwnershipData OwnershipSample { get; }

    /// <summary>The reconstructed body rows for a section (empty → the card shows just its header).</summary>
    public Row[] BodyRows(LayoutSection section)
        => _body.TryGetValue(section, out var rows) ? rows : NoRows;

    /// <summary>
    ///     Build the sample from the Lumina <see cref="Item" /> sheet. Best-effort and crash-safe — any
    ///     failure falls back to a labels-only sample so the editor still opens.
    /// </summary>
    public static SampleItemData Build(IDataManager data, IPluginLog log)
    {
        try
        {
            var sheet = data.GetExcelSheet<Item>();

            // Prefer a realistic geared item: equippable, has defense or damage, materia slots, a class/job.
            Item? chosen = null;
            foreach (var item in sheet)
            {
                if (item.RowId == 0) continue;
                if (item.LevelEquip < 50) continue;
                if (item.MateriaSlotCount < 2) continue;
                if (item.DefensePhys == 0 && item.DamagePhys == 0) continue;
                if (item.ClassJobCategory.RowId == 0) continue;
                if (string.IsNullOrWhiteSpace(item.Name.ToString())) continue;

                chosen = item;
                break;
            }

            // Fallback: any item with a name and either damage or defense.
            if (chosen is null)
                foreach (var item in sheet)
                {
                    if (item.RowId == 0) continue;
                    if (item.DamagePhys == 0 && item.DefensePhys == 0) continue;
                    if (string.IsNullOrWhiteSpace(item.Name.ToString())) continue;

                    chosen = item;
                    break;
                }

            if (chosen is null) return new SampleItemData();

            return FromItem(chosen.Value, data);
        }
        catch (Exception ex)
        {
            log.Error(ex, "BetterTips: failed to build sample item; using a labels-only mock.");
            return new SampleItemData();
        }
    }

    private static SampleItemData FromItem(Item item, IDataManager data)
    {
        var body = new Dictionary<LayoutSection, Row[]>();
        var isWeapon = item.DamagePhys > 0;

        body[LayoutSection.DamageDefense] = isWeapon
            ? [new Row("Physical Damage", item.DamagePhys.ToString()), new Row("Magic Damage", item.DamageMag.ToString())]
            : [new Row("Defense", item.DefensePhys.ToString()), new Row("Magic Defense", item.DefenseMag.ToString())];

        body[LayoutSection.ItemLevelClassJob] =
        [
            new Row("Item Level", item.LevelItem.RowId.ToString()),
            new Row("", item.ClassJobCategory.Value.Name.ToString()),
            new Row("", $"Lv. {item.LevelEquip}")
        ];

        var attrs = new List<Row>();
        for (var i = 0; i < item.BaseParam.Count; i++)
        {
            var param = item.BaseParam[i];
            var value = item.BaseParamValue[i];
            if (param.RowId == 0 || value == 0) continue;
            attrs.Add(new Row(param.Value.Name.ToString(), $"+{value}"));
        }

        if (attrs.Count > 0)
            body[LayoutSection.AttributeBonuses] = attrs.ToArray();

        var description = item.Description.ToString();
        body[LayoutSection.Description] = string.IsNullOrWhiteSpace(description)
            ? MockFlavorText
            : description.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new Row("", line)).ToArray();

        body[LayoutSection.Materia] = BuildMateria(item.MateriaSlotCount);

        // Effects are rare on gear; a representative placeholder.
        body[LayoutSection.Effects] = [new Row("", "(item effects appear here)")];

        // Crafting & Repairs are per-instance; representative placeholders matching the game's row labels.
        body[LayoutSection.CraftingRepairs] =
        [
            new Row("Condition", "100%"),
            new Row("Spiritbond", "0%"),
            new Row("Repair Level", $"Lv. {Math.Max(1, item.LevelEquip - 10)}"),
            new Row("Materials", "Dark Matter"),
            new Row("Quick Repairs", "0")
        ];

        body[LayoutSection.VendorMarket] = item.PriceLow > 0
            ? [new Row("", $"Sells for {item.PriceLow:N0} gil")]
            : [new Row("", "Cannot be sold")];

        // The crafter-signature line (only on signed/HQ player-crafted items); a representative name in the mock.
        body[LayoutSection.CrafterSignature] = [new Row("", "Iam Banana")];

        // The real Gear Sets row renders job icons; in the mock it's a representative note.
        body[LayoutSection.GearSets] = [new Row("", "(job icons for gear sets with this item)")];

        // The real Glamour section shows the appearance name + dye swatches (live only); the mock lists them
        // as text rows so the card has representative content to reorder.
        body[LayoutSection.Glamour] =
        [
            new Row("", "Peacelover's Hat"),
            new Row("Dye 1", "Coral Pink"),
            new Row("Dye 2", "Ink Blue")
        ];

        // The unified header's big primary stat: weapons show the higher of Physical/Magic damage with its
        // descriptor; armor shows the higher of Physical/Magic defense.
        Row primaryStat;
        if (item.DamagePhys > 0 || item.DamageMag > 0)
            primaryStat = item.DamageMag > item.DamagePhys
                ? new Row("Magic Damage", item.DamageMag.ToString())
                : new Row("Physical Attack", item.DamagePhys.ToString());
        else
            primaryStat = item.DefenseMag > item.DefensePhys
                ? new Row("Magic Defense", item.DefenseMag.ToString())
                : new Row("Defense", item.DefensePhys.ToString());

        // The Condition section: representative durability/spiritbond (per-instance live) + the item's real
        // sell price.
        var condition = new ConditionData("100%", "0%",
            item.PriceLow > 0 ? item.PriceLow.ToString("N0") : null);

        return new SampleItemData(
            item.Name.ToString(),
            item.ItemUICategory.Value.Name.ToString(),
            item.LevelItem.RowId,
            item.Icon,
            primaryStat,
            item.LevelEquip,
            item.Rarity,
            body,
            BuildUnifiedBonuses(item, data),
            condition,
            SampleOwnership);
    }

    /// <summary>
    ///     The sample data for the "Unified bonuses &amp; materia" section: the item's attribute bonuses
    ///     (classified into the green / pink / gold grouping and sorted that way), plus a representative set of
    ///     melded materia pulled from the Lumina <see cref="Materia" /> sheet (icon + stat boost). Live, this
    ///     materia is scraped per-instance; the preview just needs something realistic to render.
    /// </summary>
    private static UnifiedBonusesData BuildUnifiedBonuses(Item item, IDataManager data)
    {
        var bonuses = new List<BonusEntry>();
        for (var i = 0; i < item.BaseParam.Count; i++)
        {
            var param = item.BaseParam[i];
            var value = item.BaseParamValue[i];
            if (param.RowId == 0 || value == 0) continue;

            var name = param.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            bonuses.Add(new BonusEntry(name, $"+{value}", UnifiedBonusesData.Classify(name)));
        }

        bonuses = bonuses.OrderBy(b => (int)b.Color).ToList();
        var materia = BuildSampleMateria(data);
        return new UnifiedBonusesData(bonuses, materia);
    }

    /// <summary>A few representative melded materia from the Lumina <see cref="Materia" /> sheet (highest grade
    /// of each) — icon + stat boost, colored by group — plus a couple of empty sockets so the preview shows the
    /// empty-slot rendering. Guarded; an empty list just renders bonuses only.</summary>
    private static List<MateriaEntry> BuildSampleMateria(IDataManager data)
    {
        var result = new List<MateriaEntry>();
        try
        {
            var sheet = data.GetExcelSheet<Materia>();
            foreach (var materia in sheet)
            {
                if (result.Count >= 3) break;
                if (materia.BaseParam.RowId == 0) continue;

                var name = materia.BaseParam.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Use the highest grade that has a non-zero value and a real materia item (for its icon).
                for (var grade = materia.Value.Count - 1; grade >= 0; grade--)
                {
                    var value = materia.Value[grade];
                    if (value <= 0) continue;
                    var matItem = materia.Item[grade];
                    if (matItem.RowId == 0) continue;

                    result.Add(new MateriaEntry(matItem.Value.Icon, name, $"+{value}", UnifiedBonusesData.Classify(name)));
                    break;
                }
            }
        }
        catch
        {
            // Best-effort sample data — leave the materia empty if the sheet can't be read.
        }

        // Filled slots grouped green → pink → gold, then a couple of empty sockets to show that rendering.
        var ordered = result.OrderBy(m => (int)m.Color).ToList();
        ordered.Add(new MateriaEntry(0, string.Empty, string.Empty, BonusColor.Bonus, IsEmpty: true));
        ordered.Add(new MateriaEntry(0, string.Empty, string.Empty, BonusColor.Bonus, IsEmpty: true));
        return ordered;
    }

    private static SampleItemData.Row[] BuildMateria(int slots)
    {
        if (slots <= 0) return [new Row("", "No materia slots")];

        var rows = new Row[slots];
        for (var i = 0; i < slots; i++)
            rows[i] = new Row($"Materia Slot {i + 1}", "(empty)");
        return rows;
    }
}
