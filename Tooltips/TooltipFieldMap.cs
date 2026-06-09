namespace BetterTips.Tooltips;

/// <summary>
///     The named node groups the <c>ItemDetail</c> addon exposes via FFXIVClientStructs. Hiding one of
///     these is signature-free and robust — FFXIVClientStructs maintains the field offsets — so this is the
///     primary hide path. Several groups also contain non-text elements (the Spiritbond/Condition bars, the
///     FC crest) that have no string to blank, so node hiding is the only way to remove them.
/// </summary>
public enum ItemDetailGroup
{
    HeaderStats,              // Cast/Recast, Defense, Magic Defense
    SpiritbondConditionCrest, // Spiritbond & Condition (durability) bars, FC crest
    EquipRestriction,         // Item level, ClassJobCategory, required level
    Materialize               // Extractable / Projectable / Desynthesizable text
}

/// <summary>
///     Display metadata plus the two kinds of hide target a <see cref="TooltipSection" /> can have:
///     <see cref="Groups" /> are hidden by node visibility (signature-free, primary), and
///     <see cref="StringIndices" /> are blanked in the tooltip's <c>StringArrayData</c> by the signature
///     hook (cleanest collapse, used when the signature resolves). A section may use either or both; the
///     two subsystems each process their own target kind independently.
/// </summary>
public sealed record TooltipSectionInfo(
    TooltipSection Section,
    string Label,
    string Description,
    ItemDetailGroup[] Groups,
    int[] StringIndices);

/// <summary>
///     Maps each user-facing <see cref="TooltipSection" /> to its hide targets. String indices are seeded
///     from SimpleTweaksPlugin's <c>ItemTooltipField</c> enum; node groups come from
///     <c>FFXIVClientStructs.FFXIV.Client.UI.AddonItemDetail</c>. Both are refined with a live
///     <c>/btips dump</c> and in-game node inspection (repair/melding detail, item level text, vendor
///     price, bind status still need indices/nodes pinned down).
/// </summary>
public static class TooltipFieldMap
{
    public static readonly IReadOnlyList<TooltipSectionInfo> Sections =
    [
        new(TooltipSection.ItemCategory, "Item category",
            "The category line (e.g. \"Arm Armor\").",
            [], [2]),
        new(TooltipSection.EquipRestriction, "Item level, class/job & level",
            "Item level, equippable classes/jobs, and required level (one addon group).",
            [ItemDetailGroup.EquipRestriction], []),
        new(TooltipSection.Stats, "Stats & parameters",
            "Damage / defense lines and attribute parameters.",
            [ItemDetailGroup.HeaderStats], [16, 37, 38, 39, 40, 41, 42]),
        new(TooltipSection.ExtractProjectDesynth, "Extractable / Projectable / Desynthesizable",
            "The materia-extraction / glamour-projection / desynthesis flags line.",
            [ItemDetailGroup.Materialize], [35]),
        new(TooltipSection.DurabilitySpiritbondRepair, "Durability / Spiritbond / repair",
            "Durability (condition) and Spiritbond bars plus their percentage text.",
            [ItemDetailGroup.SpiritbondConditionCrest], [28, 30]),
        new(TooltipSection.Description, "Flavor / description",
            "The lore/description paragraph.",
            [], [13]),
        new(TooltipSection.ControlHints, "Control hints",
            "The keybind row at the bottom (Equip / Discard / etc.).",
            [], [64])
    ];

    /// <summary>The distinct string-array indices to blank for the given hidden sections (signature-hook path).</summary>
    public static int[] IndicesFor(IReadOnlyCollection<TooltipSection> hidden)
    {
        if (hidden.Count == 0) return [];
        return Sections.Where(s => hidden.Contains(s.Section))
            .SelectMany(s => s.StringIndices)
            .Distinct()
            .ToArray();
    }

    /// <summary>The distinct node groups to hide for the given hidden sections (signature-free node path).</summary>
    public static ItemDetailGroup[] GroupsFor(IReadOnlyCollection<TooltipSection> hidden)
    {
        if (hidden.Count == 0) return [];
        return Sections.Where(s => hidden.Contains(s.Section))
            .SelectMany(s => s.Groups)
            .Distinct()
            .ToArray();
    }
}
