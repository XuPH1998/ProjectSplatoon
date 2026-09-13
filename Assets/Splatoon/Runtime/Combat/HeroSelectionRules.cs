using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum HeroSelectionOrigin : byte { Warmup, Debug }
    public static class HeroSelectionRules
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool Development = true;
#else
        public const bool Development = false;
#endif
        public static string Validate(PlayerSnapshot s, int heroId, HeroSelectionOrigin origin,
            uint expectedRound, uint round, uint lifecycle, MatchPhase phase, bool development)
        {
            if (LubanConfigService.Current.Tables.TbHero.GetOrDefault(heroId) == null) return "英雄不存在";
            if (s.Revision != lifecycle || expectedRound != round) return "角色或回合已变化，请重新选择";
            if (s.Health <= 0) return "重生后才能切换英雄";
            if (origin != HeroSelectionOrigin.Warmup && origin != HeroSelectionOrigin.Debug) return "切换英雄入口无效";
            if (origin == HeroSelectionOrigin.Debug && !development) return "当前环境不支持调试切换英雄";
            if (phase == MatchPhase.Finished) return "结算阶段不能切换英雄";
            if (phase != MatchPhase.Practice && !(phase == MatchPhase.Playing && origin == HeroSelectionOrigin.Debug && development)) return "普通选英雄仅在热身阶段开放";
            return null;
        }
        public static bool Apply(ref PlayerSnapshot s, int heroId, bool refillInk, PlayerInputFrame input)
        {
            if (s.HeroId == heroId) return false;
            WeaponSimulation.Cancel(ref s, input, true);
            s.HeroId = heroId; s.HeroRevision++;
            s.CurrentSpread = WeaponSimulation.Spread(GameplayConfig.GetHero(heroId), !s.Grounded, 0);
            var hero = GameplayConfig.GetHero(heroId);
            s.Ink = refillInk ? hero.MaxInk : UnityEngine.Mathf.Min(s.Ink, hero.MaxInk);
            s.Health = UnityEngine.Mathf.Min(s.Health, hero.MaxHealth);
            return true;
        }
    }
}
