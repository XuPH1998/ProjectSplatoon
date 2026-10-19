#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    // Runs the real serialization and replay dependency gate; this is not a socket or Player test.
    [Category("BlockSurfacePaint")]
    public sealed class BlockSurfaceTraversalTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        PrototypePlayer _player;
        BlockSurfaceWorld _surface;
        static readonly BlockSurfaceKey Key = new(11, -2, 3);

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Surface replay ownership fixture");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            _surface = new BlockSurfaceWorld(_terrain.Round);
            _surface.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, new[] { new Vector2Int(-2, 3) });
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.SurfacePaints)).SetValue(_terrain, _surface);
            var playerObject = new GameObject("Surface dependency predicting owner"); playerObject.transform.SetParent(_root.transform);
            _player = playerObject.AddComponent<PrototypePlayer>();
        }

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static PlayerSnapshot Supported(uint required = 1) => new()
        {
            Revision = 10, Health = 100, Team = 1, LifeState = PlayerLifeState.Alive,
            SupportIsSurfacePaint = true, SupportSurfaceFace = Key.FaceId, SupportSurfaceU = Key.U, SupportSurfaceV = Key.V,
            SupportTerrainRevision = required, Grounded = true
        };
        bool Ready(PlayerSnapshot state)
            => (bool)typeof(PrototypePlayer).GetMethod("TerrainReadyForReplay", Private).Invoke(_player, new object[] { state, 0 });
        Dictionary<BlockSurfaceKey, uint> SurfaceDependencies
            => (Dictionary<BlockSurfaceKey, uint>)typeof(PrototypePlayer).GetField("_surfaceReplayDependencies", Private).GetValue(_player);

        [Test] public void TypedSurfaceSupportSurvivesCanonicalAndDeltaNetworkSerialization()
        {
            var source = Supported(49); source.SupportCell = new Vector3Int(777, 888, 999);
            source.GroundNormal = new Vector3(-.2f, 1, .3f).normalized;
            using var writer = new FastBufferWriter(4096, Allocator.Temp); writer.WriteNetworkSerializable(source);
            using var reader = new FastBufferReader(writer, Allocator.Temp); reader.ReadNetworkSerializable(out PlayerSnapshot restored);
            AssertSupport(source, restored);
            var baseline = source; baseline.SupportIsSurfacePaint = false; baseline.SupportSurfaceFace = 0;
            baseline.SupportSurfaceU = baseline.SupportSurfaceV = 0; baseline.SupportTerrainRevision = 0;
            using var delta = new FastBufferWriter(4096, Allocator.Temp); PlayerSnapshotDelta.WriteDelta(delta, source, baseline);
            using var deltaReader = new FastBufferReader(delta, Allocator.Temp); PlayerSnapshotDelta.ReadDelta(deltaReader, ref baseline);
            AssertSupport(source, baseline);
        }

        static void AssertSupport(PlayerSnapshot expected, PlayerSnapshot actual)
        {
            Assert.That(actual.SupportIsSurfacePaint, Is.True); Assert.That(actual.SupportSurfaceFace, Is.EqualTo(expected.SupportSurfaceFace));
            Assert.That(actual.SupportSurfaceU, Is.EqualTo(expected.SupportSurfaceU)); Assert.That(actual.SupportSurfaceV, Is.EqualTo(expected.SupportSurfaceV));
            Assert.That(actual.SupportTerrainRevision, Is.EqualTo(expected.SupportTerrainRevision));
            Assert.That(actual.SupportCell, Is.EqualTo(expected.SupportCell)); Assert.That(actual.GroundNormal, Is.EqualTo(expected.GroundNormal));
        }

        [Test] public void SurfaceContactsCarryTheTypedKeyAndMotorSupportCannotLeakItToLegacyTerrain()
        {
            var contact = new PlayerMotorSimulation.TraversalContact(new BlockContact
            { Kind = BlockContactKind.SurfacePaint, SurfaceKey = Key, Owner = 1, Stable = true, Revision = 5 });
            Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.SurfacePaint)); Assert.That(contact.SurfaceKey, Is.EqualTo(Key));
            var state = Supported();
            var setSupport = typeof(PlayerMotorSimulation).GetMethod("SetSupport", BindingFlags.Static | BindingFlags.NonPublic);
            object[] arguments = { state, contact }; setSupport.Invoke(null, arguments); state = (PlayerSnapshot)arguments[0];
            Assert.That(state.SupportIsSurfacePaint, Is.True); Assert.That(state.SupportSurfaceFace, Is.EqualTo(11));
            Assert.That(state.SupportSurfaceU, Is.EqualTo(-2)); Assert.That(state.SupportSurfaceV, Is.EqualTo(3));
            var legacy = new PlayerMotorSimulation.TraversalContact(new BlockContact
            { Kind = BlockContactKind.Legacy, Owner = 1, Stable = true, Cell = Vector3Int.one, Revision = 7 });
            arguments[0] = state; arguments[1] = legacy; setSupport.Invoke(null, arguments); state = (PlayerSnapshot)arguments[0];
            Assert.That(state.SupportIsSurfacePaint, Is.False); Assert.That(state.SupportSurfaceFace, Is.Zero);
            Assert.That(state.SupportSurfaceU, Is.Zero); Assert.That(state.SupportSurfaceV, Is.Zero);
            Assert.That(state.SupportCell, Is.EqualTo(Vector3Int.one)); Assert.That(state.SupportTerrainRevision, Is.EqualTo(7));
        }

        [Test] public void AirborneSnapshotRetainsHistoricalSurfaceWaitUntilDeletionTombstoneArrives()
        {
            var state = Supported(); Assert.That(Ready(state), Is.False); Assert.That(SurfaceDependencies[Key], Is.EqualTo(1));
            var airborne = state; airborne.SupportIsSurfacePaint = false; airborne.SupportTerrainRevision = 0; airborne.Grounded = false;
            Assert.That(Ready(airborne), Is.False, "Losing current support does not prove historical surface state arrived.");
            _surface.TrySet(Key, 1, out _); _surface.TrySet(Key, 0, out _);
            Assert.That(Ready(airborne), Is.True, "A newer deletion is enough; waiting must not require the square still be owned.");
            Assert.That(SurfaceDependencies, Is.Empty);
        }

        [Test] public void VoxelAndSurfaceRevisionsAreSeparateDependenciesAndBothMustArrive()
        {
            var legacy = Supported(7); legacy.SupportIsSurfacePaint = false; legacy.SupportCell = new Vector3Int(0, 1, 0);
            Assert.That(Ready(legacy), Is.False);
            var surface = Supported(); Assert.That(Ready(surface), Is.False);
            _surface.TrySet(Key, 1, out _);
            var airborne = surface; airborne.SupportTerrainRevision = 0;
            Assert.That(Ready(airborne), Is.False, "A surface revision cannot satisfy the old voxel chunk dependency.");
            _terrain.World.Seal(7, new[] { BlockWorld.ChunkAt(legacy.SupportCell) });
            Assert.That(Ready(airborne), Is.True);
            var futureSurface = Supported(8);
            Assert.That(Ready(futureSurface), Is.False, "The voxel world's higher revision cannot stand in for a surface-cell revision.");
        }

        [Test] public void LifecycleRoundAndTimeoutBoundariesClearSurfaceReplayDependencies()
        {
            var state = Supported(); Assert.That(Ready(state), Is.False);
            var respawn = state; respawn.Revision++; respawn.SupportTerrainRevision = 0;
            Assert.That(Ready(respawn), Is.True); Assert.That(SurfaceDependencies, Is.Empty);
            Assert.That(Ready(state), Is.False);
            var timeout = state; timeout.InputTimedOut = true;
            Assert.That(Ready(timeout), Is.True); Assert.That(SurfaceDependencies, Is.Empty);
            Assert.That(Ready(state), Is.False);
            _terrain.Clear(_terrain.Round + 1); state.SupportTerrainRevision = 0;
            Assert.That(Ready(state), Is.True); Assert.That(SurfaceDependencies, Is.Empty);
        }

        [Test] public void DeferredMovementKeepsThePublishedTypedSurfaceSupportTogether()
        {
            var published = Supported(5); published.Position = Vector3.one; var incoming = Supported(9);
            incoming.SupportSurfaceFace = 100; incoming.SupportSurfaceU = 7; incoming.SupportSurfaceV = 8;
            object[] arguments = { published, incoming };
            typeof(PrototypePlayer).GetMethod("KeepPublishedMovement", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
            var retained = (PlayerSnapshot)arguments[1]; AssertSupport(published, retained);
            Assert.That(retained.Position, Is.EqualTo(published.Position));
        }

        [Test] public void SurfaceObservationsExposeRealKeysAndTombstonesWithoutReadingTheDefaultVoxel()
        {
            _terrain.World.Set(Vector3Int.zero, BlockCell.Create(2)); _terrain.World.Seal(99, new[] { Vector3Int.zero });
            var state = Supported();
            var before = new PrototypePlayer.ReconcileTelemetryState(state, _terrain);
            Assert.That(before.SupportIsSurfacePaint, Is.True); Assert.That(before.SurfaceKey, Is.EqualTo(Key));
            Assert.That(before.AppliedSupportRevision, Is.Zero); Assert.That(before.SupportPresent, Is.False);
            Assert.That(before.SupportOwner, Is.Zero, "An unrelated occupied default voxel is not surface ownership.");
            var observations = new List<PrototypePlayer.ReplayDependencyObservation>();
            System.Action<PrototypePlayer, PrototypePlayer.ReplayDependencyObservation> observer = (player, observation) => observations.Add(observation);
            PrototypePlayer.ReplayDependencyObserved += observer;
            try
            {
                Assert.That(Ready(state), Is.False);
                var pending = observations[0]; Assert.That(pending.After.TotalCount, Is.EqualTo(1));
                Assert.That(pending.After.Rows, Is.Empty); Assert.That(pending.After.SurfaceRows.Length, Is.EqualTo(1));
                Assert.That(pending.After.SurfaceRows[0].Key, Is.EqualTo(Key)); Assert.That(pending.After.SurfaceRows[0].Applied, Is.Zero);
                _surface.TrySet(Key, 1, out _); _surface.TrySet(Key, 0, out _);
                state.SupportTerrainRevision = 0; Assert.That(Ready(state), Is.True);
                var satisfied = observations[1]; Assert.That(satisfied.SatisfiedCount, Is.Zero);
                Assert.That(satisfied.SatisfiedSurfaceCount, Is.EqualTo(1)); Assert.That(satisfied.SatisfiedSurfaceCells, Is.EqualTo(new[] { Key }));
                Assert.That(satisfied.SatisfactionCheckPerformed, Is.True);
                state.SupportTerrainRevision = 1;
                var deleted = new PrototypePlayer.ReconcileTelemetryState(state, _terrain);
                Assert.That(deleted.AppliedSupportRevision, Is.EqualTo(2)); Assert.That(deleted.SupportPresent, Is.False);
                Assert.That(deleted.SupportOwner, Is.Zero);
            }
            finally { PrototypePlayer.ReplayDependencyObserved -= observer; }
        }
    }
}
#endif
