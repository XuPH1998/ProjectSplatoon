#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockSurfacePaint")]
    public sealed class BlockSurfaceDynamicEnvelopeTests
    {
        static readonly BlockSurfaceKey Key = new(11, 0, 0);
        static BlockSurfaceWorld Surface()
        {
            var world = new BlockSurfaceWorld(7);
            world.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, new[] { Vector2Int.zero });
            return world;
        }
        static BlockDynamicState Body(ulong id = 1) => new() { Id = id, Round = 7, Owner = 1,
            Position = Vector3.up * 2, Rotation = Quaternion.identity, Velocity = Vector3.right };
        static byte[] BodyBytes(params BlockDynamicState[] states)
            => BlockDynamicCodec.Encode(7, states, Array.Empty<ulong>());
        static byte[] Edit(byte[] bytes, int offset, Action<BinaryWriter> edit)
        {
            var changed = (byte[])bytes.Clone();
            using var stream = new MemoryStream(changed); using var writer = new BinaryWriter(stream);
            stream.Position = offset; edit(writer); return changed;
        }

        [Test] public void FullEnvelopeRetainsBothOwnershipAndRotatingBodyLifecycle()
        {
            var surface = Surface(); surface.TrySet(Key, 2, out _);
            var expected = Body(); expected.Rotation = Quaternion.Euler(20, 40, 15); expected.AngularVelocity = Vector3.forward;
            byte[] bytes = BlockSurfaceDynamicEnvelope.Encode(BodyBytes(expected), surface.ExportFull(), surface.Revision, true);
            BlockSurfaceDynamicEnvelope.Decode(bytes, out var dynamicBytes, out var surfaceBytes, out uint revision, out bool full);
            Assert.That(full, Is.True); Assert.That(revision, Is.EqualTo(1));
            var copy = Surface(); Assert.That(copy.TryImportFull(surfaceBytes), Is.True);
            copy.TryGet(Key, out var painted); Assert.That(painted.Owner, Is.EqualTo(2));
            var created = new List<BlockDynamicState>(); var removed = new List<ulong>();
            BlockDynamicCodec.Decode(dynamicBytes, 7, created, removed);
            Assert.That(created.Count, Is.EqualTo(1)); Assert.That(removed, Is.Empty);
            Assert.That(created[0].Id, Is.EqualTo(expected.Id)); Assert.That(created[0].Rotation, Is.EqualTo(expected.Rotation));
            Assert.That(created[0].AngularVelocity, Is.EqualTo(expected.AngularVelocity));
        }

        [Test] public void PaintOnlyDeltaCarriesDeletionWithoutInventingDynamicLifecycleChanges()
        {
            var server = Surface(); server.TrySet(Key, 2, out _);
            var client = Surface(); Assert.That(client.TryImportFull(server.ExportFull()), Is.True);
            uint baseline = client.Revision; server.TrySet(Key, 0, out _);
            byte[] bytes = BlockSurfaceDynamicEnvelope.Encode(BodyBytes(), server.ExportDelta(baseline), server.Revision, false);
            BlockSurfaceDynamicEnvelope.Decode(bytes, out var dynamicBytes, out var surfaceBytes, out uint revision, out bool full);
            Assert.That(full, Is.False); Assert.That(revision, Is.EqualTo(2));
            var created = new List<BlockDynamicState>(); var removed = new List<ulong>();
            BlockDynamicCodec.Decode(dynamicBytes, 7, created, removed);
            Assert.That(created, Is.Empty); Assert.That(removed, Is.Empty);
            Assert.That(client.TryImportDelta(surfaceBytes), Is.True); Assert.That(client.BlueCount, Is.Zero);
            client.TryGet(Key, out var blank); Assert.That(blank.Owner, Is.Zero); Assert.That(blank.Revision, Is.EqualTo(2));
        }

        [Test] public void LegacyBodyPayloadCannotDowngradeAnEnabledSurfaceEnvelope()
        {
            Assert.Throws<InvalidDataException>(() => BlockSurfaceDynamicEnvelope.Decode(BodyBytes(Body()), out _, out _, out _, out _));
        }

        [Test] public void UnknownVersionNonBooleanFlagAndForgedLengthsAreRejected()
        {
            byte[] bytes = BlockSurfaceDynamicEnvelope.Encode(BodyBytes(), Surface().ExportFull(), 0, true);
            foreach (var changed in new[] { Edit(bytes, 4, w => w.Write(2)), Edit(bytes, 12, w => w.Write((byte)2)),
                Edit(bytes, 13, w => w.Write(int.MaxValue)), Edit(bytes, 17, w => w.Write(-1)),
                Edit(bytes, 13, w => w.Write(11)), Edit(bytes, 17, w => w.Write(29)) })
                Assert.Throws<InvalidDataException>(() => BlockSurfaceDynamicEnvelope.Decode(changed, out _, out _, out _, out _));
            Array.Resize(ref bytes, bytes.Length + 1);
            Assert.Throws<InvalidDataException>(() => BlockSurfaceDynamicEnvelope.Decode(bytes, out _, out _, out _, out _));
        }

        [Test] public void EveryTruncatedPrefixFailsBeforeReturningPayloads()
        {
            byte[] bytes = BlockSurfaceDynamicEnvelope.Encode(BodyBytes(Body()), Surface().ExportFull(), 0, true);
            for (int count = 0; count < bytes.Length; count++)
            {
                var truncated = new byte[count]; Array.Copy(bytes, truncated, count);
                Assert.Throws<InvalidDataException>(() => BlockSurfaceDynamicEnvelope.Decode(truncated, out _, out _, out _, out _), "Length " + count);
            }
        }

        [Test] public void PreparedSurfaceAndBodyRemovalAreBothCommittedBeforeAnyChangedObserver()
        {
            var root = new GameObject("Combined lifecycle commit fixture");
            try
            {
                var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
                var world = terrain.Dynamic; world.Reset(7);
                Assert.That(world.ApplySnapshot(7, 1, 1, new[] { Body() }, false), Is.True);
                var visibleSurface = Surface(); var prepared = Surface(); prepared.TrySet(Key, 1, out _);
                int installerCalls = 0, observations = 0;
                world.Changed += () =>
                {
                    observations++;
                    Assert.That(installerCalls, Is.EqualTo(1)); Assert.That(world.LifecycleRevision, Is.EqualTo(2));
                    Assert.That(world.Count, Is.Zero); Assert.That(visibleSurface.PinkCount, Is.EqualTo(1));
                };
                Assert.That(world.ApplySnapshot(7, 2, 2, Array.Empty<BlockDynamicState>(), false,
                    () => { installerCalls++; visibleSurface = prepared; }), Is.True);
                Assert.That(observations, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false); }
        }

        [Test] public void InvalidBodyOrUnresetRoundCannotInvokeThePairedInstaller()
        {
            var root = new GameObject("Rejected combined lifecycle fixture");
            try
            {
                var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
                var world = terrain.Dynamic; world.Reset(7);
                Assert.That(world.ApplySnapshot(7, 1, 1, new[] { Body() }, false), Is.True);
                int installerCalls = 0, observations = 0; world.Changed += () => observations++;
                var invalid = Body(2); invalid.Position.x = float.NaN;
                Assert.That(world.ApplySnapshot(7, 2, 2, new[] { Body(), invalid }, false, () => installerCalls++), Is.False);
                Assert.That(world.ApplySnapshot(8, 2, 2, Array.Empty<BlockDynamicState>(), false, () => installerCalls++), Is.False);
                Assert.That(installerCalls, Is.Zero); Assert.That(observations, Is.Zero);
                Assert.That(world.Count, Is.EqualTo(1)); Assert.That(world.Round, Is.EqualTo(7));
                Assert.That(world.LifecycleRevision, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false); }
        }
    }
}
#endif
