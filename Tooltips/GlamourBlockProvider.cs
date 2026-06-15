using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own "Glamour" content block — the glamoured-appearance name and the applied dye
///     channels (with color swatches). Like <see cref="GearSetBlockProvider" /> it is a <b>passive provider</b>:
///     it builds/measures/shows the block but never positions or anchors anything — <see cref="TooltipRelayoutController" />
///     drives it as a custom section in the single layout pass.
///     <para>
///         The glamour name is per-instance state the tooltip never renders, so it comes from
///         <see cref="GlamourSource" /> (populated by the string hook) and is set as a raw SeString to keep
///         its payload glyph. The dyes are scraped from the description block by <see cref="GlamourData" />.
///         Node lifetime mirrors the other added blocks: created once under the header, reused for the addon's
///         life, disposed on <see cref="OnPreFinalize" /> and plugin <see cref="Dispose" />.
///     </para>
/// </summary>
public sealed unsafe class GlamourBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // Body layout (block-relative).
    private const float NameLineHeight = 18f;
    private const float DyeLineHeight = 20f;
    private const float SwatchSize = 12f;
    private const float SwatchGap = 6f;        // gap between a swatch and its dye name
    private const float SwatchTopPad = 3f;     // nudge the swatch down to vertically center it on the text
    private const uint BodyFontSize = 12;
    private const float BlockBottomPad = 6f;   // breathing room below the block

    private static readonly Vector4 NameColor = new(0xE8 / 255f, 0xDD / 255f, 0xC4 / 255f, 1f); // cream
    private static readonly Vector4 DyeNameColor = new(0xFF / 255f, 0xFF / 255f, 0xFF / 255f, 1f);
    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly GlamourSource _glamour;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private TextNode? _name;
    private readonly List<ColorImageNode> _swatches = [];
    private readonly List<TextNode> _dyeNames = [];

    private AtkUnitBase* _attachedAddon;

    public GlamourBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui, IDataManager data,
        GlamourSource glamour, Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _data = data;
        _glamour = glamour;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the feature (and the plugin) is on. Forced on by the Enhanced tooltip (one of its five
    /// sections); otherwise follows the modifier-mode <see cref="Configuration.Configuration.ShowGlamour" /> toggle.</summary>
    public bool Enabled => _config.Enabled && (_config.EnhancedMode || _config.ShowGlamour);

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the item has neither a
    ///     glamour nor any dye. The caller positions it via <see cref="PlaceAt" /> + <see cref="Show" />. Must
    ///     run <em>before</em> the relayout hides the description block, since the dyes are scraped from it.
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

            // Gate the dyes on the rendered description block actually showing a dye line (per-item accurate),
            // so a stale slot-[13] value can't surface dyes on a non-dyeable item. The node text persists even
            // after we hide the block, so no visibility check.
            var dyeText = NodeContainsDye(addon) ? _glamour.DyeText : string.Empty;
            var data = GlamourData.FromHovered(_data, _glamour.GlamourNameRaw, dyeText);
            if (data is null || !data.HasContent)
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

            var y = block.BodyTop;

            // Glamour name (raw SeString → the glamour glyph renders).
            if (data.GlamourNameRaw is not null)
            {
                _name!.String = new ReadOnlySeString(data.GlamourNameRaw);
                _name.Position = new Vector2(TooltipContentBlock.BodyInsetX, y);
                _name.IsVisible = true;
                y += NameLineHeight;
            }
            else
            {
                _name!.IsVisible = false;
            }

            // One row per applied dye: a color swatch + the dye name.
            for (var i = 0; i < data.Dyes.Count; i++)
            {
                var dye = data.Dyes[i];
                var (swatch, label) = GetOrCreateDye(i);

                swatch.Color = dye.Color;
                swatch.Size = new Vector2(SwatchSize, SwatchSize);
                swatch.Position = new Vector2(TooltipContentBlock.BodyInsetX, y + SwatchTopPad);
                swatch.IsVisible = dye.HasColor;

                var textX = TooltipContentBlock.BodyInsetX + (dye.HasColor ? SwatchSize + SwatchGap : 0f);
                label.String = dye.Name;
                label.Position = new Vector2(textX, y);
                label.IsVisible = true;

                y += DyeLineHeight;
            }

            for (var i = data.Dyes.Count; i < _swatches.Count; i++)
            {
                _swatches[i].IsVisible = false;
                _dyeNames[i].IsVisible = false;
            }

            height = block.Resize(width, y - block.BodyTop) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring glamour block (skipped).");
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
            _log.Warning("BetterTips: glamour block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { HeaderText = "Glamour", IsVisible = false };

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

            _name = new TextNode
            {
                FontType = FontType.Axis,
                FontSize = BodyFontSize,
                AlignmentType = AlignmentType.TopLeft,
                TextColor = NameColor,
                TextOutlineColor = OutlineColor,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                IsVisible = false
            };
            _name.AttachNode(_block);
        }

        return true;
    }

    /// <summary>True if the description block (#40) currently renders a dye line — the per-item truth used to
    /// validate the (possibly stale) slot-[13] dye text. No visibility check: the text persists after we hide
    /// the block, and we measure before the relayout re-shows it.</summary>
    private static bool NodeContainsDye(AddonItemDetail* addon)
    {
        var list = addon->UldManager.NodeList;
        if (list is null) return false;
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];
            if (node is null || node->Type != NodeType.Text) continue;
            if (!IsDescendantOf(node, TooltipLayout.DescriptionBlockId)) continue;

            try
            {
                if (((AtkTextNode*)node)->NodeText.ToString().Contains(TooltipLayout.DyeLineMarker, StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // ignore and keep scanning
            }
        }

        return false;
    }

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

    /// <summary>Get or lazily create the (swatch, name) node pair for dye row <paramref name="index" />.</summary>
    private (ColorImageNode Swatch, TextNode Label) GetOrCreateDye(int index)
    {
        if (index < _swatches.Count) return (_swatches[index], _dyeNames[index]);

        var swatch = new ColorImageNode { IsVisible = false };
        swatch.AttachNode(_block!);

        var label = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = DyeNameColor,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
            IsVisible = false
        };
        label.AttachNode(_block!);

        _swatches.Add(swatch);
        _dyeNames.Add(label);
        return (swatch, label);
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
            _log.Error(ex, "BetterTips: error detaching glamour block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (name, swatches, dye labels) too.
        _block?.Dispose();
        _block = null;
        _name = null;
        _swatches.Clear();
        _dyeNames.Clear();
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
            _log.Error(ex, "BetterTips: error disposing glamour block.");
        }
    }
}
