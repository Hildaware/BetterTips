using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own "Condition" content block — a single horizontal row of the item's durability,
///     spiritbond, and sell price, each an icon with its value beside it (see <see cref="ConditionData" />).
///     Like <see cref="GearSetBlockProvider" /> / <see cref="GlamourBlockProvider" /> it is a <b>passive
///     provider</b>: it builds/measures/shows the block but never positions or anchors anything —
///     <see cref="TooltipRelayoutController" /> drives it as a custom section in the single layout pass and
///     hides the native durability/spiritbond gauge (<c>#7</c>) when this block shows.
///     <para>
///         The durability/spiritbond values are scraped from the native gauge, which the relayout hides once
///         our block shows, so they're cached per hovered item (only a non-null scrape replaces the cache) —
///         the same trick the unified-materia block uses for the materia it scrapes. Node lifetime mirrors the
///         other added blocks: created once under the header, reused for the addon's life, disposed on
///         <see cref="OnPreFinalize" /> and plugin <see cref="Dispose" />; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class ConditionBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // Horizontal row layout (block-relative). Shared with the editor preview's BuildCondition so both render
    // the same shape — tune here and both move. The (icon + value) groups are packed together and the whole
    // cluster is right-aligned to the section's right padding (BodyInsetX), with GroupGap between each group.
    public const float IconSize = 26f;
    public const float IconValueGap = 4f;   // gap between an icon and its value text
    public const float IconOffsetY = 2f;     // nudge the icon down to sit on the value text's baseline
    public const float RowHeight = 28f;      // height of the single content row (fits the icon)
    public const uint BodyFontSize = 12;

    // The block is headerless (no title/divider). Kept at zero so it doesn't compound with the previous
    // section's bottom padding (and the block's own headerless body inset) into a large gap. Shared with the
    // editor preview's BuildCondition.
    public const float TopPad = 0f;

    // Spacing between adjacent (icon + value) groups in the right-aligned cluster.
    public const float GroupGap = 20f;

    private const float BlockBottomPad = 2f; // breathing room below the block (kept tight)

    private static readonly Vector4 ValueColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private readonly List<IconImageNode> _icons = [];
    private readonly List<TextNode> _values = [];

    private AtkUnitBase* _attachedAddon;

    // Durability/spiritbond are scraped from the gauge value nodes, but once our block shows the relayout hides
    // the gauge — so a later scrape comes back empty. Cache them per hovered item; only a non-null scrape
    // replaces the cache, and it resets on an item change.
    private ulong _lastHovered = ulong.MaxValue;
    private string? _cachedDurability;
    private string? _cachedSpiritbond;

    public ConditionBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the feature (and the plugin) is on. Forced on by the Enhanced tooltip (one of its five
    /// sections); otherwise follows the modifier-mode <see cref="Configuration.Configuration.ShowCondition" /> toggle.</summary>
    public bool Enabled => _config.Enabled && (_config.EnhancedMode || _config.ShowCondition);

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the item has no
    ///     durability/spiritbond/sell price. The caller positions it via <see cref="PlaceAt" /> +
    ///     <see cref="Show" />. Must run <em>before</em> the relayout hides the native gauge, since the
    ///     condition values are scraped from it.
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

            var data = ConditionData.FromHoveredItem(_gameGui, _data, addon);
            if (data is null)
            {
                Hide();
                return false;
            }

            // Cache the scraped durability/spiritbond per hover so hiding the gauge (which we do once our block
            // shows) can't wipe them: reset on a new item, then only let a non-null scrape replace the cache.
            var hovered = _gameGui.HoveredItem;
            if (hovered != _lastHovered)
            {
                _lastHovered = hovered;
                _cachedDurability = data.Durability;
                _cachedSpiritbond = data.Spiritbond;
            }
            else
            {
                if (data.Durability is not null) _cachedDurability = data.Durability;
                if (data.Spiritbond is not null) _cachedSpiritbond = data.Spiritbond;
            }

            data = data with { Durability = _cachedDurability, Spiritbond = _cachedSpiritbond };
            var entries = data.Entries();
            if (entries.Count == 0)
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
            Hide(); // keep hidden while we measure

            // Right-align the whole cluster of (icon + value) groups to the section's right padding
            // (BodyInsetX), packed together with GroupGap between each. Measure each value unscaled (the live
            // addon renders at the user's UI scale, but our coordinates are node-local — same reasoning as the
            // unified-materia block's right-alignment measure) to get each group's width, sum them to find the
            // cluster's left start, then lay out left-to-right (order preserved: durability, spiritbond, sell).
            var y = block.BodyTop + TopPad;
            var count = entries.Count;
            var rightEdge = width - TooltipContentBlock.BodyInsetX;

            var groupWidths = new float[count];
            var total = 0f;
            for (var i = 0; i < count; i++)
            {
                var (_, value) = GetOrCreate(i);
                value.String = entries[i].Value;
                var textW = value.GetTextDrawSize(entries[i].Value, considerScale: false).X;
                groupWidths[i] = IconSize + IconValueGap + textW;
                total += groupWidths[i];
            }
            total += (count - 1) * GroupGap;

            var groupX = rightEdge - total;
            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                var (icon, value) = GetOrCreate(i);

                icon.IconId = entry.IconId;
                icon.Size = new Vector2(IconSize, IconSize);
                icon.Position = new Vector2(groupX, y + (RowHeight - IconSize) / 2f + IconOffsetY);
                icon.IsVisible = true;

                value.TextColor = entry.Color;
                value.Position = new Vector2(groupX + IconSize + IconValueGap, y + (RowHeight - BodyFontSize) / 2f);
                value.IsVisible = true;

                groupX += groupWidths[i] + GroupGap;
            }

            for (var i = entries.Count; i < _icons.Count; i++)
            {
                _icons[i].IsVisible = false;
                _values[i].IsVisible = false;
            }

            height = block.Resize(width, TopPad + RowHeight) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring condition block (skipped).");
            Hide();
            return false;
        }
    }

    /// <summary>Position the block at <paramref name="y" /> (header-relative; the header is at y=0, so this is
    /// effectively root-absolute).</summary>
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

    private bool EnsureAttached(AddonItemDetail* addon)
    {
        var ptr = (AtkUnitBase*)addon;
        if (_attachedAddon == ptr && _block is not null) return true;

        if (_attachedAddon != ptr && _attachedAddon is not null)
        {
            _log.Warning("BetterTips: condition block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { IsVisible = false };
            _block.SetHeaderless(); // no title/divider — just the row, with a little top padding

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
        }

        return true;
    }

    /// <summary>Get or lazily create the (icon, value) node pair for column <paramref name="index" />.</summary>
    private (IconImageNode Icon, TextNode Value) GetOrCreate(int index)
    {
        if (index < _icons.Count) return (_icons[index], _values[index]);

        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_block!);

        var value = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ValueColor,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
            IsVisible = false
        };
        value.AttachNode(_block!);

        _icons.Add(icon);
        _values.Add(value);
        return (icon, value);
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
            _log.Error(ex, "BetterTips: error detaching condition block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (icons + value texts) too.
        _block?.Dispose();
        _block = null;
        _icons.Clear();
        _values.Clear();
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
            _log.Error(ex, "BetterTips: error disposing condition block.");
        }
    }
}
