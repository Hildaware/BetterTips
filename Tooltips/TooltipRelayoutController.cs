using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     The single, signature-free relayout engine for the <c>ItemDetail</c> tooltip. Listens to Dalamud's
///     <see cref="IAddonLifecycle" /> and lays out the tooltip in <b>one absolute pass</b>: it hides the
///     disabled sections, stacks the visible content (plus the custom "Gear Sets" block) in the user's
///     order, and resizes the tooltip to fit.
///     <para>
///         <b>Why one absolute pass.</b> The game does <em>not</em> fully re-lay-out the tooltip on every
///         update, so our edits persist between updates — any <em>relative</em> positioning (read current
///         <c>Y</c>, add a delta) would accumulate and drift. This pass instead computes every position
///         <em>absolutely</em> from a stable anchor (the bottom of the fixed header, which we never touch)
///         and sets every size to an absolute target, so running it any number of times converges to the
///         same layout. It never moves the window (<see cref="AtkUnitBase" /> position) — the tooltip grows
///         and shrinks from the game's natural top-left — which removes positional jitter.
///     </para>
///     <para>
///         <b>Compute vs. enforce.</b> The game periodically re-runs its <em>own</em> natural layout for the
///         tooltip (every several seconds, even on a static hover), resetting positions/visibility/size. If
///         we only re-applied on the update events, that natural layout would flash for a moment before we
///         caught it. So the expensive decisions (what to hide, the order, measuring the gear block) run on
///         the update events (<see cref="Recompute" />) and are cached as a plan; a cheap re-assert of that
///         plan (<see cref="ApplyPlan" />) runs <b>every frame</b> on <c>PreDraw</c>, snapping any reset back
///         before it's drawn. The per-frame path does no text reads and no allocation, and only writes when a
///         value actually differs from its target.
///     </para>
///     <para>
///         Crash-safety: everything runs on the framework thread (same as <see cref="Dispose" />), and every
///         native access is null/bounds-checked first. An access violation can't be caught, so these checks
///         are what keep the client alive.
///     </para>
/// </summary>
public sealed unsafe class TooltipRelayoutController : IDisposable
{
    private const string AddonName = "ItemDetail";

    // Spacing constants (cosmetic; confirm/tune against a live "/btips dumpnodes"). Getting these a few px
    // off only changes margins, never correctness.
    private const float ControlGapTop = 4f;  // gap between the last content block and the control-hints row
    private const float BottomPadding = 8f;  // gap below the control row to the window's bottom edge

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    // Owns/draws BetterTips' added "Gear Sets" block. Driven as just another section in this layout pass.
    private readonly GearSetBlockProvider _gearSet;

    // Owns/draws the "Unified item header" block (the UnifiedItemHeader enhancement). When active it replaces
    // the native header name/icon + the Item Level (#62) and Damage/Defense (#36) blocks, and the content is
    // re-anchored below it instead of below the header.
    private readonly UnifiedHeaderBlockProvider _unified;

    // Owns/draws the "Unified bonuses & materia" block (the UnifiedBonusesMateria enhancement). When active it
    // replaces the native Bonuses (#97) and Materia (#93) blocks, laid out at the Bonuses slot in the order.
    private readonly UnifiedBonusesBlockProvider _bonuses;

    // Owns/draws BetterTips' "Glamour" block (appearance name + dyes). Laid out at the Glamour slot in the
    // order; when it shows, the native description block (#40) is hidden if it's printing dye channels.
    private readonly GlamourBlockProvider _glamour;

    // Owns/draws BetterTips' "Condition" block (durability + spiritbond + sell price). Laid out at the
    // Condition slot in the order; when it shows, the native durability/spiritbond gauge (#7) is hidden.
    private readonly ConditionBlockProvider _condition;

    // Owns/draws BetterTips' Enhanced-only "Description" block (Lumina lore text, headerless). Laid out at the
    // EnhancedDescription slot (just above Glamour); the native description block (#40) is hidden in Enhanced
    // mode regardless (it's in EnhancedHiddenBlockIds).
    private readonly DescriptionBlockProvider _description;

    // Recomputed on config change; each replaced as a whole reference (atomic) so a callback never sees a
    // half-mutated collection.
    private ItemDetailGroup[] _hiddenGroups = [];
    private uint[] _hiddenBlocks = [];
    private uint[] _hiddenNodeIds = [];
    // Blocks hidden only when their visible text matches a phrase (e.g. "Advanced Melding Forbidden").
    private (uint Block, string Text)[] _conditionalBlocks = [];

    // The user's content-section order (deduped, all sections present). While it equals the default the
    // reorder is a no-op, but hides/gear-sets still drive the pass.
    private LayoutSection[] _sectionOrder = [];
    private bool _reorderActive;

    // The cached layout plan (built by Recompute, re-asserted by ApplyPlan every frame). Pure data — node
    // ids and target positions, no pointers or text — so re-applying it is cheap and allocation-free.
    private bool _hasPlan;
    private float _planTotalHeight;
    private float _planControlY = float.NaN;          // NaN → control not placed (hidden/absent)
    private bool _planShowGear;
    private float _planGearY;
    private bool _planShowUnified;
    private float _planUnifiedY;
    private bool _planShowBonuses;
    private float _planBonusesY;
    private bool _planShowGlamour;
    private float _planGlamourY;
    private bool _planShowCondition;
    private float _planConditionY;
    private bool _planShowDescription;
    private float _planDescriptionY;
    private readonly List<ItemDetailGroup> _planGroups = [];   // named groups to keep hidden
    private readonly List<uint> _planHideIds = [];             // block/sub-node ids to keep hidden (incl. conditional)
    private readonly List<(uint Id, float Y)> _planBlocks = []; // native content blocks → absolute target Y

    // The native block ids included in the previous successful layout. Used to keep a section's slot stable
    // when the game momentarily blanks its text mid-refresh: a block that was present and is still *visible*
    // stays included even if its text reads blank this frame, so the layout doesn't collapse (and the
    // gear-set block doesn't jump to the top) for a frame. A genuine empty shell is never in this set
    // (its header text is hidden, so it was never included), so it stays excluded.
    private readonly HashSet<uint> _prevIncluded = [];

