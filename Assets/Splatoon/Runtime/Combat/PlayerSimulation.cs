using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum MovementMode : byte { Human, Air, GroundInk, WallInk, Mantle, Dead }
    public enum SwimSurface : byte { None, Friendly, Neutral }
    public enum WeaponPhase : byte { Idle, Starting, Firing, Ending, Charging, BurstCooldown }

    public static class ResourceSimulation
    {
        public static void Step(ref PlayerSnapshot s, cfg.HeroConfig c, bool enemyInk, bool triggerHeld, float dt, double now)
        {
            if (s.Health <= 0) return;
            bool inkRecovery = s.HasInkRecovery && !enemyInk;
            if (now + 1e-8 >= s.InkRecoverAt && (!triggerHeld || s.Swimming) && s.WeaponPhase == WeaponPhase.Idle)
                s.Ink = PrototypeRules.Recover(s.Ink, c.MaxInk, inkRecovery ? c.SwimRecoverInk : c.RecoverInk, dt);
            if (enemyInk)
            {
                if (now >= s.ProtectedUntil) s.Health -= Mathf.Min(Mathf.Max(0, s.Health - c.EnemyInkHealthFloor), c.EnemyInkDamageRate * dt);
                s.LastDamageAt = now;
            }
            else if (now - s.LastDamageAt + 1e-8 >= c.HealthRecoverDelay)
                s.Health = Mathf.Min(c.MaxHealth, s.Health + (inkRecovery ? c.SwimHealthRecoverRate : c.HealthRecoverRate) * dt);
        }
    }

    /// <summary>The same collision/movement rules run on the authority and the predicting owner.</summary>
    public sealed class PlayerMotorSimulation
    {
        public const int WorldMask = ~((1 << 8) | (1 << SwimBody.HitProxyLayer));
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
            SetShape(s.Swimming || s.CompactBody); _controller.enabled = s.Health > 0;
            _root.GetComponent<PrototypePlayer>()?.SwimBody?.ApplyCollision(s);
            Physics.SyncTransforms();
        }
        void SetShape(bool ink)
        {
            float height = ink ? .7f : _standingHeight;
            Vector3 center = ink ? Vector3.up * .35f : _standingCenter;
            if (_controller.height == height && _controller.center == center) return;
            // Avoid rewriting unchanged controller geometry every simulation tick.
            _controller.height = height; _controller.center = center;
        }
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
        public static bool CanClimb(InkContact contact, byte team) => contact.Climbable && (contact.Owner == 0 || contact.Owner == team);
        static float WallSpeed(SwimSurface source, cfg.HeroConfig hero) => source == SwimSurface.Neutral ? hero.NeutralSwimSpeed : hero.WallSwimSpeed;
        public void Step(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, bool wantsFire, float shootMoveSpeed = -1, bool shootingMovement = false)
        {
            var previous = s;
            StepMovement(ref s, input, dt, now, wantsFire, shootMoveSpeed, shootingMovement);
            var profile = _root.GetComponent<PrototypePlayer>()?.SwimBody?.Profile;
            // The capsule alone decides traversal. Paper geometry follows the
            // completed simulation and never rolls it back at a surface edge.
            PaperPoseSimulation.Resolve(ref s, previous, profile, now);
            PaperAnimation.Step(ref s, previous, dt, profile);
        }

        void StepMovement(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, bool wantsFire, float shootMoveSpeed, bool shootingMovement)
        {
            var c = GameplayConfig.GetHero(s.HeroId);
            if (s.Health <= 0) { StepDead(ref s, dt); return; }
            s.Yaw = input.Look.x; s.Pitch = input.Look.y;
            bool jump = input.JumpSequence != s.ConsumedJump; s.ConsumedJump = input.JumpSequence;
            bool groundContact = PrototypeArena.TryGetGround(s.Position, out byte floor, _controller.slopeLimit);
            bool grounded = s.VerticalSpeed <= 0 && s.Grounded && (_controller.isGrounded || groundContact);
            // A flight retains its source across human/paper switches. A fresh
            // airborne spawn (or hero change) defaults to neutral swimming.
            if (s.Movement == MovementMode.WallInk || s.Movement == MovementMode.Mantle)
                s.AirSwimSource = s.SwimSource;
            else if (!grounded && s.AirSwimSource == SwimSurface.None)
                s.AirSwimSource = s.Grounded && s.Swimming && s.SwimSource == SwimSurface.Friendly ? SwimSurface.Friendly : SwimSurface.Neutral;
            bool wasCompact = s.Swimming || s.CompactBody;
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
                    s.CompactBody = false;
                    s.Grounded = _controller.isGrounded; s.TurnDirection = 0;
                    if (t < 1) return;
                    s.Movement = MovementMode.Air; s.VerticalSpeed = -2;
                    grounded = PrototypeArena.TryGetGround(s.Position, out floor, _controller.slopeLimit);
                }
            }
            if (s.Movement == MovementMode.WallInk)
            {
                bool currentFound = Query(s.Position + Vector3.up * .35f + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out var currentWall);
                bool blocked = currentFound && !CanClimb(currentWall, s.Team);
                if (currentFound && !blocked) BindWall(ref s, currentWall);
                if (!blocked && input.Swim && !wantsFire && !jump)
                {
                    Vector3 along = Vector3.Cross(s.WallNormal, Vector3.up).normalized;
                    Vector3 direction = Vector3.up * input.Move.y + along * input.Move.x;
                    float wallSpeed = WallSpeed(s.SwimSource, c);
                    Vector3 delta = direction * wallSpeed * dt;
                    var next = s.Position + delta;
                    bool found = Query(next + Vector3.up * .35f + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out var wall);
                    if (found && CanClimb(wall, s.Team) && wall.Owner == 0 && wallSpeed > c.NeutralSwimSpeed)
                    {
                        delta = direction * c.NeutralSwimSpeed * dt; next = s.Position + delta;
                        found = Query(next + Vector3.up * .35f + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out wall);
                    }
                    if (found && CanClimb(wall, s.Team) && Vector3.Dot(wall.Normal, s.WallNormal) > .99f)
                    {
                        s.WallSeenAt = now; BindWall(ref s, wall);
                        next += s.WallNormal * (_controller.radius + .015f - Vector3.Dot(next + Vector3.up * .35f - wall.Point, s.WallNormal));
                        MoveOnWall(ref s, next - _root.position, input, dt, wantsFire);
                        if (s.Movement == MovementMode.WallInk && input.Move.y > 0) TryMantle(ref s, now);
                        return;
                    }
                    if (!found && input.Move.y > 0 && TryMantle(ref s, now)) return;
                    // Grace only bridges a geometric seam; enemy or blocked contacts detach immediately.
                    if (!found && now - s.WallSeenAt < c.WallGraceSeconds && ClimbableSeam(next, s.WallNormal, s.Team, c.WallProbeDistance, out var seamSource))
                    {
                        s.SwimSource = s.AirSwimSource = seamSource;
                        delta = direction * WallSpeed(seamSource, c) * dt;
                        MoveOnWall(ref s, delta, input, dt, wantsFire); return;
                    }
                }
                s.Movement = MovementMode.Air;
                detached = true;
                s.PlanarVelocity = s.WallNormal * (jump ? c.WallJumpSpeed : .8f);
                s.VerticalSpeed = jump ? c.JumpSpeed : 0;
                // Jumping preserves the ink form; a failed wall contact simply detaches.
                s.AirSwimSource = s.SwimSource;
                grounded = false;
            }
            bool useInk = input.Swim && !wantsFire && (!grounded || !IsEnemy(floor, s.Team));
            if (useInk) s.SwimSource = grounded ? (floor == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral) : s.AirSwimSource;
            if (!useInk) s.SwimSource = SwimSurface.None;
            if (grounded) s.AirSwimSource = useInk ? s.SwimSource : SwimSurface.Neutral;
            s.Swimming = useInk;
            s.CompactBody = !useInk && wasCompact && !canStand;
            SetShape(useInk || s.CompactBody);
            var aim = Quaternion.Euler(0, s.Yaw, 0);
            if (!detached && (grounded || useInk) && !(grounded && IsEnemy(floor, s.Team)) && input.Swim && !wantsFire && !jump && input.Move.y > 0 &&
                Query(s.Position + Vector3.up * .35f, aim * Vector3.forward, c.WallProbeDistance, out var entry) && CanClimb(entry, s.Team) &&
                TryAttach(ref s, entry, dt))
            {
                s.WallSeenAt = now; s.Movement = MovementMode.WallInk;
                s.Swimming = true; s.Grounded = false; s.VerticalSpeed = 0; s.PlanarVelocity = Vector3.zero;
                s.CompactBody = false;
                SetShape(true); return;
            }
            float speed = useInk ? (s.SwimSource == SwimSurface.Neutral ? c.NeutralSwimSpeed : c.SwimSpeed)
                : wantsFire || shootingMovement ? (shootMoveSpeed > 0 ? shootMoveSpeed : c.ShootMoveSpeed) : c.MoveSpeed;
            if (grounded && IsEnemy(floor, s.Team)) speed *= c.EnemyInkMultiplier;
            var desired = aim * new Vector3(input.Move.x, 0, input.Move.y) * speed;
            s.PlanarVelocity = Vector3.MoveTowards(s.PlanarVelocity, desired, (useInk ? c.SwimAcceleration : c.MoveAcceleration) * dt);
            if (grounded && s.VerticalSpeed < 0) s.VerticalSpeed = -2;
            if (jump && grounded) s.VerticalSpeed = c.JumpSpeed;
            s.VerticalSpeed -= c.CharacterGravity * dt;
            var collisions = _controller.Move((s.PlanarVelocity + Vector3.up * s.VerticalSpeed) * dt);
            if ((collisions & CollisionFlags.Above) != 0 && s.VerticalSpeed > 0) s.VerticalSpeed = 0;
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position; s.Grounded = _controller.isGrounded;
            // Resolve the destination before resources/presentation, including landing and fresh enemy paint.
            if (s.Grounded)
            {
                ResolveGround(ref s, input, wantsFire);
                useInk = s.Swimming;
            }
            s.Movement = !s.Grounded ? MovementMode.Air : useInk ? MovementMode.GroundInk : MovementMode.Human;
            if (!s.Grounded && !useInk) s.WallSurfaceId = s.WallRegionId = 0;
        }
        bool TryAttach(ref PlayerSnapshot s, InkContact entry, float dt)
        {
            float gap = Vector3.Dot(_root.position + Vector3.up * .35f - entry.Point, entry.Normal);
            SetShape(true);
            _controller.Move(entry.Normal * (_controller.radius + .015f - gap));
            // An obstacle can stop the snap. Bind only after the capsule reaches
            // the wall, and record the real contact and position for prediction.
            gap = Vector3.Dot(_root.position + Vector3.up * .35f - entry.Point, entry.Normal);
            if (gap > _controller.radius + .065f || !Query(_root.position + Vector3.up * .35f + entry.Normal * .08f,
                -entry.Normal, gap + .1f, out var actual) || !CanClimb(actual, s.Team) ||
                Vector3.Dot(actual.Normal, entry.Normal) < .99f) return false;
            BindWall(ref s, actual);
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position;
            return true;
        }
        void MoveOnWall(ref PlayerSnapshot s, Vector3 delta, PlayerInputFrame input, float dt, bool wantsFire)
        {
            SetShape(true); var collisions = _controller.Move(delta);
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position;
            s.Swimming = true; s.TurnDirection = 0; s.VerticalSpeed = 0; s.PlanarVelocity = Vector3.zero;
            s.CompactBody = false;
            s.Grounded = input.Move.y < 0 && (collisions & CollisionFlags.Below) != 0;
            if (s.Grounded) ResolveGround(ref s, input, wantsFire);
        }
        void ResolveGround(ref PlayerSnapshot s, PlayerInputFrame input, bool wantsFire)
        {
            PrototypeArena.TryGetGround(s.Position, out byte floor, _controller.slopeLimit);
            bool allowed = input.Swim && !wantsFire && !IsEnemy(floor, s.Team);
            s.CompactBody = !allowed && (s.Swimming || s.CompactBody) && !CanStand(s.Position);
            s.Swimming = allowed;
            s.SwimSource = allowed ? (floor == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral) : SwimSurface.None;
            s.Movement = allowed ? MovementMode.GroundInk : MovementMode.Human;
            s.AirSwimSource = SwimSurface.None;
            s.WallSurfaceId = s.WallRegionId = 0; s.VerticalSpeed = -2;
            SetShape(allowed || s.CompactBody);
        }
        static void BindWall(ref PlayerSnapshot s, InkContact c)
        {
            s.WallSurfaceId = c.Surface.SurfaceId; s.WallRegionId = c.Region.Id; s.WallNormal = c.Normal; s.WallPoint = c.Point;
            s.SwimSource = s.AirSwimSource = c.Owner == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral;
        }
        static bool ClimbableSeam(Vector3 position, Vector3 normal, byte team, float distance, out SwimSurface source)
        {
            source = SwimSurface.None;
            Vector3 origin = position + Vector3.up * .35f + normal * .08f;
            foreach (var along in new[] { Vector3.Cross(normal, Vector3.up).normalized, Vector3.up })
            {
                // Cover both sides of the authored 8 cm seam even when the slower
                // neutral step stops near one edge instead of its centre.
                if (!Query(origin + along * .09f, -normal, distance + .08f, out var a) ||
                    !Query(origin - along * .09f, -normal, distance + .08f, out var b)) continue;
                if (CanClimb(a, team) && CanClimb(b, team) &&
                    (a.Surface != b.Surface || a.Region.Id != b.Region.Id) && Vector3.Dot(a.Normal, normal) > .999f && Vector3.Dot(b.Normal, normal) > .999f && Mathf.Abs(Vector3.Dot(a.Point - b.Point, normal)) < .01f)
                { source = a.Owner == team && b.Owner == team ? SwimSurface.Friendly : SwimSurface.Neutral; return true; }
            }
            return false;
        }
        bool TryMantle(ref PlayerSnapshot s, double now)
        {
            Vector3 probe = s.Position + Vector3.up - s.WallNormal * .6f;
            if (!Physics.Raycast(probe, Vector3.down, out var hit, 1.05f, WorldMask, QueryTriggerInteraction.Ignore) || hit.normal.y < .65f) return false;
            if (hit.point.y < s.Position.y + .1f || hit.point.y > s.Position.y + .95f) return false;
            Vector3 target = hit.point + Vector3.up * .025f;
            if (!CanStand(target) || Mathf.Abs(target.x) > 15.8f || Mathf.Abs(target.z) > 31.8f) return false;
            var apex = new Vector3(s.Position.x, target.y + .15f, s.Position.z);
            if (!InkPathClear(s.Position, apex) || !InkPathClear(apex, target)) return false;
            s.MantleFrom = s.Position; s.MantleTo = target; s.MantleStartedAt = now;
            s.Movement = MovementMode.Mantle; s.Swimming = true; s.Velocity = Vector3.zero; s.TurnDirection = 0;
            s.CompactBody = false;
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
            s.SwimSource = s.AirSwimSource = SwimSurface.None; s.CompactBody = false;
            s.VerticalSpeed -= GameplayConfig.GetHero(s.HeroId).CharacterGravity * dt;
            Vector3 direction = s.VerticalSpeed > 0 ? Vector3.up : Vector3.down;
            float distance = Mathf.Abs(s.VerticalSpeed * dt); s.Grounded = false;
            Vector3 origin = s.Position + Vector3.up * .3f;
            if (Physics.SphereCast(origin, .25f, direction, out var hit, distance + .05f, WorldMask, QueryTriggerInteraction.Ignore))
            { distance = Mathf.Max(0, hit.distance - .05f); s.VerticalSpeed = 0; s.Grounded = direction.y < 0; }
            s.Position += direction * distance; _root.position = s.Position; s.Velocity = direction * (distance / dt);
        }
    }
}
