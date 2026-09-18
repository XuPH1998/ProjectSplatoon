using UnityEngine;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public static class FoamSupport
    {
        public static bool Sample(FoamPatch patch,PlayerSnapshot state,PaperBodyProfile profile,out Vector3 point,out Vector3 normal,out byte owner)
        {
            bool center=patch.Sample(state.Position,out point,out normal,out owner);
            if(!center)
            {
                if(!state.ShowsSwimBody||profile==null||!patch.Contains(state.Position,profile.Size.magnitude*.5f))return false;
                point=patch.BasePoint(state.Position);normal=patch.Normal;owner=0;
            }
            if(!state.ShowsSwimBody||profile==null)return true;
            if(!state.Grounded)normal=Vector3.up;
            var rotation=PaperPoseSimulation.GroundRotation(normal,state.Yaw);
            Vector3 right=rotation*Vector3.right,forward=rotation*Vector3.up;
            float lift=float.NegativeInfinity;
            bool found=patch.PaperSupport(point,normal,right,forward,profile.Size,ref lift,ref owner);
            foreach(var neighbour in patch.Neighbours)found|=neighbour.PaperSupport(point,normal,right,forward,profile.Size,ref lift,ref owner);
            if(found)point.y+=lift;return found;
        }
    }
}
