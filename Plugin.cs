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

    /// <summary>
    ///     v2→v3 migration table: the old block-level <see cref="TooltipSection" /> hides and the
    ///     <see cref="LayoutSection" /> block(s) each now corresponds to. Only these (whole-block) sections
    ///     move into <see cref="Configuration.Configuration.HiddenLayoutSections" />; the finer detail hides
    ///     (item category, extract flags, control hints, Advanced Melding) stay in <c>HiddenSections</c>.
    /// </summary>
    private static readonly (TooltipSection Section, LayoutSection[] Layouts)[] BlockHideMigration =
    [
        (TooltipSection.EquipRestriction, [LayoutSection.ItemLevelClassJob]),
        (TooltipSection.Stats, [LayoutSection.DamageDefense, LayoutSection.AttributeBonuses]),
        (TooltipSection.DurabilitySpiritbondRepair, [LayoutSection.CraftingRepairs]),
        (TooltipSection.MateriaMelding, [LayoutSection.Materia]),
        (TooltipSection.VendorMarket, [LayoutSection.VendorMarket]),
        (TooltipSection.Description, [LayoutSection.Description])
    ];

    private readonly ICommandManager _commandManager;
    private readonly ConfigWindow _configWindow;
    private readonly TooltipControlWindow _controlWindow;
    private readonly TooltipPreviewWindow _previewWindow;
    private readonly GearSetBlockProvider _gearSet;
    private readonly UnifiedHeaderBlockProvider _unifiedHeader;
    private readonly UnifiedBonusesBlockProvider _unifiedBonuses;
    private readonly GlamourBlockProvider _glamour;
    private readonly ConditionBlockProvider _condition;
    private readonly IPluginLog _log;
    private readonly TooltipRelayoutController _relayout;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ItemTooltipModifier _stringHook;
    private readonly WindowSystem _windowSystem;
    private readonly IClientState _clientState;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interopProvider,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _clientState = clientState;
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

        // v3 introduces the visual editor's per-block removal (HiddenLayoutSections). Block-level hides used
        // to live as TooltipSection entries; translate the user's old block hides into the new collection so
        // the catalog is the single source of truth, then drop them from HiddenSections (which now holds only
        // the finer detail hides). Rebuild HiddenLayoutSections from scratch rather than merging into its
        // fresh-install default ([CraftingRepairs]) — a user who deliberately un-hid Crafting & Repairs must
        // not be silently re-hidden by that default. Idempotent: fresh/corrupt configs start at v3, so this
        // is skipped and the initializer defaults stand.
        if (config.Version < 3)
        {
            config.HiddenLayoutSections = new HashSet<LayoutSection>();
            foreach (var (section, layouts) in BlockHideMigration)
                if (config.HiddenSections.Remove(section))
                    foreach (var layout in layouts)
                        config.HiddenLayoutSections.Add(layout);
            config.Version = 3;
            try
            {
                config.Save(pluginInterface);
            }
            catch (Exception ex)
            {
                log.Error(ex, "BetterTips: failed to persist v3 config migration.");
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

        // The block providers are driven by the relayout controller (so they lay out as part of the single
        // pass), so they must exist first. The gear-set block reads its lookup from a shared GearSetIndex; the
        // unified-header block reads the hovered item's static data from Lumina.
        // The glamoured-appearance name isn't rendered to any node, so the string hook reads it from the
        // tooltip's string array into this shared carrier, which the glamour block then renders.
        var glamourSource = new GlamourSource();

        _gearSet = new GearSetBlockProvider(addonLifecycle, gameGui, config, new GearSetIndex(), log);
        _unifiedHeader = new UnifiedHeaderBlockProvider(addonLifecycle, gameGui, dataManager, objectTable, config, log);
        _unifiedBonuses = new UnifiedBonusesBlockProvider(addonLifecycle, gameGui, dataManager, config, log);
        _glamour = new GlamourBlockProvider(addonLifecycle, gameGui, dataManager, glamourSource, config, log);
        _condition = new ConditionBlockProvider(addonLifecycle, gameGui, dataManager, config, log);

        // Primary path: the signature-free single-pass relayout (hide + reorder + gear-set + unified header +
        // unified bonuses/materia + glamour + condition).
        _relayout = new TooltipRelayoutController(addonLifecycle, gameGui, config, log, _gearSet, _unifiedHeader, _unifiedBonuses, _glamour, _condition);
        // Fallback/enhancement: a signature hook that blanks text-only lines for the cleanest collapse, and
        // snapshots the glamour name for the glamour block. Only active if the signature resolves; otherwise
        // it self-disables to a harmless no-op (the glamour name is then unavailable, but dyes still scrape).
        _stringHook = new ItemTooltipModifier(interopProvider, config, glamourSource, log);

        // Persisting + rebuilding both paths is funneled through one guarded callback so a save failure
        // (e.g. disk error) can never escape into the UI draw loop.
        // Persisting + rebuilding the tooltip paths is funneled through one guarded callback, shared by both
        // editor surfaces, so a save failure (e.g. disk error) can never escape into a draw/click loop.
        void OnChanged()
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
        }

        // Primary surface: the native (KamiToolKit) visual editor, split into two windows — a standalone
        // tooltip preview (rendered with the game's own node types, filled from a representative item) and a
        // control window with the Enable switch + add/remove catalog that drives it. Nodes are created on Open.
        var sample = SampleItemData.Build(dataManager, log);
        _previewWindow = new TooltipPreviewWindow(config, sample, OnChanged, log)
        {
            InternalName = "BetterTipsPreview",
            Title = "BetterTips — Preview",
            Size = new Vector2(400f, 780f)
        };
        _controlWindow = new TooltipControlWindow(config, OnChanged, _previewWindow, log)
        {
            InternalName = "BetterTipsControls",
            Title = "BetterTips",
            Size = new Vector2(300f, 580f)
        };

        // Fallback surface (/btips classic): the ImGui settings window — insurance if the native window has
        // trouble on a given patch. Drives the same config via the same callback.
        _configWindow = new ConfigWindow(config, OnChanged);
        _windowSystem = new WindowSystem("BetterTips");
        _windowSystem.AddWindow(_configWindow);

        pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the visual editor. \"dump\"/\"dumpnodes\"/\"watch\" log tooltip internals; \"classic\" opens the ImGui settings."
        });
        _commandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /bettertips."
        });

        // Close the native editor windows on logout so KamiToolKit can finalize them while the game is still
        // alive. Left open, a window's deferred finalize runs through a torn-down hook during the game's exit
        // teardown and crashes in native code (see the native-window-exit-crash notes).
        _clientState.Logout += OnLogout;

        log.Information($"BetterTips loaded. Node path: active. String path: " +
                        $"{(_stringHook.IsActive ? "active" : "unavailable (signature not found)")}.");
    }

    /// <summary>Close the native editor windows when the player logs out (incl. the Exit Game path), so their
    /// KamiToolKit addons finalize while the game is alive rather than during the crash-prone exit teardown.</summary>
    private void OnLogout(int type, int code)
    {
        try
        {
            _controlWindow.Close();
            _previewWindow.Close();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error closing editor windows on logout.");
        }
    }

    public void Dispose()
    {
        _clientState.Logout -= OnLogout;
        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(CommandAlias);
        _pluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;

        _stringHook.Dispose();
        // Relayout controller first (stops driving the block), then the provider (frees its nodes while the
        // tooltip may still be alive), then the native editor window, then KamiToolKit last — every owner of
        // KTK nodes is torn down before the library that backs them.
        _relayout.Dispose();
        _gearSet.Dispose();
        _unifiedHeader.Dispose();
        _unifiedBonuses.Dispose();
        _glamour.Dispose();
        _condition.Dispose();
        _controlWindow.Dispose();
        _previewWindow.Dispose();
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

            case "watch":
                // Per-frame geometry watch: logs each ordered block's live Y/Height/parent/in-plan once a
                // second so a layout problem can be read off /xllog. Toggle off when done.
                var on = _relayout.ToggleWatch();
                _log.Information($"BetterTips: layout watch {(on ? "ON — hover an item; check /xllog" : "OFF")}.");
                return;

            case "classic":
                // The ImGui fallback settings window.
                _configWindow.Toggle();
                return;

            default:
                _controlWindow.Toggle();
                return;
        }
    }

    private void OpenConfigUi()
    {
        _controlWindow.Toggle();
    }
}
