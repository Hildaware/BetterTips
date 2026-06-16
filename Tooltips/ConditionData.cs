using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>One row of the "Condition" section: an icon, the value, and the value's colour.</summary>
public sealed record ConditionEntry(uint IconId, string Value, Vector4 Color);

/// <summary>
///     The data the "Condition" section renders: the item's <b>durability</b> and <b>spiritbond</b> (both
///     per-instance) and its <b>sell price</b> (static). The two condition values can't come from Lumina — the
///     master sheet can't know a specific item's wear — so they're read off the rendered tooltip's own gauge
///     value nodes (<see cref="AddonItemDetail.ConditionValue" /> / <see cref="AddonItemDetail.SpiritbondValue" />),
///     gated on the matching gauge group actually being shown for the hovered item so a stale value can't leak
///     onto an item with no durability (the project's "Lumina for static data, scrape rendered nodes for
///     per-instance data" rule). The sell price comes from the Lumina <see cref="Item" /> sheet.
/// </summary>
public sealed record ConditionData(string? Durability, string? Spiritbond, string? SellPrice)
{
    /// <summary>The user-supplied game icon ids for each row.</summary>
    public const uint DurabilityIcon = 60434;
    public const uint SpiritbondIcon = 60427;
    public const uint SellPriceIcon = 60412;

    // Row colours. Sell price is always a warm gold; durability is coloured by percentage (<50% yellow, <30%
    // orange, <15% red, else white); spiritbond is green only at 100% (ready to extract), else white.
    private static readonly Vector4 DefaultColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 GoldColor = new(0xE6 / 255f, 0xBE / 255f, 0x5A / 255f, 1f);
    private static readonly Vector4 YellowColor = new(0xFF / 255f, 0xE3 / 255f, 0x4D / 255f, 1f);
    private static readonly Vector4 OrangeColor = new(0xFF / 255f, 0x9B / 255f, 0x3D / 255f, 1f);
    private static readonly Vector4 RedColor = new(0xE8 / 255f, 0x47 / 255f, 0x3C / 255f, 1f);
    private static readonly Vector4 GreenColor = new(0x5A / 255f, 0xE6 / 255f, 0x5A / 255f, 1f);

    /// <summary>Whether the section has anything to show.</summary>
    public bool HasContent => Durability is not null || Spiritbond is not null || SellPrice is not null;

    /// <summary>The visible rows in display order: durability, spiritbond, then sell price (each present only
    /// when it has a value), each with its colour.</summary>
    public IReadOnlyList<ConditionEntry> Entries()
    {
        var list = new List<ConditionEntry>(3);
        if (Durability is not null) list.Add(new ConditionEntry(DurabilityIcon, Durability, PercentColor(Durability)));
        if (Spiritbond is not null) list.Add(new ConditionEntry(SpiritbondIcon, Spiritbond, SpiritbondColor(Spiritbond)));
        if (SellPrice is not null) list.Add(new ConditionEntry(SellPriceIcon, SellPrice, GoldColor));
        return list;
    }

    /// <summary>Colour a percentage value ("23%") by threshold: &lt;15% red, &lt;30% orange, &lt;50% yellow,
    /// otherwise the default. Non-percent text keeps the default.</summary>
    private static Vector4 PercentColor(string value)
    {
        if (!int.TryParse(value.TrimEnd('%', ' '), out var pct)) return DefaultColor;
        if (pct < 15) return RedColor;
        if (pct < 30) return OrangeColor;
        if (pct < 50) return YellowColor;
        return DefaultColor;
    }

    /// <summary>Colour spiritbond: green at 100% (ready to extract materia), otherwise the default white.</summary>
    private static Vector4 SpiritbondColor(string value)
        => int.TryParse(value.TrimEnd('%', ' '), out var pct) && pct >= 100 ? GreenColor : DefaultColor;

    /// <summary>
    ///     Build the data for the current hover, or <c>null</c> when there's nothing to show. The condition
    ///     values are scraped from <paramref name="addon" />'s gauge value nodes (so this must run <em>before</em>
    ///     the relayout hides the gauge); the sell price is read from Lumina via <see cref="IGameGui.HoveredItem" />.
    /// </summary>
    public static unsafe ConditionData? FromHoveredItem(IGameGui gameGui, IDataManager data, AddonItemDetail* addon)
    {
        if (addon is null) return null;

        string? durability = null, spiritbond = null, sellPrice = null;

        var hovered = gameGui.HoveredItem;
        if (hovered != 0)
        {
            var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
            if (data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            {
                if (item.PriceLow > 0)
                    sellPrice = item.PriceLow.ToString("N0") + "g"; // g = gil

                // Durability/spiritbond only exist on equippable gear (weapons, armour, accessories, tools).
                // Gate on Lumina, NOT on the rendered gauge's visibility: the relayout hides the gauge block
                // (#7) and the game doesn't re-hide its child groups for a non-gear item, so a Triple Triad
                // card / material / consumable would otherwise inherit the previous gear's stale gauge value.
                // Soul crystals are equippable but have no durability, so exclude them.
                if (HasDurability(item))
                {
                    durability = ReadGaugeValue(addon->ConditionGroup, addon->ConditionValue);
                    spiritbond = ReadGaugeValue(addon->SpiritbondGroup, addon->SpiritbondValue);
                }
            }
        }

        var result = new ConditionData(durability, spiritbond, sellPrice);
        return result.HasContent ? result : null;
    }

    /// <summary>Whether the item is durability-bearing gear: equippable (has an equip slot) but not a soul
    /// crystal (which is equippable yet has no durability/spiritbond).</summary>
    private static bool HasDurability(Item item)
        => item.EquipSlotCategory.RowId != 0 && item.EquipSlotCategory.Value.SoulCrystal == 0;

    /// <summary>Read a gauge's value text, but only when its group is actually visible for this item (so a
    /// stale value left in the node from a previously-hovered durability item can't surface here). Guarded —
    /// a bad pointer must not throw out of the framework callback.</summary>
    private static unsafe string? ReadGaugeValue(AtkResNode* group, AtkTextNode* valueNode)
    {
        if (group is null || (group->NodeFlags & NodeFlags.Visible) == 0) return null;
        if (valueNode is null) return null;

        string text;
        try { text = valueNode->NodeText.ToString(); }
        catch { return null; }

        text = text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
