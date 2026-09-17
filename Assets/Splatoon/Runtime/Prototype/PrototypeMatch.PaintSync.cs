using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Networking;
using Unity.Collections;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        private sealed class Transfer
        {
            public SnapshotTransferLayout Layout;
            public SnapshotSendWindow Window;
            public byte[] Bytes;
            public PaintStamp[] Journal;
            public ClientRpcParams Target;
            public bool EndSent;
        }
        private sealed class Incoming
        {
            public SnapshotTransferLayout Layout;
            public byte[] Bytes;
            public bool[] Received;
            public int Count;
            public double LastProgress;
        }
        private readonly Dictionary<ulong, Transfer> _transfers = new();
        private readonly HashSet<ulong> _waiting = new();
        private PaintCheckpoint _checkpoint;
        private byte[] _checkpointBytes;
        private uint _checkpointHash;
        private Incoming _incoming;
        private uint _transferId;
        private int _captureGeneration;
        private bool _capturing;
        private float _lastGapRequest;
        private uint _latestTransferId;
        private bool _syncRetryPending;
        private readonly SnapshotRateBudget _syncBudget = new();
        private readonly List<ulong> _syncClients = new(8), _syncWaiting = new(8);
        private readonly List<uint> _obsoletePaint = new(1024);
        private int _syncCursor;
        private FastBufferWriter _syncScratch;
        public long SnapshotBytesSent { get; private set; }
        public long SnapshotInFlightBytes { get { long bytes = 0; foreach (var transfer in _transfers.Values) bytes += transfer.Window.InFlightBytes; return bytes; } }
        public double LastSnapshotSeconds { get; private set; }
        private double _snapshotStarted;
        private FastBufferWriter SyncScratch
        { get { if (!_syncScratch.IsInitialized) _syncScratch = new FastBufferWriter(8192, Allocator.Persistent); return _syncScratch; } }
        private void Update()
        {
            if (!IsSpawned) return;
            if (!IsServer)
            {
                bool gap = _buffered.Count > 0 && InitialSyncComplete && !_buffered.ContainsKey(_appliedSequence + 1);
                bool stalled = _incoming != null && Time.unscaledTimeAsDouble - _incoming.LastProgress > GameplayConfig.Global.ConnectionTimeout * 2;
                if ((gap || stalled || _syncRetryPending) && Time.unscaledTime - _lastGapRequest > 2)
                { _lastGapRequest = Time.unscaledTime; _incoming = null; _syncRetryPending = false; InitialSyncComplete = false; RequestSnapshotRpc(); }
                return;
            }
            if (!_capturing && ((_checkpoint == null && _waiting.Count > 0) || PaintSequence - (_checkpoint?.Sequence ?? 0) >= GameplayConfig.Global.CheckpointStamps)) CaptureCheckpoint().Forget();
            if (_checkpoint != null && _checkpointBytes != null && _waiting.Count > 0)
            {
                _syncWaiting.Clear(); foreach (var client in _waiting) _syncWaiting.Add(client);
                foreach (var client in _syncWaiting) { if (NetworkManager.ConnectedClients.ContainsKey(client)) StartTransfer(client); _waiting.Remove(client); }
            }
        }
        private void LateUpdate()
        {
            // NGO's network tick has already queued this frame's live state, shots, hits and paint.
            if (!IsSpawned || !IsServer || _transfers.Count == 0) return;
            double now = Time.unscaledTimeAsDouble;
            _syncBudget.Refill(now, GameplayConfig.Global.SnapshotBytesPerSecond);
            _syncClients.Clear(); foreach (var client in _transfers.Keys) _syncClients.Add(client);
            int budget = GameplayConfig.Global.ChunksPerFrame, idle = 0;
            while (budget > 0 && idle < _syncClients.Count)
            {
                _syncCursor %= _syncClients.Count;
                ulong client = _syncClients[_syncCursor++];
                if (!_transfers.TryGetValue(client, out var t)) { idle++; continue; }
                if (t.Window.TimedOut(now, GameplayConfig.Global.ConnectionTimeout * 2))
                {
                    SnapshotAbortClientRpc(t.Layout.Manifest.Round, t.Layout.Manifest.Id, t.Target);
                    _transfers.Remove(client); idle++; continue;
                }
                if (t.Window.Complete)
                {
                    if (!t.EndSent) { t.EndSent = true; SnapshotEndClientRpc(t.Layout.Manifest.Round, t.Layout.Manifest.Id, t.Target); }
                    idle++; continue;
                }
                if (!t.Window.TryPeek(out int index) || !_syncBudget.TrySpend(t.Layout.SizeAt(index))) { idle++; continue; }
                var record = t.Layout.Record(index, t.Bytes, t.Journal, SyncScratch);
                SnapshotRecordClientRpc(record, t.Target);
                t.Window.Sent(index, t.Layout.SizeAt(index)); SnapshotBytesSent += t.Layout.SizeAt(index);
                budget--; idle = 0;
            }
        }
        private static ClientRpcParams Target(ulong client) => new() { Send = new ClientRpcSendParams { TargetClientIds = new[] { client } } };
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestSnapshotRpc(RpcParams rpc = default)
        {
            ulong client = rpc.Receive.SenderClientId;
            if (client != NetworkManager.LocalClientId && NetworkManager.ConnectedClients.ContainsKey(client))
            { _transfers.Remove(client); _waiting.Add(client); }
        }
        private async UniTaskVoid CaptureCheckpoint()
        {
            _capturing = true; int generation = _captureGeneration;
            var checkpoint = new PaintCheckpoint { Round = State.Value.Round, Sequence = PaintSequence, Topology = Arena.BakedTopology, Ownership = Arena.CaptureOwnership() };
            int pending = 0; bool failed = false;
            try
            {
                // Copy all painted surfaces at one sequence boundary. Subsequent paint commands
                // write different targets, so async readback cannot include later stamps.
                using (FramePerformance.Checkpoint.Auto())
                {
                foreach (var surface in PrototypeArena.Current.Surfaces.Values)
                {
                    if (!surface.HasPaint || surface.Mask == null) continue;
                    int id = surface.SurfaceId;
                    var copy = RenderTexture.GetTemporary(surface.Mask.descriptor);
                    Graphics.CopyTexture(surface.Mask, copy); pending++;
                    _ = AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32, request =>
                    {
                        try
                        {
                            if (request.hasError) failed = true;
                            else checkpoint.Surfaces[id] = request.GetData<byte>().ToArray();
                        }
                        finally { RenderTexture.ReleaseTemporary(copy); pending--; }
                    });
                }
                }
                while (pending > 0) await UniTask.Yield();
                if (generation != _captureGeneration || !IsSpawned) return;
                if (failed) throw new InvalidOperationException("涂色检查点 GPU 读回失败");
                var encoded = await Task.Run(() => { var data = PaintSnapshotCodec.Encode(checkpoint); return (bytes: data, hash: PaintSnapshotCodec.Hash(data)); });
                await UniTask.SwitchToMainThread();
                if (generation != _captureGeneration || !IsSpawned) return;
                _checkpoint = checkpoint; _checkpointBytes = encoded.bytes; _checkpointHash = encoded.hash; _journal.RemoveAll(s => s.Sequence <= checkpoint.Sequence);
                Debug.Log($"[INK] Checkpoint round={checkpoint.Round} seq={checkpoint.Sequence} surfaces={checkpoint.Surfaces.Count} bytes={encoded.bytes.Length} rtMiB={PaintSurface.AllocatedBytes / 1048576f:F1}");
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { if (generation == _captureGeneration) _capturing = false; }
        }
        private void StartTransfer(ulong client)
        {
            // Pin the historical boundary. Later checkpoints may prune the shared journal.
            var journal = _journal.ToArray();
            var manifest = new SnapshotManifest { Id = ++_transferId, Round = _checkpoint.Round,
                Sequence = _checkpoint.Sequence, EndSequence = PaintSequence, JournalCount = journal.Length,
                Length = _checkpointBytes.Length, Hash = _checkpointHash, RecordBytes = GameplayConfig.Global.SnapshotChunkBytes };
            var layout = new SnapshotTransferLayout(manifest);
            var t = new Transfer { Layout = layout, Bytes = _checkpointBytes, Journal = journal, Target = Target(client),
                Window = new SnapshotSendWindow(layout.TotalRecords, GameplayConfig.Global.SnapshotMaxInFlightRecords, Time.unscaledTimeAsDouble) };
            _transfers[client] = t;
            SnapshotBeginClientRpc(manifest, t.Target);
        }
        [ClientRpc]
        private void SnapshotBeginClientRpc(SnapshotManifest manifest, ClientRpcParams targets = default)
        {
            if (IsServer || manifest.Round != _paintRound || (_latestTransferId != 0 && unchecked((int)(manifest.Id - _latestTransferId)) <= 0)) return;
            if (manifest.Length <= 0 || manifest.Length > GameplayConfig.Global.MaxPaintMemoryMiB * 1048576 ||
                manifest.JournalCount < 0 || manifest.JournalCount > GameplayConfig.Global.MaxPaintMemoryMiB * 1048576 / 53 ||
                manifest.RecordBytes != GameplayConfig.Global.SnapshotChunkBytes)
            { Debug.LogError("涂色快照大小无效"); _syncRetryPending = true; return; }
            var layout = new SnapshotTransferLayout(manifest);
            _latestTransferId = manifest.Id; _syncRetryPending = false;
            InitialSyncComplete = false;
            _snapshotStarted = Time.unscaledTimeAsDouble;
            _incoming = new Incoming { Layout = layout, Bytes = new byte[manifest.Length], Received = new bool[layout.TotalRecords], LastProgress = _snapshotStarted };
        }
        [ClientRpc]
        private void SnapshotRecordClientRpc(SnapshotRecord record, ClientRpcParams targets = default)
        {
            try
            {
                var t = _incoming;
                if (IsServer || t == null || record.Round != _paintRound || t.Layout.Manifest.Id != record.TransferId ||
                    record.Index < 0 || record.Index >= t.Received.Length) return;
                var layout = t.Layout; int index = record.Index;
                if (record.IsJournal != (index < layout.JournalRecords) ||
                    (record.IsJournal ? record.Stamps.Count : record.Bytes.Count) != layout.CountAt(index) ||
                    SnapshotTransferLayout.Hash(record, SyncScratch) != record.Hash)
                { RequestRecordRpc(record.Round, record.TransferId, index); return; }
                if (!t.Received[index])
                {
                    if (record.IsJournal)
                    {
                        uint first = layout.Manifest.Sequence + (uint)(index * layout.StampsPerRecord) + 1;
                        for (int i = 0; i < record.Stamps.Count; i++)
                            if (record.Stamps[i].Round != _paintRound || record.Stamps[i].Sequence != first + i)
                            { RequestRecordRpc(record.Round, record.TransferId, index); return; }
                        for (int i = 0; i < record.Stamps.Count; i++) { var stamp = record.Stamps[i]; _buffered[stamp.Sequence] = stamp; }
                    }
                    else Buffer.BlockCopy(record.Bytes.Buffer, record.Bytes.Offset, t.Bytes, (index - layout.JournalRecords) * layout.DataBytes, record.Bytes.Count);
                    t.Received[index] = true; t.Count++; t.LastProgress = Time.unscaledTimeAsDouble;
                }
                AckRecordRpc(record.Round, record.TransferId, index);
            }
            finally { record.Dispose(); }
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestRecordRpc(uint round, uint id, int index, RpcParams rpc = default)
        {
            if (_transfers.TryGetValue(rpc.Receive.SenderClientId, out var t) && t.Layout.Manifest.Round == round && t.Layout.Manifest.Id == id)
                t.Window.Retry(index);
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void AckRecordRpc(uint round, uint id, int index, RpcParams rpc = default)
        {
            if (_transfers.TryGetValue(rpc.Receive.SenderClientId, out var t) && t.Layout.Manifest.Round == round && t.Layout.Manifest.Id == id)
                t.Window.Acknowledge(index, Time.unscaledTimeAsDouble);
        }
        [ClientRpc]
        private void SnapshotAbortClientRpc(uint round, uint id, ClientRpcParams targets = default)
        {
            if (IsServer || round != _paintRound || _incoming == null || _incoming.Layout.Manifest.Id != id) return;
            _incoming = null; InitialSyncComplete = false; _syncRetryPending = true;
        }
        [ClientRpc]
        private void SnapshotEndClientRpc(uint round, uint id, ClientRpcParams targets = default)
        {
            var t = _incoming; if (IsServer || round != _paintRound || t == null || t.Layout.Manifest.Id != id) return;
            if (t.Count != t.Received.Length)
            { _syncRetryPending = true; return; }
            try
            {
                var manifest = t.Layout.Manifest;
                if (PaintSnapshotCodec.Hash(t.Bytes) != manifest.Hash) throw new InvalidOperationException("快照完整性校验失败");
                var sizes = PrototypeArena.Current.Surfaces.ToDictionary(p => p.Key, p => p.Value.TextureBytes);
                var checkpoint = PaintSnapshotCodec.Decode(t.Bytes, Arena.BakedTopology, Arena.OwnershipSizes(), sizes);
                if (checkpoint.Round != _paintRound || checkpoint.Sequence != manifest.Sequence) throw new InvalidOperationException("快照边界不一致");
                for (uint sequence = manifest.Sequence; sequence < manifest.EndSequence;)
                    if (!_buffered.ContainsKey(++sequence)) throw new InvalidOperationException("补同步历史墨迹缺失");
                PrototypeArena.Current.ClearPaint();
                Arena.RestoreOwnership(checkpoint.Ownership);
                foreach (var pair in checkpoint.Surfaces) PrototypeArena.Current.Surfaces[pair.Key].Restore(pair.Value);
                _appliedSequence = checkpoint.Sequence;
                _obsoletePaint.Clear(); foreach (var sequence in _buffered.Keys) { if (sequence > _appliedSequence) break; _obsoletePaint.Add(sequence); }
                foreach (var sequence in _obsoletePaint) _buffered.Remove(sequence);
                _incoming = null; InitialSyncComplete = true; DrainPaint(); AckSnapshotRpc(round, id);
                LastSnapshotSeconds = Time.unscaledTimeAsDouble - _snapshotStarted;
                Debug.Log($"[INK] Snapshot applied round={_paintRound} seq={_appliedSequence} surfaces={checkpoint.Surfaces.Count} hash={Arena.OwnershipHash()}");
            }
            catch (Exception e) { Debug.LogException(e); _incoming = null; InitialSyncComplete = false; _syncRetryPending = true; }
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void AckSnapshotRpc(uint round, uint id, RpcParams rpc = default)
        { if (_transfers.TryGetValue(rpc.Receive.SenderClientId, out var t) && t.Layout.Manifest.Round == round && t.Layout.Manifest.Id == id && t.Window.Complete && t.EndSent) _transfers.Remove(rpc.Receive.SenderClientId); }

        private void ResetSnapshotTransfer(bool dispose)
        {
            _syncBudget.Reset(); _syncClients.Clear(); _syncWaiting.Clear(); _obsoletePaint.Clear(); _syncCursor = 0;
            _syncRetryPending = false; _latestTransferId = 0; _checkpointHash = 0;
            if (dispose && _syncScratch.IsInitialized) { _syncScratch.Dispose(); _syncScratch = default; }
        }
    }
}
