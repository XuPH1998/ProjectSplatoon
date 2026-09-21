using System;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public enum SubWeaponPhase : byte { Idle, Holding, Starting }
    public enum SubWeaponFailure : byte { None, NoInk, Busy, NoStandingRoom, InvalidGround, ActiveLimit }
    public static class SubWeaponSimulation
    {
        public static void Cancel(ref PlayerSnapshot s, PlayerInputFrame input, bool requireRelease = true)
        {
            s.SubPhase = SubWeaponPhase.Idle; s.SubCharge = 0; s.SubChargeSeconds = 0;
            s.SubNeedsRelease = requireRelease; s.SubConsumedPress = input.SubPressSequence; s.SubConsumedRelease = input.SubReleaseSequence;
        }
        public static bool BlocksMain(PlayerSnapshot s, double now) => s.SubPhase != SubWeaponPhase.Idle || now + 1e-8 < s.SubRecoveryUntil;
        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, SubWeaponRuntimeConfig c, float dt, double now,
            bool canStand, SubWeaponFailure placement, out float charge, bool deferCommit = false)
        {
            charge = 0;
            bool press = input.SubPressSequence != s.SubConsumedPress;
            bool release = input.SubReleaseSequence != s.SubConsumedRelease;
            s.SubConsumedPress = input.SubPressSequence; s.SubConsumedRelease = input.SubReleaseSequence;
            if (s.Health <= 0 || input.CancelSub || input.CancelFire || input.HeroRevision != s.HeroRevision || input.Swim)
            { Cancel(ref s, input); return false; }
            if (s.SubNeedsRelease)
            { if (!input.SubHeld) s.SubNeedsRelease = false; return false; }
            if (s.SubPhase == SubWeaponPhase.Idle)
            {
                if (!press) return false;
                s.SubFailure = SubWeaponFailure.None;
                if (now + 1e-8 < s.SubReadyAt || now + 1e-8 < s.AttackRecoveryUntil || now + 1e-8 < s.SubRecoveryUntil)
                { s.SubFailure = SubWeaponFailure.Busy; s.SubNeedsRelease = true; return false; }
                if (!canStand) { s.SubFailure = SubWeaponFailure.NoStandingRoom; s.SubNeedsRelease = true; return false; }
                // Main weapon cancellation releases any reserved splatling ink exactly once.
                WeaponSimulation.Cancel(ref s, input, true);
                if (s.Ink + .0001f < c.Common.inkCost) { s.SubFailure = SubWeaponFailure.NoInk; s.SubNeedsRelease = true; return false; }
                s.SubPhase = SubWeaponPhase.Holding; s.SubChargeSeconds = 0;
            }
            if (s.SubPhase == SubWeaponPhase.Holding)
            {
                if (input.SubHeld) s.SubChargeSeconds += dt;
                s.SubCharge = c.Charge(s.SubChargeSeconds);
                if (release) { s.SubPhase = SubWeaponPhase.Starting; s.SubReleaseAt = now + c.Common.startup; }
                else if (!input.SubHeld) { Cancel(ref s, input, false); return false; }
            }
            if (deferCommit || !ReadyToCommit(s, now)) return false;
            return Commit(ref s, input, c, now, canStand, placement, out charge);
        }
        public static bool ReadyToCommit(PlayerSnapshot s, double now) => s.SubPhase == SubWeaponPhase.Starting && now + 1e-8 >= s.SubReleaseAt;
        public static bool Commit(ref PlayerSnapshot s, PlayerInputFrame input, SubWeaponRuntimeConfig c, double now, bool canStand, SubWeaponFailure placement, out float charge)
        {
            charge = 0;
            if (!ReadyToCommit(s, now)) return false;
            s.SubPhase = SubWeaponPhase.Idle;
            s.SubFailure = !canStand ? SubWeaponFailure.NoStandingRoom : placement;
            if (s.Ink + .0001f < c.Common.inkCost) s.SubFailure = SubWeaponFailure.NoInk;
            if (s.SubFailure != SubWeaponFailure.None) { s.SubNeedsRelease = input.SubHeld; return false; }
            charge = s.SubCharge; s.Ink = Mathf.Max(0, s.Ink - c.Common.inkCost);
            s.SubRecoveryUntil = now + c.Common.recovery; s.SubReadyAt = now + c.Common.reuse;
            s.InkRecoverAt = Math.Max(s.InkRecoverAt, now + c.InkLock(charge));
            s.SubAction++; s.SubUsedAt = now; s.SubNeedsRelease = input.SubHeld;
            return true;
        }
        public static string FailureText(SubWeaponFailure failure) => failure switch
        {
            SubWeaponFailure.NoInk => "墨水不足", SubWeaponFailure.Busy => "动作恢复中",
            SubWeaponFailure.NoStandingRoom => "空间不足", SubWeaponFailure.InvalidGround => "需要可涂墨的非敌方地面",
            SubWeaponFailure.ActiveLimit => "等待当前鱼雷结束", _ => "按住 E 瞄准，松开使用"
        };
    }
}
