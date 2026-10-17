#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BlockBuildQueryTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        BlockTerrain _terrain;
        object Call(string name, params object[] args) => typeof(BlockTerrain).GetMethod(name, Private).Invoke(_terrain, args);
        IDictionary Cache(string name) => (IDictionary)typeof(BlockTerrain).GetField(name, Private).GetValue(_terrain);
        HashSet<Collider> StaticColliders => (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", Private).GetValue(_terrain);
        bool Overlap(Vector3Int cell) { Physics.SyncTransforms(); return (bool)Call("BuildMapOverlapCached", cell); }

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrain = new GameObject("Build query regression terrain").AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
        }
        [TearDown] public void Cleanup()
        {
            if (_terrain != null) Object.DestroyImmediate(_terrain.gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static Vector3Int[] Normals => new[] { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down,
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) };
        IEnumerable<Vector3Int> Optimized(BlockWorld world, Vector3 point, Vector3Int normal, int count, float radius)
        {
            var type = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var command = Activator.CreateInstance(type, true);
            type.GetField("BuildDimensions").SetValue(command, world.Dimensions); type.GetField("BuildBottom").SetValue(command, world.Bottom);
            type.GetField("Point").SetValue(command, point);
            type.GetField("Normal").SetValue(command, (Vector3)normal); type.GetField("Count").SetValue(command, count);
            type.GetField("Radius").SetValue(command, radius);
            return (IEnumerable<Vector3Int>)Call("OrderedBuildCandidates", command);
        }

        [Test] public void QueuedBuildKeepsOnlyBoundsWhileDestroyRetainsItsSnapshotAndTime()
        {
            var source = new BlockWorld(new Vector2(2.3f, 3.7f), round: 19, bottom: -.2f);
            var worldProperty = typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World));
            var clock = typeof(BlockTerrain).GetField("_now", Private);
            worldProperty.SetValue(_terrain, source); clock.SetValue(_terrain, 12.25);
            var point = new Vector3(-1.15f, -.21f, 1.84f);
            _terrain.QueueBuild(point, Vector3.up, 1, 30, .2f, false);
            _terrain.QueueDestroy(point, .3f, .15f, 1, false);
            var queued = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().ToArray();
            Assert.That(queued.Length, Is.EqualTo(2));
            var type = queued[0].GetType(); var build = queued[0]; var destroy = queued[1];
            worldProperty.SetValue(_terrain, new BlockWorld(new Vector2(8, 9), source.Round, bottom: -1));
            clock.SetValue(_terrain, 20.0);
            Assert.That(type.GetField("Build").GetValue(build), Is.EqualTo(true));
            Assert.That(type.GetField("Source").GetValue(build), Is.Null, "A queued build cannot retain an old world's dictionaries");
            Assert.That(type.GetField("BuildDimensions").GetValue(build), Is.EqualTo(source.Dimensions));
            Assert.That(type.GetField("BuildBottom").GetValue(build), Is.EqualTo(source.Bottom));
            CollectionAssert.AreEqual(LegacyCandidates(source, point, Vector3Int.up, 30, .2f).Where(source.InBounds).ToArray(),
                ((IEnumerable<Vector3Int>)Call("OrderedBuildCandidates", build)).Where(source.InBounds).ToArray(),
                "A queued build keeps its original bounds after the published world changes");
            Assert.That(type.GetField("Build").GetValue(destroy), Is.EqualTo(false));
            Assert.That(type.GetField("Source").GetValue(destroy), Is.SameAs(source), "Destruction must inspect its original published snapshot");
            Assert.That(type.GetField("SourceTime").GetValue(destroy), Is.EqualTo(12.25), "Falling targets retain their original sampling time");
        }

        // The former streaming order is the regression oracle. Filtering uses
        // the public world bounds, independently of the optimized axis clipping.
        static IEnumerable<Vector3Int> LegacyCandidates(BlockWorld world, Vector3 point, Vector3Int n, int count, float radius)
        {
            var first = BlockWorld.CellAt(point + (Vector3)n * (BlockWorld.CellSize * .51f));
            var u = n.y != 0 ? Vector3Int.right : Vector3Int.up;
            var v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
            int span = Mathf.CeilToInt(Mathf.Max(Mathf.Max(world.Dimensions.x, world.Dimensions.y), BlockWorld.Top - world.Bottom) / BlockWorld.CellSize);
            int extent = Mathf.CeilToInt(Mathf.Min(radius / BlockWorld.CellSize, span));
            for (int layer = 0; layer < Math.Min(span, Math.Max(1, count)); layer++) for (int ring = 0; ring <= extent; ring++)
            {
                if (ring == 0) { yield return first + n * layer; continue; }
                for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    if ((x * x + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        yield return first + n * layer + u * x + v * (ring * side);
                for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    if ((y * y + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radius * radius + .00001f)
                        yield return first + n * layer + u * (ring * side) + v * y;
            }
        }

        [Test] public void CachedDisksAndAxisClippingPreserveEveryValidCandidateAndItsOrder()
        {
            using var historicalHeight = new HistoricalBlockWorldHeightScope();
            var world = new BlockWorld(new Vector2(2.3f, 3.7f), bottom: -.2f);
            var points = new[] { new Vector3(-1.15f, -.21f, 1.84f), new Vector3(1.2f, 31.91f, -1.9f), Vector3.zero };
            var radii = new[] { 0f, .2f, .200001f, 1.1023006f, 1.4452718f };
            foreach (var normal in Normals) foreach (var point in points) foreach (float radius in radii)
            {
                var expected = LegacyCandidates(world, point, normal, 30, radius).Where(world.InBounds).ToArray();
                var actual = Optimized(world, point, normal, 30, radius).Where(world.InBounds).ToArray();
                CollectionAssert.AreEqual(expected, actual, $"normal={normal}, point={point}, radius={radius:R}");
            }
        }

        [Test] public void DiskCacheKeepsFloatBoundaryCellsForAllSixDirections()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            foreach (int squared in new[] { 1, 2, 5, 16, 121, 208 })
            {
                float edge = Mathf.Sqrt(Mathf.Max(0, squared * BlockWorld.CellSize * BlockWorld.CellSize - .00001f));
                foreach (float radius in new[] { edge - .0000001f, edge, edge + .0000001f }) foreach (var normal in Normals)
                    CollectionAssert.AreEqual(LegacyCandidates(world, Vector3.up, normal, 1, radius).ToArray(),
                        Optimized(world, Vector3.up, normal, 1, radius).ToArray(), $"squared={squared}, radius={radius:R}, normal={normal}");
            }
            // Different requested radii may share a disk only when every lattice
            // membership test is identical. Verify the actual gameplay footprint.
            Assert.That(Optimized(world, Vector3.up, Vector3Int.up, 1, 1.4452718f).Count(), Is.EqualTo(665));
        }

        [Test] public void FiniteHugeRadiusCannotOverflowTheBoundedDiskCache()
        {
            using var historicalHeight = new HistoricalBlockWorldHeightScope();
            var world = new BlockWorld(Vector2.one, bottom: 31);
            Assert.That(Optimized(world, new Vector3(0, 31.1f, 0), Vector3Int.up, 1, float.MaxValue).Count(), Is.EqualTo(441));
        }

        [Test] public void PrimitivePositiveCacheInvalidatesTransformShapeLayerAndRegistration()
        {
            var cell = Vector3Int.zero;
            var box = new GameObject("registered map box").AddComponent<BoxCollider>();
            box.transform.position = BlockWorld.Center(cell); box.size = Vector3.one * .2f; StaticColliders.Add(box);
            Assert.That(Overlap(cell), Is.True); Assert.That(Cache("_staticBuildOverlaps").Count, Is.EqualTo(1));
            Assert.That(Overlap(cell), Is.True);
            box.transform.position += Vector3.right;
            Assert.That(Overlap(cell), Is.False); Assert.That(Cache("_staticBuildOverlaps").Count, Is.Zero);
            box.transform.position = BlockWorld.Center(cell); Assert.That(Overlap(cell), Is.True);
            box.center = Vector3.right; Assert.That(Overlap(cell), Is.False);
            box.center = Vector3.zero; Assert.That(Overlap(cell), Is.True);
            box.center = Vector3.right * .08f; Assert.That(Overlap(cell), Is.True);
            box.size = Vector3.one * .02f; Assert.That(Overlap(cell), Is.False);
            box.size = Vector3.one * .2f; Assert.That(Overlap(cell), Is.True);
            box.center = Vector3.zero; Assert.That(Overlap(cell), Is.True);
            box.gameObject.layer = 8; Assert.That(Overlap(cell), Is.False);
            box.gameObject.layer = 0; Assert.That(Overlap(cell), Is.True);
            StaticColliders.Remove(box); Assert.That(Overlap(cell), Is.False);
        }

        [Test] public void DisabledTriggersAndRigidbodyGeometryAreNeverRetainedAsStaticProof()
        {
            var cell = Vector3Int.zero;
            var box = new GameObject("changing map box").AddComponent<BoxCollider>();
            box.transform.position = BlockWorld.Center(cell); box.size = Vector3.one * .2f; StaticColliders.Add(box);
            Assert.That(Overlap(cell), Is.True);
            box.enabled = false; Assert.That(Overlap(cell), Is.False);
            box.enabled = true; Assert.That(Overlap(cell), Is.True);
            box.isTrigger = true; Assert.That(Overlap(cell), Is.False);
            box.isTrigger = false; Assert.That(Overlap(cell), Is.True);
            box.gameObject.AddComponent<Rigidbody>().isKinematic = true;
            Assert.That(Overlap(cell), Is.True); Assert.That(Cache("_staticBuildOverlaps").Count, Is.Zero);
            box.transform.position += Vector3.right; Assert.That(Overlap(cell), Is.False);
        }

        [Test] public void LiveProtectionColumnsDoNotBecomeStaticCacheEntries()
        {
            var cell = new Vector3Int(0, 100, 0);
            Assert.That(Overlap(cell), Is.False);
            var box = new GameObject("live protection zone").AddComponent<BoxCollider>(); box.isTrigger = true;
            box.size = new Vector3(.1f, 1, .1f); box.transform.position = new Vector3(.05f, .5f, .05f);
            var zone = box.gameObject.AddComponent<HeroChangeZone>();
            typeof(BlockTerrain).GetField("_constructionZones", Private).SetValue(_terrain, new[] { zone });
            Assert.That(Overlap(cell), Is.True); Assert.That(Cache("_staticBuildOverlaps").Count, Is.Zero);
            box.transform.position += Vector3.right;
            Assert.That(Overlap(cell), Is.False, "moving a live exclusion cannot leave a cached protected column");
        }

        HeroChangeZone ProtectionZone(Vector3 center, Vector3 size, float yaw = 0)
        {
            var box = new GameObject("whole footprint protection zone").AddComponent<BoxCollider>();
            box.isTrigger = true; box.size = size; box.transform.SetPositionAndRotation(center, Quaternion.Euler(0, yaw, 0));
            var zone = box.gameObject.AddComponent<HeroChangeZone>();
            typeof(BlockTerrain).GetField("_constructionZones", Private).SetValue(_terrain, new[] { zone });
            Physics.SyncTransforms(); return zone;
        }
        bool ProtectedProof(Vector3 point, Vector3Int normal, float radius)
        {
            var type = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var command = Activator.CreateInstance(type, true);
            type.GetField("Point").SetValue(command, point); type.GetField("Normal").SetValue(command, (Vector3)normal);
            type.GetField("Radius").SetValue(command, radius);
            return (bool)Call("FullyProtectedVerticalBuild", command);
        }
        object StageCommandsWithJournal(object[] arguments)
        {
            var journalField = typeof(BlockTerrain).GetField("_gravityJournal", Private);
            Assert.That(journalField.GetValue(_terrain), Is.Null, "Each oracle stage owns a fresh gravity journal");
            try
            {
                Call("StageCommands", arguments);
                return journalField.GetValue(_terrain);
            }
            finally { journalField.SetValue(_terrain, null); }
        }
        void PublishStagedWorld(BlockWorld next, object journal)
        {
            // Match Publish's World-before-journal order. Candidate-only stages
            // discard their captured journal without publishing its gravity work.
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, next);
            Call("CommitGravityJournal", journal);
        }
        (BlockWorld next, IList results, int checks, object journal) StageBuilds(int maxChecks)
        {
            var commandType = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var pairType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType));
            var next = _terrain.World.Clone();
            object[] arguments = { next, new HashSet<Vector3Int>(), results, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, maxChecks };
            var journal = StageCommandsWithJournal(arguments);
            return (next, results, (int)arguments[3], journal);
        }

        [TestCase(1, 0f, .31f)] [TestCase(-1, 0f, .31f)]
        [TestCase(1, 45f, .44f)] [TestCase(-1, 45f, .44f)]
        public void FourIntersectingCornerColumnsProveEveryLayerOfTheVerticalDisk(int normalY, float yaw, float width)
        {
            var point = new Vector3(.05f, normalY > 0 ? -.001f : 2, .05f);
            var normal = new Vector3Int(0, normalY, 0);
            var zone = ProtectionZone(new Vector3(.05f, 1, .05f), new Vector3(width, 2, width), yaw);
            Assert.That(zone.Contains(1, new Vector3(-.15f, 1, -.15f)), Is.False,
                "Corner cell centres can be outside: their complete columns still intersect the OBB");
            Assert.That(ProtectedProof(point, normal, .2f), Is.True);
            var candidates = LegacyCandidates(_terrain.World, point, normal, 22, .2f).Where(_terrain.World.InBounds).ToArray();
            Assert.That(candidates.Length, Is.GreaterThan(0));
            foreach (var cell in candidates) Assert.That((bool)Call("ExcludedConstruction", cell), Is.True, cell.ToString());
            int callbacks = 0;
            _terrain.QueueBuild(point, normal, 1, 22, .2f, false, result => { callbacks++; Assert.That(result.Changed, Is.Zero); });
            _terrain.Tick(true, 1, 1f / 60);
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(_terrain.CommandWorkChecks, Is.EqualTo(1));
            Assert.That(_terrain.Revision, Is.Zero); Assert.That(_terrain.PinkCount, Is.Zero);
        }

        [TestCase(0f)] [TestCase(45f)]
        public void APartlyProtectedNarrowDiskKeepsEveryValidCandidateAndTheFirstLegalBuild(float yaw)
        {
            ProtectionZone(new Vector3(.05f, 1, .05f), new Vector3(.1f, 2, .8f), yaw);
            var point = new Vector3(.05f, -.001f, .05f); var normal = Vector3Int.up; var world = _terrain.World;
            Assert.That(ProtectedProof(point, normal, .2f), Is.False);
            var all = LegacyCandidates(world, point, normal, 22, .2f).Where(world.InBounds).ToArray();
            var expected = all.Where(cell => !(bool)Call("ExcludedConstruction", cell)).ToArray();
            Assert.That(expected.Length, Is.InRange(1, all.Length - 1));
            CollectionAssert.AreEqual(expected, Optimized(world, point, normal, 22, .2f).Where(world.InBounds)
                .Where(cell => !(bool)Call("ExcludedConstruction", cell)).ToArray());
            _terrain.QueueBuild(point, normal, 1, 1, .2f, false);
            var staged = StageBuilds(4096);
            Assert.That(staged.next.PinkCount, Is.EqualTo(1)); Assert.That(staged.next.TryGet(expected[0], out _), Is.True);
        }

        [TestCase(1, 0)] [TestCase(-1, 0)] [TestCase(0, 1)] [TestCase(0, -1)]
        public void HorizontalNormalsAlwaysKeepTheOrdinaryPath(int normalX, int normalZ)
        {
            ProtectionZone(new Vector3(.05f, 1, .05f), Vector3.one * 6);
            Assert.That(ProtectedProof(Vector3.up, new Vector3Int(normalX, 0, normalZ), .2f), Is.False);
        }

        [Test] public void AMovedZoneCannotRejectTheNextCommandUsingAnEarlierProof()
        {
            var zone = ProtectionZone(new Vector3(.05f, 1, .05f), Vector3.one * 6);
            var point = new Vector3(.05f, -.001f, .05f); int callbacks = 0;
            _terrain.QueueBuild(point, Vector3.up, 1, 1, .2f, false, _ => callbacks++);
            _terrain.Tick(true, 1, 1f / 60); Assert.That(callbacks, Is.EqualTo(1));
            zone.transform.position += Vector3.right * 10; Physics.SyncTransforms();
            _terrain.QueueBuild(point, Vector3.up, 1, 1, .2f, false);
            Assert.That(ProtectedProof(point, Vector3Int.up, .2f), Is.False);
            Assert.That(StageBuilds(4096).next.PinkCount, Is.EqualTo(1));
        }

        [TestCase("component")] [TestCase("volume")] [TestCase("trigger")] [TestCase("active")]
        public void TheWholeFootprintProofReadsLiveZoneEligibility(string change)
        {
            var zone = ProtectionZone(new Vector3(.05f, 1, .05f), Vector3.one * 6);
            var point = new Vector3(.05f, -.001f, .05f);
            Assert.That(ProtectedProof(point, Vector3Int.up, .2f), Is.True);
            if (change == "component") zone.enabled = false;
            else if (change == "volume") zone.Volume.enabled = false;
            else if (change == "trigger") zone.Volume.isTrigger = false;
            else zone.gameObject.SetActive(false);
            Assert.That(ProtectedProof(point, Vector3Int.up, .2f), Is.False);
        }

        [Test] public void RefusedBuildRefundsItsPaymentWithoutChargingPointsOrRepeatingTheCallback()
        {
            HeroMigrationTests.Load();
            try
            {
                ProtectionZone(new Vector3(.05f, 1, .05f), Vector3.one * 6.4f);
                var token = new BlockAttackToken(new PaintCredit(0, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, 29, 8, 2);
                int reserved = token.ReserveBuild(29, false), callbacks = 0;
                _terrain.QueueBuild(new Vector3(.05f, -.001f, .05f), Vector3.up, 1, reserved, 1.4452718f, false,
                    result => { callbacks++; Assert.That(result.OrdinaryMainAttack, Is.True); token.CompleteBuild(reserved, false, result.Added); },
                    ordinaryMainAttack: true);
                _terrain.Tick(true, 1, 1f / 60); _terrain.Tick(true, 2, 1f / 60);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(token.BuildRemaining, Is.EqualTo(29));
                Assert.That(token.Added, Is.Zero); Assert.That(token.TakePoints(), Is.Zero); Assert.That(token.AwardedPoints, Is.Zero);
                Assert.That(_terrain.CommandWorkChecks, Is.EqualTo(1)); Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(_terrain.Revision, Is.Zero);
            }
            finally { LubanConfigService.Current.Reset(); }
        }

        [Test] public void RefusedCommandsStillConsumeTheStageCheckBudgetAndCompleteOnce()
        {
            ProtectionZone(new Vector3(.05f, 1, .05f), Vector3.one * 6.4f); int callbacks = 0;
            for (int i = 0; i < 10; i++) _terrain.QueueBuild(new Vector3(.05f, -.001f, .05f), Vector3.up, 1, 22, 1.4452718f, false,
                result => { callbacks++; Assert.That(result.Changed, Is.Zero); });
            var staged = StageBuilds(3);
            Assert.That(staged.checks, Is.EqualTo(3)); Assert.That(staged.results.Count, Is.EqualTo(3));
            Assert.That(_terrain.CommandWorkChecks, Is.EqualTo(3)); Assert.That(_terrain.PendingTransactions, Is.EqualTo(7));
            Assert.That(callbacks, Is.Zero, "Callbacks remain outside terrain staging");
            foreach (var pair in staged.results)
            {
                var command = pair.GetType().GetField("Item1").GetValue(pair);
                var result = (BlockMutationResult)pair.GetType().GetField("Item2").GetValue(pair);
                Assert.That(result.Changed, Is.Zero);
                Call("Complete", command, result); Call("Complete", command, result);
            }
            Assert.That(callbacks, Is.EqualTo(3));
            for (int i = 0; i < 20 && _terrain.PendingTransactions != 0; i++) _terrain.Tick(true, 1 + i / 60d, 1f / 60);
            Assert.That(callbacks, Is.EqualTo(10)); Assert.That(_terrain.CommandWorkChecks, Is.EqualTo(10));
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(staged.next.PinkCount, Is.Zero);
        }

        [Test] public void QueryBufferOverflowDoesNotLeaveAFalseStaticProof()
        {
            var boxes = new List<BoxCollider>();
            for (int i = 0; i < 65; i++)
            {
                var box = new GameObject("unregistered overlapping collider " + i).AddComponent<BoxCollider>();
                box.transform.position = BlockWorld.Center(Vector3Int.zero); box.size = Vector3.one * .2f; boxes.Add(box);
            }
            Assert.That(Overlap(Vector3Int.zero), Is.True); Assert.That(Cache("_staticBuildOverlaps").Count, Is.Zero);
            foreach (var box in boxes) Object.DestroyImmediate(box.gameObject);
            Assert.That(Overlap(Vector3Int.zero), Is.False);
        }

        [Test] public void ExplicitMapLifecycleResetReleasesBothCaches()
        {
            var world = new BlockWorld(Vector2.one);
            Optimized(world, Vector3.up, Vector3Int.up, 1, .2f).ToArray();
            var box = new GameObject("map cache lifecycle").AddComponent<BoxCollider>();
            box.transform.position = BlockWorld.Center(Vector3Int.zero); StaticColliders.Add(box);
            Assert.That(Overlap(Vector3Int.zero), Is.True);
            Assert.That(Cache("_buildDisks").Count, Is.GreaterThan(0)); Assert.That(Cache("_staticBuildOverlaps").Count, Is.GreaterThan(0));
            Call("ResetBuildQueriesCache");
            Assert.That(Cache("_buildDisks").Count, Is.Zero); Assert.That(Cache("_staticBuildOverlaps").Count, Is.Zero);
        }
        IDictionary ZoneColumnCache => Cache("_constructionZoneColumnCache");
        void EndZoneStage()
        {
            // The production StageCommands finally performs exactly these writes.
            typeof(BlockTerrain).GetField("_stageConstructionZonesActive", Private).SetValue(_terrain, false);
            typeof(BlockTerrain).GetField("_stageConstructionZoneCount", Private).SetValue(_terrain, 0);
        }
        [TestCase(1)] [TestCase(3)]
        public void ProductionVerticalCandidatesReuseZoneColumnsWithinAndAcrossActualStages(int maxChecks)
        {
            ProtectionZone(new Vector3(.05f, .5f, .05f), new Vector3(.1f, 1, .1f));
            var point = new Vector3(.05f, -.001f, .05f); var hits = new List<bool>();
            _terrain.QueueBuild(point, Vector3.up, 1, 3, 0, false);
            var command = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().Single();
            IEnumerable<Vector3Int> Observe()
            {
                foreach (var cell in Optimized(_terrain.World, point, Vector3Int.up, 3, 0))
                {
                    hits.Add(ZoneColumnCache.Contains(new Vector2Int(cell.x, cell.z)));
                    yield return cell;
                }
            }
            // Supply the actual production stream so the whole-command proof does
            // not preempt the suspended-cell path this fixture is exercising.
            command.GetType().GetField("Cells").SetValue(command, Observe().GetEnumerator());
            int stages = 0, checks = 0;
            while (!(bool)command.GetType().GetField("Done").GetValue(command) && stages++ < 8)
            {
                var stage = StageBuilds(maxChecks); checks += stage.checks;
                Assert.That(stage.checks, Is.LessThanOrEqualTo(maxChecks));
                Assert.That(stage.next.PinkCount, Is.Zero);
                Assert.That((bool)typeof(BlockTerrain).GetField("_stageConstructionZonesActive", Private).GetValue(_terrain), Is.False);
            }
            CollectionAssert.AreEqual(new[] { false, true, true }, hits);
            Assert.That((bool)command.GetType().GetField("Done").GetValue(command), Is.True);
            Assert.That(checks, Is.EqualTo(4)); Assert.That(ZoneColumnCache.Count, Is.EqualTo(1));
            Assert.That(_terrain.BuildProtectedByZone, Is.EqualTo(3));
        }
        [TestCase("move")] [TestCase("tinyMove")] [TestCase("size")] [TestCase("scale")]
        [TestCase("tilt")] [TestCase("center")] [TestCase("component")] [TestCase("volume")]
        [TestCase("trigger")] [TestCase("active")]
        public void ChangedEffectiveZoneGeometryInvalidatesBothPositiveAndNegativeColumns(string change)
        {
            var zone = ProtectionZone(new Vector3(.05f, .5f, .05f), new Vector3(.1f, 1, .1f));
            var cells = new[] { new Vector3Int(0, 100, 0), new Vector3Int(10, 100, 0), new Vector3Int(3, 100, 3) };
            Call("CaptureStageConstructionZones");
            Assert.That((bool)Call("ExcludedConstruction", cells[0]), Is.True);
            Assert.That((bool)Call("ExcludedConstruction", cells[1]), Is.False);
            Assert.That(ZoneColumnCache.Count, Is.EqualTo(2)); EndZoneStage();
            if (change == "move") zone.transform.position += Vector3.right;
            else if (change == "tinyMove") zone.transform.position += Vector3.right * .000001f;
            else if (change == "size") zone.Volume.size += Vector3.one;
            else if (change == "scale") zone.transform.localScale = new Vector3(-2, 3, .5f);
            else if (change == "tilt") zone.transform.rotation = Quaternion.Euler(20, 35, 10);
            else if (change == "center") zone.Volume.center += Vector3.right;
            else if (change == "component") zone.enabled = false;
            else if (change == "volume") zone.Volume.enabled = false;
            else if (change == "trigger") zone.Volume.isTrigger = false;
            else zone.gameObject.SetActive(false);
            Physics.SyncTransforms();
            // This oracle uses the existing live path, including its full SAT.
            var expected = cells.Select(cell => (bool)Call("ExcludedConstruction", cell)).ToArray();
            Assert.That(ZoneColumnCache.Count, Is.EqualTo(2), "Outside-stage queries must bypass this cache");
            Call("CaptureStageConstructionZones");
            try
            {
                Assert.That(ZoneColumnCache.Count, Is.Zero, "Any effective geometry change invalidates both answers");
                CollectionAssert.AreEqual(expected, cells.Select(cell => (bool)Call("ExcludedConstruction", cell)).ToArray());
            }
            finally { EndZoneStage(); }
        }
        [TestCase(true)] [TestCase(false)]
        public void SuspendedVerticalBuildUsesRecapturedProtectionAfterTheZoneMoves(bool protectedFirst)
        {
            var zone = ProtectionZone(new Vector3(protectedFirst ? .05f : 10.05f, .5f, .05f), new Vector3(.1f, 1, .1f));
            var point = new Vector3(.05f, -.001f, .05f); var hits = new List<bool>();
            _terrain.QueueBuild(point, Vector3.up, 1, 2, 0, false);
            var command = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().Single();
            IEnumerable<Vector3Int> Observe()
            {
                foreach (var cell in Optimized(_terrain.World, point, Vector3Int.up, 2, 0))
                {
                    hits.Add(ZoneColumnCache.Contains(new Vector2Int(cell.x, cell.z))); yield return cell;
                }
            }
            command.GetType().GetField("Cells").SetValue(command, Observe().GetEnumerator());
            var first = StageBuilds(1); Assert.That(first.next.PinkCount, Is.EqualTo(protectedFirst ? 0 : 1));
            var total = new BlockMutationResult { Added = protectedFirst ? 0 : 1 };
            command.GetType().GetField("Total").SetValue(command, total);
            PublishStagedWorld(first.next, first.journal);
            zone.transform.position = new Vector3(protectedFirst ? 10.05f : .05f, .5f, .05f); Physics.SyncTransforms();
            var second = StageBuilds(1);
            CollectionAssert.AreEqual(new[] { false, false }, hits);
            Assert.That(second.next.PinkCount, Is.EqualTo(1));
            Assert.That(second.next.TryGet(new Vector3Int(0, 1, 0), out _), Is.EqualTo(protectedFirst));
            Assert.That(second.checks, Is.EqualTo(1));
        }
        [Test] public void BoundsAndSafetyLifecycleInvalidateTheZoneColumnSnapshot()
        {
            ProtectionZone(new Vector3(.05f, .5f, .05f), new Vector3(.1f, 1, .1f));
            Call("CaptureStageConstructionZones"); Call("ExcludedConstruction", new Vector3Int(0, 100, 0)); EndZoneStage();
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain,
                new BlockWorld(_terrain.World.Dimensions, _terrain.World.Round, bottom: 2));
            Call("CaptureStageConstructionZones");
            Assert.That(ZoneColumnCache.Count, Is.Zero);
            Assert.That((bool)Call("ExcludedConstruction", new Vector3Int(0, 100, 0)), Is.False);
            EndZoneStage(); Call("ClearSafetyState"); Assert.That(ZoneColumnCache.Count, Is.Zero);
            Assert.That((bool)typeof(BlockTerrain).GetField("_constructionZoneColumnCacheValid", Private).GetValue(_terrain), Is.False);
        }
        [Test] public void ZoneColumnCacheAtCapacityRetainsTheOrdinaryQueryForUncachedColumns()
        {
            ProtectionZone(new Vector3(.05f, .5f, .05f), new Vector3(.1f, 1, .1f));
            Call("CaptureStageConstructionZones");
            try
            {
                // Seed valid outside-zone answers instead of running a large SAT sweep.
                for (int x = 10; x < 4105; x++) ZoneColumnCache.Add(new Vector2Int(x, 0), false);
                Assert.That((bool)Call("ExcludedConstruction", new Vector3Int(4105, 100, 0)), Is.False);
                Assert.That(ZoneColumnCache.Count, Is.EqualTo(4096));
                Assert.That((bool)Call("ExcludedConstruction", new Vector3Int(-1, 100, 0)), Is.True,
                    "The full adjacent cube touches the zone even when its column cannot be added to the cache");
                Assert.That(ZoneColumnCache.Contains(new Vector2Int(-1, 0)), Is.False);
                Assert.That((bool)Call("ExcludedConstruction", new Vector3Int(-20, 100, 0)), Is.False);
                Assert.That(ZoneColumnCache.Count, Is.EqualTo(4096));
            }
            finally { EndZoneStage(); }
        }
        [Test] public void NegativeZoneColumnCacheStillChecksChangedStaticGeometry()
        {
            var cell = new Vector3Int(0, 10, 0); Call("CaptureStageConstructionZones");
            try
            {
                Assert.That(Overlap(cell), Is.False);
                var box = new GameObject("Live static geometry after negative zone cache").AddComponent<BoxCollider>();
                box.size = Vector3.one * .1f; box.transform.position = BlockWorld.Center(cell); StaticColliders.Add(box);
                Assert.That(Overlap(cell), Is.True);
                box.transform.position += Vector3.right * 10; Assert.That(Overlap(cell), Is.False);
                Assert.That((bool)ZoneColumnCache[new Vector2Int(cell.x, cell.z)], Is.False);
            }
            finally { EndZoneStage(); }
        }

        readonly struct HorizontalZoneSpec
        {
            public readonly Vector3 Center, Size, Scale;
            public readonly Quaternion Rotation;
            public HorizontalZoneSpec(Vector3 center, Vector3 size, float yaw = 0, Vector3? scale = null)
            { Center = center; Size = size; Rotation = Quaternion.Euler(0, yaw, 0); Scale = scale ?? Vector3.one; }
        }
        sealed class HorizontalReplay
        {
            public byte[] World;
            public int Added, Callbacks, Remaining;
            public double Points;
            public long Visited, Proofs, Layers, Tails, VerticalProofs, VerticalSkipped;
            public int RetryVisited, RetrySpans, SecondRetryVisited, SecondRetrySpans;
            public bool RetryTerminal, SecondRetryTerminal;
            public Vector3Int FirstCandidate;
        }
        void ResetOracleCommandStage()
        {
            // Match StageAuthority's fresh per-stage checkpoint ownership. Do not
            // reset the paid command, cursor, totals, published world or caches.
            foreach (var field in new[] { "_stageCommandCheckpoints", "_stageCommandCheckpoint", "_stageAddedBuildSlots", "_stageMovingBuildColumns" })
                typeof(BlockTerrain).GetField(field, Private).SetValue(_terrain, null);
            typeof(BlockTerrain).GetField("_stageHasMovingBuild", Private).SetValue(_terrain, false);
        }
        HorizontalReplay ReplayHorizontalBuild(bool legacy, Vector3 point, Vector3Int normal, int count, float radius,
            HorizontalZoneSpec[] specifications, Action<BlockWorld> seed = null,
            Action<int, HeroChangeZone[]> change = null, int maxChecks = 4096,
            int consumedProtectedPrefix = 0, bool retryFirstStage = false, bool movingBudgetRetry = false, bool retryTwice = false)
        {
            // Independent old stream, same real construction/PhysX/credit code.
            // World installation below models successful stage publication; this
            // oracle does not pretend to verify asynchronous mesh publication.
            if (_terrain != null) Object.DestroyImmediate(_terrain.gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrain = new GameObject("Horizontal full-stream oracle").AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var zones = specifications.Select(spec =>
            {
                var zone = new GameObject("Horizontal oracle live zone").AddComponent<HeroChangeZone>();
                zone.transform.SetPositionAndRotation(spec.Center, spec.Rotation); zone.transform.localScale = spec.Scale;
                zone.Volume.isTrigger = true; zone.Volume.size = spec.Size; return zone;
            }).ToArray();
            typeof(BlockTerrain).GetField("_constructionZones", Private).SetValue(_terrain, zones);
            seed?.Invoke(_terrain.World); Physics.SyncTransforms(); HeroMigrationTests.Load();
            try
            {
                var credit = new BlockAttackToken(new PaintCredit(0, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, count, 8, 2);
                int reserved = credit.ReserveBuild(count, false), callbacks = 0;
                BlockMutationResult delivered = default;
                _terrain.QueueBuild(point, normal, 1, reserved, radius, false,
                    result => { callbacks++; delivered = result; credit.CompleteBuild(reserved, false, result.Added); }, ordinaryMainAttack: true);
                var command = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().Single();
                var commandType = command.GetType(); var totalField = commandType.GetField("Total");
                if (legacy || consumedProtectedPrefix > 0)
                {
                    var stream = (legacy ? LegacyCandidates(_terrain.World, point, normal, count, radius)
                        : (IEnumerable<Vector3Int>)Call("OrderedBuildCandidates", command)).GetEnumerator();
                    // Resume after an independently selected already-rejected prefix;
                    // both oracles retain the identical original suffix, never restart it.
                    for (int i = 0; i < consumedProtectedPrefix; i++)
                    {
                        Assert.That(stream.MoveNext(), Is.True);
                        Assert.That((bool)Call("ExcludedConstruction", stream.Current), Is.True);
                    }
                    commandType.GetField("Cells").SetValue(command, stream);
                }
                if (movingBudgetRetry)
                {
                    var deferred = LegacyCandidates(_terrain.World, point, normal, count, radius)
                        .Skip(consumedProtectedPrefix).First(cell => _terrain.World.InBounds(cell) && !(bool)Call("ExcludedConstruction", cell));
                    InstallEmptySourceColumnBudgetFixture(deferred, maxChecks);
                }
                var record = new HorizontalReplay(); int stage = 0;
                while (!(bool)commandType.GetField("Done").GetValue(command) && stage < 2048)
                {
                    change?.Invoke(stage, zones); Physics.SyncTransforms();
                    ResetOracleCommandStage();
                    long proofsBefore = _terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs;
                    var staged = StageBuilds(maxChecks);
                    Assert.That(staged.checks, Is.LessThanOrEqualTo(maxChecks), "Do not relax the logical check ceiling");
                    Assert.That(_terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs - proofsBefore, Is.LessThanOrEqualTo(2L * staged.checks),
                        "One MoveNext must not hide more than two proof attempts inside one original work check");
                    Assert.That((bool)typeof(BlockTerrain).GetField("_stageConstructionZonesActive", Private).GetValue(_terrain), Is.False);
                    if (retryFirstStage && stage == 0)
                    {
                        var checkpoint = typeof(BlockTerrain).GetField("_stageCommandCheckpoint", Private).GetValue(_terrain);
                        var checkpointType = checkpoint.GetType();
                        record.RetryVisited = ((ICollection)checkpointType.GetField("Visited").GetValue(checkpoint)).Count;
                        record.RetrySpans = ((ICollection)checkpointType.GetField("ProtectedBuildSpans").GetValue(checkpoint)).Count;
                        record.RetryTerminal = (bool)commandType.GetField("Done").GetValue(command);
                        Assert.That(record.RetryTerminal, Is.EqualTo(!movingBudgetRetry));
                        if (!legacy) Assert.That(record.RetrySpans, Is.GreaterThan(0), "This retry must exercise a real recorded positive prefix");
                        if (movingBudgetRetry)
                            Assert.That(_terrain.BuildDeferredByChunkBudget, Is.Zero, "The intended remove-last/prepend came from the actual moving-query budget branch");
                        var beforeRetry = _terrain.World.EncodeSnapshot();
                        Call("RestoreCheckpoint", checkpoint);
                        Assert.That((bool)commandType.GetField("Done").GetValue(command), Is.False);
                        ResetOracleCommandStage();
                        // Reject the unpublished draft, then change live protection
                        // before the same paid command's ordinary retry.
                        foreach (var zone in zones) zone.enabled = false;
                        Physics.SyncTransforms();
                        long retryProofs = _terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs;
                        staged = StageBuilds(maxChecks + BlockWorld.MaxCohortCells + 2);
                        Assert.That(staged.checks, Is.LessThanOrEqualTo(maxChecks + BlockWorld.MaxCohortCells + 2));
                        Assert.That(_terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs - retryProofs,
                            Is.LessThanOrEqualTo(2L * staged.checks));
                        CollectionAssert.AreEqual(beforeRetry, _terrain.World.EncodeSnapshot(), "Neither rejected draft nor retry staging installs a world early");
                        if (retryTwice)
                        {
                            var second = typeof(BlockTerrain).GetField("_stageCommandCheckpoint", Private).GetValue(_terrain);
                            Assert.That(second, Is.Not.SameAs(checkpoint), "A later real stage owns a new checkpoint");
                            var secondType = second.GetType();
                            record.SecondRetryVisited = ((ICollection)secondType.GetField("Visited").GetValue(second)).Count;
                            record.SecondRetrySpans = ((ICollection)secondType.GetField("ProtectedBuildSpans").GetValue(second)).Count;
                            record.SecondRetryTerminal = (bool)commandType.GetField("Done").GetValue(command);
                            Assert.That(record.SecondRetryTerminal, Is.True);
                            Call("RestoreCheckpoint", second); ResetOracleCommandStage();
                            // A second rejected draft must not publish or credit its
                            // first retry. Recapture another live protection geometry.
                            foreach (var zone in zones) { zone.enabled = true; zone.transform.position += Vector3.right * 10; }
                            Physics.SyncTransforms();
                            long secondProofs = _terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs;
                            staged = StageBuilds(maxChecks + BlockWorld.MaxCohortCells + 2);
                            Assert.That(staged.checks, Is.LessThanOrEqualTo(maxChecks + BlockWorld.MaxCohortCells + 2));
                            Assert.That(_terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs - secondProofs,
                                Is.LessThanOrEqualTo(2L * staged.checks));
                            CollectionAssert.AreEqual(beforeRetry, _terrain.World.EncodeSnapshot(), "The second rejected draft must not escape its private stage");
                        }
                    }
                    foreach (var pair in staged.results)
                    {
                        var mutation = (BlockMutationResult)pair.GetType().GetField("Item2").GetValue(pair);
                        var total = (BlockMutationResult)totalField.GetValue(command);
                        total.Added += mutation.Added; total.RemovedEnemy += mutation.RemovedEnemy; total.RemovedFriendly += mutation.RemovedFriendly;
                        totalField.SetValue(command, total);
                    }
                    if (stage == 0 && (bool)commandType.GetField("HasCandidate").GetValue(command))
                        record.FirstCandidate = (Vector3Int)commandType.GetField("LastCandidate").GetValue(command);
                    PublishStagedWorld(staged.next, staged.journal);
                    Assert.That(callbacks, Is.Zero, "A staging oracle must not invoke result callbacks early");
                    stage++;
                }
                Assert.That((bool)commandType.GetField("Done").GetValue(command), Is.True, "Finite full candidate stream must drain");
                var final = (BlockMutationResult)totalField.GetValue(command);
                Call("Complete", command, final); Call("Complete", command, final);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(delivered.OrdinaryMainAttack, Is.True);
                record.World = _terrain.World.EncodeSnapshot(); record.Added = delivered.Added;
                record.Callbacks = callbacks; record.Remaining = credit.BuildRemaining; record.Points = credit.TakePoints();
                record.Visited = _terrain.BuildCandidatesVisited; record.Proofs = _terrain.BuildHorizontalProofAttempts;
                record.Layers = _terrain.BuildHorizontalProtectedLayers; record.Tails = _terrain.BuildHorizontalProtectedTails;
                record.VerticalProofs = _terrain.BuildVerticalProtectedPrefixProofs; record.VerticalSkipped = _terrain.BuildVerticalProtectedCandidatesSkipped;
                Assert.That(_terrain.CommandsCompleted, Is.EqualTo(1)); Assert.That(_terrain.PendingTransactions, Is.Zero);
                return record;
            }
            finally { LubanConfigService.Current.Reset(); }
        }
        static void SameHorizontalState(HorizontalReplay expected, HorizontalReplay actual)
        {
            CollectionAssert.AreEqual(expected.World, actual.World, "All encoded cell/run/count/round state, not only totals");
            Assert.That(actual.Added, Is.EqualTo(expected.Added)); Assert.That(actual.Remaining, Is.EqualTo(expected.Remaining));
            Assert.That(actual.Points, Is.EqualTo(expected.Points)); Assert.That(actual.Callbacks, Is.EqualTo(expected.Callbacks));
        }
        // This fixture supplies a semantically empty, source-bound full-column query
        // to exercise the real TryCurrentBuildOverlap admission and RemoveLast+Prepend.
        // Formation of the frozen cache remains covered by the existing shallow-column
        // publication tests; this helper does not claim to test that formation.
        void InstallEmptySourceColumnBudgetFixture(Vector3Int cell, int maximum)
        {
            Assert.That(_terrain.World.Falling.Count, Is.Zero); Assert.That(_terrain.World.Cohorts.Count, Is.Zero);
            var terrainType = typeof(BlockTerrain);
            var cacheType = terrainType.GetNestedType("BuildSourceQueryCache", BindingFlags.NonPublic);
            var columnType = terrainType.GetNestedType("BuildColumnQuery", BindingFlags.NonPublic);
            var cache = Activator.CreateInstance(cacheType, true); var column = Activator.CreateInstance(columnType, true);
            cacheType.GetField("Source").SetValue(cache, _terrain.World);
            cacheType.GetField("Epoch").SetValue(cache, terrainType.GetField("_epoch", Private).GetValue(_terrain));
            cacheType.GetField("Revision").SetValue(cache, _terrain.Revision); cacheType.GetField("Round").SetValue(cache, _terrain.Round);
            cacheType.GetField("Mutation").SetValue(cache, _terrain.World.MutationGeneration);
            int minimum = Mathf.CeilToInt(_terrain.World.Bottom / BlockWorld.CellSize);
            int length = Mathf.CeilToInt(BlockWorld.Top / BlockWorld.CellSize) - minimum;
            Assert.That(maximum, Is.EqualTo(length), "One original cell check leaves insufficient budget for this complete column query");
            columnType.GetField("Minimum").SetValue(column, minimum); columnType.GetField("Length").SetValue(column, length);
            columnType.GetField("QueryWork").SetValue(column, length); columnType.GetField("Moving").SetValue(column, new bool[length]);
            ((IDictionary)cacheType.GetField("Columns").GetValue(cache)).Add(new Vector2Int(cell.x, cell.z), column);
            terrainType.GetField("_buildSourceQueryCache", Private).SetValue(_terrain, cache);
        }

        [TestCase(1, 0f, 1f)] [TestCase(-1, 0f, 1f)]
        [TestCase(1, 37f, -1f)] [TestCase(-1, 37f, -1f)]
        public void PartialVerticalPrefixesKeepCrossChunkFullStreamStateAndPaidResults(int normalY, float yaw, float scaleX)
        {
            var point = new Vector3(1.55f, 3.25f, 1.55f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(1.55f, 1, 1.55f), new Vector3(.5f, 2, .5f), yaw, new Vector3(scaleX * 1.25f, 1, .8f)) };
            void Seed(BlockWorld world)
            {
                var cells = LegacyCandidates(world, point, normal, 7, .4f).Where(world.InBounds).ToArray();
                Assert.That(cells.Select(BlockWorld.ChunkAt).Distinct().Count(), Is.GreaterThan(1));
                var legal = cells.Where(cell => !(bool)Call("ExcludedConstruction", cell)).ToArray();
                Assert.That(legal.Length, Is.InRange(4, cells.Length - 1));
                foreach (var cell in legal.Skip(3)) world.Set(cell, BlockCell.Create(2, true));
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 7, .4f, zones, Seed, maxChecks: 3);
            var actual = ReplayHorizontalBuild(false, point, normal, 7, .4f, zones, Seed, maxChecks: 3);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(3)); Assert.That(actual.Remaining, Is.EqualTo(4));
            Assert.That(actual.VerticalSkipped, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }

        [TestCase(1)] [TestCase(-1)]
        public void VerticalPrefixCannotCombineDifferentCornerZonesAcrossALegalHole(int normalY)
        {
            var point = new Vector3(.45f, 3.25f, .45f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[]
            {
                new HorizontalZoneSpec(new Vector3(.25f, 1, .25f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(.65f, 1, .25f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(.25f, 1, .65f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(.65f, 1, .65f), new Vector3(.02f, 2, .02f))
            };
            void Seed(BlockWorld world)
            {
                var first = BlockWorld.CellAt(point + (Vector3)normal * (BlockWorld.CellSize * .51f));
                var hole = new Vector3Int(first.x, first.y, first.z - 2);
                Assert.That((bool)Call("ExcludedConstruction", hole), Is.False);
                foreach (var cell in LegacyCandidates(world, point, normal, 4, .4f).Where(world.InBounds))
                    if (cell != hole && !(bool)Call("ExcludedConstruction", cell)) world.Set(cell, BlockCell.Create(2, true));
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 4, .4f, zones, Seed, maxChecks: 2);
            var actual = ReplayHorizontalBuild(false, point, normal, 4, .4f, zones, Seed, maxChecks: 2);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(1)); Assert.That(actual.VerticalProofs, Is.GreaterThan(0));
        }

        [TestCase(1, "move")] [TestCase(-1, "move")] [TestCase(1, "component")]
        [TestCase(-1, "volume")] [TestCase(1, "trigger")] [TestCase(-1, "active")]
        public void ResumedVerticalPrefixReadsNextStageLiveProtection(int normalY, string change)
        {
            var point = new Vector3(.05f, 3.25f, .05f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.05f, 1, .05f), new Vector3(.1f, 2, .5f)) };
            void Change(int stage, HeroChangeZone[] live)
            {
                if (stage != 1) return;
                if (change == "move") live[0].transform.position += Vector3.right * 10;
                else if (change == "component") live[0].enabled = false;
                else if (change == "volume") live[0].Volume.enabled = false;
                else if (change == "trigger") live[0].Volume.isTrigger = false;
                else live[0].gameObject.SetActive(false);
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 8, .2f, zones, change: Change, maxChecks: 1);
            var actual = ReplayHorizontalBuild(false, point, normal, 8, .2f, zones, change: Change, maxChecks: 1);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(8));
        }

        [TestCase(1)] [TestCase(-1)]
        public void ASpanOnlyFinishedCheckpointReplaysItsPaidSuffixAfterPublicationRejection(int normalY)
        {
            var point = new Vector3(.05f, 3.25f, .05f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.05f, 1, .05f), Vector3.one * 4) };
            int consumed = LegacyCandidates(_terrain.World, point, normal, 1, .1f).Count() - 1;
            var expected = ReplayHorizontalBuild(true, point, normal, 1, .1f, zones, maxChecks: 8,
                consumedProtectedPrefix: consumed, retryFirstStage: true);
            var actual = ReplayHorizontalBuild(false, point, normal, 1, .1f, zones, maxChecks: 8,
                consumedProtectedPrefix: consumed, retryFirstStage: true);
            SameHorizontalState(expected, actual); Assert.That(actual.RetryVisited, Is.Zero); Assert.That(actual.RetrySpans, Is.GreaterThan(0));
            Assert.That(actual.RetryTerminal, Is.True); Assert.That(actual.Added, Is.EqualTo(1)); Assert.That(actual.Remaining, Is.Zero);
        }

        [TestCase(1)] [TestCase(-1)]
        public void TwoRejectedStagesKeepFreshCheckpointsAndTheSamePaidOriginalSuffix(int normalY)
        {
            var point = new Vector3(.05f, 3.25f, .05f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.05f, 1, .05f), Vector3.one * 4) };
            int consumed = LegacyCandidates(_terrain.World, point, normal, 1, .1f).Count() - 1;
            var expected = ReplayHorizontalBuild(true, point, normal, 1, .1f, zones, maxChecks: 8,
                consumedProtectedPrefix: consumed, retryFirstStage: true, retryTwice: true);
            var actual = ReplayHorizontalBuild(false, point, normal, 1, .1f, zones, maxChecks: 8,
                consumedProtectedPrefix: consumed, retryFirstStage: true, retryTwice: true);
            SameHorizontalState(expected, actual);
            Assert.That(actual.RetryVisited, Is.Zero); Assert.That(actual.RetrySpans, Is.GreaterThan(0));
            Assert.That(actual.SecondRetryVisited, Is.EqualTo(1)); Assert.That(actual.SecondRetrySpans, Is.Zero);
            Assert.That(actual.SecondRetryTerminal, Is.True); Assert.That(actual.Added, Is.EqualTo(1));
            Assert.That(actual.Callbacks, Is.EqualTo(1)); Assert.That(actual.Remaining, Is.Zero);
        }

        [TestCase(1)] [TestCase(-1)]
        public void ProtectedPrefixStaysBeforeAMovingBudgetDeferredPrependOnRetry(int normalY)
        {
            var point = new Vector3(.05f, 3.25f, .05f); var normal = new Vector3Int(0, normalY, 0);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.05f, 1, .05f), new Vector3(.02f, 2, .3f)) };
            var expected = ReplayHorizontalBuild(true, point, normal, 1, .1f, zones, maxChecks: BlockWorld.MaxCohortCells,
                consumedProtectedPrefix: 1, retryFirstStage: true, movingBudgetRetry: true);
            var actual = ReplayHorizontalBuild(false, point, normal, 1, .1f, zones, maxChecks: BlockWorld.MaxCohortCells,
                consumedProtectedPrefix: 1, retryFirstStage: true, movingBudgetRetry: true);
            SameHorizontalState(expected, actual); Assert.That(actual.RetryVisited, Is.Zero); Assert.That(actual.RetrySpans, Is.GreaterThan(0));
            Assert.That(actual.RetryTerminal, Is.False); Assert.That(actual.Added, Is.EqualTo(1)); Assert.That(actual.Remaining, Is.Zero);
        }

        [TestCase(1, 0)] [TestCase(-1, 0)] [TestCase(0, 1)] [TestCase(0, -1)]
        public void HorizontalProtectedTailMatchesIndependentFullStreamAndRefundsExactlyOnce(int x, int z)
        {
            var normal = new Vector3Int(x, 0, z); var point = Vector3.up;
            var zones = new[] { new HorizontalZoneSpec(Vector3.up, Vector3.one * 8) };
            var expected = ReplayHorizontalBuild(true, point, normal, 29, 1.4452718f, zones);
            var actual = ReplayHorizontalBuild(false, point, normal, 29, 1.4452718f, zones);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.Zero); Assert.That(actual.Remaining, Is.EqualTo(29));
            Assert.That(actual.Points, Is.Zero); Assert.That(actual.Tails, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }
        [TestCase(1)] [TestCase(-1)]
        public void RealWallPointLargeDiskKeepsSeventeenBuildsBeforeProtectedTail(int direction)
        {
            var point = new Vector3(2.95f, 1.35f, direction * 24.8f); var normal = new Vector3Int(0, 0, direction);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(0, 2, direction * 28), new Vector3(16, 4, 6)) };
            void Seed(BlockWorld world)
            {
                var legal = LegacyCandidates(world, point, normal, 29, 1.4452718f).Where(world.InBounds)
                    .Where(cell => !(bool)Call("ExcludedConstruction", cell)).ToArray();
                Assert.That(legal.Length, Is.GreaterThan(17));
                foreach (var cell in legal.Skip(17)) world.Set(cell, BlockCell.Create(2, true));
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 29, 1.4452718f, zones, Seed);
            var actual = ReplayHorizontalBuild(false, point, normal, 29, 1.4452718f, zones, Seed);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(17)); Assert.That(actual.Remaining, Is.EqualTo(12));
            Assert.That(actual.Tails, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }
        [TestCase("move")] [TestCase("component")] [TestCase("volume")] [TestCase("trigger")] [TestCase("active")]
        public void MidLayerResumeUsesTheNextStageLiveZoneAndNeverDropsRemainingLegalCells(string change)
        {
            var point = new Vector3(.05f, 1.05f, .05f); var normal = new Vector3Int(0, 0, 1);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.35f, 1, .05f), new Vector3(.5f, 2, .05f)) };
            void Change(int stage, HeroChangeZone[] live)
            {
                if (stage != 1) return;
                if (change == "move") live[0].transform.position += Vector3.right * 10;
                else if (change == "component") live[0].enabled = false;
                else if (change == "volume") live[0].Volume.enabled = false;
                else if (change == "trigger") live[0].Volume.isTrigger = false;
                else live[0].gameObject.SetActive(false);
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 8, .2f, zones, change: Change, maxChecks: 1);
            var actual = ReplayHorizontalBuild(false, point, normal, 8, .2f, zones, change: Change, maxChecks: 1);
            SameHorizontalState(expected, actual); Assert.That(actual.FirstCandidate, Is.EqualTo(expected.FirstCandidate));
            Assert.That(actual.Added, Is.EqualTo(8)); Assert.That(actual.Proofs, Is.GreaterThanOrEqualTo(1));
            if (change == "move") Assert.That(actual.Proofs, Is.GreaterThan(1), "The next stage must reprove the moved eligible zone");
        }
        [TestCase(30f, -1f)] [TestCase(45f, 1f)]
        public void RotatedBoundaryAndNegativeScaleKeepEveryFullStreamLegalCell(float yaw, float scaleX)
        {
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.15f, 1, .15f), new Vector3(.2f, 2, .2f), yaw, new Vector3(scaleX * 2, 1, 1)) };
            var point = new Vector3(.05f, 1.05f, .05f); var normal = new Vector3Int(0, 0, 1);
            var expected = ReplayHorizontalBuild(true, point, normal, 40, .4f, zones);
            var actual = ReplayHorizontalBuild(false, point, normal, 40, .4f, zones);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(40));
        }
        [Test] public void DifferentZonesCoveringFourCornersCannotProveTheUnprotectedCentre()
        {
            var point = new Vector3(.05f, 1.05f, .05f); var normal = new Vector3Int(0, 0, 1);
            var zones = new[]
            {
                new HorizontalZoneSpec(new Vector3(-.15f, 1, .15f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(.25f, 1, .15f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(-.15f, 1, .45f), new Vector3(.02f, 2, .02f)),
                new HorizontalZoneSpec(new Vector3(.25f, 1, .45f), new Vector3(.02f, 2, .02f))
            };
            void CheckCorners(BlockWorld world)
            {
                foreach (var cell in new[] { new Vector3Int(-2, 10, 1), new Vector3Int(2, 10, 1), new Vector3Int(-2, 10, 4), new Vector3Int(2, 10, 4) })
                    Assert.That((bool)Call("ExcludedConstruction", cell), Is.True, "Each corner has a different live zone");
                Assert.That((bool)Call("ExcludedConstruction", new Vector3Int(0, 10, 1)), Is.False, "Centre column is legal");
            }
            var expected = ReplayHorizontalBuild(true, point, normal, 4, .2f, zones, CheckCorners);
            var actual = ReplayHorizontalBuild(false, point, normal, 4, .2f, zones, CheckCorners);
            SameHorizontalState(expected, actual); Assert.That(actual.Added, Is.EqualTo(4)); Assert.That(actual.Tails, Is.Zero);
        }
        [Test] public void ProtectedMiddleLayersDoNotRemoveTheLaterLegalLayerOrHideUnboundedWork()
        {
            var point = new Vector3(.05f, 1.05f, .05f); var normal = new Vector3Int(0, 0, 1);
            var zones = new[] { new HorizontalZoneSpec(new Vector3(.05f, 1, 1), new Vector3(2, 2, 1.4f)) };
            void Seed(BlockWorld world)
            {
                foreach (var cell in LegacyCandidates(world, point, normal, 24, 0).Where(world.InBounds).Where(c => c.z < 3))
                    world.Set(cell, BlockCell.Create(2, true));
            }
            // radius 0 keeps one candidate per layer; require all remaining unprotected cells.
            var expected = ReplayHorizontalBuild(true, point, normal, 24, 0, zones, Seed, maxChecks: 1);
            var actual = ReplayHorizontalBuild(false, point, normal, 24, 0, zones, Seed, maxChecks: 1);
            SameHorizontalState(expected, actual); Assert.That(actual.Layers, Is.GreaterThan(2));
            Assert.That(actual.Added, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }

        sealed class OccupiedReplay
        {
            public byte[] World;
            public int Added, Callbacks, Remaining;
            public double Points;
            public long Visited, Queries, Skipped, Deferred;
        }
        (BlockWorld next, IList results, int checks, object journal) StageOccupiedOracle(int maxChecks,
            Action<BlockWorld> beforeStage = null, bool forceChunkDeferral = false)
        {
            var commandType = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var pairType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType));
            var next = _terrain.World.Clone(); beforeStage?.Invoke(next);
            var changed = new HashSet<Vector3Int>();
            if (forceChunkDeferral) for (int i = 0; i < 8; i++) changed.Add(new Vector3Int(100 + i, 100, 100));
            object[] arguments = { next, changed, results, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, maxChecks };
            long beforeProofs = _terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs;
            var journal = StageCommandsWithJournal(arguments); int checks = (int)arguments[3];
            Assert.That(checks, Is.LessThanOrEqualTo(maxChecks));
            Assert.That(_terrain.BuildHorizontalProofAttempts + _terrain.BuildOccupiedMaskQueries + _terrain.BuildVerticalProtectedPrefixProofs - beforeProofs,
                Is.LessThanOrEqualTo(2L * checks), "One original work check can hide at most two shared proof attempts");
            Assert.That(typeof(BlockTerrain).GetField("_stageBuildWorld", Private).GetValue(_terrain), Is.Null,
                "The mutable next world must never remain installed after StageCommands returns");
            return (next, results, checks, journal);
        }
        OccupiedReplay ReplayOccupiedBuild(bool legacy, Vector3 point, Vector3Int normal, int count, float radius,
            Action<BlockWorld, Vector3Int[]> seed, Action<BlockWorld, Vector3Int[]> afterFirstAddition = null,
            bool prependRetry = false, int maxChecks = 1, bool reinstallBeforeChange = false)
        {
            // Exact old full stream plus the actual StageCommands/credit code.
            // Successful stage installation is modeled here, not async mesh/Bake.
            if (_terrain != null) Object.DestroyImmediate(_terrain.gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrain = new GameObject("Occupied prefix legacy oracle").AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var cells = LegacyCandidates(_terrain.World, point, normal, count, radius).Where(_terrain.World.InBounds).ToArray();
            seed(_terrain.World, cells); Physics.SyncTransforms(); HeroMigrationTests.Load();
            try
            {
                var credit = new BlockAttackToken(new PaintCredit(0, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, count, 8, 2);
                int reserved = credit.ReserveBuild(count, false), callbacks = 0; BlockMutationResult delivered = default;
                _terrain.QueueBuild(point, normal, 1, reserved, radius, false,
                    result => { callbacks++; delivered = result; credit.CompleteBuild(reserved, false, result.Added); }, ordinaryMainAttack: true);
                var command = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().Single();
                var type = command.GetType(); var totalField = type.GetField("Total");
                if (legacy) type.GetField("Cells").SetValue(command, LegacyCandidates(_terrain.World, point, normal, count, radius).GetEnumerator());
                int stage = 0; bool changedAfterAddition = false, changedAfterDeferral = false;
                while (!(bool)type.GetField("Done").GetValue(command) && stage < 8192)
                {
                    var cumulative = (BlockMutationResult)totalField.GetValue(command);
                    Action<BlockWorld> change = null;
                    if (!changedAfterAddition && cumulative.Added >= 1 && afterFirstAddition != null)
                    {
                        if (reinstallBeforeChange)
                            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain,
                                BlockWorld.DecodeSnapshot(_terrain.World.EncodeSnapshot()));
                        change = next => afterFirstAddition(next, cells); changedAfterAddition = true;
                    }
                    if (prependRetry && !changedAfterDeferral && _terrain.BuildDeferredByChunkBudget > 0)
                    {
                        change = next => { next.Set(cells[0], BlockCell.Create(2, true)); next.Set(cells[5], default); };
                        changedAfterDeferral = true;
                    }
                    var staged = StageOccupiedOracle(maxChecks, change, prependRetry && stage == 0);
                    foreach (var pair in staged.results)
                    {
                        var mutation = (BlockMutationResult)pair.GetType().GetField("Item2").GetValue(pair);
                        var total = (BlockMutationResult)totalField.GetValue(command);
                        total.Added += mutation.Added; total.RemovedEnemy += mutation.RemovedEnemy; total.RemovedFriendly += mutation.RemovedFriendly;
                        totalField.SetValue(command, total);
                    }
                    PublishStagedWorld(staged.next, staged.journal);
                    Assert.That(callbacks, Is.Zero, "Do not invoke result callbacks while a command is only staged"); stage++;
                }
                Assert.That((bool)type.GetField("Done").GetValue(command), Is.True, "The finite old footprint must drain");
                if (afterFirstAddition != null) Assert.That(changedAfterAddition, Is.True);
                if (prependRetry) Assert.That(changedAfterDeferral, Is.True);
                var final = (BlockMutationResult)totalField.GetValue(command); Call("Complete", command, final); Call("Complete", command, final);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(delivered.OrdinaryMainAttack, Is.True);
                Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.CommandsCompleted, Is.EqualTo(1));
                return new OccupiedReplay { World = _terrain.World.EncodeSnapshot(), Added = delivered.Added,
                    Callbacks = callbacks, Remaining = credit.BuildRemaining, Points = credit.TakePoints(),
                    Visited = _terrain.BuildCandidatesVisited, Queries = _terrain.BuildOccupiedMaskQueries,
                    Skipped = _terrain.BuildOccupiedCandidatesSkipped, Deferred = _terrain.BuildDeferredByChunkBudget };
            }
            finally { LubanConfigService.Current.Reset(); }
        }
        static void SameOccupiedReplay(OccupiedReplay expected, OccupiedReplay actual)
        {
            CollectionAssert.AreEqual(expected.World, actual.World, "Exact cells/runs/counts/round state from the independent former stream");
            Assert.That(actual.Added, Is.EqualTo(expected.Added)); Assert.That(actual.Remaining, Is.EqualTo(expected.Remaining));
            Assert.That(actual.Points, Is.EqualTo(expected.Points)); Assert.That(actual.Callbacks, Is.EqualTo(expected.Callbacks));
        }
        static void SeedOccupiedPrefixWithOldStreamHoles(BlockWorld world, Vector3Int[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
                if (i < 73 || i % 11 != 0) world.Set(cells[i], BlockCell.Create(2, true));
        }
        [TestCase(1, 0, 0)] [TestCase(-1, 0, 0)] [TestCase(0, 1, 0)]
        [TestCase(0, -1, 0)] [TestCase(0, 0, 1)] [TestCase(0, 0, -1)]
        public void OccupiedPrefixesKeepEveryOldStreamHoleAndQuotaOrderInAllSixAxes(int x, int y, int z)
        {
            var point = new Vector3(-1.65f, 3.25f, -1.65f); var normal = new Vector3Int(x, y, z);
            var expected = ReplayOccupiedBuild(true, point, normal, 7, .4f, SeedOccupiedPrefixWithOldStreamHoles);
            var actual = ReplayOccupiedBuild(false, point, normal, 7, .4f, SeedOccupiedPrefixWithOldStreamHoles);
            SameOccupiedReplay(expected, actual); Assert.That(actual.Added, Is.EqualTo(7));
            Assert.That(actual.Skipped, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }
        [TestCase("delete")] [TestCase("fill")] [TestCase("detach")] [TestCase("reinstall")]
        public void AResumedIteratorReadsTheUnpublishedNextWorldAfterItsFutureRowChanges(string change)
        {
            var point = new Vector3(-1.65f, 3.25f, -1.65f); var normal = new Vector3Int(0, 0, 1);
            void Seed(BlockWorld world, Vector3Int[] cells)
            {
                for (int i = 0; i < 49; i++) if (i != 17 && (change != "fill" || i != 20))
                    world.Set(cells[i], BlockCell.Create(2, change != "detach"));
            }
            void Change(BlockWorld next, Vector3Int[] cells)
            {
                if (change == "fill") next.Set(cells[20], BlockCell.Create(2, true));
                else if (change == "detach") next.Detach(cells[20], 1, 12);
                else next.Set(cells[20], default);
                Assert.That(_terrain.World.TryGet(cells[20], out _), Is.EqualTo(change != "fill"),
                    "The published world deliberately differs from the private staging copy");
            }
            var expected = ReplayOccupiedBuild(true, point, normal, 7, .4f, Seed, Change, reinstallBeforeChange: change == "reinstall");
            var actual = ReplayOccupiedBuild(false, point, normal, 7, .4f, Seed, Change, reinstallBeforeChange: change == "reinstall");
            SameOccupiedReplay(expected, actual); Assert.That(actual.Added, Is.EqualTo(7)); Assert.That(actual.Skipped, Is.GreaterThan(0));
        }
        [TestCase(1)] [TestCase(-1)]
        public void AChunkDeferredPrependAndRemainingMaskBothRecheckTheNewNextWorld(int direction)
        {
            var point = new Vector3(-1.65f, 3.25f, -1.65f); var normal = new Vector3Int(0, 0, direction);
            void Seed(BlockWorld world, Vector3Int[] cells)
            { for (int i = 1; i < 49; i++) world.Set(cells[i], BlockCell.Create(2, true)); }
            var expected = ReplayOccupiedBuild(true, point, normal, 3, .4f, Seed, prependRetry: true);
            var actual = ReplayOccupiedBuild(false, point, normal, 3, .4f, Seed, prependRetry: true);
            SameOccupiedReplay(expected, actual); Assert.That(actual.Deferred, Is.EqualTo(1)); Assert.That(actual.Added, Is.EqualTo(3));
        }
        [Test] public void ALongFullyOccupiedDiskKeepsTheSharedTwoProofWorkBound()
        {
            void Seed(BlockWorld world, Vector3Int[] cells)
            { foreach (var cell in cells) world.Set(cell, BlockCell.Create(2, true)); }
            var point = new Vector3(.05f, 3.25f, .05f); var normal = new Vector3Int(0, 0, 1);
            var expected = ReplayOccupiedBuild(true, point, normal, 4, 1.4452718f, Seed);
            var actual = ReplayOccupiedBuild(false, point, normal, 4, 1.4452718f, Seed);
            SameOccupiedReplay(expected, actual); Assert.That(actual.Added, Is.Zero); Assert.That(actual.Remaining, Is.EqualTo(4));
            Assert.That(actual.Skipped, Is.GreaterThan(0)); Assert.That(actual.Visited, Is.LessThan(expected.Visited));
        }
        [Test] public void OccupiedChunksCannotFilterAnIteratorOutsideItsSynchronousStage()
        {
            var world = _terrain.World; var point = new Vector3(-1.65f, 3.25f, -1.65f);
            foreach (var normal in Normals)
            {
                var expected = LegacyCandidates(world, point, normal, 3, .4f).Where(world.InBounds).ToArray();
                foreach (var cell in expected) world.Set(cell, BlockCell.Create(2, true));
                CollectionAssert.AreEqual(expected, Optimized(world, point, normal, 3, .4f).Where(world.InBounds).ToArray());
            }
            Assert.That(_terrain.BuildOccupiedMaskQueries, Is.Zero); Assert.That(_terrain.BuildOccupiedCandidatesSkipped, Is.Zero);
        }

    }
}
#endif
