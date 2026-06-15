using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>
///     A color group for a stat line in the unified bonuses/materia section. The user-chosen palette: the
///     physical attributes (Strength / Dexterity / Vitality) are green, the mental attributes (Intelligence /
///     Mind) are pink, and everything else (the secondary "bonuses": Critical Hit, Determination, Speed, …)
///     is gold. <see cref="UnifiedBonusesNodes" /> maps each to an actual color.
/// </summary>
public enum BonusColor
{
    /// <summary>Strength / Dexterity / Vitality — rendered green.</summary>
    Physical,

    /// <summary>Intelligence / Mind — rendered pink.</summary>
    Mental,

    /// <summary>Every other (secondary) bonus — rendered gold.</summary>
    Bonus
}

/// <summary>One attribute bonus line: a stat name, its value (e.g. <c>"+42"</c>), and its color group.</summary>
public sealed record BonusEntry(string Name, string Value, BonusColor Color);

/// <summary>One materia slot row. A <b>filled</b> slot carries the materia's item icon, the stat it boosts and
/// the value (e.g. <c>"Spell Speed"</c> / <c>"+24"</c>), and the color group of that stat — the materia's own
/// name is omitted, only the boosted stat is shown, in the same form as the bonuses. An <b>empty</b> slot
/// (<see cref="IsEmpty" />) carries none of that and is drawn as an empty socket, matching the native tooltip.</summary>
public sealed record MateriaEntry(uint IconId, string Name, string Value, BonusColor Color, bool IsEmpty = false);

