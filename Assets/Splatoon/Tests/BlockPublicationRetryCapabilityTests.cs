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
    // Prepared Reports source only. No compilation, execution or performance pass.
    // The third method is explicitly a controlled retry seam, not natural reproduction.
    public sealed class BlockPublicationRetryCapabilityTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo PendingField = typeof(BlockTerrain).GetField("_pending", Private);
        static readonly FieldInfo StageWindowField = typeof(BlockTerrain).GetField("_stagePublicationWindow", Private);

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Fresh local publication capability contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault(20);
            _terrain.Initialize(null);
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static BlockPublicationWindow WindowOf(object pending)
            => (BlockPublicationWindow)pending.GetType().GetField("Window", BindingFlags.Instance | BindingFlags.Public).GetValue(pending);
        static object Call(object target, string method, params object[] args)
        {
            var entry = target.GetType().GetMethod(method, Private);
            Assert.That(entry, Is.Not.Null, "The wired R2 Terrain is required: " + method);
            return entry.Invoke(target, args);
        }
        static bool CollisionAt(Vector3Int cell)
            => Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .2f, Vector3.down, .4f,
                1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);
        IEnumerator Pump(Func<bool> done, bool authority, double now = 0, Action observe = null)
        {
            double until = Time.realtimeSinceStartupAsDouble + 180;
            for (int frame = 0; frame < 6000 && !done() && Time.realtimeSinceStartupAsDouble < until; frame++)
            { _terrain.Tick(authority, now, 1f / 60); observe?.Invoke(); yield return null; }
            Assert.That(done(), Is.True, "Full publication did not finish within bounded pumping.");
        }
        IEnumerator Install(BlockWorld source)
        {
            uint revision = _terrain.Revision + 1;
            source.Seal(revision, source.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(source.EncodeSnapshot()), Is.True);
            yield return Pump(() => _terrain.Revision == revision && PendingField.GetValue(_terrain) == null, false);
        }
        static object QueueHead(BlockTerrain terrain)
        {
            var queue = typeof(BlockTerrain).GetField("_commands", Private).GetValue(terrain);
            return queue.GetType().GetMethod("Peek").Invoke(queue, null);
        }
        static BlockPublicationWindow RetryWindowOf(object command)
            => (BlockPublicationWindow)command.GetType().GetField("RetryWindow").GetValue(command);

        [UnityTest, Timeout(600000)]
        public IEnumerator NaturalEmptyWorldBuildCapturesFutureGravityAndPublishesCollisionOnceUnderOldPolicy()
        {
            Assert.That(_terrain.World.Falling.Count, Is.Zero);
            Assert.That(_terrain.World.Cohorts.Count, Is.Zero);
            int callbacks = 0, commits = 0;
            BlockMutationResult result = default;
            var requested = new Vector3Int(5, 5, 5);
            var origin = _terrain.World;
            _terrain.QueueBuild(BlockWorld.Center(requested) - Vector3.up * .049f, Vector3.up, 1, 1, 0, false,
                value => { callbacks++; result = value; }, ordinaryMainAttack: true);
            yield return Pump(() => PendingField.GetValue(_terrain) != null, true);
            var pending = PendingField.GetValue(_terrain);
            var captured = WindowOf(pending);
            Assert.That(captured.Policy, Is.SameAs(_terrain.PublicationPolicy));
            Assert.That(captured.Includes(BlockPublicationOperation.OrdinaryAttack), Is.True);
            Assert.That(captured.Includes(BlockPublicationOperation.Gravity), Is.True);
            Assert.That(captured.MaximumChangedKeys, Is.EqualTo(8));
            Assert.That(_terrain.PinkCount, Is.Zero);
            Assert.That(callbacks, Is.Zero);
            _terrain.PublishedCommit += commit =>
            {
                commits++;
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                Assert.That(_terrain.World.TryGet(requested, out var own), Is.True);
                Assert.That(own.Owner, Is.EqualTo(1));
                Assert.That(CollisionAt(requested), Is.True);
            };
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(21);
            Assert.That(WindowOf(PendingField.GetValue(_terrain)), Is.SameAs(captured));
            yield return Pump(() => callbacks == 1, true);
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(commits, Is.EqualTo(1));
            Assert.That(result.Added, Is.EqualTo(1)); Assert.That(result.RemovedEnemy, Is.Zero);
            Assert.That(result.RemovedFriendly, Is.Zero); Assert.That(result.OrdinaryMainAttack, Is.True);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.Zero);
            Assert.That(origin.PinkCount, Is.Zero); Assert.That(origin.Chunks.Count, Is.Zero);
            Assert.That(CollisionAt(requested), Is.True);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator NaturalOrdinaryAndBlastPathsRespectSmallerCustomGravityCapAndCompleteEveryTarget()
        {
            // Both public attacks are exercised. The unequal policy makes silently
            // dropping Gravity from the initial capture observable in real windows.
            foreach (bool ordinary in new[] { true, false })
            {
                _terrain.Clear(_terrain.Round + 1);
                _terrain.PublicationPolicy = new BlockPublicationPolicy(30 + (ordinary ? 0 : 1),
                    ordinaryAttackKeys: 16, destructionKeys: 24, gravityKeys: 8, metadataKeys: 16,
                    frameKeys: 32, frameSynchronousMilliseconds: 3);
                var cells = Enumerable.Range(-6, 12).Select(x => new Vector3Int(x * 16 + 5, 5, 5)).ToArray();
                var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
                foreach (var cell in cells) seed.Set(cell, BlockCell.Create(2, true));
                yield return Install(seed);
                var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
                foreach (var cell in cells) Assert.That(CollisionAt(cell), Is.True);
                int callbacks = 0, commits = 0; BlockMutationResult result = default;
                var selectedPolicy = _terrain.PublicationPolicy;
                _terrain.QueueDestroy(BlockWorld.Center(new Vector3Int(0, 5, 5)), 10, 0, 1, true,
                    value => { callbacks++; result = value; }, maxEnemyBlocks: ordinary ? 4 : 12,
                    ordinaryMainAttack: ordinary);
                yield return Pump(() => PendingField.GetValue(_terrain) != null, true);
                var captured = WindowOf(PendingField.GetValue(_terrain));
                Assert.That(captured.Policy, Is.SameAs(selectedPolicy));
                Assert.That(captured.Includes(BlockPublicationOperation.Gravity), Is.True);
                Assert.That(captured.Includes(BlockPublicationOperation.Destruction), Is.True);
                Assert.That(captured.Includes(BlockPublicationOperation.OrdinaryAttack), Is.EqualTo(ordinary));
                Assert.That(captured.MaximumChangedKeys, Is.EqualTo(8));
                Action<BlockTerrainCommit> observer = commit =>
                { commits++; Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8)); };
                _terrain.PublishedCommit += observer;
                try { yield return Pump(() => callbacks == 1, true); }
                finally { _terrain.PublishedCommit -= observer; }
                int removed = ordinary ? 4 : 12;
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(commits, Is.GreaterThan(0));
                Assert.That(result.RemovedEnemy, Is.EqualTo(removed)); Assert.That(result.RemovedFriendly, Is.Zero);
                Assert.That(result.Added, Is.Zero); Assert.That(result.OrdinaryMainAttack, Is.EqualTo(ordinary));
                Assert.That(_terrain.BlueCount, Is.EqualTo(12 - removed)); Assert.That(_terrain.PinkCount, Is.Zero);
                int survivors = 0;
                foreach (var cell in cells)
                {
                    bool exists = _terrain.World.TryGet(cell, out var own);
                    Assert.That(CollisionAt(cell), Is.EqualTo(exists));
                    if (exists) { survivors++; Assert.That(own.Owner, Is.EqualTo(2)); }
                }
                Assert.That(survivors, Is.EqualTo(12 - removed));
                Assert.That(source.BlueCount, Is.EqualTo(12)); CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            }
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ControlledRetainedFreshLeaseIndexesLaterSameRoundMotionAndFinishesOriginalBuildOnce()
        {
            // Controlled seam: capture/restore the queued public Build through its
            // actual checkpoint helpers. This does not claim a normal guard
            // naturally requested that retry while the old source was empty.
            var below = new Vector3Int(5, 4, 5); var requested = below + Vector3Int.up;
            int callbacks = 0; BlockMutationResult result = default;
            _terrain.QueueBuild(BlockWorld.Center(below) + Vector3.up * .05f, Vector3.up, 1, 1, 0, false,
                value => { callbacks++; result = value; }, ordinaryMainAttack: true);
            var command = QueueHead(_terrain);
            var captured = (BlockPublicationWindow)Call(_terrain, "CaptureNewPublicationWindow");
            Assert.That(captured.Includes(BlockPublicationOperation.Gravity), Is.True);
            Assert.That(captured.MaximumChangedKeys, Is.EqualTo(8));
            StageWindowField.SetValue(_terrain, captured);
            object checkpoint;
            try { checkpoint = Call(_terrain, "CheckpointCommand", command); }
            finally { StageWindowField.SetValue(_terrain, null); }
            Call(_terrain, "RestoreCheckpoint", checkpoint);
            // Test setup mimics the real stage boundary that clears its local
            // checkpoint reference; the restored queue/lease remain unchanged.
            typeof(BlockTerrain).GetField("_stageCommandCheckpoint", Private).SetValue(_terrain, null);
            typeof(BlockTerrain).GetField("_stageCommandCheckpoints", Private).SetValue(_terrain, null);
            Assert.That(RetryWindowOf(command), Is.SameAs(captured));
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(41);
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            seed.Set(below, BlockCell.Create(1, true));
            var remoteBottom = new Vector3Int(55, 20, 55);
            for (int i = 0; i < 16; i++) seed.Set(remoteBottom + Vector3Int.up * i, BlockCell.Create(2, false));
            seed.Detach(remoteBottom, 16, 0);
            yield return Install(seed);
            var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
            Assert.That(_terrain.BlueCount, Is.EqualTo(16)); Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That((bool)typeof(BlockTerrain).GetField("_cohortIndexPending", Private).GetValue(_terrain), Is.True);
            Assert.That(RetryWindowOf(command), Is.SameAs(captured));
            int callbacksBefore = callbacks; Assert.That(callbacksBefore, Is.Zero);
            int observedRetainedPending = 0;
            yield return Pump(() => callbacks == 1, true, 0, () =>
            {
                var current = PendingField.GetValue(_terrain);
                if (callbacks == 0 && current != null)
                { observedRetainedPending++; Assert.That(WindowOf(current), Is.SameAs(captured)); }
                Assert.That(_terrain.LocalPublishedKeysThisFrame, Is.LessThanOrEqualTo(8));
            });
            Assert.That(observedRetainedPending, Is.GreaterThan(0));
            Assert.That((bool)typeof(BlockTerrain).GetField("_cohortIndexPending", Private).GetValue(_terrain), Is.False);
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(1));
            Assert.That(result.RemovedEnemy, Is.Zero); Assert.That(result.RemovedFriendly, Is.Zero);
            Assert.That(result.OrdinaryMainAttack, Is.True);
            Assert.That(_terrain.PinkCount, Is.EqualTo(2)); Assert.That(_terrain.BlueCount, Is.EqualTo(16));
            var survivors = _terrain.World.Falling.Values.SelectMany(run => run.Cells).Where(cell => cell.Occupied).ToArray();
            Assert.That(survivors.Length, Is.EqualTo(16));
            foreach (var cell in survivors) Assert.That(cell.Owner, Is.EqualTo(2));
            Assert.That(_terrain.World.TryGet(requested, out var own), Is.True); Assert.That(own.Owner, Is.EqualTo(1));
            Assert.That(CollisionAt(requested), Is.True);
            Assert.That(source.PinkCount, Is.EqualTo(1)); Assert.That(source.BlueCount, Is.EqualTo(16));
            CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            Assert.That(captured.Policy.Version, Is.EqualTo(20)); Assert.That(captured.MaximumChangedKeys, Is.EqualTo(8));
            Assert.That(RetryWindowOf(command), Is.Null, "Once-only completion releases the original lease.");
        }
    }
}
#endif
