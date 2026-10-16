using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Splatoon.Blocks
{
    // Admission wraps, rather than replaces, the existing physical semaphore.
    // A single dispatcher chooses a queued request AFTER obtaining a permit;
    // old background WaitAsync calls cannot form a physical FIFO ahead of a shot.
    public sealed class BlockWorkerQueue
    {
        internal enum State { Waiting, Granted, Entered, Released }
        internal sealed class Request
        {
            public readonly bool Foreground;
            public readonly CancellationToken Token;
            public readonly TaskCompletionSource<Lease> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public State State;
            public LinkedListNode<Request> Node;
            public CancellationTokenRegistration Registration;
            public bool HasRegistration;
            public Request(bool foreground, CancellationToken token) { Foreground = foreground; Token = token; }
        }

        readonly object _gate = new();
        readonly SemaphoreSlim _physical;
        readonly int _foregroundBurst;
        readonly LinkedList<Request> _foreground = new(), _background = new();
        int _foregroundGranted, _leased, _releasesInFlight;
        bool _dispatching;
        CancellationTokenSource _waitStop;
        TaskCompletionSource<bool> _idle = CompletedIdle();

        public BlockWorkerQueue(SemaphoreSlim physical, int foregroundBurst = 2)
        {
            _physical = physical ?? throw new ArgumentNullException(nameof(physical));
            if (foregroundBurst <= 0) throw new ArgumentOutOfRangeException(nameof(foregroundBurst));
            _foregroundBurst = foregroundBurst;
        }
        static TaskCompletionSource<bool> CompletedIdle()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult(true); return completion;
        }
        public Task WhenIdle { get { lock (_gate) return _idle.Task; } }
        bool NoWaiting => _foreground.Count == 0 && _background.Count == 0;
        void CompleteIdleLocked()
        {
            if (!_dispatching && NoWaiting && _leased == 0 && _releasesInFlight == 0) _idle.TrySetResult(true);
        }
        static void StopWait(CancellationTokenSource stop)
        {
            if (stop == null) return;
            // The dispatcher owns disposal and can finish between this snapshot
            // and cancellation; a disposed completed wait needs no cancellation.
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        static bool DetachRegistrationLocked(Request request, out CancellationTokenRegistration registration)
        {
            registration = request.Registration;
            bool present = request.HasRegistration;
            request.HasRegistration = false; request.Registration = default; return present;
        }

        public Task<Lease> EnterAsync(bool foreground, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Task.FromCanceled<Lease>(token);
            var request = new Request(foreground, token);
            bool start;
            lock (_gate)
            {
                if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                request.Node = (foreground ? _foreground : _background).AddLast(request);
                start = !_dispatching;
                if (start) _dispatching = true;
            }
            if (token.CanBeCanceled)
            {
                // Register outside the queue lock: a previously cancelled token
                // invokes its callback synchronously. Late registration cannot
                // resurrect a released or already entered request.
                var registration = token.Register(() => Cancel(request));
                bool dispose;
                lock (_gate)
                {
                    dispose = request.State == State.Released || request.State == State.Entered;
                    if (!dispose) { request.Registration = registration; request.HasRegistration = true; }
                }
                if (dispose) registration.Dispose();
            }
            if (start) _ = DispatchAsync(); // All faults are observed and forwarded inside the dispatcher.
            return request.Completion.Task;
        }

        void Cancel(Request request)
        {
            CancellationTokenSource stop = null;
            CancellationTokenRegistration registration = default;
            bool disposeRegistration = false, release = false, cancelCompletion = false;
            lock (_gate)
            {
                if (request.State == State.Waiting)
                {
                    request.Node.List.Remove(request.Node); request.Node = null;
                    request.State = State.Released; cancelCompletion = true;
                    disposeRegistration = DetachRegistrationLocked(request, out registration);
                    if (NoWaiting) stop = _waitStop;
                }
                else if (request.State == State.Granted)
                {
                    request.State = State.Released; _leased--; _releasesInFlight++;
                    release = cancelCompletion = true;
                    disposeRegistration = DetachRegistrationLocked(request, out registration);
                }
                // Entered native work keeps its physical ownership until Dispose.
                // Its task is still joined by the pending/retired transaction.
                CompleteIdleLocked();
            }
            if (cancelCompletion) request.Completion.TrySetCanceled(request.Token);
            StopWait(stop);
            if (release) ReleasePhysical();
            if (disposeRegistration) registration.Dispose();
        }

        Request SelectLocked()
        {
            LinkedList<Request> queue;
            if (_foreground.Count != 0 && (_foregroundGranted < _foregroundBurst || _background.Count == 0))
            { queue = _foreground; if (_foregroundGranted < _foregroundBurst) _foregroundGranted++; }
            else { queue = _background; _foregroundGranted = 0; }
            var request = queue.First.Value; queue.RemoveFirst(); request.Node = null;
            request.State = State.Granted; _leased++; return request;
        }
        async Task DispatchAsync()
        {
            try
            {
                while (true)
                {
                    CancellationTokenSource stop;
                    lock (_gate)
                    {
                        if (NoWaiting) { _dispatching = false; CompleteIdleLocked(); return; }
                        stop = new CancellationTokenSource(); _waitStop = stop;
                    }
                    bool acquired = false;
                    try
                    {
                        try { await _physical.WaitAsync(stop.Token).ConfigureAwait(false); acquired = true; }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                        Request request = null;
                        lock (_gate)
                        {
                            if (ReferenceEquals(_waitStop, stop)) _waitStop = null;
                            if (acquired && !NoWaiting) { request = SelectLocked(); acquired = false; }
                        }
                        if (request != null)
                        {
                            var lease = new Lease(this, request);
                            if (request.Token.IsCancellationRequested) Cancel(request);
                            request.Completion.TrySetResult(lease);
                        }
                    }
                    finally
                    {
                        lock (_gate) if (ReferenceEquals(_waitStop, stop)) _waitStop = null;
                        if (acquired) _physical.Release(); // No caller was assigned this physical permit.
                        stop.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                // A failed physical admission cannot strand queued caller tasks.
                // Already entered owners remain untouched and release themselves.
                var failed = new List<Request>();
                lock (_gate)
                {
                    while (!NoWaiting)
                    {
                        var queue = _foreground.Count != 0 ? _foreground : _background;
                        var request = queue.First.Value; queue.RemoveFirst(); request.Node = null;
                        request.State = State.Released; failed.Add(request);
                    }
                    _dispatching = false; CompleteIdleLocked();
                }
                foreach (var request in failed)
                {
                    CancellationTokenRegistration registration; bool disposeRegistration;
                    lock (_gate) disposeRegistration = DetachRegistrationLocked(request, out registration);
                    request.Completion.TrySetException(exception);
                    if (disposeRegistration) registration.Dispose();
                }
            }
        }

        bool TryEnter(Request request)
        {
            CancellationTokenRegistration registration = default;
            bool disposeRegistration = false, entered = false, release = false;
            lock (_gate)
            {
                if (request.State == State.Granted)
                {
                    disposeRegistration = DetachRegistrationLocked(request, out registration);
                    if (request.Token.IsCancellationRequested)
                    { request.State = State.Released; _leased--; _releasesInFlight++; release = true; }
                    else { request.State = State.Entered; entered = true; }
                }
            }
            if (release) ReleasePhysical();
            if (disposeRegistration) registration.Dispose();
            return entered;
        }
        void Release(Request request)
        {
            CancellationTokenRegistration registration = default;
            bool disposeRegistration = false, release = false;
            lock (_gate)
            {
                if (request.State == State.Granted || request.State == State.Entered)
                {
                    request.State = State.Released; _leased--; _releasesInFlight++;
                    release = true; disposeRegistration = DetachRegistrationLocked(request, out registration);
                }
            }
            if (release) ReleasePhysical();
            if (disposeRegistration) registration.Dispose();
        }
        void ReleasePhysical()
        {
            try { _physical.Release(); }
            finally { lock (_gate) { _releasesInFlight--; CompleteIdleLocked(); } }
        }

        public sealed class Lease : IDisposable
        {
            readonly BlockWorkerQueue _owner;
            readonly Request _request;
            internal Lease(BlockWorkerQueue owner, Request request) { _owner = owner; _request = request; }
            public bool TryEnter() => _owner.TryEnter(_request);
            public void Dispose() => _owner.Release(_request);
        }
    }
}
