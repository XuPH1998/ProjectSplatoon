using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class BlockDynamicSendSchedulerTests
    {
        sealed class Source : BlockDynamicSendScheduler.ISource
        {
            public readonly BlockDynamicSendScheduler.Peer[] Peers;
            public readonly List<(int peer, BlockDynamicSendScheduler.Kind kind, int cost)> Sent = new();
            public Func<int, BlockDynamicSendScheduler.Kind, BlockDynamicSendScheduler.Window, int> Work;
            public Source(int count)
            { Peers = new BlockDynamicSendScheduler.Peer[count]; for (int i = 0; i < count; i++) Peers[i] = new(); }
            public BlockDynamicSendScheduler.Peer GetPeer(int index) => Peers[index];
            public int TrySend(int index, BlockDynamicSendScheduler.Kind kind, BlockDynamicSendScheduler.Window window)
            {
                int cost = Work(index, kind, window);
                if (cost <= 0 || cost > window.Allowance) return 0;
                Sent.Add((index, kind, cost)); return cost;
            }
        }
        static BlockDynamicSendScheduler Scheduler() => new(() => 0);
        const BlockDynamicSendScheduler.Kind Life = BlockDynamicSendScheduler.Kind.Lifecycle;
        const BlockDynamicSendScheduler.Kind Motion = BlockDynamicSendScheduler.Kind.Motion;

        [Test] public void OneBusyPeerAndSixIdlePeersConsumeSuccessfulPacketsInsteadOfVisits()
        {
            var source = new Source(7) { Work = (peer, kind, window) => peer == 0 && kind == Life ? 1000 : 0 };
            double room = 200000, dynamic = room;
            var result = Scheduler().Run(7, source, ref room, ref dynamic);
            Assert.That(result.LifecyclePackets, Is.EqualTo(128)); Assert.That(result.MotionPackets, Is.Zero);
            Assert.That(result.Visits, Is.GreaterThan(128));
            Assert.That(result.Reason, Is.EqualTo(BlockDynamicSendScheduler.Stop.Packets));
            Assert.That(room, Is.EqualTo(72000)); Assert.That(dynamic, Is.EqualTo(room));
            foreach (var sent in source.Sent) Assert.That(sent.peer, Is.Zero);
        }
        [Test] public void SevenBusyPeersAndBothClassesRemainFairAcrossSuccessCapBoundaries()
        {
            var source = new Source(7) { Work = (_, _, _) => 200 };
            var scheduler = Scheduler(); double room = 200000, dynamic = room;
            scheduler.Run(7, source, ref room, ref dynamic); scheduler.Run(7, source, ref room, ref dynamic);
            var counts = new int[7]; var life = new int[7]; var motion = new int[7];
            foreach (var sent in source.Sent)
            { counts[sent.peer]++; if (sent.kind == Life) life[sent.peer]++; else motion[sent.peer]++; }
            foreach (int count in counts) Assert.That(count, Is.InRange(36, 37));
            for (int i = 0; i < 7; i++) Assert.That(Math.Abs(life[i] - motion[i]), Is.LessThanOrEqualTo(1));
            Assert.That(source.Sent[128].peer, Is.EqualTo(2), "The next frame resumes the persistent peer round robin.");
        }
        [Test] public void SoftDeadlineBoundsSubsequentSubmissionsAndRetainsTheNextPeer()
        {
            double clock = 0;
            var scheduler = new BlockDynamicSendScheduler(() => clock);
            var source = new Source(7) { Work = (_, kind, _) => { if (kind != Life) return 0; clock += .6; return 200; } };
            double room = 200000, dynamic = room;
            var first = scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(first.Packets, Is.EqualTo(4)); Assert.That(first.Reason, Is.EqualTo(BlockDynamicSendScheduler.Stop.Time));
            Assert.That(first.ElapsedMilliseconds, Is.EqualTo(2.4).Within(.00001), "A single synchronous emit can cross the soft deadline.");
            scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(source.Sent[4].peer, Is.EqualTo(4)); Assert.That(source.Sent.Count, Is.EqualTo(8));
        }
        [Test] public void UnaffordableLargeFragmentDoesNotBlockSmallerMotionOnAnotherPeer()
        {
            var source = new Source(2) { Work = (peer, kind, _) => peer == 0 && kind == Life ? 1000 : peer == 1 && kind == Motion ? 844 : 0 };
            double room = 900, dynamic = 900;
            var result = Scheduler().Run(2, source, ref room, ref dynamic);
            Assert.That(result.MotionPackets, Is.EqualTo(1)); Assert.That(result.LifecyclePackets, Is.Zero);
            Assert.That(room, Is.EqualTo(56)); Assert.That(dynamic, Is.EqualTo(56));
        }
        [Test] public void SamePeerFallsBackToAnAffordableClassAndUnreadyPeerRemainsLifecycleOnly()
        {
            var source = new Source(1) { Work = (_, kind, _) => kind == Life ? 1000 : 162 };
            double room = 200, dynamic = 200;
            Assert.That(Scheduler().Run(1, source, ref room, ref dynamic).MotionPackets, Is.EqualTo(1));
            // Eligibility belongs to the real peer adapter; a baseline-unready/frozen peer offers no motion.
            source.Work = (_, kind, _) => kind == Life ? 1000 : 0;
            room = dynamic = 200;
            Assert.That(Scheduler().Run(1, source, ref room, ref dynamic).Packets, Is.Zero);
        }
        [Test] public void FullIdleCircleAndZeroCreditExitWithoutChargingOrBusySpinning()
        {
            var source = new Source(7) { Work = (_, _, _) => 0 };
            double room = 2000, dynamic = 1000;
            var scheduler = Scheduler(); var result = scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(result.Visits, Is.EqualTo(7)); Assert.That(result.Reason, Is.EqualTo(BlockDynamicSendScheduler.Stop.Idle));
            Assert.That(room, Is.EqualTo(2000)); Assert.That(dynamic, Is.EqualTo(1000));
            dynamic = 0; result = scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(result.Visits, Is.Zero); Assert.That(result.Reason, Is.EqualTo(BlockDynamicSendScheduler.Stop.Credit));
        }
        [Test] public void BothCreditsLimitTheWholeRoomAndResetStartsAtTheFirstPeer()
        {
            var source = new Source(7) { Work = (_, _, _) => 200 };
            double room = 1200, dynamic = 500; var scheduler = Scheduler();
            var result = scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(result.Packets, Is.EqualTo(2)); Assert.That(result.ChargedBytes, Is.EqualTo(400));
            Assert.That(room, Is.EqualTo(800)); Assert.That(dynamic, Is.EqualTo(100));
            scheduler.Reset(); room = dynamic = 200;
            scheduler.Run(7, source, ref room, ref dynamic);
            Assert.That(source.Sent[source.Sent.Count - 1].peer, Is.Zero);
        }

        static List<BlockDynamicState> States(int count)
        {
            var result = new List<BlockDynamicState>(count);
            for (int i = 0; i < count; i++) result.Add(new BlockDynamicState { Id = (ulong)i + 1 });
            return result;
        }
        [Test] public void SparseKnownIdsAdvanceAtMostOneBoundedWindowPerIdleFrame()
        {
            var states = States(1000); var known = new HashSet<ulong> { 150 }; var selected = new List<BlockDynamicState>();
            var source = new Source(1); int calls = 0; var received = new List<ulong>();
            source.Work = (peer, kind, window) => {
                if (kind != Motion) return 0;
                calls++;
                var cursor = source.Peers[peer].Motion;
                var selection = cursor.Select(1, states, known, 12, window, selected);
                cursor.Commit(selection, selected.Count > 0);
                foreach (var state in selected) received.Add(state.Id);
                return selected.Count == 0 ? 0 : 100 + selected.Count * BlockDynamicCodec.StateBytes;
            };
            var scheduler = Scheduler(); double room = 100000, dynamic = room;
            Assert.That(scheduler.Run(1, source, ref room, ref dynamic).Scans, Is.EqualTo(64));
            Assert.That(source.Peers[0].Motion.Position, Is.EqualTo(64));
            Assert.That(scheduler.Run(1, source, ref room, ref dynamic).Scans, Is.EqualTo(64));
            scheduler.Run(1, source, ref room, ref dynamic);
            CollectionAssert.AreEqual(new ulong[] { 150 }, received);
            Assert.That(calls, Is.GreaterThanOrEqualTo(3));
        }
        [Test] public void KnownStateNotSubmittedIsRetriedAndEachCaptureVisitsEachBodyOnlyOnce()
        {
            var states = States(30); var known = new HashSet<ulong>(); foreach (var state in states) known.Add(state.Id);
            var selected = new List<BlockDynamicState>(); var received = new List<ulong>(); bool fail = true; ulong capture = 1;
            var source = new Source(1);
            source.Work = (peer, kind, window) => {
                if (kind != Motion) return 0;
                var cursor = source.Peers[peer].Motion;
                var selection = cursor.Select(capture, states, known, 12, window, selected);
                cursor.Commit(selection, !fail);
                if (fail) { fail = false; return 0; }
                foreach (var state in selected) received.Add(state.Id);
                return selected.Count == 0 ? 0 : 100 + selected.Count * BlockDynamicCodec.StateBytes;
            };
            var scheduler = Scheduler(); double room = 100000, dynamic = room;
            Assert.That(scheduler.Run(1, source, ref room, ref dynamic).Packets, Is.Zero);
            Assert.That(source.Peers[0].Motion.Position, Is.Zero);
            Assert.That(scheduler.Run(1, source, ref room, ref dynamic).MotionPackets, Is.EqualTo(3));
            Assert.That(new HashSet<ulong>(received).Count, Is.EqualTo(30)); Assert.That(received.Count, Is.EqualTo(30));
            Assert.That(scheduler.Run(1, source, ref room, ref dynamic).MotionPackets, Is.Zero);
            capture++; scheduler.Run(1, source, ref room, ref dynamic);
            Assert.That(received.Count, Is.EqualTo(60));
        }
        [Test] public void FailedCandidateCommitsOnlyTheUnknownPrefixBeforeItsFirstKnownId()
        {
            var states = States(200); var known = new HashSet<ulong> { 70 }; var selected = new List<BlockDynamicState>();
            var source = new Source(1); bool fail = true; var received = new List<ulong>();
            source.Work = (peer, kind, window) => {
                if (kind != Motion) return 0;
                var cursor = source.Peers[peer].Motion;
                var selection = cursor.Select(1, states, known, 12, window, selected);
                bool submitted = !fail && selected.Count > 0;
                cursor.Commit(selection, submitted);
                if (selected.Count == 0) return 0;
                if (fail) { fail = false; return 0; }
                foreach (var state in selected) received.Add(state.Id);
                return 100 + selected.Count * BlockDynamicCodec.StateBytes;
            };
            var scheduler = Scheduler(); double room = 100000, dynamic = room;
            scheduler.Run(1, source, ref room, ref dynamic);
            Assert.That(source.Peers[0].Motion.Position, Is.EqualTo(64));
            scheduler.Run(1, source, ref room, ref dynamic);
            Assert.That(source.Peers[0].Motion.Position, Is.EqualTo(69), "Unsubmitted ID70 stays at the cursor; only IDs65–69 were skipped.");
            scheduler.Run(1, source, ref room, ref dynamic);
            CollectionAssert.AreEqual(new ulong[] { 70 }, received);
        }
        [Test] public void GlobalMembershipScanCapStillAllowsLifecycleWork()
        {
            var states = States(10000); var known = new HashSet<ulong>(); var selected = new List<BlockDynamicState>();
            var source = new Source(1);
            source.Work = (peer, kind, window) => {
                if (kind == Life) return 112;
                var cursor = source.Peers[peer].Motion;
                var selection = cursor.Select(1, states, known, 12, window, selected);
                cursor.Commit(selection, false); return 0;
            };
            double room = 100000, dynamic = room;
            var result = Scheduler().Run(1, source, ref room, ref dynamic);
            Assert.That(result.Scans, Is.EqualTo(4096)); Assert.That(result.LifecyclePackets, Is.EqualTo(128));
            Assert.That(source.Peers[0].Motion.Position, Is.EqualTo(4096));
        }
    }
}
