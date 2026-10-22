using System.Buffers.Binary;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class WeaponStatsTests
{
    /// <summary>Builds a 204-byte WEAP <c>DNAM</c> struct with only the fields this reader decodes set
    /// (everything else zero) — offsets per https://tes5edit.github.io/fopdoc/FalloutNV/Records/WEAP.html.</summary>
    private static byte[] Dnam(uint projectileFormId, float fireRate, int skill, int strengthReq, int skillReq)
    {
        var b = new byte[204];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36, 4), projectileFormId);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(64, 4), fireRate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(104, 4), skill);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(168, 4), (uint)strengthReq);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(200, 4), (uint)skillReq);
        return b;
    }

    /// <summary>Builds a 15-byte WEAP <c>DATA</c> struct: Value(0)/Health/Weight/BaseDamage/ClipSize.</summary>
    private static byte[] WeapData(int health, float weight, short baseDamage, byte clipSize)
    {
        var b = new byte[15];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4, 4), health);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8, 4), weight);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12, 2), baseDamage);
        b[14] = clipSize;
        return b;
    }

    private static byte[] Nam0(uint ammoFormId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, ammoFormId);
        return b;
    }

    /// <summary>Builds a 40-byte PROJ <c>DATA</c> struct with only Explosion (offset 36) set.</summary>
    private static byte[] ProjData(uint explosionFormId)
    {
        var b = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36, 4), explosionFormId);
        return b;
    }

    /// <summary>Builds a 12-byte EXPL <c>DATA</c> struct: Force(0)/Damage/Radius.</summary>
    private static byte[] ExplData(float damage, float radius)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4, 4), damage);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8, 4), radius);
        return b;
    }

    private static byte[] Lnam(uint formId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, formId);
        return b;
    }

    [Fact]
    public void TesPlugin_decodes_real_WEAP_combat_stats_from_DATA_and_DNAM()
    {
        var bytes = EsmBuilder.Plugin([],
            new TestRecord("WEAP", 0x00000ABC, Edid: "TestRifle", Full: "Test Rifle", Subs:
            [
                ("DATA", WeapData(health: 200, weight: 4.5f, baseDamage: 26, clipSize: 6)),
                ("DNAM", Dnam(projectileFormId: 0, fireRate: 1.0f, skill: 41, strengthReq: 3, skillReq: 25)),
            ]));

        var plugin = TesPlugin.Read(new MemoryStream(bytes), "Test.esm", readWeaponStats: true);

        var w = Assert.Single(plugin.WeaponRecords);
        Assert.Equal(0x00000ABCu, w.FormId);
        Assert.Equal(26, w.BaseDamage);
        Assert.Equal(6, w.ClipSize);
        Assert.Equal(4.5f, w.Weight);
        Assert.Equal(1.0f, w.FireRate);
        Assert.Equal(41, w.Skill); // Guns
        Assert.Equal(3, w.StrengthRequirement);
        Assert.Equal(25, w.SkillRequirement);
        // The same DATA subrecord also feeds Forms' existing max-condition (Health) field - must still work.
        Assert.Equal(200, plugin.Forms.Single(f => f.LocalFormId == 0x00000ABC).Health);
    }

    [Fact]
    public void TesPlugin_resolves_explosive_weapon_area_damage_via_projectile_and_explosion_chain()
    {
        // Mirrors a real in-game case (Frag Grenade): WEAP.DATA.BaseDamage is a direct-hit placeholder (1);
        // the real area damage only reaches through DNAM.Projectile -> PROJ.DATA.Explosion -> EXPL.DATA.Damage.
        var bytes = EsmBuilder.Plugin([],
            new TestRecord("WEAP", 0x00000001, Edid: "TestGrenade", Full: "Test Grenade", Subs:
            [
                ("DATA", WeapData(health: 0, weight: 0.5f, baseDamage: 1, clipSize: 1)),
                ("DNAM", Dnam(projectileFormId: 0x00000002, fireRate: 1.0f, skill: 35, strengthReq: 2, skillReq: 25)),
            ]),
            new TestRecord("PROJ", 0x00000002, Edid: "TestGrenadeProjectile", Full: null, Subs:
            [
                ("DATA", ProjData(explosionFormId: 0x00000003)),
            ]),
            new TestRecord("EXPL", 0x00000003, Edid: "TestGrenadeExplosion", Full: null, Subs:
            [
                ("DATA", ExplData(damage: 125f, radius: 900f)),
            ]));

        var plugin = TesPlugin.Read(new MemoryStream(bytes), "Test.esm", readWeaponStats: true);
        var db = WeaponStatsDatabase.Build(["Test.esm"], new Dictionary<string, string> { ["Test.esm"] = Write(bytes) });

        var stats = db.Stats(0x00000001);
        Assert.NotNull(stats);
        Assert.Equal(1, stats!.BaseDamage); // the placeholder, unchanged
        Assert.Equal(125f, stats.ExplosionDamage); // the real area damage
        Assert.Equal(900f, stats.ExplosionRadius);
        Assert.Equal(125.0, stats.ApproxDps); // uses ExplosionDamage, not BaseDamage, for an explosive
    }

    [Fact]
    public void WeaponStatsDatabase_resolves_cross_plugin_projectile_chain_via_each_files_own_masters()
    {
        // Mirrors Mercenary's Grenade Rifle: the WEAP lives in a child plugin, but its projectile/explosion
        // chain resolves into the BASE plugin. Getting this backwards (or using the wrong file's remap table
        // for the second hop) would silently return null or the wrong explosion - exactly the bug class this
        // feature depends on not having.
        using var dir = new TempDataFolder();
        dir.Write("Base.esm", EsmBuilder.Plugin([],
            new TestRecord("PROJ", 0x00000010, Edid: "BaseProjectile", Full: null, Subs:
            [
                ("DATA", ProjData(explosionFormId: 0x00000011)),
            ]),
            new TestRecord("EXPL", 0x00000011, Edid: "BaseExplosion", Full: null, Subs:
            [
                ("DATA", ExplData(damage: 100f, radius: 750f)),
            ])));
        dir.Write("Child.esp", EsmBuilder.Plugin(["Base.esm"],
            new TestRecord("WEAP", 0x01000020, Edid: "ChildGrenadeRifle", Full: "Child Grenade Rifle", Subs:
            [
                ("DATA", WeapData(health: 100, weight: 5.5f, baseDamage: 2, clipSize: 1)),
                // Child.esp's own Masters = ["Base.esm"], so local high byte 0x00 means "master index 0" =
                // Base.esm's PROJ 0x000010 - NOT this plugin's own form.
                ("DNAM", Dnam(projectileFormId: 0x00000010, fireRate: 1.0f, skill: 35, strengthReq: 3, skillReq: 0)),
            ])));

        var paths = PluginDatabase.CollectPlugins(dir.Path, null);
        var db = WeaponStatsDatabase.Build(["Base.esm", "Child.esp"], paths);

        // Child.esp is save index 1, so its own form 0x01000020 remaps to save-space 0x01000020 (self: local
        // high byte 0x01 == its masters.Count==1).
        var stats = db.Stats(0x01000020);
        Assert.NotNull(stats);
        Assert.Equal(100f, stats!.ExplosionDamage);
        Assert.Equal(750f, stats.ExplosionRadius);
    }

    [Fact]
    public void WeaponStatsDatabase_flattens_an_FLST_ammo_list_so_any_member_counts_as_stock()
    {
        // Mirrors a real shared-ammo-type weapon (e.g. .357 Magnum Revolver): WEAP.NAM0 points at an FLST
        // (HP/Hand Load/Match-grade variants), not a single AMMO record directly.
        var bytes = EsmBuilder.Plugin([],
            new TestRecord("WEAP", 0x00000001, Edid: "TestRevolver", Full: "Test Revolver", Subs:
            [
                ("DATA", WeapData(health: 200, weight: 2.0f, baseDamage: 26, clipSize: 6)),
                ("DNAM", Dnam(projectileFormId: 0, fireRate: 1.0f, skill: 41, strengthReq: 3, skillReq: 0)),
                ("NAM0", Nam0(0x00000002)),
            ]),
            new TestRecord("FLST", 0x00000002, Edid: "TestAmmoList", Full: null, Subs:
            [
                ("LNAM", Lnam(0x00000003)),
                ("LNAM", Lnam(0x00000004)),
            ]));

        var db = WeaponStatsDatabase.Build(["Test.esm"], new Dictionary<string, string> { ["Test.esm"] = Write(bytes) });

        var stats = db.Stats(0x00000001);
        Assert.NotNull(stats);
        Assert.Equal([0x00000003u, 0x00000004u], stats!.AmmoFormIds);
    }

    [Fact]
    public void WeaponStatsDatabase_returns_null_for_an_unknown_formid()
    {
        var db = WeaponStatsDatabase.Build(["Test.esm"], new Dictionary<string, string>
        {
            ["Test.esm"] = Write(EsmBuilder.Plugin([], new TestRecord("WEAP", 0x00000001, Edid: "X", Full: "X"))),
        });

        Assert.Null(db.Stats(0x00099999));
    }

    [Fact]
    public void WeaponRanking_orders_by_tier_then_dps_then_stock_then_condition_then_weight()
    {
        var a = new WeaponRankingEntry("A-lower-tier", WeaponSkillTier.Guns, Dps: 10, Stock: 1, ConditionPct: 1.0, Weight: 1.0);
        var b = new WeaponRankingEntry("B-higher-tier-wins", WeaponSkillTier.EnergyWeapons, Dps: 1, Stock: 1, ConditionPct: 0.1, Weight: 9.0);
        var c = new WeaponRankingEntry("C-same-tier-higher-dps", WeaponSkillTier.Guns, Dps: 50, Stock: 1, ConditionPct: 1.0, Weight: 1.0);
        var d = new WeaponRankingEntry("D-unconfirmed-dps-ranks-last-in-tier", WeaponSkillTier.Guns, Dps: null, Stock: 999, ConditionPct: 1.0, Weight: 0.1);
        var e1 = new WeaponRankingEntry("E1-tiebreak-by-stock", WeaponSkillTier.MeleeWeapons, Dps: 5, Stock: 10, ConditionPct: 0.5, Weight: 1.0);
        var e2 = new WeaponRankingEntry("E2-tiebreak-by-stock", WeaponSkillTier.MeleeWeapons, Dps: 5, Stock: 1, ConditionPct: 0.9, Weight: 1.0);
        var f1 = new WeaponRankingEntry("F1-tiebreak-by-condition", WeaponSkillTier.Explosives, Dps: 5, Stock: 1, ConditionPct: 0.9, Weight: 1.0);
        var f2 = new WeaponRankingEntry("F2-tiebreak-by-condition", WeaponSkillTier.Explosives, Dps: 5, Stock: 1, ConditionPct: null, Weight: 1.0);
        var g1 = new WeaponRankingEntry("G1-tiebreak-by-weight-lighter-wins", WeaponSkillTier.Unarmed, Dps: 5, Stock: 1, ConditionPct: 1.0, Weight: 0.5);
        var g2 = new WeaponRankingEntry("G2-tiebreak-by-weight-lighter-wins", WeaponSkillTier.Unarmed, Dps: 5, Stock: 1, ConditionPct: 1.0, Weight: 5.0);

        var tierPriority = new Dictionary<WeaponSkillTier, int>
        {
            [WeaponSkillTier.EnergyWeapons] = 0,
            [WeaponSkillTier.Guns] = 1,
            [WeaponSkillTier.Explosives] = 2,
            [WeaponSkillTier.MeleeWeapons] = 3,
            [WeaponSkillTier.Unarmed] = 4,
        };

        var ranked = WeaponRanking.Rank([a, b, c, d, e1, e2, f1, f2, g1, g2], tierPriority);
        var names = ranked.Select(w => w.Name).ToList();

        Assert.Equal("B-higher-tier-wins", names[0]); // tier beats everything, even worse DPS/condition
        Assert.Equal("C-same-tier-higher-dps", names[1]); // within Guns, higher DPS wins
        Assert.Equal("A-lower-tier", names[2]);
        Assert.Equal("D-unconfirmed-dps-ranks-last-in-tier", names[3]); // null DPS never outranks a real figure
        Assert.True(names.IndexOf("E1-tiebreak-by-stock") < names.IndexOf("E2-tiebreak-by-stock")); // same tier+dps -> higher stock wins
        Assert.True(names.IndexOf("F1-tiebreak-by-condition") < names.IndexOf("F2-tiebreak-by-condition")); // null condition ranks last
        Assert.True(names.IndexOf("G1-tiebreak-by-weight-lighter-wins") < names.IndexOf("G2-tiebreak-by-weight-lighter-wins")); // lighter wins
    }

    /// <summary>Writes plugin bytes to a throwaway temp file (for the <see cref="WeaponStatsDatabase.Build(IReadOnlyList{string},IReadOnlyDictionary{string,string})"/>
    /// overload, which reads from disk).</summary>
    private static string Write(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "fnv-weap-test-" + Guid.NewGuid().ToString("N") + ".esm");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