    // Node ids / named groups we hid and applied to the live addon last pass. At the start of the next
    // SAME-item recompute (and on disable) we re-show them, then re-apply only what the current config wants —
    // so a removed hide / toggled-off enhancement restores immediately, even while statically hovering (the
    // game otherwise only re-shows on its own next refresh). Cleared WITHOUT re-showing on an item change,
    // where the new item's refresh governs natural visibility.
    private readonly HashSet<uint> _appliedHideIds = [];
    private readonly List<ItemDetailGroup> _appliedGroups = [];

    // Set when the config changes (Rebuild), consumed on the next PreDraw to recompute against the live
    // tooltip — so a toggle takes effect on the open tooltip immediately, instead of only after the game's
    // next refresh (which never comes while statically hovering the same item).
    private bool _recomputePending;

    // The raw hovered-item value last seen; a change resets the sticky/transient state for the new item.
    private ulong _lastHovered = ulong.MaxValue;
    // Consecutive transient (mid-refresh) frames we've skipped recomputing; bounded so a real change lands.
    private int _transientSkips;
    private const int TransientSkipLimit = 30;

    // Set by "/btips dumpnodes"; on the next addon update we log the node tree, then clear it.
    private volatile bool _nodeDumpRequested;

    // "/btips watch": when on, log each ordered block's live geometry (id/parent/Y/Height/visible/in-plan/
    // has-text) once a second from the per-frame pass, so a layout problem can be read off /xllog directly.
    private volatile bool _watch;
    private int _watchFrame;

    public TooltipRelayoutController(IAddonLifecycle addonLifecycle, IGameGui gameGui,
        Configuration.Configuration config, IPluginLog log, GearSetBlockProvider gearSet,
        UnifiedHeaderBlockProvider unified, UnifiedBonusesBlockProvider bonuses, GlamourBlockProvider glamour,
        ConditionBlockProvider condition, DescriptionBlockProvider description)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _config = config;
        _log = log;
        _gearSet = gearSet;
        _unified = unified;
        _bonuses = bonuses;
        _glamour = glamour;
        _condition = condition;
        _description = description;
        Rebuild();

