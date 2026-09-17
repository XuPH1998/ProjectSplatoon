using System.Collections.Generic;
using System.Linq;
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
        public readonly MatchCombatStats CombatStats = new();
        public bool CanStartRound => PrototypeRules.CanStart(
            Players.Count(p => p != null && p.IsSpawned && !p.IsTestBot && p.Snapshot.Value.Team == 1),
            Players.Count(p => p != null && p.IsSpawned && !p.IsTestBot && p.Snapshot.Value.Team == 2), State.Value.Phase, GameplayConfig.Mode.MinPlayers);
        public void PublishCombatStats()
        {
            if (!IsServer) return;
            foreach (var player in Players) if (player != null && player.IsSpawned) player.Stats.Value = CombatStats.Get(player.PlayerId);
        }
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
            if (!IsServer) return;
            Players.RemoveAll(p => p == null || !p.IsSpawned);
            if (Players.Exists(p => !p.IsTestBot && p.OwnerClientId == clientId)) return;
            // A connected human must never be left without a player because a test target occupies the final slot.
            if (Players.Count >= GameplayConfig.Mode.MaxPlayers)
            {
                var bot = Players.FirstOrDefault(p => p.IsTestBot);
                if (bot != null) RemoveTestBot(bot);
            }
            if (Players.Count >= GameplayConfig.Mode.MaxPlayers) return;
            int pink = Players.FindAll(p => p.Snapshot.Value.Team == 1).Count;
            byte team = PrototypeRules.ChooseTeam(pink, Players.Count - pink);
            if (!TeamSelectionRules.TryFindSlot(team, Players.Select(p => p.Snapshot.Value), out byte slot)) return;
            var go = Instantiate(prefab, PrototypeArena.Spawn(team, slot), Quaternion.identity);
            var p = go.GetComponent<PrototypePlayer>(); p.Initialize(team, (byte)slot);
            go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true); Players.Add(p);
            Debug.Log($"[LAN] Player joined id={clientId} team={team} slot={slot}");
        }
        public void RemovePlayer(ulong id) { Players.RemoveAll(p => p == null || (!p.IsTestBot && p.OwnerClientId == id)); CombatStats.Remove(id); _transfers.Remove(id); _waiting.Remove(id); }
        public void StartRound()
        {
            if (!IsServer || !CanStartRound || (PrototypeApp.Current != null && PrototypeApp.Current.IsWeaponDebugRoom)) return;
            ClearTestBots();
            var s = State.Value; s.Round++; s.Phase = MatchPhase.Playing;
            s.StartsAt = NetworkManager.ServerTime.Time; s.EndsAt = s.StartsAt + GameplayConfig.Mode.MatchSeconds;
            s.PinkArea = s.BlueArea = 0; State.Value = s;
            ResetPaint(s.Round); ResetRoundClientRpc(s.Round);
            CombatStats.Reset(s.Round);
            foreach (var p in Players) if (p != null && p.IsSpawned) p.Respawn();
            PublishCombatStats();
            Debug.Log($"[LAN] Round started round={s.Round} ends={s.EndsAt:F2}");
        }
        public void ReturnToRoom()
        {
            if (!IsServer || State.Value.Phase != MatchPhase.Finished) return;
            var s = State.Value; s.Round++; s.Phase = MatchPhase.Practice;
            s.StartsAt = s.EndsAt = 0; s.PinkArea = s.BlueArea = 0;
            State.Value = s;
            ResetPaint(s.Round); ResetRoundClientRpc(s.Round); CombatStats.Reset(s.Round);
            foreach (var player in Players) if (player != null && player.IsSpawned) player.Respawn();
            PublishCombatStats();
            Debug.Log($"[LAN] Returned to room generation={s.Round}");
        }
        private void ResetPaint(uint round)
        {
            _paintRound = round; PaintSequence = _appliedSequence = 0; Projectiles.Clear();
            _pending.Clear(); _journal.Clear(); _buffered.Clear(); _checkpoint = null; _checkpointBytes = null;
            _transfers.Clear(); _incoming = null; _captureGeneration++; _capturing = false;
            ResetSnapshotTransfer(false);
            PrototypeArena.Current.ClearPaint(); InkPresentation.Current?.Clear(); InitialSyncComplete = true;
        }
        [ClientRpc] private void ResetRoundClientRpc(uint round) { if (!IsServer && round > _paintRound) ResetPaint(round); }
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
#if UNITY_EDITOR
            PrototypeApp.Current?.ApplyDebugWeaponChanges();
