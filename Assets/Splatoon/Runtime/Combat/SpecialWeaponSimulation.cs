using System;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
namespace Splatoon.Combat
{
    public enum SpecialPhase:byte { Charging, Starting, Active, Recovering }
    public enum SpecialFailure:byte { None, NotReady, Busy, NoSpace }
    public static class SpecialWeaponSimulation
    {
        public static bool Active(PlayerSnapshot s)=>s.SpecialPhase!=SpecialPhase.Charging;
        public static float MovementSpeed(PlayerSnapshot s,SpecialWeaponRuntimeConfig c,double now)
        {
            bool firing=c.Type==SpecialWeaponType.Trizooka&&(s.SpecialShotAt>0||
                s.SpecialRemaining<c.P.shots&&now+1e-6<s.SpecialAttackAt+c.P.repeat-c.P.shotDelay);
            return firing?c.P.firingMoveSpeed:c.P.moveSpeed;
        }
        public static bool CanCharge(PlayerSnapshot s,double now)=>s.IsAlive&&!Active(s)&&now+1e-6>=s.SpecialChargeLockedUntil;
        public static void AddPoints(ref PlayerSnapshot s,double points,double now,float cost)
        {if(CanCharge(s,now)&&double.IsFinite(points)&&points>0)s.SpecialPoints=Math.Min(cost,s.SpecialPoints+points);}
        public static void Interrupt(ref PlayerSnapshot s)
        {if(Active(s)){s.SpecialPoints=0;s.SpecialPhase=SpecialPhase.Charging;s.SpecialRemaining=0;}s.SpecialShotAt=0;s.SpecialAiming=false;s.SpecialNeedsRelease=true;s.SpecialImpulseVelocity=Vector3.zero;s.SpecialImpulseRemaining=0;}
        public static void AddImpulse(ref PlayerSnapshot s,Vector3 velocity,float retention,float distance)
        {
            if(!s.IsAlive||distance<=0||!float.IsFinite(distance)||!float.IsFinite(velocity.sqrMagnitude))return;
            float cap=Mathf.Max(s.SpecialImpulseVelocity.magnitude,velocity.magnitude);
            s.SpecialImpulseVelocity=Vector3.ClampMagnitude(s.SpecialImpulseVelocity+velocity,cap);
            s.SpecialImpulseRetention=Mathf.Clamp(retention,0,.999f);s.SpecialImpulseRemaining=Mathf.Max(s.SpecialImpulseRemaining,distance);
        }
        public static void Die(ref PlayerSnapshot s){if(Active(s))Interrupt(ref s);else s.SpecialPoints*=.5;}
        public static bool Invincible(PlayerSnapshot s,SpecialWeaponRuntimeConfig c,double now)
        {
            if(!s.IsAlive||c.Type!=SpecialWeaponType.Reefslider||!Active(s)||s.SpecialRideStage<0)return false;
            return now+1e-6>=s.SpecialStartedAt+c.P.rideInvincibleStart&&(s.SpecialBurstAt<=0||now<s.SpecialBurstAt+c.P.rideInvincibleAfter);
        }
        public static void BeginRecovery(ref PlayerSnapshot s,SpecialWeaponRuntimeConfig c,double now)
        {s.SpecialPhase=SpecialPhase.Recovering;s.SpecialUntil=now+c.P.recovery;s.SpecialShotAt=0;s.SpecialAiming=false;}
        // Returns an attack commitment. Entity spawn occurs after authoritative/predicted movement.
        public static bool Step(ref PlayerSnapshot s,PlayerInputFrame input,SpecialWeaponRuntimeConfig c,float cost,float maxInk,bool canStand,double now)
        {
            bool currentEquipment=input.HeroRevision==s.HeroRevision;
            bool q=currentEquipment&&input.SpecialSequence!=s.SpecialConsumed;
            // Menu/focus cancellation consumes current edges, but old equipment
            // must never rewind or advance the current equipment's Q baseline.
            if(currentEquipment)s.SpecialConsumed=input.SpecialSequence;
            bool press=input.FireSequence!=s.SpecialConsumedFire,release=input.ReleaseSequence!=s.SpecialConsumedRelease;
            s.SpecialConsumedFire=input.FireSequence;s.SpecialConsumedRelease=input.ReleaseSequence;
            bool blocked=input.CancelFire||!currentEquipment;
            if(!s.IsAlive){Interrupt(ref s);return false;}
            if(q&&!blocked&&!Active(s))
            {
                s.SpecialFailure=s.SpecialPoints+1e-7<cost?SpecialFailure.NotReady:!canStand?SpecialFailure.NoSpace:now<s.SubRecoveryUntil||now<s.AttackRecoveryUntil?SpecialFailure.Busy:SpecialFailure.None;
                if(s.SpecialFailure==SpecialFailure.None)
                {
                    WeaponSimulation.Cancel(ref s,input,true);SubWeaponSimulation.Cancel(ref s,input);
                    s.Ink=maxInk;s.SpecialPoints=0;s.SpecialAction++;s.SpecialRemaining=c.P.shots;
                    s.SpecialStartedAt=now;s.SpecialUntil=now+c.P.duration;s.SpecialReadyAt=now+c.P.equipDelay+c.P.startup;
                    s.SpecialChargeLockedUntil=(c.Type==SpecialWeaponType.WaveBreaker||c.Type==SpecialWeaponType.InkStorm)?0:now+c.P.chargeLock;s.SpecialPhase=SpecialPhase.Starting;
                    s.SpecialNeedsRelease=input.Fire;s.SpecialAiming=false;s.SpecialShotAt=0;s.SpecialBurstAt=0;s.SpecialRideStage=0;s.SpecialRideSpeed=0;
                    // The ride's grounded preparation starts only after actual floor contact.
                    if(c.Type==SpecialWeaponType.Reefslider&&!s.Grounded){s.SpecialRideStage=-1;s.SpecialPoints=cost;s.SpecialChargeLockedUntil=0;}
                    s.SpecialDirection=Quaternion.Euler(0,input.Look.x,0)*Vector3.forward;
                }
            }
            if(!Active(s))return false;
            if(c.Type==SpecialWeaponType.Reefslider)
            {
                if(blocked)s.SpecialNeedsRelease=true;
                else if(s.SpecialNeedsRelease&&!input.Fire)s.SpecialNeedsRelease=false;
                return false;
            }
            if(s.SpecialPhase==SpecialPhase.Recovering){if(now+1e-6>=s.SpecialUntil)s.SpecialPhase=SpecialPhase.Charging;return false;}
            if(now+1e-6>=s.SpecialUntil){BeginRecovery(ref s,c,now);return false;}
            if(now+1e-6>=s.SpecialReadyAt)s.SpecialPhase=SpecialPhase.Active;
            if(blocked){s.SpecialAiming=false;s.SpecialNeedsRelease=true;}
            if(s.SpecialNeedsRelease){if(!input.Fire&&!blocked)s.SpecialNeedsRelease=false;press=release=false;}
            if(s.SpecialShotAt>0&&now+1e-6>=s.SpecialShotAt)
            {
                // A shot committed before losing focus still completes; uncommitted throws never do.
                s.SpecialShotAt=0;s.SpecialRemaining--;s.SpecialAttack++;s.SpecialAttackAt=now;s.SpecialReadyAt=now+c.P.repeat-(c.Type==SpecialWeaponType.Trizooka?c.P.shotDelay:0);
                if(c.Type==SpecialWeaponType.WaveBreaker)s.SpecialChargeLockedUntil=now+c.P.chargeLock;
                if(s.SpecialRemaining==0){if(c.Type==SpecialWeaponType.Trizooka||c.Type==SpecialWeaponType.TripleInkstrike)s.SpecialChargeLockedUntil=now+c.P.recovery;BeginRecovery(ref s,c,now);}
                return true;
            }
            if(blocked||s.SpecialNeedsRelease||s.SpecialPhase!=SpecialPhase.Active||now+1e-6<s.SpecialReadyAt||s.SpecialShotAt>0)return false;
            if(c.Type==SpecialWeaponType.Trizooka)
            {if(press||input.Fire)s.SpecialShotAt=now+c.P.shotDelay;}
            else
            {
                if(press&&input.Fire)s.SpecialAiming=true;
                if(release&&s.SpecialAiming)
                {
                    s.SpecialAiming=false;double delay=c.P.shotDelay;
                    // Free loadouts select the timing by the chosen sub, never by hero ID.
                    // The measured sonar animation is 5 frames, or 16 with Ink Mine.
                    if(c.Type==SpecialWeaponType.WaveBreaker&&PlayerLoadout.SubWeapon(s).Type==SubWeaponType.InkMine)delay+=c.P.mineThrowExtraDelay;
                    s.SpecialShotAt=now+Math.Max(delay,1d/60);
                }
            }
            return false;
        }
    }
}
