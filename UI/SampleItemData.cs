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
        Row primaryStat, uint requiredLevel, byte rarity, Dictionary<LayoutSection, Row[]> body)
    {
        Name = name;
        Category = category;
        ItemLevel = itemLevel;
        Icon = icon;
        PrimaryStat = primaryStat;
        RequiredLevel = requiredLevel;
        Rarity = rarity;
        _body = body;
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
    }

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

            return FromItem(chosen.Value);
        }
        catch (Exception ex)
        {
            log.Error(ex, "BetterTips: failed to build sample item; using a labels-only mock.");
            return new SampleItemData();
        }
    }

    private static SampleItemData FromItem(Item item)
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

        // The real Gear Sets row renders job icons; in the mock it's a representative note.
        body[LayoutSection.GearSets] = [new Row("", "(job icons for gear sets with this item)")];

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

        return new SampleItemData(
            item.Name.ToString(),
            item.ItemUICategory.Value.Name.ToString(),
            item.LevelItem.RowId,
            item.Icon,
            primaryStat,
            item.LevelEquip,
            item.Rarity,
            body);
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
