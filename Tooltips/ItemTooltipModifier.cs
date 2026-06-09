using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     Intercepts the game's item-tooltip generation and blanks the lines for the sections the user has
///     chosen to hide. We hook <c>GenerateItemTooltip</c> directly — the same entry point SimpleTweaks'
///     tooltip tweaks use — because it hands us the tooltip's own <see cref="StringArrayData" /> with
///     stable per-line indices (vs. the global array table the addon-lifecycle events expose, which would
///     need a magic index). The hook is guarded: if the signature ever fails to resolve after a game
///     patch, the plugin still loads and simply leaves tooltips untouched.
///     <para>
///         Crash-safety: this detour runs inside the game's own call stack, so an escaping managed
///         exception or an invalid pointer dereference would take the whole client down. We (1) always
///         call the original first and always return its result, (2) wrap all of our own work in a catch,
///         and (3) null- and bounds-check every native access before touching it. An access violation is
///         a corrupted-state exception that <c>catch</c> cannot stop, so those checks — not the
///         try/catch — are the real protection.
///     </para>
/// </summary>
public sealed unsafe class ItemTooltipModifier : IDisposable
{
    // GenerateItemTooltip(AtkUnitBase* addonItemDetail, NumberArrayData*, StringArrayData*) -> void*
    // Shared with SimpleTweaksPlugin (Tweaks/TooltipTweaks.cs).
    private const string GenerateItemTooltipSignature =
        "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B 42 ?? 4C 8B EA";

    private delegate nint GenerateItemTooltipDelegate(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData);

    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;
    private readonly Hook<GenerateItemTooltipDelegate>? _hook;

    // Snapshot of the string-array indices to blank, recomputed whenever the config changes. Replaced as a
    // whole reference (atomic) so the game-thread detour never observes a half-mutated collection.
    private int[] _hiddenIndices = [];

    // Set by "/btips dump"; on the next tooltip we log every non-empty line + index, then clear it.
    private volatile bool _dumpRequested;

    public ItemTooltipModifier(IGameInteropProvider interop, Configuration.Configuration config, IPluginLog log)
    {
        _config = config;
        _log = log;
        Rebuild();

        try
        {
            _hook = interop.HookFromSignature<GenerateItemTooltipDelegate>(GenerateItemTooltipSignature,
                GenerateItemTooltipDetour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: failed to hook item tooltip generation; tooltips will be left " +
                           "unmodified. The game signature may have changed in a patch.");
        }
    }

    /// <summary>True when the signature resolved and the string-blanking path is live.</summary>
    public bool IsActive => _hook is not null;

    /// <summary>Recompute which string-array indices to blank from the current config. Call after any change.</summary>
    public void Rebuild()
    {
        _hiddenIndices = TooltipFieldMap.IndicesFor(_config.HiddenSections);
    }

    /// <summary>Request a full field dump on the next item hover (dev aid for mapping tooltip lines).</summary>
    public void RequestDump()
    {
        _dumpRequested = true;
    }

    private nint GenerateItemTooltipDetour(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData)
    {
        // Let the game fully populate the tooltip arrays first, then trim what we don't want.
        // (_hook is guaranteed non-null here: the detour can only fire after HookFromSignature returned
        // and assigned it, then Enable() was called.)
        var ret = _hook!.Original(addonItemDetail, numberArrayData, stringArrayData);

        // A null array would AV on dereference below — and an AV cannot be caught — so bail before the
        // try, still returning the game's own result untouched.
        if (stringArrayData is null) return ret;

        try
        {
            if (_dumpRequested)
            {
                _dumpRequested = false;
                DumpFields(stringArrayData);
            }

            if (_config.Enabled)
            {
                var indices = _hiddenIndices; // atomic snapshot; the array is replaced wholesale, never mutated in place
                foreach (var index in indices)
                    BlankField(stringArrayData, index);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error while modifying item tooltip (left as-is).");
        }

        return ret;
    }

    /// <summary>Overwrite a tooltip line with an empty string so the addon collapses it.</summary>
    private static void BlankField(StringArrayData* sad, int index)
    {
        if (sad is null) return;
        if (index < 0 || index >= sad->AtkArrayData.Size) return;

        byte empty = 0;
        // managed: true  -> the game copies the text into UI-space memory, so &empty need not outlive the
        //                   call (no dangling pointer, unlike SetValueForced).
        // suppressUpdates: false -> flag the addon to refresh from the changed array.
        sad->SetValue(index, &empty, readBeforeWrite: false, managed: true, suppressUpdates: false);
    }

    private void DumpFields(StringArrayData* sad)
    {
        if (sad is null) return;
        var size = sad->AtkArrayData.Size;
        _log.Information($"BetterTips: ItemDetail tooltip dump ({size} fields):");
        for (var i = 0; i < size; i++)
        {
            var span = sad->StringArray[i].AsSpan();
            if (span.IsEmpty) continue;

            var text = SeString.Parse(span).TextValue;
            if (string.IsNullOrEmpty(text)) continue;

            _log.Information($"  [{i}] {text.Replace("\n", "\\n")}");
        }
    }

    public void Dispose()
    {
        // Runs on the framework thread, same as the detour, so no in-flight call can race this.
        try
        {
            _hook?.Disable();
            _hook?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error disposing the tooltip hook.");
        }
    }
}
