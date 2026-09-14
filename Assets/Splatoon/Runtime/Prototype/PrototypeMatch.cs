using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Networking;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch : NetworkBehaviour, INetworkUpdateSystem
    {
        public static PrototypeMatch Current { get; private set; }
        public readonly NetworkVariable<MatchStateSnapshot> State = new();
        public readonly List<PrototypePlayer> Players = new();
        public PrototypeArena Arena => PrototypeArena.Current;
        public readonly InkProjectileService Projectiles = new();
        public uint PaintSequence { get; private set; }
        public uint AppliedPaintSequence => IsServer ? PaintSequence : _appliedSequence;
        public bool InitialSyncComplete { get; private set; }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public int DiagnosticPendingStamps => _pending.Count;
        public int DiagnosticBufferedStamps => _buffered.Count;
        public int DiagnosticJournalStamps => _journal.Count;
        public int DiagnosticTransfers => _transfers.Count;
#endif
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
                this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
                _networkFrameOffset = NetworkManager.ServerTime.Time - Time.timeAsDouble;
            }
            else RequestSnapshotRpc();
            Debug.Log($"[LAN] Match spawned server={IsServer} cells={Arena.CellCount} hash={Arena.OwnershipHash()}");
        }
        public void Paint(PaintSurface surface, Vector3 position, Vector3 normal, float radius, byte team, float hardness, float strength, uint? shapeSeed = null)
        {
            if (!IsServer || State.Value.Phase == MatchPhase.Finished) return;
            var sequence = ++PaintSequence;
            uint entropy = InkShapeAtlas.Hash(sequence ^ (uint)surface.SurfaceId * 0x9e3779b9u ^ team);
            uint appearance = shapeSeed ?? InkShapeAtlas.Pack((int)(entropy % InkShapeAtlas.Count), entropy);
            var stamp = new PaintStamp { Sequence = sequence, Round = State.Value.Round, SurfaceId = surface.SurfaceId, Position = position, Normal = normal, Radius = radius, Team = team, Hardness = hardness, Strength = strength, ShapeSeed = appearance };
            PrototypeArena.Current.Apply(stamp, true); _pending.Add(stamp); _journal.Add(stamp);
        }
        public void AddPlayer(ulong clientId, GameObject prefab)
        {
            if (!IsServer || Players.Exists(p => p.OwnerClientId == clientId)) return;
            int pink = Players.FindAll(p => p.Snapshot.Value.Team == 1).Count;
            byte team = PrototypeRules.ChooseTeam(pink, Players.Count - pink);
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
            s.PinkArea = s.BlueArea = 0; State.Value = s;
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
        private double _simulationTime;
        private double _networkFrameOffset;
        public void NetworkUpdate(NetworkUpdateStage stage)
        {
            if (stage != NetworkUpdateStage.EarlyUpdate || !IsSpawned || !IsServer) return;
            // NGO advances its unscaled clock in PreUpdate, after Unity's fixed
            // steps. Anchor this frame's fixed timestamps to that same clock.
            // Unity caps catch-up at maximumDeltaTime; accumulating dt forever
            // therefore loses time on a hitch and makes new shots look expired.
            double frameServerTime = NetworkManager.ServerTime.Time + Time.unscaledDeltaTime;
            _networkFrameOffset = frameServerTime - Time.timeAsDouble;
        }
        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer) return;
            _simulationTime = System.Math.Max(_simulationTime, Time.fixedTimeAsDouble + _networkFrameOffset);
            double now = _simulationTime; var s = State.Value;
            if (PrototypeRules.HasEnded(s.Phase, now, s.EndsAt))
            { s.Phase = MatchPhase.Finished; Projectiles.Clear(); ClearShotsClientRpc(); Debug.Log($"[LAN] Round finished pink={Arena.PinkArea} blue={Arena.BlueArea} hash={Arena.OwnershipHash()}"); }
            State.Value = s; Players.RemoveAll(p => p == null || !p.IsSpawned);
            foreach (var p in Players) p.Simulate(1f / GameplayConfig.Global.SimulationRate, now, s.Phase);
            if (s.Phase != MatchPhase.Finished) Projectiles.Simulate(now);
        }
        private void ServerTick()
        {
            var s = State.Value;
            if (Projectiles.Spawned.Count > 0) { ShotsClientRpc(Projectiles.Spawned.ToArray()); Projectiles.Spawned.Clear(); }
            if (Projectiles.Impacts.Count > 0) { ImpactsClientRpc(Projectiles.Impacts.ToArray()); Projectiles.Impacts.Clear(); }
            if (_pending.Count > 0) { PaintClientRpc(_pending.ToArray()); _pending.Clear(); }
            s.Tick = (uint)NetworkManager.ServerTime.Tick; s.PlayerCount = Players.Count; s.PinkArea = Arena.PinkArea; s.BlueArea = Arena.BlueArea; State.Value = s;
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
            this.UnregisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= ServerTick;
            _captureGeneration++; Projectiles.Clear(); Players.Clear(); _transfers.Clear(); _waiting.Clear(); _buffered.Clear();
            if (Current == this) Current = null;
        }
    }
}
