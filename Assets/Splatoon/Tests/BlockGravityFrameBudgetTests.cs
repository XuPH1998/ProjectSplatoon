#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Integer-budget tests use an explicit large clock policy to isolate the
    // shared check ledger. The separate clock test uses the real 3ms default.
    // These are Editor contracts, not formal Player performance acceptance.
    public sealed class BlockGravityFrameBudgetTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Shared real terrain gravity work budget");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            yield return NextNativeFrame(); // Initialize has its own genuine terrain clock.
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RepeatedRealTicksShareTheCheckLedgerAndResumeOnTheNextUnityFrame()
        {
            UseIntegerIsolationPolicy();
            // Real committed gravity column work, not a fake budget model.
            // Empty columns prove absence without starting bake/worker Tasks.
            for (int z = 0; z < 40; z++) for (int x = 0; x < 50; x++)
                Call("QueueGravity", new Vector3Int(x - 25, 0, z - 20));
            long workBefore = _terrain.GravityWorkChecks;
            long stagesBefore = _terrain.AuthorityStageCount;
            long deferredBefore = _terrain.GravityFrameBudgetDeferrals;
            int frame = Time.frameCount, previous = 0;
            for (int entry = 0; entry < 64; entry++)
            {
                _terrain.Tick(true, 10 + entry / 6000d, 1f / 60);
                Assert.That(Time.frameCount, Is.EqualTo(frame));
                int charged = _terrain.GravityChecksThisFrame;
                Assert.That(charged, Is.GreaterThanOrEqualTo(previous));
                Assert.That(charged, Is.LessThanOrEqualTo(BlockPublicationPolicy.GravityChecksAlone));
                Assert.That(_terrain.GravityWorkChecks - workBefore, Is.EqualTo(charged),
                    "Every admitted gravity check reached through real Tick must reach the one shared frame ledger.");
                previous = charged;
            }
            Assert.That(_terrain.AuthorityStageCount - stagesBefore, Is.GreaterThan(1),
                "The fixture must actually enter multiple authority stages in the same Unity frame.");
            Assert.That(previous, Is.GreaterThan(0));
            Assert.That(_terrain.GravityFrameBudgetDeferrals, Is.GreaterThan(deferredBefore));
            Assert.That(_terrain.UnstableColumns, Is.GreaterThan(0), "The work queue must outlive this frame's allowance.");
            long beforeNextFrame = _terrain.GravityWorkChecks;
            yield return NextNativeFrame();
            Assert.That(Time.frameCount, Is.GreaterThan(frame));
            Assert.That(_terrain.GravityChecksThisFrame, Is.Zero, "The public witness cannot expose the prior frame as current.");
            _terrain.Tick(true, 11, 1f / 60);
            Assert.That(_terrain.GravityChecksThisFrame, Is.GreaterThan(0));
            Assert.That(_terrain.GravityWorkChecks - beforeNextFrame, Is.EqualTo(_terrain.GravityChecksThisFrame));
            Assert.That(_terrain.GravityChecksThisFrame, Is.LessThanOrEqualTo(BlockPublicationPolicy.GravityChecksAlone));
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.Revision, Is.Zero, "Proof-only queue progress must not invent a terrain publication.");
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator DifferentProductionSpendEntrancesShareCommandsCapAndDiscardDoesNotRefundWork()
        {
            UseIntegerIsolationPolicy();
            // A genuine paid command makes the foreground cap apply. It need
            // not execute in this focused admission contract.
            _terrain.QueueDestroy(new Vector3(1000, 1, 1000), .001f, 0, 1, true);
            long workBefore = _terrain.GravityWorkChecks;
            using (OpenProductionTimingWindow())
            {
                Call("StartGravityJournal", _terrain.World.Clone());
                Assert.That(Spend("SpendGravity", 1000), Is.True);
                Assert.That(Spend("SpendDependentPreparation", 1000), Is.True);
                Assert.That(Spend("SpendBuildPreparation", 1000), Is.True);
                Assert.That(Spend("SpendHistoricalRecovery", 1000), Is.True);
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(4000));
                Assert.That(_terrain.GravityWorkChecks - workBefore, Is.EqualTo(4000));
                var discardedJournal = Read<object>(_terrain, "_gravityJournal");
                Assert.That(Read<long>(discardedJournal, "AdmittedWork"), Is.EqualTo(4000),
                    "All successful production preparation entrances charge the same active journal.");
                Assert.That(Spend("SpendGravity", 100), Is.False,
                    "Fresh phase-local counters cannot acquire another 4096 checks in the same frame.");

                Call("StartGravityJournal", _terrain.World.Clone()); // Discard the prior private journal.
                var freshJournal = Read<object>(_terrain, "_gravityJournal");
                Assert.That(freshJournal, Is.Not.SameAs(discardedJournal));
                Assert.That(Read<long>(freshJournal, "AdmittedWork"), Is.Zero);
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(4000));
                Assert.That(Spend("SpendBuildPreparation", 97), Is.False);
                Assert.That(Read<long>(freshJournal, "AdmittedWork"), Is.Zero,
                    "Rejected work cannot borrow discarded preparation credit from the lifetime CPU counter.");
                Assert.That(Read<long>(discardedJournal, "AdmittedWork"), Is.EqualTo(4000));
            }
            _terrain.Clear(_terrain.Round + 1); // Public cancellation and round reset.
            Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(4000),
                "Actual CPU work remains charged even though the draft, command and round were discarded.");
            using (OpenProductionTimingWindow())
            {
                // The command is gone, so this same ledger now uses the 8192
                // no-command ceiling; it receives only its remaining allowance.
                Assert.That(Spend("SpendHistoricalRecovery", BlockPublicationPolicy.GravityChecksAlone), Is.False);
                int frameBeforeRemainder = Time.frameCount, checksBeforeRemainder = _terrain.GravityChecksThisFrame;
                bool commandBeforeRemainder = _terrain.InspectCommandHead().present;
                double frameLimit = _terrain.PublicationPolicy.FrameSynchronousMilliseconds;
                double clockBeforeRemainder = _terrain.TerrainSynchronousMilliseconds;
                bool remainderAdmitted = Spend("SpendDependentPreparation", BlockPublicationPolicy.GravityChecksAlone - 4000);
                Assert.That(remainderAdmitted, Is.True,
                    $"frame={frameBeforeRemainder}->{Time.frameCount} checks={checksBeforeRemainder}->{_terrain.GravityChecksThisFrame}"
                    + $" terrainMs={clockBeforeRemainder:R}->{_terrain.TerrainSynchronousMilliseconds:R} frameLimitMs={frameLimit:R} commandPresent={commandBeforeRemainder}");
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(BlockPublicationPolicy.GravityChecksAlone));
                Assert.That(Spend("SpendGravity", 1), Is.False);
            }
            yield return NextNativeFrame();
            Assert.That(_terrain.GravityChecksThisFrame, Is.Zero);
            using (OpenProductionTimingWindow())
            {
                Assert.That(Spend("SpendGravity", 64), Is.True);
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(64));
            }
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RealThreeMillisecondTerrainClockAllowsOneFreshBoundedUnitThenDefersAllOtherEntrances()
        {
            _terrain.PublicationPolicy = BlockPublicationPolicy.CandidateDefault();
            Assert.That(_terrain.PublicationPolicy.FrameSynchronousMilliseconds, Is.EqualTo(3));
            int frame = Time.frameCount;
            long workBefore = _terrain.GravityWorkChecks;
            long deferredBefore = _terrain.GravityFrameBudgetDeferrals;
            using (OpenProductionTimingWindow())
            {
                WaitForActualTerrainClock(4);
                Assert.That(_terrain.TerrainSynchronousMilliseconds, Is.GreaterThanOrEqualTo(3));
                Assert.That(Spend("SpendGravity", 64), Is.True,
                    "An expired outer clock still admits one bounded unit on a fresh gravity frame.");
                Assert.That(Spend("SpendBuildPreparation", 1), Is.False);
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(64));
                Assert.That(_terrain.GravityWorkChecks - workBefore, Is.EqualTo(64));
            }
            // Another actual timing scope is not another frame and cannot reset
            // the outer terrain clock or the gravity check ledger.
            using (OpenProductionTimingWindow())
                Assert.That(Spend("SpendDependentPreparation", 1), Is.False);
            Assert.That(_terrain.GravityFrameBudgetDeferrals - deferredBefore, Is.EqualTo(2));
            Assert.That(Time.frameCount, Is.EqualTo(frame));
            yield return NextNativeFrame();
            Assert.That(_terrain.GravityChecksThisFrame, Is.Zero);
            using (OpenProductionTimingWindow())
            {
                WaitForActualTerrainClock(4);
                Assert.That(Spend("SpendHistoricalRecovery", 64), Is.True);
                Assert.That(Spend("SpendGravity", 1), Is.False);
                Assert.That(_terrain.GravityChecksThisFrame, Is.EqualTo(64));
            }
        }

        static IEnumerator NextNativeFrame()
        {
            int frame = Time.frameCount;
            double deadline = Time.realtimeSinceStartupAsDouble + 3;
            do { yield return null; }
            while (Time.frameCount == frame && Time.realtimeSinceStartupAsDouble < deadline);
            Assert.That(Time.frameCount, Is.Not.EqualTo(frame),
                "The frame-budget fixture requires an advancing native frame, rather than another Editor test-runner update.");
        }

        // Isolate integer ledger admission from elapsed Editor wall time.
        // The separate real-clock test retains the 3 ms production policy.
        void UseIntegerIsolationPolicy()
            => _terrain.PublicationPolicy = new BlockPublicationPolicy(141, 8, 32, 32, 8, 32, double.MaxValue);

        IDisposable OpenProductionTimingWindow()
        {
            Set("_stagePublicationWindow", _terrain.PublicationPolicy.Capture(BlockPublicationOperation.Gravity));
            return new WindowScope(this, (IDisposable)Call("MeasurePublicationWork"));
        }
        sealed class WindowScope : IDisposable
        {
            BlockGravityFrameBudgetTests _owner;
            IDisposable _inner;
            public WindowScope(BlockGravityFrameBudgetTests owner, IDisposable inner) { _owner = owner; _inner = inner; }
            public void Dispose()
            {
                var owner = _owner; var inner = _inner; _owner = null; _inner = null;
                if (owner == null) return;
                try { inner.Dispose(); } finally { owner.Set("_stagePublicationWindow", null); }
            }
        }
        bool Spend(string method, int cost)
        {
            // The real Spend entry points perform admission; this test does not
            // reimplement the ledger, its cap selection, or its time decision.
            var arguments = new object[] { 0, cost, BlockPublicationPolicy.GravityChecksAlone, new Stopwatch(), 1000d };
            bool admitted = (bool)Call(method, arguments);
            Assert.That((int)arguments[0], Is.EqualTo(admitted ? cost : 0));
            return admitted;
        }
        void WaitForActualTerrainClock(double minimumMilliseconds)
        {
            var guard = Stopwatch.StartNew();
            while (_terrain.TerrainSynchronousMilliseconds < minimumMilliseconds && guard.ElapsedMilliseconds < 1000)
                Thread.SpinWait(32);
            Assert.That(_terrain.TerrainSynchronousMilliseconds, Is.GreaterThanOrEqualTo(minimumMilliseconds));
        }
        object Call(string name, params object[] arguments)
        {
            var method = typeof(BlockTerrain).GetMethod(name, Private);
            Assert.That(method, Is.Not.Null, name);
            return method.Invoke(_terrain, arguments);
        }
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null, name);
            var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(owner);
        }
        void Set(string name, object value)
        {
            var field = typeof(BlockTerrain).GetField(name, Private);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(_terrain, value);
        }
    }
}
#endif
