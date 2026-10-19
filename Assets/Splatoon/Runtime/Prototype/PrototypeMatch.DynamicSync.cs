using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Profiling;
using UnityEngine;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        static readonly ProfilerMarker DynamicCaptureCpu = new("Splatoon.Blocks.DynamicCapture");
        static readonly ProfilerMarker DynamicLifecyclePrepareCpu = new("Splatoon.Blocks.DynamicLifecyclePrepare");
        static readonly ProfilerMarker DynamicMotionSelectCpu = new("Splatoon.Blocks.DynamicMotionSelect");

        sealed class DynamicSend
        {
            public BlockDynamicFragment Manifest;
            public byte[] Bytes;
            public HashSet<ulong> Ids;
            public uint SurfaceRevision;
            public int Cursor;
            public double LastProgress;
        }
        sealed class DynamicPeer
        {
            public bool Ready, Frozen;
            public uint Applied, SurfaceApplied;
            public double NextLifecycleAt;
            public double LastLifecycleAt, LastMotionAt, MaximumLifecycleGap, MaximumMotionGap;
            public readonly BlockDynamicSendScheduler.Peer Schedule = new();
            public readonly HashSet<ulong> Ids = new();
            public DynamicSend Send;
        }
        sealed class DynamicSendSource : BlockDynamicSendScheduler.ISource
        {
            readonly PrototypeMatch _owner;
            public DynamicSendSource(PrototypeMatch owner) { _owner = owner; }
            public BlockDynamicSendScheduler.Peer GetPeer(int index)
                => _owner._dynamicPeers.TryGetValue(_owner._dynamicClients[index], out var peer) ? peer.Schedule : null;
            public int TrySend(int index, BlockDynamicSendScheduler.Kind kind, BlockDynamicSendScheduler.Window window)
                => _owner.TrySendDynamicPacket(_owner._dynamicClients[index], kind, window);
        }
        [Serializable] public struct DynamicSyncStatistics
        {
            public long serviceCalls, visits, lifecyclePackets, motionPackets, chargedBytes, motionScans;
            public long stopIdle, stopPackets, stopTime, stopVisits, stopCredit, stopScans;
            public long transfersStarted, fragmentsQueued, sendFailures, observerFailures;
            public long fragmentsUnique, fragmentsDuplicate, fragmentsRejected, assembliesStarted, assembliesCompleted, applyFailures;
            public long requestsSent, requestsReceived, requestsIgnored, assembliesAbandoned, sendTimeouts, acksMatched, acksRejected;
            public long motionApplied, motionRejected;
            public int peers, incomingReceived, incomingCount, lastVisits, lastPackets, lastScans, lastStop;
            public uint round, worldRevision, requiredRevision, incomingTransfer, incomingRevision, surfaceRevision;
            public double maximumServiceMs, lastServiceMs, roomCredit, dynamicCredit, incomingUniqueProgressAge;
        }
        [Serializable] public struct DynamicPeerStatistics
        {
            public ulong peer;
            public bool ready, frozen, full;
            public uint round, appliedRevision, targetRevision, currentRevision, transfer, captureTick;
            public uint appliedSurfaceRevision, targetSurfaceRevision, currentSurfaceRevision;
            public ulong sequence;
            public int knownIds, queuedFragments, fragmentCount;
            public long revisionGap;
            public double lastSubmitAge, lifecycleSubmitAge, motionSubmitAge, maximumLifecycleGap, maximumMotionGap;
        }
        DynamicSyncStatistics _dynamicStats;
        readonly BlockDynamicSendScheduler _dynamicScheduler = new();
        DynamicSendSource _dynamicSendSource;
        readonly List<BlockDynamicState> _dynamicMotionScratch = new(BlockDynamicCodec.MotionBatchSize);
        readonly Dictionary<ulong, DynamicPeer> _dynamicPeers = new();
        readonly List<ulong> _dynamicClients = new(), _dynamicDeparted = new();
        readonly List<BlockDynamicState> _dynamicCapture = new(), _dynamicCreated = new(), _dynamicMerged = new();
        readonly List<ulong> _dynamicRemoved = new();
        readonly Dictionary<ulong, BlockDynamicState> _dynamicMerge = new();
        readonly HashSet<uint> _dynamicMotionRevisions = new();
        readonly HashSet<ulong> _dynamicMotionIds = new();
        readonly List<BlockDynamicState> _dynamicReceiveMotion = new(BlockDynamicCodec.MotionBatchSize);
        BlockDynamicAssembly _dynamicIncoming;
        bool _blockTerrainReady, _dynamicReady;
        uint _dynamicTransferId, _dynamicCompletedId, _dynamicCompletedSurfaceRevision;
        uint _dynamicCapturedTick, _dynamicAppliedTick;
        ulong _dynamicSequence, _dynamicCaptureSerial;
        ulong _dynamicMotionEpochSequence;
        double _dynamicLastRequest, _dynamicLastProgress, _dynamicCapturedAt = -1;
        double _dynamicLastUniqueProgress, _dynamicLastFailureWarning = -1;
        double _dynamicSendCredit, _dynamicCreditAt;
        public bool DynamicBlockSyncReady => Blocks?.FixedGridMode == true ? FixedGridBlockSyncReady : IsServer || _dynamicReady;
        // Zero means unknown. Live PhysX poses and sparse lifecycle/motion merges do not
        // form a snapshot of State.Tick; only a future per-body physics clock can prove that.
        public uint DynamicBlockCaptureTick => IsServer ? 0 : _dynamicAppliedTick;
        public long DynamicBlockBytesSent { get; private set; }
        public event Action<ulong, BlockDynamicMotion, bool> DynamicMotionObserved;

        public DynamicSyncStatistics InspectDynamicSyncStatistics()
        {
            var value = _dynamicStats;
            value.peers = _dynamicPeers.Count; value.round = Blocks?.Dynamic?.Round ?? 0;
            value.worldRevision = Blocks?.Dynamic?.LifecycleRevision ?? 0;
            value.requiredRevision = State.Value.DynamicBlockRevision;
            value.surfaceRevision = Blocks?.SurfacePaints?.Revision ?? 0;
            value.roomCredit = _blockSendBudget; value.dynamicCredit = _dynamicSendCredit;
            if (_dynamicIncoming != null)
            {
                value.incomingTransfer = _dynamicIncoming.Manifest.Transfer; value.incomingRevision = _dynamicIncoming.Manifest.Revision;
                value.incomingReceived = _dynamicIncoming.ReceivedCount; value.incomingCount = _dynamicIncoming.Manifest.Count;
                value.incomingUniqueProgressAge = Math.Max(0, Time.unscaledTimeAsDouble - _dynamicLastUniqueProgress);
            }
            return value;
        }
        // Allocates only when explicitly inspected, never for each packet.
        public DynamicPeerStatistics[] InspectDynamicSyncPeers()
        {
            var result = new DynamicPeerStatistics[_dynamicPeers.Count]; int index = 0;
            foreach (var pair in _dynamicPeers)
            {
                var peer = pair.Value; var send = peer.Send; var m = send?.Manifest ?? default;
                result[index++] = new DynamicPeerStatistics { peer = pair.Key, ready = peer.Ready, frozen = peer.Frozen,
                    round = Blocks?.Dynamic?.Round ?? 0, appliedRevision = peer.Applied, targetRevision = m.Revision,
                    transfer = m.Transfer, captureTick = m.Tick, sequence = m.Sequence, full = m.Full,
                    currentRevision = Blocks?.Dynamic?.LifecycleRevision ?? 0,
                    appliedSurfaceRevision = peer.SurfaceApplied, targetSurfaceRevision = send?.SurfaceRevision ?? 0,
                    currentSurfaceRevision = Blocks?.SurfacePaints?.Revision ?? 0,
                    revisionGap = (long)(Blocks?.Dynamic?.LifecycleRevision ?? 0) - peer.Applied,
                    knownIds = peer.Ids.Count, queuedFragments = send?.Cursor ?? 0, fragmentCount = send == null ? 0 : m.Count,
                    lastSubmitAge = send == null ? 0 : Math.Max(0, Time.unscaledTimeAsDouble - send.LastProgress),
                    lifecycleSubmitAge = peer.LastLifecycleAt > 0 ? Math.Max(0, Time.unscaledTimeAsDouble - peer.LastLifecycleAt) : -1,
                    motionSubmitAge = peer.LastMotionAt > 0 ? Math.Max(0, Time.unscaledTimeAsDouble - peer.LastMotionAt) : -1,
                    maximumLifecycleGap = peer.MaximumLifecycleGap, maximumMotionGap = peer.MaximumMotionGap };
            }
            return result;
        }

        void SetTerrainSyncReady(bool ready)
        { _blockTerrainReady = ready; RefreshInitialBlockSync(); }
        void RefreshInitialBlockSync()
        { InitialSyncComplete = IsServer || (Blocks?.FixedGridMode == true ? _fixedGridReady : _blockTerrainReady && _dynamicReady); }
        void ResetDynamicSync()
        {
            _dynamicPeers.Clear(); _dynamicClients.Clear(); _dynamicDeparted.Clear();
            _dynamicCapture.Clear(); _dynamicCreated.Clear(); _dynamicRemoved.Clear(); _dynamicMerged.Clear(); _dynamicMerge.Clear();
            _dynamicIncoming = null; _dynamicCompletedId = 0; _dynamicScheduler.Reset(); _dynamicStats = default;
            _dynamicCompletedSurfaceRevision = 0;
            _dynamicLastRequest = _dynamicLastProgress = 0; _dynamicCapturedAt = -1;
            _dynamicCapturedTick = _dynamicAppliedTick = 0;
            _dynamicMotionEpochSequence = 0; _dynamicMotionRevisions.Clear();
            _dynamicMotionIds.Clear(); _dynamicReceiveMotion.Clear();
            _dynamicSendCredit = _dynamicCreditAt = 0;
            _dynamicLastUniqueProgress = 0; _dynamicMotionScratch.Clear();
            _dynamicReady = _blockTerrainReady = IsServer; RefreshInitialBlockSync();
            // Keep transfer IDs and pose sequences monotonic across round resets.
        }
        void CaptureDynamicStates(double now)
        {
            using var marker = DynamicCaptureCpu.Auto();
            _dynamicCapture.Clear(); Blocks.Dynamic.CaptureStates(_dynamicCapture);
            // NGO's State.Tick is not the clock of the native PhysX solve. Do not attach
            // a fabricated common server tick to these poses or to a later sparse merge.
            _dynamicCapturedTick = 0;
            _dynamicCaptureSerial++; if (_dynamicCaptureSerial == 0) _dynamicCaptureSerial++;
            _dynamicCapturedAt = now;
        }
        void UpdateDynamicSync()
        {
            if (Blocks?.FixedGridMode == true) return;
            if (Blocks?.Dynamic == null) return;
            double now = Time.unscaledTimeAsDouble;
            if (!IsServer)
            {
                if ((!_dynamicReady && _dynamicIncoming == null || _dynamicIncoming != null && now - _dynamicLastProgress > 3)
                    && now - _dynamicLastRequest > 1)
                {
                    uint abandonedTransfer = _dynamicIncoming?.Manifest.Transfer ?? 0;
                    if (_dynamicIncoming != null) _dynamicStats.assembliesAbandoned++;
                    _dynamicIncoming = null; _dynamicLastRequest = now; RequestDynamicSnapshot(abandonedTransfer);
                }
                return;
            }
            _dynamicClients.Clear();
            bool captured = false;
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client == NetworkManager.LocalClientId) continue;
                _dynamicClients.Add(client);
                if (!_dynamicPeers.TryGetValue(client, out var peer)) _dynamicPeers.Add(client, peer = new DynamicPeer());
                if (peer.Send != null && now - peer.Send.LastProgress > Math.Max(15, GameplayConfig.Global.ConnectionTimeout * 2))
                { peer.Send = null; peer.Ready = false; _dynamicStats.sendTimeouts++; }
                if (peer.Send != null || peer.Ready && peer.Applied == Blocks.Dynamic.LifecycleRevision &&
                    peer.SurfaceApplied == (Blocks.SurfacePaints?.Revision ?? 0) && peer.Frozen == Blocks.Dynamic.Frozen) continue;
                if (peer.Ready && !Blocks.Dynamic.Frozen && now < peer.NextLifecycleAt) continue;
                if (!captured) { CaptureDynamicStates(now); captured = true; }
                StartDynamicTransfer(peer, now);
            }
            _dynamicDeparted.Clear();
            foreach (ulong id in _dynamicPeers.Keys) if (!NetworkManager.ConnectedClients.ContainsKey(id)) _dynamicDeparted.Add(id);
            foreach (ulong id in _dynamicDeparted) _dynamicPeers.Remove(id);
            if (!captured && _dynamicClients.Count > 0 && !Blocks.Dynamic.Frozen && now - _dynamicCapturedAt >= .1)
                CaptureDynamicStates(now);
        }
        void StartDynamicTransfer(DynamicPeer peer, double now)
        {
            using var marker = DynamicLifecyclePrepareCpu.Auto();
            peer.NextLifecycleAt = now + .1;
            bool full = !peer.Ready || Blocks.Dynamic.Frozen;
            var ids = new HashSet<ulong>(); _dynamicCreated.Clear(); _dynamicRemoved.Clear();
            foreach (var state in _dynamicCapture)
            { ids.Add(state.Id); if (full || !peer.Ids.Contains(state.Id)) _dynamicCreated.Add(state); }
            if (!full) foreach (ulong id in peer.Ids) if (!ids.Contains(id)) _dynamicRemoved.Add(id);
            byte[] bytes = BlockDynamicCodec.Encode(Blocks.Dynamic.Round, _dynamicCreated, _dynamicRemoved);
            uint surfaceRevision = Blocks.SurfacePaints?.Revision ?? 0;
            if (Blocks.SurfacePaints != null)
            {
                byte[] surfaceBytes = full ? Blocks.SurfacePaints.ExportFull() : Blocks.SurfacePaints.ExportDelta(peer.SurfaceApplied);
                bytes = BlockSurfaceDynamicEnvelope.Encode(bytes, surfaceBytes, surfaceRevision, full);
            }
            uint transfer = ++_dynamicTransferId; if (transfer == 0) transfer = ++_dynamicTransferId;
            peer.Send = new DynamicSend { Bytes = bytes, Ids = ids, LastProgress = now, SurfaceRevision = surfaceRevision,
                Manifest = new BlockDynamicFragment { Round = Blocks.Dynamic.Round, Transfer = transfer,
                    BaseRevision = full ? 0 : peer.Applied, Revision = Blocks.Dynamic.LifecycleRevision,
                    Tick = _dynamicCapturedTick, Sequence = ++_dynamicSequence, Full = full, Frozen = Blocks.Dynamic.Frozen,
                    Length = bytes.Length, Hash = BlockTransferFragment.Checksum(bytes) } };
            _dynamicStats.transfersStarted++;
        }
        // Uses the shared whole-room terrain budget; lifecycle is independent of mesh publication.
        void SendDynamicTransfers(double rate, bool terrainPending)
        {
            if (Blocks?.FixedGridMode == true) return;
            if (!IsServer || Blocks?.Dynamic == null || _dynamicClients.Count == 0) return;
            double now = Time.unscaledTimeAsDouble, share = terrainPending ? rate * .5 : rate;
            _dynamicSendCredit = _dynamicCreditAt == 0 ? share * .1
                : Math.Min(share * .25, _dynamicSendCredit + Math.Max(0, now - _dynamicCreditAt) * share);
            _dynamicCreditAt = now;
            _dynamicSendSource ??= new DynamicSendSource(this);
            var result = _dynamicScheduler.Run(_dynamicClients.Count, _dynamicSendSource, ref _blockSendBudget, ref _dynamicSendCredit);
            _dynamicStats.serviceCalls++; _dynamicStats.visits += result.Visits;
            _dynamicStats.lifecyclePackets += result.LifecyclePackets; _dynamicStats.motionPackets += result.MotionPackets;
            _dynamicStats.chargedBytes += result.ChargedBytes; _dynamicStats.motionScans += result.Scans;
            _dynamicStats.lastVisits = result.Visits; _dynamicStats.lastPackets = result.Packets;
            _dynamicStats.lastScans = result.Scans; _dynamicStats.lastStop = (int)result.Reason;
            _dynamicStats.lastServiceMs = result.ElapsedMilliseconds;
            _dynamicStats.maximumServiceMs = Math.Max(_dynamicStats.maximumServiceMs, result.ElapsedMilliseconds);
            switch (result.Reason)
            {
                case BlockDynamicSendScheduler.Stop.Idle: _dynamicStats.stopIdle++; break;
                case BlockDynamicSendScheduler.Stop.Packets: _dynamicStats.stopPackets++; break;
                case BlockDynamicSendScheduler.Stop.Time: _dynamicStats.stopTime++; break;
                case BlockDynamicSendScheduler.Stop.Visits: _dynamicStats.stopVisits++; break;
                case BlockDynamicSendScheduler.Stop.Credit: _dynamicStats.stopCredit++; break;
                case BlockDynamicSendScheduler.Stop.Scans: _dynamicStats.stopScans++; break;
            }
        }
        int TrySendDynamicPacket(ulong id, BlockDynamicSendScheduler.Kind kind, BlockDynamicSendScheduler.Window window)
        {
            if (!_dynamicPeers.TryGetValue(id, out var peer) || window.Expired) return 0;
            if (kind == BlockDynamicSendScheduler.Kind.Lifecycle)
            {
                var send = peer.Send;
                if (send == null || send.Cursor >= send.Manifest.Count) return 0;
                int size = Math.Min(BlockDynamicFragment.PayloadBytes, send.Bytes.Length - send.Cursor * BlockDynamicFragment.PayloadBytes);
                if (window.Allowance < size + 100) return 0;
                var fragment = send.Manifest; fragment.Index = send.Cursor; fragment.Data = new byte[size];
                Buffer.BlockCopy(send.Bytes, fragment.Index * BlockDynamicFragment.PayloadBytes, fragment.Data, 0, size);
                if (window.Expired) return 0;
                try { DynamicLifecycleClientRpc(fragment, Target(id)); }
                catch (Exception error) { DynamicSendFailed(error); return 0; }
                send.Cursor++; send.LastProgress = Time.unscaledTimeAsDouble; _dynamicStats.fragmentsQueued++;
                if (peer.LastLifecycleAt > 0) peer.MaximumLifecycleGap = Math.Max(peer.MaximumLifecycleGap, send.LastProgress - peer.LastLifecycleAt);
                peer.LastLifecycleAt = send.LastProgress;
                DynamicBlockBytesSent += size; BlockBytesSent += size;
                return size + 100;
            }
            if (!TryPrepareDynamicMotion(peer, window, out var motion, out var selection)) return 0;
            if (window.Expired) return 0;
            try { DynamicMotionClientRpc(motion, Target(id)); }
            catch (Exception error) { DynamicSendFailed(error); return 0; }
            peer.Schedule.Motion.Commit(selection, true);
            double now = Time.unscaledTimeAsDouble;
            if (peer.LastMotionAt > 0) peer.MaximumMotionGap = Math.Max(peer.MaximumMotionGap, now - peer.LastMotionAt);
            peer.LastMotionAt = now;
            ObserveDynamicMotion(id, motion, true);
            int cost = 100 + motion.States.Length * BlockDynamicCodec.StateBytes;
            DynamicBlockBytesSent += cost - 100; BlockBytesSent += cost - 100;
            return cost;
        }
        bool TryPrepareDynamicMotion(DynamicPeer peer, BlockDynamicSendScheduler.Window window,
            out BlockDynamicMotion motion, out BlockDynamicSendScheduler.MotionCursor.Selection selection)
        {
            motion = default; selection = default;
            // A reliable transfer can run for several RTTs while paint continues changing.
            // Already ACKed, still-live IDs keep moving on their approved baseline; births
            // remain excluded until their lifecycle is ACKed. Deletion never sends a pose.
            if (!peer.Ready || peer.Frozen || Blocks?.Dynamic == null || Blocks.Dynamic.Frozen ||
                peer.Applied > Blocks.Dynamic.LifecycleRevision || _dynamicCapture.Count == 0 ||
                window.Allowance < 100 + BlockDynamicCodec.StateBytes) return false;
            int maximum = Math.Min(BlockDynamicCodec.MotionBatchSize, (int)((window.Allowance - 100) / BlockDynamicCodec.StateBytes));
            using (DynamicMotionSelectCpu.Auto())
                selection = peer.Schedule.Motion.Select(_dynamicCaptureSerial, _dynamicCapture, peer.Ids, maximum, window, _dynamicMotionScratch);
            peer.Schedule.Motion.Commit(selection, false);
            for (int i = _dynamicMotionScratch.Count - 1; i >= 0; i--)
                if (!Blocks.Dynamic.TryGet(_dynamicMotionScratch[i].Id, out var body) || body.Owner != _dynamicMotionScratch[i].Owner)
                    _dynamicMotionScratch.RemoveAt(i);
            if (_dynamicMotionScratch.Count == 0)
            {
                // Selected IDs that were deleted after capture are consumed without an RPC.
                peer.Schedule.Motion.Commit(selection, true); return false;
            }
            if (window.Expired) return false;
            motion = new BlockDynamicMotion { Round = Blocks.Dynamic.Round, Revision = peer.Applied,
                Tick = 0, Sequence = ++_dynamicSequence, States = _dynamicMotionScratch.ToArray() };
            return true;
        }
        void DynamicSendFailed(Exception error)
        {
            _dynamicStats.sendFailures++;
            if (Time.unscaledTimeAsDouble - _dynamicLastFailureWarning < 1) return;
            _dynamicLastFailureWarning = Time.unscaledTimeAsDouble;
            Debug.LogWarning("[BLOCK] Dynamic RPC submit failed without advancing its cursor: " + error.Message);
        }
        void ObserveDynamicMotion(ulong peer, BlockDynamicMotion motion, bool sending)
        {
            try { DynamicMotionObserved?.Invoke(peer, motion, sending); }
            catch (Exception error)
            { _dynamicStats.observerFailures++; Debug.LogWarning("[BLOCK] Dynamic motion observer failed: " + error.Message); }
        }
        void RequestDynamicSnapshot(uint rejectedTransfer)
        {
            if (Blocks?.FixedGridMode == true) { RequestFixedGridSnapshot(rejectedTransfer); return; }
            _dynamicStats.requestsSent++; RequestDynamicSnapshotRpc(rejectedTransfer);
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestDynamicSnapshotRpc(uint rejectedTransfer = 0, RpcParams rpc = default)
        {
            if (!IsServer || Blocks?.Dynamic == null) return;
            ulong client = rpc.Receive.SenderClientId;
            if (client == NetworkManager.LocalClientId || !NetworkManager.ConnectedClients.ContainsKey(client)) return;
            _dynamicStats.requestsReceived++;
            if (!_dynamicPeers.TryGetValue(client, out var peer)) _dynamicPeers.Add(client, peer = new DynamicPeer());
            if (peer.Send != null && peer.Send.Manifest.Transfer != rejectedTransfer
                && (peer.Send.Cursor < peer.Send.Manifest.Count || Time.unscaledTimeAsDouble - peer.Send.LastProgress < 1))
            { _dynamicStats.requestsIgnored++; return; }
            peer.Send = null;
            peer.Ready = false; CaptureDynamicStates(Time.unscaledTimeAsDouble); StartDynamicTransfer(peer, Time.unscaledTimeAsDouble);
        }
        [ClientRpc]
        void DynamicLifecycleClientRpc(BlockDynamicFragment fragment, ClientRpcParams targets = default)
        {
            if (Blocks?.FixedGridMode == true) return;
            if (IsServer || Blocks?.Dynamic == null) return;
            if (fragment.Round != _paintRound || !fragment.Valid) { _dynamicStats.fragmentsRejected++; return; }
            if (fragment.Transfer == _dynamicCompletedId)
            { _dynamicStats.fragmentsDuplicate++; AckDynamicSnapshotRpc(fragment.Round, fragment.Transfer, fragment.Revision, _dynamicCompletedSurfaceRevision); return; }
            if (_dynamicCompletedId != 0 && unchecked((int)(fragment.Transfer - _dynamicCompletedId)) <= 0)
            { _dynamicStats.fragmentsRejected++; return; }
            if (_dynamicIncoming == null || unchecked((int)(fragment.Transfer - _dynamicIncoming.Manifest.Transfer)) > 0)
            {
                if (!fragment.Full && (!_dynamicReady || fragment.BaseRevision != Blocks.Dynamic.LifecycleRevision))
                { _dynamicReady = false; RefreshInitialBlockSync(); RequestDynamicSnapshot(fragment.Transfer); return; }
                if (_dynamicIncoming != null) _dynamicStats.assembliesAbandoned++;
                _dynamicIncoming = new BlockDynamicAssembly(fragment);
                _dynamicStats.assembliesStarted++; _dynamicLastUniqueProgress = Time.unscaledTimeAsDouble;
            }
            int before = _dynamicIncoming.ReceivedCount;
            if (!_dynamicIncoming.Add(fragment)) { _dynamicStats.fragmentsRejected++; return; }
            if (_dynamicIncoming.ReceivedCount > before)
            { _dynamicStats.fragmentsUnique++; _dynamicLastUniqueProgress = Time.unscaledTimeAsDouble; }
            else _dynamicStats.fragmentsDuplicate++;
            _dynamicLastProgress = Time.unscaledTimeAsDouble;
            if (!_dynamicIncoming.Complete) return;
            var manifest = _dynamicIncoming.Manifest;
            try
            {
                if (!_dynamicIncoming.Verify()) throw new InvalidOperationException("Dynamic lifecycle checksum mismatch.");
                uint surfaceRevision = ApplyDynamicLifecycle(manifest, _dynamicIncoming.Bytes);
                AckDynamicSnapshotRpc(manifest.Round, manifest.Transfer, manifest.Revision, surfaceRevision);
            }
            catch (Exception exception)
            {
                _dynamicStats.applyFailures++; _dynamicStats.assembliesAbandoned++;
                Debug.LogWarning("[BLOCK] Dynamic snapshot resynchronizing: " + exception.Message);
                _dynamicIncoming = null; _dynamicReady = false; RefreshInitialBlockSync();
                _dynamicLastRequest = Time.unscaledTimeAsDouble; RequestDynamicSnapshot(manifest.Transfer);
            }
        }
        uint ApplyDynamicLifecycle(BlockDynamicFragment manifest, byte[] bytes)
        {
            if (Blocks?.FixedGridMode == true) throw new InvalidOperationException("Legacy dynamic lifecycle is disabled in fixed-grid mode.");
            if (Blocks?.Dynamic == null || manifest.Round != _paintRound || manifest.Round != Blocks.Dynamic.Round ||
                manifest.Sequence == 0 || manifest.Sequence < _dynamicMotionEpochSequence ||
                manifest.Revision < Blocks.Dynamic.LifecycleRevision || Blocks.Dynamic.Frozen && !manifest.Frozen ||
                manifest.Frozen && !manifest.Full ||
                !manifest.Full && (!_dynamicReady || manifest.BaseRevision != Blocks.Dynamic.LifecycleRevision))
                throw new InvalidOperationException("Dynamic lifecycle round, epoch or baseline was rejected.");
            byte[] dynamicBytes = bytes;
            BlockSurfaceWorld nextSurface = null; uint surfaceRevision = 0;
            if (Blocks.SurfacePaints != null)
            {
                BlockSurfaceDynamicEnvelope.Decode(bytes, out dynamicBytes, out var surfaceBytes, out surfaceRevision, out bool surfaceFull);
                if (surfaceFull != manifest.Full || !Blocks.PrepareSurfacePaintImport(surfaceBytes, surfaceFull, out nextSurface) ||
                    nextSurface == null || nextSurface.Revision != surfaceRevision || nextSurface.Round != manifest.Round)
                    throw new InvalidOperationException("Surface lifecycle baseline or identity was rejected.");
            }
            BlockDynamicCodec.Decode(dynamicBytes, manifest.Round, _dynamicCreated, _dynamicRemoved);
            if (manifest.Full && _dynamicRemoved.Count > 0) throw new InvalidOperationException("A full dynamic baseline contains removals.");
            _dynamicMerged.Clear();
            if (manifest.Full) _dynamicMerged.AddRange(_dynamicCreated);
            else
            {
                Blocks.Dynamic.CaptureStates(_dynamicMerged); _dynamicMerge.Clear();
                foreach (var state in _dynamicMerged) _dynamicMerge.Add(state.Id, state);
                foreach (ulong id in _dynamicRemoved) _dynamicMerge.Remove(id);
                foreach (var state in _dynamicCreated)
                {
                    if (_dynamicMerge.ContainsKey(state.Id)) throw new InvalidOperationException("Dynamic creation reuses an existing body ID.");
                    _dynamicMerge.Add(state.Id, state);
                }
                _dynamicMerged.Clear(); _dynamicMerged.AddRange(_dynamicMerge.Values);
            }
            // A delta carries no pose for retained IDs. Sequence zero preserves each live
            // body's own ordering instead of relabelling an old local pose with this transfer's
            // newer sequence. New bodies receive the actual lifecycle sequence before Changed.
            Action beforeNotify = () =>
            {
                if (!manifest.Full)
                    foreach (var state in _dynamicCreated)
                        if (Blocks.Dynamic.TryGet(state.Id, out var body)) body.MotionSequence = manifest.Sequence;
                if (nextSurface != null) Blocks.InstallSurfacePaints(nextSurface);
            };
            if (!Blocks.Dynamic.ApplySnapshot(manifest.Round, manifest.Revision, manifest.Full ? manifest.Sequence : 0,
                _dynamicMerged, manifest.Frozen, beforeNotify))
                throw new InvalidOperationException("Dynamic baseline was rejected.");
            if (manifest.Full)
            {
                _dynamicMotionRevisions.Clear();
                _dynamicMotionEpochSequence = manifest.Sequence;
            }
            _dynamicMotionRevisions.Add(manifest.Revision);
            _dynamicIncoming = null; _dynamicCompletedId = manifest.Transfer; _dynamicReady = true;
            _dynamicCompletedSurfaceRevision = surfaceRevision; _dynamicStats.assembliesCompleted++;
            _dynamicAppliedTick = 0;
            if (nextSurface != null) Blocks.NotifySurfacePaintChanged();
            RefreshInitialBlockSync(); return surfaceRevision;
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void AckDynamicSnapshotRpc(uint round, uint transfer, uint revision, uint surfaceRevision, RpcParams rpc = default)
        {
            if (!IsServer) return;
            if (ApplyDynamicAcknowledgement(rpc.Receive.SenderClientId, round, transfer, revision, surfaceRevision)) _dynamicStats.acksMatched++;
            else _dynamicStats.acksRejected++;
        }
        bool ApplyDynamicAcknowledgement(ulong client, uint round, uint transfer, uint revision, uint surfaceRevision)
        {
            if (round != _paintRound || !_dynamicPeers.TryGetValue(client, out var peer)) return false;
            var send = peer.Send;
            if (send == null || send.Manifest.Transfer != transfer || send.Manifest.Revision != revision || send.SurfaceRevision != surfaceRevision)
                return false;
            peer.Ids.Clear(); peer.Ids.UnionWith(send.Ids); peer.Applied = revision; peer.SurfaceApplied = surfaceRevision;
            peer.Frozen = send.Manifest.Frozen; peer.Ready = true; peer.Send = null;
            return true;
        }
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        void DynamicMotionClientRpc(BlockDynamicMotion motion, ClientRpcParams targets = default)
        {
            if (IsServer || Blocks?.Dynamic == null) return;
            if (ApplyDynamicMotion(motion))
            { _dynamicStats.motionApplied++; ObserveDynamicMotion(NetworkManager.LocalClientId, motion, false); }
            else _dynamicStats.motionRejected++;
        }
        bool ApplyDynamicMotion(BlockDynamicMotion motion)
        {
            if (Blocks?.FixedGridMode == true) return false;
            if (!_dynamicReady || Blocks?.Dynamic == null || motion.Round != _paintRound || motion.Round != Blocks.Dynamic.Round ||
                motion.Sequence <= _dynamicMotionEpochSequence || !_dynamicMotionRevisions.Contains(motion.Revision) ||
                motion.Revision > Blocks.Dynamic.LifecycleRevision || motion.States == null ||
                motion.States.Length == 0 || motion.States.Length > BlockDynamicCodec.MotionBatchSize) return false;
            _dynamicReceiveMotion.Clear(); _dynamicMotionIds.Clear();
            foreach (var state in motion.States)
            {
                if (!BlockDynamicCodec.Valid(state, motion.Round) || !_dynamicMotionIds.Add(state.Id)) return false;
                if (!Blocks.Dynamic.TryGet(state.Id, out var body)) continue;
                if (body.Owner != state.Owner) return false;
                _dynamicReceiveMotion.Add(state);
            }
            // The wire revision must be a baseline this epoch really committed. The current
            // world revision then checks the native apply, which only updates surviving IDs.
            // This permits packets sent before an ACK arrives without resurrecting deletions.
            if (_dynamicReceiveMotion.Count == 0 || !Blocks.Dynamic.ApplyMotion(motion.Round,
                Blocks.Dynamic.LifecycleRevision, motion.Sequence, _dynamicReceiveMotion)) return false;
            _dynamicAppliedTick = 0; return true;
        }
    }
}
