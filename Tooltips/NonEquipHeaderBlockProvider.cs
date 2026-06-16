using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' <b>non-equipment</b> item-header block — the counterpart to
///     <see cref="UnifiedHeaderBlockProvider" /> for consumables / materials / cards / etc. (everything the
///     gear header rejects). Same passive-provider contract: it builds/measures/shows the block; the relayout
///     hides the native name/icon/category and anchors the rest below it. The two headers are mutually
///     exclusive — <see cref="NonEquipHeaderData.FromHoveredItem" /> returns null for equippable gear, so only
///     one engages per hover.
///     <para>
///         Nodes (a <see cref="NonEquipHeaderNodes" /> tree) are created once and reused, content updated in
///         place only on hovered-item change — the lifetime-safe pattern. Detached + disposed on
///         <see cref="OnPreFinalize" /> and plugin <see cref="Dispose" />; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class NonEquipHeaderBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    private const float BlockBottomPad = 4f;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private ResNode? _block;
    private NonEquipHeaderNodes? _nodes;
    private AtkUnitBase* _attachedAddon;
    private ulong _lastHovered = ulong.MaxValue;
    private float _lastHeight;

    public NonEquipHeaderBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Enhanced-only, like the gear header (the curated layout owns the whole header).</summary>
    public bool Enabled => _config.Enabled && _config.EnhancedMode;

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the hovered item is
    ///     equippable gear (the gear header's job) / nothing resolvable.
    /// </summary>
    public bool TryMeasure(AddonItemDetail* addon, float width, out float height)
    {
        height = 0f;
        try
        {
            if (addon is null || !Enabled)
            {
                Hide();
                return false;
            }

            var data = NonEquipHeaderData.FromHoveredItem(_gameGui, _data);
            if (data is null)
            {
                Hide();
                return false;
            }

            if (!EnsureAttached(addon))
            {
                Hide();
                return false;
            }

            Hide(); // keep hidden while we measure

            var hovered = _gameGui.HoveredItem;
            if (hovered != _lastHovered)
            {
                _lastHovered = hovered;

                // Prefer the rendered native name (#33) for its payload glyphs (HQ mark); scraped before the
                // relayout hides #33, falls back to the Lumina name.
                var nameRaw = ReadNameRaw(addon);
                if (nameRaw is not null) data = data with { NameRaw = nameRaw };

                _lastHeight = _nodes!.Update(data, width);
                _block!.Size = new Vector2(width, _lastHeight);
            }

            height = _lastHeight + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring non-equipment header block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> (header-relative; parented to the header at y=0).</summary>
    public void PlaceAt(float y)
    {
        if (_block is null) return;
        _block.X = 0;
        _block.Y = y;
    }

    public void Show()
    {
        if (_block is not null) _block.IsVisible = true;
    }

    public void Hide()
    {
        if (_block is not null) _block.IsVisible = false;
    }

    private static byte[]? ReadNameRaw(AddonItemDetail* addon)
    {
        var node = addon->GetNodeById(TooltipLayout.ItemNameNodeId);
        if (node is null || node->Type != NodeType.Text) return null;

        var span = ((AtkTextNode*)node)->NodeText.AsSpan();
        return span.IsEmpty ? null : span.ToArray();
    }

    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: non-equipment header block saw a new ItemDetail without a finalize; rebuilding.");
            DisposeNodes();
        }

        _attachedAddon = ptr;
        _lastHovered = ulong.MaxValue; // force a content rebuild for the new addon

        if (_block is null)
        {
            _block = new ResNode { IsVisible = false };

            var parent = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            if (parent is null)
                foreach (var id in TooltipLayout.HeaderFallbackIds)
                {
                    parent = addon->GetNodeById(id);
                    if (parent is not null) break;
                }

            if (parent is not null) _block.AttachNode(parent);
            else _block.AttachNode(ptr);

            _nodes = new NonEquipHeaderNodes();
            _nodes.Build(_block);
        }

        return true;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (_attachedAddon is null || (AtkUnitBase*)args.Addon.Address != _attachedAddon) return;

            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error detaching non-equipment header block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        _block?.Dispose();
        _block = null;
        _nodes?.Reset();
        _nodes = null;
        _lastHovered = ulong.MaxValue;
    }

    public void Dispose()
    {
        try
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
            DisposeNodes();
            _attachedAddon = null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error disposing non-equipment header block.");
        }
    }
}
