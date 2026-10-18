#if UNITY_EDITOR
using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class BlockBlastCompletionObservationTests
    {
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        Vector3 _center;
        Vector3Int _surface;
        uint _entity;
        int _queued, _completed, _completedRemoved;
        BlockTerrain Terrain => _match.Blocks;

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
                "Real authority surface/free block world");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local;
            _match.enabled = false;
            foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match);
            BlockAttackService.Clear(); Terrain.Clear(_match.State.Value.Round); _fixture.Tick();
            // Reproduce the real FixedUpdate boundary: the terrain freezes while the
            // public match phase is still Playing, before its caller stores Finished.
            var state = _match.State.Value; state.Phase = MatchPhase.Playing; _match.State.Value = state;
            Assert.That(Terrain.SurfaceOnly, Is.True);
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True, "Original map floor");
            _surface = BlockWorld.CellAt(floor.point + Vector3.up * (BlockWorld.CellSize * .51f));
            _center = BlockWorld.Center(_surface);
            _entity = 0; _queued = _completed = _completedRemoved = 0;
            BlockAttackObservation.BlastQueued += Queued;
            BlockAttackObservation.BlastCompleted += Completed;
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            BlockAttackObservation.BlastQueued -= Queued;
            BlockAttackObservation.BlastCompleted -= Completed;
            _match?.Projectiles.Clear(); _match?.SubWeapons.Clear();
            BlockAttackService.Clear(); _fixture?.Dispose();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Queued(BlockAttackObservation.Blast observation, int removed)
        { if (observation.Entity == _entity) _queued++; }

        void Completed(BlockAttackObservation.Blast observation, int removed)
        { if (observation.Entity == _entity) { _completed++; _completedRemoved += removed; } }

        IEnumerator SeedTargets()
        {
            var seed = _fixture.Empty(); seed.Set(_surface, BlockCell.Create(2, true, true));
            yield return _fixture.Seed(seed);
            Assert.That(Terrain.Dynamic.TrySpawn(2, _center + Vector3.up * .6f, Quaternion.identity, out var body), Is.True);
            body.Body.Sleep(); Physics.SyncTransforms();
            Assert.That(Terrain.World.BlueCount, Is.EqualTo(1));
            Assert.That(Terrain.Dynamic.BlueCount, Is.EqualTo(1));
            Assert.That(SurfaceColliderPresent(), Is.True, "The seeded surface really owns published collision");
        }

        bool SurfaceColliderPresent() => Physics.Raycast(_center + Vector3.up * 2, Vector3.down, out var hit, 3,
            1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore) && Terrain.TryContact(hit, out _);

        void PaidExplosion()
        {
            float charge = _fixture.PaySub(_host, SubWeaponType.BurstBomb);
            var burst = _match.SubWeapons.Spawn(_host, _host.Snapshot.Value, charge, _fixture.Now, Terrain.Round,
                new SubLaunchSolution { Position = _center + Vector3.up * 1.2f, Velocity = Vector3.down * 6, Normal = Vector3.up });
            Assert.That(burst, Is.Not.Null);
            _entity = burst.State.Id;
            // No terrain Tick or yielded frame occurs between the real projectile
            // collision and FinishRound, so its surface transaction is still queued.
            for (int step = 0; step < 120 && !burst.Removed; step++)
            {
                _fixture.Now += BlockDefaultContractFixture.Dt;
                _match.SubWeapons.Step(_fixture.Now, BlockDefaultContractFixture.Dt, _match.Players);
            }
            Assert.That(burst.Removed, Is.True, "Production BurstBomb hit really exploded");
            Assert.That(_queued, Is.EqualTo(1));
            Assert.That(_completed, Is.Zero);
            Assert.That(Terrain.Dynamic.BlueCount, Is.Zero, "The synchronous free-body destruction already happened");
            Assert.That(Terrain.World.BlueCount, Is.EqualTo(1), "The surface collider has not published yet");
            Assert.That(Terrain.PendingTransactions, Is.GreaterThan(0));
        }

        [UnityTest, Timeout(600000)] public IEnumerator FinishRoundCancellationCannotCompleteAnObservedBlast()
        {
            yield return SeedTargets();
            byte[] before = Terrain.ExportSnapshot();
            PaidExplosion();
            Terrain.FinishRound(_fixture.Now);
            Assert.That(_match.State.Value.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That(BlockAttackService.Active, Is.True, "The old token-validity guard still accepts this boundary");
            Assert.That(Terrain.Dynamic.Frozen, Is.True);
            Assert.That(Terrain.PendingTransactions, Is.Zero);
            Assert.That(_completed, Is.Zero, "A cancelled surface publication is not a completed blast, even after real dynamic removal");
            CollectionAssert.AreEqual(before, Terrain.ExportSnapshot());
            Assert.That(Terrain.World.BlueCount, Is.EqualTo(1));
            Assert.That(SurfaceColliderPresent(), Is.True, "Cancellation retained the published surface collider");
            _fixture.Tick();
            Assert.That(_completed, Is.Zero, "A following frozen tick cannot revive the cancelled completion");
        }

        [UnityTest, Timeout(600000)] public IEnumerator PublishedBlastStillCompletesOnceAfterRealColliderRemoval()
        {
            yield return SeedTargets();
            PaidExplosion();
            yield return _fixture.Publish();
            Assert.That(_completed, Is.EqualTo(1));
            Assert.That(_completedRemoved, Is.EqualTo(2), "The real surface and independent body removals both count");
            Assert.That(Terrain.World.BlueCount + Terrain.Dynamic.BlueCount, Is.Zero);
            Assert.That(SurfaceColliderPresent(), Is.False, "The published surface no longer participates in collision");
            Terrain.FinishRound(_fixture.Now);
            Assert.That(_completed, Is.EqualTo(1));
        }
    }
}
#endif
