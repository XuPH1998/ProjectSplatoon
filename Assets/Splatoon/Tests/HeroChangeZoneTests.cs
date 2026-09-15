#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class HeroChangeZoneTests
    {
        GameObject _go;
        HeroChangeZone _zone;
        [SetUp] public void Setup()
        {
            _go = new GameObject("Test hero change zone");
            _zone = _go.AddComponent<HeroChangeZone>();
            _zone.Volume.isTrigger = true; _zone.Volume.size = new Vector3(16, 4, 6);
            _go.transform.position = new Vector3(0, 2, -28);
        }
        [TearDown] public void Cleanup()
        {
            Object.DestroyImmediate(_go); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static PlayerSnapshot Alive() => new() { HeroId = 1, Health = 70, Ink = 20, Team = 1, Revision = 3, Position = new Vector3(0, .05f, -28) };

        [TestCase(1)] [TestCase(2)]
        public void OnlyOwnTeamInsideInclusiveBoxCanSwitch(byte team)
        {
            _zone.Team = team;
            Assert.That(_zone.Contains(team, _go.transform.position), Is.True);
            Assert.That(_zone.Contains((byte)(3 - team), _go.transform.position), Is.False);
            Assert.That(_zone.Contains(0, _go.transform.position), Is.False);
            Assert.That(_zone.Contains(team, _go.transform.TransformPoint(8, 2, 3)), Is.True);
            Assert.That(_zone.Contains(team, _go.transform.TransformPoint(8.01f, 0, 0)), Is.False);
            Assert.That(_zone.Contains(team, _go.transform.TransformPoint(0, 2.01f, 0)), Is.False);
            Assert.That(_zone.Contains(team, _go.transform.TransformPoint(0, 0, 3.01f)), Is.False);
        }
        [Test] public void RotatedScaledOffsetBoxUsesLocalVolumeNotWorldBounds()
        {
            _go.transform.rotation = Quaternion.Euler(12, 45, 5); _go.transform.localScale = new Vector3(2, .5f, -1.5f);
            _zone.Volume.center = new Vector3(1, 2, 3);
            Assert.That(_zone.Contains(1, _go.transform.TransformPoint(_zone.Volume.center + new Vector3(7.9f, 1.9f, 2.9f))), Is.True);
            Assert.That(_zone.Contains(1, _go.transform.TransformPoint(_zone.Volume.center + new Vector3(8.1f, 0, 0))), Is.False);
            Assert.That(_zone.Contains(1, new Vector3(float.NaN, 0, 0)), Is.False);
            _go.transform.localScale = Vector3.zero;
            Assert.That(_zone.Contains(1, _go.transform.position), Is.False);
        }
        [Test] public void DisabledOrNonTriggerVolumeGrantsNoAccess()
        {
            var position = _go.transform.position;
            _zone.enabled = false; Assert.That(_zone.Contains(1, position), Is.False); _zone.enabled = true;
            _zone.Volume.enabled = false; Assert.That(_zone.Contains(1, position), Is.False); _zone.Volume.enabled = true;
            _zone.Volume.isTrigger = false; Assert.That(_zone.Contains(1, position), Is.False); _zone.Volume.isTrigger = true;
            _go.SetActive(false); Assert.That(_zone.Contains(1, position), Is.False);
        }
        [TestCase(MatchPhase.Playing, true, false, true)]
        [TestCase(MatchPhase.Playing, false, true, false)]
        [TestCase(MatchPhase.Practice, true, true, false)]
        [TestCase(MatchPhase.Finished, true, true, false)]
        public void SpawnEntryRequiresPlayingAndAreaEvenInDevelopment(MatchPhase phase, bool inside, bool development, bool allowed)
        {
            Assert.That(HeroSelectionRules.Availability(Alive(), HeroSelectionOrigin.SpawnArea, phase, development, inside) == null, Is.EqualTo(allowed));
        }
        [Test] public void AuthorityStillRejectsDeadStaleAndInvalidRequests()
        {
            HeroMigrationTests.Load(); var state = Alive();
            string Validate(int hero, uint round, uint life) => HeroSelectionRules.Validate(state, hero, HeroSelectionOrigin.SpawnArea, round, 5, life, MatchPhase.Playing, false, true);
            Assert.That(Validate(2, 5, 3), Is.Null);
            Assert.That(Validate(2, 4, 3), Is.Not.Null); Assert.That(Validate(2, 5, 2), Is.Not.Null);
            Assert.That(Validate(999, 5, 3), Does.Contain("不存在"));
            state.Health = 0; Assert.That(Validate(2, 5, 3), Does.Contain("重生"));
        }
        [Test] public void RepeatedSwitchesPreserveResourcesProtectionPositionAndLife()
        {
            HeroMigrationTests.Load(); var state = Alive(); state.ProtectedUntil = 123; state.NextShotAt = 20;
            var position = state.Position;
            for (int i = 0; i < 6; i++)
            {
                HeroSelectionRules.Apply(ref state, i % 2 + 2, false, new PlayerInputFrame { Fire = true });
                Assert.That(state.Health, Is.EqualTo(70)); Assert.That(state.Ink, Is.EqualTo(20));
                Assert.That(state.Position, Is.EqualTo(position)); Assert.That(state.ProtectedUntil, Is.EqualTo(123));
                Assert.That(state.Revision, Is.EqualTo(3)); Assert.That(state.NextShotAt, Is.GreaterThanOrEqualTo(20));
                Assert.That(state.AttackNeedsRelease, Is.True);
            }
            HeroMigrationTests.Load(rows => { rows[1]["maxHealth"] = 40; rows[1]["maxInk"] = 10; });
            HeroSelectionRules.Apply(ref state, 2, false, default);
            Assert.That(state.Health, Is.EqualTo(40)); Assert.That(state.Ink, Is.EqualTo(10));
        }
        [Test] public void TriggerDoesNotBlockAimProjectileOrCameraQueries()
        {
            Physics.SyncTransforms(); var solver = new TpsAimSolver();
            Assert.That(TpsAimSolver.Valid(_zone.Volume, ulong.MaxValue), Is.False);
            Assert.That(solver.ClosestCast(new Vector3(0, 2, -40), Vector3.forward, 20, .2f, ulong.MaxValue, out _), Is.False);
            Assert.That(solver.Overlap(_go.transform.position, .2f, ulong.MaxValue, Vector3.up, out _), Is.False);
            Assert.That(Physics.SphereCast(new Vector3(0, 2, -40), .2f, Vector3.forward, out _, 20, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.False);
        }
        [Test] public void SavedMapCoversAllSpawnsAndHashesZoneConfiguration()
        {
            Object.DestroyImmediate(_go);
            var scene = EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity", OpenSceneMode.Single);
            var arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single();
            var zones = arena.GetComponentsInChildren<HeroChangeZone>();
            Assert.That(zones.Length, Is.EqualTo(2));
            for (int i = 0; i < 8; i++)
            {
                byte team = (byte)(i / 4 + 1); var position = arena.SpawnPoints[i].position;
                Assert.That(arena.IsInHeroChangeZone(team, position), Is.True);
                Assert.That(arena.IsInHeroChangeZone((byte)(3 - team), position), Is.False);
            }
            string hash = arena.ComputeTopology(); Assert.That(arena.BakedTopology, Is.EqualTo(hash));
            var zone = zones[0]; byte originalTeam = zone.Team;
            zone.Team = (byte)(3 - originalTeam); Assert.That(arena.ComputeTopology(), Is.Not.EqualTo(hash)); zone.Team = originalTeam;
            zone.enabled = false; Assert.That(arena.ComputeTopology(), Is.Not.EqualTo(hash)); zone.enabled = true;
            zone.Volume.size += Vector3.one; Assert.That(arena.ComputeTopology(), Is.Not.EqualTo(hash)); zone.Volume.size -= Vector3.one;
            zone.transform.parent.gameObject.SetActive(false); Assert.That(arena.IsInHeroChangeZone(1, arena.SpawnPoints[0].position), Is.False);
            Assert.That(arena.ComputeTopology(), Is.Not.EqualTo(hash)); zone.transform.parent.gameObject.SetActive(true);
            Assert.That(arena.ComputeTopology(), Is.EqualTo(hash));
        }
    }
}
#endif
