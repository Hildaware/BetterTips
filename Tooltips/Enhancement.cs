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
    UnifiedItemHeader
}
