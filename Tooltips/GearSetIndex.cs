using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace BetterTips.Tooltips;

/// <summary>
///     A reverse index "item id → which jobs have a gear set containing it", built from the player's
///     <see cref="RaptureGearsetModule" />. Used by <see cref="GearSetBlockProvider" /> to draw the
///     "Gear Sets" row. We dedupe to <em>distinct</em> jobs (a job that owns three sets with the same ring
///     contributes one icon, not three), in first-seen gear-set order.
///     <para>
///         Built lazily and cached for <see cref="TtlMilliseconds" /> — gear sets change rarely, and a
///         tooltip can refresh many times per second, so rebuilding the whole table every hover would be
///         wasteful. The rebuild is cheap (≤100 sets × 14 slots) and runs on the framework thread.
///     </para>
/// </summary>
public sealed unsafe class GearSetIndex
{
    /// <summary>How long a built index is reused before the next lookup rebuilds it.</summary>
    private const long TtlMilliseconds = 2000;

    /// <summary>RaptureGearsetModule holds at most this many gear sets.</summary>
    private const int MaxGearSets = 100;

    // itemId (base, no HQ offset) → distinct ClassJob ids, in first-seen order.
    private readonly Dictionary<uint, List<byte>> _itemToJobs = new();

    private long _builtAtTick = long.MinValue;

    /// <summary>Rebuild the index if the cached copy is older than <see cref="TtlMilliseconds" />.</summary>
    public void EnsureFresh()
    {
        var now = Environment.TickCount64;
        if (_builtAtTick != long.MinValue && now - _builtAtTick < TtlMilliseconds)
            return;

        Rebuild();
        _builtAtTick = now;
    }

    /// <summary>
    ///     The distinct jobs that have a gear set containing <paramref name="baseItemId" /> (HQ offset
    ///     already stripped by the caller), or <c>null</c> if the item is in no gear set.
    /// </summary>
    public IReadOnlyList<byte>? JobsForItem(uint baseItemId)
        => _itemToJobs.TryGetValue(baseItemId, out var jobs) ? jobs : null;

    private void Rebuild()
    {
        _itemToJobs.Clear();

        var module = RaptureGearsetModule.Instance();
        if (module is null) return;

        for (var i = 0; i < MaxGearSets; i++)
        {
            if (!module->IsValidGearset(i)) continue;

            var gearset = module->GetGearset(i);
            if (gearset is null) continue;

            var job = gearset->ClassJob;

            foreach (ref var item in gearset->Items)
            {
                var itemId = item.ItemId;
                if (itemId == 0) continue;

                // Gear sets store base ids, but mask defensively in case an HQ-flagged id ever appears —
                // base item ids are far below the 1,000,000 HQ offset, so this only ever strips the flag.
                if (itemId >= 1_000_000) itemId -= 1_000_000;

                if (!_itemToJobs.TryGetValue(itemId, out var jobs))
                {
                    jobs = new List<byte>(2);
                    _itemToJobs[itemId] = jobs;
                }

                if (!jobs.Contains(job))
                    jobs.Add(job);
            }
        }
    }
}
