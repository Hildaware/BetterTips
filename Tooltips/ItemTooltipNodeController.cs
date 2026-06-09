using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BetterTips.Tooltips;

/// <summary>
///     The primary, signature-free hide path. Listens to the <c>ItemDetail</c> addon through Dalamud's
///     <see cref="IAddonLifecycle" /> and toggles the visibility of the named node groups
///     (<see cref="ItemDetailGroup" />) for whichever sections the user has hidden. No game-function
///     signature is involved — this is the same technique HUDUnlimited/KamiToolKit use. We re-apply after
///     every requested-update and refresh because the game re-shows groups per hovered item.
///     <para>
///         Crash-safety: the callback runs on the framework thread (same as <see cref="Dispose" />), so no
///         in-flight call can race teardown. Every native access is null-checked first — and note that the
///         group pointers are *legitimately* null for items lacking that group (a potion has no durability
///         group), so the checks are normal control flow, not merely defensive. An access violation can't
///         be caught, so these checks, not the try/catch, are what keep the client alive.
///     </para>
/// </summary>
public sealed unsafe class ItemTooltipNodeController : IDisposable
{
    private const string AddonName = "ItemDetail";

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    // Recomputed on config change; replaced as a whole reference (atomic) so the callback never sees a
    // half-mutated collection.
    private ItemDetailGroup[] _hiddenGroups = [];
    private uint[] _hiddenBlocks = [];
    private uint[] _hiddenNodeIds = [];

    // Set by "/btips dumpnodes"; on the next addon update we log the node tree, then clear it.
    private volatile bool _nodeDumpRequested;

