using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum WeaponSelectionOrigin : byte { Warmup, Debug }
    public static class WeaponSelectionRules
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool Development = true;
#else
        public const bool Development = false;
#endif
        public static string Validate(PlayerSnapshot s, int weaponId, WeaponSelectionOrigin origin,
            uint expectedRound, uint round, uint lifecycle, MatchPhase phase, bool development)
        {
            if (LubanConfigService.Current.Tables.TbWeapon.GetOrDefault(weaponId) == null) return "枪械不存在";
            if (s.Revision != lifecycle || expectedRound != round) return "角色或回合已变化，请重新选择";
            if (s.Health <= 0) return "重生后才能换枪";
            if (origin != WeaponSelectionOrigin.Warmup && origin != WeaponSelectionOrigin.Debug) return "换枪入口无效";
            if (origin == WeaponSelectionOrigin.Debug && !development) return "当前环境不支持调试换枪";
            if (phase == MatchPhase.Finished) return "结算阶段不能换枪";
            if (phase != MatchPhase.Practice && !(phase == MatchPhase.Playing && origin == WeaponSelectionOrigin.Debug && development)) return "普通选枪仅在热身阶段开放";
            return null;
        }
        public static bool Apply(ref PlayerSnapshot s, int weaponId, bool refillInk, PlayerInputFrame input)
        {
            if (s.WeaponId == weaponId) return false;
            WeaponSimulation.Cancel(ref s, input, true);
            s.WeaponId = weaponId; s.EquipmentRevision++;
            s.CurrentSpread = WeaponSimulation.Spread(GameplayConfig.GetWeapon(weaponId), !s.Grounded, 0);
            if (refillInk) s.Ink = GameplayConfig.Character.MaxInk;
            return true;
        }
    }
}
