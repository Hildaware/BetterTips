namespace BetterTips.Tooltips;

/// <summary>
///     A user-facing group of item-tooltip lines that can be shown or hidden as a unit. Each section maps
///     to one or more raw indices in the item tooltip's <c>StringArrayData</c>; that mapping lives in
///     <see cref="TooltipFieldMap" />.
/// </summary>
public enum TooltipSection
{
    ItemCategory,
    EquipRestriction,
    Stats,
    ExtractProjectDesynth,
    DurabilitySpiritbondRepair,
    MateriaMelding,
    VendorMarket,
    Description,
    ControlHints,

    // Appended last on purpose: HiddenSections is serialized by enum value, so new members must go at the
    // end or existing saved configs would shift to the wrong sections.
    AdvancedMelding
}
