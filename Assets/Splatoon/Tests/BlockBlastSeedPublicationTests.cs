#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>
    /// Real authority world -> original pressure observer -> atomic control file contract.
    /// Spawned NPCs are fixture actors, not independent network clients or input acceptance evidence.
    /// </summary>
    public sealed class BlockBlastSeedPublicationTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type ProbeType = typeof(BlockDynamicAcceptanceProbe);
        PrototypeMatch _match;
        PrototypePlayer[] _players;
        BlockDefaultContractFixture _fixture;
        GameObject _probeObject;
        BlockDynamicAcceptanceProbe _probe;
        object _report;
        List<BlockDynamicAcceptanceProbe.BlastPressureActor> _actors;
        readonly Dictionary<int, List<ulong>> _bodyIds = new();
        readonly HashSet<int> _surfaceActors = new();
        Vector3[] _seeds;
        string _directory, _control, _session;
        BlockTerrain Terrain => _match.Blocks;
        BlockDynamicAcceptanceProbe.BlastPressureReport Pressure =>
            (BlockDynamicAcceptanceProbe.BlastPressureReport)Get(_report, "blastPressure");

        [Serializable] sealed class PublishedControl
        {
            public string session, phase;
            public uint round;
            public int sequence;
            public List<BlockDynamicAcceptanceProbe.BlastPressureActor> actors;
        }

        static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        object Invoke(string method, params object[] arguments) => ProbeType.GetMethod(method, Private).Invoke(_probe, arguments);
        double PhaseAt => (double)Get(_probe, "_pressurePhaseAt");

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks?.Dynamic != null,
                "Real authority block world");
            _match = PrototypeMatch.Current;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var first = _match.AddTestBot(prefab);
            var second = _match.AddTestBot(prefab);
            Assert.That(first, Is.Not.Null, "First real spawned fixture NPC");
            Assert.That(second, Is.Not.Null, "Second real spawned fixture NPC");
            _players = new[] { PrototypePlayer.Local, first, second };
            Assert.That(_players.Select(player => player.PlayerId).Distinct().Count(), Is.EqualTo(3));
            foreach (var player in _match.Players) player.enabled = false;
            foreach (var player in _players)
            {
                Assert.That(player.IsSpawned && player.IsServer, Is.True, "No fabricated ownership/spawn flags");
                var snapshot = player.Snapshot.Value;
                snapshot.SubWeaponId = 3;
                player.Snapshot.Value = snapshot;
            }
            _match.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match);
            var state = _match.State.Value; state.Phase = MatchPhase.Playing; _match.State.Value = state;
            _seeds = new Vector3[3];
            int mapMask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
            for (int index = 0; index < _seeds.Length; index++)
            {
                Assert.That(Physics.Raycast(new Vector3((index - 1) * 6, 4, -20), Vector3.down, out var floor, 10,
                    mapMask, QueryTriggerInteraction.Ignore), Is.True, "Fixture target has an original map floor");
                _seeds[index] = floor.point;
            }
            _directory = Path.Combine(Path.GetTempPath(), "BlockBlastSeedPublication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _session = Guid.NewGuid().ToString("N");
            // Keep the fixture inactive: production Awake must not install a global probe,
            // subscribe cameras, start recorders, or write the default output path.
            _probeObject = new GameObject("Seed file publication boundary fixture");
            _probeObject.SetActive(false);
            _probe = _probeObject.AddComponent<BlockDynamicAcceptanceProbe>();
            _report = Activator.CreateInstance(ProbeType.GetNestedType("Report", BindingFlags.NonPublic), true);
            Set(_report, "mode", "blast8"); Set(_report, "session", _session);
            Set(_probe, "_report", _report); Set(_probe, "_match", _match); Set(_probe, "_stage", "combat");
            Set(_probe, "_boot", Time.realtimeSinceStartupAsDouble);
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_probeObject != null) UnityEngine.Object.DestroyImmediate(_probeObject);
            _match?.Projectiles.Clear(); _match?.SubWeapons.Clear();
            BlockAttackService.Clear(); _fixture?.Dispose();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
            {
                string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Assert.That(Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase), Is.True);
                Directory.Delete(_directory, true);
            }
        }

        void ResetPhase(string phase)
        {
            BlockAttackService.Clear(); Terrain.Clear(_match.State.Value.Round); _fixture.Tick();
            _surfaceActors.Clear(); _bodyIds.Clear();
            _control = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".control.txt");
            Set(_probe, "_control", _control);
            _actors = _players.Select((player, index) =>
            {
                var state = player.Snapshot.Value;
                return new BlockDynamicAcceptanceProbe.BlastPressureActor { owner = player.PlayerId, team = state.Team,
                    slot = state.Slot, life = state.Revision, heroRevision = state.HeroRevision,
                    shotsBefore = state.ShotSequence, seed = _seeds[index], lane = PlayerMotorSimulation.HumanPosition(state) };
            }).ToList();
            object control = Activator.CreateInstance(ProbeType.GetNestedType("BlastPressureControl", BindingFlags.NonPublic), true);
            Set(control, "session", _session); Set(control, "round", _match.State.Value.Round); Set(control, "actors", _actors);
            Set(_probe, "_pressureControl", control); Set(_probe, "_pressureFailed", false);
            Set(_probe, "_pressureNextPoll", 0d);
            Set(_report, "blastPressure", new BlockDynamicAcceptanceProbe.BlastPressureReport { actors = _actors });
            Invoke("PressurePhase", phase);
            Assert.That(Read().sequence, Is.EqualTo(1));
            Assert.That(Pressure.controlPublications, Is.EqualTo(1));
        }

        PublishedControl Read()
        {
            string path = _control + ".blast8-phase.json";
            Assert.That(File.Exists(path), Is.True, "The real publisher installed its control file");
            Assert.That(File.Exists(path + ".writing"), Is.False, "Atomic publication consumed the temporary file");
            var value = JsonUtility.FromJson<PublishedControl>(File.ReadAllText(path));
            Assert.That(value.session, Is.EqualTo(_session));
            Assert.That(value.round, Is.EqualTo(_match.State.Value.Round));
            return value;
        }

        void Poll(double? at = null)
        {
            double now = at ?? Math.Max(Time.realtimeSinceStartupAsDouble, (double)Get(_probe, "_pressureNextPoll")) + .11;
            string phase = Read().phase;
            double began = PhaseAt;
            Invoke("ObserveBlastPressure", _match, now);
            TestContext.WriteLine($"Pressure poll phase={phase}->{Read().phase} now={now:R} began={began:R} elapsed={now - began:R} " +
                $"failure={Pressure.failure} actors={string.Join(";", _actors.Select(actor => $"{actor.owner}:s{actor.surfaces}/b{actor.bodies}/ready{actor.seeded}"))}");
        }

        IEnumerator Targets(params int[] indices)
        {
            foreach (int index in indices) _surfaceActors.Add(index);
            var world = _fixture.Empty();
            foreach (int index in _surfaceActors)
            {
                var cell = BlockWorld.CellAt(_seeds[index] + Vector3.up * (BlockWorld.CellSize * .51f));
                Assert.That(world.Set(cell, BlockCell.Create(_actors[index].team, surfaceAttached: true)), Is.True);
            }
            yield return _fixture.Seed(world);
            foreach (int index in indices)
            {
                Assert.That(Terrain.Dynamic.TrySpawn(_actors[index].team, _seeds[index] + Vector3.up * .6f,
                    Quaternion.identity, out var body), Is.True);
                if (!_bodyIds.TryGetValue(index, out var ids)) _bodyIds[index] = ids = new List<ulong>();
                ids.Add(body.Id);
                // Existing paid-contract fixture supplies the real ShotSequence condition.
                // This is not evidence of the separate owner-input/clearance chain.
                _fixture.PayMain(_players[index], _players[index].Snapshot.Value.HeroId);
                Assert.That(_players[index].Snapshot.Value.ShotSequence, Is.GreaterThan(_actors[index].shotsBefore));
            }
            Physics.SyncTransforms();
        }

        void PaidRemoveBodies(int index)
        {
            var payer = _players.First(player => player.Snapshot.Value.Team != _actors[index].team);
            foreach (ulong id in _bodyIds[index])
            {
                if (!Terrain.Dynamic.TryGet(id, out var body)) continue;
                var token = _fixture.PayMain(payer, payer.Snapshot.Value.HeroId);
                Assert.That(token.DirectLimit, Is.GreaterThan(0));
                BlockAttackService.Direct(token, body.Body.position, token.DirectLimit, 0x7b000000u + (uint)id, 1);
                Assert.That(Terrain.Dynamic.TryGet(id, out _), Is.False, "Paid production Direct really removed the target");
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator SeedAndVisualSeedPublishRealReadinessBothDirectionsAndSkipUnchangedPolls()
        {
            foreach (string phase in new[] { "seed", "visual-seed" })
            {
                ResetPhase(phase); double began = PhaseAt;
                yield return Targets(0);
                Poll(); var ready = Read();
                Assert.That(ready.phase, Is.EqualTo(phase));
                Assert.That(ready.sequence, Is.EqualTo(2)); Assert.That(Pressure.controlPublications, Is.EqualTo(2));
                Assert.That(ready.actors[0].seeded, Is.True);
                Assert.That(ready.actors[0].surfaces, Is.GreaterThan(0)); Assert.That(ready.actors[0].bodies, Is.GreaterThan(0));
                Assert.That(ready.actors[1].seeded || ready.actors[2].seeded, Is.False, "A real slow fixture actor prevents phase advance");
                Assert.That(PhaseAt, Is.EqualTo(began), "Same-phase publication does not reset the deadline");
                string unchanged = File.ReadAllText(_control + ".blast8-phase.json");
                Poll(); Poll();
                Assert.That(File.ReadAllText(_control + ".blast8-phase.json"), Is.EqualTo(unchanged));
                Assert.That(Pressure.controlPublications, Is.EqualTo(2), "No identical-state replacement was submitted");
                long removed = Terrain.Dynamic.RemovedCount;
                PaidRemoveBodies(0);
                Assert.That(Terrain.Dynamic.RemovedCount, Is.GreaterThan(removed));
                Poll(); var missing = Read();
                Assert.That(missing.phase, Is.EqualTo(phase)); Assert.That(missing.sequence, Is.EqualTo(3));
                Assert.That(missing.actors[0].seeded, Is.False); Assert.That(missing.actors[0].bodies, Is.Zero);
                Assert.That(Pressure.controlPublications, Is.EqualTo(3));
                Assert.That(PhaseAt, Is.EqualTo(began));
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator MultipleActorChangesPublishOnceAndFinalReadyPhaseTransitionDoesNotDoublePublish()
        {
            foreach (string phase in new[] { "seed", "visual-seed" })
            {
                ResetPhase(phase); double began = PhaseAt;
                yield return Targets(0, 1);
                Poll(); var partial = Read();
                Assert.That(partial.phase, Is.EqualTo(phase)); Assert.That(partial.sequence, Is.EqualTo(2));
                Assert.That(partial.actors[0].seeded && partial.actors[1].seeded, Is.True);
                Assert.That(partial.actors[2].seeded, Is.False);
                Assert.That(Pressure.controlPublications, Is.EqualTo(2), "Both actual world changes share one publication");
                Assert.That(PhaseAt, Is.EqualTo(began));
                yield return Targets(2);
                Poll(); var complete = Read();
                Assert.That(complete.phase, Is.EqualTo(phase == "seed" ? "recover" : "done"));
                Assert.That(complete.sequence, Is.EqualTo(3)); Assert.That(complete.actors.All(actor => actor.seeded), Is.True);
                Assert.That(Pressure.controlPublications, Is.EqualTo(3), "PressurePhase already published the complete actor state");
                Assert.That(Pressure.phases.Count, Is.EqualTo(2), "Only the initial and actual next phase were recorded");
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator PendingSurfaceDoesNotPublishReadyBeforeTheRealTerrainCommit()
        {
            foreach (string phase in new[] { "seed", "visual-seed" })
            {
                ResetPhase(phase);
                var world = _fixture.Empty();
                var cell = BlockWorld.CellAt(_seeds[0] + Vector3.up * (BlockWorld.CellSize * .51f));
                Assert.That(world.Set(cell, BlockCell.Create(_actors[0].team, surfaceAttached: true)), Is.True);
                Assert.That(Terrain.QueueSeed(world), Is.True);
                Assert.That(Terrain.PendingTransactions, Is.GreaterThan(0));
                Assert.That(Terrain.Dynamic.TrySpawn(_actors[0].team, _seeds[0] + Vector3.up * .6f,
                    Quaternion.identity, out _), Is.True);
                _fixture.PayMain(_players[0], _players[0].Snapshot.Value.HeroId);
                Poll(); var pending = Read();
                Assert.That(_actors[0].bodies, Is.GreaterThan(0), "The original counter saw the real free body");
                Assert.That(_actors[0].surfaces, Is.Zero, "Queued surfaces are absent from the published world");
                Assert.That(pending.phase, Is.EqualTo(phase)); Assert.That(pending.actors[0].seeded, Is.False);
                Assert.That(pending.sequence, Is.EqualTo(1)); Assert.That(Pressure.controlPublications, Is.EqualTo(1));
                yield return _fixture.Publish();
                Poll(); var published = Read();
                Assert.That(published.phase, Is.EqualTo(phase)); Assert.That(published.actors[0].seeded, Is.True);
                Assert.That(published.actors[0].surfaces, Is.GreaterThan(0));
                Assert.That(published.sequence, Is.EqualTo(2)); Assert.That(Pressure.controlPublications, Is.EqualTo(2));
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator RealActorLifecycleChangesFailBeforeAnyReadyPublication()
        {
            foreach (string phase in new[] { "seed", "visual-seed" })
            foreach (bool changeHero in new[] { false, true })
            {
                ResetPhase(phase);
                yield return Targets(0);
                var changed = _players[0].Snapshot.Value;
                if (changeHero) changed.HeroRevision++; else changed.Revision++;
                _players[0].Snapshot.Value = changed;
                Poll(); var failed = Read();
                Assert.That(failed.phase, Is.EqualTo("failed")); Assert.That(failed.sequence, Is.EqualTo(2));
                Assert.That(failed.actors[0].seeded, Is.False, "Owner validation preceded the original target counter");
                Assert.That(Pressure.failure, Does.Contain("disconnected, died, respawned or changed equipment"));
                Assert.That(Pressure.controlPublications, Is.EqualTo(2));
                Assert.That(Pressure.phases.Count, Is.EqualTo(2), "Only initial phase and the real identity failure were published");
            }
        }

        [UnityTest, Timeout(600000)] public IEnumerator AReadinessChangeNearTheDeadlineDoesNotExtendTheOriginalThirtyFiveSeconds()
        {
            foreach (string phase in new[] { "seed", "visual-seed" })
            {
                ResetPhase(phase); double began = PhaseAt;
                yield return Targets(0);
                Poll(began + 34.85);
                Assert.That(Read().actors[0].seeded, Is.True); Assert.That(PhaseAt, Is.EqualTo(began));
                Assert.That(Pressure.controlPublications, Is.EqualTo(2));
                Poll(began + 35.01);
                var failed = Read();
                Assert.That(failed.phase, Is.EqualTo("failed")); Assert.That(failed.sequence, Is.EqualTo(3));
                Assert.That(Pressure.failure, Does.Contain("Timed out in phase " + phase));
                Assert.That(Pressure.controlPublications, Is.EqualTo(3));
            }
        }
    }
}
#endif
