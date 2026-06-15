namespace BetterTips.Tooltips;

/// <summary>
///     A curated, opinionated tooltip <b>enhancement</b> — a single named "make this section more
///     streamlined / cleaner" toggle surfaced in the editor's <c>Enhancements</c> tab. Unlike the
///     <see cref="LayoutSection" /> show/hide + reorder controls (which are generic and binary), each
///     enhancement is a hand-picked modification whose implementation we choose per toggle (a line hide,
///     or swapping a native block for a custom Lumina-fed render — the Gear Sets pattern). The user only
///     ever sees the checkbox; the mechanism lives behind it.
///     <para>
///         <b>Append-only.</b> Members are serialized by value in
///         <see cref="Configuration.Configuration.EnabledEnhancements" />, so add new members at the end
///         and never reorder/remove existing ones. Register each member's label/description in
///         <see cref="UI.EnhancementCatalog" /> and wire its effect in the relayout/preview path.
///     </para>
/// </summary>
public enum Enhancement
{
    /// <summary>
    ///     Merge the icon/name header, the Item Level / Class / Job block (<c>#62</c>), and the
    ///     Damage / Defense block (<c>#36</c>) into a single redesigned "item header" section at the top of
    ///     the tooltip. Backed by Lumina static data (name, icon, item level, class/job, damage/defense) —
    ///     see the unified-header builder. The preview renders the redesign; the live relayout folds the
    ///     three native blocks into the custom one (Phase 2).
    /// </summary>
    UnifiedItemHeader,

    /// <summary>
    ///     Merge the attribute <b>Bonuses</b> block (<c>#97</c>) and the <b>Materia</b> block (<c>#93</c>) into
    ///     one redesigned section: the bonuses listed in two columns (physical attributes green, mental
    ///     attributes pink, the remaining secondary "bonuses" gold), then — after a little vertical space — the
    ///     melded materia in the same two-column form (the materia icon followed by the stat boost it grants,
    ///     colored the same way; the materia name is omitted). Attribute bonuses come from Lumina; melded
    ///     materia is per-instance, so it is scraped from the rendered native block. While this is on, the
    ///     editor's Structure tab can't hide the Bonuses or Materia sections (they're folded in here).
    /// </summary>
    UnifiedBonusesMateria
}
