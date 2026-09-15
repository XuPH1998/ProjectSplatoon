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
        public static float MovementSpeed(PlayerSnapshot s, cfg.HeroConfig w) =>
            s.SplatlingRemaining > 0 || s.WeaponPhase == WeaponPhase.Firing ? w.ShootMoveSpeed : w.SplatlingChargeMoveSpeed;
        public static float RangeCharge(cfg.HeroConfig w, float charge) => Mathf.InverseLerp(w.SplatlingMinChargeFrames, w.SplatlingFirstChargeFrames, charge * w.ChargeFrames);
        public static float Window(cfg.HeroConfig w, float ticks) => ticks <= w.SplatlingFirstChargeFrames
            ? Mathf.Lerp(0, w.SplatlingFirstShootFrames, Mathf.InverseLerp(w.SplatlingMinChargeFrames, w.SplatlingFirstChargeFrames, ticks))
            : Mathf.Lerp(w.SplatlingFirstShootFrames, w.SplatlingFullShootFrames, Mathf.InverseLerp(w.SplatlingFirstChargeFrames, w.ChargeFrames, ticks));
        public static int Rounds(cfg.HeroConfig w, float ticks) => ticks + .0001f < w.SplatlingMinChargeFrames ? 0 :
            1 + Mathf.FloorToInt(Window(w, ticks) * w.FireRate / 60 + .00001f);
        public static void Refund(ref PlayerSnapshot s)
        {
            s.Ink += s.SplatlingReservedInk;
            s.SplatlingReservedInk = s.SplatlingCharge = 0;
            s.SplatlingLoaded = s.SplatlingRemaining = 0;
            s.SplatlingSlow = false;
            s.SplatlingEndedAt = s.SplatlingReleasedAt = s.SplatlingUpdatedAt = 0;
            s.SplatlingReleasedCharge = 0;
        }
        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now,
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
                s.SplatlingCharge = 0; s.SplatlingLoaded = s.SplatlingRemaining = 0;
                s.SplatlingEndedAt = 0;
                s.ChargeReleasePending = false;
                s.WeaponReadyAt = Math.Max(s.NextShotAt, now + WeaponSimulation.Seconds(emerged ? w.EmergeStartFrames : w.StartFrames));
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
                float elapsed = Mathf.Max(0, (float)(now - s.SplatlingUpdatedAt));
                s.SplatlingUpdatedAt = now;
                // A charge reserves its output. When the tank is exhausted, the
                // slower filling process supplies only the missing reserved ink.
                float normal = Mathf.Min(w.ChargeFrames, s.SplatlingCharge + elapsed * 60);
                float needed = ReserveAt(w, normal) - s.SplatlingReservedInk;
                s.SplatlingSlow = !s.Grounded || needed > s.Ink + .00001f;
                float next = Mathf.Min(w.ChargeFrames, s.SplatlingCharge + elapsed * 60 / (s.SplatlingSlow ? w.SplatlingSlowChargeMultiplier : 1));
                if (Mathf.Abs(next - Mathf.Round(next)) < .0002f) next = Mathf.Round(next);
                float add = Mathf.Max(0, ReserveAt(w, next) - s.SplatlingReservedInk);
                float taken = Mathf.Min(s.Ink, add);
                s.Ink -= taken; s.SplatlingReservedInk += add;
                s.SplatlingCharge = next; s.ChargeTicks = Mathf.FloorToInt(next + .00001f);
                s.SplatlingLoaded = Rounds(w, next);
                if (!s.ChargeReleasePending || s.SplatlingLoaded == 0) return false;
                // Return the fractional reservation left between discrete bullets.
                float cost = s.SplatlingLoaded * w.ShotInk;
                s.Ink = Mathf.Min(w.MaxInk, s.Ink + Mathf.Max(0, s.SplatlingReservedInk - cost)); s.SplatlingReservedInk = cost;
                s.SplatlingRemaining = s.SplatlingLoaded;
                s.SplatlingReleasedCharge = Mathf.Clamp01(next / w.ChargeFrames);
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
                s.SplatlingCharge = 0; s.ChargeTicks = s.SplatlingLoaded = 0;
                s.SplatlingEndedAt = now + WeaponSimulation.Seconds(1);
                s.FireVisualUntil = s.SplatlingEndedAt;
                s.NextShotAt = now + WeaponSimulation.Seconds(w.SplatlingPostFrames);
                s.WeaponPhase = WeaponPhase.Ending;
            }
            return true;
        }
        static float ReserveAt(cfg.HeroConfig w, float ticks)
        {
            if (ticks < w.SplatlingMinChargeFrames) return w.ShotInk * ticks / w.SplatlingMinChargeFrames;
            return Mathf.Min(Rounds(w, w.ChargeFrames), 1 + Window(w, ticks) * w.FireRate / 60) * w.ShotInk;
        }
    }
}
