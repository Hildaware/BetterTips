using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own "Gear Sets" content block (a <see cref="TooltipContentBlock" /> header + divider
///     whose body lists one job icon per distinct job whose gear set contains the hovered item — see
///     <see cref="GearSetIndex" />). Unlike the rest of the plugin this <em>adds</em> nodes, so it uses
///     KamiToolKit to create/own the native nodes safely.
///     <para>
///         This is a <b>passive provider</b>: it never positions, resizes, or anchors anything itself.
///         <see cref="TooltipRelayoutController" /> drives it as just another section in the single layout
///         pass — it asks the provider to <see cref="TryMeasure" /> (build + measure, leaving the block
///         hidden), then <see cref="PlaceAt" /> + <see cref="Show" /> at the slot the order dictates. All
///         work runs on the framework thread; every native access is null/bounds-checked because an access
///         violation here would take down the game client.
///     </para>
///     <para>
///         Node lifetime is the delicate part (a teardown bug used to crash on logout): the block is
///         attached to the addon root <b>once</b> and reused for the addon's whole life (never detached and
///         re-attached per frame); it is detached+disposed on <see cref="OnPreFinalize" /> while the addon
///         is still alive, and on plugin <see cref="Dispose" />. We never drop a node reference without
///         disposing it first — see <see cref="EnsureAttached" />.
///     </para>
/// </summary>
public sealed unsafe class GearSetBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // Game icon ids 62100.. are the small class/job symbols (62100 + ClassJob id).
    private const uint JobIconBase = 62100;

    // Job-icon layout inside the block body.
    private const float IconSize = 20f;
    private const float IconGap = 4f;
    private const float LineHeight = 22f;   // height of one icon line (icons centered in it)
    private const float RightMargin = 8f;   // keep icons this far from the right edge before wrapping

    private const int MaxIcons = 24;        // safety cap; an all-class accessory tops out near the job count

    // A little breathing room below the icons so the block doesn't read cramped against the next section /
    // control row when stacked back-to-back (the block itself has no built-in bottom padding).
    private const float BlockBottomPad = 6f;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly Configuration.Configuration _config;
    private readonly GearSetIndex _index;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private readonly List<IconImageNode> _icons = [];

    // The addon our nodes are currently attached to (null = detached).
    private AtkUnitBase* _attachedAddon;

    public GearSetBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui,
        Configuration.Configuration config, GearSetIndex index, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _config = config;
        _index = index;
        _log = log;

        // The tooltip addon can be torn down (e.g. on a zone change or logout). Detach while it's valid.
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>
    ///     Decide whether the block should appear for the current hover and, if so, build/fill it and report
    ///     its height — leaving the block <b>hidden</b> so the caller's layout scan doesn't see it. Returns
    ///     <c>false</c> (and hides the block) when the feature is off, no item is hovered, or the item is in
    ///     no gear set. The caller positions it later via <see cref="PlaceAt" /> + <see cref="Show" />.
    /// </summary>
    public bool TryMeasure(AddonItemDetail* addon, float width, out float height)
    {
        height = 0f;
        try
        {
            // Forced on by the Enhanced tooltip (one of its five sections); otherwise the modifier-mode toggle.
            if (addon is null || !_config.Enabled || !(_config.EnhancedMode || _config.ShowGearSets))
            {
                Hide();
                return false;
            }

            // Which item is the tooltip for? HoveredItem is the reliable source; HQ adds 1,000,000.
            var hovered = _gameGui.HoveredItem;
            if (hovered == 0)
            {
                Hide();
                return false;
            }

            var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);

            _index.EnsureFresh();
            var jobs = _index.JobsForItem(itemId);
            if (jobs is null || jobs.Count == 0)
            {
                Hide();
                return false;
            }

            if (!EnsureAttached(addon))
            {
                Hide();
                return false;
            }

            var block = _block!;

            // Keep the block hidden while we build it so the caller's frame/content scan ignores it.
            Hide();

            // Lay out the job icons inside the block body (block-relative coordinates), wrapping to new
            // lines if a many-job item would overflow the width.
            var bodyLeft = TooltipContentBlock.BodyInsetX;
            var bodyTop = block.BodyTop;
            var availRight = width - RightMargin;

            var x = bodyLeft;
            var y = bodyTop;
            var lines = 1;
            var shown = Math.Min(jobs.Count, MaxIcons);
            for (var j = 0; j < shown; j++)
            {
                if (x + IconSize > availRight && x > bodyLeft)
                {
                    x = bodyLeft;
                    y += LineHeight;
                    lines++;
                }

                var icon = GetOrCreateIcon(j);
                if (icon is null) break;

                icon.IconId = JobIconBase + jobs[j];
                icon.Position = new Vector2(x, y + (LineHeight - IconSize) / 2f);
                icon.Size = new Vector2(IconSize, IconSize);
                icon.IsVisible = true;

                x += IconSize + IconGap;
            }

            for (var j = shown; j < _icons.Count; j++)
                _icons[j].IsVisible = false;

            if (jobs.Count > MaxIcons)
                _log.Debug($"BetterTips: gear-set block capped at {MaxIcons} icons (item is in {jobs.Count} distinct jobs).");

            // Resize sets the block's visual height to exactly the body; report a touch more so the layout
            // leaves a small gap below it (its bottom padding) before the next element.
            height = block.Resize(width, lines * LineHeight) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring gear-set block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> in addon-root coordinates (the block is parented
    /// to the header at y=0, so these are effectively root-absolute).</summary>
    public void PlaceAt(float y)
    {
        if (_block is null) return;
        _block.X = 0;
        _block.Y = y;
    }

    /// <summary>Show the block after it's been measured and placed.</summary>
    public void Show()
    {
        if (_block is not null)
            _block.IsVisible = true;
    }

    /// <summary>Hide the block (adds nothing to the tooltip).</summary>
    public void Hide()
    {
        if (_block is not null)
            _block.IsVisible = false;
    }

    /// <summary>Ensure the content block (and its header) exists and is attached to <paramref name="addon" />.</summary>
    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        // Attached to a different addon we never saw finalize (shouldn't happen — we register PreFinalize
        // for the plugin's lifetime). The old addon is presumed still alive here (a finalize would have
        // nulled _attachedAddon and disposed), so dispose our nodes now — detaching them cleanly — rather
        // than abandoning the reference (which would leave KTK-allocated memory attached to a dying addon
        // and crash when the game later frees it).
        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: gear-set block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { HeaderText = "Gear Sets", IsVisible = false };

            // Attach under the header block, NOT the addon root. The game's content-reflow periodically
            // (~10s) re-stacks the root's visible children and wrongly includes our block, yanking it up for
            // a frame; it computes the node's transform in that same pass, so correcting the position
            // afterward can't render in time. The header (and its children) are left alone by that reflow,
            // and a plain Res node doesn't clip its children, so nesting our block under the header keeps it
            // put. The header sits at y=0, so our root-absolute Y values still land correctly.
            var parent = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            if (parent is null)
                foreach (var id in TooltipLayout.HeaderFallbackIds)
                {
                    parent = addon->GetNodeById(id);
                    if (parent is not null) break;
                }

            if (parent is not null)
                _block.AttachNode(parent);
            else
                _block.AttachNode(ptr);
        }

        return true;
    }

    /// <summary>Get or lazily create the icon at <paramref name="index" />, attached to the block body.</summary>
    private IconImageNode? GetOrCreateIcon(int index)
    {
        if (_block is null) return null;
        if (index < _icons.Count) return _icons[index];

        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_block);
        _icons.Add(icon);
        return icon;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (_attachedAddon is null || (AtkUnitBase*)args.Addon.Address != _attachedAddon) return;

            // The addon is still alive at PreFinalize, so detaching/freeing our nodes here is safe.
            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error detaching gear-set block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children too (header, divider, and the attached icons), so the
        // icon list just needs clearing of its now-dangling references.
        _block?.Dispose();
        _block = null;
        _icons.Clear();
    }

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);

            // If the tooltip is still alive these detach cleanly; if it's already gone our nodes were freed
            // in OnPreFinalize and the lists are empty. KTK's Dispose also guards game-shutdown.
            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error disposing gear-set block.");
        }
    }
}
