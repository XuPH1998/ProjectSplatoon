using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum HeroSelectionOrigin : byte { Warmup = 0, Debug = 1, SpawnArea = 2 }
    public static class HeroSelectionRules
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool Development = true;
#else
        public const bool Development = false;
#endif
        public static string Validate(PlayerSnapshot s, int heroId, HeroSelectionOrigin origin,
            uint expectedRound, uint round, uint lifecycle, MatchPhase phase, bool development, bool inOwnSpawnArea = false)
        {
            if (LubanConfigService.Current.Tables.TbHero.GetOrDefault(heroId) == null) return "英雄不存在";
            if (s.Revision != lifecycle || expectedRound != round) return "角色或回合已变化，请重新选择";
            return Availability(s, origin, phase, development, inOwnSpawnArea);
        }
        public static string Availability(PlayerSnapshot s, HeroSelectionOrigin origin, MatchPhase phase,
            bool development, bool inOwnSpawnArea = false)
        {
            if (!s.IsAlive) return "重生后才能切换英雄";
            if(SpecialWeaponSimulation.Active(s))return "大招使用期间不能换装";
            if (origin != HeroSelectionOrigin.Warmup && origin != HeroSelectionOrigin.Debug && origin != HeroSelectionOrigin.SpawnArea) return "切换英雄入口无效";
            if (origin == HeroSelectionOrigin.Debug && !development) return "当前环境不支持调试切换英雄";
            if (phase == MatchPhase.Finished) return "结算阶段不能切换英雄";
            if (phase == MatchPhase.Playing && !inOwnSpawnArea) return "请回到本方出生区换装";
            if (origin == HeroSelectionOrigin.SpawnArea)
            {
                if (phase != MatchPhase.Playing) return "出生区切换仅在比赛中开放";
                return inOwnSpawnArea ? null : "请回到本方出生区切换英雄";
            }
            if (phase != MatchPhase.Practice && !(phase == MatchPhase.Playing && origin == HeroSelectionOrigin.Debug && development)) return "普通选英雄仅在热身阶段开放";
            return null;
        }
        public static bool Apply(ref PlayerSnapshot s, int heroId, bool refillInk, PlayerInputFrame input)
        {
            if (s.HeroId == heroId) return false;
            float heightChange = GameplayConfig.GetHero(heroId).StandingHeight - GameplayConfig.GetHero(s.HeroId).StandingHeight;
            if (s.AirHumanOffset != 0 && heightChange != 0)
            {
                // Keep the implied standing feet fixed when replacing an airborne paper body.
                float shift = heightChange * .5f;
                s.Position += UnityEngine.Vector3.up * shift;
                s.PaperCenter += UnityEngine.Vector3.up * shift;
                s.AirHumanOffset += shift; s.CameraRebaseOffset -= shift;
            }
            WeaponSimulation.Cancel(ref s, input, true);
            SubWeaponSimulation.Cancel(ref s, input);
            s.HeroId = heroId; s.HeroRevision++;
            s.AirSwimSource = SwimSurface.None;
            WeaponSimulation.ResetPresentation(ref s);
            SpreadSimulation.Reset(ref s, GameplayConfig.GetWeapon(heroId));
            var hero = GameplayConfig.GetHero(heroId);
            s.Ink = refillInk ? hero.MaxInk : UnityEngine.Mathf.Min(s.Ink, hero.MaxInk);
            s.Health = UnityEngine.Mathf.Min(s.Health, hero.MaxHealth);
            return true;
        }
    }
}
