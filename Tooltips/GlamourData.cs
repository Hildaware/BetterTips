using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>A single applied dye channel: its name plus the swatch color resolved from the Lumina
/// <see cref="Stain" /> sheet (<see cref="HasColor" /> is false when the name didn't resolve).</summary>
public sealed record DyeEntry(string Name, Vector4 Color, bool HasColor);

/// <summary>
///     The per-instance data the "Glamour" section renders: the glamoured-appearance name and the applied
///     dye channels. Both are per-item state, so neither comes from Lumina static data — they're read from the
///     tooltip's string array (via <see cref="GlamourSource" />, populated by the signature hook): the
///     appearance name from slot [1], the dye lines from slot [13]. The dye text is parsed line-by-line and
///     each non-empty channel ("No Color" dropped) gets a swatch color from the <see cref="Stain" /> sheet.
/// </summary>
public sealed record GlamourData(byte[]? GlamourNameRaw, IReadOnlyList<DyeEntry> Dyes)
{
    /// <summary>Whether the section has anything to show (a glamour and/or at least one real dye).</summary>
    public bool HasContent => GlamourNameRaw is not null || Dyes.Count > 0;

    private static Dictionary<string, uint>? _stainColors;

    /// <summary>
    ///     Build the data for the current hover, or <c>null</c> when there's nothing to show.
    ///     <paramref name="dyeText" /> is the clean slot-[13] text, but the caller passes it empty unless the
    ///     rendered description block actually shows a dye line — the string array can hold a stale dye slot
    ///     from a previously-hovered dyeable item, while the node is always per-item accurate.
    /// </summary>
    public static GlamourData? FromHovered(IDataManager data, byte[]? glamourName, string dyeText)
    {
        var dyes = ParseDyes(dyeText, data);
        if (glamourName is null && dyes.Count == 0) return null;

        return new GlamourData(glamourName, dyes);
    }

    /// <summary>Parse the "Dye N: &lt;name&gt;" lines (real newlines from the string array) into entries,
    /// resolving each non-empty channel's swatch color from the <see cref="Stain" /> sheet.</summary>
    private static IReadOnlyList<DyeEntry> ParseDyes(string text, IDataManager data)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var dyes = new List<DyeEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Dye", StringComparison.Ordinal)) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var name = line[(colon + 1)..].Trim();
            if (name.Length == 0 || name.Equals("No Color", StringComparison.OrdinalIgnoreCase)) continue;

            var (color, has) = ResolveStain(data, name);
            dyes.Add(new DyeEntry(name, color, has));
        }

        return dyes;
    }

    /// <summary>Map a dye name to its swatch color via the <see cref="Stain" /> sheet (built once, cached).</summary>
    private static (Vector4 Color, bool Has) ResolveStain(IDataManager data, string name)
    {
        _stainColors ??= BuildStainColors(data);
        if (_stainColors.TryGetValue(name, out var packed))
        {
            // Stain.Color is a packed 0xRRGGBB; the alpha byte (if any) is ignored. Tune in-game if off.
            var r = ((packed >> 16) & 0xFF) / 255f;
            var g = ((packed >> 8) & 0xFF) / 255f;
            var b = (packed & 0xFF) / 255f;
            return (new Vector4(r, g, b, 1f), true);
        }

        return (new Vector4(1f, 1f, 1f, 1f), false);
    }

    private static Dictionary<string, uint> BuildStainColors(IDataManager data)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var stain in data.GetExcelSheet<Stain>())
        {
            var name = stain.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            map[name] = stain.Color;
        }

        return map;
    }
}