    public ItemTooltipNodeController(IAddonLifecycle addonLifecycle, Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _config = config;
        _log = log;
        Rebuild();

        _addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPostUpdate);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPostUpdate);
    }

    /// <summary>Recompute which node groups to hide from the current config. Call after any change.</summary>
    public void Rebuild()
    {
        _hiddenGroups = TooltipFieldMap.GroupsFor(_config.HiddenSections);
        _hiddenBlocks = TooltipFieldMap.BlocksFor(_config.HiddenSections);
        _hiddenNodeIds = TooltipFieldMap.NodeIdsFor(_config.HiddenSections);
    }

    /// <summary>Request a node-tree dump of the ItemDetail addon on the next update (dev aid for finding leftover label nodes).</summary>
    public void RequestNodeDump()
    {
        _nodeDumpRequested = true;
    }

    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AddonItemDetail*)args.Addon.Address;
            if (addon is null) return;

            if (_nodeDumpRequested)
            {
                _nodeDumpRequested = false;
                DumpNodeTree(addon);
            }

            if (!_config.Enabled) return;

            foreach (var group in _hiddenGroups)
            {
                var node = ResolveGroupNode(addon, group);
                if (node is not null)
                    node->ToggleVisibility(false);
            }

            foreach (var nodeId in _hiddenNodeIds)
            {
                var node = addon->GetNodeById(nodeId);
                if (node is not null)
                    node->ToggleVisibility(false);
            }

            HideBlocksAndReflow(addon);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error in ItemDetail node post-update (tooltip left as-is).");
        }
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
    ///     Hides each section-block container the user disabled, then closes the vertical gap: every block
    ///     below a removed one is shifted up by the removed height, and the whole tooltip is shrunk to
    ///     match. Re-applied each update because the game re-lays-out the tooltip from scratch every change.
    /// </summary>
    private void HideBlocksAndReflow(AddonItemDetail* addon)
    {
        if (_hiddenBlocks.Length == 0) return;

        var root = addon->RootNode;
        if (root is null) return;
        var rootId = root->NodeId;

        // Hide each target block, recording the Y/height of those that were actually visible this frame.
        Span<float> removedY = stackalloc float[_hiddenBlocks.Length];
        Span<float> removedH = stackalloc float[_hiddenBlocks.Length];
        var removedCount = 0;

        foreach (var id in _hiddenBlocks)
        {
            var block = addon->GetNodeById(id);
            if (block is null) continue;
            if ((block->NodeFlags & NodeFlags.Visible) == 0) continue; // already hidden (by us last frame, or the game)

            removedY[removedCount] = block->Y;
            removedH[removedCount] = block->Height;
            removedCount++;
            block->ToggleVisibility(false);
        }

        if (removedCount == 0) return;

        float totalRemoved = 0;
        for (var i = 0; i < removedCount; i++)
            totalRemoved += removedH[i];

        // Shift every still-visible sibling block up by the height removed above it.
        var list = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->ParentNode is null || node->ParentNode->NodeId != rootId) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

            float shift = 0;
            for (var k = 0; k < removedCount; k++)
                if (removedY[k] < node->Y) shift += removedH[k];

            if (shift > 0)
                node->SetYFloat(node->Y - shift);
        }

        // Shrink the whole tooltip by the removed height. SetSize handles the content area and the
        // root-level collision, but not the visible bordered frame, so resize that separately below.
        var newHeight = root->Height - totalRemoved;
        if (newHeight < 1) newHeight = 1;
        addon->SetSize(root->Width, (ushort)newHeight);
        addon->UpdateCollisionNodeList(false);

        // The window frame is the tallest visible non-collision child of the root; SetSize leaves it at
        // full height, so shrink its node and its nine-grid/collision parts to match.
        AtkResNode* frame = null;
        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->ParentNode is null || node->ParentNode->NodeId != rootId) continue;
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Type == NodeType.Collision) continue;
            if (frame is null || node->Height > frame->Height) frame = node;
        }

        if (frame is not null && frame->Height > totalRemoved)
            ResizeFrame(frame, totalRemoved);

        // Anchor: keep the chosen corner fixed as the tooltip shrinks. Only the height changes, so only
        // the vertical half matters — bottom anchors move the window down by the removed height so its
        // bottom edge stays put; top anchors leave the position alone. (Left/right is a no-op until width
        // changes.)
        if (_config.Anchor is TooltipAnchor.BottomLeft or TooltipAnchor.BottomRight)
            addon->SetPosition(addon->X, (short)(addon->Y + (int)totalRemoved));
    }

    private static void ResizeFrame(AtkResNode* frame, float amount)
    {
        frame->SetHeight((ushort)(frame->Height - amount));

        // For a window-background component, the visible border is a nine-grid inside it that must be
        // shrunk too (and its collision part). Other children keep their size.
        if ((uint)frame->Type < 1000) return;

        var component = ((AtkComponentNode*)frame)->Component;
        if (component is null) return;

        var list = component->UldManager.NodeList;
        var count = component->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null) continue;
            if ((node->Type == NodeType.NineGrid || node->Type == NodeType.Collision) && node->Height > amount)
                node->SetHeight((ushort)(node->Height - amount));
        }
    }

    /// <summary>
    ///     Dev aid: logs the ItemDetail node tree (id / type / visibility / Y / Height / text) by walking the
    ///     real parent/child tree, so the section-block hierarchy and heights are visible — that's what's
    ///     needed to reposition blocks and close the gap left by hiding one. Budget- and depth-capped.
    /// </summary>
    private void DumpNodeTree(AddonItemDetail* addon)
    {
        // The flat node list is the complete enumeration (ItemDetail's content isn't reachable from
        // RootNode's child chain). Logging each node's parent id reconstructs the hierarchy, and the
        // height/Y let us plan a reposition to close the gap left by hiding a block.
        _log.Information("BetterTips: ItemDetail nodes (#id <parent | type vis y=Y h=Height \"text\"):");

        var list = addon->UldManager.NodeList;
        if (list is null) return;

        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count && i < 400; i++)
        {
            var node = list[i];
            if (node is not null)
                LogNode(node);
        }
    }

    private void LogNode(AtkResNode* node)
    {
        var visible = (node->NodeFlags & NodeFlags.Visible) != 0;
        var parentId = node->ParentNode is not null ? node->ParentNode->NodeId : 0;

        var text = "";
        if (node->Type == NodeType.Text)
        {
            try
            {
                var s = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrEmpty(s)) text = $" \"{s.Replace("\n", "\\n")}\"";
            }
            catch
            {
                text = " <text?>";
            }
        }

        _log.Information($"#{node->NodeId} <#{parentId} {node->Type} vis={visible} y={node->Y:0} h={node->Height:0}{text}");
    }

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPostUpdate);
            _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPostUpdate);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error unregistering tooltip node listeners.");
        }
    }
}
