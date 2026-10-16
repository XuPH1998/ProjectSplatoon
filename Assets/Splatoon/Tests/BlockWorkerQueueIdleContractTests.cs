using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;

// Prepared public-API ownership contracts only; not compiled or executed.
// This supplements, rather than replaces, the seven independently prepared contracts.
public sealed class BlockWorkerQueueIdleContractTests
{
    [Test]
    public async Task CancellingEveryWaiterBecomesIdleWhileBothPhysicalPermitsRemainExternallyHeld()
    {
        using var raw = new SemaphoreSlim(2, 2);
        var queue = new BlockWorkerQueue(raw);
        using var cancellation = new CancellationTokenSource();
        await raw.WaitAsync(); await raw.WaitAsync();
        var first = queue.EnterAsync(false, cancellation.Token);
        var second = queue.EnterAsync(true, cancellation.Token);
        try
        {
            cancellation.Cancel();
            foreach (var task in new[] { first, second })
            {
                bool cancelled = false;
                try { await task; } catch (OperationCanceledException) { cancelled = true; }
                Assert.That(cancelled && task.IsCanceled, Is.True);
            }
            var idle = queue.WhenIdle;
            Assert.That(await Task.WhenAny(idle, Task.Delay(3000)), Is.SameAs(idle),
                "An empty logical queue must cancel its physical wait; external raw holders need not release first.");
            await idle;
            Assert.That(raw.CurrentCount, Is.Zero, "Queue cancellation cannot release a permit owned externally.");
        }
        finally
        {
            cancellation.Cancel(); raw.Release(2);
            foreach (var task in new[] { first, second })
            { try { (await task).Dispose(); } catch (OperationCanceledException) { } }
            await queue.WhenIdle;
        }
        Assert.That(raw.CurrentCount, Is.EqualTo(2));
    }

    [Test]
    public async Task PreCancelledAdmissionPreservesAnIdleQueueAndBothAvailablePhysicalPermits()
    {
        using var raw = new SemaphoreSlim(2, 2);
        var queue = new BlockWorkerQueue(raw);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var idle = queue.WhenIdle;
        var task = queue.EnterAsync(true, cancellation.Token);
        bool cancelled = false;
        try { await task; } catch (OperationCanceledException) { cancelled = true; }
        Assert.That(cancelled && task.IsCanceled, Is.True);
        Assert.That(queue.WhenIdle, Is.SameAs(idle));
        Assert.That(idle.IsCompleted, Is.True);
        Assert.That(raw.CurrentCount, Is.EqualTo(2));
    }
}
