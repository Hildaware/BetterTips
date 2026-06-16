using System.Text.Json;

namespace FfxivCollections;

/// <summary>
///     The on-disk snapshot store for ownership state the game only keeps in memory while the source is open
///     (retainers, the Glamour Dresser, the Armoire, saddlebags). The service captures a source into here
///     whenever it's loaded, and the query path merges these snapshots with what it can read live. Each source
///     is keyed (e.g. <c>retainer:{id}</c>) so a fresh capture replaces the old one, and carries the owning
///     character + a capture time so the tooltip can show how stale it is.
///     <para>
///         Serialized as a single JSON file at a <b>shared</b> path (the host passes the directory), so several
///         plugins read/write one store. Saves are debounced. All access is on the framework thread, so no
///         locking is needed; I/O failures are swallowed (a missing/corrupt store just starts empty).
///     </para>
/// </summary>
internal sealed class OwnershipStore
{
    private const int CurrentVersion = 1;

    /// <summary>One stored item stack: the item and how many sat there at capture time.</summary>
    internal sealed class StoredStack
    {
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
    }

    /// <summary>One captured source (a retainer's bags, the dresser, the armoire, a saddlebag) for one
    /// character, with the stacks it held and when it was captured.</summary>
    internal sealed class StoredSource
    {
        public string Key { get; set; } = string.Empty;
        public OwnershipSourceKind Kind { get; set; }
        public string Label { get; set; } = string.Empty;
        public ulong OwnerContentId { get; set; }
        public long CapturedAtUnix { get; set; }
        public List<StoredStack> Stacks { get; set; } = [];
    }

    private sealed class StoreFile
    {
        public int Version { get; set; } = CurrentVersion;
        public List<StoredSource> Sources { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Action<string, Exception>? _onError;
    private StoreFile _file = new();
    private bool _dirty;
    private long _lastSaveTick;

    public OwnershipStore(string directory, Action<string, Exception>? onError = null)
    {
        _onError = onError;
        _path = Path.Combine(directory, "ownership.json");
        Load();
    }

    /// <summary>All stored sources (read-only view for the query path).</summary>
    public IReadOnlyList<StoredSource> Sources => _file.Sources;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<StoreFile>(json, JsonOptions);
            if (parsed is not null && parsed.Version == CurrentVersion)
                _file = parsed;
        }
        catch (Exception ex)
        {
            // A missing/corrupt/old store just starts empty — never block the plugin on it.
            _onError?.Invoke("BetterTips: failed to load ownership store; starting empty.", ex);
            _file = new StoreFile();
        }
    }

    /// <summary>
    ///     Replace (or add) the snapshot for <paramref name="key" /> with the given stacks, stamped now. A
    ///     capture that finds nothing (empty <paramref name="stacks" />) still replaces the old snapshot, so an
    ///     emptied retainer/dresser stops reporting its old contents.
    /// </summary>
    public void Capture(string key, OwnershipSourceKind kind, string label, ulong ownerContentId,
        IEnumerable<StoredStack> stacks, long nowUnix)
    {
        var source = new StoredSource
        {
            Key = key,
            Kind = kind,
            Label = label,
            OwnerContentId = ownerContentId,
            CapturedAtUnix = nowUnix,
            Stacks = stacks.ToList()
        };

        var index = _file.Sources.FindIndex(s => s.Key == key);
        if (index >= 0) _file.Sources[index] = source;
        else _file.Sources.Add(source);

        _dirty = true;
    }

    /// <summary>Persist now if anything changed. Guarded — an I/O failure must not escape the framework tick.</summary>
    public void Save()
    {
        if (!_dirty) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_file, JsonOptions));
            _dirty = false;
        }
        catch (Exception ex)
        {
            _onError?.Invoke("BetterTips: failed to save ownership store.", ex);
        }
    }

    /// <summary>Save at most once every <paramref name="minIntervalMs" /> ms, and only when dirty.</summary>
    public void SaveIfDirty(long nowTick, long minIntervalMs = 5000)
    {
        if (!_dirty) return;
        if (nowTick - _lastSaveTick < minIntervalMs) return;
        _lastSaveTick = nowTick;
        Save();
    }
}
