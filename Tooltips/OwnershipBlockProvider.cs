using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FfxivCollections;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace BetterTips.Tooltips;

/// <summary>
///     Owns BetterTips' own "Ownership" content block — a short vertical list of where the hovered item is
///     already in the player's possession (owned count + location, Glamour Dresser, Armoire, and whether its
///     mount/minion/card is unlocked; see <see cref="OwnershipData" />). Like the other added sections it is a
///     <b>passive provider</b>: it builds/measures/shows the block but never positions or anchors anything —
///     <see cref="TooltipRelayoutController" /> drives it as a custom section in the single layout pass.
///     <para>
///         Unlike the condition/glamour blocks it reads independent game state (inventory, collections), not
///         tooltip nodes the relayout hides, so it needs no per-hover cache. Node lifetime mirrors the other
///         added blocks: created once under the header, reused for the addon's life, disposed on
///         <see cref="OnPreFinalize" /> and plugin <see cref="Dispose" />; every native access is null-checked.
///     </para>
/// </summary>
public sealed unsafe class OwnershipBlockProvider : IDisposable
{
    private const string AddonName = "ItemDetail";

    // Vertical row layout (block-relative). Icon sizing/positioning mirrors the Condition section's
    // (icon + value) groups — same icon size and baseline nudge. Shared with the editor preview's
    // BuildOwnership so both render the same shape — tune here and both move.
    public const float IconSize = 26f;
    public const float RowHeight = 28f;
    public const float IconTextGap = 4f;
    public const float IconOffsetY = 2f; // nudge the icon down to sit on the text's baseline
    public const float StatusIconSize = 20f; // trailing status icon (e.g. the "collected" checkmark)
    public const uint BodyFontSize = 12;

    private const float BlockBottomPad = 2f; // breathing room below the block (kept small so the gap to the
                                             // next section — e.g. Condition — stays tight)

    public const string HeaderLabel = "Possessions";

    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly ItemCollectionService _collections;
    private readonly Configuration.Configuration _config;
    private readonly IPluginLog _log;

    private TooltipContentBlock? _block;
    private readonly List<IconImageNode> _icons = [];
    private readonly List<TextNode> _texts = [];
    private readonly List<IconImageNode> _statusIcons = []; // trailing status icon per row (e.g. checkmark)

    private AtkUnitBase* _attachedAddon;

    public OwnershipBlockProvider(IAddonLifecycle addonLifecycle, IGameGui gameGui,
        ItemCollectionService collections, Configuration.Configuration config, IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _collections = collections;
        _config = config;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    /// <summary>Whether the feature (and the plugin) is on. Forced on by the Enhanced tooltip; otherwise follows
    /// the modifier-mode <see cref="Configuration.Configuration.ShowOwnership" /> toggle.</summary>
    public bool Enabled => _config.Enabled && (_config.EnhancedMode || _config.ShowOwnership);

    /// <summary>
    ///     Build/refresh the block for the current hover and report its height, leaving it <b>hidden</b> so the
    ///     caller's layout scans ignore it. Returns <c>false</c> (and hides) when off or the item is owned
    ///     nowhere and grants no tracked collectible. The caller positions it via <see cref="PlaceAt" /> +
    ///     <see cref="Show" />.
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

            var data = OwnershipData.FromHovered(_gameGui, _collections);
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

            var entries = data.Entries;
            var y = block.BodyTop;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var (icon, text, status) = GetOrCreate(i);

                icon.IconId = entry.IconId;
                icon.Size = new Vector2(IconSize, IconSize);
                icon.Position = new Vector2(TooltipContentBlock.BodyInsetX,
                    y + (RowHeight - IconSize) / 2f + IconOffsetY);
                icon.IsVisible = true;

                var textX = TooltipContentBlock.BodyInsetX + IconSize + IconTextGap;
                text.String = entry.Text;
                text.TextColor = entry.Color;
                text.Position = new Vector2(textX, y + (RowHeight - BodyFontSize) / 2f);
                text.IsVisible = true;

                // Optional trailing status icon (e.g. the "collected" checkmark) drawn just after the text.
                if (entry.TrailingIcon != 0)
                {
                    var textW = text.GetTextDrawSize(entry.Text, considerScale: false).X;
                    status.IconId = entry.TrailingIcon;
                    status.Size = new Vector2(StatusIconSize, StatusIconSize);
                    status.Position = new Vector2(textX + textW + IconTextGap,
                        y + (RowHeight - StatusIconSize) / 2f + IconOffsetY);
                    status.IsVisible = true;
                }
                else
                {
                    status.IsVisible = false;
                }

                y += RowHeight;
            }

            for (var i = entries.Count; i < _icons.Count; i++)
            {
                _icons[i].IsVisible = false;
                _texts[i].IsVisible = false;
                _statusIcons[i].IsVisible = false;
            }

            height = block.Resize(width, entries.Count * RowHeight) + BlockBottomPad;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "BetterTips: error measuring ownership block (skipped).");
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
            _log.Warning("BetterTips: ownership block saw a new ItemDetail without a finalize; rebuilding nodes.");
            DisposeNodes();
        }

        _attachedAddon = ptr;

        if (_block is null)
        {
            _block = new TooltipContentBlock { IsVisible = false, HeaderText = HeaderLabel };

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

    /// <summary>Get or lazily create the (leading icon, text, trailing status icon) nodes for row
    /// <paramref name="index" />.</summary>
    private (IconImageNode Icon, TextNode Text, IconImageNode Status) GetOrCreate(int index)
    {
        if (index < _icons.Count) return (_icons[index], _texts[index], _statusIcons[index]);

        var icon = new IconImageNode { FitTexture = true, IsVisible = false };
        icon.AttachNode(_block!);

        var text = new TextNode
        {
            FontType = FontType.Axis,
            FontSize = BodyFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextOutlineColor = OutlineColor,
            TextFlags = TextFlags.AutoAdjustNodeSize,
            IsVisible = false
        }; // TextColor is set per row from the entry's colour
        text.AttachNode(_block!);

        var status = new IconImageNode { FitTexture = true, IsVisible = false };
        status.AttachNode(_block!);

        _icons.Add(icon);
        _texts.Add(text);
        _statusIcons.Add(status);
        return (icon, text, status);
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
            _log.Error(ex, "BetterTips: error detaching ownership block on finalize.");
        }
    }

    private void DisposeNodes()
    {
        // Disposing the block disposes its children (icons + texts) too.
        _block?.Dispose();
        _block = null;
        _icons.Clear();
        _texts.Clear();
        _statusIcons.Clear();
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
            _log.Error(ex, "BetterTips: error disposing ownership block.");
        }
    }
}
