using System;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    /// <summary>A committed, prepaid volley. Existing snapshot fields carry all replay state.</summary>
    public static class BubbleVolleySimulation
    {
        public static bool Pending(PlayerSnapshot s) => s.BurstRemaining > 0 && s.WeaponPhase == WeaponPhase.Firing;

        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now)
            => !input.CancelFire && !s.AttackNeedsRelease && (Pending(s) || s.WeaponPhase == WeaponPhase.Starting ||
                (!input.Swim || s.Swimming) && (
                s.Ink + PrototypeRules.InkTolerance >= w.ShotInk && now + 1e-8 >= s.BurstReadyAt &&
                (input.Fire || input.FireSequence != s.ConsumedFire)));

        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w,
            double now, bool emerged, bool canShoot, bool edge, out WeaponFireResult result)
        {
            result = default;
            s.ConsumedFire = input.FireSequence;
            if (!input.Fire) s.SemiHoldStarted = false;
            if (!canShoot) { WeaponSimulation.Cancel(ref s, input); return false; }
            if (!Pending(s) && s.WeaponPhase != WeaponPhase.Starting)
            {
                s.WeaponPhase = WeaponPhase.Idle;
                if (input.Swim && !emerged || !(input.Fire || edge) || now + 1e-8 < s.BurstReadyAt || s.Ink + PrototypeRules.InkTolerance < w.ShotInk) return false;
                s.WeaponPhase = WeaponPhase.Starting;
                // Held repetitions use the exact first-to-first deadline; fresh clicks have startup.
                s.WeaponReadyAt = s.SemiHoldStarted && input.Fire ? Math.Max(now, s.BurstReadyAt)
                    : now + (emerged ? w.EmergeStartSeconds : w.StartSeconds);
            }
            if (s.WeaponPhase == WeaponPhase.Starting)
            {
                if (now + 1e-8 < s.WeaponReadyAt) return false;
                if (!PrototypeRules.Spend(ref s.Ink, w.ShotInk)) { WeaponSimulation.Cancel(ref s, input); return false; }
                s.FireBurstSequence = s.ShotSequence + 1; s.BurstShotIndex = 0;
                s.BurstRemaining = w.BurstCount; s.WeaponPhase = WeaponPhase.Firing;
                s.FireStartedAt = now; s.BurstReadyAt = now + w.BubbleVolleySeconds;
                s.NextShotAt = now; s.FireVisualUntil = s.BurstReadyAt;
                s.SemiHoldStarted = input.Fire;
            }
            if (!input.Fire) s.SemiHoldStarted = false;
            if (now + 1e-8 < s.NextShotAt) return false;
            s.ShotSequence++; s.BurstShotIndex++; s.BurstRemaining--;
            s.LastShotMuzzle = 0; s.LastShotCharge = 0; s.Firing = true; s.ProtectedUntil = 0;
            if (s.BurstShotIndex == 1) { s.RightShotAction = s.ShotActionId; s.RightShotAt = now; }
            s.NextShotAt += w.BubbleIntervalSeconds;
            s.InkRecoverAt = now + w.InkRecoverLockSeconds;
            if (s.BurstRemaining == 0)
            { s.WeaponPhase = WeaponPhase.Idle; s.NextShotAt = s.BurstReadyAt; if (input.Swim && input.Fire) s.AttackNeedsRelease = true; }
            result = new WeaponFireResult(s.HeroId, 0, s.ShotActionId);
            return true;
        }
    }
}
