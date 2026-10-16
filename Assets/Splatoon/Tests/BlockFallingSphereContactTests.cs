#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Exercises the published remote world, not a mutated live World or a mock collider.
    public sealed class BlockFallingSphereContactTests
    {
        const double Epoch = 10;
        const float Radius = .14976911f;
        const string Preview = "Assets/GameResource/Characters/RifleGirlChibi/Preview/RifleGirlChibiPreview.unity";
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            Assert.That(Application.isPlaying, Is.False, "Use the preserved EditMode runner.");
            Assert.That(SessionState.GetBool("RoomDiscovery.PlaySmoke", false), Is.False);
            EditorSceneManager.OpenScene(Preview, OpenSceneMode.Single);
            yield return new EnterPlayMode();
            Assert.That(PrototypeApp.Current, Is.Null, "The existing preview skips game bootstrap.");
            Assert.That(BlockTerrain.Current, Is.Null);
            _root = new GameObject("Published falling sphere contact geometry");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            _terrain.Initialize(null);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static BlockWorld World() => new(new Vector2(32, 64));
        static FallingRun Column(BlockWorld world, Vector3Int bottom, params byte[] owners)
        {
            for (int i = 0; i < owners.Length; i++) world.Set(bottom + Vector3Int.up * i, BlockCell.Create(owners[i]));
            return world.Detach(bottom, owners.Length, Epoch);
        }
        IEnumerator Install(BlockWorld incoming)
        {
            incoming.Seal(_terrain.Revision + 1, incoming.RevisionKeys.ToArray());
            uint revision = incoming.Revision;
            int publications = 0;
            Action<BlockTerrainCommit> published = _ => publications++;
            _terrain.PublishedCommit += published;
            try
            {
                Assert.That(_terrain.ApplyRemoteSnapshot(incoming.EncodeSnapshot()), Is.True);
                Assert.That(_terrain.PendingTransactions, Is.GreaterThan(0));
                for (int tick = 0; tick < 1000 && _terrain.PendingTransactions != 0; tick++)
                { _terrain.Tick(false, Epoch, .016f); yield return null; }
                Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(_terrain.FailedTransactions, Is.Zero);
                Assert.That(publications, Is.EqualTo(1));
                Assert.That(_terrain.Revision, Is.EqualTo(revision));
                Assert.That(_terrain.SimulationTime, Is.EqualTo(Epoch));
                Assert.That(_terrain.World, Is.Not.SameAs(incoming));
                Assert.That(_terrain.FallingCount, Is.EqualTo(incoming.Falling.Values.Sum(run => run.Cells.Count(cell => cell.Occupied))));
            }
            finally { _terrain.PublishedCommit -= published; }
        }

        Vector3 Center(ulong runId, Vector3Int cell)
            => BlockWorld.Center(cell) - Vector3.up * _terrain.World.Falling[runId].Offset(_terrain.SimulationTime);

        BlockFallingHit Hit(Vector3 origin, Vector3 direction, float limit, float radius,
            byte owner, ulong run, Vector3Int cell, byte? ignoreTeam = null)
        {
            Assert.That(_terrain.RaycastFalling(origin, direction, limit, radius, out var hit, ignoreTeam), Is.True);
            Assert.That(hit.Owner, Is.EqualTo(owner)); Assert.That(hit.RunId, Is.EqualTo(run)); Assert.That(hit.Cell, Is.EqualTo(cell));
            Assert.That(hit.Distance, Is.InRange(0, limit));
            Assert.That(hit.Normal.magnitude, Is.EqualTo(1).Within(.00001f));
            var box = new Bounds(Center(run, cell), Vector3.one * BlockWorld.CellSize);
            Assert.That(Vector3.Distance(box.ClosestPoint(hit.Point), hit.Point), Is.LessThan(.000003f), "Point lies on the real occupied cube.");
            Vector3 sphereCenter = origin + direction.normalized * hit.Distance;
            Assert.That(Vector3.Distance(box.ClosestPoint(sphereCenter), sphereCenter), Is.LessThanOrEqualTo(radius + .000004f));
            if (hit.Distance > .00001f && radius > 0)
            {
                Assert.That(Vector3.Distance(hit.Point, sphereCenter), Is.EqualTo(radius).Within(.000004f));
                Assert.That(Vector3.Dot(hit.Normal, (sphereCenter - hit.Point).normalized), Is.GreaterThan(.999f));
            }
            Assert.That(_terrain.TryContact(hit.Collider, hit.Point, hit.Normal, out var contact), Is.True,
                "Every successful sweep resolves through the production falling proxy contact.");
            Assert.That(contact.Owner, Is.EqualTo(owner)); Assert.That(contact.Cell, Is.EqualTo(cell));
            Assert.That(contact.Stable, Is.False); Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
            return hit;
        }

        [UnityTest]
        public IEnumerator SuppliedEdgeRayReturnsRealSurfaceAndLaterAccurateEntry()
        {
            var cell = new Vector3Int(118, 13, -319);
            var world = World(); var run = Column(world, cell, 2);
            yield return Install(world);
            Vector3 origin = new(12, 1.4f, -30);
            var hit = Hit(origin, Vector3.back, 3, Radius, 2, run.Id, cell);
            var box = new Bounds(Center(run.Id, cell), Vector3.one * BlockWorld.CellSize);
            double dx = Math.Max(0, origin.x - box.max.x), dy = Math.Max(0, origin.y - box.max.y);
            double roundedFront = Math.Sqrt((double)Radius * Radius - dx * dx - dy * dy);
            double expected = origin.z - box.max.z - roundedFront;
            Assert.That(hit.Distance, Is.EqualTo(expected).Within(.000004));
            Assert.That(hit.Distance, Is.GreaterThan(origin.z - box.max.z - Radius + .03f));
            Assert.That(hit.Point.x, Is.EqualTo(box.max.x).Within(.000003f));
            Assert.That(hit.Point.z, Is.EqualTo(box.max.z).Within(.000003f));
            Assert.That(hit.Point.y, Is.EqualTo(origin.y).Within(.000003f));
            Assert.That(hit.Normal.x, Is.GreaterThan(.6f)); Assert.That(hit.Normal.z, Is.GreaterThan(.6f));
        }

        [UnityTest]
        public IEnumerator ExpandedAabbCornerIsNotASphereHitOrInitialOverlap()
        {
            var cell = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, cell, 2);
            yield return Install(world);
            Vector3 center = Center(run.Id, cell);
            Vector3 offset = new(.18f, .18f, 0);
            var enlarged = new Bounds(center, Vector3.one * (BlockWorld.CellSize + 2 * Radius));
            Assert.That(enlarged.Contains(center + offset), Is.True, "Old broad phase admits this false corner overlap.");
            Assert.That(_terrain.RaycastFalling(center + offset + Vector3.forward, Vector3.back, 2, Radius, out _), Is.False);
            Assert.That(_terrain.RaycastFalling(center + offset, Vector3.forward, 1, Radius, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator RoundedCornerAndFaceReturnActualPointsAndNormals()
        {
            var cell = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, cell, 1);
            yield return Install(world);
            Vector3 center = Center(run.Id, cell);
            var corner = Hit(center + new Vector3(.1f, .1f, 1), Vector3.back, 2, .1f, 1, run.Id, cell);
            Assert.That(corner.Distance, Is.EqualTo(.95 - Math.Sqrt(.01 - .005)).Within(.000005));
            Assert.That(Vector3.Distance(corner.Point, center + Vector3.one * .05f), Is.LessThan(.000003f));
            Assert.That(corner.Normal.x, Is.GreaterThan(.49f)); Assert.That(corner.Normal.y, Is.GreaterThan(.49f));
            var face = Hit(center + Vector3.forward, Vector3.back, 2, .1f, 1, run.Id, cell);
            Assert.That(face.Distance, Is.EqualTo(.85f).Within(.000005f));
            Assert.That(face.Normal, Is.EqualTo(Vector3.forward));
        }

        [UnityTest]
        public IEnumerator ZeroRadiusAndNonUnitDirectionPreserveMetreDistance()
        {
            var cell = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, cell, 1);
            yield return Install(world);
            Vector3 center = Center(run.Id, cell);
            var hit = Hit(center + Vector3.right * .4f, Vector3.left * 7, .5f, 0, 1, run.Id, cell);
            Assert.That(hit.Distance, Is.EqualTo(.35f).Within(.000003f));
            Assert.That(hit.Normal, Is.EqualTo(Vector3.right));
            Assert.That(_terrain.RaycastFalling(center + new Vector3(.4f, .051f, 0), Vector3.left, .5f, 0, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator InitialContainmentAndExternalOverlapReturnZeroAndRealContact()
        {
            var cell = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, cell, 1);
            yield return Install(world);
            Vector3 center = Center(run.Id, cell);
            var contained = Hit(center, Vector3.right, 0, 0, 1, run.Id, cell);
            Assert.That(contained.Distance, Is.Zero);
            Assert.That(Vector3.Distance(contained.Point, center), Is.EqualTo(.05f).Within(.000003f));
            var overlap = Hit(center + Vector3.right * .12f, Vector3.right, .5f, .1f, 1, run.Id, cell);
            Assert.That(overlap.Distance, Is.Zero); Assert.That(overlap.Normal, Is.EqualTo(Vector3.right));
            Assert.That(_terrain.RaycastFalling(center + Vector3.right * .3f, Vector3.right, .5f, .1f, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator SegmentLimitRejectsFutureContactAndIncludesItsEndpoint()
        {
            var cell = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, cell, 2);
            yield return Install(world);
            Vector3 center = Center(run.Id, cell), origin = center + Vector3.right * .4f;
            Assert.That(_terrain.RaycastFalling(origin, Vector3.left, .24f, .1f, out _), Is.False);
            var hit = Hit(origin, Vector3.left, .251f, .1f, 2, run.Id, cell);
            Assert.That(hit.Distance, Is.EqualTo(.25f).Within(.000003f));
            // All values below are exact binary fractions relative to the
            // real box plane, so this checks a true endpoint rather than the
            // rounding of a previously returned float travel distance.
            var box = new Bounds(center, Vector3.one * BlockWorld.CellSize);
            Vector3 endpointOrigin = new(box.max.x + .125f, center.y, center.z);
            Hit(endpointOrigin, Vector3.left, .125f, 0, 2, run.Id, cell);
        }

        [UnityTest]
        public IEnumerator PublishedMixedOwnersAndHolesRetainOriginalCellIdentity()
        {
            var bottom = new Vector3Int(0, 20, 0);
            var world = World(); var run = Column(world, bottom, 1, 2, 2);
            Assert.That(world.RemoveFallingCell(run.Id, 1, out _), Is.True);
            yield return Install(world);
            Vector3 lower = Center(run.Id, bottom), upper = Center(run.Id, bottom + Vector3Int.up * 2);
            Hit(lower + Vector3.right * .4f, Vector3.left, .5f, .01f, 1, run.Id, bottom);
            Hit(upper + Vector3.right * .4f, Vector3.left, .5f, .01f, 2, run.Id, bottom + Vector3Int.up * 2);
            Assert.That(_terrain.RaycastFalling((lower + upper) * .5f + Vector3.right * .4f, Vector3.left, .5f, .01f, out _), Is.False);
            Assert.That(_terrain.RaycastFalling(lower + Vector3.right * .4f, Vector3.left, .5f, .01f, out _, 1), Is.False);
            Hit(upper + Vector3.right * .4f, Vector3.left, .5f, .01f, 2, run.Id, bottom + Vector3Int.up * 2, 1);
        }

        [UnityTest]
        public IEnumerator RemoteMotionOffsetIsQueriedAtCurrentTimeWithoutChangingSourceCell()
        {
            var cell = new Vector3Int(0, 30, 0);
            var world = World(); var run = Column(world, cell, 2); run.InitialOffset = .12f;
            yield return Install(world);
            _terrain.Tick(false, Epoch + .2, .2f);
            var published = _terrain.World.Falling[run.Id];
            Assert.That(published.Offset(_terrain.SimulationTime), Is.GreaterThan(.3f));
            Vector3 center = Center(run.Id, cell);
            var hit = Hit(center + Vector3.right * .4f, Vector3.left, .5f, .04f, 2, run.Id, cell);
            Assert.That(hit.Point.y, Is.EqualTo(center.y).Within(.000003f));
            Assert.That(_terrain.RaycastFalling(BlockWorld.Center(cell) + Vector3.right * .4f, Vector3.left, .5f, .04f, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator NearestRunAndDirectIgnoredTeamSelectActualRemainingCell()
        {
            Vector3Int farCell = new Vector3Int(0, 20, 0), nearCell = new Vector3Int(0, 20, 6);
            var world = World(); var far = Column(world, farCell, 2); var near = Column(world, nearCell, 1);
            yield return Install(world);
            Vector3 origin = Center(near.Id, nearCell) + Vector3.forward;
            var first = Hit(origin, Vector3.back, 2, .1f, 1, near.Id, nearCell);
            var ignored = Hit(origin, Vector3.back, 2, .1f, 2, far.Id, farCell, 1);
            Assert.That(first.Distance, Is.LessThan(ignored.Distance));
            Assert.That(ignored.Distance - first.Distance, Is.EqualTo(.6f).Within(.000004f));
            Hit(origin, Vector3.back, 2, .1f, 1, near.Id, nearCell, 2);
        }
    }
}
#endif
