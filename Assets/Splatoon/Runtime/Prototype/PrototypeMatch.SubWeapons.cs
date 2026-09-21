using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        public readonly SubWeaponService SubWeapons = new();
        public SubWeaponPresentation SubPresentation { get; private set; }
        uint _subSnapshotSequence, _subIncomingSequence;
        int _subIncomingParts;
        uint _subIncomingWatermark;
        ulong _subParts;
        readonly List<SubEntityState> _subIncoming = new(256);
        readonly List<SubEntityState> _subSend = new(16);
        int _subSendTick;
        void EnsureSubPresentation()
        { if (SubPresentation == null) { SubPresentation = gameObject.AddComponent<SubWeaponPresentation>(); SubPresentation.Match = this; } }
        void TickSubWeapons()
        {
            EnsureSubPresentation();
            if (SubWeapons.Lifecycle.Count > 0)
            {
                for (int offset = 0; offset < SubWeapons.Lifecycle.Count; offset += 12)
                    SubLifecycleClientRpc(new NetworkBatch<SubLifecycleEvent>(SubWeapons.Lifecycle.GetRange(offset, Mathf.Min(12, SubWeapons.Lifecycle.Count - offset))));
                SubWeapons.Lifecycle.Clear();
            }
            // Complete snapshots at 10Hz bound bandwidth and also cover joining clients.
            if (++_subSendTick % 3 == 0)
            {
                var states = SubWeapons.Capture(); uint sequence = ++_subSnapshotSequence;
                int parts = Mathf.Max(1, Mathf.CeilToInt(states.Count / 16f));
                for (int part = 0; part < parts; part++)
                {
                    _subSend.Clear(); for (int i = part * 16; i < Mathf.Min(states.Count, part * 16 + 16); i++) _subSend.Add(states[i]);
                    SubStatesClientRpc(new NetworkBatch<SubEntityState>(_subSend), State.Value.Round, sequence, part, parts, SubWeapons.Watermark);
                }
            }
            if (SubWeapons.Effects.Count > 0)
            { SubEffectsClientRpc(new NetworkBatch<SubEffectEvent>(SubWeapons.Effects)); SubWeapons.Effects.Clear(); }
        }
        [ClientRpc]
        void SubStatesClientRpc(NetworkBatch<SubEntityState> states, uint round, uint sequence, int part, int parts, uint watermark)
        {
            try
            {
                EnsureSubPresentation();
                if (round != State.Value.Round || State.Value.Phase == MatchPhase.Finished || sequence < _subIncomingSequence || parts < 1 || parts > 64 || part < 0 || part >= parts) return;
                if (sequence > _subIncomingSequence) { _subIncomingSequence = sequence; _subIncoming.Clear(); _subParts = 0; _subIncomingParts = parts; _subIncomingWatermark = watermark; }
                ulong bit = 1UL << part;
                if (_subIncomingWatermark != watermark || _subIncomingParts != parts || (_subParts & bit) != 0) return;
                _subParts |= bit; for (int i = 0; i < states.Count; i++) _subIncoming.Add(states[i]);
                ulong complete = parts == 64 ? ulong.MaxValue : (1UL << parts) - 1;
                if (_subParts == complete) SubPresentation.Apply(_subIncoming, watermark);
            }
            finally { states.Dispose(); }
        }
        [ClientRpc]
        void SubLifecycleClientRpc(NetworkBatch<SubLifecycleEvent> events)
        { try { EnsureSubPresentation(); for (int i = 0; i < events.Count; i++) if (events[i].State.Round == State.Value.Round && State.Value.Phase != MatchPhase.Finished) SubPresentation.Lifecycle(events[i]); } finally { events.Dispose(); } }
        [ClientRpc]
        void SubEffectsClientRpc(NetworkBatch<SubEffectEvent> effects)
        { try { EnsureSubPresentation(); for (int i = 0; i < effects.Count; i++) if (effects[i].Round == State.Value.Round && State.Value.Phase != MatchPhase.Finished) SubPresentation.Effect(effects[i]); } finally { effects.Dispose(); } }
        void ClearSubWeapons()
        { SubWeapons.Clear(); SubPresentation?.Clear(); _subIncoming.Clear(); _subParts = 0; }
    }
}
