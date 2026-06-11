namespace BetterTips.Tooltips;

/// <summary>
///     The named node groups the <c>ItemDetail</c> addon exposes via FFXIVClientStructs. The game reflows
///     these itself when hidden, so they need no manual repositioning.
/// </summary>
public enum ItemDetailGroup
{
    HeaderStats,              // Cast/Recast, Defense, Magic Defense
    SpiritbondConditionCrest, // Spiritbond & Condition (durability) bars, FC crest
    EquipRestriction,         // Item level, ClassJobCategory, required level
    Materialize               // Extractable / Projectable / Desynthesizable text
}

/// <summary>
///     Display metadata plus the hide targets for a <see cref="TooltipSection" />:
///     <list type="bullet">
///         <item><see cref="Groups" /> — named node groups; the game reflows these, hidden as-is.</item>
///         <item><see cref="Blocks" /> — top-level section-block node ids; hidden AND manually reflowed
///             (the blocks below are shifted up and the window shrunk to close the gap).</item>
///         <item><see cref="NodeIds" /> — other sub-element nodes; hidden, no reflow.</item>
///         <item><see cref="StringIndices" /> — string-array fields blanked by the signature hook.</item>
///     </list>
/// </summary>
/// <param name="TextConditionalBlocks">
///     Blocks hidden + reflowed only when their visible text currently contains the given phrase. Lets a
///     section target a shared block by content (e.g. hide the description block only when it reads
///     "Advanced Melding Forbidden", leaving real lore text alone). Matched case-insensitively, English
///     phrasing (like the rest of the version-specific field map).
/// </param>
public sealed record TooltipSectionInfo(
    TooltipSection Section,
    string Label,
    string Description,
    ItemDetailGroup[] Groups,
    uint[] Blocks,
    uint[] NodeIds,
    int[] StringIndices,
    (uint Block, string Text)[]? TextConditionalBlocks = null);

/// <summary>
///     Maps each user-facing <see cref="TooltipSection" /> to its hide targets. String indices are seeded
///     from SimpleTweaksPlugin's <c>ItemTooltipField</c> enum; node groups come from
///     <c>FFXIVClientStructs.FFXIV.Client.UI.AddonItemDetail</c>; block/node ids were read from a live
///     <c>/btips dumpnodes</c>. Node ids are stable within a game version but can shift when SE revises the
///     tooltip ULD — re-dump and update if a major UI patch lands.
/// </summary>
public static class TooltipFieldMap
{
    public static readonly IReadOnlyList<TooltipSectionInfo> Sections =
    [
        // Item category is one line inside the shared name/header block (#17), so it can't be a reflowed
        // block; hide just its text node + blank the string. (Leaves a small space in the header.)
        new(TooltipSection.ItemCategory, "Item category",
            "The category line (e.g. \"Arm Armor\").",
            [], [], [35], [2]),
        new(TooltipSection.EquipRestriction, "Item level, class/job & level",
            "Item level, equippable classes/jobs, and required level.",
            [], [62], [], []),
        new(TooltipSection.Stats, "Stats & parameters",
            "Damage / defense block and the attribute Bonuses block.",
            [], [36, 97], [], [16, 37, 38, 39, 40, 41, 42]),
        new(TooltipSection.ExtractProjectDesynth, "Extractable / Projectable / Desynthesizable",
            "The materia-extraction / glamour-projection / desynthesis flags line.",
            [ItemDetailGroup.Materialize], [], [], [35]),
        // The whole "Crafting & Repairs" block (#68) — Condition, Spiritbond, Repair Level, Materials,
        // Quick Repairs, and the Materia Melding requirement line (which lives inside this block).
        new(TooltipSection.DurabilitySpiritbondRepair, "Durability / Spiritbond / repair",
            "The whole Crafting & Repairs block (Condition, Spiritbond, Repair Level, Materials, Quick Repairs, melding requirement).",
            [], [68], [], [28, 29, 30, 31, 32, 33]),
        // The melded-materia slot display is its own block (#93).
        new(TooltipSection.MateriaMelding, "Materia (melded slots)",
            "The melded-materia slot display block.",
            [], [93], [], [52]),
        new(TooltipSection.VendorMarket, "Vendor / market price",
            "\"Sells for ... gil\" / market status and the shop selling price line.",
            [], [43, 47], [], [25, 63]),
        // Block #40 / string 13 is the item *description* field — flavor/lore text, or the notice the game
        // writes there for gear that can't be overmelded ("Advanced Melding Forbidden"). This hides the
        // whole field; to hide only the melding notice use AdvancedMelding below.
        new(TooltipSection.Description, "Flavor / description",
            "The whole item description field (lore/flavor text — or \"Advanced Melding Forbidden\" on gear " +
            "that can't be overmelded). To hide just that notice instead, use \"Advanced Melding Forbidden\".",
            [], [40], [], [13]),
        new(TooltipSection.ControlHints, "Control hints",
            "The keybind row at the bottom (Equip / Discard / etc.).",
            [], [2], [], [64]),
        // Hides only the "Advanced Melding Forbidden" line: the description block (#40) is dropped just for
        // items whose description field currently reads that phrase, so real lore text is left alone.
        new(TooltipSection.AdvancedMelding, "Advanced Melding Forbidden",
            "Hides only the \"Advanced Melding Forbidden\" notice (on gear that can't be overmelded), without " +
            "touching real description/lore text on items that have it.",
            [], [], [], [],
            [(40u, "Advanced Melding Forbidden")])
    ];

    /// <summary>String-array indices to blank for the hidden sections (signature-hook path).</summary>
    public static int[] IndicesFor(IReadOnlyCollection<TooltipSection> hidden)
        => Collect(hidden, s => s.StringIndices);

    /// <summary>Named node groups to hide for the hidden sections (game reflows these).</summary>
    public static ItemDetailGroup[] GroupsFor(IReadOnlyCollection<TooltipSection> hidden)
        => Collect(hidden, s => s.Groups);

    /// <summary>Section-block node ids to hide and reflow for the hidden sections.</summary>
    public static uint[] BlocksFor(IReadOnlyCollection<TooltipSection> hidden)
        => Collect(hidden, s => s.Blocks);

    /// <summary>Sub-element node ids to hide (no reflow) for the hidden sections.</summary>
    public static uint[] NodeIdsFor(IReadOnlyCollection<TooltipSection> hidden)
        => Collect(hidden, s => s.NodeIds);

    /// <summary>Content-conditional blocks (block id + phrase) to hide+reflow for the hidden sections.</summary>
    public static (uint Block, string Text)[] TextConditionalBlocksFor(IReadOnlyCollection<TooltipSection> hidden)
    {
        if (hidden.Count == 0) return [];
        return Sections.Where(s => hidden.Contains(s.Section))
            .SelectMany(s => s.TextConditionalBlocks ?? [])
            .ToArray();
    }

    private static T[] Collect<T>(IReadOnlyCollection<TooltipSection> hidden, Func<TooltipSectionInfo, T[]> selector)
    {
        if (hidden.Count == 0) return [];
        return Sections.Where(s => hidden.Contains(s.Section))
            .SelectMany(selector)
            .Distinct()
            .ToArray();
    }
}
