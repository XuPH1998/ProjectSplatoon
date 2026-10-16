#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Deterministic check budgets exercise the real staging loop. No clock delays
    // or production budget changes are needed to hit the final-work-unit boundary.
    public sealed class BlockBuildQuotaDoneScalarTests
    {
        GameObject _object;
        BlockTerrain _terrain;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _object = new GameObject("Build quota scalar boundary");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            _terrain.enabled = false;
        }
        [TearDown] public void Cleanup()
        {
            if (_object != null) Object.DestroyImmediate(_object);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test] public void OneAddedCellBelowQuotaKeepsTheSameUnfinishedHead()
        {
            int callbacks = 0;
            _terrain.QueueBuild(new Vector3(.25f, .149f, .25f), Vector3.up, 1, 2, 0, false, _ => callbacks++);
            var command = BuildQuotaDoneFixture.Head(_terrain);
            var batch = BuildQuotaDoneFixture.Stage(_terrain, 1);
            Assert.That(batch.Checks, Is.EqualTo(1)); Assert.That(batch.Mutation.Added, Is.EqualTo(1));
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.False);
            Assert.That(BuildQuotaDoneFixture.Head(_terrain), Is.SameAs(command));
            Assert.That(callbacks, Is.Zero); Assert.That(_terrain.World.PinkCount, Is.Zero);
        }

        [Test] public void ChunkDeferredFinalQuotaCellIsNotCompletedOrDropped()
        {
            int callbacks = 0;
            _terrain.QueueBuild(new Vector3(.25f, .149f, .25f), Vector3.up, 1, 1, 0, false, _ => callbacks++);
            var command = BuildQuotaDoneFixture.Head(_terrain);
            var changed = new HashSet<Vector3Int>(Enumerable.Range(0, 8).Select(i => new Vector3Int(100 + i, 100, 100)));
            var batch = BuildQuotaDoneFixture.Stage(_terrain, 1, changed);
            Assert.That(batch.Checks, Is.EqualTo(1)); Assert.That(batch.Mutation.Added, Is.Zero);
            Assert.That(_terrain.BuildDeferredByChunkBudget, Is.EqualTo(1));
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.False);
            Assert.That(BuildQuotaDoneFixture.Head(_terrain), Is.SameAs(command));
            Assert.That(callbacks, Is.Zero); Assert.That(batch.Next.PinkCount, Is.Zero);
        }
    }

    // These EditMode UnityTests enter Play Mode and retain the real Task,
    // upload, Physics.BakeMesh, collider swap and primary result callback chain.
    public sealed class BlockBuildQuotaDonePublicationTests
    {
        GameObject _object;
        BlockTerrain _terrain;
        bool _cleaning;
        int _callbacks, _mutations, _terminals, _terminalsBeforeCallback;
        bool _callbackCell, _callbackCollider, _mutationCollider;
        uint _callbackContactRevision;
        BlockMutationResult _delivered;
        BlockAttackToken _token;
        Vector3Int _finalCell;
        readonly List<string> _order = new();

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            // Let the real bootstrap finish owning its Addressables operations before
            // this fixture uses the signed global settings. No manual weapon injection.
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || !PrototypeApp.Current.Ready) &&
                   Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (PrototypeApp.Current != null && !string.IsNullOrEmpty(PrototypeApp.Current.Error)) break;
                yield return null;
            }
            Assert.That(PrototypeApp.Current, Is.Not.Null, "Real application bootstrap must exist before the publication fixture.");
            Assert.That(PrototypeApp.Current.Ready, Is.True, PrototypeApp.Current.Error);
            Assert.That(LubanConfigService.Current.IsReady, Is.True);
            Assert.That(LubanConfigService.Current.ContentSignature, Has.Length.EqualTo(32));
            _object = new GameObject("Build quota real publication boundary");
            _terrain = _object.AddComponent<BlockTerrain>(); _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            _terrain.enabled = false;
            _cleaning = false; _callbacks = _mutations = _terminals = _terminalsBeforeCallback = 0;
            _callbackCell = _callbackCollider = _mutationCollider = false; _callbackContactRevision = 0;
            _delivered = default; _token = null; _order.Clear();
            _terrain.PendingTransactionFinished += OnTerminal;
            _terrain.MutationCompleted += OnMutation;
            _terrain.PublishedCommit += OnCommit;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            _cleaning = true;
            if (_terrain != null)
            {
                _terrain.PendingTransactionFinished -= OnTerminal;
                _terrain.MutationCompleted -= OnMutation;
                _terrain.PublishedCommit -= OnCommit;
            }
            if (_object != null) Object.DestroyImmediate(_object);
            yield return new ExitPlayMode();
            LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void OnTerminal(BlockTerrain.PendingWallDiagnostic sample)
        {
            if (_cleaning) return;
            // Scalar collection only: do not reenter terrain from diagnostics.
            if (sample.Outcome == BlockTerrain.PendingWallOutcome.Published) { _terminals++; _order.Add("terminal"); }
        }
        void OnMutation(BlockMutationResult result, double latency)
        {
            if (_cleaning) return;
            _mutations++; _order.Add("mutation");
            _mutationCollider = PublishedTop(_finalCell, out _);
        }
        void OnCommit(BlockTerrainCommit commit)
        {
            if (!_cleaning) _order.Add("commit");
        }
        object QueuePaidBuild(int quota, Vector3Int first, Vector3Int final, Vector3Int[] explicitCells = null)
        {
            _finalCell = final;
            _token = new BlockAttackToken(new PaintCredit(0, 1, _terrain.Round, 1, PaintAttackKind.Main, true), 0, quota, 8, 2);
            int reserved = _token.ReserveBuild(quota, false);
            Assert.That(reserved, Is.EqualTo(quota)); Assert.That(_token.BuildRemaining, Is.Zero);
            _terrain.QueueBuild(BlockWorld.Center(first) - Vector3.up * .051f, Vector3.up, 1, reserved, 0, false, result =>
            {
                if (_cleaning) return;
                _callbacks++; _order.Add("callback"); _delivered = result; _terminalsBeforeCallback = _terminals;
                _callbackCell = _terrain.World.TryGet(_finalCell, out var cell) && cell.Owner == 1;
                _callbackCollider = PublishedTop(_finalCell, out var contact);
                _callbackContactRevision = contact.Revision;
                _token.CompleteBuild(reserved, false, result.Added);
            }, ordinaryMainAttack: true);
            var command = BuildQuotaDoneFixture.Head(_terrain);
            if (explicitCells != null)
                BuildQuotaDoneFixture.Set(command, "Cells", ((IEnumerable<Vector3Int>)explicitCells).GetEnumerator());
            return command;
        }
        bool PublishedTop(Vector3Int cell, out BlockContact contact)
        {
            contact = default;
            return Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .4f, Vector3.down,
                out var hit, .5f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore)
                && _terrain.TryContact(hit, out contact) && contact.Stable && contact.Owner == 1 && contact.Cell == cell;
        }
        void AssertBeforePublication(object command, int alreadyPublished)
        {
            Assert.That(_callbacks, Is.Zero); Assert.That(_mutations, Is.Zero);
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Completed"), Is.False);
            Assert.That(((BlockMutationResult)BuildQuotaDoneFixture.Get(command, "Total")).Added, Is.EqualTo(alreadyPublished));
            Assert.That(_token.Added, Is.Zero, "Partial publication cannot finalize the reserved payment");
            Assert.That(_token.BuildRemaining, Is.Zero); Assert.That(_token.TakePoints(), Is.Zero);
        }
        void AssertFinal(object command, int actual, int refund, int expectedTerminals)
        {
            Assert.That(_callbacks, Is.EqualTo(1)); Assert.That(_mutations, Is.EqualTo(1));
            Assert.That(_terminalsBeforeCallback, Is.EqualTo(expectedTerminals));
            Assert.That(_callbackCell && _callbackCollider && _mutationCollider, Is.True,
                "The real committed World and registered MeshCollider precede MutationCompleted and the paid callback");
            Assert.That(_delivered.Added, Is.EqualTo(actual)); Assert.That(_delivered.OrdinaryMainAttack, Is.True);
            Assert.That(_delivered.RemovedEnemy, Is.Zero); Assert.That(_delivered.RemovedFriendly, Is.Zero);
            Assert.That(_callbackContactRevision, Is.EqualTo(_delivered.Revision));
            Assert.That(_token.Added, Is.EqualTo(actual)); Assert.That(_token.BuildRemaining, Is.EqualTo(refund));
            Assert.That(_token.TakePoints(), Is.EqualTo(actual / (double)_token.BlocksPerPoint));
            Assert.That(_token.TakePoints(), Is.Zero);
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(1));
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            BuildQuotaDoneFixture.Call(_terrain, "Complete", command, _delivered);
            Assert.That(_callbacks, Is.EqualTo(1)); Assert.That(_mutations, Is.EqualTo(1));
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(1), "Reentry cannot pay or complete twice");
        }

        [UnityTest] public IEnumerator FinalQuotaUsesLastCheckAndCompletesAtItsRealColliderCommit()
        {
            var cell = new Vector3Int(2, 1, 2);
            var command = QueuePaidBuild(1, cell, cell); // Real production candidate iterator.
            var batch = BuildQuotaDoneFixture.Stage(_terrain, 1);
            Assert.That(batch.Checks, Is.EqualTo(1)); Assert.That(batch.Mutation.Added, Is.EqualTo(1));
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.True);
            Assert.That(BuildQuotaDoneFixture.QueueCount(_terrain), Is.Zero);
            AssertBeforePublication(command, 0); Assert.That(_terrain.World.TryGet(cell, out _), Is.False);
            BuildQuotaDoneFixture.StartPublication(_terrain, batch);
            yield return BuildQuotaDoneFixture.WaitReady(_terrain);
            AssertBeforePublication(command, 0); Assert.That(_terrain.World.TryGet(cell, out _), Is.False);
            Assert.That(PublishedTop(cell, out _), Is.False, "Ready meshes are still unpublished");
            _terrain.Tick(false, 10, 1f / 60);
            AssertFinal(command, 1, 0, 1);
            CollectionAssert.AreEqual(new[] { "terminal", "mutation", "callback", "commit" }, _order);
        }

        [UnityTest] public IEnumerator ThreePartialTransactionsKeepReservationUntilTheFinalQuotaCommit()
        {
            var cells = new[] { new Vector3Int(2, 1, 2), new Vector3Int(20, 1, 2), new Vector3Int(38, 1, 2) };
            var command = QueuePaidBuild(3, cells[0], cells[2], cells);
            for (int i = 0; i < cells.Length; i++)
            {
                var batch = BuildQuotaDoneFixture.Stage(_terrain, 1);
                Assert.That(batch.Checks, Is.EqualTo(1)); Assert.That(batch.Mutation.Added, Is.EqualTo(1));
                Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.EqualTo(i == 2));
                Assert.That(BuildQuotaDoneFixture.QueueCount(_terrain), Is.EqualTo(i == 2 ? 0 : 1));
                AssertBeforePublication(command, i);
                BuildQuotaDoneFixture.StartPublication(_terrain, batch);
                yield return BuildQuotaDoneFixture.WaitReady(_terrain);
                AssertBeforePublication(command, i); Assert.That(_terrain.World.TryGet(cells[i], out _), Is.False);
                _terrain.Tick(false, 10 + i, 1f / 60);
                Assert.That(_terrain.World.TryGet(cells[i], out _), Is.True);
                if (i < 2) AssertBeforePublication(command, i + 1);
            }
            AssertFinal(command, 3, 0, 3);
            CollectionAssert.AreEqual(new[] { "terminal", "commit", "terminal", "commit",
                "terminal", "mutation", "callback", "commit" }, _order);
        }

        [UnityTest] public IEnumerator FinalQuotaAndRealGravitySettlementFinishInTheirSharedCommit()
        {
            var build = new Vector3Int(2, 1, 2); var falling = new Vector3Int(34, 10, 34);
            var landing = new Vector3Int(34, 1, 34); var seed = _terrain.World.Clone();
            seed.Set(build + Vector3Int.down, BlockCell.Create(1, true));
            seed.Set(landing + Vector3Int.down, BlockCell.Create(2, true));
            seed.Set(falling, BlockCell.Create(2)); var run = seed.Detach(falling, 1, -10);
            run.LandingKnown = true; run.LandingOffset = (falling.y - landing.y) * BlockWorld.CellSize;
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            _terrain.Tick(false, 1, 1f / 60); yield return BuildQuotaDoneFixture.WaitReady(_terrain);
            _terrain.Tick(false, 2, 1f / 60);
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.FallingCount, Is.EqualTo(1));
            _order.Clear(); _terminals = 0;

            var command = QueuePaidBuild(1, build, build);
            var batch = BuildQuotaDoneFixture.Stage(_terrain, 1);
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.True);
            object[] gravity = { batch.Next, batch.Changed, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, 4096 };
            bool gravityChanged = (bool)BuildQuotaDoneFixture.Call(_terrain, "StageGravity", gravity);
            Assert.That(gravityChanged, Is.True, gravityChanged ? string.Empty
                : BuildQuotaDoneFixture.GravityDiagnostics(_terrain, batch.Next, (int)gravity[2]));
            Assert.That((int)gravity[2], Is.GreaterThan(0));
            Assert.That(batch.Next.Falling.Count, Is.Zero);
            Assert.That(batch.Next.TryGet(landing, out var landed) && landed.Owner == 2, Is.True);
            Assert.That(_terrain.World.Falling.Count, Is.EqualTo(1)); AssertBeforePublication(command, 0);
            BuildQuotaDoneFixture.StartPublication(_terrain, batch);
            yield return BuildQuotaDoneFixture.WaitReady(_terrain);
            AssertBeforePublication(command, 0);
            _terrain.Tick(false, 10, 1f / 60);
            Assert.That(_terrain.World.TryGet(landing, out landed) && landed.Owner == 2, Is.True);
            Assert.That(_terrain.FallingCount, Is.Zero); AssertFinal(command, 1, 0, 1);
            CollectionAssert.AreEqual(new[] { "terminal", "mutation", "callback", "commit" }, _order);
        }

        [UnityTest] public IEnumerator ExhaustedTwoCellFootprintRefundsOnlyOneReservedCellAfterFinalCommit()
        {
            var cells = new[] { new Vector3Int(2, 1, 2), new Vector3Int(20, 1, 2) };
            var command = QueuePaidBuild(3, cells[0], cells[1], cells);
            var first = BuildQuotaDoneFixture.Stage(_terrain, 1);
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.False);
            BuildQuotaDoneFixture.StartPublication(_terrain, first);
            yield return BuildQuotaDoneFixture.WaitReady(_terrain); AssertBeforePublication(command, 0);
            _terrain.Tick(false, 10, 1f / 60); AssertBeforePublication(command, 1);
            var last = BuildQuotaDoneFixture.Stage(_terrain, 2); // Second check observes iterator exhaustion.
            Assert.That(last.Checks, Is.EqualTo(2)); Assert.That(last.Mutation.Added, Is.EqualTo(1));
            Assert.That((bool)BuildQuotaDoneFixture.Get(command, "Done"), Is.True);
            AssertBeforePublication(command, 1); BuildQuotaDoneFixture.StartPublication(_terrain, last);
            yield return BuildQuotaDoneFixture.WaitReady(_terrain); AssertBeforePublication(command, 1);
            _terrain.Tick(false, 11, 1f / 60);
            AssertFinal(command, 2, 1, 2);
            CollectionAssert.AreEqual(new[] { "terminal", "commit", "terminal", "mutation", "callback", "commit" }, _order);
        }
    }

    static class BuildQuotaDoneFixture
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static object Get(object target, string field) => target.GetType().GetField(field, Fields).GetValue(target);
        public static void Set(object target, string field, object value) => target.GetType().GetField(field, Fields).SetValue(target, value);
        public static object Call(object target, string method, params object[] arguments)
        {
            var selected = target.GetType().GetMethods(Fields).Single(m => m.Name == method && m.GetParameters().Length == arguments.Length);
            try { return selected.Invoke(target, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        public static int QueueCount(BlockTerrain terrain) => ((IEnumerable)Get(terrain, "_commands")).Cast<object>().Count();
        public static object Head(BlockTerrain terrain) => ((IEnumerable)Get(terrain, "_commands")).Cast<object>().Single();
        public static string GravityDiagnostics(BlockTerrain terrain, BlockWorld next, int checks)
        {
            double now = (double)Get(terrain, "_now");
            var journal = Get(terrain, "_gravityJournal");
            var motions = ((IDictionary)Get(journal, "Motions")).Values.Cast<object>();
            var runs = next.Falling.Values.Select(run => $"run={run.Id} offset={run.Offset(now):R} landing={run.LandingOffset:R} known={run.LandingKnown}");
            var work = motions.Select(motion => motion == null ? "motion=completed"
                : $"column={Get(motion, "Column")} phase={Get(motion, "Phase")} proof={Get(motion, "ProofKnown")} landing={Get(motion, "Landing")} validate={Get(motion, "Validate")}");
            return $"Actual gravity stage: checks={checks}, now={now:R}, indexedMotions={((IList)Get(terrain, "_cohortMotionOrder")).Count}, "
                + $"window={((BlockPublicationWindow)Get(journal, "Window")).MaximumChangedKeys}; "
                + string.Join("; ", runs.Concat(work));
        }
        public sealed class Batch
        {
            public BlockWorld Next;
            public HashSet<Vector3Int> Changed;
            public IList Results;
            public int Checks;
            public BlockMutationResult Mutation => (BlockMutationResult)Results[0].GetType().GetField("Item2").GetValue(Results[0]);
        }
        public static Batch Stage(BlockTerrain terrain, int maxChecks, HashSet<Vector3Int> changed = null)
        {
            Assert.That(Get(terrain, "_pending"), Is.Null, "Stage only against a fully published source");
            var command = typeof(BlockTerrain).GetNestedType("Command", BindingFlags.NonPublic);
            var pair = typeof(ValueTuple<,>).MakeGenericType(command, typeof(BlockMutationResult));
            var batch = new Batch { Next = terrain.World.Clone(), Changed = changed ?? new HashSet<Vector3Int>(),
                Results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pair)) };
            if ((bool)Get(terrain, "_cohortIndexPending"))
            {
                // Remote publication leaves motion indexing to the authority stage.
                // Establish that prerequisite in the same mixed publication window
                // before applying this fixture's independent command-check budget.
                Call(terrain, "StartWindowGravityJournal", batch.Next, Call(terrain, "CaptureNewPublicationWindow"));
                object[] indexing = { batch.Next, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, 1024 };
                Call(terrain, "IndexGravityWork", indexing);
                Assert.That((bool)Get(terrain, "_cohortIndexPending"), Is.False);
            }
            object[] arguments = { batch.Next, batch.Changed, batch.Results, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, maxChecks };
            Call(terrain, "StageCommands", arguments); batch.Checks = (int)arguments[3];
            Assert.That(batch.Results.Count, Is.EqualTo(1));
            Assert.That(batch.Checks, Is.LessThanOrEqualTo(maxChecks));
            Assert.That(Get(terrain, "_stageBuildWorld"), Is.Null, "The stage query world cannot survive an iterator yield");
            return batch;
        }
        public static void StartPublication(BlockTerrain terrain, Batch batch)
        {
            Assert.That(batch.Changed.Count, Is.GreaterThan(0)); Assert.That(batch.Changed.Count, Is.LessThanOrEqualTo(8));
            batch.Next.Seal(terrain.Revision + 1, batch.Changed);
            Call(terrain, "BeginTransaction", batch.Next, batch.Changed, false, batch.Results);
            Assert.That(Get(terrain, "_pending"), Is.Not.Null);
        }
        public static IEnumerator WaitReady(BlockTerrain terrain)
        {
            for (int frame = 0; frame < 1000; frame++)
            {
                Call(terrain, "AdvancePending", false);
                if (terrain.InspectPendingTransaction().phase == BlockTerrain.PendingWallPhase.ReadyToPublish) yield break;
                Assert.That(Get(terrain, "_pending"), Is.Not.Null, "The real async transaction failed or published early");
                yield return null;
            }
            Assert.Fail("Real generation/upload/Bake did not reach readiness within 1000 frames");
        }
    }
}
#endif
