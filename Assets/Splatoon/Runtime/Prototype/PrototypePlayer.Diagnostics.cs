#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Networking;
using Splatoon.Combat;
using Unity.Netcode;
namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        public bool DiagnosticOwnershipVerified { get; private set; }
        [Rpc(SendTo.Server)]
        public void VerifyPaintDiagnosticRpc(uint hash)
        {
            if (!ShooterMovementSmoke.Active) return;
            DiagnosticOwnershipVerified = hash == PrototypeArena.Current.OwnershipHash();
            PaintVerifiedDiagnosticRpc(DiagnosticOwnershipVerified);
        }
        [Rpc(SendTo.Owner)] void PaintVerifiedDiagnosticRpc(bool verified) => DiagnosticOwnershipVerified = verified;
        internal void ValidateMapSwimming()
        {
            if (!IsServer) return;
            var original=Snapshot.Value;
            try
            {
                foreach(var position in new[]{new Vector3(1,.08f,1),new Vector3(1,3.08f,1),new Vector3(10.5f,1.58f,-8)})
                {
                    var s=original;s.Position=position;s.Ink=40;s.Health=100;s.InkRecoverAt=0;s.WeaponPhase=WeaponPhase.Idle;s.Swimming=false;
                    _motor.Restore(s);Physics.SyncTransforms();
                    if(!Physics.Raycast(position+Vector3.up*.2f,Vector3.down,out var hit,.55f,PlayerMotorSimulation.WorldMask))throw new InvalidOperationException("诊断角色未落地");
                    var surface=hit.collider.GetComponent<PaintSurface>();
                    PrototypeMatch.Current.Paint(surface,hit.point,hit.normal,1.5f,s.Team,.5f,1);
                    var input=new PlayerInputFrame{Swim=true,Look=Vector2.zero,JumpSequence=s.ConsumedJump,FireSequence=s.ConsumedFire};
                    for(int i=0;i<12;i++)Step(ref s,input,1f/60,NetworkManager.ServerTime.Time+i/60.0,MatchPhase.Practice);
                    if(!s.Swimming||s.Ink<=40)throw new InvalidOperationException("实际角色潜墨或回墨失败："+surface.name);
                }
            }
            finally { _motor.Restore(original); }
        }
        internal void DiagnosticPlace(Vector3 position,float yaw)
        {
            if(!IsServer)return;
            Respawn();var s=Snapshot.Value;s.Position=position;s.Yaw=s.BodyYaw=s.TurnStartYaw=yaw;s.Pitch=0;s.ProtectedUntil=0;
            _motor.Restore(s);Snapshot.Value=s;
            if(IsOwner)_look=new Vector2(yaw,0);
        }
    }
}
#endif
