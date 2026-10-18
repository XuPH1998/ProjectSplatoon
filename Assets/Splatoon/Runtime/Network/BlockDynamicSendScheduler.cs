using System;
using System.Collections.Generic;
using System.Diagnostics;
using Splatoon.Blocks;

namespace Splatoon.Networking
{
    /// <summary>Whole-room bounded service; the adapter submits an RPC before reporting its charge.</summary>
    public sealed class BlockDynamicSendScheduler
    {
        public const int PacketLimit = 128, ScanLimitPerVisit = 64, ScanLimitPerCall = 4096;
        public const double SoftLimitMilliseconds = 2;
        public enum Kind { Lifecycle, Motion }
        public enum Stop { Idle, Packets, Time, Visits, Credit, Scans, Empty }
        public sealed class Peer
        {
            public bool PreferMotion;
            public readonly MotionCursor Motion = new();
        }
        public interface ISource
        {
            Peer GetPeer(int index);
            // Return zero when no legal packet was submitted. Never charge an attempted/failed RPC.
            int TrySend(int index, Kind kind, Window window);
        }
        public struct Result
        {
            public int Visits, LifecyclePackets, MotionPackets, ChargedBytes, Scans;
            public double ElapsedMilliseconds;
            public Stop Reason;
            public int Packets => LifecyclePackets + MotionPackets;
        }
        public sealed class Window
        {
            readonly Func<double> _clock;
            double _deadline;
            int _visitScans, _totalScans;
            internal Window(Func<double> clock) { _clock = clock; }
            public double Allowance { get; internal set; }
            public bool Expired => _clock() >= _deadline;
            public int Scans => _totalScans;
            public bool ScanLimitReached => _totalScans >= ScanLimitPerCall;
            internal void Begin(double deadline) { _deadline = deadline; _totalScans = 0; }
            internal void BeginVisit(double allowance) { Allowance = allowance; _visitScans = 0; }
            public bool TryInspect()
            {
                if (_visitScans >= ScanLimitPerVisit || ScanLimitReached) return false;
                if ((_visitScans & 31) == 0 && Expired) return false;
                _visitScans++; _totalScans++; return true;
            }
        }

        /// <summary>One pass per capture; selected but unsubmitted known IDs remain retryable.</summary>
        public sealed class MotionCursor
        {
            ulong _capture;
            bool _initialized;
            int _cursor, _remaining;
            public int Remaining => _remaining;
            public int Position => _cursor;
            public readonly struct Selection
            {
                internal readonly int Next, Left, Skipped, SkippedLeft;
                internal Selection(int next, int left, int skipped, int skippedLeft)
                { Next = next; Left = left; Skipped = skipped; SkippedLeft = skippedLeft; }
            }
            public Selection Select(ulong captureSerial, IReadOnlyList<BlockDynamicState> states,
                HashSet<ulong> known, int maximum, Window window, List<BlockDynamicState> output)
            {
                output.Clear();
                if (!_initialized || _capture != captureSerial)
                {
                    _initialized = true; _capture = captureSerial;
                    _remaining = states.Count; _cursor = states.Count == 0 ? 0 : _cursor % states.Count;
                }
                int next = _cursor, left = _remaining, skipped = next, skippedLeft = left;
                while (left > 0 && output.Count < maximum && window.TryInspect())
                {
                    var state = states[next];
                    next++; if (next == states.Count) next = 0;
                    left--;
                    if (known.Contains(state.Id)) output.Add(state);
                    else if (output.Count == 0) { skipped = next; skippedLeft = left; }
                }
                return new Selection(next, left, skipped, skippedLeft);
            }
            public void Commit(Selection selection, bool submitted)
            {
                _cursor = submitted ? selection.Next : selection.Skipped;
                _remaining = submitted ? selection.Left : selection.SkippedLeft;
            }
        }

        readonly Func<double> _clock;
        readonly Window _window;
        int _cursor;
        public BlockDynamicSendScheduler(Func<double> milliseconds = null)
        {
            _clock = milliseconds ?? ClockMilliseconds;
            _window = new Window(_clock);
        }
        static double ClockMilliseconds() => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
        public void Reset() { _cursor = 0; }
        public Result Run(int peerCount, ISource source, ref double roomCredit, ref double dynamicCredit)
        {
            var result = new Result { Reason = Stop.Empty };
            if (peerCount <= 0) return result;
            double started = _clock(); _window.Begin(started + SoftLimitMilliseconds);
            int idle = 0;
            long visitLimit = (long)PacketLimit * peerCount + peerCount;
            _cursor %= peerCount;
            while (true)
            {
                if (result.Packets >= PacketLimit) { result.Reason = Stop.Packets; break; }
                if (_window.Expired) { result.Reason = Stop.Time; break; }
                if (result.Visits >= visitLimit) { result.Reason = Stop.Visits; break; }
                if (idle >= peerCount) { result.Reason = Stop.Idle; break; }
                double allowance = Math.Min(roomCredit, dynamicCredit);
                if (allowance < 112) { result.Reason = Stop.Credit; break; }
                int index = _cursor; _cursor++; if (_cursor == peerCount) _cursor = 0;
                result.Visits++; _window.BeginVisit(allowance);
                var peer = source.GetPeer(index);
                if (peer == null) { idle++; continue; }
                Kind first = peer.PreferMotion ? Kind.Motion : Kind.Lifecycle;
                Kind kind = first;
                int charged = source.TrySend(index, kind, _window);
                if (charged == 0 && !_window.Expired)
                { kind = first == Kind.Motion ? Kind.Lifecycle : Kind.Motion; charged = source.TrySend(index, kind, _window); }
                if (charged == 0) { idle++; continue; }
                if (charged < 0 || charged > allowance) throw new InvalidOperationException("Dynamic RPC charge exceeds its service window.");
                roomCredit -= charged; dynamicCredit -= charged;
                result.ChargedBytes += charged;
                if (kind == Kind.Lifecycle) result.LifecyclePackets++; else result.MotionPackets++;
                peer.PreferMotion = kind == Kind.Lifecycle;
                idle = 0;
            }
            result.Scans = _window.Scans;
            result.ElapsedMilliseconds = Math.Max(0, _clock() - started);
            // Scan exhaustion does not block affordable lifecycle packets. Attribute it only at idle exit.
            if (result.Reason == Stop.Idle && _window.ScanLimitReached) result.Reason = Stop.Scans;
            return result;
        }
    }
}
