using BetterTips.UI;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' "Unified item header" block — a single redesigned section (icon + name + category +
///     primary stat + item level + required level + equippable-job icons) that replaces the native header
///     name/icon, the Item Level block (<c>#62</c>), and the Damage/Defense block (<c>#36</c>) when the
///     enhancement is on. Like <see cref="GearSetBlockProvider" /> it is a <b>passive provider</b>: it
///     builds/measures/shows the block but never positions or anchors anything — <see cref="TooltipRelayoutController" />
///     drives it, hides the native pieces, places this at the top, and re-anchors the rest below it.
///     <para>
///         The nodes (a <see cref="UnifiedHeaderNodes" /> tree) are created once and reused, with content
///         updated in place only when the hovered item changes — the lifetime-safe pattern the gear-set
///         block established (no per-frame re-attach). Detached + disposed on <see cref="OnPreFinalize" />
///         and on plugin <see cref="Dispose" />; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class UnifiedHeaderBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // A little breathing room below the block before the next section, like the gear-set block.
    private const float BlockBottomPad = 4f;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly IObjectTable _objects;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private ResNode? _block;
    private UnifiedHeaderNodes? _nodes;
    private AtkUnitBase* _attachedAddon;
    private ulong _lastHovered = ulong.MaxValue;
    private float _lastHeight;

    public UnifiedHeaderBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        IObjectTable objects, Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _objects = objects;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the enhancement (and the plugin) is on.</summary>
    public bool Enabled
        => _config.Enabled && EnhancementCatalog.IsEnabled(_config, Enhancement.UnifiedItemHeader);

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so
    ///     the caller's layout scans ignore it. Returns <c>false</c> (and hides) when the enhancement is off
    ///     or nothing resolvable is hovered. The caller positions it via <see cref="PlaceAt" /> + <see cref="Show" />.
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

            var data = UnifiedHeaderData.FromHoveredItem(_gameGui, _data, _objects);
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

            // Rebuild the content only when the hovered item changes — not every frame (avoids churn and the
            // re-attach hazard). EnsureAttached resets _lastHovered when the addon changes, forcing a rebuild.
            var hovered = _gameGui.HoveredItem;
            if (hovered != _lastHovered)
            {
                _lastHovered = hovered;

                // Use the rendered native name (#33) — its SeString carries the game's payload glyphs (HQ
                // mark, etc.) that the payload-free Lumina name lacks. Scraped before the relayout hides #33;
                // falls back to the Lumina name when unavailable.
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
            _log.Error(ex, "BetterTips: error measuring unified header block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> (header-relative; the block is parented to the
    /// header at y=0, so this is effectively root-absolute).</summary>
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

    /// <summary>Copy the rendered native name node's (#33) raw SeString bytes, or <c>null</c> if unavailable.
    /// Guarded — a bad pointer must not throw out of the layout pass.</summary>
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

        // A new addon we never saw finalize — dispose the old nodes cleanly rather than abandon them (the
        // same lifetime rule as the gear-set block).
        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: unified header block saw a new ItemDetail without a finalize; rebuilding.");
            DisposeNodes();
        }

        _attachedAddon = ptr;
        _lastHovered = ulong.MaxValue; // force a content rebuild for the new addon

        if (_block is null)
        {
            _block = new ResNode { IsVisible = false };

            // Attach under the header (#17), like the gear-set block: the game's content reflow leaves the
            // header's children alone, so the block stays put, and the header sits at y=0 so block-relative Y
            // is effectively root-absolute.
            var parent = addon->GetNodeById(TooltipLayout.HeaderBlockId);
            if (parent is null)
                foreach (var id in TooltipLayout.HeaderFallbackIds)
                {
                    parent = addon->GetNodeById(id);
                    if (parent is not null) break;
                }

            if (parent is not null) _block.AttachNode(parent);
            else _block.AttachNode(ptr);

            _nodes = new UnifiedHeaderNodes();
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
            _log.Error(ex, "BetterTips: error detaching unified header block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (every UnifiedHeaderNodes node) too.
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
            _log.Error(ex, "BetterTips: error disposing unified header block.");
        }
    }
}
