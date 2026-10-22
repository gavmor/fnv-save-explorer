namespace FnvSaveExplorer.Core;

/// <summary>The skill category a weapon's <see cref="WeaponStats.Skill"/> Actor Value maps to — just an
/// identification, carrying no ordering of its own. <b>Which tier ranks above which is a per-character
/// judgment call</b> (which skill the player actually tagged/invested in), not something derivable from the
/// save file (the save stores manual skill-point top-ups, not which skill is tagged — tagged skills get a flat
/// bonus that never appears as a stored deviation). <see cref="WeaponRanking.Rank"/> therefore takes the
/// priority order as an explicit parameter rather than assuming one.</summary>
public enum WeaponSkillTier { EnergyWeapons, Guns, Explosives, MeleeWeapons, Unarmed, Unknown }

public static class WeaponSkillTierExtensions
{
    /// <summary>Maps a raw FNV Actor Value (<see cref="WeaponStats.Skill"/>) to its skill tier. 34 = Energy
    /// Weapons, 35 = Explosives, 38 = Melee Weapons, 41 = Guns, 45 = Unarmed; anything else (including -1, a
    /// record with no usable <c>DNAM</c>) is <see cref="WeaponSkillTier.Unknown"/>.</summary>
    public static WeaponSkillTier Of(int actorValue) => actorValue switch
    {
        34 => WeaponSkillTier.EnergyWeapons,
        41 => WeaponSkillTier.Guns,
        35 => WeaponSkillTier.Explosives,
        38 => WeaponSkillTier.MeleeWeapons,
        45 => WeaponSkillTier.Unarmed,
        _ => WeaponSkillTier.Unknown,
    };
}

/// <summary>One weapon's inputs to the decision-matrix ranking: the character-facing identity (<see cref="Name"/>)
/// and inventory facts (<see cref="Stock"/>/<see cref="ConditionPct"/>, from a save's inventory — not this
/// class's concern) alongside its skill tier and combat throughput (from <see cref="WeaponStats"/>).</summary>
/// <param name="Tier">The weapon's skill category (see <see cref="WeaponSkillTierExtensions.Of"/>) — its
/// ranking priority is supplied separately to <see cref="WeaponRanking.Rank"/>, not implied by this value.</param>
/// <param name="Dps">Approximate damage output (<see cref="WeaponStats.ApproxDps"/>); null only when no combat
/// stats were found at all (ranks last within its tier — never guessed).</param>
/// <param name="Stock">Units on hand.</param>
/// <param name="ConditionPct">Current/max condition, 0–1; null when the stack carries no condition (ammo-less
/// consumables/thrown weapons track no durability).</param>
/// <param name="Weight">In-game weight; null when unknown. An undesirable value — lighter ranks higher.</param>
public sealed record WeaponRankingEntry(string Name, WeaponSkillTier Tier, double? Dps, int Stock, double? ConditionPct, double? Weight);

/// <summary>Ranks weapons by a fixed cascade of tiebreakers, evaluated strictly in order — each one only
/// decides anything when every tiebreaker before it is exactly equal:
/// <list type="number">
/// <item>skill tier, per <paramref name="tierPriority"/> (lower number ranks first) — a character-specific
/// judgment call the caller supplies, never assumed here (see <see cref="WeaponSkillTier"/>)</item>
/// <item>DPS, descending (unconfirmed/null always ranks last within its tier)</item>
/// <item>stock on hand, descending</item>
/// <item>condition, descending</item>
/// <item>weight, ascending (undesirable — lighter wins)</item>
/// </list>
/// Plain LINQ ordering: a one-shot ranking over a small, static, already-in-memory list of facts has no
/// incremental-rematching workload for a production-rule engine (NRules/Rete) to amortize, and no unbound
/// variables for a relational/logic-programming search (miniKanren) to solve.</summary>
public static class WeaponRanking
{
    /// <param name="entries">The weapons to rank.</param>
    /// <param name="tierPriority">Skill tier → priority rank (lower ranks first). A tier absent from this map
    /// sorts after every tier present in it.</param>
    public static IReadOnlyList<WeaponRankingEntry> Rank(
        IEnumerable<WeaponRankingEntry> entries, IReadOnlyDictionary<WeaponSkillTier, int> tierPriority) =>
        entries
            .OrderBy(w => tierPriority.TryGetValue(w.Tier, out var p) ? p : int.MaxValue)
            .ThenByDescending(w => w.Dps ?? double.NegativeInfinity)
            .ThenByDescending(w => w.Stock)
            .ThenByDescending(w => w.ConditionPct ?? 0)
            .ThenBy(w => w.Weight ?? double.PositiveInfinity)
            .ToList();
}
