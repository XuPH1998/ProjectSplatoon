using Splatoon.Config;
using System;
using UnityEngine;
using Splatoon.Prototype;
using Splatoon.Networking;

namespace Splatoon.Combat
{
    /// <summary>Deterministic charge magazine. Ink is reserved, then spent only by emitted rounds.</summary>
    public static class SplatlingSimulation
    {
        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input, double now) => !input.CancelFire && !s.AttackNeedsRelease &&
            (Charging(s) || s.SplatlingRemaining > 0 ||
             ((s.WeaponPhase != WeaponPhase.Ending || now + 1e-8 >= s.NextShotAt) && (input.Fire || input.FireSequence != s.ConsumedFire)));
        public static bool Charging(PlayerSnapshot s) => s.WeaponPhase == WeaponPhase.Starting || s.WeaponPhase == WeaponPhase.Charging;
        public static float MovementSpeed(PlayerSnapshot s, WeaponRuntimeConfig w) =>
            s.SplatlingRemaining > 0 || s.WeaponPhase == WeaponPhase.Firing ? w.ShootMoveSpeed : w.SplatlingChargeMoveSpeed;
        public static float RangeCharge(WeaponRuntimeConfig w, float charge) => (float)Fraction(w.SplatlingMinChargeSeconds, w.SplatlingFirstChargeSeconds, charge * w.ChargeSeconds);
        public static double Fraction(double from, double to, double seconds) => to > from ? Math.Clamp((seconds - from) / (to - from), 0, 1) : 0;
        public static double Window(WeaponRuntimeConfig w, double chargeSeconds) => chargeSeconds <= w.SplatlingFirstChargeSeconds
            ? w.SplatlingFirstShootSeconds * Fraction(w.SplatlingMinChargeSeconds, w.SplatlingFirstChargeSeconds, chargeSeconds)
            : w.SplatlingFirstShootSeconds + (w.SplatlingFullShootSeconds - w.SplatlingFirstShootSeconds) * Fraction(w.SplatlingFirstChargeSeconds, w.ChargeSeconds, chargeSeconds);
        public static int Rounds(WeaponRuntimeConfig w, double chargeSeconds) => chargeSeconds + .0001 / 60 < w.SplatlingMinChargeSeconds ? 0 :
            1 + (int)Math.Floor(Window(w, chargeSeconds) * w.FireRate + .00001);
        static double SnapCharge(WeaponRuntimeConfig w, double seconds)
        {
            const double tolerance = .0002 / 60;
            // Keep the legacy boundary tolerance in seconds, including arbitrary
            // authored endpoints that do not fall on a reference tick.
            if (Math.Abs(seconds - w.ChargeSeconds) < tolerance) return w.ChargeSeconds;
            if (Math.Abs(seconds - w.SplatlingFirstChargeSeconds) < tolerance) return w.SplatlingFirstChargeSeconds;
            if (Math.Abs(seconds - w.SplatlingMinChargeSeconds) < tolerance) return w.SplatlingMinChargeSeconds;
            double reference = Math.Round(seconds * 60) / 60;
            return Math.Clamp(Math.Abs(seconds - reference) < tolerance ? reference : seconds, 0, w.ChargeSeconds);
        }
        public static void Refund(ref PlayerSnapshot s)
        {
            s.Ink += s.SplatlingReservedInk;
            s.SplatlingReservedInk = 0; s.SplatlingChargeSeconds = 0;
            s.SplatlingLoaded = s.SplatlingRemaining = 0;
            s.SplatlingSlow = false;
            s.SplatlingEndedAt = s.SplatlingReleasedAt = s.SplatlingUpdatedAt = 0;
            s.SplatlingReleasedCharge = 0;
        }
        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig hero, WeaponRuntimeConfig w, double now,
            bool emerged, bool canShoot, bool edge, bool release, out WeaponFireResult result)
        {
            result = default;
            if (!canShoot) { WeaponSimulation.Cancel(ref s, input, true); return false; }
            if (s.WeaponPhase == WeaponPhase.Ending)
            {
                s.ConsumedFire = input.FireSequence;
                if (now + 1e-8 < s.NextShotAt) return false;
                s.WeaponPhase = WeaponPhase.Idle;
                // Earlier clicks were consumed on their own ticks. A held trigger
                // or a new edge exactly at recovery completion may start again.
            }
            if (s.WeaponPhase == WeaponPhase.Idle)
            {
                if (!(input.Fire || edge)) return false;
                s.WeaponPhase = WeaponPhase.Starting; s.ConsumedFire = input.FireSequence;
                s.FireBurstSequence = input.Sequence; s.BurstShotIndex = 0;
                s.SplatlingChargeSeconds = 0; s.SplatlingLoaded = s.SplatlingRemaining = 0;
                s.SplatlingEndedAt = 0;
                s.ChargeReleasePending = false;
                s.WeaponReadyAt = Math.Max(s.NextShotAt, now + (emerged ? w.EmergeStartSeconds : w.StartSeconds));
                s.ChargeStartedAt = s.SplatlingUpdatedAt = s.WeaponReadyAt;
            }
            s.ConsumedFire = input.FireSequence;
            if (Charging(s) && (release || !input.Fire)) s.ChargeReleasePending = true;
            if (s.WeaponPhase == WeaponPhase.Starting)
            {
                if (now + 1e-8 < s.WeaponReadyAt) return false;
                s.WeaponPhase = WeaponPhase.Charging;
            }
            if (s.WeaponPhase == WeaponPhase.Charging)
            {
                double elapsed = Math.Max(0, now - s.SplatlingUpdatedAt);
                s.SplatlingUpdatedAt = now;
                // A charge reserves its output. When the tank is exhausted, the
                // slower filling process supplies only the missing reserved ink.
                double normal = Math.Min(w.ChargeSeconds, s.SplatlingChargeSeconds + elapsed);
                float needed = ReserveAt(w, normal) - s.SplatlingReservedInk;
                s.SplatlingSlow = !s.Grounded || needed > s.Ink + PrototypeRules.InkTolerance;
                double next = SnapCharge(w, Math.Min(w.ChargeSeconds, s.SplatlingChargeSeconds + elapsed / (s.SplatlingSlow ? w.SplatlingSlowChargeMultiplier : 1)));
                float add = Mathf.Max(0, ReserveAt(w, next) - s.SplatlingReservedInk);
                float taken = Mathf.Min(s.Ink, add);
                s.Ink -= taken; s.SplatlingReservedInk += add;
                s.SplatlingChargeSeconds = next; s.ChargeElapsedSeconds = next;
                s.SplatlingLoaded = Rounds(w, next);
                if (!s.ChargeReleasePending || s.SplatlingLoaded == 0) return false;
                // Return the fractional reservation left between discrete bullets.
                float cost = s.SplatlingLoaded * w.ShotInk;
                s.Ink = Mathf.Min(hero.MaxInk, s.Ink + Mathf.Max(0, s.SplatlingReservedInk - cost)); s.SplatlingReservedInk = cost;
                s.SplatlingRemaining = s.SplatlingLoaded;
                s.SplatlingReleasedCharge = (float)Math.Clamp(next / w.ChargeSeconds, 0, 1);
                s.SplatlingReleasedAt = s.NextShotAt = now;
                s.SplatlingEndedAt = 0;
                s.WeaponPhase = WeaponPhase.Firing; s.ChargeReleasePending = false;
            }
            if (s.WeaponPhase != WeaponPhase.Firing || s.SplatlingRemaining <= 0 || now + 1e-8 < s.NextShotAt) return false;
            s.SplatlingReservedInk = Mathf.Max(0, s.SplatlingReservedInk - w.ShotInk);
            if (!WeaponSimulation.Emit(ref s, w, now, s.SplatlingReleasedCharge, out result, true)) return false;
            s.SplatlingRemaining--;
            if (s.SplatlingRemaining == 0)
            {
                s.SplatlingReservedInk = 0; s.SplatlingSlow = false;
                s.SplatlingChargeSeconds = 0; s.ChargeElapsedSeconds = s.SplatlingLoaded = 0;
                s.SplatlingEndedAt = now + WeaponSimulation.Seconds(1);
                s.FireVisualUntil = s.SplatlingEndedAt;
                s.NextShotAt = now + w.SplatlingPostSeconds;
                s.WeaponPhase = WeaponPhase.Ending;
            }
            return true;
        }
        static float ReserveAt(WeaponRuntimeConfig w, double chargeSeconds)
        {
            if (chargeSeconds < w.SplatlingMinChargeSeconds) return (float)(w.ShotInk * chargeSeconds / w.SplatlingMinChargeSeconds);
            return (float)(Math.Min(Rounds(w, w.ChargeSeconds), 1 + Window(w, chargeSeconds) * w.FireRate) * w.ShotInk);
        }
    }
}
