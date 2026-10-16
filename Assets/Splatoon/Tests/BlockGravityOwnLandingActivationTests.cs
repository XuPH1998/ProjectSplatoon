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
    // Reports-only, not compiled or executed. The platform oracle uses natural
    // build/destroy/authority entry points. Reflection only registers fixed map
    // colliders and reads real pending certificates; it never edits scheduling,
    // invokes private gravity, or supplies manufactured World/journal state.
    public sealed class BlockGravityOwnLandingActivationTests
    {
        const double Epoch = 10, BuildAt = 10.1;
        const int PumpLimit = 4000, LoadAttacks = 48, LoadCellsPerAttack = 4;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly Vector3Int Floor = new(2, 0, 2), LowerSource = new(2, 20, 2);
        static readonly Vector3Int PlatformCell = new(2, 31, 2), UpperSource = new(2, 32, 2);
        GameObject _root;
        BlockTerrain _terrain;
        BlockWorld _retainedSource, _peer, _certifiedActivationWorld;
        byte[] _retainedBytes;
        ulong _lowerId;
        double _now, _firstActivationAt = double.NaN;
        bool _delayedRealPending, _upperProofInActivationPending, _firstActivation;
        int _upperBuildCallbacks, _upperDestroyCallbacks, _loadQueued, _loadCompleted;
        readonly int[] _loadCallbacks = new int[LoadAttacks];
        readonly HashSet<Vector3Int> _loadCells = new();
        long _commandsBeforeLoad, _gravityChecksBeforeLoad;
        bool _firstOwnRejectionAttempted;
        long _observedOwnDeferrals;
        string _firstOwnRejection = "not observed";

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Natural linked upper own-floor activation contract");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            RegisterMapBox("Fixed map ground top zero", new Vector3(0, -.05f, 0), new Vector3(16, .1f, 16));
            RegisterMapBox("Fixed independent platform top 3.1m", new Vector3(.25f, 3.05f, .25f), new Vector3(.8f, .1f, .8f));
            Physics.SyncTransforms();
            CaptureFirstOwnActivationRejection("before Tick");
            _now = Epoch; _terrain.Tick(true, _now, .016f);
            CaptureFirstOwnActivationRejection("after Tick");
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator LinkedUpperProvesItsOwnPlatformBeforeDelayedFirstActivationUnderRealCommandLoad()
        {
            int built = 0, destroyed = 0;
            _terrain.QueueBuild(BuildPoint(Floor), Vector3.up, 2, 21, 0, false,
                result => { built++; Assert.That(result.Added, Is.EqualTo(21)); });
            yield return Pump(Epoch, () => built == 1 && _terrain.PendingTransactions == 0, false, "initial lower build");
            _terrain.QueueDestroy(new Vector3(.25f, 1.05f, .25f), .8501f, 0, 1, true,
                result => { destroyed++; Assert.That(result.RemovedEnemy, Is.EqualTo(19)); }, 19, 0);
            yield return Pump(Epoch, () => destroyed == 1 && _terrain.PendingTransactions == 0
                && _terrain.World.Cohorts.Values.Any(c => c.Bottom == LowerSource
                    && c.Phase == FallingCohortPhase.Falling), false, "lower support removal and first activation");
            var lower = _terrain.World.Cohorts.Values.Single(c => c.Bottom == LowerSource);
            _lowerId = lower.Id;
            Assert.That(lower.TopExclusive, Is.EqualTo(21));
            Assert.That(lower.StartedAt, Is.EqualTo(Epoch));
            Assert.That(lower.LandingKnown, Is.True);
            Assert.That(lower.LandingOffset, Is.EqualTo(1.9f).Within(1e-6));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2));
            Assert.That(_terrain.PinkCount, Is.Zero);
            _retainedSource = _terrain.World; _retainedBytes = _retainedSource.EncodeSnapshot();
            _peer = BlockWorld.DecodeSnapshot(_retainedBytes);
            _commandsBeforeLoad = _terrain.CommandsQueued;
            _gravityChecksBeforeLoad = _terrain.GravityWorkChecks;
            _terrain.PublishedCommit += ObservePublication;
            try
            {
                _terrain.QueueBuild(BuildPoint(PlatformCell), Vector3.up, 1, 2, 0, false,
                    result => { _upperBuildCallbacks++; Assert.That(result.Added, Is.EqualTo(2)); });
                yield return Pump(BuildAt, () => _upperBuildCallbacks == 1 && _terrain.PendingTransactions == 0, false, "upper platform build");
                Assert.That(_terrain.World.TryGet(PlatformCell, out var platformBlock), Is.True);
                Assert.That(_terrain.World.TryGet(UpperSource, out var upperBlock), Is.True);
                Assert.That(platformBlock.Owner, Is.EqualTo(1));
                Assert.That(upperBlock.Owner, Is.EqualTo(1));
                Assert.That(_terrain.World.Cohorts.ContainsKey(_lowerId), Is.True);

                // An ordinary zero-radius enemy quota removes Y31 only. The
                // source Y32 center is 3.25m; its .125m immediate-support ray
                // cannot reach the fixed platform top at 3.1m.
                _terrain.QueueDestroy(BlockWorld.Center(PlatformCell), 0, 0, 2, true,
                    result => { _upperDestroyCallbacks++; Assert.That(result.RemovedEnemy, Is.EqualTo(1)); }, 1, 0);
                _now = BuildAt;
                for (int frame = 0; frame < PumpLimit && !_firstActivation; frame++)
                {
                    QueueOneLoadAttack();
                    InspectRealUpperPending();
                    CaptureFirstOwnActivationRejection("before Tick");
                    _terrain.Tick(true, _now, .016f);
                    CaptureFirstOwnActivationRejection("after Tick");
                    AssertVisibleWorld(_terrain.World, _now);
                    InspectRealUpperPending();
                    yield return null;
                }
                Assert.That(_firstActivation, Is.True, "The natural linked upper must actually publish Falling.");
                Assert.That(_delayedRealPending, Is.True,
                    "The oracle must advance time by at least .16 through a real upper metadata/activation pending.");
                Assert.That(_upperProofInActivationPending, Is.True,
                    "The activation transaction must carry the upper query certificate at Y3.25 with minimum cell31.");
                Assert.That(_upperDestroyCallbacks, Is.EqualTo(1));
                Assert.That(_firstActivationAt, Is.GreaterThanOrEqualTo(BuildAt + .16 - 1e-9));
                Assert.That(_terrain.CommandsQueued, Is.GreaterThan(_commandsBeforeLoad + 2));
                Assert.That(_terrain.GravityWorkChecks, Is.GreaterThan(_gravityChecksBeforeLoad));

                // A .1m free fall reaches the platform in about .143s. Continue
                // command and pending work through at least .30s after actual
                // first activation, sampling visible bodies after every Tick.
                for (int frame = 0; frame < PumpLimit
                    && (_now < _firstActivationAt + .30 || _loadQueued < LoadAttacks); frame++)
                {
                    QueueOneLoadAttack();
                    _now = Math.Min(_firstActivationAt + .35, _now + .02);
                    CaptureFirstOwnActivationRejection("before Tick");
                    _terrain.Tick(true, _now, .016f);
                    CaptureFirstOwnActivationRejection("after Tick");
                    AssertVisibleWorld(_terrain.World, _now);
                    yield return null;
                }
                Assert.That(_now, Is.GreaterThanOrEqualTo(_firstActivationAt + .30));
                Assert.That(_loadQueued, Is.EqualTo(LoadAttacks));
                yield return Pump(Epoch + 2, () => _terrain.PendingTransactions == 0
                    && _terrain.World.Falling.Count == 0 && _terrain.World.Cohorts.Count == 0, false, "final complete landing");
            }
            finally { _terrain.PublishedCommit -= ObservePublication; }

            Assert.That(_loadCompleted, Is.EqualTo(LoadAttacks));
            Assert.That(_loadCallbacks.All(count => count == 1), Is.True);
            Assert.That(_upperBuildCallbacks, Is.EqualTo(1));
            Assert.That(_upperDestroyCallbacks, Is.EqualTo(1));
            AssertCell(PlatformCell, 1);
            Assert.That(_terrain.World.TryGet(UpperSource, out _), Is.False);
            AssertCell(Floor, 2); AssertCell(Floor + Vector3Int.up, 2);
            foreach (var cell in _loadCells) AssertCell(cell, 2);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(_terrain.BlueCount, Is.EqualTo(2 + LoadAttacks * LoadCellsPerAttack));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            Assert.That(_terrain.StaticProofInvalidations, Is.Zero,
                "The independent platform remains fixed throughout this natural oracle.");
            CollectionAssert.AreEqual(_retainedBytes, _retainedSource.EncodeSnapshot());
            AssertVisibleWorld(_terrain.World, _now);
            CollectionAssert.AreEqual(_terrain.World.EncodeSnapshot(), _peer.EncodeSnapshot());
        }

        // Include the existing Play Mode setup/teardown reload in the framework timeout.
        // The standalone managed assertions and all gameplay/Player timing gates are unchanged.
        [TestCase(false)]
        [TestCase(true)]
        [Timeout(600000)]
        public void UnknownOwnLandingCannotBePreparedForFirstActivationEvenWithALiveLower(bool linked)
        {
            // Standalone managed gate fixture, not natural terrain/proof evidence.
            var world = new BlockWorld(new Vector2(32, 64));
            FallingCohort lower = null;
            if (linked)
            {
                world.Set(LowerSource, BlockCell.Create(2));
                lower = world.BeginCohort(LowerSource, 1);
                world.DetachCohortPiece(lower.Id, 1, Epoch, out _);
                lower = world.EditCohort(lower.Id, out _);
                lower.LandingKnown = true; lower.LandingOffset = 1.9f;
                world.PrepareCohortForPublication(lower.Id, out _);
                world.ActivateRebasePendingCohorts(Epoch);
            }
            world.Set(UpperSource, BlockCell.Create(1));
            var upper = world.BeginCohort(UpperSource, 1);
            if (linked) world.SetCohortSupport(upper.Id, lower.Id, 1.1f, out _);
            world.DetachCohortPiece(upper.Id, 1, BuildAt, out _);
            upper = world.Cohorts[upper.Id];
            Assert.That(upper.Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
            Assert.That(upper.LandingKnown, Is.False);
            byte[] source = world.EncodeSnapshot();
            ulong nextRun = world.NextRunId, nextCohort = world.NextCohortId;
            world.PrepareCohortForPublication(upper.Id, out _);
            world.ActivateRebasePendingCohorts(BuildAt + .2);
            CollectionAssert.AreEqual(source, world.EncodeSnapshot());
            Assert.That(world.Cohorts[upper.Id].Phase, Is.EqualTo(FallingCohortPhase.AwaitActivation));
            Assert.That(world.Cohorts[upper.Id].LandingKnown, Is.False);
            Assert.That(world.NextRunId, Is.EqualTo(nextRun));
            Assert.That(world.NextCohortId, Is.EqualTo(nextCohort));
            Assert.That(world.PinkCount, Is.EqualTo(1));
            Assert.That(world.BlueCount, Is.EqualTo(linked ? 1 : 0));
        }

        void InspectRealUpperPending()
        {
            var pending = ReadField<object>(_terrain, "_pending");
            if (pending == null || _firstActivation) return;
            var next = ReadField<BlockWorld>(pending, "Next");
            var upper = next.Cohorts.Values.FirstOrDefault(c => c.Bottom == UpperSource);
            if (upper == null) return;
            if (!_delayedRealPending
                && (upper.Phase == FallingCohortPhase.Detaching || upper.Phase == FallingCohortPhase.AwaitActivation))
            {
                double createdAt = ReadField<double>(pending, "CreatedAt");
                _now = Math.Max(_now + .16, createdAt + .16);
                Assert.That(_now, Is.LessThanOrEqualTo(Epoch + .5),
                    "The separate lower must remain a live natural predecessor for first activation.");
                _delayedRealPending = true;
            }
            if (upper.Phase != FallingCohortPhase.AwaitActivation) return;
            var proofs = ReadField<IEnumerable>(pending, "StaticProofs");
            if (proofs == null) return;
            foreach (var proof in proofs)
            {
                var position = ReadField<Vector3>(proof, "Position");
                int minimum = ReadField<int>(proof, "Minimum");
                if (Mathf.Abs(position.x - .25f) < 1e-6 && Mathf.Abs(position.z - .25f) < 1e-6
                    && Mathf.Abs(position.y - 3.25f) < 1e-6 && minimum == 31)
                { _upperProofInActivationPending = true; _certifiedActivationWorld = next; }
            }
        }

        void ObservePublication(BlockTerrainCommit commit)
        {
            Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
            var world = _terrain.World;
            var upper = world.Cohorts.Values.FirstOrDefault(c => c.Bottom == UpperSource
                && c.Phase == FallingCohortPhase.Falling);
            if (!_firstActivation && upper != null)
            {
                Assert.That(_upperProofInActivationPending, Is.True,
                    "First activation cannot substitute a later floor certificate.");
                Assert.That(world, Is.SameAs(_certifiedActivationWorld),
                    "The exact first-activation pending World must carry the upper certificate.");
                Assert.That(upper.LandingKnown, Is.True);
                Assert.That(upper.LandingOffset, Is.EqualTo(.1f).Within(1e-6));
                Assert.That(upper.InitialOffset, Is.Zero.Within(1e-6));
                Assert.That(upper.InitialSpeed, Is.Zero.Within(1e-6));
                Assert.That(upper.StartedAt, Is.EqualTo(_now));
                Assert.That(upper.Offset(_now), Is.Zero.Within(1e-6));
                Assert.That(upper.LowerSupport, Is.Not.Null);
                Assert.That(upper.LowerSupport.Id, Is.EqualTo(_lowerId));
                Assert.That(upper.LowerSupport.Bottom, Is.EqualTo(LowerSource));
                Assert.That(upper.LowerSupport.TopExclusive, Is.EqualTo(21));
                Assert.That(upper.SupportClearance, Is.EqualTo(1.1f).Within(1e-6));
                Assert.That(upper.LowerSupport.Offset(_now) + upper.SupportClearance,
                    Is.GreaterThan(upper.LandingOffset + .5f));
                _firstActivationAt = _now; _firstActivation = true;
            }
            var snapshot = BlockWorld.DecodeSnapshot(world.EncodeSnapshot());
            var delta = _peer.WithDelta(world.EncodeDelta(_peer.Revision));
            CollectionAssert.AreEqual(world.EncodeSnapshot(), snapshot.EncodeSnapshot());
            CollectionAssert.AreEqual(world.EncodeSnapshot(), delta.EncodeSnapshot());
            AssertVisibleWorld(world, _now); AssertVisibleWorld(snapshot, _now); AssertVisibleWorld(delta, _now);
            _peer = delta;
            CollectionAssert.AreEqual(_retainedBytes, _retainedSource.EncodeSnapshot());
        }

        void AssertVisibleWorld(BlockWorld world, double now)
        {
            int pink = 0, blue = 0;
            var occupiedSources = new HashSet<Vector3Int>();
            foreach (var pair in world.Chunks)
                for (int index = 0; index < pair.Value.Cells.Length; index++)
                {
                    var cell = pair.Value.Cells[index]; if (!cell.Occupied) continue;
                    var source = BlockWorld.CellFromIndex(pair.Key, index);
                    Assert.That(occupiedSources.Add(source), Is.True);
                    AssertCellMetadata(cell);
                    if (cell.Owner == 1) { pink++; AssertPinkBottom(source, source.y * BlockWorld.CellSize); }
                    else { blue++; Assert.That(source == Floor || source == LowerSource
                        || source == Floor + Vector3Int.up || _loadCells.Contains(source), Is.True); }
                }
            foreach (var run in world.Falling.Values)
                for (int index = 0; index < run.Cells.Length; index++)
                {
                    var cell = run.Cells[index]; if (!cell.Occupied) continue;
                    var source = run.Bottom + Vector3Int.up * index;
                    Assert.That(occupiedSources.Add(source), Is.True);
                    AssertCellMetadata(cell);
                    if (cell.Owner == 1)
                    {
                        pink++; Assert.That(source, Is.EqualTo(UpperSource));
                        AssertPinkBottom(source, source.y * BlockWorld.CellSize - run.Offset(now));
                    }
                    else { blue++; Assert.That(source, Is.EqualTo(LowerSource)); }
                }
            Assert.That(world.PinkCount, Is.EqualTo(pink));
            Assert.That(world.BlueCount, Is.EqualTo(blue));
            Assert.That(pink, Is.LessThanOrEqualTo(2));
            if (_upperDestroyCallbacks == 1) Assert.That(pink, Is.EqualTo(1));
            if (_retainedSource != null) CollectionAssert.AreEqual(_retainedBytes, _retainedSource.EncodeSnapshot());
        }

        static void AssertPinkBottom(Vector3Int source, float physicalBottom)
        {
            Assert.That(source.x, Is.EqualTo(UpperSource.x));
            Assert.That(source.z, Is.EqualTo(UpperSource.z));
            Assert.That(physicalBottom, Is.GreaterThanOrEqualTo(3.1f - 1e-6),
                "The upper may never cross its unchanged independent static platform.");
        }
        static void AssertCellMetadata(BlockCell cell)
        { Assert.That(cell.Durability, Is.EqualTo(100)); Assert.That(cell.Flags, Is.Zero); }
        void AssertCell(Vector3Int source, byte team)
        {
            Assert.That(_terrain.World.TryGet(source, out var cell), Is.True);
            Assert.That(cell.Owner, Is.EqualTo(team)); AssertCellMetadata(cell);
        }
        void QueueOneLoadAttack()
        {
            if (_loadQueued == LoadAttacks) return;
            int attack = _loadQueued++;
            var bottom = new Vector3Int(40 + attack % 8 * 2, 0, 40 + attack / 8 * 2);
            for (int index = 0; index < LoadCellsPerAttack; index++) _loadCells.Add(bottom + Vector3Int.up * index);
            _terrain.QueueBuild(BuildPoint(bottom), Vector3.up, 2, LoadCellsPerAttack, 0, false,
                result =>
                {
                    _loadCallbacks[attack]++; _loadCompleted++;
                    Assert.That(result.Added, Is.EqualTo(LoadCellsPerAttack));
                    Assert.That(_loadCallbacks[attack], Is.EqualTo(1));
                });
        }
        IEnumerator Pump(double now, Func<bool> ready, bool supplyLoad, string phase)
        {
            _now = now;
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            {
                if (supplyLoad) QueueOneLoadAttack();
                CaptureFirstOwnActivationRejection("before Tick");
                _terrain.Tick(true, _now, .016f);
                CaptureFirstOwnActivationRejection("after Tick");
                if (_retainedSource != null) AssertVisibleWorld(_terrain.World, _now);
                yield return null;
            }
            bool reached = ready();
            Assert.That(reached, Is.True, reached
                ? "Bounded Editor scheduling reached the required natural state."
                : "Bounded Editor scheduling did not reach the required natural state; this is not a Player timing gate. "
                    + FailureState(phase));
        }
        // Read only after a failed original wait, outside all measured Tick work.
        string FailureState(string phase)
        {
            var command = _terrain.InspectCommandHead();
            var cohorts = string.Join(";", _terrain.World.Cohorts.Values.Select(c =>
                $"{c.Id}:bottom={c.Bottom}:top={c.TopExclusive}:phase={c.Phase}:landingKnown={c.LandingKnown}:landing={c.LandingOffset}:detach={c.DetachCursor}:lower={c.LowerSupport?.Id ?? 0}"));
            return $"phase={phase}; now={_now:R}; revision={_terrain.Revision}; pending={_terrain.PendingTransactions}; "
                + $"unstable={_terrain.UnstableColumns}; falling={_terrain.FallingCount}; pink={_terrain.PinkCount}; blue={_terrain.BlueCount}; "
                + $"queued={_terrain.CommandsQueued}; completed={_terrain.CommandsCompleted}; failed={_terrain.FailedTransactions}; "
                + $"commandChecks={_terrain.CommandWorkChecks}; gravityChecks={_terrain.GravityWorkChecks}; "
                + $"buildGuardStarts={_terrain.BuildOccupancyPreparationStarts}; buildRetries={_terrain.BuildPublicationRetries}; "
                + $"ownLandingDeferrals={_terrain.OwnLandingPublicationDeferrals}; staticInvalidations={_terrain.StaticProofInvalidations}; "
                + $"command={JsonUtility.ToJson(command)}; upperBuildCallbacks={_upperBuildCallbacks}; upperDestroyCallbacks={_upperDestroyCallbacks}; "
                + $"loadQueued={_loadQueued}; loadCompleted={_loadCompleted}; firstActivation={_firstActivation}; cohorts=[{cohorts}]; firstOwnActivationRejection=[{_firstOwnRejection}]";
        }
        // Only one readback; inspect before Tick cleanup as well as immediately after Tick.
        void CaptureFirstOwnActivationRejection(string boundary)
        {
            if (_firstOwnRejectionAttempted) return;
            long current = _terrain.OwnLandingPublicationDeferrals;
            if (current <= _observedOwnDeferrals) return;
            long delta = current - _observedOwnDeferrals; _observedOwnDeferrals = current;
            _firstOwnRejectionAttempted = true;
            try
            {
                var retired = CaptureField<IEnumerable>(_terrain, "_retired").Cast<object>().ToArray();
                if (retired.Length == 0)
                    _firstOwnRejection = $"unreadable: retired Pending already absent; boundary={boundary}; deferrals={current}; delta={delta}";
                else
                {
                    // Read the last actually retained Pending, never the published World.
                    // Multiple deferrals or no reproduced eligible rejection leave association unverified.
                    var pending = CaptureField<object>(retired[retired.Length - 1], "Pending");
                    var next = CaptureField<BlockWorld>(pending, "Next");
                    var journal = CaptureField<object>(pending, "Gravity");
                    var own = CaptureField<IDictionary>(journal, "OwnActivationProofs");
                    var prepared = CaptureField<HashSet<ulong>>(next, "_preparedCohortIds");
                    var staged = CaptureField<Dictionary<Vector2Int, uint>>(journal, "Generations");
                    var committed = CaptureField<Dictionary<Vector2Int, uint>>(_terrain, "_landingGeneration");
                    var rows = new List<string>(); bool eligibleRejection = false;
                    foreach (DictionaryEntry entry in own)
                    {
                        ulong id = (ulong)entry.Key; var proof = entry.Value;
                        bool exists = next.Cohorts.TryGetValue(id, out var cohort), isPrepared = prepared.Contains(id);
                        if (!exists || !isPrepared)
                        { rows.Add($"id={id}:skipped=true:exists={exists}:prepared={isPrepared}"); continue; }
                        var column = CaptureField<Vector2Int>(proof, "Column");
                        int sourceBottom = CaptureField<int>(proof, "SourceBottom"), sourceTop = CaptureField<int>(proof, "SourceTop");
                        int landing = CaptureField<int>(proof, "Landing");
                        uint proofGeneration = CaptureField<uint>(proof, "Generation");
                        bool known = CaptureField<bool>(proof, "ProofKnown");
                        var native = CaptureField<object>(proof, "NativeProof");
                        bool hasStaged = staged.TryGetValue(column, out uint stagedGeneration);
                        bool hasCommitted = committed.TryGetValue(column, out uint committedGeneration);
                        uint generation = hasStaged ? stagedGeneration : hasCommitted ? committedGeneration : 0;
                        bool g1 = !known, g2 = native == null, g3 = proofGeneration != generation;
                        bool g4 = !cohort.LandingKnown, g5 = cohort.NeedsLandingProof;
                        bool g6 = cohort.Bottom.y != sourceBottom, g7 = cohort.TopExclusive > sourceTop;
                        // Preserve the runtime's inline expression; do not round expected first.
                        bool g8 = cohort.LandingOffset != (cohort.Bottom.y - landing) * BlockWorld.CellSize;
                        float storedExpected = (cohort.Bottom.y - landing) * BlockWorld.CellSize;
                        double wideExpected = (double)(cohort.Bottom.y - landing) * (double)BlockWorld.CellSize;
                        bool rejected = g1 || g2 || g3 || g4 || g5 || g6 || g7 || g8;
                        eligibleRejection |= rejected;
                        rows.Add($"id={id}:skipped=false:exists={exists}:prepared={isPrepared}:guards={g1},{g2},{g3},{g4},{g5},{g6},{g7},{g8}:rejected={rejected}"
                            + $":column={column}:sourceBottom={sourceBottom}:sourceTop={sourceTop}:landing={landing}:proofKnown={known}:nativePresent={native != null}"
                            + $":proofGeneration={proofGeneration}:hasStaged={hasStaged}:stagedGeneration={stagedGeneration}:hasCommitted={hasCommitted}:committedGeneration={committedGeneration}:resolvedGeneration={generation}"
                            + $":bottom={cohort.Bottom}:top={cohort.TopExclusive}:detach={cohort.DetachCursor}:phase={cohort.Phase}:landingKnown={cohort.LandingKnown}:needsLandingProof={cohort.NeedsLandingProof}"
                            + $":capSingle={cohort.LandingOffset:R}:capAsDouble={(double)cohort.LandingOffset:R}:capBits={FloatBits(cohort.LandingOffset)}:expectedStored={storedExpected:R}:expectedBits={FloatBits(storedExpected)}"
                            + $":cellSizeBits={FloatBits(BlockWorld.CellSize)}:expectedWide={wideExpected:R}:wideDelta={(double)cohort.LandingOffset - wideExpected:R}");
                    }
                    bool associationVerified = delta == 1 && own.Count != 0 && eligibleRejection;
                    _firstOwnRejection = $"readable=true; retained=true; boundary={boundary}; deferrals={current}; delta={delta}; associationVerified={associationVerified}; "
                        + $"selection=last-retired-at-observation; retainedCount={retired.Length}; pendingIdentity={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pending)}; "
                        + $"epoch={CaptureField<uint>(pending, "Epoch")}; createdAt={CaptureField<double>(pending, "CreatedAt"):R}; nextRevision={next.Revision}; proofs={own.Count}; rows=[{string.Join(";", rows)}]";
                }
            }
            catch (Exception exception)
            { _firstOwnRejection = $"unreadable: {exception.GetType().Name}: {exception.Message}; boundary={boundary}; deferrals={current}; delta={delta}"; }
            Debug.Log("[Own activation first rejected Pending] " + _firstOwnRejection);
        }
        static T CaptureField<T>(object owner, string name)
        {
            if (owner == null) throw new InvalidOperationException("Missing diagnostic owner for " + name);
            var field = owner.GetType().GetField(name, Fields);
            if (field == null) throw new MissingFieldException(owner.GetType().FullName, name);
            return (T)field.GetValue(owner);
        }
        static string FloatBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("X8");
        void RegisterMapBox(string label, Vector3 position, Vector3 size)
        {
            var map = new GameObject(label); map.transform.SetParent(_root.transform, false); map.transform.position = position;
            var collider = map.AddComponent<BoxCollider>(); collider.size = size;
            var field = typeof(BlockTerrain).GetField("_staticColliders", Fields);
            Assert.That(field, Is.Not.Null); ((HashSet<Collider>)field.GetValue(_terrain)).Add(collider);
        }
        static T ReadField<T>(object owner, string name)
        {
            var field = owner.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, "Required read-only fixture field: " + name); return (T)field.GetValue(owner);
        }
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
    }
}
#endif
