using System;
using System.Collections.Generic;
using Unity.Netcode;
using Splatoon.Networking;

namespace Splatoon.Combat
{
    public struct PlayerCombatStats : INetworkSerializable, IEquatable<PlayerCombatStats>
    {
        public uint Round;
        public int Kills, Deaths, Assists;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Round); serializer.SerializeValue(ref Kills);
            serializer.SerializeValue(ref Deaths); serializer.SerializeValue(ref Assists);
        }
        public bool Equals(PlayerCombatStats other) => Round == other.Round && Kills == other.Kills && Deaths == other.Deaths && Assists == other.Assists;
    }

    /// <summary>Server-only combat ledger. Contributions belong to a victim's current life.</summary>
    public sealed class MatchCombatStats
    {
        public const double AssistSeconds = 5;
        sealed class Entry
        {
            public byte Team;
            public uint Life;
            public bool Dead;
            public PlayerCombatStats Stats;
            public readonly Dictionary<ulong, double> Contributors = new();
        }
        readonly Dictionary<ulong, Entry> _players = new();
        uint _round;
        public PlayerCombatStats Get(ulong id) => _players.TryGetValue(id, out var entry) ? entry.Stats : new PlayerCombatStats { Round = _round };
        public void BeginLife(ulong id, byte team, uint life)
        {
            if (!_players.TryGetValue(id, out var entry)) _players.Add(id, entry = new Entry { Stats = new PlayerCombatStats { Round = _round } });
            entry.Team = team; entry.Life = life; entry.Dead = false; entry.Contributors.Clear();
        }
        public void Remove(ulong id)
        {
            _players.Remove(id);
            foreach (var entry in _players.Values) entry.Contributors.Remove(id);
        }
        public void Reset(uint round)
        {
            _round = round;
            foreach (var entry in _players.Values)
            { entry.Stats = new PlayerCombatStats { Round = round }; entry.Contributors.Clear(); }
        }
        public void Clear() { _players.Clear(); _round = 0; }
        public void RecordDamage(ulong victim, uint life, ulong? attacker, byte attackerTeam, float actualDamage, bool killed, double now, MatchPhase phase)
        {
            if (phase != MatchPhase.Playing || !float.IsFinite(actualDamage) || actualDamage <= 0 ||
                !_players.TryGetValue(victim, out var target) || target.Life != life || target.Dead) return;
            ulong? killer = null;
            if (attacker.HasValue && attacker.Value != victim && _players.TryGetValue(attacker.Value, out var source) &&
                source.Team == attackerTeam && source.Team != target.Team)
            { target.Contributors[attacker.Value] = now; killer = attacker; }
            if (killed) RecordDeath(victim, life, killer, now, phase);
        }
        public void RecordDeath(ulong victim, uint life, ulong? killer, double now, MatchPhase phase)
        {
            if (phase != MatchPhase.Playing || !_players.TryGetValue(victim, out var target) || target.Life != life || target.Dead) return;
            target.Dead = true; target.Stats.Deaths++;
            if (killer.HasValue && killer.Value != victim && _players.TryGetValue(killer.Value, out var source) && source.Team != target.Team)
            {
                source.Stats.Kills++;
                foreach (var pair in target.Contributors)
                    if (pair.Key != killer.Value && now >= pair.Value && now - pair.Value <= AssistSeconds &&
                        _players.TryGetValue(pair.Key, out var assistant) && assistant.Team == source.Team)
                        assistant.Stats.Assists++;
            }
            target.Contributors.Clear();
        }
    }
}
