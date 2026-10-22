namespace FnvSaveExplorer.Core;

/// <summary>Real combat stats for a save-space weapon FormID: the raw <c>WEAP.DATA</c>/<c>DNAM</c> fields, plus
/// — for an explosive weapon — its resolved real area damage. Nothing here is estimated, scraped, or looked up
/// externally; every field traces to a specific subrecord of a specific game-file record (see
/// <see cref="RawWeaponStats"/>/<see cref="RawExplosion"/> for exact byte provenance).</summary>
/// <param name="FormId">Save-space FormID.</param>
/// <param name="Skill">Raw FNV Actor Value enum (34=Energy Weapons, 35=Explosives, 38=Melee Weapons, 41=Guns,
/// 45=Unarmed; -1 if unknown) — see <see cref="WeaponSkillTierExtensions.Of"/> for the ranking-tier mapping.</param>
/// <param name="BaseDamage">WEAP.DATA.BaseDamage — a direct-hit value for most weapons, but only a placeholder
/// for an explosive one (see <see cref="ExplosionDamage"/>).</param>
/// <param name="ClipSize">WEAP.DATA.ClipSize.</param>
/// <param name="Weight">WEAP.DATA.Weight, in-game pounds.</param>
/// <param name="FireRate">WEAP.DNAM.FireRate.</param>
/// <param name="StrengthRequirement">WEAP.DNAM.StrengthRequirement.</param>
/// <param name="SkillRequirement">WEAP.DNAM.SkillRequirement.</param>
/// <param name="ExplosionDamage">The real area damage, resolved via <c>DNAM.Projectile -&gt;
/// PROJ.DATA.Explosion -&gt; EXPL.DATA.Damage</c> — null when the weapon has no projectile/explosion chain
/// (a direct-hit weapon; <see cref="BaseDamage"/> is the real figure for those) or the chain didn't resolve.</param>
/// <param name="ExplosionRadius">EXPL.DATA.Radius (engine units — not verified against yards), paired with
/// <see cref="ExplosionDamage"/>.</param>
/// <param name="AmmoFormIds">Save-space FormIDs of every <c>AMMO</c> this weapon can fire — usually one, but
/// several when <c>NAM0</c> points at an <c>FLST</c> ammo list (shared HP/Hand Load/Match-grade variants,
/// flattened here so any member counts). Empty for a self-ammo'd weapon (thrown/placed ordnance — Dynamite,
/// Frag Grenade/Mine, Throwing Spear — which consumes its own inventory stack, not a separate ammo type).</param>
public sealed record WeaponStats(
    uint FormId, int Skill, int BaseDamage, int ClipSize, float Weight, float FireRate,
    int StrengthRequirement, int SkillRequirement, float? ExplosionDamage, float? ExplosionRadius,
    IReadOnlyList<uint> AmmoFormIds)
{
    /// <summary>Approximate sustained damage output: <c>(ExplosionDamage ?? BaseDamage) × FireRate</c>. Uses the
    /// real resolved area damage for an explosive weapon rather than its WEAP record's own placeholder
    /// <see cref="BaseDamage"/>. This is a simple per-shot-damage × rate-of-fire model — it does not account for
    /// reload time, magazine size, or (for thrown/placed ordnance) throw cadence, so treat it as a comparison
    /// aid, not a verified in-combat DPS figure.</summary>
    public double ApproxDps => (ExplosionDamage ?? BaseDamage) * FireRate;
}

/// <summary>
/// Real weapon combat stats (damage, skill, strength/skill requirements, weight, and — for explosives — the
/// resolved area damage), read directly from the game's <c>WEAP</c>/<c>PROJ</c>/<c>EXPL</c> records.
///
/// <para><b>Deliberately separate from <see cref="PluginDatabase"/>.</b> A caller that only wants a FormID's
/// display name or Pip-Boy category doesn't need combat stats (or the cost of walking the <c>PROJ</c>/<c>EXPL</c>
/// groups neither <see cref="PluginDatabase"/> nor any existing CLI command reads), and a caller that wants
/// combat stats — the weapon decision-matrix feature — doesn't need quest/dialogue/actor-script machinery.
/// Interface Segregation in practice: each concern gets its own minimal surface rather than one database
/// growing a property for every feature that ever touches a FormID.</para>
///
/// <para>Still shares the one piece of logic every per-plugin-database builder genuinely needs —
/// <see cref="PluginFormIdRemap"/> — rather than depending on <see cref="PluginDatabase"/> to reuse it.</para>
/// </summary>
public sealed class WeaponStatsDatabase
{
    private readonly IReadOnlyDictionary<uint, WeaponStats> _stats;

    /// <summary>The load-order plugins that were found and successfully parsed.</summary>
    public IReadOnlyList<string> ResolvedPlugins { get; }

    private WeaponStatsDatabase(Dictionary<uint, WeaponStats> stats, IReadOnlyList<string> resolved)
    {
        _stats = stats;
        ResolvedPlugins = resolved;
    }

    /// <summary>An empty database; every <see cref="Stats"/> lookup returns null.</summary>
    public static readonly WeaponStatsDatabase Empty = new([], []);

    /// <summary>Builds a database for <paramref name="save"/>, auto-detecting the game <c>Data</c> folder (or
    /// using an override) and, when given, an MO2 <paramref name="modsFolder"/> for mod plugins.</summary>
    public static WeaponStatsDatabase ForSave(FalloutSave save, string? dataFolderOverride = null, string? modsFolder = null)
    {
        var folder = GameDataLocator.FindDataFolder(dataFolderOverride);
        var paths = PluginDatabase.CollectPlugins(folder, modsFolder);
        return paths.Count == 0 ? Empty : Build(save.Plugins, paths);
    }

