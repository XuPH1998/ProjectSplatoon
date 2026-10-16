#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Prototype;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class HeroMigrationPlayTests
    {
        static IEnumerator Wait(Func<bool> condition, string message, double seconds = 15)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(condition(), Is.True, message);
        }
        [UnityTest, Timeout(600000)] public IEnumerator RealAppLoadsSelectsRespawnsAndReleasesHeroModels()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity", OpenSceneMode.Single);
            yield return new EnterPlayMode();
            // Create coroutine closures after the domain reload performed by EnterPlayMode.
            yield return RunRoomScenario();
            yield return new ExitPlayMode();
        }
        static IEnumerator RunRoomScenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Real Addressables bootstrap");
            var app = PrototypeApp.Current;
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            var rows = LubanConfigService.Current.Tables.TbHero.DataList;
            int expectedAssets = rows.SelectMany(row => new[] { row.CharacterPrefabAddress,
                GameplayConfig.GetWeapon(row.Id).WeaponPrefabAddress }).Distinct(StringComparer.Ordinal).Count();
            CollectionAssert.AreEqual(Enumerable.Range(1, 9), rows.Select(row => row.Id).OrderBy(id => id));
            Assert.That(app.Heroes.AssetCount, Is.EqualTo(expectedAssets), "Each distinct configured character and weapon is loaded once");
            Assert.That(app.Heroes.PortraitCount, Is.EqualTo(rows.Select(row => row.PortraitAddress).Distinct().Count()));
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current != null, "Host local player spawn");
            var player = PrototypePlayer.Local;
            var match = PrototypeMatch.Current;
            Assert.That(player, Is.Not.Null, "Local player after spawn wait");
            Assert.That(match, Is.Not.Null, "Match after spawn wait");
            Assert.That(player.Visual, Is.Not.Null, "Hero visual after spawn");
            Assert.That(player.NetworkObject, Is.Not.Null, "Player network root after spawn");
            var firstVisual = player.Visual;
            var playerId = player.NetworkObjectId;
            Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(1));
            app.OpenHeroSelection(HeroSelectionOrigin.Warmup); Assert.That(app.Overlay, Is.EqualTo(GameplayOverlay.Heroes));
            for (int hero = 2; hero <= 9; hero++)
            {
                var before = player.Snapshot.Value; before.Ink = 20; before.InkRecoverAt = player.NetworkManager.ServerTime.Time + 100; player.Snapshot.Value = before;
                player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Select hero " + hero);
                Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(100)); Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(before.Revision));
                Assert.That(player.Snapshot.Value.ShotSequence, Is.EqualTo(before.ShotSequence));
                Assert.That(player.Visual, Is.Not.SameAs(firstVisual)); Assert.That(player.NetworkObjectId, Is.EqualTo(playerId));
                Assert.That(player.CharacterView.GetComponentsInChildren<HeroWeaponBindings>(true).Length, Is.EqualTo(1));
                Assert.That(player.BoundVisualPrefab, Is.SameAs(app.Heroes.Get(hero).CharacterPrefab));
                Assert.That(player.CharacterView.BoundWeaponPrefab, Is.SameAs(app.Heroes.Get(hero).WeaponPrefab));
                if (hero == 2)
                {
                    Assert.That(player.CharacterView.LeftWeapon.parent, Is.SameAs(player.CharacterView.LeftWeaponSocket));
                    Assert.That(player.CharacterView.LeftNozzle.IsChildOf(player.CharacterView.LeftWeapon), Is.True);
                    Assert.That(player.CharacterView.UseSupportGrip, Is.False);
                }
                yield return VerifyDeathAndRespawn(player, hero);
            }
            Directory.CreateDirectory("Reports/HeroMigration");
            var phase = match.State.Value; phase.Phase = MatchPhase.Playing; phase.EndsAt = player.NetworkManager.ServerTime.Time + 100; match.State.Value = phase;
            var state = player.Snapshot.Value; state.Ink = 20; state.Health = 70; state.LastDamageAt = player.NetworkManager.ServerTime.Time; state.InkRecoverAt = player.NetworkManager.ServerTime.Time + 100;
            uint life = state.Revision; player.Snapshot.Value = state;
            app.OpenHeroSelection(HeroSelectionOrigin.Debug); player.RequestHeroChange(2, HeroSelectionOrigin.Debug);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 2, "DEBUG selects hero");
            Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(20)); Assert.That(player.Snapshot.Value.Health, Is.EqualTo(70));
            Assert.That(player.Snapshot.Value.Revision, Is.EqualTo(life));
            player.RequestHeroChange(999, HeroSelectionOrigin.Debug);
            yield return Wait(() => !player.HeroChangePending, "Invalid hero request replied");
            Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2)); Assert.That(player.HeroChangeMessage, Does.Contain("不存在"));
            // Test-only table variation proves respawn reads the selected row, not the default hero.
            HeroMigrationTests.Load(rows => { rows[1]["maxHealth"] = 80; rows[1]["maxInk"] = 60; });
            player.Respawn(); Assert.That(player.Snapshot.Value.HeroId, Is.EqualTo(2));
            Assert.That(player.Snapshot.Value.Health, Is.EqualTo(80)); Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(60));
            HeroMigrationTests.Load();
            yield return VerifyShotgunDamage(player);
            yield return app.Leave().ToCoroutine(); Assert.That(app.Heroes.AssetCount, Is.Zero); Assert.That(PrototypePlayer.Local, Is.Null);
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine(); Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null, "Reconnected host local player spawn");
            Assert.That(PrototypePlayer.Local.Snapshot.Value.HeroId, Is.EqualTo(1));
            yield return app.Leave().ToCoroutine();
            File.WriteAllText("Reports/HeroMigration/play-validation.txt", "PASS: real Addressables + NGO host; 9 hero models; dual hand assembly; selected heroes downed-execution and timed respawn; frozen historical shotgun 8 independent Physics hits reduce 100HP to 20HP without changing the live Explosher; warmup/DEBUG/invalid selection; selected-hero respawn; leave/reenter default; resource release.\nRemote client and physical LAN are separate acceptance items.\n");
            yield return HeroUiSmoke.RunInEditorAsync(port).ToCoroutine();
        }
        static IEnumerator VerifyDeathAndRespawn(PrototypePlayer player, int hero)
        {
            var state=player.Snapshot.Value; uint life=state.Revision;
            state.ProtectedUntil=0; state.Swimming=true; state.Movement=MovementMode.GroundInk;
            player.Snapshot.Value=state;
            player.ReceiveDamage((byte)(state.Team==1?2:1),200,Quaternion.Euler(0,state.BodyYaw,0)*Vector3.forward);
            Assert.That(player.Snapshot.Value.Health,Is.Zero); Assert.That(player.Snapshot.Value.Swimming,Is.False);
            Assert.That(player.Snapshot.Value.IsBubble, Is.True, "Lethal damage first permits rescue");
            player.EndBubble(BubbleOutcome.Executed, player.NetworkManager.ServerTime.Time);
            Assert.That(player.Snapshot.Value.IsDead, Is.True, "Execution starts the ordinary respawn timer");
            yield return null;
            Assert.That(player.CharacterView.Animator.enabled,Is.True);
            Assert.That(player.CharacterView.GetComponentsInChildren<SkinnedMeshRenderer>().Any(r=>r.enabled),Is.True);
            for(int layer=1;layer<player.CharacterView.Animator.layerCount;layer++)Assert.That(player.CharacterView.Animator.GetLayerWeight(layer),Is.Zero);
            yield return Wait(()=>player.Snapshot.Value.Revision>life&&player.Snapshot.Value.Health>0,"Timed respawn "+hero,5);
            Assert.That(player.Snapshot.Value.HeroId,Is.EqualTo(hero)); Assert.That(player.Snapshot.Value.NextMuzzle,Is.Zero);
            Assert.That(player.Snapshot.Value.RightShotAction,Is.Zero); Assert.That(player.Snapshot.Value.LeftShotAction,Is.Zero);
            yield return null;
            Assert.That(player.CharacterView.Weapon.parent,Is.SameAs(player.CharacterView.WeaponSocket));
        }
        static IEnumerator VerifyShotgunDamage(PrototypePlayer player)
        {
            var state=player.Snapshot.Value;state.Health=100;state.ProtectedUntil=0;player.Snapshot.Value=state;
            // Keep the retired eight-pellet damage contract explicit; current hero 3 remains Explosher.
            var current = GameplayConfig.GetWeapon(3); WeaponRuntimeConfig historical;
            using (new HistoricalCombatContractFixture(3)) historical = GameplayConfig.GetWeapon(3);
            Assert.That(GameplayConfig.GetWeapon(3), Is.SameAs(current), "Historical measurement restores the live configuration before simulation");
            Assert.That(historical.PelletCount, Is.EqualTo(8)); Assert.That(historical.Damage, Is.EqualTo(10));
            Physics.SyncTransforms();var service=new InkProjectileService();double born=player.NetworkManager.ServerTime.Time;
            var origin=state.Position+Vector3.up*.9f+Vector3.forward*.7f;
            for(byte i=0;i<8;i++)service.SpawnForMeasurement(new InkShot { Id=(uint)i+1,ActionId=123,HeroId=3,PelletIndex=i,
                Shooter=ulong.MaxValue,Team=(byte)(state.Team==1?2:1),Born=born,Origin=origin,Velocity=Vector3.back*22,Configuration=historical });
            service.Simulate(born+.1);
            Assert.That(service.Impacts.Count,Is.EqualTo(8));Assert.That(service.Impacts.Select(x=>x.Id).Distinct().Count(),Is.EqualTo(8));
            Assert.That(service.Impacts.Sum(x=>x.Damage),Is.EqualTo(80).Within(.001));Assert.That(player.Snapshot.Value.Health,Is.EqualTo(20).Within(.001));
            service.Simulate(born+1);Assert.That(player.Snapshot.Value.Health,Is.EqualTo(20).Within(.001),"resolved pellets cannot damage twice");
            yield return null;
        }
    }
}
#endif
