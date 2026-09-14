using System.Collections.Generic;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public static class TeamSelectionRules
    {
        public const int Capacity = 2;
        public static string Validate(PlayerSnapshot player, byte target, uint expectedRound, uint round,
            uint life, MatchPhase phase, IEnumerable<PlayerSnapshot> roster, out byte slot)
        {
            slot = 0;
            if (phase != MatchPhase.Practice) return "仅热身阶段可以更换队伍";
            if (player.Revision != life || expectedRound != round) return "角色或回合已变化，请重新操作";
            if (player.Health <= 0) return "重生后才能更换队伍";
            if (target < 1 || target > 2 || target == player.Team) return "目标队伍无效";
            int count = 0; bool first = false, second = false;
            foreach (var member in roster)
            {
                if (member.Team != target) continue;
                count++; first |= member.Slot == 0; second |= member.Slot == 1;
            }
            if (count >= Capacity || (first && second)) return "目标队伍已满（2/2）";
            slot = first ? (byte)1 : (byte)0;
            return null;
        }
    }
}
