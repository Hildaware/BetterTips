using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>One row of the "Condition" section: an icon and the value drawn next to it.</summary>
public sealed record ConditionEntry(uint IconId, string Value);

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

    /// <summary>Whether the section has anything to show.</summary>
    public bool HasContent => Durability is not null || Spiritbond is not null || SellPrice is not null;

    /// <summary>The visible rows in display order: durability, spiritbond, then sell price (each present only
    /// when it has a value).</summary>
    public IReadOnlyList<ConditionEntry> Entries()
    {
        var list = new List<ConditionEntry>(3);
        if (Durability is not null) list.Add(new ConditionEntry(DurabilityIcon, Durability));
        if (Spiritbond is not null) list.Add(new ConditionEntry(SpiritbondIcon, Spiritbond));
        if (SellPrice is not null) list.Add(new ConditionEntry(SellPriceIcon, SellPrice));
        return list;
    }

    /// <summary>
    ///     Build the data for the current hover, or <c>null</c> when there's nothing to show. The condition
    ///     values are scraped from <paramref name="addon" />'s gauge value nodes (so this must run <em>before</em>
    ///     the relayout hides the gauge); the sell price is read from Lumina via <see cref="IGameGui.HoveredItem" />.
    /// </summary>
    public static unsafe ConditionData? FromHoveredItem(IGameGui gameGui, IDataManager data, AddonItemDetail* addon)
    {
        if (addon is null) return null;

        var durability = ReadGaugeValue(addon->ConditionGroup, addon->ConditionValue);
        var spiritbond = ReadGaugeValue(addon->SpiritbondGroup, addon->SpiritbondValue);

        string? sellPrice = null;
        var hovered = gameGui.HoveredItem;
        if (hovered != 0)
        {
            var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
            if (data.GetExcelSheet<Item>().TryGetRow(itemId, out var item) && item.PriceLow > 0)
                sellPrice = item.PriceLow.ToString("N0");
        }

        var result = new ConditionData(durability, spiritbond, sellPrice);
        return result.HasContent ? result : null;
    }

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
