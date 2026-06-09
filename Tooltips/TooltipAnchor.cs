namespace BetterTips.Tooltips;

/// <summary>
///     Which corner of the tooltip stays fixed when BetterTips shrinks it. Only the height changes today,
///     so the vertical half decides the behavior (top corner = grow/shrink downward; bottom corner = keep
///     the bottom edge put and move the window up). Left vs right is reserved for if width ever changes.
/// </summary>
public enum TooltipAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}
