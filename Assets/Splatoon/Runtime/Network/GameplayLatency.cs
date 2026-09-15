using System;

namespace Splatoon.Networking
{
    /// <summary>Application echo RTT, including both peers' network/frame scheduling.</summary>
    public sealed class GameplayLatency
    {
        public const double IntervalSeconds = .5, StaleSeconds = 3;
        readonly uint[] _ids = new uint[8];
        readonly double[] _sent = new double[8];
        readonly bool[] _pending = new bool[8];
        uint _sequence, _lastReply;
        double _nextSend, _receivedAt;
        public uint Samples { get; private set; }
        public double Milliseconds { get; private set; }

        public bool TryBegin(double now, out uint id)
        {
            id = 0;
            if (!double.IsFinite(now) || now < _nextSend) return false;
            _nextSend = now + IntervalSeconds; // A stalled frame must not burst old probes.
            id = ++_sequence;
            int slot = (int)(id % (uint)_ids.Length);
            _ids[slot] = id; _sent[slot] = now; _pending[slot] = true;
            return true;
        }

        public bool Complete(uint id, double now)
        {
            int slot = (int)(id % (uint)_ids.Length);
            if (!_pending[slot] || _ids[slot] != id || !double.IsFinite(now) || now < _sent[slot]) return false;
            _pending[slot] = false;
            if (now - _sent[slot] > StaleSeconds || (Samples > 0 && unchecked((int)(id - _lastReply)) <= 0)) return false;
            Milliseconds = (now - _sent[slot]) * 1000;
            _receivedAt = now; _lastReply = id; Samples++;
            return true;
        }

        public bool TryRead(double now, out double milliseconds)
        {
            milliseconds = Milliseconds;
            return Samples > 0 && double.IsFinite(now) && now >= _receivedAt && now - _receivedAt <= StaleSeconds;
        }

        public void Reset()
        {
            Array.Clear(_pending, 0, _pending.Length);
            Samples = 0; Milliseconds = 0; _nextSend = 0;
            // Do not reuse ids: a reply from before Reset cannot become a new sample.
        }
    }

    /// <summary>Bounded, local-clock input creation-to-authoritative-ack measurements.</summary>
    public sealed class InputAcknowledgementTiming
    {
        struct Entry { public uint Sequence, Revision; public double Created; public bool Pending; }
        readonly Entry[] _entries = new Entry[128];
        public uint Samples { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public double MaxMilliseconds { get; private set; }
        public double LastMilliseconds { get; private set; }
        public void Track(uint sequence, uint revision, double now) => _entries[sequence % (uint)_entries.Length] =
            new Entry { Sequence = sequence, Revision = revision, Created = now, Pending = true };
        public void Acknowledge(uint sequence, uint revision, double now)
        {
            uint newest = 0; bool measured = false;
            for (int i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (!entry.Pending) continue;
                if (entry.Revision != revision) { entry.Pending = false; continue; }
                if (entry.Sequence > sequence) continue;
                entry.Pending = false;
                double milliseconds = (now - entry.Created) * 1000;
                if (!double.IsFinite(milliseconds) || milliseconds < 0) continue;
                if (!measured || entry.Sequence > newest) { LastMilliseconds = milliseconds; newest = entry.Sequence; measured = true; }
                TotalMilliseconds += milliseconds;
                MaxMilliseconds = Math.Max(MaxMilliseconds, milliseconds); Samples++;
            }
        }
        public void DiscardPending() => Array.Clear(_entries, 0, _entries.Length);
        public void Reset() { DiscardPending(); Samples = 0; TotalMilliseconds = MaxMilliseconds = LastMilliseconds = 0; }
    }
}
