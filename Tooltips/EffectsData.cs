using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     The item's effect lines (food stat bonuses, potion HP restore, etc.) for BetterTips' own headerless
///     Effects section. The <b>text</b> comes from <see cref="EffectsSource" /> (the tooltip string array, which
///     has real <c>\n</c> separators — the rendered node's newline payload doesn't survive <c>ToString()</c>),
///     split into lines. The native Effects block (<c>#49</c>) being <b>visible</b> is the per-item "has
///     effects" gate (the string-array slot is stale for items with none), so a null result means no section.
/// </summary>
public sealed record EffectsData(IReadOnlyList<string> Lines)
{
    /// <summary>The native Effects block id.</summary>
    public const uint EffectsBlockId = 49;

    public bool HasContent => Lines.Count > 0;

    /// <summary>
    ///     Build the effect lines for the hovered item, or <c>null</c> when its Effects block (<c>#49</c>) isn't
    ///     showing (no effects). Must run <em>before</em> the relayout hides <c>#49</c>, so the gate is read
    ///     while the game still has it visible.
    /// </summary>
    public static unsafe EffectsData? FromHovered(AddonItemDetail* addon, EffectsSource source)
    {
        if (addon is null) return null;

        var block = addon->GetNodeById(EffectsBlockId);
        if (block is null || (block->NodeFlags & NodeFlags.Visible) == 0) return null;

        var text = source.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) lines.Add(line);
        }

        return lines.Count > 0 ? new EffectsData(lines) : null;
    }
}
