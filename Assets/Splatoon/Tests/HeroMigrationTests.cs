#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using SimpleJSON;
using UnityEditor;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class HeroMigrationTests
    {
        const string CharacterPath = "Assets/GameResource/Characters/RifleGirl/Prefabs/RifleGirlVisual.prefab";
        const string WeaponPath = "Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab";
        readonly List<UnityEngine.Object> _objects = new();
        public static void Load(Action<JSONNode> edit = null)
        {
            var data = Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban", "*.json").ToDictionary(Path.GetFileNameWithoutExtension, f => JSONNode.Parse(File.ReadAllText(f)));
            data["tbhero"] = CombinedHeroes();
            edit?.Invoke(data["tbhero"]);
            InstallWeapons(data["tbhero"]);
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, new cfg.Tables(n => data[n]));
        }
        public static JSONNode CombinedHeroes()
        {
            var rows = JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/tbhero.json"));
            foreach (var row in rows.Children)
            {
                var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(row["weaponConfigPath"].Value);
                Assert.That(asset, Is.Not.Null, row["weaponConfigPath"].Value);
                var weapon = JSONNode.Parse(JsonUtility.ToJson(asset));
                WeaponTimeFixture.ToReferenceFrames(weapon);
                foreach (string key in weapon.Keys) if (key != "name") row[key] = weapon[key];
            }
            return rows;
        }
        public static void InstallWeapons(JSONNode rows)
        {
            WeaponConfigService.Current.Clear();
            foreach (var row in rows.Children)
            {
                var copy = ScriptableObject.CreateInstance<WeaponConfigAsset>();
                try
                {
                    var current = JSONNode.Parse(row.ToString());
                    WeaponTimeFixture.ToSeconds(current);
                    JsonUtility.FromJsonOverwrite(current.ToString(), copy);
                    // SimpleJSON's textual formatter rounds doubles. Preserve the
                    // full numeric value when adapting the historical frame fixtures.
                    foreach (var field in typeof(WeaponConfigAsset).GetFields())
                        if (field.FieldType == typeof(double)) field.SetValue(copy, current[field.Name].AsDouble);
                    WeaponConfigService.Current.SetForEditor(row["id"].AsInt, copy.Snapshot());
                }
                finally { UnityEngine.Object.DestroyImmediate(copy); }
            }
        }
        // Retired shooter/burst mechanics remain covered using the pinned historical fixture,
        // independently of the new custom-hero balance in the live table.
        public static void LoadHistoricalWeapons() => Load(rows =>
        {
            var baseline=JSONNode.Parse(File.ReadAllText("Tools/ValidationData/HeroMigration/Migration-Baseline.json"));
            var mapping=JSONNode.Parse(File.ReadAllText("Tools/ValidationData/HeroMigration/Field-Mapping.json"));
            for(int i=0;i<baseline["Weapon"].Count;i++)
            {
                foreach(var item in mapping.Children) if(item["sourceTable"].Value=="Weapon" && item["heroField"].Value != "trailRadius") rows[i][item["heroField"].Value]=baseline["Weapon"][i][item["sourceField"].Value];
                rows[i]["motionMode"] = (int)ProjectileMotionMode.Ballistic;
                rows[i]["referenceRules"] = false; rows[i]["referenceSpreadEnabled"] = false;
                rows[i]["pelletCount"]=1;rows[i]["muzzleMode"]=0;rows[i]["semiBufferFrames"]=0;
            }
            // Pinned historical mechanics use their original fixed radius, only in this loader.
            for (int i = 0; i < baseline["Weapon"].Count; i++)
                rows[i]["trailRadiusMin"] = rows[i]["trailRadiusMax"] = baseline["Weapon"][i]["trailRadius"];
        });
        static readonly string[] CharacterNames={"","RifleGirl","DualPistolGirl","ShotgunGirl","PistolGirl","RocketLauncherGirl","MachineGunGirl"};
        static readonly string[] WeaponNames={"","RifleGirlRifle","DualPistols","Shotgun","Pistol","RocketLauncher","MachineGun"};
        static GameObject Character(int id)=>AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{CharacterNames[id]}/Prefabs/{CharacterNames[id]}Visual.prefab");
        static GameObject Weapon(int id)=>AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Weapons/{CharacterNames[id]}/Prefabs/{WeaponNames[id]}.prefab");
        [SetUp] public void Setup() => Load();
        [TearDown] public void Cleanup()
        {
            foreach (var obj in _objects) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            _objects.Clear(); LubanConfigService.Current.Reset();
        }
        GameObject Root(string name) { var go = new GameObject(name); go.layer = 8; _objects.Add(go); return go; }
        HeroContent Content(int id, GameObject character = null, GameObject weapon = null) => new(GameplayConfig.GetHero(id), character ?? Character(id), weapon ?? Weapon(id));
        static PlayerSnapshot State(int hero) => new() { HeroId = hero, Health = 100, Ink = 100, Team = 1, Grounded = true, Revision = 4 };

        [Test] public void MachineGunAdditionPreservesTheExistingFiveHeroes()
        {
            var before = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/MachineGun/Heroes-Before.json"));
            var actual = CombinedHeroes();
            Assert.That(actual.Count, Is.EqualTo(before.Count + 1));
            for (int i = 0; i < before.Count; i++)
                foreach (var field in before[i].Keys)
                    // Selection now uses character names; numeric migration invariants remain pinned.
                    if (field == "displayName") continue;
                    else if (before[i][field].IsNumber) Assert.That(actual[i][field].AsDouble, Is.EqualTo(before[i][field].AsDouble).Within(.00001), $"{i + 1}/{field}");
                    else Assert.That(actual[i][field].Value, Is.EqualTo(before[i][field].Value), $"{i + 1}/{field}");
        }
        [Test] public void SelectedHeroDrivesMovementRecoveryAndDisplay()
        {
            Load(rows => { rows[1]["maxHealth"] = 80; rows[1]["maxInk"] = 60; rows[1]["moveSpeed"] = 2; rows[1]["recoverInk"] = 4; });
            var floor = Root("Hero movement floor"); floor.layer = 0; floor.transform.position = new Vector3(500, -.5f, 500);
            floor.AddComponent<BoxCollider>().size = new Vector3(100, 1, 100);
            float Distance(int id)
            {
                var go = Root("Hero " + id); var cc = go.AddComponent<CharacterController>(); cc.height = 1.8f; cc.center = Vector3.up * .9f; cc.radius = .35f;
                var motor = new PlayerMotorSimulation(cc); var s = State(id); s.Position = new Vector3(500, .05f, 500);
                motor.Restore(s); Physics.SyncTransforms();
                for (int tick = 0; tick < 60; tick++) motor.Step(ref s, new PlayerInputFrame { Move = Vector2.up }, 1f / 60, tick / 60.0, false);
                go.SetActive(false); return s.Position.z - 500;
            }
            Assert.That(Distance(1), Is.GreaterThan(4.5f)); Assert.That(Distance(2), Is.InRange(1.8f, 2.1f));
            var state = State(2); state.Ink = 0; state.Health = 79;
            ResourceSimulation.Step(ref state, GameplayConfig.GetHero(state.HeroId), false, false, 1, 10);
            Assert.That(state.Ink, Is.EqualTo(4)); Assert.That(state.Health, Is.EqualTo(80));
            var details = WeaponDisplay.Details(GameplayConfig.GetHero(2));
            Assert.That(details.Single(x => x.label == "生命／墨量上限").value, Is.EqualTo("80 / 60"));
            Assert.That(details.Single(x => x.label == "满墨发数").value, Is.EqualTo("85 发"));
        }
        [Test] public void HeroSwitchClampsResourcesWithoutResettingLifeOrPosition()
        {
            Load(rows => { rows[1]["maxHealth"] = 80; rows[1]["maxInk"] = 60; });
            var state = State(5); state.Position = new Vector3(3, 4, 5); state.ChargeElapsedSeconds = 30 / 60.0; state.WeaponPhase = WeaponPhase.Charging;
            HeroSelectionRules.Apply(ref state, 2, false, new PlayerInputFrame { Fire = true });
            Assert.That(state.Health, Is.EqualTo(80)); Assert.That(state.Ink, Is.EqualTo(60)); Assert.That(state.HeroRevision, Is.EqualTo(1));
            Assert.That(state.Revision, Is.EqualTo(4)); Assert.That(state.Position, Is.EqualTo(new Vector3(3,4,5))); Assert.That(state.Team, Is.EqualTo(1));
            Assert.That((state.ChargeElapsedSeconds * 60), Is.Zero); Assert.That(state.AttackNeedsRelease, Is.True);
            state.Ink = 10; HeroSelectionRules.Apply(ref state, 1, true, default); Assert.That(state.Ink, Is.EqualTo(100));
        }
        [Test] public void BinderReusesSharedModelsAndReplacesAlternativeModelsWithoutLeftovers()
        {
            using var binder = new HeroViewBinder(Root("Hero view root").transform);
            binder.Apply(Content(1)); var first = binder.Visual;
            Assert.That(binder.Apply(Content(1)),Is.False);Assert.That(binder.Visual,Is.SameAs(first));
            for (int id = 2; id <= 6; id++) { Assert.That(binder.Apply(Content(id)), Is.True); Assert.That(binder.Visual, Is.Not.SameAs(first)); }
            Assert.That(binder.View.GetComponentsInChildren<HeroWeaponBindings>(true).Length, Is.EqualTo(1));
            var alternateCharacter = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath)); _objects.Add(alternateCharacter); alternateCharacter.SetActive(false);
            var alternateWeapon = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPath)); _objects.Add(alternateWeapon); alternateWeapon.SetActive(false);
            alternateWeapon.GetComponent<HeroWeaponBindings>().Nozzle.localPosition += Vector3.forward * .1f;
            for (int i = 0; i < 3; i++)
            {
                Assert.That(binder.Apply(Content(4, alternateCharacter, alternateWeapon)), Is.True);
                Assert.That(binder.View.Nozzle, Is.SameAs(binder.View.Weapon.GetComponent<HeroWeaponBindings>().Nozzle));
                Assert.That(binder.View.LeftGrip, Is.SameAs(binder.View.Weapon.GetComponent<HeroWeaponBindings>().LeftGrip));
                Assert.That(binder.View.Weapon.parent, Is.SameAs(binder.View.WeaponSocket));
                Assert.That(binder.View.GetComponentsInChildren<HeroWeaponBindings>(true).Length, Is.EqualTo(1));
                Assert.That(binder.Apply(Content(1)), Is.True);
            }
            Assert.That(binder.Visual.parent.GetComponentsInChildren<InkCharacterView>(true).Length, Is.EqualTo(1));
        }
        sealed class Assets : IHeroAssetSource
        {
            public int Loads, Releases; public string Fails;
            public readonly Dictionary<string,GameObject> Values = new();
            public int PortraitLoads;
            public UniTask<Texture2D> LoadPortraitAsync(string address, CancellationToken token)
            { token.ThrowIfCancellationRequested(); PortraitLoads++; if (address == Fails) throw new InvalidOperationException("test missing portrait"); return UniTask.FromResult(Texture2D.whiteTexture); }
            public UniTask<GameObject> LoadAsync(string address, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Loads++; if (address == Fails) throw new InvalidOperationException("test missing model"); return UniTask.FromResult(Values[address]); }
            public void ReleaseAll() => Releases++;
        }
        static Assets Catalog()
        {
            var result=new Assets();
            for(int id=1;id<=6;id++){result.Values["Character/"+CharacterNames[id]]=Character(id);result.Values["Weapon/"+WeaponNames[id]]=Weapon(id);}
            return result;
        }
        [Test] public void CatalogDeduplicatesAddressesAndRecoversAfterFailureAndCancellation()
        {
            var source = Catalog();
            using var service = new HeroContentService(source);
            var rows = LubanConfigService.Current.Tables.TbHero.DataList;
            source.Fails = "Weapon/RifleGirlRifle";
            Assert.Throws<InvalidOperationException>(() => service.InitializeAsync(rows, default).GetAwaiter().GetResult());
            Assert.That(service.AssetCount, Is.Zero); Assert.That(service.All, Is.Empty);
            source.Fails = null; source.Loads = 0;
            service.InitializeAsync(rows, default).GetAwaiter().GetResult();
            Assert.That(source.Loads, Is.EqualTo(12)); Assert.That(service.AssetCount, Is.EqualTo(12)); Assert.That(service.All.Count(), Is.EqualTo(6));
            Assert.That(service.PortraitCount, Is.EqualTo(6));
            Assert.That(service.All.All(h => h.Portrait != null), Is.True);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => service.InitializeAsync(rows, cancel.Token).GetAwaiter().GetResult());
            Assert.That(service.All, Is.Empty); service.InitializeAsync(rows, default).GetAwaiter().GetResult(); Assert.That(service.Get(5).Config.Id, Is.EqualTo(5));
        }
        [Test] public void MissingWeaponBindingsAreRejectedBeforeAssembly()
        {
            var weapon = Root("Invalid hero weapon");
            Assert.Throws<InvalidOperationException>(() => Content(1, weapon: weapon));
        }
        [Test] public void MissingPortraitReleasesPartialCatalogAndCanRetry()
        {
            var source = Catalog();
            using var service = new HeroContentService(source);
            var rows = LubanConfigService.Current.Tables.TbHero.DataList;
            source.Fails = rows[2].PortraitAddress;
            Assert.Throws<InvalidOperationException>(() => service.InitializeAsync(rows, default).GetAwaiter().GetResult());
            Assert.That(service.PortraitCount, Is.Zero); Assert.That(service.AssetCount, Is.Zero); Assert.That(service.All, Is.Empty);
            source.Fails = null;
            service.InitializeAsync(rows, default).GetAwaiter().GetResult();
            Assert.That(service.PortraitCount, Is.EqualTo(6));
            var portrait = service.Get(1).Portrait;
            var candidate = service.PrepareWeaponAsync(1, GameplayConfig.GetWeapon(1), default).GetAwaiter().GetResult();
            Assert.That(candidate.Portrait, Is.SameAs(portrait));
            service.Clear(); Assert.That(service.PortraitCount, Is.Zero);
        }
        [Test] public void AlternativeAddressesResolveAndAssembleTheirOwnModels()
        {
            Load(rows => { rows[1]["characterPrefabAddress"]="Test/AlternateCharacter"; rows[1]["weaponPrefabAddress"]="Test/AlternateWeapon"; rows[1]["muzzleMode"]=0; });
            var character=AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            var weapon=AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPath);
            var otherCharacter=UnityEngine.Object.Instantiate(character);_objects.Add(otherCharacter);otherCharacter.SetActive(false);
            var otherWeapon=UnityEngine.Object.Instantiate(weapon);_objects.Add(otherWeapon);otherWeapon.SetActive(false);
            var assets=Catalog();
            assets.Values["Test/AlternateCharacter"]=otherCharacter;assets.Values["Test/AlternateWeapon"]=otherWeapon;
            using var service=new HeroContentService(assets);
            service.InitializeAsync(LubanConfigService.Current.Tables.TbHero.DataList,default).GetAwaiter().GetResult();
            Assert.That(assets.Loads,Is.EqualTo(12));
            using var binder=new HeroViewBinder(Root("Alternate address hero").transform);
            binder.Apply(service.Get(1));var first=binder.Visual;
            Assert.That(binder.Apply(service.Get(2)),Is.True);Assert.That(binder.Visual,Is.Not.SameAs(first));
            Assert.That(binder.View.BoundWeaponPrefab,Is.SameAs(otherWeapon));
            Assert.That(binder.Content.CharacterPrefab,Is.SameAs(otherCharacter));
            Assert.That(binder.View.Weapon.parent,Is.SameAs(binder.View.WeaponSocket));
        }
        [Test] public void SignatureIncludesNonDefaultHeroPresentationAndIsIndependentOfEnumerationOrder()
        {
            Splatoon.Painting.InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(Splatoon.Painting.InkShapeAtlas.AssetPath));
            var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab").GetComponent<PrototypePlayer>();
            var character = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath)); _objects.Add(character); character.SetActive(false);
            var profile = UnityEngine.Object.Instantiate(character.GetComponent<InkCharacterView>().Profile); _objects.Add(profile); character.GetComponent<InkCharacterView>().Profile = profile;
            var heroes = new[] { Content(1), Content(4, character) };
            var baseline = GameplayContentSignature.Compute(new byte[] { 1 }, "map", player, heroes);
            CollectionAssert.AreEqual(baseline, GameplayContentSignature.Compute(new byte[] { 1 }, "map", player, heroes.Reverse()));
            profile.MuzzlePosition += Vector3.forward;
            CollectionAssert.AreNotEqual(baseline, GameplayContentSignature.Compute(new byte[] { 1 }, "map", player, heroes));
        }
    }
}
#endif
