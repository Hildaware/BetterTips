using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     A snapshot of the item tooltip's <see cref="StringArrayData" /> slots, taken by the
///     <see cref="ItemTooltipModifier" /> hook each generation. The Enhanced header / bonuses read the
///     <b>rendered</b> stat values from here rather than from Lumina, because the game computes them per hover —
///     so level-syncing items (Azeyma's Earrings, etc.) show their <em>scaled</em> item level and attributes,
///     not the static sheet base. Unaffected by our relayout (we hide the native nodes, but the string array
///     stays), so no per-hover caching is needed.
///     <para>
///         Slots are <b>stale</b> for fields an item doesn't use, so consumers gate per-item (the header/bonuses
///         only engage for equippable items, which populate these slots) and fall back to Lumina when a slot is
///         empty or the hook is unavailable (its signature didn't resolve).
///     </para>
/// </summary>
public sealed unsafe class TooltipStrings
{
    // Version-specific string-array indices (read from a live "/btips dump"; re-dump if a patch shifts them).
    public const int ItemLevelIndex = 27;       // "Item Level <n>" (the scaled value)
    public const int RequiredLevelIndex = 23;   // "Lv. <n>"
    public const int PrimaryLabelAIndex = 4;     // e.g. "Magic Defense" / "Physical Damage"
    public const int PrimaryValueAIndex = 7;
    public const int PrimaryLabelBIndex = 5;     // e.g. "Defense" / "Magic Damage"
    public const int PrimaryValueBIndex = 8;
    public const int BonusesStartIndex = 37;     // first attribute-bonus line ("Vitality +113")
    public const int BonusesEndIndex = 51;       // last possible bonus line before "Materia" (52)

    private string[] _slots = [];

    /// <summary>The slot at <paramref name="index" />, or empty if out of range / unset.</summary>
    public string Get(int index) => index >= 0 && index < _slots.Length ? _slots[index] : string.Empty;

    /// <summary>Snapshot every slot's plain text (called from the hook each generation).</summary>
    public void Capture(StringArrayData* sad)
    {
        if (sad is null) return;
        var size = sad->AtkArrayData.Size;
        if (size <= 0)
        {
            _slots = [];
            return;
        }

        var slots = new string[size];
        for (var i = 0; i < size; i++)
        {
            var span = sad->StringArray[i].AsSpan();
            if (span.IsEmpty)
            {
                slots[i] = string.Empty;
                continue;
            }

            try { slots[i] = SeString.Parse(span).TextValue ?? string.Empty; }
            catch { slots[i] = string.Empty; }
        }

        _slots = slots;
    }

    /// <summary>Clear the snapshot (e.g. when the hook is inactive or the plugin is disabled).</summary>
    public void Clear() => _slots = [];
}
