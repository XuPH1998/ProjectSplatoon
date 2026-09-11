using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed class PrototypeMatch : NetworkBehaviour
    {
        public static PrototypeMatch Current { get; private set; }
        public readonly NetworkVariable<MatchStateSnapshot> State = new();
        public NetworkList<byte> Cells;
        public readonly List<PrototypePlayer> Players = new();
        public PaintGrid Grid => PrototypeArena.Current.Grid;
        private void Awake() => Cells = new NetworkList<byte>();
        public override void OnNetworkSpawn()
        {
            Current = this;
            if (IsServer)
            {
                for (int i = 0; i < Grid.Cells.Length; i++) Cells.Add(Grid.Cells[i]);
                State.Value = new MatchStateSnapshot { Phase = MatchPhase.Practice, TotalCells = Grid.Total };
                NetworkManager.NetworkTickSystem.Tick += ServerTick;
            }
            else for (int i = 0; i < Cells.Count; i++) ApplyCell(i, Cells[i]);
            Cells.OnListChanged += OnCellsChanged;
            Debug.Log($"[LAN] Match spawned server={IsServer} cells={Cells.Count} hash={Grid.Hash()}");
        }
        private void OnCellsChanged(NetworkListEvent<byte> change)
        {
            if (change.Type == NetworkListEvent<byte>.EventType.Value) ApplyCell(change.Index, change.Value);
            else if (change.Type == NetworkListEvent<byte>.EventType.Full)
                for (int i = 0; i < Cells.Count; i++) ApplyCell(i, Cells[i]);
        }
        private void ApplyCell(int index, byte team)
        { Grid.Set(index, team); PrototypeArena.Current.RefreshCell(index); }
        public void Paint(Vector3 position, byte team)
        {
            if (!IsServer || State.Value.Phase == MatchPhase.Finished) return;
            Grid.Paint(position, PrototypeSettings.Value("PaintRadius"), team, (i, t) => Cells[i] = t);
        }
        public void AddPlayer(ulong clientId, GameObject prefab)
        {
            if (!IsServer || Players.Exists(p => p.OwnerClientId == clientId)) return;
            int orange = Players.FindAll(p => p.Snapshot.Value.Team == 1).Count;
            byte team = PrototypeRules.ChooseTeam(orange, Players.Count - orange);
            int slot = Players.Exists(p => p.Snapshot.Value.Team == team && p.Snapshot.Value.Slot == 0) ? 1 : 0;
            var go = Instantiate(prefab, PrototypeArena.Spawn(team, slot), Quaternion.identity);
            var p = go.GetComponent<PrototypePlayer>();
            p.Initialize(team, (byte)slot);
            go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
            Players.Add(p);
            Debug.Log($"[LAN] Player joined id={clientId} team={team} slot={slot}");
        }
        public void RemovePlayer(ulong id) => Players.RemoveAll(p => p == null || p.OwnerClientId == id);
        public void StartRound()
        {
            if (!IsServer || !PrototypeRules.CanStart(Players.Count, State.Value.Phase)) return;
            Grid.Clear((i, t) => Cells[i] = t);
            foreach (var p in Players) p.Respawn();
            var s = State.Value; s.Round++; s.Phase = MatchPhase.Playing;
            s.EndsAt = NetworkManager.ServerTime.Time + PrototypeSettings.Value("MatchSeconds");
            s.OrangeCells = s.BlueCells = 0; State.Value = s;
            Debug.Log($"[LAN] Round started round={s.Round} ends={s.EndsAt:F2}");
        }
        private void ServerTick()
        {
            double now = NetworkManager.ServerTime.Time;
            var s = State.Value;
            if (PrototypeRules.HasEnded(s.Phase, now, s.EndsAt))
            { s.Phase = MatchPhase.Finished; Debug.Log($"[LAN] Round finished orange={Grid.Orange} blue={Grid.Blue} hash={Grid.Hash()}"); }
            // Set the phase first so no last-tick shot can occur after the deadline.
            State.Value = s;
            Players.RemoveAll(p => p == null || !p.IsSpawned);
            foreach (var p in Players) p.Simulate(1f / NetworkManager.NetworkConfig.TickRate, now, s.Phase);
            s.Tick = (uint)NetworkManager.ServerTime.Tick; s.PlayerCount = Players.Count;
            s.OrangeCells = Grid.Orange; s.BlueCells = Grid.Blue; State.Value = s;
        }
        public override void OnNetworkDespawn()
        {
            if (NetworkManager.NetworkTickSystem != null) NetworkManager.NetworkTickSystem.Tick -= ServerTick;
            Cells.OnListChanged -= OnCellsChanged;
            Players.Clear();
            if (Current == this) Current = null;
        }
    }
}
