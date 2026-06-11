using BetterTips.Tooltips;
using BetterTips.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;

namespace BetterTips;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/bettertips";
    private const string CommandAlias = "/btips";

    private readonly ICommandManager _commandManager;
    private readonly ConfigWindow _configWindow;
    private readonly GearSetBlockProvider _gearSet;
    private readonly IPluginLog _log;
    private readonly TooltipRelayoutController _relayout;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ItemTooltipModifier _stringHook;
    private readonly WindowSystem _windowSystem;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interopProvider,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _log = log;

        Configuration.Configuration config;
        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration.Configuration
                     ?? new Configuration.Configuration();
        }
        catch (Exception ex)
        {
            // A corrupt/incompatible saved config must not stop the plugin from loading.
            log.Error(ex, "BetterTips: failed to load saved config; falling back to defaults.");
            config = new Configuration.Configuration();
        }

        // Migrate older saved configs to the current defaults. v2 adds the "Advanced Melding Forbidden"
        // hide (a new section can't be in a config saved before it existed), so opt existing users in.
        if (config.Version < 2)
        {
            config.HiddenSections.Add(TooltipSection.AdvancedMelding);
            config.Version = 2;
            try
            {
                config.Save(pluginInterface);
            }
            catch (Exception ex)
            {
                log.Error(ex, "BetterTips: failed to persist migrated config.");
            }
        }

        // Normalize the saved section order into a clean permutation of the known sections. An earlier build
        // could persist duplicates (it appended the default order onto an already-populated list, doubling
        // every entry), so rebuild the list: keep the first occurrence of each known section (dedupe + drop
        // any stale/unknown values), then append any genuinely missing sections. Gear Sets is now an
        // order-aware section like any other, so it keeps whatever slot the user gave it (no parking).
        // Idempotent; only persists when the result actually changed.
        var normalizedOrder = new List<LayoutSection>();
        foreach (var section in config.SectionOrder)
            if (TooltipLayout.Find(section) is not null && !normalizedOrder.Contains(section))
                normalizedOrder.Add(section);
        foreach (var section in TooltipLayout.DefaultOrder)
            if (!normalizedOrder.Contains(section))
                normalizedOrder.Add(section);

        if (!normalizedOrder.SequenceEqual(config.SectionOrder))
        {
            config.SectionOrder = normalizedOrder;
            try
            {
                config.Save(pluginInterface);
            }
            catch (Exception ex)
            {
                log.Error(ex, "BetterTips: failed to persist section-order migration.");
            }
        }

        // KamiToolKit backs the "Gear Sets" block's native nodes. Initialize it before any node is created.
        KamiToolKitLibrary.Initialize(pluginInterface, "BetterTips");

        // The gear-set block provider is driven by the relayout controller (so it lays out as part of the
        // single pass), so it must exist first. It reads its lookup from a shared, throttled GearSetIndex.
        _gearSet = new GearSetBlockProvider(addonLifecycle, gameGui, config, new GearSetIndex(), log);

        // Primary path: the signature-free single-pass relayout (hide + reorder + gear-set) via IAddonLifecycle.
        _relayout = new TooltipRelayoutController(addonLifecycle, gameGui, config, log, _gearSet);
        // Fallback/enhancement: a signature hook that blanks text-only lines for the cleanest collapse.
        // Only active if the signature resolves; otherwise it self-disables to a harmless no-op.
        _stringHook = new ItemTooltipModifier(interopProvider, config, log);

        // Persisting + rebuilding both paths is funneled through one guarded callback so a save failure
        // (e.g. disk error) can never escape into the UI draw loop.
        _configWindow = new ConfigWindow(config, () =>
        {
            try
            {
                config.Save(_pluginInterface);
                _relayout.Rebuild();
                _stringHook.Rebuild();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "BetterTips: failed to persist config.");
            }
        });
        _windowSystem = new WindowSystem("BetterTips");
        _windowSystem.AddWindow(_configWindow);

        pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. \"/bettertips dump\" logs tooltip string fields; \"dumpnodes\" logs the node tree."
        });
        _commandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /bettertips."
        });

        log.Information($"BetterTips loaded. Node path: active. String path: " +
                        $"{(_stringHook.IsActive ? "active" : "unavailable (signature not found)")}.");
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(CommandAlias);
        _pluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;

        _stringHook.Dispose();
        // Relayout controller first (stops driving the block), then the provider (frees its nodes while the
        // tooltip may still be alive), then KamiToolKit last.
        _relayout.Dispose();
        _gearSet.Dispose();
        KamiToolKitLibrary.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "dump":
                // Reads the tooltip string array, so it needs the signature hook to be live.
                if (_stringHook.IsActive)
                    _stringHook.RequestDump();
                else
                    _log.Information("BetterTips: dump needs the string-array hook, which isn't active on this game version.");
                _log.Information("BetterTips: hover an item to dump its tooltip string fields to the log (/xllog).");
                return;

            case "dumpnodes":
                // Signature-free: walks the ItemDetail node tree to find leftover static label nodes.
                _relayout.RequestNodeDump();
                _log.Information("BetterTips: hover an item to dump its ItemDetail node tree to the log (/xllog).");
                return;

            default:
                _configWindow.Toggle();
                return;
        }
    }

    private void OpenConfigUi()
    {
        _configWindow.Toggle();
    }
}