        // Compute the plan when the game updates the tooltip's content...
        _addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnUpdateEvent);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnUpdateEvent);
        // ...and re-assert it every frame so the game's periodic natural relayout can't flash before we fix it.
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
    }

    /// <summary>Recompute the hide sets and section order from the current config. Call after any change.</summary>
    public void Rebuild()
    {
        if (_config.EnhancedMode)
            RebuildEnhanced();
        else
            RebuildModifier();

        // Force a fresh plan on the next update (the old one may target now-shown/now-hidden sections), and
        // recompute against the live tooltip on the next frame so a config change applies immediately.
        _hasPlan = false;
        _prevIncluded.Clear();
        _recomputePending = true;
    }

    /// <summary>
    ///     The Enhanced tooltip: disregard the structure config entirely and impose a fixed layout — hide every
    ///     native content block (<see cref="TooltipLayout.EnhancedHiddenBlockIds" />) and drive the five custom
    ///     sections in <see cref="TooltipLayout.EnhancedBodyOrder" /> (the unified header anchors the top, the
    ///     other four stack below). The header's binding line and storage icons survive because they're not in
    ///     the hidden set; the unified header hides the native name/icon/category itself, only when it shows.
    /// </summary>
    private void RebuildEnhanced()
    {
        _hiddenGroups = [];
        _hiddenNodeIds = [];
        _conditionalBlocks = [];
        _hiddenBlocks = TooltipLayout.EnhancedHiddenBlockIds;
        _sectionOrder = TooltipLayout.EnhancedBodyOrder;
        _reorderActive = true; // not the default order; the idle short-circuit must not fire
    }

    /// <summary>The modifier tooltip: the structure config (hides + user order) governs.</summary>
    private void RebuildModifier()
    {
        _hiddenGroups = TooltipFieldMap.GroupsFor(_config.HiddenSections);
        _hiddenNodeIds = TooltipFieldMap.NodeIdsFor(_config.HiddenSections);
        _conditionalBlocks = TooltipFieldMap.TextConditionalBlocksFor(_config.HiddenSections);

        // Hidden blocks come from two sources: the finer TooltipSection detail hides (still in HiddenSections)
        // and the whole movable blocks the user removed in the visual editor (HiddenLayoutSections). Both are
        // top-level block ids hidden the same way — once hidden, the order walk skips them (Visible==0) and
        // ApplyPlan keeps them hidden per-frame. Gear Sets has no native block, so it's gated by ShowGearSets,
        // not here. The Unified item header's hides (#36/#62 + the header sub-nodes) are NOT here — they're
        // applied per-item in Recompute, only when the block actually shows for the hovered item.
        var hiddenBlocks = new List<uint>(TooltipFieldMap.BlocksFor(_config.HiddenSections));
        if (_config.HiddenLayoutSections is not null)
            foreach (var section in _config.HiddenLayoutSections)
                if (TooltipLayout.Find(section) is { } info)
                    hiddenBlocks.AddRange(info.BlockIds);
        _hiddenBlocks = hiddenBlocks.Distinct().ToArray();

        // Take the saved order, de-duplicated (a doubled entry would stack the same block twice), then
        // append any sections it's missing (partial/old config, or a newly added section).
        var order = new List<LayoutSection>();
        if (_config.SectionOrder is not null)
            foreach (var id in _config.SectionOrder)
                if (!order.Contains(id))
                    order.Add(id);
        foreach (var id in TooltipLayout.DefaultOrder)
            if (!order.Contains(id))
                order.Add(id);
        _sectionOrder = order.ToArray();

        _reorderActive = !_sectionOrder.SequenceEqual(TooltipLayout.DefaultOrder);
    }

    /// <summary>Request a node-tree dump of the ItemDetail addon on the next update (dev aid).</summary>
    public void RequestNodeDump()
    {
        _nodeDumpRequested = true;
    }

    /// <summary>Toggle the per-frame geometry watch (dev aid). Returns the new state.</summary>
    public bool ToggleWatch()
    {
        _watch = !_watch;
        _watchFrame = 0;
        return _watch;
    }

    // Content-change events: recompute the plan (and apply it).
    private void OnUpdateEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AddonItemDetail*)args.Addon.Address;
            if (addon is null) return;

            _recomputePending = false; // this update covers the pending recompute

            if (_nodeDumpRequested)
            {
                _nodeDumpRequested = false;
                DumpNodeTree(addon);
            }

            Recompute(addon);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error computing ItemDetail relayout (tooltip left as-is).");
        }
    }

    // Per-frame: cheaply re-assert the cached plan so a periodic game relayout can't flash before we fix it.
    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AddonItemDetail*)args.Addon.Address;
            if (addon is null) return;

            // A config change asked for a fresh layout against the live tooltip (no game refresh will come
            // while statically hovering the same item) — do it before applying/enforcing the plan.
            if (_recomputePending)
            {
                _recomputePending = false;
                Recompute(addon);
            }

            if (_hasPlan) ApplyPlan(addon);
            if (_watch) LogWatch(addon);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error enforcing ItemDetail relayout (tooltip left as-is).");
        }
    }

    /// <summary>
    ///     The expensive pass: decide what to hide, the order, and the gear-set block, compute every block's
    ///     absolute target position, apply it, and cache the result as a plan for <see cref="ApplyPlan" />.
    /// </summary>
    private void Recompute(AddonItemDetail* addon)
    {
        var root = addon->RootNode;
        if (!_config.Enabled || root is null)
        {
            // Put the tooltip back to its natural state (re-show what we hid) before going dormant — but only
            // when the root is valid; a null root means the addon is tearing down.
            if (root is not null) RestorePrevious(addon);
            Discard();
            _gearSet.Hide();
            _unified.Hide();
            _bonuses.Hide();
            _glamour.Hide();
            _condition.Hide();
            _description.Hide();
            return;
        }

        var rootId = root->NodeId;

        // Reset stickiness when the hovered item changes, so a new item doesn't inherit the previous one's
        // expected-section set (which would make the transient-refresh guard below misfire on it).
        var hovered = _gameGui.HoveredItem;
        if (hovered != _lastHovered)
        {
            _lastHovered = hovered;
            _prevIncluded.Clear();
            _transientSkips = 0;
            // New item: its own refresh governs natural visibility, so drop our applied-hide tracking WITHOUT
            // re-showing (re-showing could fight the game's per-item hiding — e.g. an empty Damage block on a
            // non-gear item).
            _appliedHideIds.Clear();
            _appliedGroups.Clear();
        }

        // Mid-refresh guard. The game periodically blanks every section's text for a frame or two while it
        // rebuilds the tooltip; recomputing then would stack the layout from a transient (shorter) state and
        // make the gear-set block jump. If we already have a good plan and a previously-shown section is
        // momentarily missing its content, keep the last good plan untouched (ApplyPlan keeps enforcing it).
        // Bounded by TransientSkipLimit so a genuine same-item change still lands.
        if (_hasPlan && _transientSkips < TransientSkipLimit && IsTransientRefresh(addon))
        {
            _transientSkips++;
            return;
        }

        _transientSkips = 0;

        _hasPlan = false;
        _planGroups.Clear();
        _planHideIds.Clear();
        _planBlocks.Clear();
        _planShowGear = false;
        _planShowUnified = false;
        _planShowBonuses = false;
        _planShowGlamour = false;
        _planShowCondition = false;
        _planShowDescription = false;
        _planControlY = float.NaN;

        // Build + measure our added blocks first; they leave themselves HIDDEN, so the scans below ignore them.
        // The bonuses block scrapes the native materia block (#93), the glamour block scrapes the description
        // block (#40), and the condition block scrapes the durability/spiritbond gauge (#7), so all must
        // measure BEFORE the hides below. (The description block reads Lumina, so order doesn't matter for it.)
        var gearWants = _gearSet.TryMeasure(addon, root->Width, out var gearHeight);
        var unifiedWants = _unified.TryMeasure(addon, root->Width, out var unifiedHeight);
        var bonusesWants = _bonuses.TryMeasure(addon, root->Width, out var bonusesHeight);
        var glamourWants = _glamour.TryMeasure(addon, root->Width, out var glamourHeight);
        var conditionWants = _condition.TryMeasure(addon, root->Width, out var conditionHeight);
        var descriptionWants = _description.TryMeasure(addon, root->Width, out var descriptionHeight);

        var hasHides = _hiddenGroups.Length > 0 || _hiddenBlocks.Length > 0 ||
                       _hiddenNodeIds.Length > 0 || _conditionalBlocks.Length > 0;

        // Do no harm when idle: nothing configured to change → leave the game's layout pristine (no plan).
        // Restore anything a prior (now-removed) customization left applied to this same item first.
        if (!hasHides && !_reorderActive && !gearWants && !unifiedWants && !bonusesWants && !glamourWants &&
            !conditionWants && !descriptionWants)
        {
            RestorePrevious(addon);
            _prevIncluded.Clear();
            return;
        }

        // Reset our previous modifications to the game's natural state before re-deciding this pass. On a
        // SAME-item recompute this re-shows what we hid / un-moves the gauge so this pass starts from the
        // game's real layout (a no-op on an item change, where the trackers were already cleared above).
        RestorePrevious(addon);

        var frame = FindFrame(addon, rootId);

        // The anchor: normally the bottom of the fixed header. When the Unified item header is active it
        // replaces the header, so the rest of the content anchors below OUR block instead — placed just under
        // the preserved binding/untradable line.
        float anchorTop;
        if (unifiedWants)
        {
            var header = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            var headerY = header is not null ? (float)header->Y : 0f;
            var binding = addon->GetNodeById(TooltipLayout.BindingLineBlockId);
            var blockTop = binding is not null && (binding->NodeFlags & NodeFlags.Visible) != 0
                ? binding->Y + binding->Height + 4f
                : 6f;

            _unified.PlaceAt(blockTop);
            _unified.Show();
            _planShowUnified = true;
            _planUnifiedY = blockTop;
            anchorTop = headerY + blockTop + unifiedHeight;

            // The durability/spiritbond gauge bars (#7) are hidden below — the redesigned header has no place
            // for them and the Condition section is their new home — so there's nothing to position here.
        }
        else
        {
            _unified.Hide();
            anchorTop = ComputeAnchorTop(addon, rootId, frame);
            if (anchorTop < 0f)
                return; // can't resolve a stable anchor (UI patch shifted ids) → bail, no plan
        }

        // --- Hides (apply now, and record so the per-frame pass can keep them hidden). ---
        foreach (var group in _hiddenGroups)
        {
            var node = ResolveGroupNode(addon, group);
            if (node is not null) node->ToggleVisibility(false);
            _planGroups.Add(group);
        }

        foreach (var nodeId in _hiddenNodeIds)
        {
            var node = addon->GetNodeById(nodeId);
            if (node is not null) node->ToggleVisibility(false);
            _planHideIds.Add(nodeId);
        }

        foreach (var id in _hiddenBlocks)
        {
            var node = addon->GetNodeById(id);
            if (node is not null) node->ToggleVisibility(false);
            _planHideIds.Add(id);
        }

        foreach (var (block, phrase) in _conditionalBlocks)
        {
            if (!BlockContainsText(addon, block, phrase)) continue;
            var node = addon->GetNodeById(block);
            if (node is not null) node->ToggleVisibility(false);
            _planHideIds.Add(block);
        }

        // Always suppress the crafter-signature line while it shows the async "Obtaining signature…" placeholder
        // (before the game resolves the crafter's name) — otherwise the order walk would stack that placeholder
        // text. Once the name resolves the game refreshes the tooltip, this re-runs with the placeholder gone,
        // and the line is included normally.
        if (BlockContainsText(addon, TooltipLayout.CrafterSignatureBlockId, TooltipLayout.SignaturePlaceholder))
        {
            var node = addon->GetNodeById(TooltipLayout.CrafterSignatureBlockId);
            if (node is not null) node->ToggleVisibility(false);
            _planHideIds.Add(TooltipLayout.CrafterSignatureBlockId);
        }

        // The Unified item header: hide the native header name/icon/category/quantity (#32/#33/#34/#35/#24)
        // and the now-folded Damage/Defense (#36) + Item Level (#62) blocks — only when our block is actually
        // showing (so a non-equippable hover never loses its native header). The binding line (#20) is left.
        if (unifiedWants)
        {
            foreach (var id in TooltipLayout.UnifiedHeaderHiddenNodeIds)
            {
                var node = addon->GetNodeById(id);
                if (node is not null) node->ToggleVisibility(false);
                _planHideIds.Add(id);
            }

            foreach (var section in (ReadOnlySpan<LayoutSection>)[LayoutSection.DamageDefense, LayoutSection.ItemLevelClassJob])
                if (TooltipLayout.Find(section) is { } info)
                    foreach (var id in info.BlockIds)
                    {
                        var node = addon->GetNodeById(id);
                        if (node is not null) node->ToggleVisibility(false);
                        _planHideIds.Add(id);
                    }
        }

        // The Unified bonuses & materia block: hide the native Bonuses (#97) and Materia (#93) blocks it folds
        // in — only when our block is actually showing. Our block is then inserted at the Bonuses slot below.
        if (bonusesWants)
            foreach (var section in (ReadOnlySpan<LayoutSection>)[LayoutSection.AttributeBonuses, LayoutSection.Materia])
                if (TooltipLayout.Find(section) is { } info)
                    foreach (var id in info.BlockIds)
                    {
                        var node = addon->GetNodeById(id);
                        if (node is not null) node->ToggleVisibility(false);
                        _planHideIds.Add(id);
                    }

        // The Glamour block folds in the dye channels the game prints into the description block (#40). Hide
        // #40 only when our block shows AND #40 is actually showing dyes (not real lore text) — so a lore
        // description on a glamoured item is preserved while its dyes appear in our section instead.
        if (glamourWants &&
            BlockContainsText(addon, TooltipLayout.DescriptionBlockId, TooltipLayout.DyeLineMarker))
        {
            var node = addon->GetNodeById(TooltipLayout.DescriptionBlockId);
            if (node is not null) node->ToggleVisibility(false);
            _planHideIds.Add(TooltipLayout.DescriptionBlockId);
        }

        // Hide the native durability/spiritbond gauge bars (#7) whenever the Condition section (their new home,
        // already scraped above for its values) or the unified header (no place for them in its redesign) is
        // active. We only ever HIDE the gauge now — never move it. RestorePrevious re-shows it when both go off.
        if (conditionWants || unifiedWants)
        {
            var gauge = addon->GetNodeById(TooltipLayout.GaugeBlockId);
            if (gauge is not null) gauge->ToggleVisibility(false);
            _planHideIds.Add(TooltipLayout.GaugeBlockId);
        }

        // --- Lay out the ordered, effective-visible content absolutely from the header anchor. ---
        var y = anchorTop;
        foreach (var id in _sectionOrder)
        {
            if (TooltipLayout.IsCustom(id))
            {
                if (id == LayoutSection.GearSets && gearWants)
                {
                    _gearSet.PlaceAt(y);
                    _gearSet.Show();
                    _planShowGear = true;
                    _planGearY = y;
                    y += gearHeight;
                }
                else if (id == LayoutSection.Glamour && glamourWants)
                {
                    _glamour.PlaceAt(y);
                    _glamour.Show();
                    _planShowGlamour = true;
                    _planGlamourY = y;
                    y += glamourHeight;
                }
                else if (id == LayoutSection.Condition && conditionWants)
                {
                    _condition.PlaceAt(y);
                    _condition.Show();
                    _planShowCondition = true;
                    _planConditionY = y;
                    y += conditionHeight;
                }
                else if (id == LayoutSection.EnhancedDescription && descriptionWants)
                {
                    _description.PlaceAt(y);
                    _description.Show();
                    _planShowDescription = true;
                    _planDescriptionY = y;
                    y += descriptionHeight;
                }

                continue;
            }

            // When the Unified bonuses & materia block is showing it takes the Bonuses slot (the natives it
            // folds in — #97 and #93 — are hidden above, so the Materia slot collapses to nothing).
            if (bonusesWants && id == LayoutSection.AttributeBonuses)
            {
                _bonuses.PlaceAt(y);
                _bonuses.Show();
                _planShowBonuses = true;
                _planBonusesY = y;
                y += bonusesHeight;
                continue;
            }

            var info = TooltipLayout.Find(id);
            if (info is null) continue;

            foreach (var blockId in info.BlockIds)
            {
                var node = addon->GetNodeById(blockId);
                if (node is null) continue;

                var dup = false;
                foreach (var (existing, _) in _planBlocks)
                    if (existing == blockId) { dup = true; break; }
                if (dup) continue;

                // Exclude a hidden block outright. For a visible block, include it if it has content now,
                // OR if it was included last pass (so a momentary mid-refresh text blank doesn't collapse
                // the layout). A genuine empty shell has a hidden header, so it's neither and stays out.
                if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;
                if (!BlockHasVisibleText(addon, blockId) && !_prevIncluded.Contains(blockId)) continue;

                node->SetYFloat(y);
                _planBlocks.Add((blockId, y));
                y += node->Height;
            }
        }

        // The control-hints row trails the content (unless the user hid it, in which case it's invisible).
        var control = addon->GetNodeById(TooltipLayout.ControlHintsBlockId);
        if (control is not null && (control->NodeFlags & NodeFlags.Visible) != 0)
        {
            y += ControlGapTop;
            control->SetYFloat(y);
            _planControlY = y;
            y += control->Height;
        }

        _planTotalHeight = y + BottomPadding;

        // --- Size once, absolutely. No position change (top-left anchor). ---
        ApplySize(addon, root, rootId, frame);

        // Remember which sections we included, so a transient mid-refresh text blank can't drop them.
        _prevIncluded.Clear();
        foreach (var (id, _) in _planBlocks)
            _prevIncluded.Add(id);

        // Remember what we hid this pass (RestorePrevious cleared the trackers above) so the next same-item
        // recompute — or a disable — can restore it.
        foreach (var id in _planHideIds)
            _appliedHideIds.Add(id);
        _appliedGroups.AddRange(_planGroups);

        _hasPlan = true;
    }

    /// <summary>Drop the cached plan and all sticky state (used on the disabled/no-root paths).</summary>
    private void Discard()
    {
        _hasPlan = false;
        _planGroups.Clear();
        _planHideIds.Clear();
        _planBlocks.Clear();
        _planShowGear = false;
        _planShowUnified = false;
        _planShowBonuses = false;
        _planShowGlamour = false;
        _planShowCondition = false;
        _planShowDescription = false;
        _planControlY = float.NaN;
        _prevIncluded.Clear();
        _appliedHideIds.Clear();
        _appliedGroups.Clear();
    }

    /// <summary>
    ///     Re-show every node/group we hid last pass, then clear the applied-modification tracking. Called at
    ///     the start of a same-item recompute (so the pass starts from the game's natural layout) and on
    ///     disable. Guarded — every native access is null/visibility checked, so a bad pointer can't throw out
    ///     of the framework callback.
    /// </summary>
    private void RestorePrevious(AddonItemDetail* addon)
    {
        foreach (var id in _appliedHideIds)
        {
            var node = addon->GetNodeById(id);
            if (node is not null && (node->NodeFlags & NodeFlags.Visible) == 0)
                node->ToggleVisibility(true);
        }
        _appliedHideIds.Clear();

        foreach (var group in _appliedGroups)
        {
            var node = ResolveGroupNode(addon, group);
            if (node is not null && (node->NodeFlags & NodeFlags.Visible) == 0)
                node->ToggleVisibility(true);
        }
        _appliedGroups.Clear();
    }

    /// <summary>
    ///     True if the tooltip looks mid-refresh: a section that was part of the last good layout is right
    ///     now gone, hidden, or blank. On such a frame we keep the last plan rather than recompute from the
    ///     transient (shorter) state — which is what made the gear-set block jump.
    /// </summary>
    private bool IsTransientRefresh(AddonItemDetail* addon)
    {
        foreach (var id in _prevIncluded)
        {
            var node = addon->GetNodeById(id);
            if (node is null) return true;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) return true;
            if (!BlockHasVisibleText(addon, id)) return true;
        }

        return false;
    }

    /// <summary>
    ///     The cheap per-frame pass: re-assert the cached plan (positions, visibility, size) with guarded
    ///     writes — no text reads, no allocation, and a write only when a value differs from its target. This
    ///     undoes the game's periodic natural relayout before it can be drawn.
    /// </summary>
    private void ApplyPlan(AddonItemDetail* addon)
    {
        var root = addon->RootNode;
        if (root is null) return;
        var rootId = root->NodeId;

        // Re-hide.
        foreach (var group in _planGroups)
        {
            var node = ResolveGroupNode(addon, group);
            if (node is not null && (node->NodeFlags & NodeFlags.Visible) != 0)
                node->ToggleVisibility(false);
        }

        foreach (var id in _planHideIds)
        {
            var node = addon->GetNodeById(id);
            if (node is not null && (node->NodeFlags & NodeFlags.Visible) != 0)
                node->ToggleVisibility(false);
        }

        // Re-place the content blocks.
        foreach (var (id, ty) in _planBlocks)
        {
            var node = addon->GetNodeById(id);
            if (node is null) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) node->ToggleVisibility(true);
            if (Math.Abs(node->Y - ty) > 0.5f) node->SetYFloat(ty);
        }

        // Gear-set block.
        if (_planShowGear)
        {
            _gearSet.PlaceAt(_planGearY);
            _gearSet.Show();
        }
        else
        {
            _gearSet.Hide();
        }

        // Unified item header block.
        if (_planShowUnified)
        {
            _unified.PlaceAt(_planUnifiedY);
            _unified.Show();
        }
        else
        {
            _unified.Hide();
        }

        // Unified bonuses & materia block.
        if (_planShowBonuses)
        {
            _bonuses.PlaceAt(_planBonusesY);
            _bonuses.Show();
        }
        else
        {
            _bonuses.Hide();
        }

        // Glamour block.
        if (_planShowGlamour)
        {
            _glamour.PlaceAt(_planGlamourY);
            _glamour.Show();
        }
        else
        {
            _glamour.Hide();
        }

        // Condition block.
        if (_planShowCondition)
        {
            _condition.PlaceAt(_planConditionY);
            _condition.Show();
        }
        else
        {
            _condition.Hide();
        }

        // Description block (Enhanced-only).
        if (_planShowDescription)
        {
            _description.PlaceAt(_planDescriptionY);
            _description.Show();
        }
        else
        {
            _description.Hide();
        }

        // The durability/spiritbond gauge (#7) is only ever hidden now (when the Condition section or unified
        // header is active), which the _planHideIds re-hide loop above already enforces — nothing to position.

        // Control-hints row.
        if (!float.IsNaN(_planControlY))
        {
            var control = addon->GetNodeById(TooltipLayout.ControlHintsBlockId);
            if (control is not null && Math.Abs(control->Y - _planControlY) > 0.5f)
                control->SetYFloat(_planControlY);
        }

        // Size — only touch it (and rebuild collision) when the game actually reset the height.
        if (root->Height != ClampUShort(_planTotalHeight))
            ApplySize(addon, root, rootId, FindFrame(addon, rootId));
    }

    /// <summary>Set the addon + window-frame height to the plan's total, absolutely, and rebuild collision.</summary>
    private void ApplySize(AddonItemDetail* addon, AtkResNode* root, uint rootId, AtkResNode* frame)
    {
        var addonOld = (float)root->Height;
        var frameOld = frame is not null ? (float)frame->Height : 0f;

        addon->SetSize(root->Width, ClampUShort(_planTotalHeight));
        if (frame is not null)
            SetFrameHeightAbsolute(frame, addonOld, frameOld, _planTotalHeight);
        addon->UpdateCollisionNodeList(false);
    }

    private static AtkResNode* ResolveGroupNode(AddonItemDetail* addon, ItemDetailGroup group) => group switch
    {
        ItemDetailGroup.HeaderStats => addon->HeaderStatsGroup,
        ItemDetailGroup.SpiritbondConditionCrest => addon->SpiritbondConditionCrestGroup,
        ItemDetailGroup.EquipRestriction => addon->EquipRestrictionGroup,
        ItemDetailGroup.Materialize => (AtkResNode*)addon->MaterializeText,
        _ => null
    };

    /// <summary>
    ///     The stable top anchor: the bottom edge of the fixed header block, below which all content is
    ///     stacked. The header is never moved, so this never drifts. Returns <c>-1</c> if no anchor can be
    ///     resolved (caller bails). <paramref name="frame" /> is excluded from the fallback scan.
    /// </summary>
    private static float ComputeAnchorTop(AddonItemDetail* addon, uint rootId, AtkResNode* frame)
    {
        var header = addon->GetNodeById(TooltipLayout.HeaderBlockId);
        if (header is not null && (header->NodeFlags & NodeFlags.Visible) != 0)
            return header->Y + header->Height;

        foreach (var id in TooltipLayout.HeaderFallbackIds)
        {
            var alt = addon->GetNodeById(id);
            if (alt is not null && (alt->NodeFlags & NodeFlags.Visible) != 0)
                return alt->Y + alt->Height;
        }

        // Fallback: the bottom of the topmost visible direct child of root (excluding the frame, collision,
        // and control row). The header is the topmost such child, so content still lands right below it.
        var list = addon->UldManager.NodeList;
        if (list is null) return -1f;
        var nodeCount = addon->UldManager.NodeListCount;
        AtkResNode* top = null;
        for (var i = 0; i < nodeCount; i++)
        {
            var node = list[i];
            if (node is null || node->ParentNode is null || node->ParentNode->NodeId != rootId) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Type == NodeType.Collision) continue;
            if (node == frame || node->NodeId == TooltipLayout.ControlHintsBlockId) continue;
            if (top is null || node->Y < top->Y) top = node;
        }

        return top is null ? -1f : top->Y + top->Height;
    }

    /// <summary>The window frame: the tallest visible non-collision direct child of root, excluding the
    /// header and control rows so they can't be mistaken for it.</summary>
    private static AtkResNode* FindFrame(AddonItemDetail* addon, uint rootId)
    {
        var list = addon->UldManager.NodeList;
        if (list is null) return null;
        var count = addon->UldManager.NodeListCount;
        AtkResNode* frame = null;
        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->ParentNode is null || node->ParentNode->NodeId != rootId) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Type == NodeType.Collision) continue;
            if (node->NodeId == TooltipLayout.ControlHintsBlockId || node->NodeId == TooltipLayout.HeaderBlockId)
                continue;
            if (frame is null || node->Height > frame->Height) frame = node;
        }

        return frame;
    }

    /// <summary>
    ///     True if a visible, non-blank text node currently sits under <paramref name="blockId" /> — i.e. the
    ///     block has real content. A block whose header text the game has hidden (an empty shell, e.g. a
    ///     Materia block with no materia) has none, so it reads false.
    ///     <para>
    ///         <b>Must descend into component nodes.</b> Some blocks render their text inside <em>component</em>
    ///         nodes — the damage/defense stat lines live in <c>DefenseComponentNode</c> etc. — and a
    ///         component's text is in the component's <em>own</em> ULD list, not the addon's flat
    ///         <c>NodeList</c> we scan here. Scanning only the flat list makes such a block read "no text", so
    ///         it gets excluded from the layout as if it were a hollow shell; it then stays at its natural top
    ///         position while the next section is stacked on top of it (the damage/item-level overlap). So we
    ///         also descend into any component child of the block.
    ///     </para>
    /// </summary>
    private static bool BlockHasVisibleText(AddonItemDetail* addon, uint blockId)
    {
        var list = addon->UldManager.NodeList;
        if (list is null) return false;
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;
            // Match the block's own node too, not just descendants: some blocks (the crafter-signature line
            // #4) ARE a bare Text node with no children, so a descendant-only scan reads them as empty.
            if (node->NodeId != blockId && !IsDescendantOf(node, blockId)) continue;

            if (node->Type == NodeType.Text)
            {
                if (TextNodeHasContent((AtkTextNode*)node)) return true;
            }
            else if ((uint)node->Type >= 1000)
            {
                // A component descendant — its text lives in the component's own ULD list, not the flat list.
                if (ComponentHasVisibleText((AtkComponentNode*)node, 0)) return true;
            }
        }

        return false;
    }

    /// <summary>True if the component (or a nested component within it) holds a visible, non-blank text node.
    /// Depth-capped against pathological nesting.</summary>
    private static bool ComponentHasVisibleText(AtkComponentNode* componentNode, int depth)
    {
        if (depth > 4) return false;
        var component = componentNode->Component;
        if (component is null) return false;
        var list = component->UldManager.NodeList;
        if (list is null) return false;
        var count = component->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

            if (node->Type == NodeType.Text)
            {
                if (TextNodeHasContent((AtkTextNode*)node)) return true;
            }
            else if ((uint)node->Type >= 1000)
            {
                if (ComponentHasVisibleText((AtkComponentNode*)node, depth + 1)) return true;
            }
        }

        return false;
    }

    /// <summary>True if a text node currently holds non-whitespace text (guarded — a bad pointer must not
    /// throw out of the framework callback).</summary>
    private static bool TextNodeHasContent(AtkTextNode* textNode)
    {
        string s;
        try { s = textNode->NodeText.ToString(); }
        catch { return false; }
        return !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>
    ///     True if <paramref name="blockId" /> is currently visible and any text node under it contains
    ///     <paramref name="phrase" /> (case-insensitive). Used to hide a shared block only for the content
    ///     we care about (e.g. "Advanced Melding Forbidden").
    /// </summary>
    private static bool BlockContainsText(AddonItemDetail* addon, uint blockId, string phrase)
    {
        var block = addon->GetNodeById(blockId);
        if (block is null) return false;
        if ((block->NodeFlags & NodeFlags.Visible) == 0) return false;

        var list = addon->UldManager.NodeList;
        if (list is null) return false;
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->Type != NodeType.Text) continue;
            if (!IsDescendantOf(node, blockId)) continue;

            string s;
            try { s = ((AtkTextNode*)node)->NodeText.ToString(); }
            catch { continue; }

            if (!string.IsNullOrEmpty(s) && s.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Walks parent links (depth-capped) to test whether <paramref name="node" /> sits under the
    /// block with id <paramref name="ancestorId" />.</summary>
    private static bool IsDescendantOf(AtkResNode* node, uint ancestorId)
    {
        var ancestor = node->ParentNode;
        for (var depth = 0; ancestor is not null && depth < 32; depth++)
        {
            if (ancestor->NodeId == ancestorId) return true;
            ancestor = ancestor->ParentNode;
        }

        return false;
    }

    /// <summary>
    ///     Resize the window frame (and its nine-grid/collision parts) to match <paramref name="totalHeight" />
    ///     absolutely. The frame's inset (the gap between the addon height and the frame height) is read from
    ///     the pre-resize values, which are stable across frames, so this never drifts — the inverse problem
    ///     of a delta-based resize.
    /// </summary>
    private static void SetFrameHeightAbsolute(AtkResNode* frame, float addonOldHeight, float frameOldHeight,
        float totalHeight)
    {
        var inset = addonOldHeight - frameOldHeight;
        var target = totalHeight - inset;
        if (target < 1f) target = 1f;

        // For a window-background component the visible border is a nine-grid (plus a collision part) that
        // must be sized too; keep each child's own inset from the frame.
        if ((uint)frame->Type >= 1000)
        {
            var component = ((AtkComponentNode*)frame)->Component;
            if (component is not null)
            {
                var list = component->UldManager.NodeList;
                var count = component->UldManager.NodeListCount;
                for (var i = 0; i < count; i++)
                {
                    var node = list[i];
                    if (node is null) continue;
                    if (node->Type != NodeType.NineGrid && node->Type != NodeType.Collision) continue;

                    var childTarget = target - (frameOldHeight - node->Height);
                    if (childTarget < 1f) childTarget = 1f;
                    node->SetHeight(ClampUShort(childTarget));
                }
            }
        }

        frame->SetHeight(ClampUShort(target));
    }

    private static ushort ClampUShort(float value)
    {
        if (value < 1f) return 1;
        if (value > ushort.MaxValue) return ushort.MaxValue;
        return (ushort)value;
    }

    /// <summary>
    ///     Dev aid (<c>/btips watch</c>): once a second, log every block in the user's order — whether or not
    ///     the plan currently includes it — with its live <c>Y</c>/<c>Height</c>/parent, visibility, in-plan
    ///     flag, and whether it reads as having text. This is what surfaced the damage/item-level overlap: a
    ///     block reading <c>vis=True inPlan=False hasText=False</c> is being dropped from layout while still
    ///     occupying space. A <c>!!NOT-ROOT-CHILD</c> flag marks a block whose parent isn't root (which would
    ///     break the root-absolute positioning). Runs only while the watch is on.
    /// </summary>
    private void LogWatch(AddonItemDetail* addon)
    {
        if (_watchFrame++ % 60 != 0) return; // ~once per second at 60fps

        var root = addon->RootNode;
        var rootId = root is not null ? root->NodeId : 0;
        var header = addon->GetNodeById(TooltipLayout.HeaderBlockId);
        var anchor = header is not null ? header->Y + header->Height : -1f;

        _log.Information(
            $"BetterTips WATCH: hasPlan={_hasPlan} reorder={_reorderActive} anchor={anchor:0} " +
            $"rootH={(root is not null ? root->Height : 0)} hovered={_lastHovered}");

        foreach (var section in _sectionOrder)
        {
            if (TooltipLayout.IsCustom(section)) continue; // no native node to inspect

            var info = TooltipLayout.Find(section);
            if (info is null) continue;

            foreach (var id in info.BlockIds)
            {
                var node = addon->GetNodeById(id);
                if (node is null)
                {
                    _log.Information($"BetterTips WATCH:   [{section}] #{id} <null>");
                    continue;
                }

                var parentId = node->ParentNode is not null ? node->ParentNode->NodeId : 0;
                var rootChild = parentId == rootId ? "" : " !!NOT-ROOT-CHILD";
                var vis = (node->NodeFlags & NodeFlags.Visible) != 0;
                var inPlan = false;
                foreach (var (pid, _) in _planBlocks)
                    if (pid == id) { inPlan = true; break; }

                _log.Information(
                    $"BetterTips WATCH:   [{section}] #{id} <#{parentId}{rootChild} y={node->Y:0} h={node->Height:0} " +
                    $"vis={vis} inPlan={inPlan} hasText={BlockHasVisibleText(addon, id)}");
            }
        }
    }

    /// <summary>Dev aid (<c>/btips dumpnodes</c>): logs the ItemDetail node list (id / parent / type /
    /// visibility / pos / size, plus text style for Text nodes and texture/nine-grid info for Image and
    /// NineGrid nodes) so block ids, heights, header styling, and the tooltip's <b>background/chrome</b> can
    /// be read off. Recurses one level into component nodes, where window/background nine-grids live.</summary>
    private void DumpNodeTree(AddonItemDetail* addon)
    {
        _log.Information("BetterTips: ItemDetail nodes (#id <#parent type vis pos size; text: fs/align/col/edge;" +
                         " image/ninegrid: parts/tex/offsets):");

        var list = addon->UldManager.NodeList;
        if (list is null) return;

        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count && i < 400; i++)
        {
            var node = list[i];
            if (node is not null)
                LogNode(node, 0);
        }
    }

    private void LogNode(AtkResNode* node, int depth)
    {
        var visible = (node->NodeFlags & NodeFlags.Visible) != 0;
        var parentId = node->ParentNode is not null ? node->ParentNode->NodeId : 0;

        var extra = "";
        if (node->Type == NodeType.Text)
        {
            var t = (AtkTextNode*)node;
            var c = t->TextColor;
            var e = t->EdgeColor;
            var s = "";
            try
            {
                var str = t->NodeText.ToString();
                if (!string.IsNullOrEmpty(str)) s = $" \"{str.Replace("\n", "\\n")}\"";
            }
            catch
            {
                s = " <text?>";
            }

            extra = $" fs={t->FontSize} align={t->AlignmentType} col=#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2} edge=#{e.R:X2}{e.G:X2}{e.B:X2}{e.A:X2}{s}";
        }
        else if (node->Type == NodeType.Image)
        {
            var img = (AtkImageNode*)node;
            extra = DescribeParts(img->PartsList, img->PartId);
        }
        else if (node->Type == NodeType.NineGrid)
        {
            var ng = (AtkNineGridNode*)node;
            extra = DescribeParts(ng->PartsList, ng->PartId) +
                    $" ninegrid(t={ng->TopOffset:0} b={ng->BottomOffset:0} l={ng->LeftOffset:0} r={ng->RightOffset:0})";
        }

        var indent = depth > 0 ? new string(' ', depth * 2) + "> " : "";
        _log.Information(
            $"{indent}#{node->NodeId} <#{parentId} {node->Type} vis={visible} x={node->X:0} y={node->Y:0} w={node->Width:0} h={node->Height:0}{extra}");

        // Recurse one level into component internals — the window/background nine-grid that gives the
        // tooltip its chrome lives inside the frame component, not in the addon's flat node list.
        if ((uint)node->Type >= 1000 && depth < 2)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null)
            {
                var childList = component->UldManager.NodeList;
                var childCount = component->UldManager.NodeListCount;
                for (var i = 0; i < childCount && i < 80; i++)
                    if (childList is not null && childList[i] is not null)
                        LogNode(childList[i], depth + 1);
            }
        }
    }

    /// <summary>
    ///     Describes an image/nine-grid node's parts: the part count, the <b>active</b> part (the one the node
    ///     is currently displaying — <paramref name="activePartId" />) with its texture and atlas rect, plus
    ///     part 0 for reference. The active part's rect (u/v/w/h) is what's needed to reproduce an atlas sprite
    ///     such as a materia socket; the texture is read per-part (atlas parts can each reference their own).
    /// </summary>
    private static string DescribeParts(AtkUldPartsList* partsList, uint activePartId)
    {
        if (partsList is null || partsList->PartCount == 0) return " parts=0";

        var active = activePartId < partsList->PartCount ? activePartId : 0;
        var activePart = &partsList->Parts[active];
        var part0 = &partsList->Parts[0];

        return $" parts={partsList->PartCount} active=#{activePartId} tex=\"{PartTexture(activePart)}\"" +
               $" part{active}(u={activePart->U} v={activePart->V} w={activePart->Width} h={activePart->Height})" +
               $" part0(u={part0->U} v={part0->V} w={part0->Width} h={part0->Height})";
    }

    /// <summary>The texture file backing a single ULD part (atlas parts can each reference their own).</summary>
    private static string PartTexture(AtkUldPart* part)
    {
        var asset = part->UldAsset;
        if (asset is null || asset->AtkTexture.Resource is null ||
            asset->AtkTexture.Resource->TexFileResourceHandle is null)
            return "?";

        try { return asset->AtkTexture.Resource->TexFileResourceHandle->FileName.ToString(); }
        catch { return "?"; }
    }

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnUpdateEvent);
            _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnUpdateEvent);
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error unregistering tooltip relayout listeners.");
        }
    }
}
