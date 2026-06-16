using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     Shared carrier for the item's effect lines, snapshotted from the tooltip's <see cref="StringArrayData" />
///     by the <see cref="ItemTooltipModifier" /> hook each generation. We read the string array (slot
///     <see cref="EffectsIndex" />) rather than the rendered Effects node because the <b>node's</b> newline is a
///     SeString payload that survives neither <c>ToString()</c> nor <c>SeString.Parse</c> (so scraped lines
///     merge into one), whereas the string array uses a real <c>'\n'</c> — the same reason the glamour dye lines
///     come from here (<see cref="GlamourSource" />).
///     <para>
///         The slot is <b>stale</b> for an item with no effects (the game leaves the previous item's text), so
///         consumers must gate on a per-item signal (the native Effects block <c>#49</c> being visible). When
///         the hook's signature is unavailable the snapshot stays empty and the Effects section omits itself.
///     </para>
/// </summary>
public sealed unsafe class EffectsSource
{
    // Version-specific string-array index (read from a live "/btips dump"): [15] is the "Effects" label, [16]
    // is the effect lines ("Crit +8% (Max 60)\nVitality +8% (Max 65)\n…"). Re-dump if a patch shifts it.
    private const int EffectsIndex = 16;

    private string _text = string.Empty;

    /// <summary>The effect lines as clean text with real newlines, or empty. Stale for no-effect items — gate
    /// on the Effects block (<c>#49</c>) being visible.</summary>
    public string Text => _text;

    /// <summary>Snapshot the effects slot from the tooltip's string array (called from the hook each generation).</summary>
    public void Capture(StringArrayData* sad)
    {
        if (sad is null) return;
        var size = sad->AtkArrayData.Size;
        if (EffectsIndex < 0 || EffectsIndex >= size)
        {
            _text = string.Empty;
            return;
        }

        var span = sad->StringArray[EffectsIndex].AsSpan();
        if (span.IsEmpty)
        {
            _text = string.Empty;
            return;
        }

        try { _text = SeString.Parse(span).TextValue ?? string.Empty; }
        catch { _text = string.Empty; }
    }

    /// <summary>Clear the snapshot (e.g. when the hook is inactive or the plugin is disabled).</summary>
    public void Clear() => _text = string.Empty;
}
