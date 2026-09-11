using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Networking;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch : NetworkBehaviour
    {
        public static PrototypeMatch Current { get; private set; }
        public readonly NetworkVariable<MatchStateSnapshot> State = new();
        public readonly List<PrototypePlayer> Players = new();
        public PrototypeArena Arena => PrototypeArena.Current;
        public readonly InkProjectileService Projectiles = new();
        public uint PaintSequence { get; private set; }
        public uint AppliedPaintSequence => IsServer ? PaintSequence : _appliedSequence;
        public bool InitialSyncComplete { get; private set; }
        private uint _appliedSequence, _paintRound;
        private readonly List<PaintStamp> _pending = new(64);
        private readonly List<PaintStamp> _journal = new(1024);
        private readonly SortedDictionary<uint, PaintStamp> _buffered = new();
        public override void OnNetworkSpawn()
        {
            Current = this; _paintRound = State.Value.Round;
            if (IsServer)
            {
                State.Value = new MatchStateSnapshot { Phase = MatchPhase.Practice, TotalArea = Arena.TotalArea };
                InitialSyncComplete = true; NetworkManager.NetworkTickSystem.Tick += ServerTick;
            }
            else RequestSnapshotRpc();
            Debug.Log($"[LAN] Match spawned server={IsServer} cells={Arena.CellCount} hash={Arena.OwnershipHash()}");
        }
        public void Paint(PaintSurface surface, Vector3 position, Vector3 normal, float radius, byte team, float hardness, float strength)
        {
            if (!IsServer || State.Value.Phase == MatchPhase.Finished) return;
            var stamp = new PaintStamp { Sequence = ++PaintSequence, Round = State.Value.Round, SurfaceId = surface.SurfaceId, Position = position, Normal = normal, Radius = radius, Team = team, Hardness = hardness, Strength = strength };
            PrototypeArena.Current.Apply(stamp, true); _pending.Add(stamp); _journal.Add(stamp);
        }
        public void AddPlayer(ulong clientId, GameObject prefab)
        {
            if (!IsServer || Players.Exists(p => p.OwnerClientId == clientId)) return;
            int orange = Players.FindAll(p => p.Snapshot.Value.Team == 1).Count;
            byte team = PrototypeRules.ChooseTeam(orange, Players.Count - orange);
            int slot = Players.Exists(p => p.Snapshot.Value.Team == team && p.Snapshot.Value.Slot == 0) ? 1 : 0;
            var go = Instantiate(prefab, PrototypeArena.Spawn(team, slot), Quaternion.identity);
            var p = go.GetComponent<PrototypePlayer>(); p.Initialize(team, (byte)slot);
            go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true); Players.Add(p);
            Debug.Log($"[LAN] Player joined id={clientId} team={team} slot={slot}");
        }
        public void RemovePlayer(ulong id) { Players.RemoveAll(p => p == null || p.OwnerClientId == id); _transfers.Remove(id); _waiting.Remove(id); }
        public void StartRound()
        {
            if (!IsServer || Players.Count < GameplayConfig.Mode.MinPlayers || State.Value.Phase == MatchPhase.Playing) return;
            var s = State.Value; s.Round++; s.Phase = MatchPhase.Playing; s.EndsAt = NetworkManager.ServerTime.Time + GameplayConfig.Mode.MatchSeconds;
            s.OrangeArea = s.BlueArea = 0; State.Value = s;
            ResetPaint(s.Round); ResetRoundClientRpc(s.Round);
            foreach (var p in Players) p.Respawn();
            Debug.Log($"[LAN] Round started round={s.Round} ends={s.EndsAt:F2}");
        }
        private void ResetPaint(uint round)
        {
            _paintRound = round; PaintSequence = _appliedSequence = 0; Projectiles.Clear();
            _pending.Clear(); _journal.Clear(); _buffered.Clear(); _checkpoint = null; _checkpointBytes = null;
            _transfers.Clear(); _incoming = null; _captureGeneration++; _capturing = false;
            PrototypeArena.Current.ClearPaint(); InkPresentation.Current?.Clear(); InitialSyncComplete = true;
        }
        [ClientRpc] private void ResetRoundClientRpc(uint round) { if (!IsServer) ResetPaint(round); }
        private void ServerTick()
        {
            double now = NetworkManager.ServerTime.Time; var s = State.Value;
            if (PrototypeRules.HasEnded(s.Phase, now, s.EndsAt))
            { s.Phase = MatchPhase.Finished; Projectiles.Clear(); ClearShotsClientRpc(); Debug.Log($"[LAN] Round finished orange={Arena.OrangeArea} blue={Arena.BlueArea} hash={Arena.OwnershipHash()}"); }
            State.Value = s; Players.RemoveAll(p => p == null || !p.IsSpawned);
            foreach (var p in Players) p.Simulate(1f / NetworkManager.NetworkConfig.TickRate, now, s.Phase);
            if (s.Phase != MatchPhase.Finished) Projectiles.Simulate(now);
            if (Projectiles.Spawned.Count > 0) { ShotsClientRpc(Projectiles.Spawned.ToArray()); Projectiles.Spawned.Clear(); }
            if (Projectiles.Impacts.Count > 0) { ImpactsClientRpc(Projectiles.Impacts.ToArray()); Projectiles.Impacts.Clear(); }
            if (_pending.Count > 0) { PaintClientRpc(_pending.ToArray()); _pending.Clear(); }
            s.Tick = (uint)NetworkManager.ServerTime.Tick; s.PlayerCount = Players.Count; s.OrangeArea = Arena.OrangeArea; s.BlueArea = Arena.BlueArea; State.Value = s;
        }
        [ClientRpc] private void ShotsClientRpc(InkShot[] shots) { if (State.Value.Phase != MatchPhase.Finished) foreach (var shot in shots) if (shot.Round == _paintRound) InkPresentation.Current?.Spawn(shot); }
        [ClientRpc] private void ImpactsClientRpc(InkImpact[] impacts) { foreach (var impact in impacts) if (impact.Round == _paintRound) InkPresentation.Current?.Impact(impact); }
        [ClientRpc] private void ClearShotsClientRpc() => InkPresentation.Current?.Clear();
        [ClientRpc] private void PaintClientRpc(PaintStamp[] stamps, ClientRpcParams targets = default)
        {
            if (IsServer) return;
            foreach (var stamp in stamps) if (stamp.Round == _paintRound && stamp.Sequence > _appliedSequence) _buffered[stamp.Sequence] = stamp;
            DrainPaint();
        }
        private void DrainPaint()
        {
            if (!InitialSyncComplete) return;
            while (_buffered.TryGetValue(_appliedSequence + 1, out var stamp))
            { PrototypeArena.Current.Apply(stamp, true); _buffered.Remove(++_appliedSequence); }
        }
        public override void OnNetworkDespawn()
        {
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= ServerTick;
            _captureGeneration++; Projectiles.Clear(); Players.Clear(); _transfers.Clear(); _waiting.Clear(); _buffered.Clear();
            if (Current == this) Current = null;
        }
    }
}
