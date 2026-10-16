#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Reports-only acceptance candidates. Wire installation is only seed setup;
    // all binding/constraint release uses ordinary authority Tick/QueueDestroy.
    public sealed class BlockGravityBindingFutureMotionTests
    {
        const double Epoch = 10, BindAt = 10.2;
        const int PumpLimit = 3000;
        static readonly Vector2Int Column = new(2, 2);
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Binding future-motion authority acceptance");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var floor = new GameObject("Registered static map floor top zero");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(.25f, -.05f, .25f);
            floor.AddComponent<BoxCollider>().size = new Vector3(2, .1f, 2);
            var colliders = (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_terrain);
            colliders.Add(floor.GetComponent<Collider>()); Physics.SyncTransforms();
            _terrain.Tick(false, Epoch, .016f);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator NaturalThreeOwnerBindingKeepsEveryPoseAndFutureContact()
            => NaturalChain(false);

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ReverseIdentityOrderCannotChangeFinalLowerPoseOrFutureContact()
            => NaturalChain(true);

        IEnumerator NaturalChain(bool reverse)
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            var byHeight = new Dictionary<int, ulong>();
            foreach (int y in reverse ? new[] { 30, 20, 10 } : new[] { 10, 20, 30 })
            {
                var group = Add(seed, y, y == 20 ? (byte)2 : (byte)1, 0, y * BlockWorld.CellSize,
                    Epoch, y == 10 ? .3f : y == 20 ? 2f : 3f);
                if (y == 10) group.InitialOffset = .05f;
                if (y == 20) group.InitialOffset = .1f;
                byHeight.Add(y, group.Id);
            }
            yield return Install(seed);
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            var originals = byHeight.ToDictionary(p => p.Key, p => source.Cohorts[p.Value]);
            var addresses = Addresses(source);
            bool seen = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (seen || !_terrain.World.Cohorts.TryGetValue(byHeight[20], out var middle)
                    || !_terrain.World.Cohorts.TryGetValue(byHeight[30], out var upper)
                    || middle.LowerSupport?.Id != byHeight[10] || upper.LowerSupport?.Id != byHeight[20]) return;
                seen = true; double published = _terrain.SimulationTime;
                foreach (var pair in originals)
                {
                    var current = _terrain.World.Cohorts[byHeight[pair.Key]];
                    AssertSample(current, published, pair.Value.Offset(published), pair.Value.Speed(published));
                }
                // Independent ballistic recurrence samples both pre-contact and
                // contact/floor-limited future motion. No private evaluator calls.
                foreach (double elapsed in new[] { 0d, .001, .01, .05, .1, .2, .3, .5, 1d })
                {
                    double at = published + elapsed;
                    var lower = Free(originals[10].Offset(published), originals[10].Speed(published), elapsed, 1f);
                    var mid = Cap(Free(originals[20].Offset(published), originals[20].Speed(published), elapsed, 2f), lower, .9f);
                    var top = Cap(Free(originals[30].Offset(published), originals[30].Speed(published), elapsed, 3f), mid, .9f);
                    AssertSample(_terrain.World.Cohorts[byHeight[10]], at, lower.Offset, lower.Speed);
                    AssertSample(middle, at, mid.Offset, mid.Speed);
                    AssertSample(upper, at, top.Offset, top.Speed);
                    Assert.That(middle.Bottom.y * BlockWorld.CellSize - middle.Offset(at),
                        Is.GreaterThanOrEqualTo(middle.LowerSupport.TopExclusive * BlockWorld.CellSize - middle.LowerSupport.Offset(at) - 1e-6f));
                    Assert.That(upper.Bottom.y * BlockWorld.CellSize - upper.Offset(at),
                        Is.GreaterThanOrEqualTo(middle.TopExclusive * BlockWorld.CellSize - middle.Offset(at) - 1e-6f));
                }
                AssertWire(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try { yield return Pump(BindAt, true, () => seen && _terrain.PendingTransactions == 0); }
            finally { _terrain.PublishedCommit -= observe; }
            // Subsequent authority visits exercise the committed edge and fresh
            // floor proofs while all three physical owners are still moving.
            foreach (double at in new[] { 10.21, 10.24, 10.28, 10.32 })
            {
                _terrain.Tick(true, at, .016f); yield return null;
                yield return Pump(at, true, () => _terrain.PendingTransactions == 0);
                foreach (var pair in originals)
                    AssertSample(_terrain.World.Cohorts[byHeight[pair.Key]], at, pair.Value.Offset(at), pair.Value.Speed(at));
                AssertWire(source, _terrain.World, addresses);
            }
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.GravityBindingPublicationDeferrals, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator DestroyingALiveLowerCannotReleaseAccumulatedFreeFallWhenItsRetainedProofClears()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            var lower = Add(seed, 10, 1, 0, 1, Epoch, 0);
            // Raw upper free fall is already large, but its live lower makes the
            // published geometry legal and limits its actual velocity.
            var upper = Add(seed, 20, 2, 0, 2, Epoch - 1, 0);
            seed.SetCohortSupport(upper.Id, lower.Id, .9f, out _);
            yield return Install(seed);
            _terrain.Tick(false, BindAt, .016f);
            var source = _terrain.World; var original = source.Cohorts[upper.Id];
            byte[] frozen = source.EncodeSnapshot();
            var addresses = Addresses(source).Where(a => source.Falling[a.Run].Cohort.Id == upper.Id).ToArray();
            var low = source.Cohorts[lower.Id];
            Vector3 hit = BlockWorld.Center(low.Bottom) - Vector3.up * low.Offset(BindAt);
            int callbacks = 0; bool released = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (!_terrain.World.Cohorts.TryGetValue(upper.Id, out var current)) return;
                AssertSample(current, BindAt, original.Offset(BindAt), original.Speed(BindAt));
                if (callbacks != 1 || current.LowerSupport != null || current.NeedsLandingProof) return;
                released = true;
                Assert.That(current.Phase, Is.EqualTo(FallingCohortPhase.Falling), "Constraint release must publish its continuous epoch before settlement.");
                foreach (double elapsed in new[] { .001, .01, .05, .1, .2, .3 })
                {
                    var expected = Free(original.Offset(BindAt), original.Speed(BindAt), elapsed, 2f);
                    AssertSample(current, BindAt + elapsed, expected.Offset, expected.Speed);
                }
                AssertWire(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueDestroy(hit, .0001f, 0, 2, true, result =>
                { callbacks++; Assert.That(result.RemovedEnemy, Is.EqualTo(1)); }, 1, 0);
                yield return Pump(BindAt, true, () => callbacks == 1 && released && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator DestroyingAStableFloorExtendsItsCapFromTheActualZeroSpeedPose()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            seed.Set(At(0), BlockCell.Create(1, true));
            var upper = Add(seed, 10, 2, 0, .9f, Epoch - 1, 0);
            yield return Install(seed);
            var source = _terrain.World; var original = source.Cohorts[upper.Id];
            byte[] frozen = source.EncodeSnapshot(); var addresses = Addresses(source);
            int callbacks = 0; bool extended = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                if (!_terrain.World.Cohorts.TryGetValue(upper.Id, out var current)) return;
                AssertSample(current, Epoch, original.Offset(Epoch), 0);
                if (callbacks != 1 || !current.LandingKnown || current.LandingOffset < .999f) return;
                extended = true;
                Assert.That(current.Phase, Is.EqualTo(FallingCohortPhase.Falling));
                foreach (double elapsed in new[] { .001, .01, .05, .1, .2 })
                {
                    var expected = Free(.9f, 0, elapsed, 1f);
                    AssertSample(current, Epoch + elapsed, expected.Offset, expected.Speed);
                }
                AssertWire(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueDestroy(BlockWorld.Center(At(0)), .0001f, 0, 2, true, result =>
                { callbacks++; Assert.That(result.RemovedEnemy, Is.EqualTo(1)); }, 1, 0);
                yield return Pump(Epoch, true, () => callbacks == 1 && extended && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator DestroyingAStableFloorCannotReleaseALegacyRunsOldRawFall()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            seed.Set(At(0), BlockCell.Create(1, true)); seed.Set(At(10), BlockCell.Create(2));
            var run = seed.Detach(At(10), 1, Epoch);
            seed.PrepareLegacyRunEpochsForPublication(out _); seed.ActivateRebasePendingCohorts(Epoch);
            run = seed.EditFalling(run.Id); run.StartedAt = Epoch - 1;
            run.LandingKnown = true; run.LandingOffset = .9f;
            yield return Install(seed);
            var source = _terrain.World; var original = source.Falling[run.Id];
            byte[] frozen = source.EncodeSnapshot(); var addresses = Addresses(source);
            int callbacks = 0; bool extended = false;
            Action<BlockTerrainCommit> observe = _ =>
            {
                Assert.That(_terrain.World.Falling.TryGetValue(run.Id, out var current), Is.True,
                    "An old legacy epoch cannot settle its unpublished jumped pose.");
                Assert.That(current.Cohort, Is.Null);
                Assert.That(current.Offset(Epoch), Is.EqualTo(original.Offset(Epoch)).Within(2e-6f));
                Assert.That(current.Speed(Epoch), Is.EqualTo(original.Speed(Epoch)).Within(2e-6f));
                if (callbacks != 1 || !current.LandingKnown || current.LandingOffset < .999f) return;
                extended = true;
                foreach (double elapsed in new[] { .001, .01, .05, .1, .2 })
                {
                    var expected = Free(.9f, 0, elapsed, 1f);
                    Assert.That(current.Offset(Epoch + elapsed), Is.EqualTo(expected.Offset).Within(2e-6f));
                    Assert.That(current.Speed(Epoch + elapsed), Is.EqualTo(expected.Speed).Within(2e-6f));
                }
                AssertWire(source, _terrain.World, addresses);
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueDestroy(BlockWorld.Center(At(0)), .0001f, 0, 2, true, result =>
                { callbacks++; Assert.That(result.RemovedEnemy, Is.EqualTo(1)); }, 1, 0);
                yield return Pump(Epoch, true, () => callbacks == 1 && extended && _terrain.PendingTransactions == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SettledLowerContactRoundingKeepsEveryPublishedSnapshotAndDeltaStrictlyValid()
            => SettledLowerContactRounding(false);

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ReverseIdentitySettledLowerContactRoundingKeepsEveryPublishedSnapshotAndDeltaStrictlyValid()
            => SettledLowerContactRounding(true);

        IEnumerator SettledLowerContactRounding(bool reverse)
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            var byHeight = new Dictionary<int, ulong>();
            foreach (int y in reverse ? new[] { 8, 3 } : new[] { 3, 8 })
            {
                var group = Add(seed, y, y == 3 ? (byte)1 : (byte)2,
                    0, y * BlockWorld.CellSize, Epoch - 1, 0);
                byHeight.Add(y, group.Id);
            }
            seed.SetCohortSupport(byHeight[8], byHeight[3], 4 * BlockWorld.CellSize, out _);
            // Both descriptors are legal. The original strict codec accepts the
            // seed before any authority mutation; only ordinary Tick follows.
            yield return Install(seed);
            var source = _terrain.World;
            byte[] frozen = source.EncodeSnapshot(), previous = frozen;
            uint previousRevision = source.Revision;
            float canonicalUpperCap = 7 * BlockWorld.CellSize;
            Assert.That(BitConverter.SingleToInt32Bits(canonicalUpperCap), Is.EqualTo(0x3f333333));
            Assert.That(BitConverter.SingleToInt32Bits(source.Cohorts[byHeight[8]].Offset(Epoch)),
                Is.EqualTo(0x3f333334), "This legal contact must exercise the actual one-ULP disagreement.");
            int publications = 0;
            bool lowerSettled = false, canonicalCapPublished = false;
            Action<BlockTerrainCommit> observe = commit =>
            {
                var current = _terrain.World;
                Assert.That(commit.Revision, Is.EqualTo(current.Revision));
                Assert.That(commit.Revision, Is.GreaterThan(previousRevision));
                byte[] snapshot = current.EncodeSnapshot();
                var fullCopy = BlockWorld.DecodeSnapshot(snapshot);
                var seedDeltaCopy = BlockWorld.DecodeSnapshot(frozen).WithDelta(current.EncodeDelta(source.Revision));
                var consecutiveDeltaCopy = BlockWorld.DecodeSnapshot(previous).WithDelta(current.EncodeDelta(previousRevision));
                CollectionAssert.AreEqual(snapshot, fullCopy.EncodeSnapshot(), "Every published full snapshot must pass the original strict decoder.");
                CollectionAssert.AreEqual(snapshot, seedDeltaCopy.EncodeSnapshot(), "Repair from the original seed must reproduce this exact publication.");
                CollectionAssert.AreEqual(snapshot, consecutiveDeltaCopy.EncodeSnapshot(), "A client applying each actual delta must reproduce this exact publication.");
                foreach (var copy in new[] { current, fullCopy, seedDeltaCopy, consecutiveDeltaCopy })
                {
                    Assert.That(copy.PinkCount, Is.EqualTo(1)); Assert.That(copy.BlueCount, Is.EqualTo(1));
                    var cells = copy.Chunks.Values.SelectMany(chunk => chunk.Cells)
                        .Concat(copy.Falling.Values.SelectMany(run => run.Cells)).Where(cell => cell.Occupied).ToArray();
                    Assert.That(cells.Count(cell => cell.Owner == 1), Is.EqualTo(1));
                    Assert.That(cells.Count(cell => cell.Owner == 2), Is.EqualTo(1));
                }
                lowerSettled |= !current.Cohorts.ContainsKey(byHeight[3])
                    && current.TryGet(At(0), out var lowerCell) && lowerCell.Owner == 1;
                if (lowerSettled && current.Cohorts.TryGetValue(byHeight[8], out var upper)
                    && upper.LowerSupport == null && ((Array)typeof(FallingCohort).GetField("RetainedSupportMotion",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(upper)).Length == 0
                    && upper.LandingKnown && !upper.NeedsLandingProof)
                {
                    canonicalCapPublished = true;
                    // A freshly proved stable contact is exactly seven cells.
                    // Neither widening the cap nor retaining the old support is
                    // an acceptable way to keep the wire descriptor valid.
                    Assert.That(BitConverter.SingleToInt32Bits(upper.LandingOffset), Is.EqualTo(0x3f333333));
                    Assert.That(BitConverter.SingleToInt32Bits(upper.InitialOffset), Is.EqualTo(0x3f333333));
                    Assert.That(upper.InitialSpeed, Is.Zero);
                    Assert.That(upper.Offset(Epoch), Is.EqualTo(canonicalUpperCap));
                }
                CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(), "Published-source data must remain immutable across rebasing and settlement.");
                previous = snapshot; previousRevision = current.Revision; publications++;
            };
            _terrain.PublishedCommit += observe;
            try
            {
                yield return Pump(Epoch, true, () => lowerSettled && canonicalCapPublished
                    && _terrain.FallingCount == 0 && _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(publications, Is.GreaterThan(0)); Assert.That(lowerSettled && canonicalCapPublished, Is.True);
            Assert.That(_terrain.World.Cohorts.Count, Is.Zero); Assert.That(_terrain.World.Falling.Count, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Physics.SyncTransforms();
            foreach (var expected in new[] { (Cell: At(0), Owner: (byte)1), (Cell: At(1), Owner: (byte)2) })
            {
                Assert.That(_terrain.World.TryGet(expected.Cell, out var cell), Is.True);
                Assert.That(cell.Owner, Is.EqualTo(expected.Owner));
                Assert.That(Physics.Raycast(BlockWorld.Center(expected.Cell) + Vector3.forward * .08f, Vector3.back,
                    out var hit, .16f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
                Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
                Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(contact.Stable, Is.True); Assert.That(contact.Owner, Is.EqualTo(expected.Owner));
                Assert.That(contact.Cell, Is.EqualTo(expected.Cell)); Assert.That(contact.Revision, Is.EqualTo(_terrain.Revision));
            }
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero); Assert.That(_terrain.GravityBindingPublicationDeferrals, Is.Zero);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        static Vector3Int At(int y) => new(Column.x, y, Column.y);
        static FallingCohort Add(BlockWorld world, int y, byte owner, float offset, float landing, double started, float speed)
        {
            world.Set(At(y), BlockCell.Create(owner));
            var group = world.BeginCohort(At(y), 1);
            world.DetachCohortPiece(group.Id, 1, Epoch, out _);
            group.LandingKnown = true; group.LandingOffset = landing;
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            group = world.EditCohort(group.Id, out _);
            group.StartedAt = started; group.InitialOffset = offset; group.InitialSpeed = speed;
            group.LandingKnown = true; group.LandingOffset = landing;
            return group;
        }
        IEnumerator Install(BlockWorld seed)
        {
            seed.Seal(1, seed.RevisionKeys.ToArray()); byte[] wire = seed.EncodeSnapshot();
            CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            yield return Pump(Epoch, false, () => _terrain.Revision == 1 && _terrain.PendingTransactions == 0);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
        }
        IEnumerator Pump(double now, bool authority, Func<bool> done)
        {
            for (int step = 0; step < PumpLimit && !done(); step++)
            {
                _terrain.Tick(authority, now, .016f);
                if (done()) yield break;
                yield return null;
            }
            Assert.That(done(), Is.True, "Finite authority future-motion contract did not reach its declared outcome.");
        }
        static (float Offset, float Speed) Free(float offset, float speed, double elapsed, float landing)
        {
            float t = (float)elapsed, raw = offset + speed * t + .5f * BlockWorld.Gravity * t * t;
            return raw >= landing ? (landing, 0) : (raw, speed + BlockWorld.Gravity * t);
        }
        static (float Offset, float Speed) Cap((float Offset, float Speed) value, (float Offset, float Speed) lower, float clearance)
            => value.Offset >= lower.Offset + clearance ? (lower.Offset + clearance, lower.Speed) : value;
        static void AssertSample(FallingCohort group, double at, float offset, float speed)
        {
            Assert.That(group.Offset(at), Is.EqualTo(offset).Within(2e-6f), "Actual position at " + at);
            Assert.That(group.Speed(at), Is.EqualTo(speed).Within(2e-6f), "Actual speed at " + at);
        }
        static FallingCellAddress[] Addresses(BlockWorld world) => world.Falling.Values
            .SelectMany(run => Enumerable.Range(0, run.Cells.Length).Where(i => run.Cells[i].Occupied)
                .Select(i => new FallingCellAddress(run.Id, i))).ToArray();
        static void AssertWire(BlockWorld source, BlockWorld current, FallingCellAddress[] addresses)
        {
            var snapshot = BlockWorld.DecodeSnapshot(current.EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(current.EncodeSnapshot(), snapshot.EncodeSnapshot());
            CollectionAssert.AreEqual(current.EncodeSnapshot(), delta.EncodeSnapshot());
            foreach (var address in addresses)
            {
                Assert.That(source.TryGetFallingCell(address.Run, address.Index, out var expected, out int oldIndex), Is.True);
                foreach (var copy in new[] { current, snapshot, delta })
                {
                    Assert.That(copy.TryGetFallingCell(address.Run, address.Index, out var actual, out int index), Is.True);
                    Assert.That(actual.Id, Is.EqualTo(expected.Id)); Assert.That(index, Is.EqualTo(oldIndex));
                    Assert.That(actual.Cells[index].Owner, Is.EqualTo(expected.Cells[oldIndex].Owner));
                    Assert.That(actual.Cells[index].Flags, Is.EqualTo(expected.Cells[oldIndex].Flags));
                    Assert.That(actual.Cells[index].Durability, Is.EqualTo(expected.Cells[oldIndex].Durability));
                }
            }
        }
    }
}
#endif
