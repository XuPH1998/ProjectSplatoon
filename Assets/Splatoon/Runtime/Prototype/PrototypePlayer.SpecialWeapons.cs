using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;
namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        bool _insideAuthorityStep;
        readonly System.Collections.Generic.List<(PaintCredit credit,double area,double time)> _stepPaintCredits=new();
        public void ReceivePaintCredit(PaintCredit credit,double area,double now)
        {
            if(!IsServer)return;
            // A replacement mine can paint while Step is mutating a local snapshot. Writing
            // the NetworkVariable here would both lose credit and inspect the pre-Q lock.
            if(_insideAuthorityStep){_stepPaintCredits.Add((credit,area,now));return;}
            var s=Snapshot.Value;credit.ApplyTo(ref s,area,now);Snapshot.Value=s;
        }
        public void ResetSpecialCharge(){if(!IsServer)return;var s=Snapshot.Value;SpecialWeaponSimulation.Interrupt(ref s);s.SpecialPoints=0;s.SpecialChargeLockedUntil=0;Snapshot.Value=s;}
        public void ReceiveSpecialKnockback(Vector3 direction,float acceleration,float retention,float distance)
        {
            if(!IsServer)return;var s=Snapshot.Value;
            if(!s.IsAlive||SpecialWeaponSimulation.Invincible(s,SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId),NetworkManager.ServerTime.Time))return;
            direction.y=0;if(direction.sqrMagnitude<.000001f)return;
            SpecialWeaponSimulation.AddImpulse(ref s,direction.normalized*(acceleration/60),retention,distance);Snapshot.Value=s;
        }
        void StepSpecialImpulse(ref PlayerSnapshot s,float dt)
        {
            if(!s.IsAlive||s.SpecialImpulseRemaining<=0||s.SpecialImpulseVelocity.sqrMagnitude<.000001f)
            {s.SpecialImpulseVelocity=Vector3.zero;s.SpecialImpulseRemaining=0;return;}
            Vector3 delta=Vector3.ClampMagnitude(s.SpecialImpulseVelocity*dt,s.SpecialImpulseRemaining),before=transform.position;
            if(!_controller.enabled)_controller.enabled=true;
            _controller.Move(delta);Vector3 moved=transform.position-before;
            s.Position=transform.position;s.PaperCenter+=moved;s.Velocity+=moved/dt;
            if(moved.y>.0001f)s.Grounded=false;
            s.SpecialImpulseRemaining=Mathf.Max(0,s.SpecialImpulseRemaining-delta.magnitude);
            s.SpecialImpulseVelocity*=Mathf.Pow(s.SpecialImpulseRetention,dt*60);
            if(moved.sqrMagnitude<delta.sqrMagnitude*.01f)s.SpecialImpulseVelocity=Vector3.zero;
        }
        static void CommitSpecialRecoil(ref PlayerSnapshot s,PlayerInputFrame input,SpecialWeaponRuntimeConfig c)
        {
            if(c.Type!=SpecialWeaponType.Trizooka||s.Grounded)return;
            Vector3 direction=-(Quaternion.Euler(0,input.Look.x,0)*Vector3.forward);
            float speed=c.P.recoilSpeed*(input.Move.y<0?c.P.recoilBackInputRate:1);
            SpecialWeaponSimulation.AddImpulse(ref s,direction*speed,c.P.recoilAirRetention,speed/60/Mathf.Max(.001f,1-c.P.recoilAirRetention));
        }
        bool StepReefslider(ref PlayerSnapshot s,PlayerInputFrame input,SpecialWeaponRuntimeConfig c,float dt,double now)
        {
            var p=c.P; bool burst=false;
            s.Yaw=input.Look.x;s.Pitch=input.Look.y;s.ConsumedJump=input.JumpSequence;
            if(s.SpecialPhase==SpecialPhase.Recovering)
            {
                input.Move=Vector2.zero;input.Swim=false;input.Fire=false;
                _motor.Step(ref s,input,dt,now,true,0,true);
                if(now+1e-6>=s.SpecialUntil){s.SpecialPhase=SpecialPhase.Charging;s.SpecialChargeLockedUntil=now;s.SpecialRideStage=0;}
                return false;
            }
            if(s.Swimming||s.AirHumanOffset!=0)
            {s.Position=PlayerMotorSimulation.HumanPosition(s);s.AirHumanOffset=0;s.Swimming=s.CompactBody=false;s.PaperPose=PaperPose.None;_motor.Restore(s);}
            double age=now-s.SpecialStartedAt;
            if(s.SpecialRideStage==0&&age+1e-6>=p.rideStartup){s.SpecialRideStage=1;s.SpecialPhase=SpecialPhase.Active;}
            if(s.SpecialRideStage==1)
            {
                s.SpecialRideSpeed=Mathf.MoveTowards(s.SpecialRideSpeed,s.Grounded?p.rideSpeed:p.rideAirSpeed,p.rideAcceleration*dt);
                if(age+1e-6>=p.rideStartup+p.rideDuration||(!s.SpecialNeedsRelease&&!input.CancelFire&&input.Fire&&age+1e-6>=p.rideStartup+p.rideBrakeAllowed))
                {s.SpecialRideStage=2;s.SpecialBurstAt=now+p.rideBurstDelay;}
            }
            if(s.SpecialRideStage==2)s.SpecialRideSpeed=Mathf.MoveTowards(s.SpecialRideSpeed,0,p.rideBrake*dt);
            Vector3 before=s.Position;
            s.VerticalSpeed=s.Grounded?-2:s.VerticalSpeed-GameplayConfig.GetHero(s.HeroId).CharacterGravity*dt*p.rideGravityMultiplier;
            if(!_controller.enabled)_controller.enabled=true;
            var flags=_controller.Move((s.SpecialDirection*s.SpecialRideSpeed+Vector3.up*s.VerticalSpeed)*dt);
            s.Position=transform.position;s.Velocity=(s.Position-before)/dt;s.PlanarVelocity=new Vector3(s.Velocity.x,0,s.Velocity.z);s.Grounded=(flags&CollisionFlags.Below)!=0;
            s.Movement=MovementMode.Reefslider;s.Swimming=s.CompactBody=false;s.PaperPose=PaperPose.None;
            if(s.SpecialRideStage<0&&s.Grounded)
            {
                s.SpecialRideStage=0;s.SpecialStartedAt=now;s.SpecialUntil=now+p.duration;
                s.SpecialReadyAt=now+p.rideStartup;s.SpecialChargeLockedUntil=now+p.chargeLock;s.SpecialPoints=0;
            }
            if(IsServer&&s.SpecialRideStage==1)PrototypeMatch.Current.SpecialWeapons.ReefContact(this,s,before,now);
            if(s.SpecialRideStage==1&&(flags&CollisionFlags.Sides)!=0){s.SpecialRideStage=2;s.SpecialBurstAt=now+p.rideBurstDelay;}
            if(s.SpecialRideStage==2&&now+1e-6>=s.SpecialBurstAt)
            {
                s.SpecialAttack++;s.SpecialAttackAt=now;s.SpecialRemaining=0;s.SpecialRideStage=3;s.SpecialPhase=SpecialPhase.Recovering;
                s.SpecialUntil=now+p.rideRecovery;s.SpecialChargeLockedUntil=s.SpecialUntil;burst=true;
            }
            return burst;
        }
    }
}
