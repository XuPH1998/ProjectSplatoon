using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        sealed class FixedGridPayload
        {
            public BlockFixedGridSnapshot Snapshot;
            public byte[] Bytes;
            public uint Hash;
        }
        sealed class FixedGridPeer
        {
            public bool Ready, Frozen;
            public uint Epoch, Applied, SurfaceApplied;
            public double LastRequest;
            public readonly BlockFixedGridSendWindow Window = new();
        }
        [Serializable] public struct FixedGridSyncStatistics
        {
            public long transfersStarted, fragmentsSent, fragmentsUnique, fragmentsDuplicate, fragmentsRejected;
            public long assembliesCompleted, assembliesAbandoned, applyFailures, requestsSent, requestsReceived;
            public long acknowledgementsApplied, acknowledgementsRejected, sendFailures, sendTimeouts;
            public long progressAcknowledgementsApplied, progressAcknowledgementsRejected;
            public int peers, incomingReceived, incomingCount, queuedCommits, unreceivedFragments;
            public uint epoch, round, revision, surfaceRevision, appliedTick;
            public bool ready, frozen;
            public long bytesSent;
        }
        [Serializable] public struct FixedGridPeerStatistics
        {
            public ulong peer, sequence;
            public bool ready, frozen, full;
            public uint epoch, appliedRevision, appliedSurfaceRevision, targetRevision, targetSurfaceRevision, transfer, captureTick;
            public int fragmentsQueued, fragmentCount, pendingTransfers, unreceivedFragments;
            public double uniqueSendProgressAge;
        }
        readonly Dictionary<ulong, FixedGridPeer> _fixedGridPeers = new();
        readonly List<ulong> _fixedGridClients = new(8), _fixedGridDeparted = new(8);
        readonly Dictionary<(bool full, uint revision, uint surface), FixedGridPayload> _fixedGridEncoded = new();
        readonly Dictionary<ulong, BlockFixedGridFragment> _fixedGridCompletedTransfers = new();
        readonly Queue<ulong> _fixedGridCompletedOrder = new();
        BlockFixedGridAssembly _fixedGridIncoming;
        FixedGridSyncStatistics _fixedGridStats;
        bool _fixedGridReady, _fixedGridRequestSent;
        uint _fixedGridTransferId, _fixedGridAppliedTick;
        ulong _fixedGridSequence, _fixedGridAcceptedSequence;
        double _fixedGridLastRequest, _fixedGridLastUniqueProgress;
        int _fixedGridPeerCursor;
        public bool FixedGridBlockSyncReady => IsServer || _fixedGridReady;
        public uint FixedGridCaptureTick => IsServer ? State.Value.Tick : _fixedGridAppliedTick;
        public FixedGridSyncStatistics InspectFixedGridSyncStatistics()
        {
            var value = _fixedGridStats; var world = Blocks?.FixedBlocks;
            value.peers = _fixedGridPeers.Count; value.epoch = world?.Epoch ?? 0; value.round = world?.Round ?? 0;
            value.revision = world?.Revision ?? 0; value.surfaceRevision = world?.Surfaces.Revision ?? 0;
            value.frozen = world?.Frozen ?? false; value.ready = FixedGridBlockSyncReady; value.appliedTick = FixedGridCaptureTick;
            value.incomingReceived = _fixedGridIncoming?.ReceivedCount ?? 0; value.incomingCount = _fixedGridIncoming?.Manifest.Count ?? 0;
            foreach (var peer in _fixedGridPeers.Values)
            { value.queuedCommits += peer.Window.Count; value.unreceivedFragments += peer.Window.UnreceivedFragments; }
            return value;
        }
        // Diagnostic snapshots allocate only when explicitly requested by a report/probe.
        public FixedGridPeerStatistics[] InspectFixedGridSyncPeers()
        {
            var result = new FixedGridPeerStatistics[_fixedGridPeers.Count]; int index = 0;
            foreach (var pair in _fixedGridPeers)
            {
                var peer = pair.Value; var send = peer.Window.Head; var manifest = send?.Manifest ?? default;
                result[index++] = new FixedGridPeerStatistics { peer = pair.Key, ready = peer.Ready, frozen = peer.Frozen,
                    epoch = peer.Epoch, appliedRevision = peer.Applied, appliedSurfaceRevision = peer.SurfaceApplied,
                    sequence = manifest.Sequence, targetRevision = manifest.Revision, targetSurfaceRevision = manifest.SurfaceRevision,
                    transfer = manifest.Transfer, captureTick = manifest.Tick, full = manifest.Full,
                    fragmentsQueued = send?.Cursor ?? 0, fragmentCount = send == null ? 0 : manifest.Count,
                    pendingTransfers = peer.Window.Count, unreceivedFragments = peer.Window.UnreceivedFragments,
                    uniqueSendProgressAge = send == null ? 0 : Math.Max(0, Time.unscaledTimeAsDouble - send.LastProgress) };
            }
            return result;
        }
        void ResetFixedGridSync()
        {
            _fixedGridPeers.Clear(); _fixedGridClients.Clear(); _fixedGridDeparted.Clear();
            _fixedGridEncoded.Clear(); _fixedGridCompletedTransfers.Clear(); _fixedGridCompletedOrder.Clear();
            _fixedGridIncoming = null; _fixedGridStats = default;
            _fixedGridReady = IsServer; _fixedGridAppliedTick = 0; _fixedGridAcceptedSequence = 0;
            _fixedGridLastRequest = _fixedGridLastUniqueProgress = 0; _fixedGridRequestSent = false; _fixedGridPeerCursor = 0;
            // Transfer IDs and send sequences remain monotonic across round resets.
        }
        void UpdateFixedGridSync()
        {
            var world = Blocks?.FixedBlocks;
            if (world == null) return;
            double now = Time.unscaledTimeAsDouble;
            if (!IsServer)
            {
                if (_fixedGridIncoming != null && now - _fixedGridLastUniqueProgress > Math.Max(15, GameplayConfig.Global.ConnectionTimeout * 2))
                {
                    uint rejected = _fixedGridIncoming.Manifest.Transfer;
                    _fixedGridIncoming = null; _fixedGridStats.assembliesAbandoned++;
                    _fixedGridReady = false; RefreshInitialBlockSync(); RequestFixedGridSnapshot(rejected);
                }
                if (!_fixedGridReady && _fixedGridIncoming == null && (!_fixedGridRequestSent || now - _fixedGridLastRequest > 1))
                    RequestFixedGridSnapshot(0);
                if (_fixedGridReady && State.Value.Phase == MatchPhase.Finished && world.Round == State.Value.Round &&
                    world.Frozen && world.Revision >= State.Value.FixedBlockRevision)
                    Blocks.FinishRound(State.Value.TerrainFrozenAt);
                return;
            }
            // All peers with the same baseline share this frame's immutable encoding.
            // Capture still occurs after authoritative commits and never aliases mutable world data.
            _fixedGridEncoded.Clear();
            _fixedGridClients.Clear();
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client == NetworkManager.LocalClientId) continue;
                _fixedGridClients.Add(client);
                if (!_fixedGridPeers.TryGetValue(client, out var peer)) _fixedGridPeers.Add(client, peer = new FixedGridPeer());
                var head = peer.Window.Head;
                if (head != null && (head.Manifest.Epoch != world.Epoch || head.Manifest.Round != world.Round))
                { peer.Window.Clear(); peer.Ready = false; head = null; }
                if (head != null && now - head.LastProgress > Math.Max(15, GameplayConfig.Global.ConnectionTimeout * 2))
                { peer.Window.Clear(); peer.Ready = false; _fixedGridStats.sendTimeouts++; }
                var tail = peer.Window.Tail;
                bool behind = tail == null ? !peer.Ready || peer.Epoch != world.Epoch || peer.Applied != world.Revision ||
                    peer.SurfaceApplied != world.Surfaces.Revision || peer.Frozen != world.Frozen :
                    tail.Manifest.Revision != world.Revision || tail.Manifest.SurfaceRevision != world.Surfaces.Revision || tail.Manifest.Frozen != world.Frozen;
                if (peer.Window.CanCapture && behind)
                    StartFixedGridTransfer(peer, tail == null && (!peer.Ready || peer.Epoch != world.Epoch), now);
            }
            _fixedGridDeparted.Clear();
            foreach (ulong client in _fixedGridPeers.Keys)
                if (!NetworkManager.ConnectedClients.ContainsKey(client)) _fixedGridDeparted.Add(client);
            foreach (ulong client in _fixedGridDeparted) _fixedGridPeers.Remove(client);
            // Every queued payload owns its detached bytes. Later captures use the tail
            // target, while confirmed peer revisions still advance only on exact commit ACKs.
            uint retainedThrough = world.Revision;
            foreach (var peer in _fixedGridPeers.Values)
            {
                if (peer.Window.Tail != null && peer.Window.Tail.Manifest.Epoch == world.Epoch)
                    retainedThrough = Math.Min(retainedThrough, peer.Window.Tail.Manifest.Revision);
                else if (peer.Ready && peer.Epoch == world.Epoch)
                    retainedThrough = Math.Min(retainedThrough, peer.Applied);
            }
            world.PruneJournal(retainedThrough);
        }
        void StartFixedGridTransfer(FixedGridPeer peer, bool full, double now)
        {
            uint baseline = full ? 0 : (peer.Window.Tail?.Manifest.Revision ?? peer.Applied);
            uint surfaceBaseline = full ? 0 : (peer.Window.Tail?.Manifest.SurfaceRevision ?? peer.SurfaceApplied);
            var key = (full, baseline, surfaceBaseline);
            if (!_fixedGridEncoded.TryGetValue(key, out var payload))
            {
                using var timing = BlockTransferEncodeCpu.Auto();
                var captured = Blocks.FixedBlocks.Capture(full, baseline, surfaceBaseline);
                byte[] encoded = BlockFixedGridCodec.Encode(captured);
                payload = new FixedGridPayload { Snapshot = captured, Bytes = encoded, Hash = BlockTransferFragment.Checksum(encoded) };
                _fixedGridEncoded.Add(key, payload);
            }
            var snapshot = payload.Snapshot; byte[] bytes = payload.Bytes;
            uint transfer = ++_fixedGridTransferId; if (transfer == 0) transfer = ++_fixedGridTransferId;
            ulong sequence = ++_fixedGridSequence;
            if (sequence == 0) throw new InvalidOperationException("Fixed-grid transfer sequence exhausted.");
            peer.Window.Enqueue(new BlockFixedGridFragment { Version = BlockFixedGridCodec.ProtocolVersion,
                    Epoch = snapshot.Epoch, Round = snapshot.Round, Transfer = transfer, Sequence = sequence,
                    BaseRevision = snapshot.BaseRevision, Revision = snapshot.Revision,
                    SurfaceBaseRevision = snapshot.SurfaceBaseRevision, SurfaceRevision = snapshot.SurfaceRevision,
                    Full = snapshot.Full, Frozen = snapshot.Frozen, Tick = State.Value.Tick,
                    Length = bytes.Length, Hash = payload.Hash }, bytes, now);
            _fixedGridStats.transfersStarted++;
        }
        void SendFixedGridTransfers()
        {
            if (!IsServer || Blocks?.FixedBlocks == null || _fixedGridClients.Count == 0) return;
            using var timing = BlockTransferSendCpu.Auto();
            double now = Time.unscaledTimeAsDouble, rate = BlockGameplaySettings.NetworkBytesPerSecond;
            if (_blockBudgetAt == 0) _blockSendBudget = rate * .1;
            else _blockSendBudget = Math.Min(rate * .25, _blockSendBudget + Math.Max(0, now - _blockBudgetAt) * rate);
            _blockBudgetAt = now;
            int idle = 0;
            // The configured byte rate and each peer's unreceived window bound this
            // loop. A fixed packets/render-frame cap loses bandwidth during hitches.
            while (idle < _fixedGridClients.Count)
            {
                int index = (_fixedGridPeerCursor & int.MaxValue) % _fixedGridClients.Count;
                _fixedGridPeerCursor = unchecked(_fixedGridPeerCursor + 1);
                ulong client = _fixedGridClients[index];
                if (!_fixedGridPeers.TryGetValue(client, out var peer) ||
                    !peer.Window.TrySelect(now, out var send, out int fragmentIndex, out bool repeat)) { idle++; continue; }
                int size = Math.Min(BlockFixedGridFragment.PayloadBytes, send.Bytes.Length - fragmentIndex * BlockFixedGridFragment.PayloadBytes);
                if (_blockSendBudget < size + 120) break;
                var fragment = send.Manifest; fragment.Index = fragmentIndex; fragment.Data = new byte[size];
                Buffer.BlockCopy(send.Bytes, fragmentIndex * BlockFixedGridFragment.PayloadBytes, fragment.Data, 0, size);
                try { FixedGridLifecycleClientRpc(fragment, Target(client)); }
                catch (Exception error)
                { _fixedGridStats.sendFailures++; Debug.LogWarning("[BLOCK] Fixed-grid send did not advance: " + error.Message); idle++; continue; }
                peer.Window.MarkQueued(send, fragmentIndex, repeat, now);
                _blockSendBudget -= size + 120; BlockBytesSent += size; _fixedGridStats.bytesSent += size;
                _fixedGridStats.fragmentsSent++; idle = 0;
            }
        }
        void RequestFixedGridSnapshot(uint rejectedTransfer)
        {
            if (IsServer || Blocks?.FixedBlocks == null) return;
            double now = Time.unscaledTimeAsDouble;
            if (_fixedGridRequestSent && now - _fixedGridLastRequest < .25) return;
            _fixedGridRequestSent = true; _fixedGridLastRequest = now; _fixedGridStats.requestsSent++;
            RequestFixedGridSnapshotRpc(rejectedTransfer);
        }
        void QueueFixedGridSnapshot(ulong client, uint rejectedTransfer)
        {
            if (!IsServer || Blocks?.FixedBlocks == null || client == NetworkManager.LocalClientId ||
                !NetworkManager.ConnectedClients.ContainsKey(client)) return;
            _fixedGridStats.requestsReceived++;
            if (!_fixedGridPeers.TryGetValue(client, out var peer)) _fixedGridPeers.Add(client, peer = new FixedGridPeer());
            double now = Time.unscaledTimeAsDouble;
            // Duplicate initial requests cannot throw away a valid full transfer.
            // A rejection for an obsolete stream cannot reset a newer recovery.
            if (rejectedTransfer == 0 ? peer.Window.ContainsFull : !peer.Window.Contains(rejectedTransfer)) return;
            if (peer.LastRequest > 0 && now - peer.LastRequest < .25) return;
            peer.LastRequest = now; peer.Ready = false; peer.Window.Clear();
            _fixedGridEncoded.Clear(); StartFixedGridTransfer(peer, true, now);
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestFixedGridSnapshotRpc(uint rejectedTransfer, RpcParams rpc = default)
            => QueueFixedGridSnapshot(rpc.Receive.SenderClientId, rejectedTransfer);
        [ClientRpc]
        void FixedGridLifecycleClientRpc(BlockFixedGridFragment fragment, ClientRpcParams targets = default)
        {
            if (IsServer || Blocks?.FixedBlocks == null || !Blocks.FixedGridMode) return;
            if (!fragment.Valid || fragment.Round != _paintRound)
            { _fixedGridStats.fragmentsRejected++; return; }
            if (_fixedGridCompletedTransfers.TryGetValue(fragment.Sequence, out var completed) && fragment.SameTransfer(completed))
            { _fixedGridStats.fragmentsDuplicate++; AckFixedGridSnapshot(completed); return; }
            if (fragment.Sequence <= _fixedGridAcceptedSequence || _fixedGridIncoming != null &&
                fragment.Sequence < _fixedGridIncoming.Manifest.Sequence)
            { _fixedGridStats.fragmentsRejected++; return; }
            if (_fixedGridIncoming == null || fragment.Sequence > _fixedGridIncoming.Manifest.Sequence)
            {
                if (_fixedGridIncoming != null && !fragment.Full)
                { RejectFixedGridIncoming(fragment.Transfer, "Ordered delta arrived before its preceding paired transfer completed."); return; }
                var world = Blocks.FixedBlocks;
                if (!fragment.Full && (!_fixedGridReady || fragment.Epoch != world.Epoch || fragment.BaseRevision != world.Revision ||
                    fragment.SurfaceBaseRevision != world.Surfaces.Revision))
                { RejectFixedGridIncoming(fragment.Transfer, "Delta baseline mismatch."); return; }
                if (_fixedGridIncoming != null) _fixedGridStats.assembliesAbandoned++;
                _fixedGridIncoming = new BlockFixedGridAssembly(fragment); _fixedGridLastUniqueProgress = Time.unscaledTimeAsDouble;
            }
            int before = _fixedGridIncoming.ReceivedCount, beforeThrough = _fixedGridIncoming.ReceivedThrough;
            if (!_fixedGridIncoming.Add(fragment))
            { _fixedGridStats.fragmentsRejected++; RejectFixedGridIncoming(fragment.Transfer, "Fragment metadata or duplicate bytes mismatch."); return; }
            if (_fixedGridIncoming.ReceivedCount > before)
            { _fixedGridStats.fragmentsUnique++; _fixedGridLastUniqueProgress = Time.unscaledTimeAsDouble; }
            else _fixedGridStats.fragmentsDuplicate++;
            if (!_fixedGridIncoming.Complete)
            {
                if (_fixedGridIncoming.ReceivedCount == before ||
                    _fixedGridIncoming.ReceivedThrough / BlockFixedGridSendWindow.ReceiptStride > beforeThrough / BlockFixedGridSendWindow.ReceiptStride)
                    AckFixedGridProgress(_fixedGridIncoming.Manifest, _fixedGridIncoming.ReceivedThrough);
                return;
            }
            var manifest = _fixedGridIncoming.Manifest;
            try
            {
                if (!_fixedGridIncoming.Verify()) throw new InvalidOperationException("Fixed-grid checksum mismatch.");
                ApplyFixedGridLifecycle(manifest, _fixedGridIncoming.Bytes);
                AckFixedGridSnapshot(manifest);
            }
            catch (Exception error)
            { _fixedGridStats.applyFailures++; RejectFixedGridIncoming(manifest.Transfer, error.Message); }
        }
        void ApplyFixedGridLifecycle(BlockFixedGridFragment manifest, byte[] bytes)
        {
            var world = Blocks?.FixedBlocks;
            if (world == null || manifest.Round != _paintRound || manifest.Sequence <= _fixedGridAcceptedSequence ||
                world.Round == manifest.Round && world.Epoch == manifest.Epoch &&
                (manifest.Revision < world.Revision || world.Frozen && !manifest.Frozen))
                throw new InvalidOperationException("Fixed-grid lifecycle is stale or belongs to another round.");
            var snapshot = BlockFixedGridCodec.Decode(bytes);
            if (!manifest.Matches(snapshot)) throw new InvalidOperationException("Fixed-grid manifest and payload differ.");
            if (!world.TryPrepareImport(snapshot, out var prepared, out string error)) throw new InvalidOperationException(error);
            // Both the fixed occupancy and surface ownership are prepared before either becomes visible.
            // CommitImport publishes the paired references and collider/query state before Committed.
            if (!world.CommitImport(prepared)) throw new InvalidOperationException("Fixed-grid prepared baseline changed before commit.");
            if (world.Epoch != manifest.Epoch || world.Round != manifest.Round || world.Revision != manifest.Revision ||
                world.Surfaces.Revision != manifest.SurfaceRevision || world.Frozen != manifest.Frozen)
                throw new InvalidOperationException("Fixed-grid prepared commit was not installed.");
            _fixedGridIncoming = null; _fixedGridAcceptedSequence = manifest.Sequence;
            _fixedGridCompletedTransfers.Add(manifest.Sequence, manifest); _fixedGridCompletedOrder.Enqueue(manifest.Sequence);
            while (_fixedGridCompletedOrder.Count > BlockFixedGridSendWindow.MaximumPendingTransfers)
                _fixedGridCompletedTransfers.Remove(_fixedGridCompletedOrder.Dequeue());
            _fixedGridAppliedTick = manifest.Tick; _fixedGridReady = true; _fixedGridStats.assembliesCompleted++;
            _fixedGridRequestSent = false;
            RefreshInitialBlockSync();
        }
        void RejectFixedGridIncoming(uint transfer, string reason)
        {
            if (_fixedGridIncoming != null) _fixedGridStats.assembliesAbandoned++;
            _fixedGridIncoming = null; _fixedGridReady = false; RefreshInitialBlockSync();
            Debug.LogWarning("[BLOCK] Fixed-grid snapshot resynchronizing: " + reason); RequestFixedGridSnapshot(transfer);
        }
        void AckFixedGridSnapshot(BlockFixedGridFragment manifest)
            => AckFixedGridSnapshotRpc(manifest.Epoch, manifest.Round, manifest.Transfer, manifest.Sequence,
                manifest.Revision, manifest.SurfaceRevision, manifest.Hash, manifest.Frozen);
        void AckFixedGridProgress(BlockFixedGridFragment manifest, int receivedThrough)
            => AckFixedGridProgressRpc(manifest.Epoch, manifest.Round, manifest.Transfer, manifest.Sequence,
                manifest.Revision, manifest.SurfaceRevision, manifest.Hash, manifest.Frozen, receivedThrough);
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void AckFixedGridProgressRpc(uint epoch, uint round, uint transfer, ulong sequence, uint revision,
            uint surfaceRevision, uint hash, bool frozen, int receivedThrough, RpcParams rpc = default)
        {
            bool applied = IsServer && Blocks?.FixedBlocks != null && epoch == Blocks.FixedBlocks.Epoch && round == _paintRound &&
                _fixedGridPeers.TryGetValue(rpc.Receive.SenderClientId, out var peer) &&
                peer.Window.AcknowledgeProgress(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen,
                    receivedThrough, Time.unscaledTimeAsDouble);
            if (applied) _fixedGridStats.progressAcknowledgementsApplied++;
            else _fixedGridStats.progressAcknowledgementsRejected++;
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void AckFixedGridSnapshotRpc(uint epoch, uint round, uint transfer, ulong sequence, uint revision,
            uint surfaceRevision, uint hash, bool frozen, RpcParams rpc = default)
        {
            if (ApplyFixedGridAcknowledgement(rpc.Receive.SenderClientId, epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen))
                _fixedGridStats.acknowledgementsApplied++;
            else _fixedGridStats.acknowledgementsRejected++;
        }
        bool ApplyFixedGridAcknowledgement(ulong client, uint epoch, uint round, uint transfer, ulong sequence,
            uint revision, uint surfaceRevision, uint hash, bool frozen)
        {
            if (!IsServer || Blocks?.FixedBlocks == null || round != _paintRound || epoch != Blocks.FixedBlocks.Epoch ||
                !_fixedGridPeers.TryGetValue(client, out var peer)) return false;
            if (!peer.Window.AcknowledgeCommit(epoch, round, transfer, sequence, revision, surfaceRevision, hash, frozen,
                    Time.unscaledTimeAsDouble, out var confirmed))
                return false;
            if (confirmed.Transfer != 0)
            {
                peer.Epoch = confirmed.Epoch; peer.Applied = confirmed.Revision; peer.SurfaceApplied = confirmed.SurfaceRevision;
                peer.Frozen = confirmed.Frozen; peer.Ready = true;
            }
            return true;
        }
    }
}
