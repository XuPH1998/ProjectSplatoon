using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridSendWindowTests
    {
        static BlockFixedGridFragment Manifest(byte[] bytes, uint transfer = 1, bool full = true,
            uint baseline = 0, uint revision = 1, uint surfaceBaseline = 0, uint surfaceRevision = 1, uint epoch = 3)
            => new() { Version = BlockFixedGridCodec.ProtocolVersion, Epoch = epoch, Round = 7,
                Transfer = transfer, Sequence = transfer, Full = full, BaseRevision = baseline, Revision = revision,
                SurfaceBaseRevision = surfaceBaseline, SurfaceRevision = surfaceRevision, Length = bytes.Length,
                Hash = BlockTransferFragment.Checksum(bytes) };
        static bool Progress(BlockFixedGridSendWindow window, BlockFixedGridFragment value, int through, double now = .1)
            => window.AcknowledgeProgress(value.Epoch, value.Round, value.Transfer, value.Sequence, value.Revision,
                value.SurfaceRevision, value.Hash, value.Frozen, through, now);
        static bool Commit(BlockFixedGridSendWindow window, BlockFixedGridFragment value, out BlockFixedGridFragment through, double now = .2)
            => window.AcknowledgeCommit(value.Epoch, value.Round, value.Transfer, value.Sequence, value.Revision,
                value.SurfaceRevision, value.Hash, value.Frozen, now, out through);
        static void QueueRemaining(BlockFixedGridSendWindow window, double now = .01)
        {
            while (window.TrySelect(now, out var transfer, out int index, out bool repeat))
            { Assert.That(repeat, Is.False); window.MarkQueued(transfer, index, false, now); }
        }

        [Test] public void ChainedDeltaStreamsBeforeCommitReceiptWithoutAdvancingConfirmedBaseline()
        {
            var bytes = new byte[1000]; var window = new BlockFixedGridSendWindow();
            var full = Manifest(bytes, revision: 8, surfaceRevision: 19);
            window.Enqueue(full, bytes, 0); QueueRemaining(window);
            var delta = Manifest(bytes, 2, false, 8, 9, 19, 25);
            window.Enqueue(delta, bytes, .02); QueueRemaining(window, .03);
            Assert.That(window.Count, Is.EqualTo(2));
            Assert.That(Progress(window, full, full.Count), Is.True);
            Assert.That(window.Count, Is.EqualTo(2), "Receiving bytes cannot acknowledge an atomic world install.");
            Assert.That(Commit(window, delta, out var confirmed), Is.True);
            Assert.That(confirmed.Transfer, Is.Zero, "A later commit receipt cannot skip an unconfirmed predecessor.");
            Assert.That(window.Count, Is.EqualTo(2));
            Assert.That(Commit(window, full, out confirmed), Is.True);
            Assert.That(confirmed.SameTransfer(delta), Is.True); Assert.That(window.Count, Is.Zero);
            Assert.That(Commit(window, full, out confirmed), Is.True, "Exact delayed receipts are idempotent.");
            Assert.That(confirmed.Transfer, Is.Zero); Assert.That(window.Count, Is.Zero);
        }

        [Test] public void ContinuousReceivePrefixBoundsNativeQueueAndExactDuplicatesDoNotInflateProgress()
        {
            var bytes = new byte[900 * 100]; var manifest = Manifest(bytes); var window = new BlockFixedGridSendWindow();
            window.Enqueue(manifest, bytes, 0); QueueRemaining(window);
            Assert.That(window.Head.Cursor, Is.EqualTo(32)); Assert.That(window.UnreceivedFragments, Is.EqualTo(32));
            Assert.That(Progress(window, manifest, 33), Is.False, "A receiver cannot acknowledge bytes never queued.");
            var forged = manifest; forged.Hash++;
            Assert.That(Progress(window, forged, 16), Is.False);
            Assert.That(window.TrySelect(.6, out var retry, out int index, out bool repeat), Is.True);
            Assert.That(repeat, Is.True); Assert.That(index, Is.EqualTo(31));
            window.MarkQueued(retry, index, repeat, .6);
            Assert.That(window.UnreceivedFragments, Is.EqualTo(32)); Assert.That(window.Head.Cursor, Is.EqualTo(32));
            Assert.That(window.Head.LastProgress, Is.Zero, "Repeating bytes is not receiver progress.");
            Assert.That(Progress(window, manifest, 16, .7), Is.True); QueueRemaining(window, .71);
            Assert.That(window.Head.Cursor, Is.EqualTo(48)); Assert.That(window.UnreceivedFragments, Is.EqualTo(32));
            Assert.That(Progress(window, manifest, 16, 20), Is.True);
            Assert.That(window.Head.LastProgress, Is.EqualTo(.7), "Duplicate receipts cannot defeat a stall watchdog.");
            Assert.That(Progress(window, manifest, 15), Is.False);
            Assert.That(Commit(window, manifest, out _), Is.False, "A partial transfer cannot acknowledge a commit.");
        }

        [Test] public void PendingCommitAndPairedBaselineLimitsCannotBeBypassed()
        {
            var bytes = new byte[100]; var window = new BlockFixedGridSendWindow();
            var first = Manifest(bytes); window.Enqueue(first, bytes, 0); QueueRemaining(window);
            var wrongSurface = Manifest(bytes, 2, false, 1, 2, 0, 2);
            Assert.Throws<InvalidOperationException>(() => window.Enqueue(wrongSurface, bytes, .02));
            for (uint id = 2; id <= 4; id++)
            {
                var next = Manifest(bytes, id, false, id - 1, id, id - 1, id);
                window.Enqueue(next, bytes, .02); QueueRemaining(window, .03);
            }
            Assert.That(window.CanCapture, Is.False);
            Assert.Throws<InvalidOperationException>(() => window.Enqueue(Manifest(bytes, 5, false, 4, 5, 4, 5), bytes, .04));
            Assert.That(window.Count, Is.EqualTo(BlockFixedGridSendWindow.MaximumPendingTransfers));
        }

        [Test] public void StaleEpochTransferAndEitherRevisionCannotReleaseANewLifecycleWindow()
        {
            var bytes = new byte[100]; var window = new BlockFixedGridSendWindow(); var old = Manifest(bytes);
            window.Enqueue(old, bytes, 0); QueueRemaining(window); window.Clear();
            var current = Manifest(bytes, 2, epoch: 4); window.Enqueue(current, bytes, 0); QueueRemaining(window);
            Assert.That(Progress(window, old, 1), Is.False); Assert.That(Commit(window, old, out _), Is.False);
            foreach (var mutate in new Action<BlockFixedGridFragmentBox>[] {
                box => box.Value.Round++, box => box.Value.Epoch++, box => box.Value.Transfer++, box => box.Value.Sequence++,
                box => box.Value.Revision++, box => box.Value.SurfaceRevision++, box => box.Value.Hash++, box => box.Value.Frozen = true })
            {
                var box = new BlockFixedGridFragmentBox { Value = current }; mutate(box);
                Assert.That(Progress(window, box.Value, 1), Is.False); Assert.That(Commit(window, box.Value, out _), Is.False);
            }
            Assert.That(window.UnreceivedFragments, Is.EqualTo(1));
            Assert.That(Commit(window, current, out var through), Is.True); Assert.That(through.SameTransfer(current), Is.True);
        }
        sealed class BlockFixedGridFragmentBox { public BlockFixedGridFragment Value; }

        sealed class Receipt
        {
            public double Due;
            public int Peer, Through;
            public bool Commit;
            public BlockFixedGridFragment Manifest;
        }
        [TestCase(.1)] [TestCase(.2)]
        public void SevenPeersDrainLargeReliableSnapshotsAtFourRenderFramesPerSecond(double receiptDelay)
        {
            // Approximately the 62 natural world's 23,509 cells + 7,856 surface cells.
            // The old shared 16-fragment/render-frame limit needs about 190 seconds.
            // The window must retain strict bounded queueing while using the byte budget.
            var bytes = new byte[1_571_400]; var windows = new BlockFixedGridSendWindow[7];
            var assemblies = new BlockFixedGridAssembly[7]; var receipts = new List<Receipt>();
            for (int peer = 0; peer < windows.Length; peer++)
            { windows[peer] = new BlockFixedGridSendWindow(); windows[peer].Enqueue(Manifest(bytes), bytes, 0); }
            const double step = .25, rate = 2 * 1024 * 1024;
            double budget = rate * .1, completedAt = -1; int maximumQueued = 0, cursor = 0;
            for (double now = 0; now < 20; now += step)
            {
                for (int i = receipts.Count - 1; i >= 0; i--)
                    if (receipts[i].Due <= now)
                    {
                        var receipt = receipts[i]; receipts.RemoveAt(i); var window = windows[receipt.Peer];
                        // Duplicated receipts after a commit are harmless obsolete observations.
                        if (!window.Contains(receipt.Manifest.Transfer)) continue;
                        if (receipt.Commit) Assert.That(Commit(window, receipt.Manifest, out _, now), Is.True);
                        else Progress(window, receipt.Manifest, receipt.Through, now);
                    }
                bool complete = true; foreach (var window in windows) complete &= window.Count == 0;
                if (complete) { completedAt = now; break; }
                budget = Math.Min(rate * .25, budget + (now == 0 ? 0 : step * rate)); int idle = 0;
                while (idle < windows.Length)
                {
                    int peer = cursor++ % windows.Length; var window = windows[peer];
                    if (!window.TrySelect(now, out var transfer, out int index, out bool repeat)) { idle++; continue; }
                    int size = Math.Min(BlockFixedGridFragment.PayloadBytes, bytes.Length - index * BlockFixedGridFragment.PayloadBytes);
                    if (budget < size + 120) break;
                    var part = transfer.Manifest; part.Index = index; part.Data = new byte[size];
                    Buffer.BlockCopy(bytes, index * BlockFixedGridFragment.PayloadBytes, part.Data, 0, size);
                    window.MarkQueued(transfer, index, repeat, now); budget -= size + 120; idle = 0;
                    maximumQueued = Math.Max(maximumQueued, window.UnreceivedFragments);
                    var assembly = assemblies[peer] ??= new BlockFixedGridAssembly(part); int before = assembly.ReceivedThrough;
                    Assert.That(assembly.Add(part), Is.True);
                    if (assembly.Complete || assembly.ReceivedThrough / 16 > before / 16 || repeat)
                        receipts.Add(new Receipt { Due = now + receiptDelay, Peer = peer, Manifest = part,
                            Through = assembly.ReceivedThrough, Commit = assembly.Complete });
                }
            }
            Assert.That(completedAt, Is.GreaterThan(0).And.LessThan(20));
            Assert.That(maximumQueued, Is.EqualTo(BlockFixedGridSendWindow.MaximumUnreceivedFragments));
            foreach (var assembly in assemblies) Assert.That(assembly.Verify(), Is.True);
        }
    }
}
