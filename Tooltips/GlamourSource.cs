using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     Shared carrier for the glamoured-appearance name. The item tooltip loads it into a string-array slot
///     but never renders it to a node, so it can only be read from the tooltip's own <see cref="StringArrayData" />
///     — which only the <see cref="ItemTooltipModifier" /> signature hook is handed. That hook snapshots the
///     name slots each time the tooltip is generated; <see cref="GlamourBlockProvider" /> reads the latest
///     snapshot when the relayout runs. Both run on the framework thread, so no locking is needed.
///     <para>
///         When the hook's signature is unavailable the snapshot stays empty and the Glamour section simply
///         omits the appearance line — the dyes still work (scraped signature-free from the description block).
///     </para>
/// </summary>
public sealed unsafe class GlamourSource
{
    // Version-specific string-array indices (read from a live "/btips dump"): [0] is the real item name,
    // [1] is the glamoured-appearance name (a leading SeString glyph + the name), and [13] is the dye lines
    // ("Dye 1: … \n Dye 2: …"). The string array is cleaner than the rendered node — its newline is a real
    // '\n', whereas the node's separator survives neither ToString() nor SeString.Parse. Re-dump if a patch
    // shifts these.
    private const int ItemNameIndex = 0;
    private const int GlamourNameIndex = 1;
    private const int DyeIndex = 13;

    private byte[] _glamourRaw = [];
    private string _glamourPlain = string.Empty;
    private string _itemPlain = string.Empty;
    private string _dyeText = string.Empty;

    /// <summary>
    ///     The glamoured-appearance name as raw SeString bytes (payloads preserved so the game renders the
    ///     glamour glyph), or <c>null</c> when the item isn't glamoured — i.e. the slot is empty or just
    ///     repeats the real item name.
    /// </summary>
    public byte[]? GlamourNameRaw
        => _glamourPlain.Length > 0 && !PlainEquals(_glamourPlain, _itemPlain) ? _glamourRaw : null;

    /// <summary>The dye lines as clean text with real newlines ("Dye 1: … \n Dye 2: …"), or empty.</summary>
    public string DyeText => _dyeText;

    /// <summary>Snapshot the name + dye slots from the tooltip's string array (called from the hook each generation).</summary>
    public void Capture(StringArrayData* sad)
    {
        if (sad is null) return;
        var size = sad->AtkArrayData.Size;

        _itemPlain = ReadPlain(sad, ItemNameIndex, size);
        (_glamourRaw, _glamourPlain) = ReadRaw(sad, GlamourNameIndex, size);
        _dyeText = ReadPlain(sad, DyeIndex, size);
    }

    /// <summary>Clear the snapshot (e.g. when the hook is inactive or the plugin is disabled).</summary>
    public void Clear()
    {
        _glamourRaw = [];
        _glamourPlain = string.Empty;
        _itemPlain = string.Empty;
        _dyeText = string.Empty;
    }

    private static bool PlainEquals(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

    private static string ReadPlain(StringArrayData* sad, int index, int size)
    {
        if (index < 0 || index >= size) return string.Empty;
        var span = sad->StringArray[index].AsSpan();
        if (span.IsEmpty) return string.Empty;
        try { return SeString.Parse(span).TextValue ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static (byte[] Raw, string Plain) ReadRaw(StringArrayData* sad, int index, int size)
    {
        if (index < 0 || index >= size) return ([], string.Empty);
        var span = sad->StringArray[index].AsSpan();
        if (span.IsEmpty) return ([], string.Empty);

        var raw = span.ToArray(); // copy — the underlying array memory is reused across tooltips
        string plain;
        try { plain = SeString.Parse(span).TextValue ?? string.Empty; }
        catch { plain = string.Empty; }
        return (raw, plain);
    }
}
