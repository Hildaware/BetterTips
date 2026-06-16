using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>
///     The static data the <b>non-equipment</b> item-header section renders, from the Lumina <see cref="Item" />
///     sheet (name, icon, type/category, rarity, and the recast cooldown). It's the counterpart to
///     <see cref="UnifiedHeaderData" /> — that one is gated to equippable gear, this one to everything else
///     (consumables, materials, Triple Triad cards, …). All sheet-derived, so the same data could drive a
///     preview. A non-zero <see cref="RecastSeconds" /> switches the header to the banner layout with a big
///     recast value; otherwise it's the compact "type above name" layout.
/// </summary>
public sealed record NonEquipHeaderData(
    string Name,
    uint IconId,
    string Type,
    byte Rarity,
    uint RecastSeconds)
{
    /// <summary>The rendered native name as raw SeString bytes (payloads preserved → the HQ mark etc. render).
    /// Set by the live block from node <c>#33</c>; <c>null</c> falls back to the payload-free <see cref="Name" />.</summary>
    public byte[]? NameRaw { get; init; }

    /// <summary>Whether the item has a recast cooldown (→ banner layout with the big recast value).</summary>
    public bool HasRecast => RecastSeconds > 0;

    /// <summary>The recast formatted for display (e.g. <c>"15s"</c>, <c>"4:30"</c>); empty when there's none.</summary>
    public string RecastText => RecastSeconds == 0
        ? string.Empty
        : RecastSeconds < 60
            ? $"{RecastSeconds}s"
            : $"{RecastSeconds / 60}:{RecastSeconds % 60:00}";

    /// <summary>
    ///     Build the data for the currently hovered <b>non-equippable</b> item, or <c>null</c> when nothing
    ///     resolvable is hovered or the item is equippable gear (which gets the <see cref="UnifiedHeaderData" />
    ///     header instead). Mirrors the HQ-offset handling of the other providers.
    /// </summary>
    public static NonEquipHeaderData? FromHoveredItem(IGameGui gameGui, IDataManager data)
    {
        var hovered = gameGui.HoveredItem;
        if (hovered == 0) return null;

        var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return null;

        // Equippable gear is the unified (gear) header's job; this header is for everything else.
        if (item.EquipSlotCategory.RowId != 0) return null;

        return new NonEquipHeaderData(
            item.Name.ToString(),
            item.Icon,
            item.ItemUICategory.Value.Name.ToString(),
            item.Rarity,
            item.Cooldowns);
    }
}