    /// <summary>Builds a database from a load order and an explicit game <c>Data</c> folder.</summary>
    public static WeaponStatsDatabase Build(IReadOnlyList<string> loadOrder, string dataFolder) =>
        Build(loadOrder, PluginDatabase.CollectPlugins(dataFolder, null));

    /// <summary>Builds a database from a load order and a plugin-name → file-path map (used in tests).</summary>
    public static WeaponStatsDatabase Build(IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> pluginPaths)
    {
        // Every plugin's WEAP/PROJ/EXPL local ids are re-keyed into the SAME save-space keyspace using that
        // plugin's OWN remap table (its own Masters list) — exactly how PluginDatabase re-keys names. A WEAP's
        // Projectile field and a PROJ's Explosion field are both local to the record that CONTAINS them, so once
        // everything lives in save space, a weapon's chain resolves with plain dictionary lookups — no need to
        // re-open another plugin file mid-resolution, even when (as with a DLC's weapon pointing at a base-game
        // explosion) the chain crosses plugins.
        var projectileExplosion = new Dictionary<uint, uint>(); // save-space PROJ FormId -> save-space EXPL FormId (0 = none)
        var explosions = new Dictionary<uint, (float Damage, float Radius)>(); // save-space EXPL FormId -> data
        var formLists = new Dictionary<uint, List<uint>>(); // save-space FLST FormId -> save-space member FormIds
        var rawWeapons = new List<(uint SaveFormId, RawWeaponStats Raw, uint SaveProjectileId, uint SaveAmmoId)>(); // later plugin overrides win, like names
        var resolved = new List<string>();

        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < loadOrder.Count; i++)
            indexOf[loadOrder[i]] = i;

        for (var i = 0; i < loadOrder.Count; i++)
        {
            if (!pluginPaths.TryGetValue(loadOrder[i], out var file))
                continue;

            TesPlugin plugin;
            try
            {
                plugin = TesPlugin.Load(file, readWeaponStats: true);
            }
            catch (SaveFormatException)
            {
                continue; // skip an unreadable plugin rather than failing the whole build
            }

            var remap = PluginFormIdRemap.BuildTable(plugin.Masters, i, indexOf);

            foreach (var proj in plugin.Projectiles)
            {
                if (PluginFormIdRemap.Resolve(proj.FormId, remap) is not { } saveProjId)
                    continue;
                var saveExplId = proj.ExplosionFormId == 0 ? 0 : PluginFormIdRemap.Resolve(proj.ExplosionFormId, remap) ?? 0;
                projectileExplosion[saveProjId] = saveExplId;
            }
            foreach (var expl in plugin.Explosions)
                if (PluginFormIdRemap.Resolve(expl.FormId, remap) is { } saveExplId)
                    explosions[saveExplId] = (expl.Damage, expl.Radius);
            // FLST members are local to the SAME plugin as the list itself (same rule as Projectile/Explosion).
            foreach (var flst in plugin.FormLists)
                if (PluginFormIdRemap.Resolve(flst.FormId, remap) is { } saveFlstId)
                    formLists[saveFlstId] = flst.Members
                        .Select(m => PluginFormIdRemap.Resolve(m, remap))
                        .Where(m => m is not null)
                        .Select(m => m!.Value)
                        .ToList();
            foreach (var w in plugin.WeaponRecords)
            {
                if (PluginFormIdRemap.Resolve(w.FormId, remap) is not { } saveWeaponId)
                    continue;
                var saveProjectileId = w.ProjectileFormId == 0 ? 0 : PluginFormIdRemap.Resolve(w.ProjectileFormId, remap) ?? 0;
                var saveAmmoId = w.AmmoFormId == 0 ? 0 : PluginFormIdRemap.Resolve(w.AmmoFormId, remap) ?? 0;
                rawWeapons.Add((saveWeaponId, w, saveProjectileId, saveAmmoId));
            }

            resolved.Add(loadOrder[i]);
        }

        var stats = new Dictionary<uint, WeaponStats>();
        foreach (var (saveFormId, raw, saveProjectileId, saveAmmoId) in rawWeapons)
        {
            float? explDamage = null, explRadius = null;
            if (saveProjectileId != 0 && projectileExplosion.TryGetValue(saveProjectileId, out var explId)
                && explId != 0 && explosions.TryGetValue(explId, out var ed))
            {
                explDamage = ed.Damage;
                explRadius = ed.Radius;
            }
            // AmmoFormId is either an FLST (flatten to its members) or a direct AMMO reference (single entry),
            // or 0 for a self-ammo'd weapon (empty - no separate ammo stock to look up).
            IReadOnlyList<uint> ammoFormIds = saveAmmoId == 0 ? []
                : formLists.TryGetValue(saveAmmoId, out var members) ? members
                : [saveAmmoId];
            stats[saveFormId] = new WeaponStats(
                saveFormId, raw.Skill, raw.BaseDamage, raw.ClipSize, raw.Weight, raw.FireRate,
                raw.StrengthRequirement, raw.SkillRequirement, explDamage, explRadius, ammoFormIds);
        }

        return new WeaponStatsDatabase(stats, resolved);
    }

    /// <summary>Real combat stats for a save-space FormID, or null if it isn't a known <c>WEAP</c> form (or the
    /// game Data folder wasn't found).</summary>
    public WeaponStats? Stats(uint formId) => _stats.TryGetValue(formId, out var s) ? s : null;
}
