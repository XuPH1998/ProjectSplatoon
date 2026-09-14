using System.Collections.Generic;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public static class TeamSelectionRules
    {
        public const int Capacity = 4;
        public static bool TryFindSlot(byte team, IEnumerable<PlayerSnapshot> roster, out byte slot)
        {
            int occupied = 0, count = 0;
            foreach (var member in roster)
            {
                if (member.Team != team) continue;
                count++;
                if (member.Slot < Capacity) occupied |= 1 << member.Slot;
            }
            for (slot = 0; slot < Capacity && count < Capacity; slot++)
                if ((occupied & (1 << slot)) == 0) return true;
            slot = 0; return false;
        }
        public static string Validate(PlayerSnapshot player, byte target, uint expectedRound, uint round,
            uint life, MatchPhase phase, IEnumerable<PlayerSnapshot> roster, out byte slot)
        {
            slot = 0;
            if (phase != MatchPhase.Practice) return "仅热身阶段可以更换队伍";
            if (player.Revision != life || expectedRound != round) return "角色或回合已变化，请重新操作";
            if (player.Health <= 0) return "重生后才能更换队伍";
            if (target < 1 || target > 2 || target == player.Team) return "目标队伍无效";
            if (!TryFindSlot(target, roster, out slot)) return $"目标队伍已满（{Capacity}/{Capacity}）";
            return null;
        }
    }
}