#endif
            if (PrototypeRules.HasEnded(s.Phase, now, s.EndsAt))
            { s.Phase = MatchPhase.Finished; Projectiles.Clear(); ClearShotsClientRpc(s.Round); Debug.Log($"[LAN] Round finished pink={Arena.PinkArea} blue={Arena.BlueArea} hash={Arena.OwnershipHash()}"); }
            State.Value = s; Players.RemoveAll(p => p == null || !p.IsSpawned);
            foreach (var p in Players) p.Simulate(1f / GameplayConfig.Global.SimulationRate, now, s.Phase);
            Physics.SyncTransforms(); // Publish switched/rotated swim hit volumes before authoritative projectile sweeps.
            if (s.Phase != MatchPhase.Finished) Projectiles.Simulate(now);
        }
        private void ServerTick()
        {
            var s = State.Value;
            if (Projectiles.Spawned.Count > 0) { ShotsClientRpc(new NetworkBatch<InkShot>(Projectiles.Spawned)); Projectiles.Spawned.Clear(); }
            if (Projectiles.Bounces.Count > 0) { BouncesClientRpc(new NetworkBatch<InkBounce>(Projectiles.Bounces)); Projectiles.Bounces.Clear(); }
            if (Projectiles.Impacts.Count > 0) { ImpactsClientRpc(new NetworkBatch<InkImpact>(Projectiles.Impacts)); Projectiles.Impacts.Clear(); }
            if (Projectiles.Explosions.Count > 0) { ExplosionsClientRpc(new NetworkBatch<InkExplosionEvent>(Projectiles.Explosions)); Projectiles.Explosions.Clear(); }
            if (_pending.Count > 0) { PaintClientRpc(new NetworkBatch<PaintStamp>(_pending)); _pending.Clear(); }
            s.Tick = (uint)NetworkManager.ServerTime.Tick; s.PlayerCount = Players.Count; s.PinkArea = Arena.PinkArea; s.BlueArea = Arena.BlueArea; State.Value = s;
        }
        [ClientRpc] private void ShotsClientRpc(NetworkBatch<InkShot> shots)
        { try { if (State.Value.Phase != MatchPhase.Finished) for (int i = 0; i < shots.Count; i++) { var shot = shots[i]; if (shot.Round == _paintRound) InkPresentation.Current?.Spawn(shot); } } finally { shots.Dispose(); } }
        [ClientRpc] private void BouncesClientRpc(NetworkBatch<InkBounce> bounces)
        { try { for (int i = 0; i < bounces.Count; i++) if (bounces[i].Round == _paintRound) InkPresentation.Current?.Bounce(bounces[i]); } finally { bounces.Dispose(); } }
        [ClientRpc] private void BubbleStatesClientRpc(NetworkBatch<InkBubbleState> states, ClientRpcParams targets = default)
        { try { for (int i = 0; i < states.Count; i++) if (states[i].Shot.Round == _paintRound) InkPresentation.Current?.RestoreBubble(states[i]); } finally { states.Dispose(); } }
        readonly List<InkBubbleState> _bubbleSnapshot = new();
        void SendBubbles(ulong client)
        {
            Projectiles.CaptureBubbles(_bubbleSnapshot);
            if (_bubbleSnapshot.Count > 0) BubbleStatesClientRpc(new NetworkBatch<InkBubbleState>(_bubbleSnapshot),
                new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { client } } });
        }
        [ClientRpc] private void ImpactsClientRpc(NetworkBatch<InkImpact> impacts)
        { try { for (int i = 0; i < impacts.Count; i++) { var impact = impacts[i]; if (impact.Round == _paintRound) InkPresentation.Current?.Impact(impact); } } finally { impacts.Dispose(); } }
        [ClientRpc] private void ExplosionsClientRpc(NetworkBatch<InkExplosionEvent> explosions)
        { try { for (int i = 0; i < explosions.Count; i++) { var explosion = explosions[i]; if (explosion.Round == _paintRound) InkPresentation.Current?.Explosion(explosion); } } finally { explosions.Dispose(); } }
        [ClientRpc] private void ClearShotsClientRpc(uint round) { if (round == _paintRound) InkPresentation.Current?.Clear(); }
        [ClientRpc] private void PaintClientRpc(NetworkBatch<PaintStamp> stamps, ClientRpcParams targets = default)
        {
            try
            {
                if (IsServer) return;
                for (int i = 0; i < stamps.Count; i++) { var stamp = stamps[i]; if (stamp.Round == _paintRound && stamp.Sequence > _appliedSequence) _buffered[stamp.Sequence] = stamp; }
                DrainPaint();
            }
            finally { stamps.Dispose(); }
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
            _captureGeneration++; Projectiles.Clear(); CombatStats.Clear(); Players.Clear(); _transfers.Clear(); _waiting.Clear(); _buffered.Clear();
            _incoming = null; _checkpoint = null; _checkpointBytes = null; _pending.Clear(); _journal.Clear();
            ResetSnapshotTransfer(true);
            if (Current == this) Current = null;
        }
    }
}
