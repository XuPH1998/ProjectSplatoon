using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class RescueBubbleRules
    {
        public static bool Expired(PlayerSnapshot state, double now) => state.IsBubble && now >= state.BubbleUntil;
        public static float Radius(Bounds standingBounds, float centerHeight, float padding)
        {
            Vector3 extent = new(Mathf.Max(Mathf.Abs(standingBounds.min.x), Mathf.Abs(standingBounds.max.x)),
                Mathf.Max(Mathf.Abs(standingBounds.min.y - centerHeight), Mathf.Abs(standingBounds.max.y - centerHeight)),
                Mathf.Max(Mathf.Abs(standingBounds.min.z), Mathf.Abs(standingBounds.max.z)));
            return extent.magnitude + Mathf.Max(.2f, padding);
        }
        public static bool Eligible(PlayerSnapshot actor, PlayerSnapshot target, uint targetLife, double now, float range, Vector3 actorPoint)
            => actor.IsAlive && target.IsBubble && target.Revision == targetLife && now < target.BubbleUntil &&
                Vector3.Distance(actorPoint, target.BubbleCenter) <= target.BubbleRadius + range;
    }
}
