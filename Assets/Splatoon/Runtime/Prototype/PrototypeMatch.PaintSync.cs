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

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        private sealed class Transfer { public uint Id; public byte[] Bytes; public int Next; public double Started; public readonly Queue<int> Retry = new(); }
        private sealed class Incoming { public uint Id, Hash; public byte[] Bytes; public bool[] Received; public int Count; }
        private readonly Dictionary<ulong, Transfer> _transfers = new();
        private readonly HashSet<ulong> _waiting = new();
        private PaintCheckpoint _checkpoint;
        private byte[] _checkpointBytes;
        private Incoming _incoming;
        private uint _transferId;
        private int _captureGeneration;
        private bool _capturing;
        private float _lastGapRequest;
        private void Update()
        {
            if (!IsSpawned) return;
            if (!IsServer)
            {
                if (_buffered.Count > 0 && InitialSyncComplete && !_buffered.ContainsKey(_appliedSequence + 1) && Time.unscaledTime - _lastGapRequest > 2)
                { _lastGapRequest = Time.unscaledTime; InitialSyncComplete = false; RequestSnapshotRpc(); }
                return;
            }
            if (!_capturing && ((_checkpoint == null && _waiting.Count > 0) || PaintSequence - (_checkpoint?.Sequence ?? 0) >= GameplayConfig.Global.CheckpointStamps)) CaptureCheckpoint().Forget();
            if (_checkpoint != null && _checkpointBytes != null)
                foreach (var client in _waiting.ToArray()) { if (NetworkManager.ConnectedClients.ContainsKey(client)) StartTransfer(client); _waiting.Remove(client); }
            int budget = GameplayConfig.Global.ChunksPerFrame;
            foreach (var pair in _transfers.ToArray())
            {
                var t = pair.Value;
                if (NetworkManager.ServerTime.Time - t.Started > GameplayConfig.Global.ConnectionTimeout * 2) { _transfers.Remove(pair.Key); continue; }
                int count = (t.Bytes.Length + GameplayConfig.Global.SnapshotChunkBytes - 1) / GameplayConfig.Global.SnapshotChunkBytes;
                while (budget > 0 && (t.Retry.Count > 0 || t.Next < count))
                {
                    int index = t.Retry.Count > 0 ? t.Retry.Dequeue() : t.Next++;
                    int offset = index * GameplayConfig.Global.SnapshotChunkBytes;
                    var bytes = new byte[Math.Min(GameplayConfig.Global.SnapshotChunkBytes, t.Bytes.Length - offset)];
                    Buffer.BlockCopy(t.Bytes, offset, bytes, 0, bytes.Length);
                    SnapshotChunkClientRpc(t.Id, index, bytes, PaintSnapshotCodec.Hash(bytes), Target(pair.Key)); budget--;
                    if (t.Next == count && t.Retry.Count == 0) SnapshotEndClientRpc(t.Id, Target(pair.Key));
                }
                if (budget <= 0) break;
            }
        }
        private static ClientRpcParams Target(ulong client) => new() { Send = new ClientRpcSendParams { TargetClientIds = new[] { client } } };
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestSnapshotRpc(RpcParams rpc = default)
        {
            ulong client = rpc.Receive.SenderClientId;
            if (client != NetworkManager.LocalClientId && NetworkManager.ConnectedClients.ContainsKey(client)) _waiting.Add(client);
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
                while (pending > 0) await UniTask.Yield();
                if (generation != _captureGeneration || !IsSpawned) return;
                if (failed) throw new InvalidOperationException("涂色检查点 GPU 读回失败");
                var bytes = await Task.Run(() => PaintSnapshotCodec.Encode(checkpoint));
                await UniTask.SwitchToMainThread();
                if (generation != _captureGeneration || !IsSpawned) return;
                _checkpoint = checkpoint; _checkpointBytes = bytes; _journal.RemoveAll(s => s.Sequence <= checkpoint.Sequence);
                Debug.Log($"[INK] Checkpoint round={checkpoint.Round} seq={checkpoint.Sequence} surfaces={checkpoint.Surfaces.Count} bytes={bytes.Length} rtMiB={PaintSurface.AllocatedBytes / 1048576f:F1}");
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { if (generation == _captureGeneration) _capturing = false; }
        }
        private void StartTransfer(ulong client)
        {
            var t = new Transfer { Id = ++_transferId, Bytes = _checkpointBytes, Started = NetworkManager.ServerTime.Time };
            _transfers[client] = t;
            SnapshotBeginClientRpc(_checkpoint.Round, t.Id, t.Bytes.Length, PaintSnapshotCodec.Hash(t.Bytes), Target(client));
            for (int offset = 0; offset < _journal.Count; offset += 64)
                PaintClientRpc(_journal.GetRange(offset, Math.Min(64, _journal.Count - offset)).ToArray(), Target(client));
        }
        [ClientRpc]
        private void SnapshotBeginClientRpc(uint round, uint id, int length, uint hash, ClientRpcParams targets = default)
        {
            if (IsServer || round != _paintRound) return;
            if (length <= 0 || length > GameplayConfig.Global.MaxPaintMemoryMiB * 1048576) { Debug.LogError("涂色快照大小无效"); return; }
            InitialSyncComplete = false;
            _incoming = new Incoming { Id = id, Hash = hash, Bytes = new byte[length], Received = new bool[(length + GameplayConfig.Global.SnapshotChunkBytes - 1) / GameplayConfig.Global.SnapshotChunkBytes] };
        }
        [ClientRpc]
        private void SnapshotChunkClientRpc(uint id, int index, byte[] bytes, uint hash, ClientRpcParams targets = default)
        {
            var t = _incoming;
            if (IsServer || t == null || t.Id != id || index < 0 || index >= t.Received.Length) return;
            int offset = index * GameplayConfig.Global.SnapshotChunkBytes;
            if (bytes.Length != Math.Min(GameplayConfig.Global.SnapshotChunkBytes, t.Bytes.Length - offset) || PaintSnapshotCodec.Hash(bytes) != hash)
            { RequestChunkRpc(id, index); return; }
            if (t.Received[index]) return;
            Buffer.BlockCopy(bytes, 0, t.Bytes, offset, bytes.Length); t.Received[index] = true; t.Count++;
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestChunkRpc(uint id, int index, RpcParams rpc = default)
        {
            if (_transfers.TryGetValue(rpc.Receive.SenderClientId, out var t) && t.Id == id && index >= 0 && index * (long)GameplayConfig.Global.SnapshotChunkBytes < t.Bytes.Length && t.Retry.Count < 64)
                t.Retry.Enqueue(index);
        }
        [ClientRpc]
        private void SnapshotEndClientRpc(uint id, ClientRpcParams targets = default)
        {
            var t = _incoming; if (IsServer || t == null || t.Id != id) return;
            if (t.Count != t.Received.Length)
            { for (int i = 0; i < t.Received.Length; i++) if (!t.Received[i]) RequestChunkRpc(id, i); return; }
            try
            {
                if (PaintSnapshotCodec.Hash(t.Bytes) != t.Hash) throw new InvalidOperationException("快照完整性校验失败");
                var sizes = PrototypeArena.Current.Surfaces.ToDictionary(p => p.Key, p => p.Value.TextureBytes);
                var checkpoint = PaintSnapshotCodec.Decode(t.Bytes, Arena.BakedTopology, Arena.Surfaces.Values.Where(s => s.Ownership != null).ToDictionary(s => s.SurfaceId, s => s.Ownership.SnapshotBytes), sizes);
                if (checkpoint.Round != _paintRound) return;
                PrototypeArena.Current.ClearPaint();
                Arena.RestoreOwnership(checkpoint.Ownership);
                foreach (var pair in checkpoint.Surfaces) PrototypeArena.Current.Surfaces[pair.Key].Restore(pair.Value);
                _appliedSequence = checkpoint.Sequence;
                foreach (var sequence in _buffered.Keys.Where(s => s <= _appliedSequence).ToArray()) _buffered.Remove(sequence);
                _incoming = null; InitialSyncComplete = true; DrainPaint(); AckSnapshotRpc(id);
                Debug.Log($"[INK] Snapshot applied round={_paintRound} seq={_appliedSequence} surfaces={checkpoint.Surfaces.Count} hash={Arena.OwnershipHash()}");
            }
            catch (Exception e) { Debug.LogException(e); _incoming = null; RequestSnapshotRpc(); }
        }
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void AckSnapshotRpc(uint id, RpcParams rpc = default)
        { if (_transfers.TryGetValue(rpc.Receive.SenderClientId, out var t) && t.Id == id) _transfers.Remove(rpc.Receive.SenderClientId); }
    }
}
