#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockSurfacePaint")]
    public sealed class BlockDynamicSurfacePaintTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _floor;
        BlockDynamicWorld World => _terrain.Dynamic;
        static readonly Vector3 Point = new(.15f, 0, .15f);

        [SetUp] public void Setup()
        {
            _root = new GameObject("Free block surface conversion contract");
            var arena = _root.AddComponent<PrototypeArena>();
            var floor = new GameObject("Paintable authored floor"); floor.transform.SetParent(_root.transform, false);
            var paint = floor.AddComponent<PaintSurface>(); paint.SurfaceId = 71;
            _floor = floor.AddComponent<BoxCollider>(); _floor.center = new Vector3(0, -.15f, 0); _floor.size = new Vector3(1.8f, .3f, 1.8f);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena); _terrain.EnableSurfaceMode();
            World.SetAuthority(true); Physics.SyncTransforms();
            Assert.That(_terrain.TryGetSurfacePaint(_floor, Point, Vector3.up, out _), Is.True, "Fixture point must resolve a complete legal map square");
        }

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
        }

        BlockDynamicBody Spawn(byte owner = 1, Vector3? position = null)
        {
            Assert.That(World.TrySpawn(owner, position ?? (Point + Vector3.up * .152f), Quaternion.identity, out var body), Is.True);
            return body;
        }

        BlockSurfaceCell Cell()
        {
            Assert.That(_terrain.TryGetSurfacePaint(_floor, Point, Vector3.up, out var cell), Is.True);
            return cell;
        }

        [TestCase((byte)0)] [TestCase((byte)2)]
        public void BlankOrEnemySurfaceConsumesOneBodyAndPublishesBothRepresentationsTogether(byte priorOwner)
        {
            var key = Cell().Key;
            if (priorOwner != 0) Assert.That(_terrain.PaintSurfaceCell(key, priorOwner), Is.True);
            var body = Spawn(); ulong id = body.Id; uint round = World.Round, revision = World.LifecycleRevision;
            long born = World.CreatedCount, removed = World.RemovedCount; int notifications = 0, surfaceNotifications = 0;
            World.Changed += () =>
            {
                notifications++;
                Assert.That(World.TryGet(id, out _), Is.False, "Observers must never see painted surface with the consumed body alive");
                Assert.That(Cell().Owner, Is.EqualTo(1));
                Assert.That(World.LifecycleRevision, Is.EqualTo(revision + 1));
            };
            _terrain.SurfacePaintChanged += () =>
            {
                surfaceNotifications++;
                Assert.That(World.TryGet(id, out _), Is.False); Assert.That(body.Collider.enabled, Is.False);
                Assert.That(Cell().Owner, Is.EqualTo(1));
            };
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up, id, round, 1), Is.True);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up, id, round, 1), Is.False);
            Assert.That(notifications, Is.EqualTo(1)); Assert.That(surfaceNotifications, Is.EqualTo(1)); Assert.That(World.Count, Is.Zero);
            Assert.That(World.PinkCount, Is.Zero); Assert.That(World.CreatedCount, Is.EqualTo(born));
            Assert.That(World.RemovedCount, Is.EqualTo(removed + 1)); Assert.That(body.Collider.enabled, Is.False);
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.Zero, "Conversion never recreates attached voxel geometry");
        }

        [Test] public void SameTeamSurfaceRetainsMassIdentityAndRevisions()
        {
            Assert.That(_terrain.PaintSurfaceCell(Cell().Key, 1), Is.True);
            var body = Spawn(); var before = Cell(); uint lifecycle = World.LifecycleRevision;
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up), Is.False);
            Assert.That(World.Contains(body), Is.True); Assert.That(body.Body.isKinematic, Is.False);
            Assert.That(World.Count, Is.EqualTo(1)); Assert.That(World.LifecycleRevision, Is.EqualTo(lifecycle));
            Assert.That(Cell().Revision, Is.EqualTo(before.Revision)); Assert.That(World.RemovedCount, Is.Zero);
        }

        [TestCase((byte)0, false)] [TestCase((byte)0, true)]
        [TestCase((byte)2, false)] [TestCase((byte)2, true)]
        public void OneContactBatchConvertsItsBlankOrEnemySquareEvenWhenTheCloserSquareIsFriendly(byte otherOwner, bool reverse)
        {
            var ownKey = Cell().Key;
            Vector3 otherPoint = Point + Vector3.right * .16f;
            Assert.That(_terrain.TryGetSurfacePaint(_floor, otherPoint, Vector3.up, out var other), Is.True);
            Assert.That(other.Key, Is.Not.EqualTo(ownKey));
            Assert.That(_terrain.PaintSurfaceCell(ownKey, 1), Is.True);
            if (otherOwner != 0) Assert.That(_terrain.PaintSurfaceCell(other.Key, otherOwner), Is.True);
            var ownBefore = Cell();
            var body = Spawn(position: Point + new Vector3(.02f, .152f, 0));
            Assert.That((Point - body.Body.position).sqrMagnitude,
                Is.LessThan((otherPoint - body.Body.position).sqrMagnitude), "The friendly contact must win the old unfiltered distance rule");
            (Collider map, Vector3 point, Vector3 normal)[] contacts =
            {
                (_floor, Point, Vector3.up), (_floor, otherPoint, Vector3.up)
            };
            if (reverse) System.Array.Reverse(contacts);
            ulong id = body.Id; long born = World.CreatedCount, removed = World.RemovedCount;
            uint lifecycle = World.LifecycleRevision;
            Assert.That(World.TryConvertSurfaceContacts(body, contacts), Is.True);
            Assert.That(World.TryGet(id, out _), Is.False); Assert.That(World.Count, Is.Zero);
            Assert.That(World.CreatedCount, Is.EqualTo(born)); Assert.That(World.RemovedCount, Is.EqualTo(removed + 1));
            Assert.That(World.LifecycleRevision, Is.EqualTo(lifecycle + 1));
            Assert.That(_terrain.SurfacePaints.TryGet(other.Key, out var converted), Is.True);
            Assert.That(converted.Owner, Is.EqualTo(1));
            Assert.That(Cell().Owner, Is.EqualTo(1)); Assert.That(Cell().Revision, Is.EqualTo(ownBefore.Revision));
        }

        [Test] public void CallbackNormalIsOrientedFromMapTowardTheCube()
        {
            var body = Spawn(); var key = Cell().Key;
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.down), Is.True);
            Assert.That(Cell().Key, Is.EqualTo(key)); Assert.That(Cell().Owner, Is.EqualTo(1));
        }

        [Test] public void StaleIdRoundAndOwnerCannotConsumeAReusedComponent()
        {
            var body = Spawn(); ulong oldId = body.Id; uint oldRound = World.Round;
            World.Reset(oldRound + 1); _terrain.Clear(oldRound + 1); World.SetAuthority(true);
            var replacement = Spawn();
            Assert.That(replacement, Is.SameAs(body)); Assert.That(replacement.Id, Is.Not.EqualTo(oldId));
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up, oldId, oldRound, 1), Is.False);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up, replacement.Id, World.Round, 2), Is.False);
            Assert.That(World.Contains(replacement), Is.True); Assert.That(Cell().Owner, Is.Zero);
        }

        [Test] public void ReentrantObserverCanReusePoolButOldExpectedIdentityCannotConsumeReplacement()
        {
            var body = Spawn(); ulong oldId = body.Id; uint round = World.Round; BlockDynamicBody replacement = null;
            World.Changed += () =>
            {
                if (replacement != null || World.Count != 0) return;
                Assert.That(World.TrySpawn(2, new Vector3(.6f, 1, 0), Quaternion.identity, out replacement), Is.True);
            };
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up, oldId, round, 1), Is.True);
            Assert.That(replacement, Is.SameAs(body)); Assert.That(replacement.Id, Is.Not.EqualTo(oldId));
            Assert.That(World.TryConvertSurfaceContact(replacement, _floor, Point, Vector3.up, oldId, round, 1), Is.False);
            Assert.That(World.Contains(replacement), Is.True); Assert.That(Cell().Owner, Is.EqualTo(1));
        }

        [Test] public void ConversionWakesTheSleepingPileBeforeItsSupportingColliderDisappears()
        {
            var lower = Spawn(); var upper = Spawn(position: Point + Vector3.up * .452f);
            lower.Body.Sleep(); upper.Body.Sleep();
            Assert.That(upper.IsAtRest, Is.True);
            Assert.That(World.TryConvertSurfaceContact(lower, _floor, Point, Vector3.up), Is.True);
            Assert.That(World.Contains(upper), Is.True); Assert.That(upper.Body.IsSleeping(), Is.False);
        }

        [Test] public void PaintingTheSameTeamUnderATouchingSleeperKeepsItsNativeSleep()
        {
            var body = Spawn(); body.Body.Sleep();
            Assert.That(body.Body.IsSleeping(), Is.True);
            Assert.That(_terrain.PaintSurfaceCell(Cell().Key, 1), Is.True);
            _terrain.NotifySurfacePaintChanged();
            Assert.That(body.Body.IsSleeping(), Is.True); Assert.That(World.Contains(body), Is.True);
            Assert.That(World.Count, Is.EqualTo(1)); Assert.That(Cell().Owner, Is.EqualTo(1));
        }

        [Test] public void EnemyRepaintWakesOnlyTouchingSleepersAndDefersConversionToNativeContact()
        {
            var key = Cell().Key;
            Assert.That(_terrain.PaintSurfaceCell(key, 1), Is.True); _terrain.NotifySurfacePaintChanged();
            var body = Spawn(); var upper = Spawn(position: Point + Vector3.up * .454f);
            var away = Spawn(position: Point + new Vector3(.604f, .152f, 0));
            body.Body.Sleep(); upper.Body.Sleep(); away.Body.Sleep();
            Assert.That(_terrain.PaintSurfaceCell(key, 2), Is.True);
            _terrain.NotifySurfacePaintChanged();
            Assert.That(body.Body.IsSleeping(), Is.False); Assert.That(upper.Body.IsSleeping(), Is.True);
            Assert.That(away.Body.IsSleeping(), Is.True); Assert.That(World.Count, Is.EqualTo(3));
            Assert.That(Cell().Owner, Is.EqualTo(2)); Assert.That(World.RemovedCount, Is.Zero);
        }

        [Test] public void ClearingPaintWakesADifferentOwnerWithoutConvertingDuringDestruction()
        {
            var key = Cell().Key;
            Assert.That(_terrain.PaintSurfaceCell(key, 1), Is.True); _terrain.NotifySurfacePaintChanged();
            var body = Spawn(); body.Body.Sleep();
            Assert.That(_terrain.SurfacePaints.TrySet(key, 0, out _), Is.True);
            World.WakeSurfacePaintContacts(new[] { key, key });
            Assert.That(body.Body.IsSleeping(), Is.False); Assert.That(World.Contains(body), Is.True);
            Assert.That(Cell().Owner, Is.Zero); Assert.That(World.RemovedCount, Is.Zero);
        }

        [Test] public void AFullSurfaceContactBufferGrowsAndWakesEveryTouchingBodyOnce()
        {
            var key = Cell().Key;
            Assert.That(_terrain.PaintSurfaceCell(key, 2), Is.True); _terrain.NotifySurfacePaintChanged();
            var bodies = new List<BlockDynamicBody>();
            for (int x = -1; x <= 1; x += 2) for (int z = -1; z <= 1; z += 2)
                bodies.Add(Spawn(position: Point + new Vector3(x * .151f, .152f, z * .151f)));
            foreach (var body in bodies) { body.Body.Sleep(); Assert.That(body.Body.IsSleeping(), Is.True); }
            var buffer = typeof(BlockDynamicWorld).GetField("_surfacePaintCandidates",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            buffer.SetValue(World, new Collider[1]);
            long before = World.InspectActivation().supportWakes;
            World.WakeSurfacePaintContacts(new[] { key, key });
            foreach (var body in bodies) Assert.That(body.Body.IsSleeping(), Is.False);
            Assert.That(((Collider[])buffer.GetValue(World)).Length, Is.GreaterThan(bodies.Count));
            Assert.That(World.InspectActivation().supportWakes - before, Is.EqualTo(bodies.Count));
            Assert.That(World.Count, Is.EqualTo(bodies.Count)); Assert.That(Cell().Owner, Is.EqualTo(2));
        }

        [Test] public void NonMapContactsAndInvalidGeometryNeverConvert()
        {
            var body = Spawn(); var other = Spawn(2, new Vector3(.6f, 1, 0));
            Assert.That(World.TryConvertSurfaceContact(body, other.Collider, Point, Vector3.up), Is.False);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, new Vector3(float.NaN, 0, 0), Vector3.up), Is.False);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.zero), Is.False);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point + Vector3.up, Vector3.up), Is.False,
                "A point away from the actual map face cannot borrow the same world-grid column");
            Assert.That(World.Count, Is.EqualTo(2)); Assert.That(Cell().Owner, Is.Zero);
        }

        [Test] public void ReplicaAndFrozenWorldCannotChangeSurfaceOrDeleteBodies()
        {
            var body = Spawn(); World.SetAuthority(false);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up), Is.False);
            World.SetAuthority(true); World.Freeze();
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up), Is.False);
            Assert.That(World.Contains(body), Is.True); Assert.That(Cell().Owner, Is.Zero);
            var states = new List<BlockDynamicState>(); World.CaptureStates(states);
            Assert.That(states.Count, Is.EqualTo(1)); Assert.That(states[0].Id, Is.EqualTo(body.Id));
        }

        [Test] public void SurfaceRevisionExhaustionDoesNotConsumeTheBody()
        {
            var body = Spawn(); uint lifecycle = World.LifecycleRevision;
            typeof(BlockSurfaceWorld).GetProperty(nameof(BlockSurfaceWorld.Revision)).SetValue(_terrain.SurfacePaints, uint.MaxValue);
            Assert.That(World.TryConvertSurfaceContact(body, _floor, Point, Vector3.up), Is.False);
            Assert.That(World.Contains(body), Is.True); Assert.That(body.Collider.enabled, Is.True);
            Assert.That(World.LifecycleRevision, Is.EqualTo(lifecycle)); Assert.That(World.RemovedCount, Is.Zero);
            Assert.That(Cell().Owner, Is.Zero);
        }
    }
}
#endif
