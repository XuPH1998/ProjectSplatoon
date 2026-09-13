using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum MovementMode : byte { Human, Air, GroundInk, WallInk, Mantle, Dead }
    public enum WeaponPhase : byte { Idle, Starting, Firing, Ending, Charging, BurstCooldown }

    public static class ResourceSimulation
    {
        public static void Step(ref PlayerSnapshot s, cfg.CharacterConfig c, bool enemyInk, bool triggerHeld, float dt, double now)
        {
            if (s.Health <= 0) return;
            if (now + 1e-8 >= s.InkRecoverAt && (!triggerHeld || s.Swimming) && s.WeaponPhase == WeaponPhase.Idle)
                s.Ink = PrototypeRules.Recover(s.Ink, c.MaxInk, s.Swimming ? c.SwimRecoverInk : c.RecoverInk, dt);
            if (enemyInk)
            {
                if (now >= s.ProtectedUntil) s.Health -= Mathf.Min(Mathf.Max(0, s.Health - c.EnemyInkHealthFloor), c.EnemyInkDamageRate * dt);
                s.LastDamageAt = now;
            }
            else if (now - s.LastDamageAt + 1e-8 >= c.HealthRecoverDelay)
                s.Health = Mathf.Min(c.MaxHealth, s.Health + (s.Swimming ? c.SwimHealthRecoverRate : c.HealthRecoverRate) * dt);
        }
    }

    /// <summary>The same collision/movement rules run on the authority and the predicting owner.</summary>
    public sealed class PlayerMotorSimulation
    {
        public const int WorldMask = ~(1 << 8);
        readonly CharacterController _controller;
        readonly PrototypeArena _arena;
        PrototypeArena Arena => _arena != null ? _arena : PrototypeArena.Current;
        readonly Transform _root;
        readonly float _standingHeight;
        readonly Vector3 _standingCenter;
        readonly Collider[] _overlap = new Collider[32];
        public PlayerMotorSimulation(CharacterController controller, PrototypeArena arena = null)
        { _controller = controller; _arena = arena; _root = controller.transform; _standingHeight = controller.height; _standingCenter = controller.center; }
        public void Restore(PlayerSnapshot s)
        {
            _controller.enabled = false; _root.position = s.Position;
            SetShape(s.Swimming); _controller.enabled = s.Health > 0;
            Physics.SyncTransforms();
        }
        void SetShape(bool ink)
        { _controller.height = ink ? .7f : _standingHeight; _controller.center = ink ? Vector3.up * .35f : _standingCenter; }
        public bool CanStand(Vector3 position)
        {
            float r = Mathf.Max(.05f, _controller.radius - .025f);
            Vector3 center = position + _standingCenter;
            int count = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * (_standingHeight / 2 - r),
                center - Vector3.up * (_standingHeight / 2 - r), r, _overlap, WorldMask, QueryTriggerInteraction.Ignore);
            return count == 0;
        }
        public static bool Query(Vector3 origin, Vector3 direction, float distance, out InkContact contact)
        {
            if (Physics.Raycast(origin, direction, out var hit, distance, WorldMask, QueryTriggerInteraction.Ignore))
            {
                var surface = hit.collider.GetComponentInParent<PaintSurface>();
                if (surface != null && surface.QueryRegion(hit.point, hit.normal, out contact)) return true;
            }
            contact = default; return false;
        }
        public static bool IsEnemy(byte owner, byte team) => owner != 0 && owner != 255 && owner != team;
        public void Step(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, bool wantsFire, float shootMoveSpeed = -1)
        {
            var c = GameplayConfig.Character;
            if (s.Health <= 0) { StepDead(ref s, dt); return; }
            s.Yaw = input.Look.x; s.Pitch = input.Look.y;
            bool jump = input.JumpSequence != s.ConsumedJump; s.ConsumedJump = input.JumpSequence;
            var floor = Arena != null ? Arena.FloorOwner(s.Position) : (byte)255;
            bool grounded = _controller.isGrounded || Physics.Raycast(s.Position + Vector3.up * .08f, Vector3.down, .15f, WorldMask);
            bool canStand = CanStand(s.Position), detached = false;
            if (s.Movement == MovementMode.Mantle)
            {
                if (!input.Swim || wantsFire || jump) s.Movement = MovementMode.Air;
                else
                {
                    float t = Mathf.Clamp01((float)(now - s.MantleStartedAt) / c.MantleSeconds);
                    var apex = new Vector3(s.MantleFrom.x, s.MantleTo.y + .15f, s.MantleFrom.z);
                    Vector3 target = t < .5f ? Vector3.Lerp(s.MantleFrom, apex, t * 2) : Vector3.Lerp(apex, s.MantleTo, (t - .5f) * 2);
                    SetShape(true); _controller.Move(target - _root.position);
                    s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position; s.Swimming = true;
                    s.Grounded = _controller.isGrounded; s.TurnDirection = 0;
                    if (t < 1) return;
                    s.Movement = MovementMode.Air; s.VerticalSpeed = -2;
                    floor = Arena != null ? Arena.FloorOwner(s.Position) : (byte)255;
                    grounded = Physics.Raycast(s.Position + Vector3.up * .08f, Vector3.down, .15f, WorldMask);
                }
            }
            if (s.Movement == MovementMode.WallInk)
            {
                if (input.Swim && !wantsFire && !jump)
                {
                    Vector3 along = Vector3.Cross(s.WallNormal, Vector3.up).normalized;
                    Vector3 delta = (Vector3.up * input.Move.y + along * input.Move.x) * c.WallSwimSpeed * dt;
                    var next = s.Position + delta;
                    bool found = Query(next + Vector3.up * .35f + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out var wall);
                    if (found && wall.Climbable && wall.Owner == s.Team && Vector3.Dot(wall.Normal, s.WallNormal) > .99f)
                    {
                        s.WallSeenAt = now; BindWall(ref s, wall);
                        next += s.WallNormal * (_controller.radius + .015f - Vector3.Dot(next + Vector3.up * .35f - wall.Point, s.WallNormal));
                        SetShape(true); _controller.Move(next - _root.position);
                        s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position;
                        s.Grounded = false; s.Swimming = true; s.TurnDirection = 0;
                        if (input.Move.y > 0) TryMantle(ref s, now);
                        return;
                    }
                    if (!found && input.Move.y > 0 && TryMantle(ref s, now)) return;
                    // A real enemy/unpainted contact has no grace period.
                    if (!found && now - s.WallSeenAt < c.WallGraceSeconds && FriendlySeam(next, s.WallNormal, s.Team, c.WallProbeDistance))
                    {
                        SetShape(true); _controller.Move(delta);
                        s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position; return;
                    }
                }
                s.Movement = MovementMode.Air;
                detached = true;
                s.PlanarVelocity = s.WallNormal * (jump ? c.WallJumpSpeed : .8f);
                s.VerticalSpeed = jump ? c.JumpSpeed : 0;
                if (canStand) s.Swimming = false;
            }
            bool useInk = input.Swim && !wantsFire && floor == s.Team && grounded;
            if (!canStand && s.Swimming) useInk = true;
            s.Swimming = useInk; SetShape(useInk);
            var aim = Quaternion.Euler(0, s.Yaw, 0);
            if (!detached && input.Swim && !wantsFire && !jump && input.Move.y > 0 &&
                Query(s.Position + Vector3.up * .35f, aim * Vector3.forward, c.WallProbeDistance, out var entry) && entry.Climbable && entry.Owner == s.Team)
            {
                BindWall(ref s, entry); s.WallSeenAt = now; s.Movement = MovementMode.WallInk;
                s.Swimming = true; s.Grounded = false; s.VerticalSpeed = 0; s.PlanarVelocity = Vector3.zero; s.Velocity = Vector3.zero;
                SetShape(true); return;
            }
            float speed = useInk ? c.SwimSpeed : wantsFire ? (shootMoveSpeed > 0 ? shootMoveSpeed : c.ShootMoveSpeed) : c.MoveSpeed;
            if (grounded && IsEnemy(floor, s.Team)) speed *= c.EnemyInkMultiplier;
            var desired = aim * new Vector3(input.Move.x, 0, input.Move.y) * speed;
            s.PlanarVelocity = Vector3.MoveTowards(s.PlanarVelocity, desired, (useInk ? c.SwimAcceleration : c.MoveAcceleration) * dt);
            if (grounded && s.VerticalSpeed < 0) s.VerticalSpeed = -2;
            if (jump && grounded) s.VerticalSpeed = c.JumpSpeed;
            s.VerticalSpeed -= c.Gravity * dt;
            _controller.Move((s.PlanarVelocity + Vector3.up * s.VerticalSpeed) * dt);
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position; s.Grounded = _controller.isGrounded;
            if (!s.Grounded && useInk && CanStand(s.Position)) { useInk = false; s.Swimming = false; SetShape(false); }
            s.Movement = useInk ? MovementMode.GroundInk : s.Grounded ? MovementMode.Human : MovementMode.Air;
            if (!s.Grounded && !useInk) s.WallSurfaceId = s.WallRegionId = 0;
        }
        static void BindWall(ref PlayerSnapshot s, InkContact c)
        { s.WallSurfaceId = c.Surface.SurfaceId; s.WallRegionId = c.Region.Id; s.WallNormal = c.Normal; s.WallPoint = c.Point; }
        static bool FriendlySeam(Vector3 position, Vector3 normal, byte team, float distance)
        {
            Vector3 origin = position + Vector3.up * .35f + normal * .08f;
            foreach (var along in new[] { Vector3.Cross(normal, Vector3.up).normalized, Vector3.up })
            {
                if (!Query(origin + along * .045f, -normal, distance + .08f, out var a) ||
                    !Query(origin - along * .045f, -normal, distance + .08f, out var b)) continue;
                if (a.Climbable && b.Climbable && a.Owner == team && b.Owner == team &&
                    (a.Surface != b.Surface || a.Region.Id != b.Region.Id) && Vector3.Dot(a.Normal, normal) > .999f && Vector3.Dot(b.Normal, normal) > .999f && Mathf.Abs(Vector3.Dot(a.Point - b.Point, normal)) < .01f) return true;
            }
            return false;
        }
        bool TryMantle(ref PlayerSnapshot s, double now)
        {
            Vector3 probe = s.Position + Vector3.up * 1.0f - s.WallNormal * .6f;
            if (!Physics.Raycast(probe, Vector3.down, out var hit, 1.05f, WorldMask, QueryTriggerInteraction.Ignore) || hit.normal.y < .65f) return false;
            if (hit.point.y < s.Position.y + .1f || hit.point.y > s.Position.y + .95f) return false;
            Vector3 target = hit.point + Vector3.up * .025f;
            if (!CanStand(target) || Mathf.Abs(target.x) > 15.8f || Mathf.Abs(target.z) > 31.8f) return false;
            var apex = new Vector3(s.Position.x, target.y + .15f, s.Position.z);
            if (!InkPathClear(s.Position, apex) || !InkPathClear(apex, target)) return false;
            s.MantleFrom = s.Position; s.MantleTo = target; s.MantleStartedAt = now;
            s.Movement = MovementMode.Mantle; s.Swimming = true; s.Velocity = Vector3.zero; s.TurnDirection = 0;
            return true;
        }
        bool InkPathClear(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; float radius = Mathf.Max(.05f, _controller.radius - .04f);
            return !Physics.CapsuleCast(from + Vector3.up * radius, from + Vector3.up * (.7f-radius), radius,
                delta.normalized, delta.magnitude, WorldMask, QueryTriggerInteraction.Ignore);
        }
        void StepDead(ref PlayerSnapshot s, float dt)
        {
            _controller.enabled = false; s.Movement = MovementMode.Dead; s.Swimming = s.Firing = false; s.WeaponPhase = WeaponPhase.Idle; s.TurnDirection = 0;
            s.VerticalSpeed -= GameplayConfig.Character.Gravity * dt;
            Vector3 direction = s.VerticalSpeed > 0 ? Vector3.up : Vector3.down;
            float distance = Mathf.Abs(s.VerticalSpeed * dt); s.Grounded = false;
            Vector3 origin = s.Position + Vector3.up * .3f;
            if (Physics.SphereCast(origin, .25f, direction, out var hit, distance + .05f, WorldMask, QueryTriggerInteraction.Ignore))
            { distance = Mathf.Max(0, hit.distance - .05f); s.VerticalSpeed = 0; s.Grounded = direction.y < 0; }
            s.Position += direction * distance; _root.position = s.Position; s.Velocity = direction * (distance / dt);
        }
    }
}
