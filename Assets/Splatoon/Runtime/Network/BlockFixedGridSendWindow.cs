using System;
using System.Collections.Generic;

namespace Splatoon.Networking
{
    /// <summary>
    /// A bounded, reliable, ordered stream of paired commits. Queuing bytes is not an
    /// acknowledgement: only exact receiver receipts advance the confirmed baseline.
    /// </summary>
    public sealed class BlockFixedGridSendWindow
    {
        public const int MaximumPendingTransfers = 4, MaximumUnreceivedFragments = 32, ReceiptStride = 16;
        public const double RepeatSeconds = .5;

        public sealed class Transfer
        {
            public readonly BlockFixedGridFragment Manifest;
            public readonly byte[] Bytes;
            public int Cursor { get; internal set; }
            public int ReceivedThrough { get; internal set; }
            public bool Committed { get; internal set; }
            public double LastProgress { get; internal set; }
            public double LastRepeat { get; internal set; }
            public bool FullyQueued => Cursor == Manifest.Count;
            internal Transfer(BlockFixedGridFragment manifest, byte[] bytes, double now)
            { Manifest = manifest; Bytes = bytes; LastProgress = now; }
        }

        readonly List<Transfer> _pending = new(MaximumPendingTransfers);
        readonly Dictionary<ulong, BlockFixedGridFragment> _confirmed = new();
        readonly Queue<ulong> _confirmedOrder = new();
        public int Count => _pending.Count;
        public Transfer Head => Count == 0 ? null : _pending[0];
        public Transfer Tail => Count == 0 ? null : _pending[Count - 1];
        public bool CanCapture => Count < MaximumPendingTransfers && (Tail == null || Tail.FullyQueued);
        public int UnreceivedFragments
        {
            get { int count = 0; foreach (var value in _pending) count += value.Cursor - value.ReceivedThrough; return count; }
        }
        public bool Contains(uint transfer)
        { foreach (var value in _pending) if (value.Manifest.Transfer == transfer) return true; return false; }
        public bool ContainsFull
        { get { foreach (var value in _pending) if (value.Manifest.Full) return true; return false; } }
        public void Clear() { _pending.Clear(); _confirmed.Clear(); _confirmedOrder.Clear(); }

        public void Enqueue(BlockFixedGridFragment manifest, byte[] bytes, double now)
        {
            if (!CanCapture || bytes == null || bytes.Length != manifest.Length ||
                manifest.Version != BlockFixedGridCodec.ProtocolVersion || manifest.Epoch == 0 || manifest.Transfer == 0 ||
                manifest.Sequence == 0 || manifest.Length < BlockFixedGridCodec.HeaderBytes + 30 ||
                manifest.Length > BlockFixedGridCodec.MaxTransferBytes || manifest.Revision < manifest.BaseRevision ||
                manifest.SurfaceRevision < manifest.SurfaceBaseRevision ||
                manifest.Full && (manifest.BaseRevision != 0 || manifest.SurfaceBaseRevision != 0))
                throw new InvalidOperationException("Invalid or unbounded fixed-grid stream transfer.");
            var previous = Tail;
            if (previous != null)
            {
                var before = previous.Manifest;
                if (manifest.Epoch != before.Epoch || manifest.Round != before.Round || manifest.Sequence <= before.Sequence ||
                    manifest.Revision < before.Revision || manifest.SurfaceRevision < before.SurfaceRevision ||
                    before.Frozen && !manifest.Frozen || !manifest.Full &&
                    (manifest.BaseRevision != before.Revision || manifest.SurfaceBaseRevision != before.SurfaceRevision))
                    throw new InvalidOperationException("Fixed-grid stream lost its ordered paired baseline.");
            }
            _pending.Add(new Transfer(manifest, bytes, now));
        }

        public bool TrySelect(double now, out Transfer transfer, out int index, out bool repeat)
        {
            transfer = null; index = 0; repeat = false;
            foreach (var value in _pending)
                if (!value.FullyQueued)
                {
                    if (UnreceivedFragments >= MaximumUnreceivedFragments)
                    {
                        // A progress receipt can itself be delayed. An exact duplicate
                        // requests the current prefix without consuming another slot.
                        if (Head.Cursor == 0 || now - Math.Max(Head.LastRepeat, Head.LastProgress) < RepeatSeconds) return false;
                        transfer = Head; index = Head.Cursor - 1; repeat = true; return true;
                    }
                    transfer = value; index = value.Cursor; return true;
                }
            // A repeated final fragment asks for the exact progress/commit receipt.
            // NGO owns retransmission of missing bytes in its reliable ordered stream.
            if (Head == null || now - Math.Max(Head.LastRepeat, Head.LastProgress) < RepeatSeconds) return false;
            transfer = Head; index = Head.Manifest.Count - 1; repeat = true; return true;
        }
        public void MarkQueued(Transfer transfer, int index, bool repeat, double now)
        {
            if (!_pending.Contains(transfer) || index < 0 || index >= transfer.Manifest.Count ||
                (repeat ? index != transfer.Cursor - 1 :
                    index != transfer.Cursor || UnreceivedFragments >= MaximumUnreceivedFragments))
                throw new InvalidOperationException("Fixed-grid queue cursor did not match its selected fragment.");
            if (repeat) transfer.LastRepeat = now;
            else transfer.Cursor++;
        }
        Transfer Find(uint epoch, uint round, uint transfer, ulong sequence, uint revision, uint surfaceRevision, uint hash, bool frozen)
        {
            foreach (var value in _pending)
                if (value.Manifest.MatchesAcknowledgement(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen)) return value;
            return null;
        }
        public bool AcknowledgeProgress(uint epoch, uint round, uint transfer, ulong sequence, uint revision,
            uint surfaceRevision, uint hash, bool frozen, int receivedThrough, double now)
        {
            var value = Find(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen);
            if (value == null && _confirmed.TryGetValue(sequence, out var previous))
                return previous.MatchesAcknowledgement(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen) &&
                    receivedThrough >= 0 && receivedThrough <= previous.Count;
            if (value == null || receivedThrough < value.ReceivedThrough || receivedThrough > value.Cursor) return false;
            if (receivedThrough > value.ReceivedThrough)
            { value.ReceivedThrough = receivedThrough; value.LastProgress = now; }
            return true;
        }
        public bool AcknowledgeCommit(uint epoch, uint round, uint transfer, ulong sequence, uint revision,
            uint surfaceRevision, uint hash, bool frozen, double now, out BlockFixedGridFragment confirmedThrough)
        {
            confirmedThrough = default;
            var value = Find(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen);
            if (value == null && _confirmed.TryGetValue(sequence, out var previous))
                return previous.MatchesAcknowledgement(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen);
            if (value == null || !value.FullyQueued) return false;
            value.ReceivedThrough = value.Manifest.Count; value.Committed = true; value.LastProgress = now;
            // Out-of-order receipts cannot skip a preceding unconfirmed transaction.
            while (Head != null && Head.Committed)
            {
                confirmedThrough = Head.Manifest; _confirmed.Add(confirmedThrough.Sequence, confirmedThrough);
                _confirmedOrder.Enqueue(confirmedThrough.Sequence); _pending.RemoveAt(0);
                while (_confirmedOrder.Count > MaximumPendingTransfers) _confirmed.Remove(_confirmedOrder.Dequeue());
            }
            return true;
        }
    }
}
