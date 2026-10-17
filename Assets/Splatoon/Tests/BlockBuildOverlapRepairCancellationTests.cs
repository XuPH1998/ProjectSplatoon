#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    // Natural public snapshot/attacks create reason 18 and its repair pending.
    // Reflection observes ownership and reuses the existing joined teardown;
    // no repair token, command cursor, private proof or budget is injected.
    public sealed class BlockBuildOverlapRepairCancellationTests
    {
        const double Epoch = 10;
        const int PumpLimit = 2000;
        BlockBuildShallowColumnReplayCandidateTests _fixture;
        BlockTerrain _terrain;
        bool _callbacksLive;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _fixture = new BlockBuildShallowColumnReplayCandidateTests();
            yield return _fixture.SetupAfterEnteringPlayMode();
            _terrain = Read<BlockTerrain>(_fixture, "_terrain");
            _callbacksLive = true;
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            _callbacksLive = false;
            if (_fixture != null) yield return _fixture.TeardownBeforeExitingPlayMode();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrain = null; _fixture = null;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ClearCancelsNaturallyAttachedRepairAndRefundsTheOriginalPaidFifoExactlyOnce()
            => RunCancellation(false);

        [UnityTest, Timeout(600000)]
        public IEnumerator AuthorityLossCancelsNaturallyAttachedRepairWithoutPublishingOrCompletingPaidCommands()
            => RunCancellation(true);

        IEnumerator RunCancellation(bool authorityLoss)
        {
            var seed = MakeShallowCappedColumns();
            byte[] wire = seed.EncodeSnapshot();
            Assert.That(_terrain.ApplyRemoteSnapshot(wire), Is.True);
            for (int frame = 0; frame < PumpLimit
                && (_terrain.Revision != seed.Revision || Read<object>(_terrain, "_pending") != null); frame++)
            {
                RememberPending(); _terrain.Tick(false, Epoch, 1f / 60f); RememberPending();
                yield return null;
            }
            Assert.That(_terrain.Revision, Is.EqualTo(seed.Revision));
            Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
            CollectionAssert.AreEqual(wire, _terrain.ExportSnapshot());
            var originalSource = _terrain.World;
            byte[] originalWire = originalSource.EncodeSnapshot();
            _terrain.Tick(false, Epoch, 1f / 60f);

            var tokens = new[] { MakeToken(101), MakeToken(102) };
            int[] reserved = tokens.Select(token => token.ReserveBuild(1, false)).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 1 }, reserved);
            var callbacks = new int[2];
            var outcomes = new BlockMutationResult[2];
            var order = new List<int>();
            Action<int, BlockMutationResult> finish = (index, result) =>
            {
                // Teardown still refunds real reservations if the test fails early.
                tokens[index].CompleteBuild(reserved[index], false, result.Added);
                if (!_callbacksLive) return;
                callbacks[index]++; outcomes[index] = result; order.Add(index);
                Assert.That(callbacks[index], Is.EqualTo(1), "A paid callback may execute only once.");
            };

            int overlapAborts = 0, commits = 0;
            Action<BlockTerrain.PendingWallDiagnostic> terminal = sample =>
            {
                if (sample.Outcome == BlockTerrain.PendingWallOutcome.Aborted
                    && sample.AbortReason == BlockTerrain.PendingAbortReason.BuildMovingOverlap) overlapAborts++;
            };
            Action<BlockTerrainCommit> published = _ => commits++;
            _terrain.PendingTransactionFinished += terminal;
            _terrain.PublishedCommit += published;
            try
            {
                _terrain.QueueBuild(BlockWorld.Center(new Vector3Int(2, 1, 2)) - Vector3.up * .051f,
                    Vector3.up, 1, reserved[0], .101f, false, result => finish(0, result), true);
                RememberLastCommand();
                _terrain.QueueBuild(BlockWorld.Center(new Vector3Int(30, 0, 30)) - Vector3.up * .051f,
                    Vector3.up, 1, reserved[1], 0, true, result => finish(1, result), true);
                RememberLastCommand();
                object repairPending = null;
                double now = Epoch, deadline = Time.realtimeSinceStartupAsDouble + 120;
                for (int frame = 0; frame < PumpLimit && Time.realtimeSinceStartupAsDouble < deadline; frame++)
                {
                    RememberPending();
                    var pending = Read<object>(_terrain, "_pending");
                    if (pending != null && Read<object>(pending, "BuildOverlapRepair") != null)
                    {
                        // Update can attach a late-bound repair journal without
                        // publishing it. Capture before the next Tick can publish.
                        repairPending = pending;
                        break;
                    }
                    now = Epoch + (frame + 1) / 60d;
                    _terrain.Tick(true, now, 1f / 60f); RememberPending();
                    pending = Read<object>(_terrain, "_pending");
                    if (pending != null && Read<object>(pending, "BuildOverlapRepair") != null)
                    {
                        repairPending = pending;
                        // Do not advance another Tick or yield before cancelling
                        // this actual attached pending; Update itself cannot publish.
                        break;
                    }
                    Assert.That(callbacks, Is.All.EqualTo(0), "Cancellation must catch the original paid FIFO before completion."
                        + $" frame={frame} now={now:R} reason18={overlapAborts} commits={commits}"
                        + $" firstAdded={outcomes[0].Added} firstChanged={outcomes[0].Changed}"
                        + $" secondAdded={outcomes[1].Added} secondChanged={outcomes[1].Changed}"
                        + $" pending={pending != null} activeRepair={Read<object>(_terrain, "_buildOverlapRepair") != null}");
                    yield return null;
                }
                Assert.That(overlapAborts, Is.GreaterThan(0), "The public inputs must really encounter reason 18.");
                Assert.That(repairPending, Is.Not.Null, "A real repair pending must attach naturally within the finite wait.");
                Assert.That(callbacks, Is.All.EqualTo(0));
                Assert.That(Read<ICollection>(repairPending, "Results").Count, Is.Zero);
                Assert.That(Read<ICollection>(repairPending, "CommandCheckpoints")?.Count ?? 0, Is.Zero);
                Assert.That(Read<object>(repairPending, "FairSeed"), Is.Null);
                var repair = Read<object>(repairPending, "BuildOverlapRepair");
                Assert.That(Read<object>(_terrain, "_buildOverlapRepair"), Is.SameAs(repair));
                object window = Read<object>(repairPending, "Window");
                Assert.That(window, Is.SameAs(Read<object>(repair, "Window")));
                Assert.That(window, Is.SameAs(Read<object>(Read<object>(repair, "Head"), "RetryWindow")));
                var before = _terrain.World;
                byte[] beforeWire = _terrain.ExportSnapshot();
                uint beforeRevision = _terrain.Revision;
                long repaired = Read<long>(_terrain, "_buildRepairPublished");
                long noChange = Read<long>(_terrain, "_buildRepairNoChange");
                long cancelled = Read<long>(_terrain, "_buildRepairCancelled");
                int beforeCommits = commits;
                Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(20));

                if (authorityLoss)
                {
                    _terrain.Tick(false, now, 1f / 60f);
                    RememberPending();
                    Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
                    Assert.That(Read<object>(_terrain, "_buildOverlapRepair"), Is.Null);
                    Assert.That(Read<object>(_terrain, "_stageBuildOverlapRepair"), Is.Null);
                    Assert.That(_terrain.World, Is.SameAs(before));
                    Assert.That(_terrain.Revision, Is.EqualTo(beforeRevision));
                    CollectionAssert.AreEqual(beforeWire, _terrain.ExportSnapshot());
                    Assert.That(callbacks, Is.All.EqualTo(0), "Authority loss retires only repair, retaining both original paid commands.");
                    Assert.That(tokens[0].BuildRemaining, Is.EqualTo(tokens[0].BuildLimit - 1));
                    Assert.That(tokens[1].BuildRemaining, Is.EqualTo(tokens[1].BuildLimit - 1));
                    Assert.That(Read<long>(_terrain, "_buildRepairCancelled"), Is.EqualTo(cancelled + 1));
                    Assert.That(commits, Is.EqualTo(beforeCommits));
                }

                RememberPending();
                uint nextRound = _terrain.Round + 1;
                _terrain.Clear(nextRound);
                RememberPending();
                Assert.That(_terrain.Round, Is.EqualTo(nextRound));
                Assert.That(Read<object>(_terrain, "_pending"), Is.Null);
                Assert.That(Read<object>(_terrain, "_buildOverlapRepair"), Is.Null);
                Assert.That(Read<object>(_terrain, "_stageBuildOverlapRepair"), Is.Null);
                Assert.That(_terrain.PendingTransactions, Is.Zero);
                Assert.That(_terrain.World.Chunks, Is.Empty); Assert.That(_terrain.World.Falling, Is.Empty);
                Assert.That(_terrain.PinkCount, Is.Zero); Assert.That(_terrain.BlueCount, Is.Zero);
                CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks);
                CollectionAssert.AreEqual(new[] { 0, 1 }, order);
                Assert.That(outcomes.Select(result => result.Changed), Is.All.EqualTo(0));
                foreach (var token in tokens)
                {
                    Assert.That(token.Added, Is.Zero);
                    Assert.That(token.BuildRemaining, Is.EqualTo(token.BuildLimit));
                    Assert.That(token.TakePoints(), Is.Zero);
                }
                Assert.That(Read<long>(_terrain, "_buildRepairCancelled"), Is.EqualTo(cancelled + 1));
                Assert.That(Read<long>(_terrain, "_buildRepairPublished"), Is.EqualTo(repaired));
                Assert.That(Read<long>(_terrain, "_buildRepairNoChange"), Is.EqualTo(noChange));
                Assert.That(commits, Is.EqualTo(beforeCommits));
                CollectionAssert.AreEqual(originalWire, originalSource.EncodeSnapshot());

                // Let actual retired worker/native ownership progress on the
                // non-authority path; no retained repair may leak a late commit.
                for (int frame = 0; frame < 8; frame++)
                {
                    _terrain.Tick(false, now + frame / 60d, 1f / 60f); RememberPending();
                    yield return null;
                    Assert.That(commits, Is.EqualTo(beforeCommits));
                    CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks);
                    Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero);
                }
            }
            finally
            {
                _terrain.PendingTransactionFinished -= terminal;
                _terrain.PublishedCommit -= published;
            }
        }

        static BlockAttackToken MakeToken(ulong owner)
            => new(new PaintCredit(owner, 1, 0, 1, PaintAttackKind.Main, true), 0, 32, 8, 2);

        static BlockWorld MakeShallowCappedColumns()
        {
            var world = new BlockWorld(new Vector2(32, 64));
            foreach (var at in new Vector2Int[] { new(2, 2), new(2, 1), new(2, 3), new(1, 2), new(3, 2) })
            {
                world.Set(new Vector3Int(at.x, 0, at.y), BlockCell.Create(2));
                for (int y = 4; y < 7; y++) world.Set(new Vector3Int(at.x, y, at.y), BlockCell.Create(2));
                var group = world.BeginCohort(new Vector3Int(at.x, 4, at.y), 3);
                world.DetachCohortPiece(group.Id, 3, Epoch, out _);
                world.SetCohortSupport(group.Id, 0, 0, out _);
                group = world.Cohorts[group.Id];
                group.LandingKnown = true; group.LandingOffset = .3f;
                group.InitialOffset = .3f; group.InitialSpeed = 0; group.NeedsLandingProof = false;
            }
            world.PreparePendingCohortsForPublication(out _);
            world.ActivateRebasePendingCohorts(Epoch);
            world.Seal(1, world.RevisionKeys.ToArray());
            return world;
        }

        void RememberPending() => ObserveFixture("RememberActualPending", _terrain);
        void RememberLastCommand() => ObserveFixture("RememberLastCommand");
        void ObserveFixture(string name, params object[] args)
            => typeof(BlockBuildShallowColumnReplayCandidateTests)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_fixture, args);
        static T Read<T>(object target, string field)
        {
            Assert.That(target, Is.Not.Null, field);
            var member = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(member, Is.Not.Null, field);
            return (T)member.GetValue(target);
        }
    }
}
#endif
