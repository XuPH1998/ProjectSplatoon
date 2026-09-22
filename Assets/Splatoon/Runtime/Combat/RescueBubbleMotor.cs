using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>A swept sphere, shared by the authority and owner replay. No rigidbody simulation.</summary>
    public sealed class RescueBubbleMotor : System.IDisposable
    {
        const float Skin = .015f;
        readonly SphereCollider _probe;
        readonly Collider[] _overlaps = new Collider[128];
        public RescueBubbleMotor()
        {
            var go = new GameObject("Rescue bubble collision probe") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            // PhysX needs a live shape for mesh penetration queries. Every gameplay world query ignores triggers.
            _probe = go.AddComponent<SphereCollider>(); _probe.isTrigger = true;
        }
        public void Dispose() { if (_probe != null) HeroViewBinder.Destroy(_probe.gameObject); }
        public Vector3 ResolveOverlap(Vector3 center, float radius)
        {
            _probe.radius = radius;
            for (int pass = 0; pass < 16; pass++)
            {
                int count = Physics.OverlapSphereNonAlloc(center, radius, _overlaps, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    var other = _overlaps[i];
                    if (!Physics.ComputePenetration(_probe, center, Quaternion.identity, other, other.transform.position, other.transform.rotation, out var direction, out float depth)) continue;
                    center += direction * (depth + Skin); changed = true;
                }
                if (!changed)
                {
                    if (count == 0) return center;
                    break; // An unresolved mesh overlap must not be mistaken for a valid placement.
                }
            }
            // Narrow tunnels cannot contain the full sphere. Search upward and then locally for a full-size fit.
            Vector3 origin = center;
            for (int step = 1; step <= 40; step++)
            {
                float distance = step * .25f;
                for (int dir = 0; dir < 9; dir++)
                {
                    float angle = (dir - 1) * Mathf.PI / 4;
                    Vector3 candidate = origin + (dir == 0 ? Vector3.up : new Vector3(Mathf.Cos(angle), .5f, Mathf.Sin(angle)).normalized) * distance;
                    if (!Physics.CheckSphere(candidate, radius, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return candidate;
                }
            }
            return center;
        }
        public void Step(ref PlayerSnapshot s, PlayerInputFrame input, float dt)
        {
            var c = GameplayConfig.Mode;
            var start = s.Position;
            Vector3 center = ResolveOverlap(s.BubbleCenter, s.BubbleRadius);
            s.Yaw = input.Look.x; s.Pitch = input.Look.y; s.BodyYaw = s.Yaw;
            Vector3 planar = Quaternion.Euler(0, s.Yaw, 0) * new Vector3(input.Move.x, 0, input.Move.y) * c.BubbleMoveSpeed;
            s.VerticalSpeed = Mathf.Max(-c.BubbleFallSpeed, s.VerticalSpeed - c.BubbleGravity * dt);
            var displacement = (planar + Vector3.up * s.VerticalSpeed) * dt;
            s.Grounded = false;
            for (int iteration = 0; iteration < 4 && displacement.sqrMagnitude > .00000001f; iteration++)
            {
                float length = displacement.magnitude;
                if (!Physics.SphereCast(center, s.BubbleRadius, displacement / length, out var hit, length + Skin, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
                { center += displacement; break; }
                float advance = Mathf.Clamp(hit.distance - Skin, 0, length);
                center += displacement / length * advance;
                displacement = Vector3.ProjectOnPlane(displacement * (1 - advance / length), hit.normal);
                if (hit.normal.y >= .5f && s.VerticalSpeed <= 0)
                { s.VerticalSpeed = c.BubbleBounceSpeed; s.Grounded = true; displacement.y = Mathf.Max(0, displacement.y); }
                else if (hit.normal.y < -.5f && s.VerticalSpeed > 0) { s.VerticalSpeed = 0; displacement.y = Mathf.Min(0, displacement.y); }
            }
            s.Position = center - Vector3.up * s.BubbleCenterHeight;
            s.Velocity = (s.Position - start) / dt; s.PlanarVelocity = planar;
            s.Movement = MovementMode.Bubble; s.Swimming = s.Firing = false;
            s.ConsumedJump = input.JumpSequence;
        }
    }
}
