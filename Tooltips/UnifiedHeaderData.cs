using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace BetterTips.Tooltips;

/// <summary>
///     The static data the unified item-header section renders, gathered from the Lumina <see cref="Item" />
///     sheet (name, icon, category, the big primary stat, item level, required level, equippable-job icons).
///     All fields are sheet-derived — no per-instance state — so the same data drives both the live tooltip
///     and the editor preview (the preview builds one from its sample item instead of a hovered one).
/// </summary>
/// <param name="JobIconIds">
///     Icon ids (62100 + ClassJob) for the jobs that can equip the item, or <b>empty</b> when the item's
///     class/job category is broad (All Classes, Disciples of War/Magic, …) — those show <see cref="ClassJobText" />
///     instead.
/// </param>
/// <param name="ClassJobText">The class/job category name, shown as text when there are no job icons.</param>
/// <param name="Rarity">The item's rarity (1 normal … 7 aetherial) — drives the name color.</param>
/// <param name="CurrentJobIconId">The player's current-job icon id (62100 + job), 0 if none — gets a green outline.</param>
/// <param name="CurrentJobEquippable">Whether the player's current job can equip the item (greens the text fallback).</param>
/// <param name="MeetsLevelRequirement">Whether the player's current job is high enough level to equip the item;
/// <c>false</c> reddens the Req Level value. Defaults to <c>true</c> when no level is known (logged out / preview).</param>
public sealed record UnifiedHeaderData(
    string Name,
    uint IconId,
    string Category,
    string PrimaryValue,
    string PrimaryLabel,
    uint ItemLevel,
    uint RequiredLevel,
    IReadOnlyList<uint> JobIconIds,
    string ClassJobText,
    byte Rarity,
    uint CurrentJobIconId,
    bool CurrentJobEquippable,
    bool MeetsLevelRequirement)
{
    /// <summary>
    ///     The rendered native name as raw SeString bytes (payloads preserved → the game's glyphs render, e.g.
    ///     the HQ mark). Set by the live block from node <c>#33</c>; <c>null</c> in the preview, which falls
    ///     back to the payload-free <see cref="Name" />.
    /// </summary>
    public byte[]? NameRaw { get; init; }

    /// <summary>Job icon ids start here (62100 + ClassJob row id).</summary>
    private const uint JobIconBase = 62100;

    /// <summary>Above this many equippable jobs the category is treated as "broad" — show the class/job text
    /// instead of icons. The job row wraps now, so this can be generous while still folding the genuinely broad
    /// categories (All Classes, Disciples of War or Magic, …) into text.</summary>
    private const int MaxJobIcons = 16;

    /// <summary>Highest ClassJob row id we map to a category column (PCT). Newer jobs are simply skipped.</summary>
    private const uint MaxClassJobId = 42;

    /// <summary>
    ///     Build the data for the currently hovered item, or <c>null</c> when nothing resolvable is hovered.
    ///     Mirrors <c>GearSetBlockProvider</c>'s HQ-offset handling.
    /// </summary>
    public static unsafe UnifiedHeaderData? FromHoveredItem(IGameGui gameGui, IDataManager data, IObjectTable objects)
    {
        var hovered = gameGui.HoveredItem;
        if (hovered == 0) return null;

        var itemId = (uint)(hovered >= 1_000_000 ? hovered - 1_000_000 : hovered);
        if (!data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return null;

        // Only equippable items get the unified (gear-style) header; consumables/materials keep the native one.
        if (item.EquipSlotCategory.RowId == 0) return null;

        var (label, value) = PrimaryStat(item);
        var category = item.ClassJobCategory.Value;

        var playerState = PlayerState.Instance();
        uint currentJob = playerState is not null ? (uint)playerState->CurrentClassJobId : 0u;

        // The current job's level (0 when logged out) — reddens the Req Level value when too low to equip.
        var currentLevel = objects.LocalPlayer?.Level ?? 0;
        var meetsLevel = currentLevel == 0 || item.LevelEquip <= currentLevel;

        return new UnifiedHeaderData(
            item.Name.ToString(),
            item.Icon,
            item.ItemUICategory.Value.Name.ToString(),
            value,
            label,
            item.LevelItem.RowId,
            item.LevelEquip,
            ResolveJobIcons(category),
            category.Name.ToString(),
            item.Rarity,
            currentJob != 0 ? JobIconBase + currentJob : 0,
            currentJob != 0 && IsInCategory(category, currentJob),
            meetsLevel);
    }

    /// <summary>The big primary stat: weapons show the higher of Physical/Magic damage, armor the higher of
    /// Physical/Magic defense; anything else has none.</summary>
    private static (string Label, string Value) PrimaryStat(Item item)
    {
        if (item.DamagePhys > 0 || item.DamageMag > 0)
            return item.DamageMag > item.DamagePhys
                ? ("Magic Damage", item.DamageMag.ToString())
                : ("Physical Attack", item.DamagePhys.ToString());

        if (item.DefensePhys > 0 || item.DefenseMag > 0)
            return item.DefenseMag > item.DefensePhys
                ? ("Magic Defense", item.DefenseMag.ToString())
                : ("Defense", item.DefensePhys.ToString());

        return (string.Empty, string.Empty);
    }

    /// <summary>
    ///     The equippable-job icon ids from a class/job category. Returns empty for a broad category (none
    ///     match, or more than <see cref="MaxJobIcons" /> do — e.g. "Disciples of War or Magic").
    /// </summary>
    private static IReadOnlyList<uint> ResolveJobIcons(ClassJobCategory category)
    {
        var icons = new List<uint>();
        for (uint id = 1; id <= MaxClassJobId; id++)
        {
            if (!IsInCategory(category, id)) continue;
            icons.Add(JobIconBase + id);
            if (icons.Count > MaxJobIcons) return [];
        }

        return icons;
    }

    /// <summary>Whether a ClassJob (by row id) is a member of the category (its per-job boolean column).</summary>
    private static bool IsInCategory(ClassJobCategory c, uint classJobId) => classJobId switch
    {
        1 => c.GLA, 2 => c.PGL, 3 => c.MRD, 4 => c.LNC, 5 => c.ARC, 6 => c.CNJ, 7 => c.THM,
        8 => c.CRP, 9 => c.BSM, 10 => c.ARM, 11 => c.GSM, 12 => c.LTW, 13 => c.WVR, 14 => c.ALC,
        15 => c.CUL, 16 => c.MIN, 17 => c.BTN, 18 => c.FSH, 19 => c.PLD, 20 => c.MNK, 21 => c.WAR,
        22 => c.DRG, 23 => c.BRD, 24 => c.WHM, 25 => c.BLM, 26 => c.ACN, 27 => c.SMN, 28 => c.SCH,
        29 => c.ROG, 30 => c.NIN, 31 => c.MCH, 32 => c.DRK, 33 => c.AST, 34 => c.SAM, 35 => c.RDM,
        36 => c.BLU, 37 => c.GNB, 38 => c.DNC, 39 => c.RPR, 40 => c.SGE, 41 => c.VPR, 42 => c.PCT,
        _ => false
    };
}
