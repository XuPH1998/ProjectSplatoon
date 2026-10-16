using System;
using System.Diagnostics;

namespace Splatoon.Blocks
{
    public enum BlockPublicationAdmission
    {
        Ready, InvalidWindow, AnotherAtomicAdmissionActive, DeferKeysUntilNextFrame, DeferCpuUntilNextFrame,
        ReadyWithObservedCpuOverrun
    }

    // Used by the Reports Terrain candidate for full synchronous Tick/Update,
    // clone, PhysX, native swap, recovery and callbacks. Not applied or measured.
    // A time gate cannot preempt one atomic native call. Overruns remain failures.
    public sealed class BlockPublicationFrameBudget
    {
        int _frame = -1, _commits, _keys;
        long _chargedTicks, _scopeStart;
        bool _scopeOpen;
        Reservation _reservation;
        public int Frame => _frame;
        public int Commits => _commits;
        public int ChangedKeys => _keys;
        public long ObservedOverrunAdmissions { get; private set; }
        public double PeakObservedSynchronousMilliseconds { get; private set; }
        public double ObservedSynchronousMilliseconds => TicksToMilliseconds(
            _chargedTicks + (_scopeOpen ? Stopwatch.GetTimestamp() - _scopeStart : 0));
        static double TicksToMilliseconds(long ticks) => 1000d * ticks / Stopwatch.Frequency;
        public void BeginFrame(int frame)
        {
            if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
            if (_frame == frame) return;
            if (_scopeOpen || _reservation != null) throw new InvalidOperationException("Frame cannot change within synchronous work or an atomic publication admission.");
            _frame = frame; _commits = _keys = 0; _chargedTicks = 0;
        }
        public IDisposable MeasureSynchronousWork(int frame)
        {
            BeginFrame(frame);
            if (_scopeOpen) throw new InvalidOperationException("Nested timing scopes would double-count terrain CPU wall time.");
            _scopeStart = Stopwatch.GetTimestamp(); _scopeOpen = true;
            return new Scope(this);
        }
        void FinishScope()
        {
            if (!_scopeOpen) throw new InvalidOperationException("No synchronous scope is active.");
            _chargedTicks += Stopwatch.GetTimestamp() - _scopeStart; _scopeOpen = false;
            PeakObservedSynchronousMilliseconds = Math.Max(PeakObservedSynchronousMilliseconds, ObservedSynchronousMilliseconds);
        }
        sealed class Scope : IDisposable
        {
            BlockPublicationFrameBudget _owner;
            public Scope(BlockPublicationFrameBudget owner) => _owner = owner;
            public void Dispose()
            { var owner = _owner; _owner = null; owner?.FinishScope(); }
        }
        public BlockPublicationAdmission Evaluate(BlockPublicationWindow window, int completeChangedKeys)
        {
            if (window == null || _frame < 0 || completeChangedKeys < 0
                || completeChangedKeys > window.MaximumChangedKeys) return BlockPublicationAdmission.InvalidWindow;
            if (_reservation != null) return BlockPublicationAdmission.AnotherAtomicAdmissionActive;
            // Use the immutable pending policy, never the newest global policy.
            // A downgrade therefore cannot strand a previously admitted group.
            if ((long)_keys + completeChangedKeys > window.Policy.FrameKeys)
                return BlockPublicationAdmission.DeferKeysUntilNextFrame;
            if (ObservedSynchronousMilliseconds >= window.Policy.FrameSynchronousMilliseconds)
                return _commits == 0 ? BlockPublicationAdmission.ReadyWithObservedCpuOverrun
                    : BlockPublicationAdmission.DeferCpuUntilNextFrame;
            return BlockPublicationAdmission.Ready;
        }
        public bool TryReserve(BlockPublicationWindow window, int completeChangedKeys,
            out Reservation reservation, out BlockPublicationAdmission admission)
        {
            admission = Evaluate(window, completeChangedKeys);
            reservation = null;
            if (admission != BlockPublicationAdmission.Ready && admission != BlockPublicationAdmission.ReadyWithObservedCpuOverrun)
                return false;
            reservation = _reservation = new Reservation(this, window, completeChangedKeys, _frame, admission);
            return true;
        }
        void Commit(Reservation reservation)
        {
            if (!ReferenceEquals(_reservation, reservation) || reservation.Frame != _frame)
                throw new InvalidOperationException("Atomic admission belongs to another frame or owner.");
            // The native swap may exceed the time gate. Do not reevaluate and
            // throw after visible publication; record the real overrun instead.
            if (reservation.Admission == BlockPublicationAdmission.ReadyWithObservedCpuOverrun
                || ObservedSynchronousMilliseconds >= reservation.Window.Policy.FrameSynchronousMilliseconds)
                ObservedOverrunAdmissions++;
            _keys += reservation.Keys; _commits++; _reservation = null;
        }
        public sealed class Reservation : IDisposable
        {
            BlockPublicationFrameBudget _owner;
            public BlockPublicationWindow Window { get; }
            public int Keys { get; }
            public int Frame { get; }
            public BlockPublicationAdmission Admission { get; }
            internal Reservation(BlockPublicationFrameBudget owner, BlockPublicationWindow window, int keys,
                int frame, BlockPublicationAdmission admission)
            { _owner = owner; Window = window; Keys = keys; Frame = frame; Admission = admission; }
            public void Commit()
            {
                var owner = _owner;
                if (owner == null) throw new InvalidOperationException("Atomic admission is already completed or cancelled.");
                owner.Commit(this); _owner = null;
            }
            public void Dispose()
            {
                var owner = _owner; _owner = null;
                if (owner != null && ReferenceEquals(owner._reservation, this)) owner._reservation = null;
            }
        }
    }
}