/// <summary>
///     The data the unified bonuses/materia section renders. Attribute bonuses are static (from the Lumina
///     <see cref="Item" /> sheet), so they drive both the live tooltip and the editor preview. Melded materia
///     is <b>per-instance</b> — the master sheet can't know what's melded into the hovered piece — so the live
///     path <see cref="ScrapeMateria" /> reads it off the rendered native materia block (the project's
///     "Lumina for static data, scrape rendered nodes for per-instance data" rule); the preview supplies a
///     representative list instead.
/// </summary>
public sealed record UnifiedBonusesData(
    IReadOnlyList<BonusEntry> Bonuses,
    IReadOnlyList<MateriaEntry> Materia)
{
    // BaseParam row ids — green for the three physical attributes, pink for the two mental ones, gold for the
    // rest. These ids are long-stable in FFXIV (1 STR, 2 DEX, 3 VIT, 4 INT, 5 MND).
    private const uint Strength = 1, Dexterity = 2, Vitality = 3, Intelligence = 4, Mind = 5;

    // Stat-name → group fallback used for scraped materia, where we only have the rendered stat name (no id).
    // English-only, like the rest of the version-specific node/text scraping.
    private static readonly HashSet<string> PhysicalNames = new(StringComparer.OrdinalIgnoreCase)
        { "Strength", "Dexterity", "Vitality" };
    private static readonly HashSet<string> MentalNames = new(StringComparer.OrdinalIgnoreCase)
        { "Intelligence", "Mind" };

    // A rendered materia stat line: "<stat name> +<value>" (value may carry a % suffix, and the game appends a
    // trailing space). Tolerant of the odd extra whitespace.
    private static readonly Regex StatLine = new(@"^(?<name>.+?)\s*\+\s*(?<value>\d+%?)\s*$",
        RegexOptions.Compiled);

    /// <summary>The native Materia block node id (<c>#93</c>) — see <see cref="TooltipLayout" />.</summary>
    private const uint MateriaBlockId = 93;

    // Materia-item name → icon, built once from the Lumina Materia sheet. Sorted by name length (desc) so a
    // longest-match scan picks "… Materia VIII" over "… Materia VII" when reading the (payload-laced) rendered
    // materia name. Null until first built.
    private static (string Name, uint Icon)[]? _materiaIcons;

    /// <summary>Whether the section has anything to show (no point replacing the natives with an empty block).</summary>
    public bool HasContent => Bonuses.Count > 0 || Materia.Count > 0;

    /// <summary>
    ///     Build the data for the currently hovered item, or <c>null</c> when nothing equippable is hovered.
    ///     Attribute bonuses come from Lumina; melded materia is scraped from <paramref name="addon" />'s
    ///     native materia block (which must still be populated — call before the relayout hides it).
    /// </summary>
    public static unsafe UnifiedBonusesData? FromHoveredItem(IGameGui gameGui, IDataManager data, AddonItemDetail* addon)
    {
        var hovered = gameGui.HoveredItem;
        if (hovered == 0) return null;

        var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return null;

        // Gear only — consumables/materials keep their native sections.
        if (item.EquipSlotCategory.RowId == 0) return null;

        var bonuses = ReadBonuses(item);
        var materia = ScrapeMateria(addon, data);

        var result = new UnifiedBonusesData(bonuses, materia);
        return result.HasContent ? result : null;
    }

    /// <summary>The attribute bonuses from the item sheet, sorted into the green → pink → gold grouping the
    /// section renders (stable within each group, by sheet order).</summary>
    private static List<BonusEntry> ReadBonuses(Item item)
    {
        var entries = new List<(int Index, BonusEntry Entry)>();
        for (var i = 0; i < item.BaseParam.Count; i++)
        {
            var param = item.BaseParam[i];
            var value = item.BaseParamValue[i];
            if (param.RowId == 0 || value == 0) continue;

            var name = param.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            entries.Add((i, new BonusEntry(name, $"+{value}", ClassifyById(param.RowId))));
        }

        return entries
            .OrderBy(e => (int)e.Entry.Color)
            .ThenBy(e => e.Index)
            .Select(e => e.Entry)
            .ToList();
    }

    /// <summary>
    ///     Read the materia slots off the rendered native materia block (<c>#93</c>). Each slot is a
    ///     <b>component node</b> child of <c>#93</c> whose text lives in the <em>component's own</em> node list
    ///     (not the addon's flat list — the same trap as the damage/defense block): a filled slot has a left
    ///     text node with the materia's name and a right text node with the boosted stat (<c>"Spell Speed +24"</c>),
    ///     while an <b>empty</b> slot is the same row with no text (just the socket texture). We read the stat
    ///     (name + value) from the line matching <see cref="StatLine" /> and resolve the materia's item icon
    ///     from its name via Lumina; a visible component with no stat line is recorded as an empty slot. Filled
    ///     slots are grouped green → pink → gold; empty slots trail them. Best-effort and fully guarded.
    /// </summary>
    private static unsafe List<MateriaEntry> ScrapeMateria(AddonItemDetail* addon, IDataManager data)
    {
        var result = new List<MateriaEntry>();
        if (addon is null) return result;

        var block = ((AtkUnitBase*)addon)->GetNodeById(MateriaBlockId);
        if (block is null || (block->NodeFlags & NodeFlags.Visible) == 0) return result;

        var list = addon->UldManager.NodeList;
        if (list is null) return result;
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;
            if ((uint)node->Type < 1000) continue;             // each materia slot is a component node…
            if (!IsDescendantOf(node, MateriaBlockId)) continue; // …directly under the materia block.

            // A visible slot component with a stat line is filled; one without is an empty socket.
            result.Add(TryReadMateriaRow((AtkComponentNode*)node, data, out var filled)
                ? filled
                : new MateriaEntry(0, string.Empty, string.Empty, BonusColor.Bonus, IsEmpty: true));
        }

        // Filled slots grouped green → pink → gold; empty slots last.
        return result
            .OrderBy(e => e.IsEmpty ? 99 : (int)e.Color)
            .ToList();
    }

    /// <summary>Read one materia component: the boosted stat (name + value) from its right-hand text line, and
    /// the materia's item icon from its left-hand name line. Returns false if no stat line is present (an empty
    /// slot).</summary>
    private static unsafe bool TryReadMateriaRow(AtkComponentNode* componentNode, IDataManager data,
        out MateriaEntry entry)
    {
        entry = null!;
        var component = componentNode->Component;
        if (component is null) return false;
        var list = component->UldManager.NodeList;
        if (list is null) return false;
        var count = component->UldManager.NodeListCount;

        string? statName = null, statValue = null;
        var rawName = string.Empty;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->Type != NodeType.Text) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

            string text;
            try { text = ((AtkTextNode*)node)->NodeText.ToString(); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(text)) continue;

            var m = StatLine.Match(text);
            if (m.Success)
            {
                statName = m.Groups["name"].Value.Trim();
                statValue = "+" + m.Groups["value"].Value;
            }
            else
            {
                rawName = text; // the materia's own name (carries SeString payload artifacts)
            }
        }

        if (statName is null || statValue is null) return false;

        var icon = ResolveMateriaIcon(rawName, data);
        entry = new MateriaEntry(icon, statName, statValue, Classify(statName));
        return true;
    }

    /// <summary>
    ///     Resolve a melded materia's item icon from its rendered name. The rendered string carries SeString
    ///     payload bytes (rendered as stray glyphs), so we strip it to printable ASCII and find the longest
    ///     known materia-item name it contains (longest wins so "… Materia VIII" beats "… Materia VII"). Returns
    ///     0 (no icon) if nothing matches.
    /// </summary>
    private static uint ResolveMateriaIcon(string rawName, IDataManager data)
    {
        if (string.IsNullOrEmpty(rawName)) return 0;

        var cleaned = new string(rawName.Where(c => c is >= ' ' and < (char)0x7F).ToArray());
        if (cleaned.Length == 0) return 0;

        foreach (var (name, icon) in MateriaIcons(data))
            if (cleaned.Contains(name, StringComparison.Ordinal))
                return icon;

        return 0;
    }

    /// <summary>The materia-item name → icon table (built once from the Materia sheet, sorted longest-name
    /// first). Guarded; an unreadable sheet yields an empty table (materia then render with no icon).</summary>
    private static (string Name, uint Icon)[] MateriaIcons(IDataManager data)
    {
        if (_materiaIcons is not null) return _materiaIcons;

        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        try
        {
            foreach (var materia in data.GetExcelSheet<Materia>())
                for (var grade = 0; grade < materia.Item.Count; grade++)
                {
                    var item = materia.Item[grade];
                    if (item.RowId == 0) continue;
                    var name = item.Value.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) map[name] = item.Value.Icon;
                }
        }
        catch
        {
            // Best-effort — leave whatever was gathered.
        }

        _materiaIcons = map.Select(kv => (kv.Key, kv.Value))
            .OrderByDescending(p => p.Key.Length)
            .ToArray();
        return _materiaIcons;
    }

    private static BonusColor ClassifyById(uint baseParamId) => baseParamId switch
    {
        Strength or Dexterity or Vitality => BonusColor.Physical,
        Intelligence or Mind => BonusColor.Mental,
        _ => BonusColor.Bonus
    };

    /// <summary>Classify a stat by its (English) name into the green / pink / gold grouping. Public so the
    /// editor preview can color its sample bonuses/materia the same way.</summary>
    public static BonusColor Classify(string name)
    {
        if (PhysicalNames.Contains(name)) return BonusColor.Physical;
        if (MentalNames.Contains(name)) return BonusColor.Mental;
        return BonusColor.Bonus;
    }

    /// <summary>Walks parent links (depth-capped) to test whether <paramref name="node" /> sits under the block
    /// with id <paramref name="ancestorId" />.</summary>
    private static unsafe bool IsDescendantOf(AtkResNode* node, uint ancestorId)
    {
        var ancestor = node->ParentNode;
        for (var depth = 0; ancestor is not null && depth < 32; depth++)
        {
            if (ancestor->NodeId == ancestorId) return true;
            ancestor = ancestor->ParentNode;
        }

        return false;
    }
}
