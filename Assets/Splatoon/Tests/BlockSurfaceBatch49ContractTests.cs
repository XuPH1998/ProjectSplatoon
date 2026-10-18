#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Painting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Controlled staging exposes the exact admitted prefix; every result below
    // still completes through real asynchronous terrain/collider publication.
    public sealed class BlockSurfaceBatch49ContractTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        bool[,] _ignored;
        static float Size => BlockWorld.CellSize;
        static object Read(object owner, string field) => owner.GetType().GetField(field, Private | BindingFlags.Public).GetValue(owner);
        static void Set(object owner, string field, object value) => owner.GetType().GetField(field, Private | BindingFlags.Public).SetValue(owner, value);
        static object Call(object owner, string method, params object[] arguments)
            => owner.GetType().GetMethod(method, Private).Invoke(owner, arguments);
        object[] Commands() => ((IEnumerable)Read(_terrain, "_commands")).Cast<object>().ToArray();
        BlockPublicationWindow Capture() => (BlockPublicationWindow)Call(_terrain, "CaptureNewPublicationWindow");
        static Vector3 MapPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * (Size * .5f);

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _ignored = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) _ignored[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            _root = new GameObject("Surface FIFO publication contracts");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null); _terrain.EnableSurfaceMode();
            var floor = new GameObject("Original authored floor"); floor.transform.SetParent(_root.transform);
            floor.SetActive(false);
            floor.transform.position = Vector3.down * (Size * .5f);
            var collider = floor.AddComponent<BoxCollider>(); collider.size = new Vector3(32, Size, 64);
            // This component marks an attachable map face. Disable legacy GPU
            // painting before activation, as this fixture only tests blocks.
            floor.AddComponent<PaintSurface>().enabled = false;
            floor.SetActive(true);
            ((HashSet<Collider>)Read(_terrain, "_staticColliders")).Add(collider);
            Physics.SyncTransforms();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_ignored != null)
                for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, _ignored[a, b]);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        IEnumerator Pump(Func<bool> done, bool authority)
        {
            double until = Time.realtimeSinceStartupAsDouble + 90;
            for (int i = 0; i < 6000 && !done() && Time.realtimeSinceStartupAsDouble < until; i++)
            { _terrain.Tick(authority, i / 60d, 1f / 60); yield return null; }
            Assert.That(done(), Is.True, "Real asynchronous publication did not drain within the bounded pump.");
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        IEnumerator Install(params (Vector3Int cell, byte team)[] cells)
        {
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            foreach (var entry in cells) Assert.That(seed.Set(entry.cell, BlockCell.Create(entry.team, surfaceAttached: true)), Is.True);
            uint revision = _terrain.Revision + 1;
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(() => _terrain.Revision == revision && Read(_terrain, "_pending") == null, false);
        }

        sealed class Draft
        {
            public BlockWorld World;
            public HashSet<Vector3Int> Changed;
            public IList Results;
        }
        Draft Stage(BlockPublicationWindow window, bool replay = false)
        {
            var commandType = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var pair = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var draft = new Draft { World = _terrain.World.Clone(), Changed = new HashSet<Vector3Int>(),
                Results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pair)) };
            Set(_terrain, "_stagePublicationWindow", window); Set(_terrain, "_stagePublicationReplayOnly", replay);
            Set(_terrain, "_stageCommandCheckpoint", null); Set(_terrain, "_stageCommandCheckpoints", null);
            object[] arguments = { draft.World, draft.Changed, draft.Results, 0,
                System.Diagnostics.Stopwatch.StartNew(), 10000d, 100000 };
            try { Call(_terrain, "StageCommands", arguments); }
            finally { Set(_terrain, "_stagePublicationWindow", null); Set(_terrain, "_stagePublicationReplayOnly", false); }
            Assert.That(draft.Changed.Count, Is.LessThanOrEqualTo(window.MaximumChangedKeys));
            return draft;
        }
        void Begin(Draft draft, BlockPublicationWindow window)
        {
            Assert.That(draft.Changed.Count, Is.GreaterThan(0));
            draft.World.Seal(_terrain.Revision + 1, draft.Changed);
            Call(_terrain, "BeginWindowTransaction", draft.World, draft.Changed, false, draft.Results, window);
        }
        static object ResultCommand(object pair) => pair.GetType().GetField("Item1").GetValue(pair);
        static BlockMutationResult Result(object pair) => (BlockMutationResult)pair.GetType().GetField("Item2").GetValue(pair);
        static bool TerrainCollision(Vector3Int cell)
            => Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * Size, Vector3.down, Size * 2,
                1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);

        [UnityTest, Timeout(600000)] public IEnumerator MixedLeaseUsesAllOperationCapsAndHistoricalCaptureRemainsHeadOnly()
        {
            foreach (var caps in new (int ordinary, int destruction, int gravity)[] { (12, 8, 24), (8, 24, 32), (24, 24, 8) })
            {
                _terrain.Clear(_terrain.Round + 1);
                _terrain.PublicationPolicy = new BlockPublicationPolicy(49, caps.ordinary, caps.destruction, caps.gravity, 32, 32, 3);
                _terrain.QueueBuild(MapPoint(new Vector3Int(1, 0, 1)), Vector3.up, 1, 1, 0, true);
                _terrain.QueueDestroy(Vector3.up, Size, Size, 1, false);
                var window = Capture();
                Assert.That(window.Includes(BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Destruction | BlockPublicationOperation.Gravity), Is.True);
                Assert.That(window.MaximumChangedKeys, Is.EqualTo(Math.Min(caps.ordinary, Math.Min(caps.destruction, caps.gravity))));
                Assert.That(window.Policy.FrameSynchronousMilliseconds, Is.EqualTo(3));
            }
            var legacyRoot = new GameObject("Historical mode window");
            try
            {
                var legacy = legacyRoot.AddComponent<BlockTerrain>(); legacy.Initialize(null);
                legacy.QueueBuild(Vector3.up, Vector3.up, 1, 1, 0, false);
                legacy.QueueDestroy(Vector3.up, Size, Size, 1, false);
                var window = (BlockPublicationWindow)Call(legacy, "CaptureNewPublicationWindow");
                Assert.That(window.Includes(BlockPublicationOperation.Destruction), Is.False);
                Assert.That(window.Includes(BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Gravity), Is.True);
            }
            finally { Object.DestroyImmediate(legacyRoot); }
            yield break;
        }

        [UnityTest, Timeout(600000)] public IEnumerator LookaheadStopsAtEightAndAtRetryOrSpreadWithoutSkippingCommands()
        {
            for (int builds = 7; builds <= 8; builds++)
            {
                _terrain.Clear(_terrain.Round + 1);
                for (int i = 0; i < builds; i++) _terrain.QueueBuild(Vector3.zero, Vector3.up, 1, 1, 0, true);
                _terrain.QueueDestroy(Vector3.up, Size, Size, 1, false);
                var before = Commands();
                Assert.That(Capture().Includes(BlockPublicationOperation.Destruction), Is.EqualTo(builds == 7));
                CollectionAssert.AreEqual(before, Commands(), "Operation discovery cannot consume or reorder the queue.");
            }
            foreach (bool spread in new[] { false, true })
            {
                _terrain.Clear(_terrain.Round + 1);
                _terrain.QueueBuild(Vector3.zero, Vector3.up, 1, 1, 0, true);
                _terrain.QueueBuild(Vector3.zero, Vector3.up, 1, 1, 0, true);
                _terrain.QueueDestroy(Vector3.up, Size, Size, 1, false);
                var commands = Commands();
                if (spread) Set(commands[1], "Spread", true);
                else Set(commands[1], "RetryWindow", _terrain.PublicationPolicy.Capture(BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Gravity));
                Assert.That(Capture().Includes(BlockPublicationOperation.Destruction), Is.False);
                CollectionAssert.AreEqual(commands, Commands());
            }
            yield break;
        }

        [UnityTest, Timeout(600000)] public IEnumerator BuildDirectCompleteBlastAndBuildShareOneDraftWithOrderedActualResults()
        {
            var first = new Vector3Int(1, 0, 1); var direct = new Vector3Int(5, 0, 1);
            var friendly = new Vector3Int(6, 0, 1); var enemy = new Vector3Int(7, 0, 1); var last = new Vector3Int(12, 0, 1);
            yield return Install((direct, (byte)2), (friendly, (byte)1), (enemy, (byte)2));
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            var callbacks = new List<int>(); var actual = new BlockMutationResult[5];
            var firstPayment = new BlockAttackToken(default, 0, 3, 0, 2);
            var directPayment = new BlockAttackToken(default, 0, 0, 3, 2);
            var lastPayment = new BlockAttackToken(default, 0, 4, 0, 2);
            int firstReserved = firstPayment.ReserveBuild(3, false), directReserved = directPayment.ReserveDirect(3), lastReserved = lastPayment.ReserveBuild(4, false);
            Action<BlockMutationResult> Credit(int index) => result =>
            {
                callbacks.Add(index); actual[index] = result;
                if (index == 0) firstPayment.CompleteBuild(firstReserved, false, result.Added);
                if (index == 1) directPayment.CompleteDirect(directReserved, result.RemovedEnemy);
                if (index == 4) lastPayment.CompleteBuild(lastReserved, false, result.Added);
            };
            _terrain.QueueBuild(MapPoint(first), Vector3.up, 1, firstReserved, 0, true, Credit(0), true);
            _terrain.QueueDestroy(BlockWorld.Center(direct), 0, 0, 1, true, Credit(1), directReserved, 0, true);
            _terrain.QueueDestroy(BlockWorld.Center(friendly), Size * 2.1f, Size * 2.1f, 1, false, Credit(2));
            _terrain.QueueDestroy(BlockWorld.Center(friendly), Size * 2.1f, Size * 2.1f, 1, false, Credit(3));
            _terrain.QueueBuild(MapPoint(last), Vector3.up, 1, lastReserved, 0, true, Credit(4), true);
            var commands = Commands(); var window = Capture(); var draft = Stage(window);
            Assert.That(Commands(), Is.Empty, "A completed blast may continue to the next FIFO head in this same draft.");
            CollectionAssert.AreEqual(commands, draft.Results.Cast<object>().Select(ResultCommand));
            CollectionAssert.AreEqual(new[] { 1, 0, 0, 0, 1 }, draft.Results.Cast<object>().Select(pair => Result(pair).Added));
            CollectionAssert.AreEqual(new[] { 0, 1, 1, 0, 0 }, draft.Results.Cast<object>().Select(pair => Result(pair).RemovedEnemy));
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 0, 0 }, draft.Results.Cast<object>().Select(pair => Result(pair).RemovedFriendly));
            Assert.That(callbacks, Is.Empty); Assert.That(source.BlueCount, Is.EqualTo(2));
            Assert.That(firstPayment.BuildRemaining + directPayment.DirectRemaining + lastPayment.BuildRemaining, Is.Zero);
            Assert.That(firstPayment.Added + directPayment.RemovedEnemy + lastPayment.Added, Is.Zero, "No unpublished draft settles a payment.");
            Begin(draft, window); yield return Pump(() => callbacks.Count == 5, false);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, callbacks);
            foreach (var cell in new[] { first, last }) { Assert.That(_terrain.World.TryGet(cell, out _), Is.True); Assert.That(TerrainCollision(cell), Is.True); }
            foreach (var cell in new[] { direct, friendly, enemy }) { Assert.That(_terrain.World.TryGet(cell, out _), Is.False); Assert.That(TerrainCollision(cell), Is.False); }
            Assert.That(actual[1].Added + actual[2].Added + actual[3].Added, Is.Zero);
            Assert.That(firstPayment.Added, Is.EqualTo(1)); Assert.That(firstPayment.BuildRemaining, Is.EqualTo(2));
            Assert.That(directPayment.RemovedEnemy, Is.EqualTo(1)); Assert.That(directPayment.DirectRemaining, Is.EqualTo(2));
            Assert.That(lastPayment.Added, Is.EqualTo(1)); Assert.That(lastPayment.BuildRemaining, Is.EqualTo(3));
            Assert.That(firstPayment.Added + firstPayment.BuildRemaining, Is.EqualTo(firstPayment.BuildLimit));
            Assert.That(directPayment.RemovedEnemy + directPayment.DirectRemaining, Is.EqualTo(directPayment.DirectLimit));
            Assert.That(lastPayment.Added + lastPayment.BuildRemaining, Is.EqualTo(lastPayment.BuildLimit));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            for (int i = 0; i < 3; i++) { _terrain.Tick(true, 10 + i / 60d, 1f / 60); yield return null; }
            Assert.That(callbacks.Count, Is.EqualTo(5));
        }

        [UnityTest, Timeout(600000)] public IEnumerator MixedDestructionMinimumBoundsEveryUnionAndUnfinishedBlastBlocksSuffixUntilAllTargetsPublish()
        {
            _terrain.PublicationPolicy = new BlockPublicationPolicy(49, 12, 8, 24, 32, 32, 3);
            var victims = (from x in new[] { -2, -1, 0, 1 } from z in new[] { -1, 0, 1 }
                select new Vector3Int(x * BlockWorld.ChunkSize + 5, 5, z * BlockWorld.ChunkSize + 5)).ToArray();
            yield return Install(victims.Select(cell => (cell, (byte)2)).ToArray());
            var callbacks = new List<int>(); var actual = new BlockMutationResult[4];
            Action<BlockMutationResult> Credit(int i) => result => { callbacks.Add(i); actual[i] = result; };
            // An ordinary finite quota supplies OrdinaryAttack; neither its zero
            // quota nor the later full blasts may borrow another attack's quota.
            _terrain.QueueDestroy(Vector3.zero, 0, 0, 1, true, Credit(0), 0, 0, true);
            _terrain.QueueDestroy(Vector3.up * (Size * 5.5f), 32, 32, 1, false, Credit(1));
            _terrain.QueueDestroy(Vector3.up * (Size * 5.5f), 32, 32, 1, false, Credit(2));
            var suffix = new Vector3Int(1, 0, 1);
            _terrain.QueueBuild(MapPoint(suffix), Vector3.up, 1, 1, 0, true, Credit(3), true);
            var commands = Commands();
            foreach (var command in commands.Where(command => !(bool)Read(command, "Build")))
            {
                object[] args = { command, System.Diagnostics.Stopwatch.StartNew(), 0, 10000d, 100000 };
                Assert.That((bool)Call(_terrain, "PrepareDestruction", args), Is.True);
            }
            var window = Capture(); Assert.That(window.MaximumChangedKeys, Is.EqualTo(8));
            var draft = Stage(window);
            Assert.That(draft.Changed.Count, Is.EqualTo(8), "Complete interior-cell footprints consume eight distinct keys.");
            Assert.That(draft.Results.Cast<object>().Sum(pair => Result(pair).RemovedEnemy), Is.EqualTo(8));
            Assert.That(Read(_terrain, "_blastBatch"), Is.Not.Null);
            Assert.That(Commands()[0], Is.SameAs(commands[1]));
            Assert.That((bool)Read(commands[3], "Done"), Is.False);
            Assert.That(draft.World.TryGet(suffix, out _), Is.False, "No suffix work may pass an unfinished blast.");
            Assert.That(callbacks, Is.Empty);
            var publishedKeys = new List<int>();
            _terrain.PublishedCommit += commit => publishedKeys.Add(commit.ChangedChunks.Length);
            Begin(draft, window); yield return Pump(() => Read(_terrain, "_pending") == null, false);
            CollectionAssert.AreEqual(new[] { 0 }, callbacks, "An unfinished blast retains its own callback until completion.");
            yield return Pump(() => callbacks.Count == 4, true);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, callbacks);
            Assert.That(publishedKeys.All(keys => keys <= 8), Is.True);
            Assert.That(actual[1].RemovedEnemy, Is.EqualTo(victims.Length)); Assert.That(actual[2].Changed, Is.Zero);
            Assert.That(actual[0].Changed, Is.Zero); Assert.That(actual[3].Added, Is.EqualTo(1));
            foreach (var cell in victims) { Assert.That(_terrain.World.TryGet(cell, out _), Is.False); Assert.That(TerrainCollision(cell), Is.False); }
            Assert.That(TerrainCollision(suffix), Is.True);
        }

        [UnityTest, Timeout(600000)] public IEnumerator CompletedRetrySharesOnlyItsExactCurrentPolicyAndReplacementStartsFreshAdmission()
        {
            foreach (bool replace in new[] { false, true })
            {
                _terrain.Clear(_terrain.Round + 1);
                var callbacks = new List<int>(); var cells = new[] { new Vector3Int(1, 0, 1), new Vector3Int(3, 0, 1) };
                for (int i = 0; i < 2; i++) { int index = i; _terrain.QueueBuild(MapPoint(cells[i]), Vector3.up, 1, 1, 0, true, _ => callbacks.Add(index), true); }
                var commands = Commands(); var window = Capture(); Set(commands[0], "RetryWindow", window);
                if (replace) _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(50);
                var draft = Stage(window, true);
                Assert.That(draft.Results.Count, Is.EqualTo(replace ? 1 : 2));
                Assert.That(Commands().Length, Is.EqualTo(replace ? 1 : 0));
                Assert.That(draft.Results.Cast<object>().All(pair => ReferenceEquals(ResultCommand(pair), commands[0]) || !replace), Is.True);
                Assert.That(callbacks, Is.Empty);
                Begin(draft, window); yield return Pump(() => Read(_terrain, "_pending") == null, false);
                Assert.That(callbacks.Count, Is.EqualTo(replace ? 1 : 2));
                Assert.That(Read(commands[0], "RetryWindow"), Is.Null);
                if (replace) yield return Pump(() => callbacks.Count == 2, true);
                CollectionAssert.AreEqual(new[] { 0, 1 }, callbacks);
                foreach (var cell in cells) Assert.That(TerrainCollision(cell), Is.True);
            }
        }
    }
}
#endif
