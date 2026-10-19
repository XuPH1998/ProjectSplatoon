using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Unity.Profiling;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        static readonly ProfilerMarker BlockTransferEncodeCpu = new("Splatoon.Blocks.TransferEncode");
        static readonly ProfilerMarker BlockTransferSendCpu = new("Splatoon.Blocks.TransferSend");
        sealed class BlockSend
        {
            public uint Id, Round, Base, Revision, Hash;
            public byte[] Bytes;
            public bool Full;
            public bool[] Ack;
            public double[] Sent;
            public double Started;
            public int Cursor;
            public int Count => Ack.Length;
        }
        sealed class BlockPeer
        {
            public uint Applied;
            public bool Ready;
            public double LastRequest;
            public BlockSend Send;
        }
        public BlockTerrain Blocks { get; private set; }
        readonly Dictionary<ulong, BlockPeer> _blockPeers = new();
        readonly List<ulong> _blockClients = new(8);
        readonly List<ulong> _blockDeparted = new(8);
        readonly List<BlockPeer> _blockTransferStarts = new(8);
        readonly List<BlockWorld.EncodingRequest> _blockTransferRequests = new(8);
        BlockTransferAssembly _blockIncoming;
        bool _blockInstalling, _blockInstallPublished;
        BlockTerrain _blockObservedTerrain;
        BlockFixedGridWorld _blockObservedFixedWorld;
        int _blockInstallFailures;
        double _blockInstallStarted;
        uint _blockTransferId, _blockCompletedId;
        double _blockLastProgress, _blockLastRequest, _blockBudgetAt;
        double _blockSendBudget;
        int _blockPeerCursor;
        public long BlockBytesSent { get; private set; }
        public uint AppliedBlockRevision => Blocks?.FixedGridMode == true ? Blocks.FixedBlocks.Revision : Blocks != null ? Blocks.Revision : 0;
        public bool BlockSyncReady => InitialSyncComplete && Blocks != null;

        void EnsureBlocks()
        {
            if (Blocks == null)
            {
                Blocks = Arena.GetComponent<BlockTerrain>();
                if (Blocks == null) Blocks = Arena.gameObject.AddComponent<BlockTerrain>();
                Blocks.Initialize(Arena);
            }
            Blocks.EnableFixedGridMode();
            if (_blockObservedTerrain == Blocks && _blockObservedFixedWorld == Blocks.FixedBlocks) return;
            DetachBlockPublications(); _blockObservedTerrain = Blocks;
            _blockObservedTerrain.PublishedCommit += OnBlockPublished;
            _blockObservedTerrain.SurfacePaintChanged += OnSurfacePaintChanged;
            _blockObservedFixedWorld = Blocks.FixedBlocks;
            _blockObservedFixedWorld.Committed += OnFixedGridCommitted;
        }
        void DetachBlockPublications()
        {
            if (_blockObservedTerrain != null)
            {
                _blockObservedTerrain.PublishedCommit -= OnBlockPublished;
                _blockObservedTerrain.SurfacePaintChanged -= OnSurfacePaintChanged;
            }
            if (_blockObservedFixedWorld != null) _blockObservedFixedWorld.Committed -= OnFixedGridCommitted;
            _blockObservedFixedWorld = null;
            _blockObservedTerrain = null;
        }
        void OnFixedGridCommitted(BlockFixedGridCommit commit)
        {
            if (!IsServer || !IsSpawned || State.Value.Round != commit.Round) return;
            var state = State.Value; ReadBlockScore(ref state); State.Value = state;
        }
        void OnBlockPublished(BlockTerrainCommit commit)
        {
            if (IsServer && IsSpawned && State.Value.Round == commit.Round)
            {
                var state = State.Value;
                ReadBlockScore(ref state);
                State.Value = state;
            }
            if (!_blockInstalling || _blockIncoming == null) return;
            var manifest = _blockIncoming.Manifest;
            if (commit.Round == manifest.Round && commit.Revision == manifest.Revision)
            { _blockInstallPublished = true; ObserveTerrainPublishedCallback(manifest); }
        }
        void OnSurfacePaintChanged()
        {
            if (!IsServer || !IsSpawned || Blocks == null || State.Value.Round != Blocks.Round) return;
            var state = State.Value; ReadBlockScore(ref state); State.Value = state;
        }
        void ReadBlockScore(ref MatchStateSnapshot s)
        {
            if (Blocks == null) return;
            s.PinkBlockCount = Blocks.PinkCount; s.BlueBlockCount = Blocks.BlueCount;
            s.TerrainRevision = Blocks.FixedGridMode ? Blocks.FixedBlocks.Revision : Blocks.Revision;
            s.FixedBlockRevision = Blocks.FixedGridMode ? Blocks.FixedBlocks.Revision : 0;
            s.DynamicBlockRevision = Blocks.FixedGridMode ? 0 : Blocks.Dynamic?.LifecycleRevision ?? 0;
            // Old area fields remain diagnostic compatibility fields. Product UI
            // and the winner use explicit block counts.
            s.PinkArea = s.PinkBlockCount; s.BlueArea = s.BlueBlockCount; s.TotalArea = s.PinkBlockCount + s.BlueBlockCount;
        }
        void ResetBlockSync()
        {
            ObserveCurrentTerrainTransfer("Abandoned", "ResetBlockSync");
            ResetTerrainTransferObservation();
            _blockPeers.Clear(); _blockClients.Clear(); _blockTransferStarts.Clear(); _blockTransferRequests.Clear();
            _blockIncoming = null; _blockInstalling = _blockInstallPublished = false;
            _blockInstallStarted = 0; _blockInstallFailures = 0;
            _blockCompletedId = 0; _blockLastProgress = _blockLastRequest = _blockBudgetAt = 0; _blockSendBudget = 0;
            ResetFixedGridSync(); ResetDynamicSync();
        }
        void UpdateBlockSync()
        {
            if (Blocks?.FixedGridMode == true) { UpdateFixedGridSync(); return; }
            UpdateDynamicSync();
            if (!IsServer)
            {
                double now = Time.unscaledTimeAsDouble;
                if (_blockInstalling && (Blocks.FailedTransactions != _blockInstallFailures ||
                    now - _blockInstallStarted > Math.Max(15, GameplayConfig.Global.ConnectionTimeout * 2)))
                {
                    ObserveCurrentTerrainTransfer("Abandoned", "InstallFailureOrTimeout");
                    _blockInstalling = _blockInstallPublished = false; _blockIncoming = null; SetTerrainSyncReady(false);
                    _blockLastRequest = now; RequestBlockSnapshotRpc();
                }
                if (InitialSyncComplete && State.Value.Phase == MatchPhase.Finished && Blocks.Round == State.Value.Round &&
                    Blocks.Revision >= State.Value.TerrainRevision && !_blockInstalling && Blocks.PendingTransactions == 0 &&
                    Blocks.Dynamic.Frozen && Blocks.Dynamic.LifecycleRevision >= State.Value.DynamicBlockRevision)
                    Blocks.FinishRound(State.Value.TerrainFrozenAt);
                if (_blockInstalling && _blockInstallPublished && _blockIncoming != null && Blocks.PendingTransactions == 0 &&
                    Blocks.Round == _blockIncoming.Manifest.Round && Blocks.Revision == _blockIncoming.Manifest.Revision)
                {
                    var manifest = _blockIncoming.Manifest;
                    RememberTerrainAppliedObservation();
                    _blockCompletedId = manifest.Id; _blockIncoming = null; _blockInstalling = _blockInstallPublished = false;
                    SetTerrainSyncReady(true);
                    AckBlockAppliedRpc(manifest.Round, manifest.Id, manifest.Revision);
                    ObserveTerrainAppliedAck(manifest, false);
                }
                if (!_blockInstalling && ((!_blockTerrainReady && _blockIncoming == null) ||
                    (_blockIncoming != null && now - _blockLastProgress > 3)) && now - _blockLastRequest > 1)
                {
                    ObserveCurrentTerrainTransfer("Abandoned", "FragmentProgressTimeout");
                    _blockLastRequest = now; _blockIncoming = null; RequestBlockSnapshotRpc();
                }
                return;
            }
            _blockClients.Clear(); _blockTransferStarts.Clear(); _blockTransferRequests.Clear();
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client == NetworkManager.LocalClientId) continue;
                _blockClients.Add(client);
                if (!_blockPeers.TryGetValue(client, out var peer)) _blockPeers.Add(client, peer = new BlockPeer());
                if (peer.Send == null && (!peer.Ready || peer.Applied < Blocks.Revision))
                { _blockTransferStarts.Add(peer); _blockTransferRequests.Add(new BlockWorld.EncodingRequest(!peer.Ready, peer.Applied)); }
            }
            StartPendingBlockTransfers();
            _blockDeparted.Clear();
            foreach (ulong client in _blockPeers.Keys) if (!NetworkManager.ConnectedClients.ContainsKey(client)) _blockDeparted.Add(client);
            foreach (ulong client in _blockDeparted) _blockPeers.Remove(client);
        }
        void StartPendingBlockTransfers()
        {
            try
            {
                if (_blockTransferStarts.Count == 0) return;
                if (_blockTransferStarts.Count == 1)
                { StartBlockTransfer(_blockTransferStarts[0], _blockTransferRequests[0].Full); return; }
                using var marker = BlockTransferEncodeCpu.Auto();
                var world = Blocks.World; var bytes = world.EncodeBatch(_blockTransferRequests);
                var hashes = new Dictionary<byte[], uint>();
                for (int i = 0; i < _blockTransferStarts.Count; i++)
                {
                    var request = _blockTransferRequests[i];
                    StartEncodedBlockTransfer(_blockTransferStarts[i], request.Full, request.Since, world.Revision, bytes[i], hashes);
                }
            }
            finally { _blockTransferStarts.Clear(); _blockTransferRequests.Clear(); }
        }
        void StartBlockTransfer(BlockPeer peer, bool full)
        {
            using var marker = BlockTransferEncodeCpu.Auto();
            byte[] bytes = full ? Blocks.ExportSnapshot() : Blocks.ExportDelta(peer.Applied);
            StartEncodedBlockTransfer(peer, full, peer.Applied, Blocks.Revision, bytes, null);
        }
        void StartEncodedBlockTransfer(BlockPeer peer, bool full, uint baseline, uint revision, byte[] bytes, Dictionary<byte[], uint> hashes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > BlockTransferFragment.MaxTransferBytes)
            { Debug.LogError("[BLOCK] 地形传输大小无效"); return; }
            uint hash;
            if (hashes == null || !hashes.TryGetValue(bytes, out hash))
            { hash = BlockTransferFragment.Checksum(bytes); hashes?.Add(bytes, hash); }
            int count = (bytes.Length + BlockTransferFragment.PayloadBytes - 1) / BlockTransferFragment.PayloadBytes;
            peer.Send = new BlockSend { Id = ++_blockTransferId, Round = State.Value.Round, Base = full ? 0 : baseline,
                Revision = revision, Hash = hash, Bytes = bytes, Full = full,
                Ack = new bool[count], Sent = new double[count], Started = Time.unscaledTimeAsDouble };
        }
        void SendBlockTransfers()
        {
            if (Blocks?.FixedGridMode == true) { SendFixedGridTransfers(); return; }
            if (!IsServer) return;
            using var marker = BlockTransferSendCpu.Auto();
            double now = Time.unscaledTimeAsDouble;
            double rate = BlockGameplaySettings.NetworkBytesPerSecond;
            if (_blockBudgetAt == 0) _blockSendBudget = rate * .1;
            else _blockSendBudget = Math.Min(rate * .25, _blockSendBudget + Math.Max(0, now - _blockBudgetAt) * rate);
            _blockBudgetAt = now;
            bool terrainPending = false;
            foreach (var peer in _blockPeers.Values) if (peer.Send != null) { terrainPending = true; break; }
            SendDynamicTransfers(rate, terrainPending);
            if (_blockClients.Count == 0) return;
            int budget = 16, idle = 0;
            while (budget-- > 0 && idle < _blockClients.Count)
            {
                ulong client = _blockClients[_blockPeerCursor++ % _blockClients.Count];
                if (!_blockPeers.TryGetValue(client, out var peer) || peer.Send == null) { idle++; continue; }
                var t = peer.Send;
                if (now - t.Started > Math.Max(15, GameplayConfig.Global.ConnectionTimeout * 2))
                { peer.Send = null; peer.Ready = false; idle++; continue; }
                int selected = -1;
                for (int scanned = 0; scanned < t.Count; scanned++)
                {
                    int index = t.Cursor++ % t.Count;
                    if (!t.Ack[index] && (t.Sent[index] == 0 || now - t.Sent[index] >= .20)) { selected = index; break; }
                }
                // If the apply acknowledgement was lost, repeat the final fragment;
                // the client remembers the completed transfer and acknowledges it again.
                if (selected < 0 && now - t.Sent[t.Count - 1] >= .35) selected = t.Count - 1;
                if (selected < 0) { idle++; continue; }
                int size = Math.Min(BlockTransferFragment.PayloadBytes, t.Bytes.Length - selected * BlockTransferFragment.PayloadBytes);
                if (_blockSendBudget < size + 100) break;
                var payload = new byte[size]; Buffer.BlockCopy(t.Bytes, selected * BlockTransferFragment.PayloadBytes, payload, 0, size);
                var fragment = new BlockTransferFragment { Round = t.Round, Id = t.Id, BaseRevision = t.Base, Revision = t.Revision,
                    Hash = t.Hash, Length = t.Bytes.Length, Index = selected, Full = t.Full, Data = payload };
                BlockFragmentClientRpc(fragment, Target(client));
                t.Sent[selected] = now; _blockSendBudget -= size + 100; BlockBytesSent += size; idle = 0;
            }
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestBlockSnapshotRpc(RpcParams rpc = default)
        {
            if (!IsServer || Blocks == null) return;
            ulong client = rpc.Receive.SenderClientId;
            if (client == NetworkManager.LocalClientId || !NetworkManager.ConnectedClients.ContainsKey(client)) return;
            if (Blocks.FixedGridMode) { QueueFixedGridSnapshot(client, 0); return; }
            if (!_blockPeers.TryGetValue(client, out var peer)) _blockPeers.Add(client, peer = new BlockPeer());
            double now = Time.unscaledTimeAsDouble;
            if (peer.Send != null && now - peer.LastRequest < .5) return;
            peer.LastRequest = now; peer.Ready = false; peer.Applied = 0; StartBlockTransfer(peer, true);
        }
        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        void BlockFragmentClientRpc(BlockTransferFragment fragment, ClientRpcParams targets = default)
        {
            if (Blocks?.FixedGridMode == true) return;
            ObserveTerrainTransfer(fragment, "Received");
            if (IsServer || Blocks == null || fragment.Round != _paintRound || !fragment.Valid)
            { ObserveTerrainTransfer(fragment, "Ignored", "ServerOrTerrainOrRoundOrInvalid"); return; }
            if (fragment.Id == _blockCompletedId)
            { AckBlockAppliedRpc(fragment.Round, fragment.Id, fragment.Revision); ObserveTerrainAppliedAck(fragment, true); return; }
            if (_blockCompletedId != 0 && unchecked((int)(fragment.Id - _blockCompletedId)) <= 0)
            { ObserveTerrainTransfer(fragment, "Ignored", "OldCompletedTransfer"); return; }
            if (_blockIncoming == null || unchecked((int)(fragment.Id - _blockIncoming.Manifest.Id)) > 0)
            {
                if (_blockInstalling) { ObserveTerrainTransfer(fragment, "Ignored", "InstallingOtherTransfer"); return; }
                if (!fragment.Full && (!_blockTerrainReady || fragment.BaseRevision != Blocks.Revision))
                { ObserveTerrainTransfer(fragment, "Ignored", "DeltaBaseOrSyncMismatch"); SetTerrainSyncReady(false); RequestBlockSnapshotRpc(); return; }
                ObserveCurrentTerrainTransfer("Abandoned", "SupersededAssembly");
                _blockIncoming = new BlockTransferAssembly(fragment); _blockLastProgress = Time.unscaledTimeAsDouble;
                ResetTerrainIncomingObservation();
                ObserveTerrainTransfer(fragment, "AssemblyCreated");
            }
            int traceCountBefore = _blockIncoming.ReceivedCount;
            bool traceAdded = _blockIncoming.Add(fragment);
            ObserveTerrainTransfer(fragment, "AddReturned", "", true, traceAdded, traceCountBefore, _blockIncoming.ReceivedCount);
            if (!traceAdded) return;
            AckBlockFragmentRpc(fragment.Round, fragment.Id, fragment.Index);
            ObserveTerrainTransfer(fragment, "FragmentAckCallReturned");
            _blockLastProgress = Time.unscaledTimeAsDouble;
            if (_blockInstalling || !_blockIncoming.Complete) return;
            ObserveTerrainTransfer(fragment, "AssemblyComplete");
            ObserveTerrainTransfer(fragment, "VerifyEntered");
            bool traceVerified = _blockIncoming.Verify();
            ObserveTerrainTransfer(fragment, "VerifyReturned", "", true, traceVerified);
            if (!traceVerified)
            { _blockIncoming = null; SetTerrainSyncReady(false); RequestBlockSnapshotRpc(); return; }
            _blockInstallPublished = false; _blockInstallFailures = Blocks.FailedTransactions; _blockInstallStarted = Time.unscaledTimeAsDouble;
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var terrainTraceScope = Blocks.ObserveRemoteTransferScope(fragment, GetInstanceID());
#endif
            ObserveTerrainTransfer(fragment, "ApplyEntered");
            bool accepted = fragment.Full ? Blocks.ApplyRemoteSnapshot(_blockIncoming.Bytes) : Blocks.ApplyRemoteDelta(_blockIncoming.Bytes);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RecordTerrainAcceptedObservation(accepted ? terrainTraceScope.Accepted : default);
#endif
            ObserveTerrainTransfer(fragment, "ApplyReturned", "", true, accepted);
            if (accepted) _blockInstalling = true;
            else { _blockIncoming = null; SetTerrainSyncReady(false); RequestBlockSnapshotRpc(); }
        }
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
        void AckBlockFragmentRpc(uint round, uint id, int index, RpcParams rpc = default)
        {
            if (Blocks?.FixedGridMode == true) return;
            if (round != _paintRound || !_blockPeers.TryGetValue(rpc.Receive.SenderClientId, out var peer)) return;
            var t = peer.Send;
            if (t != null && t.Id == id && index >= 0 && index < t.Count) t.Ack[index] = true;
        }
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
        void AckBlockAppliedRpc(uint round, uint id, uint revision, RpcParams rpc = default)
        {
            if (Blocks?.FixedGridMode == true) return;
            if (round != _paintRound || !_blockPeers.TryGetValue(rpc.Receive.SenderClientId, out var peer)) return;
            var t = peer.Send;
            if (t == null || t.Id != id || t.Revision != revision) return;
            peer.Applied = revision; peer.Ready = true; peer.Send = null;
        }
    }
}
