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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>
    /// Original stage-file entry, real spawned authority world, legal candidate queries and lifecycle contracts.
    /// Explicit fixture world poses and singleton installation do not prove independent owner input, payment or hit acceptance.
    /// </summary>
    public sealed partial class BlockEnemyPlanTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Methods = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Type ProbeType = typeof(BlockDynamicAcceptanceProbe);
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        GameObject _probeObject;
        BlockDynamicAcceptanceProbe _probe, _previousProbe;
        object _report;
        string _directory, _control;
        Vector3 _legalFloor;
        BlockTerrain Terrain => _match.Blocks;
        BlockDynamicAcceptanceProbe.EnemyPlanReport Plan =>
            (BlockDynamicAcceptanceProbe.EnemyPlanReport)Get(_report, "enemyPlan");
        static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        object Invoke(string method, params object[] arguments) => ProbeType.GetMethod(method, Methods).Invoke(_probe, arguments);
        static FieldInfo Instance => ProbeType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);

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
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local;
            Assert.That(_match.IsServer && _host.IsServer && _host.IsSpawned, Is.True, "No fabricated server or spawn flags");
            _match.enabled = false;
            foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match);
            var state = _match.State.Value; state.Phase = MatchPhase.Playing;
            state.EndsAt = _match.NetworkManager.ServerTime.Time + 600; _match.State.Value = state;
            Terrain.Clear(state.Round); _fixture.Tick();
            int mapMask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
            float oppositeZ = _host.Snapshot.Value.Position.z < 0 ? 20 : -20;
            Assert.That(Physics.Raycast(new Vector3(6, 4, oppositeZ), Vector3.down, out var floor, 10,
                mapMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(Terrain.IsAuthoredMapCollider(floor.collider), Is.True);
            _legalFloor = floor.point;
            _directory = Path.Combine(Path.GetTempPath(), "BlockEnemyPlan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory); _control = Path.Combine(_directory, "control.txt");
            _probeObject = new GameObject("Stable enemy plan contract fixture"); _probeObject.SetActive(false);
            _probe = _probeObject.AddComponent<BlockDynamicAcceptanceProbe>();
            _report = Activator.CreateInstance(ProbeType.GetNestedType("Report", BindingFlags.NonPublic), true);
            Set(_report, "mode", "network"); Set(_report, "session", Guid.NewGuid().ToString("N"));
            Set(_probe, "_report", _report); Set(_probe, "_match", _match); Set(_probe, "_control", _control);
            Set(_probe, "_boot", Time.realtimeSinceStartupAsDouble);
            _previousProbe = (BlockDynamicAcceptanceProbe)Instance.GetValue(null); Instance.SetValue(null, _probe);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_probe != null)
            {
                Invoke("CloseCommandReader");
                if (Get(_probe, "_commandReader") is BlockProbeCommandReader reader)
                {
                    yield return Wait(() => reader.Completion.IsCompleted, "Background command reader stops before fixture cleanup.");
                    reader.Completion.GetAwaiter().GetResult();
                }
            }
            if (_probeObject != null) UnityEngine.Object.DestroyImmediate(_probeObject);
            Instance.SetValue(null, _previousProbe);
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
        BlockDynamicBody Target(Vector3 floor)
        {
            Assert.That(Terrain.Dynamic.TrySpawn((byte)(3 - _host.Snapshot.Value.Team),
                floor + Vector3.up * (BlockWorld.CellSize * .5f + .002f), Quaternion.identity, out var body), Is.True);
            body.Body.Sleep(); Physics.SyncTransforms(); return body;
        }
        void Command(int sequence, string stage)
        {
            File.WriteAllLines(_control, new[] { sequence.ToString(), stage });
            var timer = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                Invoke("ReadCommand");
                if ((int)Get(_probe, "_command") >= sequence) break;
                System.Threading.Thread.Sleep(1);
            } while (timer.ElapsedMilliseconds < 5000);
            Assert.That((int)Get(_probe, "_command"), Is.EqualTo(sequence), "The real background reader must observe the control file.");
        }
        IEnumerator Ready()
        {
            for (int frame = 0; frame < 300 && Plan?.status != "ready"; frame++)
            {
                int before = Plan?.standChecks ?? 0;
                Invoke("ObserveEnemyPlan", _match);
                Assert.That(Plan.standChecks - before, Is.LessThanOrEqualTo(2), "Includes locked-target validation and candidate work");
                if (Plan.status == "placed-awaiting-step")
                {
                    // Production authority simulation with its real timeout/CanStand/weapon gates.
                    // No accepted Fire, forced canShoot or payment helper is used by this contract.
                    _host.Simulate(BlockDefaultContractFixture.Dt, _match.NetworkManager.ServerTime.Time, MatchPhase.Playing);
                    Assert.That(_host.Snapshot.Value.SimulationTick, Is.GreaterThan(Plan.baselineSimulationTick));
                }
                yield return null;
            }
            Assert.That(Plan.status, Is.EqualTo("ready"), Plan.lastReason);
        }
        PlayerInputFrame PlanInput(PlayerSnapshot state)
        {
            object[] arguments = { _host, state, new PlayerInputFrame() };
            ProbeType.GetMethod("EnemyInput", Methods).Invoke(_probe, arguments);
            return (PlayerInputFrame)arguments[2];
        }
        void ObserveNow()
        { Set(_probe, "_enemyNextValidation", 0d); Invoke("ObserveEnemyPlan", _match); }

        [UnityTest, Timeout(600000)] public IEnumerator OriginalStageSkipsAnIllegalCandidateAndPlacesOnlyAfterFullLegalPreflight()
        {
            var invalid = Target(_legalFloor + Vector3.right * 3);
            // Deliberately invalid initial fixture pose, closer to spawn than the legal opposite-side target.
            // Production planning must reject it, rather than silently treating the first dictionary entry as success.
            float edge = Mathf.Sign(_host.Snapshot.Value.Position.z) * (Terrain.World.Dimensions.y * .5f + 1);
            invalid.Body.position = new Vector3(_host.Snapshot.Value.Position.x, _legalFloor.y + .15f, edge);
            invalid.Body.Sleep(); Physics.SyncTransforms();
            var legal = Target(_legalFloor);
            Assert.That(invalid.IsAtRest && legal.IsAtRest, Is.True);
            Assert.That((invalid.Body.position - _host.Snapshot.Value.Position).sqrMagnitude,
                Is.LessThan((legal.Body.position - _host.Snapshot.Value.Position).sqrMagnitude));
            uint originalLife = _host.Snapshot.Value.Revision;
            Command(1, "enemy");
            Assert.That(BlockDynamicAcceptanceProbe.TryEnemyPlanTrace(_host, out var planning), Is.True);
            Assert.That(planning.targetId, Is.Zero);
            double originalStageAt = (double)Get(_probe, "_stageAt");
            yield return Ready();
            Assert.That(Plan.identity.targetId, Is.EqualTo(legal.Id));
            Assert.That(Plan.transitions.Any(item => item.candidateId == invalid.Id && item.reason.Contains("outside")), Is.True);
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(_host.Snapshot.Value.Revision, Is.EqualTo(originalLife + 1));
            Assert.That(Plan.baselineShots, Is.EqualTo(_host.Snapshot.Value.ShotSequence));
            Assert.That(Plan.baselineInk, Is.GreaterThan(0));
            Assert.That(Plan.identity.life, Is.EqualTo(_host.Snapshot.Value.Revision));
            Assert.That(HeroBodyShape.For(_host.Snapshot.Value.HeroId).Fits(Plan.placedFeet,
                new Collider[32], PlayerMotorSimulation.WorldMask), Is.True);
            Assert.That((double)Get(_probe, "_stageAt"), Is.EqualTo(originalStageAt), "Planning does not restart the external deadline");
            Invoke("InitializeEnemyPlan", _match);
            Assert.That(Plan.placements, Is.EqualTo(1), "Reobserving the same command cannot Respawn again");
        }

        [UnityTest, Timeout(600000)] public IEnumerator LockedBodySurvivesNearestChangesAndStopsOnRealRemovalWithoutAnotherPlacement()
        {
            var legal = Target(_legalFloor);
            Command(1, "enemy"); yield return Ready();
            uint life = _host.Snapshot.Value.Revision;
            var state = _host.Snapshot.Value;
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
            Vector3 away = Vector3.ProjectOnPlane(feet - legal.Body.position, Vector3.up).normalized;
            Vector3 behind = feet + away * 1.2f;
            var closer = Target(behind);
            Assert.That((closer.Body.position - state.Position).sqrMagnitude,
                Is.LessThan((legal.Body.position - state.Position).sqrMagnitude));
            ObserveNow();
            Assert.That(Plan.identity.targetId, Is.EqualTo(legal.Id), "Other lifecycle changes and closer bodies do not replace a valid lock");
            Assert.That(BlockDynamicAcceptanceProbe.TryEnemyPlanTrace(_host, out var trace), Is.True);
            Assert.That(trace.targetId, Is.EqualTo(legal.Id));
            // Fixture deletion is solely an identity-negative case, never ordinary paid destruction evidence.
            Assert.That(Terrain.Dynamic.DestroyIds(new[] { legal.Id }, state.Team), Is.EqualTo(1));
            Assert.That(PlanInput(_host.Snapshot.Value).Fire, Is.False);
            Assert.That(Plan.identity.targetId, Is.Zero); Assert.That(Plan.replans, Is.EqualTo(1));
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(_host.Snapshot.Value.Revision, Is.EqualTo(life));
        }

        [UnityTest, Timeout(600000)] public IEnumerator ReleaseAndLifecycleGatesRemainInActualPlayerStateAndStageExitClearsIdentity()
        {
            Target(_legalFloor); Command(1, "enemy"); yield return Ready();
            var state = _host.Snapshot.Value; state.AttackNeedsRelease = true; _host.Snapshot.Value = state;
            uint shots = state.ShotSequence; float ink = state.Ink;
            Assert.That(PlanInput(state).Fire, Is.False);
            Assert.That(_host.Snapshot.Value.AttackNeedsRelease, Is.True);
            Assert.That(_host.Snapshot.Value.ShotSequence, Is.EqualTo(shots)); Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(ink));
            state.HeroRevision++; _host.Snapshot.Value = state;
            Assert.That(BlockDynamicAcceptanceProbe.TryEnemyPlanTrace(_host, out _), Is.False);
            ObserveNow(); Assert.That(PlanInput(state).Fire, Is.False);
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(Plan.lastReason, Does.Contain("hero"));
            Command(2, "idle");
            Assert.That(Get(_probe, "_enemyHost"), Is.Null);
            Assert.That(BlockDynamicAcceptanceProbe.TryEnemyPlanTrace(_host, out _), Is.False);
        }

        [UnityTest, Timeout(600000)] public IEnumerator ARealWorldRoundResetCannotReuseTheOldPlanForPooledBodies()
        {
            Target(_legalFloor); Command(1, "enemy"); yield return Ready();
            uint life = _host.Snapshot.Value.Revision, oldRound = Plan.identity.round;
            Terrain.Clear(oldRound + 1); _fixture.Tick();
            var replacement = Target(_legalFloor);
            Assert.That(replacement.World.Round, Is.EqualTo(oldRound + 1));
            Assert.That(BlockDynamicAcceptanceProbe.TryEnemyPlanTrace(_host, out _), Is.False);
            ObserveNow(); Assert.That(PlanInput(_host.Snapshot.Value).Fire, Is.False);
            Assert.That(Plan.lastReason, Does.Contain("round"));
            Assert.That(_host.Snapshot.Value.Revision, Is.EqualTo(life)); Assert.That(Plan.placements, Is.EqualTo(1));
        }
    }
}
#endif
