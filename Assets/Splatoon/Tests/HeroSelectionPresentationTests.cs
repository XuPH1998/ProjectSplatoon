#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class HeroSelectionPresentationTests
    {
        [SetUp] public void Setup()
        {
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
            WeaponConfigService.Current.Clear();
        }
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static CharacterPresentationProfile Profile(int id)
        {
            string name = GameplayConfig.GetHero(id).CharacterPrefabAddress.Split('/').Last();
            return AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>($"Assets/GameResource/Characters/{name}/{name}Presentation.asset");
        }
        [Test] public void NineHeroesHaveDistinctNamedPortraitsRegisteredForPlayers()
        {
            string[] names = {"紫苑", "夜雀", "白凛", "隼音", "月兔", "焰橙", "沫澜", "铃芽", "泡霰"};
            var heroes = LubanConfigService.Current.Tables.TbHero.DataList;
            Assert.That(heroes.Select(h => h.DisplayName), Is.EqualTo(names));
            Assert.That(heroes.Select(h => h.PortraitAddress).Distinct().Count(), Is.EqualTo(9));
            foreach (var hero in heroes)
            {
                Assert.That(hero.WeaponTypeName, Is.Not.Empty);
                var entry = AddressableAssetSettingsDefaultObject.Settings.groups.SelectMany(g => g.entries).Single(e => e.address == hero.PortraitAddress);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.AssetPath);
                Assert.That(texture, Is.Not.Null); Assert.That(texture.width, Is.EqualTo(texture.height));
                var importer = (TextureImporter)AssetImporter.GetAtPath(entry.AssetPath);
                Assert.That(texture.width, Is.GreaterThanOrEqualTo(512), "portrait must remain sharp at 1080p");
                Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.Default));
                using var stream = File.OpenRead(entry.AssetPath);
                byte[] png = new byte[24]; stream.Read(png, 0, png.Length);
                Assert.That(png[16] * 16777216 + png[17] * 65536 + png[18] * 256 + png[19], Is.EqualTo(1024));
            }
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void OnlyFourStatsAndCorrectWeaponSpecificUnits(int id)
        {
            var w = GameplayConfig.GetWeapon(id); var g = GameplayConfig.Global;
            var values = HeroSelectionStats.Create(w, Profile(id), g.AimCorrectionDistance, g.AimFarCorrectionDistance);
            Assert.That(values.Select(s => s.Label), Is.EqualTo(new[]{"伤害", "射速", "最大散布", "平射射程"}));
            Assert.That(values[3].Value, Does.Contain("米"));
            if (id == 3) { Assert.That(values[0].Note, Does.Contain("8 颗")); Assert.That(values[1].Value, Does.Contain("次齐射/秒")); }
            if (id == 5) { Assert.That(HeroSelectionStats.MaximumGroundSpread(w), Is.EqualTo(2)); Assert.That(HeroSelectionStats.FullChargeRate(w), Is.LessThan(w.FireRate)); }
            if (id == 6) Assert.That(values[1].Note, Is.EqualTo("连射阶段"));
            if (id == 7) { Assert.That(values[0].Note, Does.Contain("4 颗")); Assert.That(values[1].Value, Does.Contain("组/秒")); Assert.That(values[3].Note, Does.Contain("弹跳")); }
            Directory.CreateDirectory("Reports/HeroSelection");
            File.WriteAllLines($"Reports/HeroSelection/stats-{id}.txt", new[] { GameplayConfig.GetHero(id).DisplayName }
                .Concat(values.Select(s => s.Label + ": " + s.Value + " (" + s.Note + ")")));
        }
        [TestCase(1,0)] [TestCase(2,0)] [TestCase(3,0)] [TestCase(4,0)] [TestCase(5,0)] [TestCase(5,1)] [TestCase(6,0)] [TestCase(6,1)] [TestCase(7,0)]
        public void CalculatedRangeMatchesRealProjectileGroundCollision(int id, int full)
        {
            var w = GameplayConfig.GetWeapon(id); var g = GameplayConfig.Global; var profile = Profile(id);
            float charge = full == 1 ? 1 : HeroFlatRange.MinimumCharge(w);
            var floor = new GameObject("Reference range floor");
            // Isolated location keeps the measurement independent of the user's open scene.
            var offset = new Vector3(12000, 1000, 12000);
            floor.transform.position = offset + Vector3.down * .5f;
            floor.AddComponent<BoxCollider>().size = new Vector3(1000, 1, 1000);
            try
            {
                Physics.SyncTransforms();
                for (byte muzzle = 0; muzzle < (id == 2 ? 2 : 1); muzzle++)
                {
                    var shot = HeroFlatRange.ReferenceShot(w, profile, charge, g.AimCorrectionDistance, g.AimFarCorrectionDistance, muzzle);
                    var expected = HeroFlatRange.Calculate(shot, w);
                    shot.Id = 1; shot.HeroId = id; shot.Shooter = ulong.MaxValue; shot.Origin += offset;
                    var service = new InkProjectileService(); service.SpawnForMeasurement(shot); service.Simulate(w.Lifetime);
                    Assert.That(service.Impacts.Count, Is.EqualTo(1));
                    var impact = service.Impacts.Single(); Assert.That(impact.Hit, Is.EqualTo(expected.HitGround));
                    var delta = impact.Position - shot.Origin;
                    Assert.That(new Vector2(delta.x, delta.z).magnitude, Is.EqualTo(expected.Distance).Within(.05f));
                }
                if (id == 2)
                {
                    var right = HeroFlatRange.Calculate(HeroFlatRange.ReferenceShot(w, profile, charge, g.AimCorrectionDistance, g.AimFarCorrectionDistance), w);
                    var left = HeroFlatRange.Calculate(HeroFlatRange.ReferenceShot(w, profile, charge, g.AimCorrectionDistance, g.AimFarCorrectionDistance, 1), w);
                    Assert.That(HeroFlatRange.Calculate(w, profile, charge, g.AimCorrectionDistance, g.AimFarCorrectionDistance).Distance, Is.EqualTo((right.Distance + left.Distance) / 2).Within(.0001));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(floor); Physics.SyncTransforms(); }
        }
        [Test] public void ZeroGravityAndShortLifetimeAreFiniteAndMarkedAsLifetimeLimited()
        {
            var asset = UnityEngine.Object.Instantiate(WeaponConfigService.Current.Source(1) ?? AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(1).WeaponConfigPath));
            try
            {
                foreach (bool zeroGravity in new[] {true, false})
                {
                    asset.projectileGravity = zeroGravity ? 0 : 18; asset.lifetime = zeroGravity ? 10 : .01f;
                    var w = asset.Snapshot(); var result = HeroFlatRange.Calculate(w, Profile(1), 0, 6, 50);
                    Assert.That(result.HitGround, Is.False); Assert.That(float.IsFinite(result.Distance), Is.True);
                    Assert.That(result.Display, Does.Contain("寿命上限")); Assert.That(result.FlightSeconds, Is.EqualTo(w.Lifetime));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }
        [Test] public void SummaryCacheReusesResultsAndInvalidatesWeaponOrMuzzleChanges()
        {
            var cache = new HeroSelectionStatsCache(); var profile = UnityEngine.Object.Instantiate(Profile(1)); var w = GameplayConfig.GetWeapon(1);
            try
            {
                var first = cache.Get(1, 1, w, profile, 6, 50);
                Assert.That(cache.Get(1, 1, w, profile, 6, 50), Is.SameAs(first));
                var revision = cache.Get(1, 2, w, profile, 6, 50); Assert.That(revision, Is.Not.SameAs(first));
                profile.MuzzlePosition += Vector3.up;
                var moved = cache.Get(1, 2, w, profile, 6, 50); Assert.That(moved[3].Value, Is.Not.EqualTo(revision[3].Value));
                cache.Clear(); Assert.That(cache.Get(1, 2, w, profile, 6, 50), Is.Not.SameAs(moved));
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }
        [TestCase(1280,720)] [TestCase(1920,1080)] [TestCase(2560,1080)]
        public void NineCardsStayWithinWindowAndPortraitsRemainSquare(int width, int height)
        {
            var matrix = HeroSelectionLayout.Matrix(width, height);
            for (int i = 0; i < LubanConfigService.Current.Tables.TbHero.DataList.Count; i++)
            {
                var r = HeroSelectionLayout.Card(i); var p = HeroSelectionLayout.Portrait(i);
                Assert.That(r.yMax, Is.LessThan(HeroSelectionLayout.Confirm.yMin));
                Assert.That(r.yMax, Is.LessThanOrEqualTo(HeroSelectionLayout.CardViewport.yMax));
                var lo = matrix.MultiplyPoint3x4(p.min); var hi = matrix.MultiplyPoint3x4(p.max);
                Assert.That(hi.x - lo.x, Is.EqualTo(hi.y - lo.y).Within(.001));
                Assert.That(lo.x, Is.GreaterThanOrEqualTo(0)); Assert.That(hi.x, Is.LessThanOrEqualTo(width));
                Assert.That(hi.y, Is.LessThanOrEqualTo(height));
                for (int j = 0; j < i; j++) Assert.That(r.Overlaps(HeroSelectionLayout.Card(j)), Is.False);
            }
        }
        [TestCase(1280,720)] [TestCase(1920,1080)] [TestCase(2560,1080)]
        public void HudEdgesAndWorldProjectionShareScreenSpaceWithoutStretch(int width, int height)
        {
            var matrix = HeroSelectionLayout.Matrix(width, height);
            float scale = CombatUiLayout.Scale(width, height);
            var vitals = CombatUiLayout.Vitals(width, height);
            var left = matrix.MultiplyPoint3x4(vitals.min);
            Assert.That(left.x, Is.EqualTo(20 * scale).Within(.01));
            var skill = CombatUiLayout.Skill(width, height, 1);
            Assert.That(matrix.MultiplyPoint3x4(skill.max).x, Is.EqualTo(width - 16 * scale).Within(.01));
            Assert.That(matrix.MultiplyPoint3x4(vitals.max).y, Is.EqualTo(height - 47 * scale).Within(.01));
            foreach (var uv in new[] {Vector2.zero, Vector2.one, new Vector2(.17f,.63f)})
            {
                var point = matrix.MultiplyPoint3x4(CombatUiLayout.Viewport(uv,width,height));
                Assert.That(point.x, Is.EqualTo(uv.x*width).Within(.01));
                Assert.That(point.y, Is.EqualTo((1-uv.y)*height).Within(.01));
            }
            Assert.That(vitals.Overlaps(CombatUiLayout.Skill(width,height,0)), Is.False);
        }
        [Test] public void EveryEquipmentOptionFitsAboveFixedFooter()
        {
            foreach(var pair in new[] {(LubanConfigService.Current.Tables.TbSubWeapon.DataList.Count,4),(LubanConfigService.Current.Tables.TbSpecialWeapon.DataList.Count,3)})
                for(int i=0;i<pair.Item1;i++)
                {
                    var r=HeroSelectionLayout.EquipmentCard(i,pair.Item2);
                    Assert.That(r.xMin,Is.GreaterThanOrEqualTo(HeroSelectionLayout.Equipment.xMin));
                    Assert.That(r.xMax,Is.LessThanOrEqualTo(HeroSelectionLayout.Equipment.xMax));
                    Assert.That(r.yMax,Is.LessThanOrEqualTo(HeroSelectionLayout.Equipment.yMax));
                    Assert.That(r.Overlaps(HeroSelectionLayout.Confirm),Is.False);
                    for(int j=0;j<i;j++)Assert.That(r.Overlaps(HeroSelectionLayout.EquipmentCard(j,pair.Item2)),Is.False);
                }
            Assert.That(HeroSelectionLayout.ContentHeight(12),Is.GreaterThan(HeroSelectionLayout.CardViewport.height));
            Assert.That(HeroSelectionLayout.ContentHeight(12)+HeroSelectionLayout.CardViewport.y,Is.GreaterThanOrEqualTo(HeroSelectionLayout.Card(11).yMax));
        }
    }
}
#endif
