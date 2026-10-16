using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;

// Prepared against the proposed public BlockWorkerQueue API only.
// This file has not been compiled, imported into Unity, or executed.
// These contracts cover admission/ownership; they do not exercise Physics.BakeMesh.
public sealed class BlockWorkerQueueContractTests
{
    const int FailureDeadlineMilliseconds = 3000;

    static async Task<T> Deadline<T>(Task<T> task)
    {
        if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(FailureDeadlineMilliseconds)), task))
            throw new TimeoutException("Worker queue contract did not make progress before the failure deadline.");
        return await task;
    }

    static async Task Deadline(Task task)
    {
        if (!ReferenceEquals(await Task.WhenAny(task, Task.Delay(FailureDeadlineMilliseconds)), task))
            throw new TimeoutException("Worker queue ownership did not become idle before the failure deadline.");
        await task;
    }

    static async Task AssertCancelled(Task<BlockWorkerQueue.Lease> task)
    {
        bool cancelled = false;
        try { await Deadline(task); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert.That(cancelled, Is.True, "Queued cancellation must complete its caller as cancellation.");
        Assert.That(task.IsCanceled, Is.True, "Cancellation must not be reported as a generation fault.");
    }

    sealed class Rig
    {
        public readonly SemaphoreSlim Raw = new(2, 2);
        public readonly BlockWorkerQueue Queue;
        readonly CancellationTokenSource _shutdown = new();
        readonly List<CancellationTokenSource> _linked = new();
        readonly List<Task<BlockWorkerQueue.Lease>> _requests = new();
        int _rawHeld;

        public Rig(int foregroundBurst = 2) { Queue = new BlockWorkerQueue(Raw, foregroundBurst); }

        public async Task HoldRawAsync(int count)
        {
            for (int i = 0; i < count; i++) { await Deadline(Raw.WaitAsync()); _rawHeld++; }
        }

        public void ReleaseRaw(int count = 1)
        {
            if (count > _rawHeld) throw new InvalidOperationException("Contract tried to release an unowned raw permit.");
            _rawHeld -= count; Raw.Release(count);
        }

        public Task<BlockWorkerQueue.Lease> Request(bool foreground, CancellationToken token = default)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, token);
            _linked.Add(linked);
            var task = Queue.EnterAsync(foreground, linked.Token);
            _requests.Add(task); return task;
        }

        public async Task CleanupAsync()
        {
            _shutdown.Cancel();
            // A granted lease remains reachable even if the assertion body fails.
            // Entered leases are not cancelled: they are explicitly disposed here.
            foreach (var request in _requests)
                if (request.Status == TaskStatus.RanToCompletion) request.Result.Dispose();
            if (_rawHeld != 0) ReleaseRaw(_rawHeld);
            foreach (var request in _requests)
            {
                try { var lease = await Deadline(request); lease.Dispose(); }
                catch (OperationCanceledException) { }
            }
            await Deadline(Queue.WhenIdle);
            foreach (var linked in _linked) linked.Dispose();
            _shutdown.Dispose(); Raw.Dispose();
        }
    }

    [Test]
    public async Task ExistingRawSemaphoreHoldsStillBlockBothPhysicalWorkers()
    {
        var rig = new Rig();
        try
        {
            await rig.HoldRawAsync(2);
            var request = rig.Request(true);
            Assert.That(request.IsCompleted, Is.False);
            rig.ReleaseRaw();
            var lease = await Deadline(request);
            Assert.That(lease.TryEnter(), Is.True);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(0), "One raw hold and one entered lease own both permits.");
            lease.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1));
            rig.ReleaseRaw();
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(2));
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task ForegroundQueuedLaterWinsWhenPhysicalPermitBecomesAvailable()
    {
        var rig = new Rig(32);
        try
        {
            await rig.HoldRawAsync(2);
            var background = rig.Request(false);
            var foreground = rig.Request(true);
            rig.ReleaseRaw();
            var foregroundLease = await Deadline(foreground);
            Assert.That(foregroundLease.TryEnter(), Is.True);
            Assert.That(background.IsCompleted, Is.False, "Selection must happen after acquiring the physical permit.");
            foregroundLease.Dispose();
            var backgroundLease = await Deadline(background);
            Assert.That(backgroundLease.TryEnter(), Is.True);
            backgroundLease.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1));
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task QueuedCancellationDoesNotLeakOrBypassRawSemaphoreOwnership()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await rig.HoldRawAsync(2);
            var cancelledRequest = rig.Request(false, cancellation.Token);
            cancellation.Cancel();
            await AssertCancelled(cancelledRequest);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(0));
            var foreground = rig.Request(true);
            rig.ReleaseRaw();
            var lease = await Deadline(foreground);
            Assert.That(lease.TryEnter(), Is.True);
            lease.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1));
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task CancellationAfterGrantPreventsEntryAndDisposeIsIdempotent()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var lease = await Deadline(rig.Request(false, cancellation.Token));
            cancellation.Cancel();
            Assert.That(lease.TryEnter(), Is.False, "Retirement must close the granted-to-entered gate.");
            lease.Dispose(); lease.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(2), "Cancellation and Dispose must not release the same permit twice.");
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task CancellationAfterEntryDoesNotReleasePermitUntilWorkOwnerDisposes()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var entered = await Deadline(rig.Request(false, cancellation.Token));
            Assert.That(entered.TryEnter(), Is.True);
            await rig.HoldRawAsync(1);
            var foreground = rig.Request(true);
            cancellation.Cancel();
            // This is a failure sentinel for premature native-owner release,
            // not a throughput/latency acceptance measurement.
            Assert.That(ReferenceEquals(await Task.WhenAny(foreground, Task.Delay(75)), foreground), Is.False,
                "Entered work still owns a permit after retirement cancellation.");
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(0));
            entered.Dispose(); entered.Dispose();
            var next = await Deadline(foreground);
            Assert.That(next.TryEnter(), Is.True);
            next.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1));
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task TwoForegroundGrantsGiveAlreadyQueuedBackgroundTheNextOpportunity()
    {
        var rig = new Rig(2);
        try
        {
            await rig.HoldRawAsync(2);
            var background = rig.Request(false);
            var first = rig.Request(true);
            var second = rig.Request(true);
            var third = rig.Request(true);
            rig.ReleaseRaw();
            var firstLease = await Deadline(first);
            Assert.That(firstLease.TryEnter(), Is.True);
            Assert.That(second.IsCompleted, Is.False); Assert.That(background.IsCompleted, Is.False);
            firstLease.Dispose();
            var secondLease = await Deadline(second);
            Assert.That(secondLease.TryEnter(), Is.True);
            Assert.That(third.IsCompleted, Is.False); Assert.That(background.IsCompleted, Is.False);
            secondLease.Dispose();
            var backgroundLease = await Deadline(background);
            Assert.That(backgroundLease.TryEnter(), Is.True);
            Assert.That(third.IsCompleted, Is.False, "Foreground FIFO is bounded by the configured burst while background waits.");
            backgroundLease.Dispose();
            var thirdLease = await Deadline(third);
            Assert.That(thirdLease.TryEnter(), Is.True);
            thirdLease.Dispose();
            await Deadline(rig.Queue.WhenIdle);
            Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1));
        }
        finally { await rig.CleanupAsync(); }
    }

    [Test]
    public async Task ConcurrentGrantAndCancellationKeepExactlyOnePermitOwner()
    {
        var rig = new Rig();
        try
        {
            for (int iteration = 0; iteration < 128; iteration++)
            {
                await rig.HoldRawAsync(2);
                using var cancellation = new CancellationTokenSource();
                var request = rig.Request(false, cancellation.Token);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancel = Task.Run(async () => { await gate.Task; cancellation.Cancel(); });
                var release = Task.Run(async () => { await gate.Task; rig.ReleaseRaw(); });
                gate.SetResult(true);
                await Deadline(Task.WhenAll(cancel, release));
                try
                {
                    var lease = await Deadline(request);
                    Assert.That(lease.TryEnter(), Is.False, "Cancel completed before this caller attempted entry.");
                    lease.Dispose(); lease.Dispose();
                }
                catch (OperationCanceledException)
                {
                    Assert.That(request.IsCanceled, Is.True);
                }
                await Deadline(rig.Queue.WhenIdle);
                Assert.That(rig.Raw.CurrentCount, Is.EqualTo(1), "Race must retain the one outstanding raw hold.");
                rig.ReleaseRaw();
                Assert.That(rig.Raw.CurrentCount, Is.EqualTo(2), "Race cannot leak a grant or double-release a permit.");
            }
        }
        finally { await rig.CleanupAsync(); }
    }
}
