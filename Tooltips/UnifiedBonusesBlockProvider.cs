using System.Text;
using BetterTips.UI;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' "Unified bonuses &amp; materia" block — a single redesigned section that replaces the
///     native attribute Bonuses block (<c>#97</c>) and Materia block (<c>#93</c>) when the enhancement is on.
///     Like <see cref="UnifiedHeaderBlockProvider" /> / <see cref="GearSetBlockProvider" /> it is a
///     <b>passive provider</b>: it builds/measures/shows the block but never positions or anchors anything —
///     <see cref="TooltipRelayoutController" /> drives it, hides the native pieces, and lays this out at the
///     Bonuses slot in the user's order.
///     <para>
///         The nodes (a <see cref="UnifiedBonusesNodes" /> tree) are created once and reused, with content
///         rebuilt in place only when the rendered data changes (a content <em>signature</em>, not just the
///         hovered id — melded materia is scraped from the native block, which the game may populate a frame
///         or two after the hover lands). Detached + disposed on <see cref="OnPreFinalize" /> and on plugin
///         <see cref="Dispose" />; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class UnifiedBonusesBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // A little breathing room below the block before the next section, like the other added blocks.
    private const float BlockBottomPad = 4f;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private ResNode? _block;
    private UnifiedBonusesNodes? _nodes;
    private AtkUnitBase* _attachedAddon;
    private string _lastSignature = string.Empty;
    private float _lastHeight;

    // Materia is scraped from the native materia block (#93), but once our block shows the relayout hides #93 —
    // so a later scrape comes back empty. We cache the materia per hovered item and only let a non-empty scrape
    // replace it, so the data survives #93 being hidden. Reset when the hovered item changes.
    private ulong _lastHovered = ulong.MaxValue;
    private IReadOnlyList<MateriaEntry> _cachedMateria = [];

    public UnifiedBonusesBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the enhancement (and the plugin) is on.</summary>
    public bool Enabled
        => _config.Enabled && EnhancementCatalog.IsEnabled(_config, Enhancement.UnifiedBonusesMateria);

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when the enhancement is off or the
    ///     hovered item has neither bonuses nor materia. The caller positions it via <see cref="PlaceAt" /> +
    ///     <see cref="Show" />. Must run <em>before</em> the relayout hides the native materia block, since the
    ///     materia data is scraped from it.
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

            var data = UnifiedBonusesData.FromHoveredItem(_gameGui, _data, addon);
            if (data is null)
            {
                Hide();
                return false;
            }

            // Cache the scraped materia per hover so hiding #93 (which we do once our block shows) can't wipe it:
            // reset on a new item, then only let a non-empty scrape replace the cache.
            var hovered = _gameGui.HoveredItem;
            if (hovered != _lastHovered)
            {
                _lastHovered = hovered;
                _cachedMateria = data.Materia;
            }
            else if (data.Materia.Count > 0)
            {
                _cachedMateria = data.Materia;
            }

            data = data with { Materia = _cachedMateria };

            if (!EnsureAttached(addon))
            {
                Hide();
                return false;
            }

            Hide(); // keep hidden while we measure

            // Rebuild the content only when it actually changes. The signature folds in the bonuses and the
            // (scraped) materia, so a late materia population — or a re-meld — re-lays-out, but a static hover
            // doesn't churn every update.
            var signature = BuildSignature(data);
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                _lastHeight = _nodes!.Update(data, width);
                _block!.Size = new Vector2(width, _lastHeight);
            }

            height = _lastHeight + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring unified bonuses block (skipped).");
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

    private static string BuildSignature(UnifiedBonusesData data)
    {
        var sb = new StringBuilder();
        foreach (var b in data.Bonuses)
            sb.Append(b.Name).Append(b.Value).Append((int)b.Color).Append('|');
        sb.Append('#');
        foreach (var m in data.Materia)
            sb.Append(m.IconId).Append(m.Name).Append(m.Value).Append((int)m.Color).Append(m.IsEmpty ? 'e' : 'f').Append('|');
        return sb.ToString();
    }

    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        // A new addon we never saw finalize — dispose the old nodes cleanly rather than abandon them.
        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: unified bonuses block saw a new ItemDetail without a finalize; rebuilding.");
            DisposeNodes();
        }

        _attachedAddon = ptr;
        _lastSignature = string.Empty; // force a content rebuild for the new addon

        if (_block is null)
        {
            _block = new ResNode { IsVisible = false };

            // Attach under the header (#17), like the other added blocks: the game's content reflow leaves the
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

            _nodes = new UnifiedBonusesNodes();
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
            _log.Error(ex, "BetterTips: error detaching unified bonuses block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (every UnifiedBonusesNodes node) too.
        _block?.Dispose();
        _block = null;
        _nodes?.Reset();
        _nodes = null;
        _lastSignature = string.Empty;
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
            _log.Error(ex, "BetterTips: error disposing unified bonuses block.");
        }
    }
}
