using BetterTips.Tooltips;
using BetterTips.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BetterTips;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/bettertips";
    private const string CommandAlias = "/btips";

    private readonly ICommandManager _commandManager;
    private readonly ConfigWindow _configWindow;
    private readonly IPluginLog _log;
    private readonly ItemTooltipNodeController _nodeController;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ItemTooltipModifier _stringHook;
    private readonly WindowSystem _windowSystem;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interopProvider,
        IAddonLifecycle addonLifecycle,
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

        // Primary path: signature-free node-group hiding via IAddonLifecycle. Always active.
        _nodeController = new ItemTooltipNodeController(addonLifecycle, config, log);
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
                _nodeController.Rebuild();
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
            HelpMessage = "Open the BetterTips settings window. \"/bettertips dump\" logs the hovered item's tooltip fields."
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
        _nodeController.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            // The dump reads the tooltip string array, so it needs the signature hook to be live.
            if (_stringHook.IsActive)
                _stringHook.RequestDump();
            else
                _log.Information("BetterTips: dump needs the string-array hook, which isn't active on this game version.");
            _log.Information("BetterTips: hover an item to dump its tooltip fields to the log (/xllog).");
            return;
        }

        _configWindow.Toggle();
    }

    private void OpenConfigUi()
    {
        _configWindow.Toggle();
    }
}
