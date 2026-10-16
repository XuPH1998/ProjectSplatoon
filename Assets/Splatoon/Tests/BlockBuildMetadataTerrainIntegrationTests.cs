#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // Reports-only candidate. Real-paid tests use actual QueueBuild/Tick and
    // asynchronous native collision publication. Helper tests deliberately use
    // reflection to isolate exact budget, alias, writer and cancellation edges.
    // Reuse the existing robust worker/native cleanup rather than introduce a
    // second cleanup framework. No production toggle or widened budget is added.
    [Serializable]
    public sealed class BlockBuildMetadataTerrainIntegrationTests
    {
        const double Epoch = 10;
        const int Limit = 3000;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        BlockBuildShallowColumnReplayCandidateTests _fixture;
        BlockTerrain _terrain;
        // UTF restores the test object with JsonUtility after mode changes; locals
        // and nested IEnumerator instances are not part of that saved context.
        [SerializeField] byte[] _paidCandidate, _paidOracle;
        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _paidCandidate = _paidOracle = null;
            yield return CreateFixtureAfterEnteringPlayMode();
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            yield return CleanupFixtureBeforeExitingPlayMode();
            _paidCandidate = _paidOracle = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        IEnumerator CreateFixtureAfterEnteringPlayMode()
        {
            _fixture = new BlockBuildShallowColumnReplayCandidateTests();
            yield return _fixture.SetupAfterEnteringPlayMode();
            _terrain = Read<BlockTerrain>(_fixture, "_terrain");
        }
        IEnumerator CleanupFixtureBeforeExitingPlayMode()
        {
            if (_fixture != null) yield return _fixture.TeardownBeforeExitingPlayMode();
            _fixture = null; _terrain = null;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidFloorCapOnAPartiallyDetachedColumnReacquiresTheFinalPieceCertificate()
        {
            // Public world data describes a legitimate intermediate 17-cell
            // detachment. No private ProofKnown/NativeProof state is injected.
            var bottom = new Vector3Int(2, 10, 2);
            var paidCell = bottom + Vector3Int.down;
            var finalCell = bottom + Vector3Int.up * 16;
            var seed = PartialBuildCapColumn(bottom, out ulong cohortId);
            yield return Install(seed);
            var source = _terrain.World;
            byte[] frozen = source.EncodeSnapshot();
            CollectionAssert.AreEqual(frozen, BlockWorld.DecodeSnapshot(frozen).EncodeSnapshot());
            Assert.That(source.PinkCount, Is.EqualTo(9));
            Assert.That(source.BlueCount, Is.EqualTo(17));
            Assert.That(source.Cohorts[cohortId].DetachCursor, Is.EqualTo(finalCell.y));
            Assert.That(source.Cohorts[cohortId].Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(source.Falling.Values.Sum(r => r.Cells.Count(c => c.Occupied)), Is.EqualTo(16));
            AssertBuildCapStableSide(finalCell, 2);

            var token = Token(4501);
            int reserved = token.ReserveBuild(1, false);
            Assert.That(reserved, Is.EqualTo(1));
            int callbacks = 0, paidPublications = 0, certifiedDetaches = 0, reservations = 0;
            BlockMutationResult outcome = default;
            uint paidGeneration = 0;
            bool sawCapReset = false;
            long failures = _terrain.FailedTransactions;
            long queued = _terrain.CommandsQueued, completed = _terrain.CommandsCompleted;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Remember();
                var world = _terrain.World;
                AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                Assert.That(world.BlueCount, Is.EqualTo(17), "Detaching, activation and settlement conserve every source cell.");
                var paidProof = Read<object>(_terrain, "_lastBuildOccupancyProof");
                if (paidProof != null && ReferenceEquals(Read<BlockWorld>(paidProof, "Next"), world))
                {
                    foreach (DictionaryEntry pair in Read<IDictionary>(paidProof, "Caps"))
                    {
                        if (!Read<bool>(pair.Key, "Cohort") || Read<ulong>(pair.Key, "Id") != cohortId
                            || !Read<bool>(pair.Value, "PublicationApplies")) continue;
                        paidPublications++;
                        Assert.That(paidPublications, Is.EqualTo(1));
                        Assert.That(world.Cohorts.TryGetValue(cohortId, out var partial), Is.True);
                        Assert.That(partial.Phase, Is.EqualTo(FallingCohortPhase.Detaching),
                            "The natural paid publication must still affect the real final-piece boundary.");
                        Assert.That(partial.DetachCursor, Is.EqualTo(finalCell.y));
                        Assert.That(partial.TopExclusive, Is.EqualTo(finalCell.y + 1));
                        Assert.That(partial.LandingOffset, Is.Zero.Within(1e-6));
                        Assert.That(Read<float>(pair.Value, "Cap"), Is.Zero.Within(1e-6));
                        var journal = Read<object>(paidProof, "Journal");
                        var work = Read<IDictionary>(journal, "Motions")[pair.Key];
                        Assert.That(work, Is.Not.Null, "Use the motion produced by the real paid-column mapping.");
                        Assert.That(Read<bool>(work, "ProofKnown"), Is.False,
                            "A new build cap is not a complete native/stable landing certificate.");
                        Assert.That(Read<int>(work, "Phase"), Is.Zero);
                        paidGeneration = Read<uint>(work, "Generation");
                        sawCapReset = true;
                        AssertBuildCapStableSide(paidCell, 1);
                        AssertBuildCapStableSide(finalCell, 2);
                    }
                }
                if (certifiedDetaches != 0 || !world.Cohorts.TryGetValue(cohortId, out var group)
                    || group.DetachCursor != group.TopExclusive) return;
                Assert.That(sawCapReset, Is.True, "The cap publication must precede the final detach witness.");
                Assert.That(group.Phase, Is.EqualTo(FallingCohortPhase.Falling));
                var motions = Read<IDictionary>(_terrain, "_cohortMotionWork");
                object certificate = null;
                foreach (DictionaryEntry pair in motions)
                    if (Read<bool>(pair.Key, "Cohort") && Read<ulong>(pair.Key, "Id") == cohortId) certificate = pair.Value;
                Assert.That(certificate, Is.Not.Null);
                Assert.That(Read<bool>(certificate, "ProofKnown"), Is.True);
                var native = Read<object>(certificate, "NativeProof");
                Assert.That(native, Is.Not.Null, "The existing final-piece guard must require a real native proof.");
                Assert.That(Read<Vector3>(native, "Position"), Is.EqualTo(BlockWorld.Center(bottom)));
                Assert.That(Read<float>(native, "WorldBottom"), Is.EqualTo(world.Bottom));
                Assert.That(Read<int>(native, "Minimum"), Is.Zero, "The fixture's registered native map floor is at zero.");
                Assert.That(Read<uint>(certificate, "Generation"), Is.EqualTo(paidGeneration),
                    "An unrelated generation change must not hide failure to reset the paid cap certificate.");
                Assert.That(Read<Dictionary<Vector2Int, uint>>(_terrain, "_landingGeneration")[new Vector2Int(bottom.x, bottom.z)],
                    Is.EqualTo(paidGeneration));
                Assert.That(_terrain.LastPublicationStaticProofs, Is.GreaterThan(0));
                Assert.That(world.TryGet(finalCell, out _), Is.False);
                Assert.That(Physics.Raycast(BlockWorld.Center(finalCell) + Vector3.forward * .2f, Vector3.back,
                    out _, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.False,
                    "The final source cell must really leave stable native collision at this publication.");
                Assert.That(group.MemberIds.Sum(id => world.Falling[id].Cells.Count(c => c.Occupied)), Is.EqualTo(17));
                Assert.That(_terrain.RaycastFalling(BlockWorld.Center(finalCell) + Vector3.forward * .2f,
                    Vector3.back, .4f, 0, out var detached), Is.True);
                Assert.That(detached.Cell, Is.EqualTo(finalCell));
                Assert.That(_terrain.TryContact(detached.Collider, detached.Point, detached.Normal, out var contact), Is.True);
                Assert.That(contact.Stable, Is.False);
                certifiedDetaches++;
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(paidCell) - Vector3.up * .051f,
                    Vector3.up, 1, reserved, 0, false, result =>
                    {
                        callbacks++; outcome = result;
                        token.CompleteBuild(reserved, false, result.Added);
                        Assert.That(callbacks, Is.EqualTo(1));
                        Assert.That(result.Added, Is.EqualTo(1));
                        AssertBuildCapStableSide(paidCell, 1);
                    }, true);
                CallFixture("RememberLastCommand");
                // A real FIFO destruction reservation holds this column while
                // initial indexing runs. The build window lacks Destruction,
                // so this no-op cannot complete before the paid cap publishes.
                _terrain.QueueDestroy(BlockWorld.Center(paidCell), 0, 0, 1, true, result =>
                {
                    reservations++;
                    Assert.That(reservations, Is.EqualTo(1));
                    Assert.That(paidPublications, Is.EqualTo(1));
                    Assert.That(result.Changed, Is.Zero);
                    Assert.That(result.Added, Is.Zero); Assert.That(result.RemovedEnemy, Is.Zero);
                    Assert.That(result.RemovedFriendly, Is.Zero);
                }, 0, 0);
                CallFixture("RememberLastCommand");
                bool settled = false;
                for (int frame = 0; frame < 600; frame++)
                {
                    Remember(); _terrain.Tick(true, Epoch + frame * .016, .016f); Remember();
                    AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                    Assert.That(_terrain.FailedTransactions, Is.EqualTo(failures));
                    if (callbacks == 1 && reservations == 1 && certifiedDetaches == 1 && _terrain.PendingTransactions == 0
                        && _terrain.UnstableColumns == 0 && _terrain.World.Falling.Count == 0
                        && _terrain.World.Cohorts.Count == 0)
                    { settled = true; break; }
                    yield return null;
                }
                Assert.That(settled, Is.True, "One isolated paid cap must permit the genuine last piece to detach and settle.");
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(paidPublications, Is.EqualTo(1));
            Assert.That(certifiedDetaches, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(reservations, Is.EqualTo(1));
            Assert.That(outcome.Added, Is.EqualTo(1));
            Assert.That(token.Added, Is.EqualTo(1));
            Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(token.BuildLimit));
            Assert.That(_terrain.CommandsQueued, Is.EqualTo(queued + 2));
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(completed + 2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(10)); Assert.That(_terrain.BlueCount, Is.EqualTo(17));
            AssertBuildCapStableSide(paidCell, 1);
            for (int y = bottom.y; y <= finalCell.y; y++) AssertBuildCapStableSide(new Vector3Int(bottom.x, y, bottom.z), 2);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            AssertWire(source, _terrain.World); AssertGraph(_terrain.World);
        }
        static BlockWorld PartialBuildCapColumn(Vector3Int bottom, out ulong cohortId)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int y = 0; y < bottom.y - 1; y++) world.Set(new Vector3Int(bottom.x, y, bottom.z), BlockCell.Create(1));
            for (int y = 0; y < 17; y++) world.Set(bottom + Vector3Int.up * y, BlockCell.Create(2));
            var group = world.BeginCohort(bottom, 17); cohortId = group.Id;
            world.DetachCohortPiece(group.Id, 16, Epoch, out _);
            group = world.Cohorts[group.Id]; group.StartedAt = Epoch;
            group.LandingKnown = true; group.LandingOffset = BlockWorld.CellSize;
            // This is public published geometry/motion data, not a fabricated
            // private landing certificate. Terrain must obtain the latter itself.
            return world;
        }
        void AssertBuildCapStableSide(Vector3Int cell, byte owner)
        {
            Assert.That(_terrain.World.TryGet(cell, out var stable), Is.True);
            Assert.That(stable.Owner, Is.EqualTo(owner));
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .2f,
                Vector3.back, out var native, .4f, 1 << BlockTerrain.TerrainLayer,
                QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(_terrain.IsStableCollider(native.collider), Is.True);
            Assert.That(_terrain.TryContact(native, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Owner, Is.EqualTo(owner));
            Assert.That(contact.Stable, Is.True);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidEpsilonFloorCapKeepsAnAlreadyPublishedSettlingPrefix()
        {
            // Public detach/activate/Settle data leaves one moving member and
            // fifteen stable prefix cells. A deleted prefix top is built back.
            var bottom = new Vector3Int(2, 10, 2);
            var paidCell = new Vector3Int(2, 24, 2);
            var remainingSource = new Vector3Int(2, 26, 2);
            var seed = SettlingBuildCapColumn(bottom, out ulong cohortId);
            yield return Install(seed);
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            CollectionAssert.AreEqual(frozen, BlockWorld.DecodeSnapshot(frozen).EncodeSnapshot());
            var original = source.Cohorts[cohortId];
            Assert.That(original.Phase, Is.EqualTo(FallingCohortPhase.Settling));
            Assert.That(original.Bottom, Is.EqualTo(bottom));
            Assert.That(original.TopExclusive, Is.EqualTo(27));
            Assert.That(original.MemberIds.Count, Is.EqualTo(1));
            Assert.That(original.InitialOffset, Is.GreaterThan(BlockWorld.CellSize));
            Assert.That(original.InitialOffset - BlockWorld.CellSize, Is.LessThan(.000001f));
            Assert.That(source.PinkCount, Is.EqualTo(9)); Assert.That(source.BlueCount, Is.EqualTo(16));
            Assert.That(source.TryGet(paidCell, out _), Is.False);
            for (int y = 9; y < paidCell.y; y++) AssertBuildCapStableSide(new Vector3Int(2, y, 2), 2);
            Assert.That(_terrain.RaycastFalling(BlockWorld.Center(remainingSource + Vector3Int.down)
                + Vector3.forward * .2f, Vector3.back, .4f, 0, out var moving), Is.True);
            Assert.That(moving.Cell, Is.EqualTo(remainingSource));

            var token = Token(4502); int reserved = token.ReserveBuild(1, false);
            Assert.That(reserved, Is.EqualTo(1));
            int callbacks = 0, reservations = 0, paidPublications = 0;
            long failures = _terrain.FailedTransactions, recoveryStarts = _terrain.HistoricalRecoveryStarts;
            long queued = _terrain.CommandsQueued, completed = _terrain.CommandsCompleted;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Remember(); var world = _terrain.World;
                AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                Assert.That(world.BlueCount, Is.EqualTo(16));
                var proof = Read<object>(_terrain, "_lastBuildOccupancyProof");
                if (proof == null || !ReferenceEquals(Read<BlockWorld>(proof, "Next"), world)) return;
                foreach (DictionaryEntry pair in Read<IDictionary>(proof, "Caps"))
                {
                    if (!Read<bool>(pair.Key, "Cohort") || Read<ulong>(pair.Key, "Id") != cohortId
                        || !Read<bool>(pair.Value, "PublicationApplies")) continue;
                    paidPublications++;
                    Assert.That(paidPublications, Is.EqualTo(1));
                    Assert.That(world.Cohorts.TryGetValue(cohortId, out var settling), Is.True);
                    Assert.That(settling.Phase, Is.EqualTo(FallingCohortPhase.Settling));
                    Assert.That(settling.Bottom, Is.EqualTo(bottom));
                    Assert.That(settling.MemberIds.Count, Is.EqualTo(1));
                    float cap = Read<float>(pair.Value, "Cap");
                    float raw = Read<float>(Read<object>(pair.Value, "Sample"), "Offset");
                    Assert.That(cap, Is.EqualTo(BlockWorld.CellSize));
                    Assert.That(cap, Is.LessThan(original.LandingOffset), "The accepted cap is genuinely nearer.");
                    Assert.That(raw, Is.GreaterThan(cap), "Exercise the real sub-epsilon settled-pose boundary.");
                    Assert.That(raw - cap, Is.LessThan(.000001f));
                    Assert.That(settling.InitialOffset, Is.EqualTo(cap));
                    Assert.That(settling.LandingOffset, Is.EqualTo(cap));
                    var work = Read<IDictionary>(Read<object>(proof, "Journal"), "Motions")[pair.Key];
                    Assert.That(work, Is.Not.Null);
                    Assert.That(Read<bool>(work, "ProofKnown"), Is.True,
                        "Settling keeps its existing continuation; a full-span reproof would hit its stable prefix.");
                    Assert.That(Read<int>(work, "Phase"), Is.Zero);
                    Assert.That(Read<object>(work, "NativeProof"), Is.Null,
                        "The cap-only mapping is observed as produced; no private certificate is fabricated.");
                    AssertBuildCapStableSide(paidCell, 1);
                    for (int y = 9; y < paidCell.y; y++) AssertBuildCapStableSide(new Vector3Int(2, y, 2), 2);
                }
            };
            _terrain.PublishedCommit += observe;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(paidCell) - Vector3.up * .051f,
                    Vector3.up, 1, reserved, 0, false, result =>
                    {
                        callbacks++; token.CompleteBuild(reserved, false, result.Added);
                        Assert.That(callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(1));
                        Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                        AssertBuildCapStableSide(paidCell, 1);
                    }, true);
                CallFixture("RememberLastCommand");
                // Same real FIFO reservation as the final-piece case. It cannot
                // run in the OrdinaryAttack|Gravity publication window.
                _terrain.QueueDestroy(BlockWorld.Center(paidCell), 0, 0, 1, true, result =>
                {
                    reservations++; Assert.That(reservations, Is.EqualTo(1));
                    Assert.That(paidPublications, Is.EqualTo(1)); Assert.That(result.Changed, Is.Zero);
                    Assert.That(result.Added, Is.Zero); Assert.That(result.RemovedEnemy, Is.Zero);
                    Assert.That(result.RemovedFriendly, Is.Zero);
                }, 0, 0);
                CallFixture("RememberLastCommand");
                bool settled = false;
                for (int frame = 0; frame < 600; frame++)
                {
                    Remember(); _terrain.Tick(true, Epoch + frame * .016, .016f); Remember();
                    AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                    Assert.That(_terrain.FailedTransactions, Is.EqualTo(failures));
                    Assert.That(_terrain.HistoricalRecoveryStarts, Is.EqualTo(recoveryStarts),
                        "The valid published prefix must not be mistaken for an illegal historical overlap.");
                    if (callbacks == 1 && reservations == 1 && _terrain.PendingTransactions == 0
                        && _terrain.UnstableColumns == 0 && _terrain.World.Falling.Count == 0
                        && _terrain.World.Cohorts.Count == 0)
                    { settled = true; break; }
                    yield return null;
                }
                Assert.That(settled, Is.True, "The remaining real member must settle without re-scanning the original prefix.");
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(paidPublications, Is.EqualTo(1)); Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(reservations, Is.EqualTo(1)); Assert.That(token.Added, Is.EqualTo(1));
            Assert.That(token.Added + token.BuildRemaining, Is.EqualTo(token.BuildLimit));
            Assert.That(_terrain.CommandsQueued, Is.EqualTo(queued + 2));
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(completed + 2));
            Assert.That(_terrain.PinkCount, Is.EqualTo(10)); Assert.That(_terrain.BlueCount, Is.EqualTo(16));
            AssertBuildCapStableSide(paidCell, 1);
            for (int y = 9; y < paidCell.y; y++) AssertBuildCapStableSide(new Vector3Int(2, y, 2), 2);
            AssertBuildCapStableSide(new Vector3Int(2, 25, 2), 2);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            AssertWire(source, _terrain.World); AssertGraph(_terrain.World);
        }
        static BlockWorld SettlingBuildCapColumn(Vector3Int bottom, out ulong cohortId)
        {
            var world = PartialBuildCapColumn(bottom, out cohortId);
            var first = world.Falling.Values.Single(r => r.Bottom == bottom);
            world.DetachCohortPiece(cohortId, 1, Epoch, out _);
            var group = world.Cohorts[cohortId];
            group.LandingKnown = true; group.LandingOffset = group.InitialOffset = .10000002f;
            group.InitialSpeed = 0; group.NeedsLandingProof = false;
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            Assert.That(world.Settle(first.Id, bottom + Vector3Int.down, new HashSet<Vector3Int>()), Is.True);
            // This is an actual stable-prefix removal through the public world
            // API. The remaining member stays intact; NeedsRegroup remains false.
            Assert.That(world.Set(new Vector3Int(bottom.x, 24, bottom.z), default), Is.True);
            Assert.That(world.Cohorts[cohortId].Phase, Is.EqualTo(FallingCohortPhase.Settling));
            Assert.That(world.Cohorts[cohortId].NeedsRegroup, Is.False);
            return world;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidCohortAndLegacyBuildsMatchTheOriginalFullOwnedPath()
        {
            yield return RealPaid(false, bytes => _paidCandidate = bytes);
            yield return CleanupFixtureBeforeExitingPlayMode();
            yield return new ExitPlayMode();
            // Preserve the original teardown reset followed by the setup reset.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return CreateFixtureAfterEnteringPlayMode();
            yield return RealPaid(true, bytes => _paidOracle = bytes);
            Assert.That(_paidCandidate, Is.Not.Null); Assert.That(_paidOracle, Is.Not.Null);
            CollectionAssert.AreEqual(_paidOracle, _paidCandidate,
                "Normalize only wire revision fields; actual payload, owner, motion and future constraints must match full-owned publication.");
        }
        IEnumerator RealPaid(bool forceFull, Action<byte[]> finish)
        {
            yield return Install(Seed(3));
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            var tokens = new[] { Token(81), Token(82) }; var callbacks = new int[2];
            var added = new int[2]; var order = new List<int>();
            var cells = new[] { new Vector3Int(2, 0, 2), new Vector3Int(3, 0, 2) };
            for (int i = 0; i < 2; i++)
            {
                int index = i; int reserved = tokens[i].ReserveBuild(1, false);
                Assert.That(reserved, Is.EqualTo(1));
                _terrain.QueueBuild(BlockWorld.Center(cells[i]) - Vector3.up * .051f,
                    Vector3.up, 1, reserved, 0, true, result =>
                    {
                        callbacks[index]++; added[index] = result.Added; order.Add(index);
                        tokens[index].CompleteBuild(reserved, false, result.Added);
                    }, true);
                CallFixture("RememberLastCommand");
            }
            var configured = new HashSet<object>(); var completed = new HashSet<object>();
            var published = new HashSet<object>(); int publishedResultCount = 0;
            bool capCohort = false, capLegacy = false, guardCohort = false, guardLegacy = false;
            long failures = _terrain.FailedTransactions;
            for (int frame = 0; frame < Limit; frame++)
            {
                Remember();
                var preparation = Read<object>(_terrain, "_buildOccupancyPreparation");
                if (preparation != null && !Read<bool>(preparation, "Retry") && configured.Add(preparation))
                {
                    Assert.That(Read<int>(preparation, "Phase"), Is.EqualTo(1));
                    Assert.That(Read<bool>(preparation, "ZeroPreparedMetadata"), Is.True);
                    // Controlled writer seam: retain genuine Source and Draft
                    // owners outside both paid added-slot columns. The ordinary
                    // validator/apply path must own and rebase them as well.
                    AddContinuity(preparation, source.Cohorts.Values.Single(g => g.Bottom.x == 6).Id, true);
                    AddContinuity(preparation, source.Falling.Values.Single(r => r.Cohort == null && r.Bottom.x == 7).Id, false);
                    if (forceFull)
                    {
                        ((IDisposable)Read<object>(preparation, "MetadataOwnership")).Dispose();
                        Set(preparation, "MetadataOwnership", null); Set(preparation, "ZeroPreparedMetadata", false);
                        Set(preparation, "Next", null); Set(preparation, "Phase", 0);
                        Set(preparation, "Clone", Read<BlockWorld>(preparation, "Draft").BeginBudgetedOwnedClone());
                    }
                }
                _terrain.Tick(true, Epoch, .016f); Remember();
                var proof = Read<object>(_terrain, "_lastBuildOccupancyProof");
                if (proof != null && completed.Add(proof))
                {
                    Assert.That(Read<bool>(proof, "Ready"), Is.True);
                    var draft = Read<BlockWorld>(proof, "Next");
                    if (!forceFull)
                    {
                        var state = Read<object>(proof, "MetadataOwnership");
                        Assert.That(Read<int>(state, "Stage"), Is.EqualTo(9));
                        Assert.That(Read<int>(state, "TargetsCollected"), Is.GreaterThanOrEqualTo(2));
                        Assert.That(Read<int>(state, "CapsRebound"), Is.EqualTo(Read<IDictionary>(proof, "Caps").Count));
                        Assert.That(Read<long>(state, "OwnershipWork"), Is.GreaterThan(0));
                        Assert.That(Read<object>(state, "Owner"), Is.Null);
                        Assert.That(draft, Is.SameAs(Read<BlockWorld>(proof, "Draft")));
                        AssertRebound(proof);
                    }
                    else AssertFullCloneReadyBeforePublication(proof);
                }
                // Ready only creates Pending. Observe an actual World swap
                // before asserting publication-phase continuity ownership.
                foreach (var ready in completed)
                    if (ReferenceEquals(Read<BlockWorld>(ready, "Next"), _terrain.World) && published.Add(ready))
                    {
                        AssertRebound(ready);
                        publishedResultCount += Read<ICollection>(ready, "Results").Count;
                        foreach (DictionaryEntry pair in Read<IDictionary>(ready, "Caps"))
                        {
                            if (Read<bool>(pair.Key, "Cohort")) capCohort = true; else capLegacy = true;
                        }
                        var journal = Read<object>(ready, "Journal");
                        guardCohort |= Read<IDictionary>(journal, "BindingContinuity").Count != 0;
                        guardLegacy |= Read<IDictionary>(journal, "LegacyContinuity").Count != 0;
                    }
                AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                if (callbacks.All(n => n == 1) && _terrain.PendingTransactions == 0
                    && Read<object>(_terrain, "_buildOccupancyPreparation") == null) break;
                yield return null;
            }
            Assert.That(configured.Count, Is.GreaterThan(0), "Must observe real moving-build preparation before its first advance.");
            Assert.That(completed.Count, Is.GreaterThan(0));
            Assert.That(capCohort && capLegacy && guardCohort && guardLegacy, Is.True,
                "Real publication must exercise cohort/legacy caps and both outside-column continuity writers.");
            CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks); CollectionAssert.AreEqual(new[] { 1, 1 }, added);
            CollectionAssert.AreEqual(new[] { 0, 1 }, order);
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(failures));
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            Assert.That(published.Count, Is.GreaterThan(0), "Must observe an actual paid proof World publication.");
            Assert.That(completed.SetEquals(published), Is.True, "Every observed Ready proof must actually publish; retries cannot masquerade as publication.");
            Assert.That(publishedResultCount, Is.EqualTo(callbacks.Sum()), "Published proof results must account for both actual paid completion callbacks.");
            foreach (var ready in completed) AssertRebound(ready);
            for (int i = 0; i < 2; i++)
            {
                Assert.That(tokens[i].Added, Is.EqualTo(1));
                Assert.That(tokens[i].Added + tokens[i].BuildRemaining, Is.EqualTo(tokens[i].BuildLimit));
                AssertPublishedPaidCellWithClearSightLine(cells[i]);
            }
            Assert.That(_terrain.PinkCount, Is.EqualTo(source.PinkCount + 2));
            Assert.That(_terrain.BlueCount, Is.EqualTo(source.BlueCount));
            AssertWire(source, _terrain.World); AssertGraph(_terrain.World);
            // Sample before and well after floor contact on a frozen published
            // result. Snapshot comparison below includes all exact trajectory
            // fields; these checks also exercise the public future evaluator.
            foreach (var run in _terrain.World.Falling.Values)
                foreach (double time in new[] { Epoch, Epoch + .001, Epoch + .1, Epoch + 1 })
                    Assert.That(float.IsNaN(run.Offset(time)) || float.IsInfinity(run.Offset(time)), Is.False);
            var normalized = BlockWorld.DecodeSnapshot(_terrain.ExportSnapshot());
            normalized.Seal(1, normalized.RevisionKeys.ToArray()); finish(normalized.EncodeSnapshot());
        }

        void AssertPublishedPaidCellWithClearSightLine(Vector3Int cell)
        {
            if (!_terrain.World.TryGet(cell, out var stable))
            {
                CallFixture("AssertPublishedPaidCell", cell);
                return;
            }
            // This test's two paid cells are adjacent only along X. Their exposed Z
            // faces provide a real native ray path that the other cell cannot occlude.
            Assert.That(stable.Owner, Is.EqualTo(1));
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .2f,
                Vector3.back, out var native, .4f, 1 << BlockTerrain.TerrainLayer,
                QueryTriggerInteraction.Ignore), Is.True, "A stable published addition has real native collision.");
            Assert.That(_terrain.IsStableCollider(native.collider), Is.True);
            Assert.That(_terrain.TryContact(native, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell));
            Assert.That(contact.Owner, Is.EqualTo(1));
            Assert.That(contact.Stable, Is.True);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator FrozenWritersAndAliasReferencesMatchFullOwnedMetadataWithoutCopyingUnrelatedColumns()
        {
            var seed = AliasSeed(out ulong oldRun); yield return Install(seed);
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            var draft = source.Clone(); draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            var proof = HelperProof(source, draft);
            var cells = AddProofColumn(proof, 2, 2);
            foreach (int index in new[] { 0, 2 }) AddProofCell(cells, oldRun, index);
            foreach (var group in draft.Cohorts.Values.Where(g => g.Bottom.x == 2)) AddCap(proof, group.Id, true);
            AddContinuity(proof, source.Cohorts.Values.Single(g => g.Bottom.x == 6).Id, true);
            AddContinuity(proof, source.Falling.Values.Single(r => r.Cohort == null && r.Bottom.x == 7).Id, false);
            // Phantom/deleted guard keys are valid historical guards; no target
            // is emitted, and their Next is rebound to null like final Validate.
            AddContinuity(proof, ulong.MaxValue, true); AddContinuity(proof, ulong.MaxValue, false);
            var unrelated = draft.Cohorts.Values.Single(g => g.Bottom.x == 8);
            var oracle = FullyOwned(draft);
            PumpHelper(proof, 512);
            AssertRebound(proof); AssertGraph(draft); AssertWire(source, draft);
            Assert.That(draft.Cohorts[unrelated.Id], Is.SameAs(unrelated));
            CollectionAssert.AreEqual(oracle.EncodeSnapshot(), draft.EncodeSnapshot());
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator A124DescriptorClosureAndWriterStreamObeyEveryAdmission() => Closure(124);
        [UnityTest, Timeout(600000)] public IEnumerator A400DescriptorClosureAndWriterStreamObeyEveryAdmission() => Closure(400);
        IEnumerator Closure(int count)
        {
            yield return Install(Seed(count));
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot(); var draft = source.Clone();
            draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            var proof = HelperProof(source, draft);
            ulong root = draft.Cohorts.Values.Where(g => g.Bottom.x == 2).OrderBy(g => g.Bottom.y).First().Id;
            AddCap(proof, root, true);
            AddContinuity(proof, source.Cohorts.Values.Single(g => g.Bottom.x == 6).Id, true);
            AddContinuity(proof, source.Falling.Values.Single(r => r.Cohort == null && r.Bottom.x == 7).Id, false);
            int visited = 0; bool deniedClosureStep = false;
            while (!HelperDone(proof) && visited++ < 200000)
            {
                var state = Read<object>(proof, "MetadataOwnership"); var owner = Read<BlockBuildMetadataOwnershipPreparation>(state, "Owner");
                int nextCost = owner != null && !owner.Ready ? owner.EstimateNextStepWork() : 0;
                if (!deniedClosureStep && nextCost > 8)
                {
                    ulong progress = owner.ProgressVersion; long work = owner.ProcessedWork;
                    int actual = HelperStep(proof, nextCost - 1);
                    Assert.That(actual, Is.Zero); Assert.That(owner.ProgressVersion, Is.EqualTo(progress));
                    Assert.That(owner.ProcessedWork, Is.EqualTo(work)); deniedClosureStep = true;
                }
                int spent = HelperStep(proof, 512); Assert.That(spent, Is.InRange(0, 512));
                AssertSnapshotBytesEqual(frozen, source.EncodeSnapshot());
                if ((visited & 127) == 0) yield return null;
            }
            Assert.That(HelperDone(proof), Is.True); Assert.That(deniedClosureStep, Is.True);
            Assert.That(Read<int>(Read<object>(proof, "MetadataOwnership"), "MaximumAdmittedStep"), Is.LessThanOrEqualTo(412));
            AssertRebound(proof); AssertGraph(draft); AssertWire(source, draft);
            CollectionAssert.AreEqual(FullyOwned(draft).EncodeSnapshot(), draft.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator ClearDuringWriterCollectionDiscardsOnlyThePrivateDraft() => CancelAt(1, "clear");
        [UnityTest, Timeout(600000)] public IEnumerator AuthorityLossInsideAClosureDiscardsOnlyThePrivateDraft() => CancelAt(4, "authority");
        [UnityTest, Timeout(600000)] public IEnumerator RemoteReplacementBetweenClosuresDiscardsOnlyThePrivateDraft() => CancelAt(40, "remote");
        [UnityTest, Timeout(600000)] public IEnumerator FinishRoundDuringReferenceRebindCannotPublishTheDraft() => CancelAt(6, "finish");
        IEnumerator CancelAt(int stage, string action)
        {
            yield return Install(Seed(124));
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            int pink = _terrain.PinkCount, blue = _terrain.BlueCount;
            var draft = source.Clone(); draft.Seal(source.Revision + 1, Array.Empty<Vector3Int>());
            var proof = HelperProof(source, draft); AddCap(proof, source.Cohorts.Values.Where(g => g.Bottom.x == 2).OrderBy(g => g.Bottom.y).First().Id, true);
            AddContinuity(proof, source.Cohorts.Values.Single(g => g.Bottom.x == 6).Id, true);
            bool reached = false;
            for (int i = 0; i < 200000; i++)
            {
                var state = Read<object>(proof, "MetadataOwnership");
                int at = Read<int>(state, "Stage"); var owner = Read<BlockBuildMetadataOwnershipPreparation>(state, "Owner");
                bool match = stage == 40 ? at == 4 && owner != null && owner.TargetsVisited == 1
                    && owner.EstimateNextStepWork() == 8 && owner.ProcessedWork > 8
                    : stage == 4 ? at == 4 && owner != null && owner.EstimateNextStepWork() > 8 : at == stage;
                if (match) { reached = true; break; }
                HelperStep(proof, NextSingleCost(state));
            }
            Assert.That(reached, Is.True, "Must reach the specified real collector/closure/rebind boundary.");
            Set(_terrain, "_buildOccupancyPreparation", proof);
            if (action == "clear") _terrain.Clear(_terrain.Round + 1);
            else if (action == "authority") _terrain.Tick(false, Epoch, .016f);
            else if (action == "remote")
            {
                var replacement = BlockWorld.DecodeSnapshot(frozen); replacement.Seal(source.Revision + 2, replacement.RevisionKeys.ToArray());
                Assert.That(_terrain.ApplyRemoteSnapshot(replacement.EncodeSnapshot()), Is.True);
                _terrain.Tick(false, Epoch, .016f);
            }
            else _terrain.FinishRound(Epoch);
            Remember();
            Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            var disposed = Read<object>(proof, "MetadataOwnership");
            foreach (string field in new[] { "Owner", "Caps", "Bindings", "Legacies", "Targets", "Column", "Cell" })
                Assert.That(Read<object>(disposed, field), Is.Null, field);
            Assert.That(_terrain.World, Is.Not.SameAs(draft));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            if (action != "clear") { Assert.That(_terrain.PinkCount, Is.EqualTo(pink)); Assert.That(_terrain.BlueCount, Is.EqualTo(blue)); }
            yield return null;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ARealPaidClosureCancellationReplaysTheOriginalLeaseAndCompletesEachReservationOnce()
            => RealPaidCancellation(false);
        [UnityTest, Timeout(600000)]
        public IEnumerator ClearingARealPaidPartialClosureRefundsOnceAndCannotReplayIntoTheNextRound()
            => RealPaidCancellation(true);
        IEnumerator RealPaidCancellation(bool clear)
        {
            yield return Install(Seed(124));
            var tokens = new[] { Token(91), Token(92) }; var callbacks = new int[2]; var added = new int[2];
            var cells = new[] { new Vector3Int(2, 0, 2), new Vector3Int(30, 0, 30) };
            for (int index = 0; index < 2; index++)
            {
                int capture = index; Assert.That(tokens[index].ReserveBuild(1, false), Is.EqualTo(1));
                _terrain.QueueBuild(BlockWorld.Center(cells[index]) - Vector3.up * .051f, Vector3.up, 1, 1, 0, true,
                    result => { callbacks[capture]++; added[capture] = result.Added; tokens[capture].CompleteBuild(1, false, result.Added); }, true);
                CallFixture("RememberLastCommand");
            }
            object partial = null;
            for (int frame = 0; frame < Limit; frame++)
            {
                Remember(); _terrain.Tick(true, Epoch, .016f); Remember();
                var preparation = Read<object>(_terrain, "_buildOccupancyPreparation");
                if (preparation != null && Read<bool>(preparation, "ZeroPreparedMetadata"))
                {
                    var state = Read<object>(preparation, "MetadataOwnership");
                    var owner = Read<BlockBuildMetadataOwnershipPreparation>(state, "Owner");
                    if (Read<int>(state, "Stage") == 4 && owner != null && !owner.Ready
                        && owner.EstimateNextStepWork() > 8) { partial = preparation; break; }
                }
                Assert.That(callbacks[0], Is.Zero, "The test must cancel before the original moving build publishes.");
                yield return null;
            }
            Assert.That(partial, Is.Not.Null, "Must witness an actual paid preparation inside its dependent ownership closure.");
            Assert.That(Read<object>(partial, "Checkpoints"), Is.Not.Null);
            Assert.That(((IEnumerable)Read<object>(partial, "Checkpoints")).Cast<object>().Count(), Is.GreaterThan(0));
            var source = Read<BlockWorld>(partial, "Source"); byte[] frozen = source.EncodeSnapshot();
            var discarded = Read<BlockWorld>(partial, "Draft"); var window = Read<BlockPublicationWindow>(partial, "Window");
            var commands = Read<List<object>>(_fixture, "_commands").ToArray();
            Assert.That(commands.Length, Is.EqualTo(2));
            if (clear) _terrain.Clear(_terrain.Round + 1); else _terrain.Tick(false, Epoch, .016f);
            Remember();
            Assert.That(Read<object>(_terrain, "_buildOccupancyPreparation"), Is.Null);
            Assert.That(Read<object>(Read<object>(partial, "MetadataOwnership"), "Owner"), Is.Null);
            Assert.That(_terrain.World, Is.Not.SameAs(discarded));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            if (!clear)
            {
                CollectionAssert.AreEqual(new[] { 0, 0 }, callbacks);
                Assert.That(_terrain.World, Is.SameAs(source));
                Assert.That(Read<BlockPublicationWindow>(commands[0], "RetryWindow"), Is.SameAs(window));
                Assert.That(((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().Contains(commands[0]), Is.True,
                    "Authority-loss restores the original command object and its finite paid lease.");
                for (int frame = 0; frame < Limit && (!callbacks.All(value => value == 1) || _terrain.PendingTransactions != 0); frame++)
                { Remember(); _terrain.Tick(true, Epoch, .016f); Remember(); yield return null; }
                CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks); CollectionAssert.AreEqual(new[] { 1, 1 }, added);
                Assert.That(_terrain.PinkCount, Is.EqualTo(2));
                for (int i = 0; i < 2; i++)
                {
                    Assert.That(tokens[i].Added, Is.EqualTo(1));
                    Assert.That(tokens[i].Added + tokens[i].BuildRemaining, Is.EqualTo(tokens[i].BuildLimit));
                    CallFixture("AssertPublishedPaidCell", cells[i]);
                }
                _terrain.Clear(_terrain.Round + 1);
            }
            else
            {
                CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks); CollectionAssert.AreEqual(new[] { 0, 0 }, added);
                foreach (var token in tokens)
                { Assert.That(token.BuildRemaining, Is.EqualTo(token.BuildLimit)); Assert.That(token.Added, Is.Zero); Assert.That(token.TakePoints(), Is.Zero); }
            }
            uint nextRound = _terrain.Round;
            for (int frame = 0; frame < 120; frame++)
            { Remember(); _terrain.Tick(true, Epoch + .1, .016f); Remember(); yield return null; }
            CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks);
            Assert.That(_terrain.Round, Is.EqualTo(nextRound)); Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
            Assert.That(((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>(), Is.Empty);
            Assert.That(Read<object>(_terrain, "_gravityRetryPublicationWindow"), Is.Null);
            Physics.SyncTransforms();
            foreach (var cell in cells)
                Assert.That(Physics.OverlapBox(BlockWorld.Center(cell), Vector3.one * .04f, Quaternion.identity,
                    1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.Empty);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator PreparedCohortActivationUsesOriginalCloneAndProofMigration() => PreparedFallback(true);
        [UnityTest, Timeout(600000)] public IEnumerator PreparedLegacyActivationUsesOriginalCloneAndProofMigration() => PreparedFallback(false);
        IEnumerator PreparedFallback(bool cohort)
        {
            yield return Install(Seed(3));
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot(); var draft = source.Clone();
            Vector3Int at = new(12, 20, 2); draft.Set(at, BlockCell.Create(1));
            if (cohort)
            {
                var group = draft.BeginCohort(at, 1); draft.DetachCohortPiece(group.Id, 1, Epoch, out _);
                draft.SetCohortSupport(group.Id, 0, 0, out _); group = draft.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = 2; group.NeedsLandingProof = false;
                draft.PreparePendingCohortsForPublication(out _);
            }
            else { var run = draft.Detach(at, 1, Epoch); run.LandingKnown = true; run.LandingOffset = 2; draft.PrepareLegacyRunEpochsForPublication(out _); }
            var changed = new HashSet<Vector3Int>(draft.RevisionKeys); draft.Seal(source.Revision + 1, changed);
            Assert.That(draft.EstimatedPreparedPublicationWork, Is.GreaterThan(0));
            var journal = New("GravityJournal"); Set(journal, "Next", draft); Set(journal, "SupportProofDraft", draft);
            Set(_terrain, "_gravityJournal", journal);
            Set(_terrain, "_stageAddedBuildSlots", NewLinkedList("AddedBuildSlot"));
            var window = _terrain.PublicationPolicy.Capture(BlockPublicationOperation.Metadata);
            // Null results are legitimate background metadata. They prevent
            // these fallback-only fixtures from inventing a paid command.
            Call(_terrain, "BeginBuildOccupancyPreparation", draft, changed, null, window);
            var proof = Read<object>(_terrain, "_buildOccupancyPreparation");
            Assert.That(Read<bool>(proof, "ZeroPreparedMetadata"), Is.False);
            Assert.That(Read<int>(proof, "Phase"), Is.Zero); Assert.That(Read<object>(proof, "Clone"), Is.Not.Null);
            Assert.That(Read<object>(proof, "MetadataOwnership"), Is.Null);
            for (int frame = 0; frame < Limit && Read<object>(_terrain, "_pending") == null; frame++)
            { Call(_terrain, "AdvanceBuildOccupancyPreparation"); Remember(); yield return null; }
            var next = Read<BlockWorld>(proof, "Next");
            Assert.That(next, Is.Not.SameAs(draft)); Assert.That(next, Is.SameAs(Read<BlockWorld>(journal, "Next")));
            Assert.That(next, Is.SameAs(Read<BlockWorld>(journal, "SupportProofDraft")));
            for (int frame = 0; frame < Limit && !ReferenceEquals(_terrain.World, next); frame++)
            { Remember(); Call(_terrain, "AdvancePending", true); Remember(); yield return null; }
            Assert.That(_terrain.World, Is.SameAs(next)); Assert.That(_terrain.PinkCount, Is.EqualTo(source.PinkCount + 1));
            AssertGraph(next); AssertWire(source, next); CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        IEnumerator Install(BlockWorld seed)
        {
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return (IEnumerator)Call(_fixture, "PumpRemoteInstall", seed.Revision);
            // No authority Tick between installation and the controlled inputs.
        }
        object HelperProof(BlockWorld source, BlockWorld draft)
        {
            var proof = New("BuildOccupancyPreparation"); var journal = New("GravityJournal"); Set(journal, "Next", draft);
            Set(proof, "Source", source); Set(proof, "Draft", draft); Set(proof, "Next", draft); Set(proof, "Journal", journal);
            Set(proof, "Epoch", Read<uint>(_terrain, "_epoch")); Set(proof, "Revision", _terrain.Revision); Set(proof, "Round", _terrain.Round);
            Set(proof, "Mutation", source.MutationGeneration); Set(proof, "ZeroPreparedMetadata", true); Set(proof, "Phase", 2);
            Set(proof, "MetadataOwnership", Activator.CreateInstance(Nested("BuildMetadataOwnership"), Flags, null, new object[] { draft }, null));
            return proof;
        }
        static BlockWorld Seed(int count)
        {
            var world = new BlockWorld(new Vector2(32, 64)); int bottom = count == 400 ? -80 : 10; ulong lower = 0;
            for (int i = 0; i < count; i++)
            {
                var group = Add(world, new Vector3Int(2, bottom + i, 2));
                world.SetCohortSupport(group.Id, lower, 0, out _); group = world.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = count == 400 ? i * .1f : (bottom + i) * .1f;
                group.NeedsLandingProof = false; lower = group.Id;
            }
            foreach (int x in new[] { 6, 8 }) { var group = Add(world, new Vector3Int(x, 20, 2)); group.LandingKnown = true; group.LandingOffset = 2; group.NeedsLandingProof = false; }
            foreach (int x in new[] { 3, 7 })
            { var at = new Vector3Int(x, 20, 2); world.Set(at, BlockCell.Create(2)); var run = world.Detach(at, 1, Epoch); run.LandingKnown = true; run.LandingOffset = 2; }
            world.PreparePendingCohortsForPublication(out _); world.PrepareLegacyRunEpochsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch); world.Seal(1, world.RevisionKeys.ToArray()); return world;
        }
        static FallingCohort Add(BlockWorld world, Vector3Int at)
        { world.Set(at, BlockCell.Create(2)); var group = world.BeginCohort(at, 1); world.DetachCohortPiece(group.Id, 1, Epoch, out _); return group; }
        static BlockWorld AliasSeed(out ulong oldRun)
        {
            var world = Seed(1); var at = new Vector3Int(2, 10, 2); var parent = world.Cohorts.Values.Single(g => g.Bottom.x == 2);
            // Rebuild only this column as a three-cell source, then fracture it.
            world.RemoveFallingCell(parent.MemberIds.Single(), 0, out _);
            for (int y = 20; y < 23; y++) world.Set(new Vector3Int(2, y, 2), BlockCell.Create(2));
            parent = world.BeginCohort(new Vector3Int(2, 20, 2), 3); world.DetachCohortPiece(parent.Id, 3, Epoch, out _);
            parent.LandingKnown = true; parent.LandingOffset = 2; parent.NeedsLandingProof = false;
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            oldRun = parent.MemberIds.Single(); world.RemoveFallingCell(oldRun, 1, out _); at = new Vector3Int(2, 20, 2);
            var children = world.ReplaceCohort(parent.Id, new[]
            {
                new FallingCohortSpan(at, 21, new[] { new FallingCohortPiece(oldRun, 0, 1) }),
                new FallingCohortSpan(at + Vector3Int.up * 2, 23, new[] { new FallingCohortPiece(oldRun, 2, 1) })
            }, Epoch, out _);
            foreach (var child in children) { child.LandingKnown = true; child.LandingOffset = 2; child.NeedsLandingProof = false; }
            world.PreparePendingCohortsForPublication(out _); world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(2, world.RevisionKeys.ToArray()); return world;
        }
        static object AddProofColumn(object proof, int x, int z)
        { var column = New("BuildColumnProof"); Set(column, "Column", new Vector2Int(x, z)); Call(Read<object>(proof, "ColumnOrder"), "AddLast", column); return Read<object>(column, "Cells"); }
        static void AddProofCell(object cells, ulong run, int index)
        { var cell = New("BuildOccupiedCell"); Set(cell, "SourceRun", run); Set(cell, "SourceIndex", index); Call(cells, "AddLast", cell); }
        static void AddCap(object proof, ulong id, bool cohort)
        { var key = Activator.CreateInstance(Nested("GravityMotionId"), id, cohort); Read<IDictionary>(proof, "Caps").Add(key, New("BuildLandingCap")); }
        static void AddContinuity(object proof, ulong id, bool cohort)
        {
            var source = Read<BlockWorld>(proof, "Source"); var draft = Read<BlockWorld>(proof, "Draft");
            var guard = New(cohort ? "GravityBindingContinuity" : "LegacyLandingContinuity");
            if (cohort) { source.Cohorts.TryGetValue(id, out var original); draft.Cohorts.TryGetValue(id, out var next); Set(guard, "Source", original); Set(guard, "Next", next); }
            else { source.Falling.TryGetValue(id, out var original); draft.Falling.TryGetValue(id, out var next); Set(guard, "Source", original); Set(guard, "Next", next); }
            var journal = Read<object>(proof, "Journal"); Read<IDictionary>(journal, cohort ? "BindingContinuity" : "LegacyContinuity")[id] = guard;
        }
        int HelperStep(object proof, int maximum)
        {
            object[] args = { proof, 0, maximum, Stopwatch.StartNew(), double.MaxValue };
            Call(_terrain, "AdvanceBuildMetadataOwnership", args); return (int)args[1];
        }
        static int NextSingleCost(object state)
        {
            int stage = Read<int>(state, "Stage"); var owner = Read<BlockBuildMetadataOwnershipPreparation>(state, "Owner");
            if (stage == 0 || stage == 4 && owner.Ready) return 16;
            if (stage == 4) return owner.EstimateNextStepWork();
            if (stage == 5) return Read<object>(state, "Cell") == null ? 4 : BlockWorld.MaxCohortPieceCells + 18;
            return 8;
        }
        void PumpHelper(object proof, int maximum)
        { int loops = 0; while (!HelperDone(proof) && loops++ < 200000) HelperStep(proof, maximum); Assert.That(HelperDone(proof), Is.True); }
        static bool HelperDone(object proof) => Read<int>(Read<object>(proof, "MetadataOwnership"), "Stage") == 9;
        static BlockWorld FullyOwned(BlockWorld source)
        {
            using var clone = source.BeginBudgetedOwnedClone(); int loops = 0;
            while (!clone.Ready && loops++ < 200000) { int cost = clone.EstimateNextStepWork(); clone.Step(source, cost, out _); Assert.That(clone.Failed, Is.False); }
            Assert.That(clone.Ready, Is.True); return clone.TakeWorld();
        }
        static void AssertFullCloneReadyBeforePublication(object proof)
        {
            var next = Read<BlockWorld>(proof, "Next"); var draft = Read<BlockWorld>(proof, "Draft");
            Assert.That(next, Is.Not.Null); Assert.That(next, Is.Not.SameAs(draft));
            Assert.That(Read<bool>(proof, "ZeroPreparedMetadata"), Is.False);
            Assert.That(Read<object>(proof, "MetadataOwnership"), Is.Null);
            Assert.That(Read<object>(proof, "Clone"), Is.Null);
            foreach (var column in (IEnumerable)Read<object>(proof, "ColumnOrder"))
                foreach (var cell in (IEnumerable)Read<object>(column, "Cells"))
                { next.TryGetFallingCell(Read<ulong>(cell, "SourceRun"), Read<int>(cell, "SourceIndex"), out var expected, out _); Assert.That(Read<FallingRun>(cell, "PreparedRun"), Is.SameAs(expected)); }
            foreach (DictionaryEntry pair in Read<IDictionary>(proof, "Caps"))
            {
                ulong id = Read<ulong>(pair.Key, "Id"); bool cohort = Read<bool>(pair.Key, "Cohort");
                Assert.That(Read<object>(pair.Value, cohort ? "Group" : "Legacy"), Is.SameAs(cohort ? (object)next.Cohorts[id] : next.Falling[id]));
            }
            // The full clone migrates native cell/cap proofs at Ready, while
            // continuity guards retain Draft targets until Publish validates.
            // Draft is a COW graph and may still share these owners with Source.
            var source = Read<BlockWorld>(proof, "Source"); var journal = Read<object>(proof, "Journal");
            Assert.That(Read<BlockWorld>(journal, "Next"), Is.SameAs(next));
            foreach (DictionaryEntry pair in Read<IDictionary>(journal, "BindingContinuity"))
            {
                ulong id = (ulong)pair.Key; draft.Cohorts.TryGetValue(id, out var expected); source.Cohorts.TryGetValue(id, out var original);
                Assert.That(Read<object>(pair.Value, "Next"), Is.SameAs(expected)); Assert.That(Read<object>(pair.Value, "Source"), Is.SameAs(original));
            }
            foreach (DictionaryEntry pair in Read<IDictionary>(journal, "LegacyContinuity"))
            {
                ulong id = (ulong)pair.Key; draft.Falling.TryGetValue(id, out var expected); source.Falling.TryGetValue(id, out var original);
                if (expected != null && expected.Cohort != null) expected = null;
                Assert.That(Read<object>(pair.Value, "Next"), Is.SameAs(expected)); Assert.That(Read<object>(pair.Value, "Source"), Is.SameAs(original));
            }
        }
        static void AssertRebound(object proof)
        {
            var next = Read<BlockWorld>(proof, "Next");
            foreach (var column in (IEnumerable)Read<object>(proof, "ColumnOrder"))
                foreach (var cell in (IEnumerable)Read<object>(column, "Cells"))
                { next.TryGetFallingCell(Read<ulong>(cell, "SourceRun"), Read<int>(cell, "SourceIndex"), out var expected, out _); Assert.That(Read<FallingRun>(cell, "PreparedRun"), Is.SameAs(expected)); }
            foreach (DictionaryEntry pair in Read<IDictionary>(proof, "Caps"))
            {
                ulong id = Read<ulong>(pair.Key, "Id"); bool cohort = Read<bool>(pair.Key, "Cohort");
                Assert.That(Read<object>(pair.Value, cohort ? "Group" : "Legacy"), Is.SameAs(cohort ? (object)next.Cohorts[id] : next.Falling[id]));
            }
            var source = Read<BlockWorld>(proof, "Source"); var journal = Read<object>(proof, "Journal");
            foreach (DictionaryEntry pair in Read<IDictionary>(journal, "BindingContinuity"))
            {
                ulong id = (ulong)pair.Key; next.Cohorts.TryGetValue(id, out var expected); source.Cohorts.TryGetValue(id, out var original);
                Assert.That(Read<object>(pair.Value, "Next"), Is.SameAs(expected)); Assert.That(Read<object>(pair.Value, "Source"), Is.SameAs(original));
                if (expected != null) Assert.That(expected, Is.Not.SameAs(original));
            }
            foreach (DictionaryEntry pair in Read<IDictionary>(journal, "LegacyContinuity"))
            {
                ulong id = (ulong)pair.Key; next.Falling.TryGetValue(id, out var expected); source.Falling.TryGetValue(id, out var original);
                if (expected != null && expected.Cohort != null) expected = null;
                Assert.That(Read<object>(pair.Value, "Next"), Is.SameAs(expected)); Assert.That(Read<object>(pair.Value, "Source"), Is.SameAs(original));
                if (expected != null) Assert.That(expected, Is.Not.SameAs(original));
            }
        }
        static void AssertGraph(BlockWorld world)
        {
            foreach (var group in world.Cohorts.Values)
            {
                if (group.LowerSupport != null) Assert.That(group.LowerSupport, Is.SameAs(world.Cohorts[group.LowerSupport.Id]));
                foreach (ulong id in group.MemberIds) Assert.That(world.Falling[id].Cohort, Is.SameAs(group));
            }
        }
        // Every call still encodes and compares the complete source snapshot.
        // Avoid NUnit's per-byte boxing on the success path; retain its diff
        // when a mismatch is found, without changing any loop or test budget.
        static void AssertSnapshotBytesEqual(byte[] expected, byte[] actual)
        {
            bool equal = expected != null && actual != null && expected.Length == actual.Length;
            if (equal)
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i]) { equal = false; break; }
            if (!equal) CollectionAssert.AreEqual(expected, actual);
            Assert.That(equal, Is.True);
        }
        static void AssertWire(BlockWorld source, BlockWorld current)
        {
            byte[] wire = current.EncodeSnapshot(); CollectionAssert.AreEqual(wire, BlockWorld.DecodeSnapshot(wire).EncodeSnapshot());
            var delta = BlockWorld.DecodeSnapshot(source.EncodeSnapshot()).WithDelta(current.EncodeDelta(source.Revision));
            CollectionAssert.AreEqual(wire, delta.EncodeSnapshot());
        }
        static BlockAttackToken Token(ulong owner) => new(new PaintCredit(owner, 1, 0, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);
        void Remember() => CallFixture("RememberActualPending", _terrain);
        void CallFixture(string method, params object[] args) => Call(_fixture, method, args);
        static Type Nested(string name) => typeof(BlockTerrain).GetNestedType(name, BindingFlags.NonPublic);
        static object New(string name) => Activator.CreateInstance(Nested(name), true);
        static object NewLinkedList(string name) => Activator.CreateInstance(typeof(LinkedList<>).MakeGenericType(Nested(name)));
        static T Read<T>(object target, string field) => (T)target.GetType().GetField(field, Flags).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
        static object Call(object target, string method, params object[] args)
        {
            var candidates = target.GetType().GetMethods(Flags).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
            var selected = candidates.Single(m => m.GetParameters().Select((p, i) => args[i] == null
                || p.ParameterType.IsInstanceOfType(args[i]) || p.ParameterType.IsByRef && p.ParameterType.GetElementType().IsInstanceOfType(args[i])).All(x => x));
            return selected.Invoke(target, args);
        }
    }
}
#endif
