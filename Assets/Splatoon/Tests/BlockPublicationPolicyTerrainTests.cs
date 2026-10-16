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
    // Prepared against the wired Reports candidate, never run or accepted here.
    // Original large-scale and rendered Player gates remain separate requirements.
    public sealed class BlockPublicationPolicyTerrainTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        static readonly FieldInfo PendingField = typeof(BlockTerrain).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic);
        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Immutable publication policy Terrain contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault();
            _terrain.Initialize(null);
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static Vector3Int[] TwelveCells()
            => Enumerable.Range(-6, 12).Select(x => new Vector3Int(x * 16 + 5, 5, 5)).ToArray();
        IEnumerator Install(Vector3Int[] cells)
        {
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            foreach (var cell in cells) seed.Set(cell, BlockCell.Create(2, true));
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, false);
            Assert.That(_terrain.BlueCount, Is.EqualTo(cells.Length));
        }
        IEnumerator Pump(Func<bool> done, bool authority)
        {
            double until = Time.realtimeSinceStartupAsDouble + 180;
            for (int frame = 0; frame < 6000 && !done() && Time.realtimeSinceStartupAsDouble < until; frame++)
            { _terrain.Tick(authority, Time.realtimeSinceStartupAsDouble, 1f / 60); yield return null; }
            Assert.That(done(), Is.True, "Complete candidate publication did not reach a terminal result within bounded test pumping.");
        }
        void BeginCompleteDeletion(Vector3Int[] cells, BlockPublicationWindow window)
        {
            var next = _terrain.World.Clone(); var changed = new HashSet<Vector3Int>();
            foreach (var cell in cells) { Assert.That(next.Set(cell, default), Is.True); changed.Add(BlockWorld.ChunkAt(cell)); }
            next.Seal(_terrain.Revision + 1, changed);
            var begin = typeof(BlockTerrain).GetMethod("BeginWindowTransaction", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(begin, Is.Not.Null, "Wired Terrain candidate is required; a standalone policy cannot satisfy this test.");
            begin.Invoke(_terrain, new object[] { next, changed, false, null, window });
        }
        static BlockPublicationWindow WindowOf(object pending)
            => (BlockPublicationWindow)pending.GetType().GetField("Window", BindingFlags.Instance | BindingFlags.Public).GetValue(pending);
        bool CollisionAt(Vector3Int cell)
            => Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .2f, Vector3.down, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);

        [UnityTest, Timeout(600000)] public IEnumerator TwelveKeyPendingSurvivesPolicyDowngradeAndPublishesCompleteCollisionAndScore()
        {
            var cells = TwelveCells(); yield return Install(cells);
            var source = _terrain.World; var sourceBytes = source.EncodeSnapshot();
            foreach (var cell in cells) Assert.That(CollisionAt(cell), Is.True);
            var captured = _terrain.PublicationPolicy.Capture(BlockPublicationOperation.Destruction);
            int commits = 0;
            _terrain.PublishedCommit += commit =>
            {
                commits++; Assert.That(commit.ChangedChunks.Length, Is.EqualTo(12));
                Assert.That(_terrain.BlueCount, Is.Zero);
                foreach (var cell in cells) Assert.That(CollisionAt(cell), Is.False);
            };
            BeginCompleteDeletion(cells, captured);
            Assert.That(WindowOf(PendingField.GetValue(_terrain)), Is.SameAs(captured));
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(3);
            Assert.That(_terrain.BlueCount, Is.EqualTo(12));
            yield return Pump(() => _terrain.PendingTransactions == 0, true);
            Assert.That(commits, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.Zero);
            Assert.That(source.BlueCount, Is.EqualTo(12)); CollectionAssert.AreEqual(sourceBytes, source.EncodeSnapshot());
            foreach (var cell in cells) Assert.That(source.TryGet(cell, out _), Is.True);
        }

        [UnityTest, Timeout(600000)] public IEnumerator ClearingALargePreparedWindowCannotPublishItInTheNextRound()
        {
            var cells = TwelveCells(); yield return Install(cells); int commits = 0;
            _terrain.PublishedCommit += _ => commits++;
            BeginCompleteDeletion(cells, _terrain.PublicationPolicy.Capture(BlockPublicationOperation.Destruction));
            uint round = _terrain.Round + 1; _terrain.Clear(round);
            for (int frame = 0; frame < 120; frame++) { _terrain.Tick(true, frame / 60d, 1f / 60); yield return null; }
            Assert.That(_terrain.Round, Is.EqualTo(round)); Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.Zero); Assert.That(commits, Is.Zero);
            Assert.That(_terrain.LocalPublishedKeysThisFrame, Is.Zero);
            foreach (var cell in cells) Assert.That(CollisionAt(cell), Is.False);
        }

        [UnityTest, Timeout(600000)] public IEnumerator RemoteFullAndDeltaFortyKeyInstallBypassLocalEightWithoutTruncation()
        {
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            var cells = (from z in new[] { -2, -1 } from x in Enumerable.Range(-10, 20)
                         select new Vector3Int(x * 16 + 5, 5, z * 16 + 5)).ToArray();
            int commits = 0, keys = 0;
            _terrain.PublishedCommit += commit => { commits++; keys = commit.ChangedChunks.Length; };
            yield return Install(cells);
            Assert.That(commits, Is.EqualTo(1)); Assert.That(keys, Is.EqualTo(40));
            Assert.That(_terrain.BlueCount, Is.EqualTo(40));
            foreach (var cell in cells)
            { Assert.That(_terrain.World.TryGet(cell, out var value), Is.True); Assert.That(value.Owner, Is.EqualTo(2)); Assert.That(CollisionAt(cell), Is.True); }
            var source = _terrain.World; var next = source.Clone();
            foreach (var cell in cells) next.Set(cell, BlockCell.Create(1, true));
            next.Seal(source.Revision + 1, cells.Select(BlockWorld.ChunkAt).ToArray());
            Assert.That(_terrain.ApplyRemoteDelta(next.EncodeDelta(source.Revision)), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, false);
            Assert.That(commits, Is.EqualTo(2)); Assert.That(keys, Is.EqualTo(40));
            Assert.That(_terrain.BlueCount, Is.Zero); Assert.That(_terrain.PinkCount, Is.EqualTo(40));
            Assert.That(source.BlueCount, Is.EqualTo(40)); Assert.That(source.PinkCount, Is.Zero);
            foreach (var cell in cells)
            { Assert.That(_terrain.World.TryGet(cell, out var value), Is.True); Assert.That(value.Owner, Is.EqualTo(1)); Assert.That(CollisionAt(cell), Is.True); }
        }

        [UnityTest, Timeout(600000)] public IEnumerator ExplosionAndFollowingOrdinaryRemovalKeepTheirProfilesBudgetsAndCallbacks()
        {
            var blastCells = TwelveCells(); var ordinary = new Vector3Int(149, 5, 149);
            yield return Install(blastCells.Append(ordinary).ToArray()); var source = _terrain.World;
            int blastCallbacks = 0, ordinaryCallbacks = 0; var order = new List<string>();
            BlockMutationResult blast = default, shot = default; BlockPublicationWindow observed = null;
            _terrain.PublishedCommit += commit =>
            {
                Assert.That(observed, Is.Not.Null);
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(observed.MaximumChangedKeys));
                if (observed.Includes(BlockPublicationOperation.OrdinaryAttack)) Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
            };
            _terrain.QueueDestroy(BlockWorld.Center(new Vector3Int(0, 5, 5)), 10, 0, 1, true,
                result => { blast = result; blastCallbacks++; order.Add("blast"); });
            _terrain.QueueDestroy(BlockWorld.Center(ordinary), .0001f, 0, 1, true,
                result => { shot = result; ordinaryCallbacks++; order.Add("ordinary"); }, maxEnemyBlocks: 4, ordinaryMainAttack: true);
            double until = Time.realtimeSinceStartupAsDouble + 180;
            for (int frame = 0; frame < 6000 && _terrain.PendingTransactions != 0 && Time.realtimeSinceStartupAsDouble < until; frame++)
            {
                var pending = PendingField.GetValue(_terrain); if (pending != null) observed = WindowOf(pending);
                _terrain.Tick(true, Time.realtimeSinceStartupAsDouble, 1f / 60);
                pending = PendingField.GetValue(_terrain); if (pending != null) observed = WindowOf(pending);
                yield return null;
            }
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(blastCallbacks, Is.EqualTo(1)); Assert.That(ordinaryCallbacks, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "blast", "ordinary" }, order);
            Assert.That(blast.RemovedEnemy, Is.EqualTo(12)); Assert.That(blast.RemovedFriendly, Is.Zero);
            Assert.That(shot.RemovedEnemy, Is.EqualTo(1)); Assert.That(shot.OrdinaryMainAttack, Is.True);
            Assert.That(_terrain.BlueCount, Is.Zero); Assert.That(source.BlueCount, Is.EqualTo(13));
        }
    }
}
#endif
