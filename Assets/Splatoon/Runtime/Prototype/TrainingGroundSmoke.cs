using System;
using System.Linq;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Networking;
namespace Splatoon.Prototype
{
    // Explicit development diagnostics; never enabled by normal game input.
    public static class TrainingGroundSmoke
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        static bool _geometry;
        static uint _paintedRound = uint.MaxValue;
        static PrototypeMatch _match;
        public static void Tick(PrototypeMatch match)
        {
            if (!match.IsServer) return;
            if (_match != match) { _match = match; _paintedRound = uint.MaxValue; }
            if (!_geometry)
            {
                ProbeTraversal();
                match.Players[0].ValidateMapSwimming();
                _geometry=true;
                Debug.Log("[MAP-SMOKE] Traversal PASS ramps/platform/bridge/underpass/camera");
            }
            if (_paintedRound != match.State.Value.Round)
            {
                _paintedRound=match.State.Value.Round;
                foreach(var surface in match.Arena.Surfaces.Values.Where(s=>s.Scores))
                {
                    var grid=surface.Ownership;
                    // Paint both teams on every walkable surface, including both bridge layers.
                    for(int side=-1;side<=1;side+=2)
                    {
                        var local=new Vector3(side*Mathf.Min(1,surface.WalkableSize.x*.2f),0,.7f);
                        match.Paint(surface,surface.transform.TransformPoint(local),surface.transform.up,1.5f,(byte)(side<0?1:2),.5f,1);
                    }
                }
                if(match.Arena.PinkArea<=0||match.Arena.BlueArea<=0)throw new InvalidOperationException("多层涂地未增加面积");
                Debug.Log("[MAP-SMOKE] Painted all walkable layers round="+_paintedRound);
            }
            if(match.State.Value.PlayerCount>=2 && match.State.Value.Phase==MatchPhase.Practice) match.StartRound();
        }
        static void Require(bool success,string reason){if(!success)throw new InvalidOperationException("[MAP-SMOKE] "+reason);}
        static void ProbeTraversal()
        {
            var go=new GameObject("Traversal probe");go.layer=8;
            var cc=go.AddComponent<CharacterController>();cc.radius=.35f;cc.height=1.8f;cc.center=Vector3.up*.9f;cc.slopeLimit=45;cc.stepOffset=.3f;cc.skinWidth=.03f;
            try
            {
                foreach(int side in new[]{-1,1})
                {
                    cc.enabled=false;go.transform.position=new Vector3(side*11,.1f,-12);cc.enabled=true;Physics.SyncTransforms();
                    Walk(cc,new Vector3(side*11,3,0));
                    Require(Mathf.Abs(go.transform.position.y-3)<.2f,"南坡道登台失败："+go.transform.position);
                    Walk(cc,new Vector3(-side*11,3,0));
                    Require(Mathf.Abs(go.transform.position.y-3)<.2f,"横桥通行失败："+go.transform.position);
                    Walk(cc,new Vector3(-side*11,0,12));
                    Require(go.transform.position.y<.2f,"北坡道下行失败："+go.transform.position);
                }
                cc.enabled=false;go.transform.position=new Vector3(0,.1f,-4);cc.enabled=true;Physics.SyncTransforms();
                Walk(cc,new Vector3(0,0,4));Require(go.transform.position.y<.2f,"桥下通行失败");
                var camera=PrototypePlayer.CameraPosition(new Vector3(0,1.5f,0),Quaternion.Euler(35,0,0));
                Require(camera.y<2.6f,"桥下相机穿过桥面");
            }
            finally{UnityEngine.Object.Destroy(go);}
        }
        static void Walk(CharacterController cc,Vector3 target)
        {
            for(int i=0;i<1000;i++)
            {
                Vector3 delta=target-cc.transform.position;delta.y=0;
                if(delta.magnitude<.08f) {for(int j=0;j<5;j++)cc.Move(Vector3.down*.06f);return;}
                cc.Move(Vector3.ClampMagnitude(delta,.06f)+Vector3.down*.035f);
            }
            throw new InvalidOperationException("[MAP-SMOKE] 路线阻塞："+cc.transform.position+" → "+target);
        }
#endif
    }
}
