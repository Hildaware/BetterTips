using System.ComponentModel;

namespace BetterTips.Tooltips;

/// <summary>
///     Which corner of the tooltip stays fixed as BetterTips resizes it (see
///     <see cref="TooltipRelayoutController" />). Only the vertical half decides behavior today — the relayout
///     never changes the tooltip <em>width</em>, so left vs. right is reserved (a no-op until width trimming
///     ever exists). A <b>Top</b> corner keeps the top edge put: the window grows and shrinks downward, which
///     is the game's natural behavior (no window move). A <b>Bottom</b> corner keeps the bottom edge put: the
///     window is moved down by however much we trimmed so its bottom stays where the game's natural bottom
///     was, growing/shrinking upward.
///     <para>
///         The <see cref="DescriptionAttribute" /> text is the selector label — read by the native editor's
///         <c>EnumDropDownNode</c> and the ImGui fallback combo.
///     </para>
/// </summary>
public enum TooltipAnchor
{
    [Description("Top-left")] TopLeft,
    [Description("Top-right")] TopRight,
    [Description("Bottom-left")] BottomLeft,
    [Description("Bottom-right")] BottomRight
}
