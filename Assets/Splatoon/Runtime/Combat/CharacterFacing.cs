using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    /// <summary>Server-time facing. Animation root motion never moves the authority collider.</summary>
    public static class CharacterFacing
    {
        public static void Step(ref PlayerSnapshot state, CharacterPresentationProfile profile, float dt, double now)
        {
            if (state.Health <= 0) { state.TurnDirection = 0; return; }
            float speed = new Vector2(state.Velocity.x, state.Velocity.z).magnitude;
            if (!state.Grounded || state.Swimming || speed >= profile.StationarySpeed)
            {
                state.TurnDirection = 0;
                state.BodyYaw = Mathf.MoveTowardsAngle(state.BodyYaw, state.Yaw, profile.MovingTurnSpeed * dt);
                return;
            }
            if (state.TurnDirection != 0)
            {
                float t = (float)(now - state.TurnStartedAt) / profile.TurnDuration(state.TurnDirection);
                state.BodyYaw = Mathf.Repeat(state.TurnStartYaw + state.TurnDirection * 90 * profile.TurnProgress(state.TurnDirection, t), 360);
                if (t < 1) return;
                state.TurnDirection = 0;
            }
            float delta = Mathf.DeltaAngle(state.BodyYaw, state.Yaw);
            if (Mathf.Abs(delta) <= profile.TurnThreshold) return;
            state.TurnDirection = (sbyte)(delta < 0 ? -1 : 1);
            state.TurnStartYaw = state.BodyYaw;
            state.TurnStartedAt = now;
        }

        // Incoming velocity points towards the victim. Back impacts push forward;
        // frontal, lateral and unknown impacts use the backward fall.
        public static byte DeathDirection(float bodyYaw, Vector3 incomingVelocity)
        {
            incomingVelocity.y = 0;
            if (incomingVelocity.sqrMagnitude < .0001f) return 0;
            var forward = Quaternion.Euler(0, bodyYaw, 0) * Vector3.forward;
            return Vector3.Dot(forward, incomingVelocity.normalized) > .5f ? (byte)1 : (byte)0;
        }
    }
}
