using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Combat
{
    public enum SubEntityPhase : byte { Flying, Grounded, Seeking, Transforming, Warning, Active, Line, Droplet, Spray }
    public struct SubEntityState : INetworkSerializable
    {
        public uint Id, Round, Life, HeroRevision, ConfigRevision, Action, ParentId, Version;
        public ulong Owner;
        public int Hero, SubWeaponId;
        public byte Team;
        public SubWeaponType Type;
        public SubEntityPhase Phase;
        public Vector3 Position, Velocity, Normal, End;
        public float Charge, Health, Yaw;
        public int Explosions;
        public double Born, Changed, Expires, SampledAt, FuseAt, NextActionAt, GroundAge;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Life); s.SerializeValue(ref HeroRevision);
            s.SerializeValue(ref ConfigRevision); s.SerializeValue(ref Action); s.SerializeValue(ref Owner); s.SerializeValue(ref Hero); s.SerializeValue(ref SubWeaponId);
            s.SerializeValue(ref Team); s.SerializeValue(ref Type); s.SerializeValue(ref Phase);
            s.SerializeValue(ref Position); s.SerializeValue(ref Velocity); s.SerializeValue(ref Normal); s.SerializeValue(ref End);
            s.SerializeValue(ref Charge); s.SerializeValue(ref Health); s.SerializeValue(ref Yaw); s.SerializeValue(ref Explosions);
            s.SerializeValue(ref Born); s.SerializeValue(ref Changed); s.SerializeValue(ref Expires);
            s.SerializeValue(ref ParentId); s.SerializeValue(ref Version); s.SerializeValue(ref SampledAt);
            s.SerializeValue(ref FuseAt); s.SerializeValue(ref NextActionAt); s.SerializeValue(ref GroundAge);
        }
    }
    public enum SubEffectKind : byte { Explosion, Activation, Destroyed, Hit }
    public enum SubLifecycleKind : byte { Spawn, Motion, Phase, Remove }
    public struct SubLifecycleEvent : INetworkSerializable
    {
        public uint Sequence;
        public SubLifecycleKind Kind;
        public SubEntityState State;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Sequence); s.SerializeValue(ref Kind); s.SerializeValue(ref State); }
    }
    public struct SubEffectEvent : INetworkSerializable
    {
        public uint Sequence, Round, EntityId, Action;
        public SubEffectKind Kind;
        public Vector3 Normal;
        public double At;
        public Vector3 Position;
        public float Radius;
        public byte Team;
        public int Hero, SubWeaponId;
        public uint ConfigRevision;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref EntityId); s.SerializeValue(ref Action); s.SerializeValue(ref Kind); s.SerializeValue(ref Normal); s.SerializeValue(ref At); s.SerializeValue(ref Sequence); s.SerializeValue(ref Round); s.SerializeValue(ref Position); s.SerializeValue(ref Radius); s.SerializeValue(ref Team); s.SerializeValue(ref Hero); s.SerializeValue(ref SubWeaponId); s.SerializeValue(ref ConfigRevision); }
    }

    /// <summary>Host owns all entities, damage, paint and status. Presentation never advances gameplay.</summary>
    public sealed class SubWeaponService
    {
        public sealed class Entity
        {
            public SubEntityState State;
            public readonly SubWeaponRuntimeConfig Config;
            public bool Removed;
            public SubEntityPhase PublishedPhase;
            public double Fuse, NextAction, GroundAge;
            public Vector3 LastPaint, LocalPosition, LocalNormal;
            public Transform Attachment;
            public bool Attached;
            public ulong? Target;
            public float Travelled;
            public SubWeaponTarget HitTarget;
            public readonly Dictionary<ulong, double> ContactTimes = new();
            public Entity(SubEntityState state, SubWeaponRuntimeConfig config) { State = state; Config = config; LastPaint = state.Position; }
        }
        readonly List<Entity> _entities = new(128);
        readonly TpsAimSolver _aim = new();
        static readonly TpsAimSolver LaunchAim = new();
        readonly List<SubEntityState> _capture = new(128);
        readonly List<Entity> _blastTargets = new();
        readonly List<(int surface,Vector3 normal,float plane)> _painted = new();
        uint _id, _effect, _lifecycle;
        double _now;
        public uint Watermark => _lifecycle;
        public readonly List<SubLifecycleEvent> Lifecycle = new(128);
        void Stamp(Entity e)
        { e.State.SampledAt = _now; e.State.FuseAt = e.Fuse; e.State.NextActionAt = e.NextAction; e.State.GroundAge = e.GroundAge; }
        void Publish(Entity e, SubLifecycleKind kind)
        {
            Stamp(e); e.State.Version = ++_lifecycle; e.PublishedPhase = e.State.Phase;
            Lifecycle.Add(new SubLifecycleEvent { Sequence = _lifecycle, Kind = kind, State = e.State });
        }
        public readonly List<SubEffectEvent> Effects = new(32);
        public IReadOnlyList<Entity> Entities => _entities;
        public int Count(ulong owner, SubWeaponType type)
        { int n = 0; foreach (var e in _entities) if (!e.Removed && e.State.Owner == owner && e.State.Type == type && e.State.Phase != SubEntityPhase.Droplet && e.State.Phase != SubEntityPhase.Line && e.State.Phase != SubEntityPhase.Spray) n++; return n; }
        public static bool IsPersistent(SubWeaponType type) => type == SubWeaponType.InkMine || type == SubWeaponType.Sprinkler || type == SubWeaponType.SplashWall;
        public SubWeaponFailure CanUse(ulong owner, PlayerSnapshot s, SubWeaponRuntimeConfig c)
        {
            if (c.Type == SubWeaponType.Torpedo && Count(owner, c.Type) >= c.Torpedo.maxActive) return SubWeaponFailure.ActiveLimit;
            if (c.Type == SubWeaponType.InkMine && (!s.Grounded || !TryMineGround(s, out _))) return SubWeaponFailure.InvalidGround;
            return SubWeaponFailure.None;
        }
        public static bool TryMineGround(PlayerSnapshot s, out RaycastHit hit)
        {
            if (!Physics.Raycast(PlayerMotorSimulation.HumanPosition(s) + Vector3.up * .3f, Vector3.down, out hit, .8f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) || hit.normal.y < .65f) return false;
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            return surface != null && surface.QueryRegion(hit.point, hit.normal, out var ink) && !PlayerMotorSimulation.IsEnemy(ink.Owner, s.Team);
        }
        public static void LaunchGeometry(PrototypePlayer player, PlayerSnapshot s, SubWeaponRuntimeConfig c, float charge, out Vector3 position, out Vector3 velocity)
        {
            var aim = LaunchAim.Resolve(player, s, 0);
            position = s.Position + Vector3.up * (GameplayConfig.GetHero(s.HeroId).StandingHeight * .75f);
            var direction = (aim.AimPoint - position).normalized;
            var muzzle = position + direction * .45f;
            if (!Physics.Linecast(position, muzzle, out _, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) position = muzzle;
            velocity = direction * c.Flight.speed + Vector3.up * c.Flight.lift;
            if (c.Type == SubWeaponType.CurlingBomb)
            {
                var forward = Quaternion.Euler(0, s.Yaw, 0) * Vector3.forward;
                velocity = forward * Mathf.Lerp(c.Curling.speed.x, c.Curling.speed.y, charge);
                position = PlayerMotorSimulation.HumanPosition(s) + Vector3.up * (c.Flight.radius + .04f) + forward * .5f;
            }
            if (c.Type == SubWeaponType.AngleShooter) velocity = direction * c.Angle.speed;
        }
        public static SubLaunchSolution SolveLaunch(PrototypePlayer player, PlayerSnapshot s, SubWeaponRuntimeConfig c, float charge)
        {
            LaunchGeometry(player, s, c, charge, out var position, out var velocity);
            var result = new SubLaunchSolution { Position = position, Velocity = velocity, Normal = Vector3.up };
            if (c.Type == SubWeaponType.InkMine)
            {
                if (s.Grounded && TryMineGround(s, out var hit)) { result.Position = hit.point; result.Velocity = Vector3.zero; result.Normal = hit.normal; result.Surface = hit.collider; }
                else { result.Failure = SubWeaponFailure.InvalidGround; result.Position = PlayerMotorSimulation.HumanPosition(s); }
            }
            return result;
        }
        public Entity Spawn(PrototypePlayer player, PlayerSnapshot s, float charge, double now, uint round)
            => Spawn(player, s, charge, now, round, SolveLaunch(player, s, PlayerLoadout.SubWeapon(s), charge));
        public Entity Spawn(PrototypePlayer player, PlayerSnapshot s, float charge, double now, uint round, SubLaunchSolution launch)
        {
            var c = PlayerLoadout.SubWeapon(s);
            if (!launch.Valid || CanUse(player.PlayerId, s, c) != SubWeaponFailure.None) return null;
            _now = now;
            if (IsPersistent(c.Type))
                while (Count(player.PlayerId, c.Type) >= c.Deployment.maxCount)
                {
                    Entity oldest = null;
                    foreach (var e in _entities) if (!e.Removed && e.State.Owner == player.PlayerId && e.State.Type == c.Type) { oldest = e; break; }
                    if (oldest == null) break;
                    if (c.Type == SubWeaponType.InkMine) Explode(oldest, now); else Remove(oldest);
                }
            var state = new SubEntityState { Id = ++_id, Round = round, Life = s.Revision, HeroRevision = s.HeroRevision, ConfigRevision = SubWeaponConfigService.Current.RevisionById(SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId)),
                Owner = player.PlayerId, Hero = s.HeroId, SubWeaponId = SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId), Action = s.SubAction, Team = s.Team, Charge = charge, Health = c.Durability.health, Yaw = s.Yaw };
            var entity = SubWeaponMotion.Create(launch, c, state, now); _entities.Add(entity);
            if (c.Type == SubWeaponType.Torpedo) CreateTarget(entity);
            Publish(entity, SubLifecycleKind.Spawn);
            return entity;
        }
        public IReadOnlyList<SubEntityState> Capture()
        { _capture.Clear(); foreach (var e in _entities) if (!e.Removed) { Stamp(e); _capture.Add(e.State); } return _capture; }
        public void Step(double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            _now = now;
            int count = _entities.Count;
            for (int i = 0; i < count; i++)
            {
                var e = _entities[i]; if (e.Removed) continue;
                if (CleanupOwner(e, players)) { Remove(e); continue; }
                if(CancelInTornado(e,e.State.Position,now))continue;
                if (e.Attached)
                {
                    if (e.Attachment == null) { Remove(e); continue; }
                    e.State.Position = e.Attachment.TransformPoint(e.LocalPosition); e.State.Normal = e.Attachment.TransformDirection(e.LocalNormal);
                }
                if (e.State.Phase == SubEntityPhase.Line) { MarkLine(e, players, now); if (now >= e.State.Expires) Remove(e); continue; }
                if (e.State.Phase == SubEntityPhase.Spray) { Spray(e,now,dt); continue; }
                if (e.State.Phase == SubEntityPhase.Droplet) { if (now >= e.Fuse) { Explode(e, now); } else Fly(e, now, dt, players); continue; }
                switch (e.State.Type)
                {
                    case SubWeaponType.InkMine: Mine(e, now, players); break;
                    case SubWeaponType.PointSensor: if (e.State.Phase == SubEntityPhase.Active) AreaMark(e, players, now); else Fly(e, now, dt, players); break;
                    case SubWeaponType.ToxicMist: if (e.State.Phase != SubEntityPhase.Active) Fly(e, now, dt, players); break;
                    case SubWeaponType.AngleShooter: Angle(e, now, dt, players); break;
                    case SubWeaponType.Sprinkler: if (e.State.Phase == SubEntityPhase.Active) Sprinkle(e, now, players); else Fly(e, now, dt, players); break;
                    case SubWeaponType.SplashWall: if (e.State.Phase == SubEntityPhase.Active) Wall(e, now, dt, players); else Fly(e, now, dt, players); break;
                    case SubWeaponType.Autobomb: Robot(e, now, dt, players); break;
                    case SubWeaponType.Torpedo: Torpedo(e, now, dt, players); break;
                    default:
                        if (!e.Attached) Fly(e, now, dt, players);
                        if (!e.Removed && SubWeaponMotion.FuseDue(e, now, dt)) Explode(e, now);
                        break;
                }
                if (!e.Removed && now >= e.State.Expires) Remove(e);
                if (!e.Removed && e.State.Phase != e.PublishedPhase) Publish(e, SubLifecycleKind.Phase);
                Stamp(e);
                if (e.HitTarget != null) e.HitTarget.UpdateState(e.State, e.Config, now);
            }
            ApplyMist(players, now, dt);
            for (int i = _entities.Count - 1; i >= 0; i--) if (_entities[i].Removed) _entities.RemoveAt(i);
        }
        static bool CleanupOwner(Entity e, IReadOnlyList<PrototypePlayer> players)
        {
            PrototypePlayer owner = null; foreach (var p in players) if (p != null && p.PlayerId == e.State.Owner) { owner = p; break; }
            if (owner == null || owner.Snapshot.Value.Team != e.State.Team) return true;
            if (IsPersistent(e.State.Type) && owner.Snapshot.Value.HeroRevision != e.State.HeroRevision) return true;
            return e.State.Type == SubWeaponType.Sprinkler && (owner.Snapshot.Value.Health <= 0 || owner.Snapshot.Value.Revision != e.State.Life);
        }
        void Fly(Entity e, double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            if (e.Removed) return;
            var c = e.Config;
            var incomingVelocity=e.State.Velocity;
            var previewVelocity=e.State.Velocity;
            Vector3 previewDelta=FlightStep(ref previewVelocity,c.Flight,dt,
                c.Type==SubWeaponType.CurlingBomb&&e.State.Phase==SubEntityPhase.Grounded||e.State.Phase==SubEntityPhase.Seeking);
            if(FlightCollision(_aim,e.State.Position,previewDelta,Mathf.Max(.03f,c.Flight.radius),e.State.Owner,e.State.Team,out var obstruction))
                previewDelta=previewDelta.normalized*Mathf.Min(previewDelta.magnitude,obstruction.Distance);
            if(CancelInTornado(e,e.State.Position+previewDelta,now))return;
            var contact = SubWeaponMotion.Advance(e, _aim, now, dt, out var h);
            if (contact != SubMotionContact.None)
            {
                var player = h.Collider.GetComponentInParent<PrototypePlayer>();
                var objectHit = h.Collider.GetComponent<SubWeaponTarget>();
                if (contact == SubMotionContact.Explode) { Explode(e, now, player, objectHit != null ? objectHit.Id : (uint?)null); return; }
                if (contact == SubMotionContact.Activate) Effect(e, c.Type == SubWeaponType.PointSensor ? c.Mark.radius : c.Mist.radius, SubEffectKind.Activation);
                if (contact == SubMotionContact.Deploy)
                {
                    if (c.Type == SubWeaponType.Sprinkler || c.Type == SubWeaponType.SplashWall) CreateTarget(e);
                    PaintRay(e, h.Point + h.Normal * .04f, -h.Normal, .2f, c.Paint.radius);
                }
                if (contact == SubMotionContact.Bounce && c.Type == SubWeaponType.CurlingBomb)
                {
                    if (player != null) Contact(e, player, now, c.Curling.contactDamage, c.Curling.contactInterval);
                    h.Collider.GetComponent<SpecialWeaponTarget>()?.Damage(e.State.Team,c.Curling.contactDamage);
                    if (objectHit != null) DamageObject(objectHit.Id, e.State.Team, c.Curling.contactDamage * SubWeaponObjectDamage.SubMultiplier(c.Type, objectHit.Type, contact:true));
                }
                // Floor contacts repeat while sliding. Send the phase or direction discontinuity once.
                bool phaseChanged = e.PublishedPhase != e.State.Phase;
                if (phaseChanged || contact != SubMotionContact.Bounce || h.Normal.y < .65f || Vector3.Dot(incomingVelocity,h.Normal)<-.5f) Publish(e, phaseChanged ? SubLifecycleKind.Phase : SubLifecycleKind.Motion);
                if (contact != SubMotionContact.Bounce) return;
            }
            if ((c.Type == SubWeaponType.CurlingBomb || c.Type == SubWeaponType.FizzyBomb) && Vector3.Distance(e.LastPaint, e.State.Position) >= c.Paint.trailSpacing)
            {
                float distance = Vector3.Distance(e.LastPaint, e.State.Position), step = Mathf.Max(.1f, c.Paint.trailSpacing);
                for (float t = step; t <= distance && t <= step * 32; t += step) PaintRay(e, Vector3.Lerp(e.LastPaint, e.State.Position, t / distance) + Vector3.up * .15f, Vector3.down, 4, c.Paint.trailRadius);
                e.LastPaint = e.State.Position;
            }
        }
        void Robot(Entity e, double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            var c = e.Config;
            if (e.State.Phase == SubEntityPhase.Warning) { if (now >= e.Fuse) Explode(e, now); return; }
            if (e.State.Phase == SubEntityPhase.Flying) { Fly(e, now, dt, players); return; }
            if (e.State.Phase == SubEntityPhase.Grounded)
            {
                if (now < e.NextAction) return;
                var target = Nearest(e, players, c.Tracking.radius);
                if (target == null) { Warn(e, now, c.Autobomb.fuse); return; }
                e.Target = target.PlayerId; e.State.Phase = SubEntityPhase.Seeking; e.State.Changed = now;
            }
            var victim = Find(players, e.Target);
            if (victim == null || now - e.State.Changed >= c.Tracking.duration) { Warn(e, now, c.Autobomb.fuse); return; }
            Vector3 delta = victim.Snapshot.Value.Position - e.State.Position;
            if (delta.magnitude <= c.Tracking.triggerRadius) { Warn(e, now, c.Autobomb.fuse); return; }
            delta.y = 0; delta = delta.normalized * c.Tracking.speed * dt;
            var next = e.State.Position + delta;
            if (Physics.Raycast(next + Vector3.up * .75f, Vector3.down, out var ground, 1.5f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) && ground.normal.y > .65f &&
                !_aim.ClosestCast(e.State.Position + Vector3.up * .15f, delta, delta.magnitude, .1f, e.State.Owner, out _, false, e.State.Team))
            { e.State.Position = ground.point + ground.normal * .15f; e.State.Velocity = delta / dt; e.State.Normal = ground.normal; e.State.Yaw = Quaternion.LookRotation(delta).eulerAngles.y; }
            else e.State.Velocity = Vector3.zero;
        }
        void Torpedo(Entity e, double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            var c = e.Config;
            if (e.State.Phase == SubEntityPhase.Flying && e.Fuse == 0)
            {
                var target = Nearest(e, players, c.Tracking.radius);
                if (target != null) { e.Target = target.PlayerId; e.State.Phase = SubEntityPhase.Transforming; e.State.Changed = now; e.State.Velocity = Vector3.zero; }
            }
            if (e.State.Phase == SubEntityPhase.Transforming)
            {
                if (now - e.State.Changed >= c.Torpedo.transform)
                {
                    e.State.Phase = SubEntityPhase.Seeking; e.State.Changed = now;
                    // Publish the initial chase velocity with the phase event, before the next snapshot.
                    var target = Find(players, e.Target);
                    if (target != null) e.State.Velocity = (target.Snapshot.Value.Position + Vector3.up * .5f - e.State.Position).normalized * c.Tracking.speed * 2;
                }
                return;
            }
            if (e.State.Phase == SubEntityPhase.Seeking)
            {
                var victim = Find(players, e.Target);
                if (victim == null || now - e.State.Changed > c.Tracking.duration) { e.State.Phase = SubEntityPhase.Flying; e.State.Changed = now; e.Fuse = now + c.Torpedo.rollFuse; }
                else e.State.Velocity = (victim.Snapshot.Value.Position + Vector3.up * .5f - e.State.Position).normalized * c.Tracking.speed * 2;
            }
            Fly(e, now, dt, players);
            if (!e.Removed && e.Fuse > 0 && now >= e.Fuse) Explode(e, now);
        }
        static void Warn(Entity e, double now, double fuse) { e.State.Phase = SubEntityPhase.Warning; e.State.Velocity = Vector3.zero; e.Fuse = now + fuse; e.State.Changed = now; }
        void Mine(Entity e, double now, IReadOnlyList<PrototypePlayer> players)
        {
            if (e.State.Phase == SubEntityPhase.Warning) { if (now >= e.Fuse) Explode(e, now); return; }
            if (now < e.NextAction) return;
            var c = e.Config;
            bool enemyInk = PlayerMotorSimulation.Query(e.State.Position + Vector3.up * .1f, Vector3.down, .3f, out var ink) && PlayerMotorSimulation.IsEnemy(ink.Owner, e.State.Team);
            if (enemyInk || Nearest(e, players, c.Mine.triggerRadius) != null) Warn(e, now, c.Mine.fuse);
        }
        void Angle(Entity e, double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            float remaining = Mathf.Min(e.Config.Angle.speed * dt, e.Config.Angle.range - e.Travelled);
            for (int attempt = 0; attempt < 12 && remaining > .0001f && !e.Removed; attempt++)
            {
                Vector3 from = e.State.Position, direction = e.State.Velocity.normalized;
                float before = e.Travelled;
                bool hit = SubWeaponMotion.LineStep(e, _aim, remaining, out var h); remaining -= e.Travelled - before;
                var line = e.State; line.ParentId = e.State.Id; line.Id = ++_id; line.Born = now; line.Phase = SubEntityPhase.Line; line.Position = from; line.End = e.State.Position; line.Expires = now + e.Config.Angle.trailDuration;
                var segment = new Entity(line, e.Config); _entities.Add(segment); Publish(segment, SubLifecycleKind.Spawn);
                if (!hit) break;
                var player = h.Collider.GetComponentInParent<PrototypePlayer>();
                if (player != null) { DamagePlayer(e, player, e.Config.Angle.damage); Mark(player, e.State.Team, now + e.Config.Mark.duration); Remove(e); break; }
                var target = h.Collider.GetComponent<SubWeaponTarget>();
                var sonar=h.Collider.GetComponent<SpecialWeaponTarget>();if(sonar!=null){sonar.Damage(e.State.Team,e.Config.Angle.damage);Remove(e);break;}
                if (target != null) { DamageObject(target.Id, e.State.Team, e.Config.Angle.damage); Remove(e); break; }
                PaintRay(e, h.Point + h.Normal * .03f, -h.Normal, .1f, e.Config.Paint.radius);
                SubWeaponMotion.ReflectLine(e, h);
                if (e.State.Explosions > e.Config.Angle.reflections) { Remove(e); break; }
                Publish(e, SubLifecycleKind.Motion);
            }
            if (e.Travelled >= e.Config.Angle.range - .001f) Remove(e);
        }
        void MarkLine(Entity e, IReadOnlyList<PrototypePlayer> players, double now)
        {
            var segment = e.State.End - e.State.Position;
            foreach (var p in players)
            {
                if (!Enemy(e, p)) continue;
                var center = p.Snapshot.Value.Position + Vector3.up * .5f;
                var point = e.State.Position + segment * Mathf.Clamp01(Vector3.Dot(center - e.State.Position, segment) / Mathf.Max(.0001f, segment.sqrMagnitude));
                if (InkExplosionRules.ClosestPoint(p, point, out var target) && Vector3.Distance(target, point) <= e.Config.Angle.trailRadius) Mark(p, e.State.Team, now + e.Config.Mark.duration);
            }
        }
        void AreaMark(Entity e, IReadOnlyList<PrototypePlayer> players, double now)
        { foreach (var p in players) if (Enemy(e, p) && InkExplosionRules.ClosestPoint(p, e.State.Position, out var point) && Vector3.Distance(point, e.State.Position) <= e.Config.Mark.radius) Mark(p, e.State.Team, now + e.Config.Mark.duration); }
        static void Mark(PrototypePlayer p, byte team, double until)
        { var s = p.Snapshot.Value; if (team == 1) s.MarkedUntilPink = Math.Max(s.MarkedUntilPink, until); else s.MarkedUntilBlue = Math.Max(s.MarkedUntilBlue, until); p.Snapshot.Value = s; }
        void ApplyMist(IReadOnlyList<PrototypePlayer> players, double now, float dt)
        {
            foreach (var p in players)
            {
                if (p == null || p.Snapshot.Value.Health <= 0) continue;
                var state = p.Snapshot.Value; float move = 1, drain = 0; bool inside = false;
                foreach (var e in _entities)
                {
                    if (e.Removed || e.State.Type != SubWeaponType.ToxicMist || e.State.Phase != SubEntityPhase.Active || !Enemy(e, p)) continue;
                    if (!InkExplosionRules.ClosestPoint(p, e.State.Position, out var point) || Vector3.Distance(point, e.State.Position) > e.Config.Mist.radius) continue;
                    if (!inside && state.MistUntil < now - dt * 1.5) state.MistExposure = 0;
                    inside = true; var m = e.Config.Mist;
                    float rate = Mathf.Lerp(1, m.moveRate, Mathf.Clamp01((float)(state.MistExposure / Math.Max(.00001, m.slowRamp))));
                    move = Mathf.Min(move, rate);
                    drain = Mathf.Max(drain, state.MistExposure < m.drainThresholds.x ? m.drainRates.x : state.MistExposure < m.drainThresholds.y ? m.drainRates.y : m.drainRates.z);
                }
                if (inside) { state.MistExposure += dt; state.MistUntil = now + dt * 2; state.MistMoveRate = move; state.MistDrainRate = drain; state.Ink = Mathf.Max(0, state.Ink - drain * dt); state.InkRecoverAt = Math.Max(state.InkRecoverAt, state.MistUntil); }
                else { state.MistExposure = 0; state.MistUntil = 0; state.MistMoveRate = 1; state.MistDrainRate = 0; }
                p.Snapshot.Value = state;
            }
        }
        void Sprinkle(Entity e, double now, IReadOnlyList<PrototypePlayer> players)
        {
            if (now + 1e-8 < e.NextAction) return;
            var c = e.Config; double age = now - e.State.Changed;
            e.NextAction = now + (age < c.Sprinkler.phases.x ? c.Sprinkler.intervals.x : age < c.Sprinkler.phases.x + c.Sprinkler.phases.y ? c.Sprinkler.intervals.y : c.Sprinkler.intervals.z);
            var rotate = Quaternion.FromToRotation(Vector3.up, e.State.Normal);
            var direction = rotate * Quaternion.Euler(0, (float)(age * c.Sprinkler.rotationSpeed), 0) * new Vector3(1, .2f, 0).normalized;
            var start = e.State.Position + e.State.Normal * .15f;
            var state=e.State;state.ParentId=e.State.Id;state.Id=++_id;state.Phase=SubEntityPhase.Spray;state.Health=0;state.Position=start;state.Velocity=direction*c.Sprinkler.dropletSpeed;
            state.Born=state.Changed=now;state.Expires=now+c.Sprinkler.dropletLifetime;var spray=new Entity(state,c);_entities.Add(spray);Publish(spray,SubLifecycleKind.Spawn);
            e.State.Explosions++; // Monotonic spray pulse for the presentation.
        }
        void Spray(Entity e,double now,float dt)
        {
            if(now>=e.State.Expires||e.Travelled>=e.Config.Sprinkler.range){Remove(e);return;}
            var delta=FlightStep(ref e.State.Velocity,new SubFlight{gravity=e.Config.Sprinkler.dropletGravity},dt);
            e.Travelled+=delta.magnitude;
            if(FlightCollision(_aim,e.State.Position,delta,.06f,e.State.Owner,e.State.Team,out var h))
            {
                var victim=h.Collider.GetComponentInParent<PrototypePlayer>();if(victim!=null)DamagePlayer(e,victim,e.Config.Sprinkler.damage);
                h.Collider.GetComponent<SpecialWeaponTarget>()?.Damage(e.State.Team,e.Config.Sprinkler.damage);
                var target=h.Collider.GetComponent<SubWeaponTarget>();if(target!=null)DamageObject(target.Id,e.State.Team,e.Config.Sprinkler.damage*SubWeaponObjectDamage.SubMultiplier(e.Config.Type,target.Type));
                PaintRay(e,h.Point+h.Normal*.03f,-h.Normal,.1f,e.Config.Sprinkler.paintRadius);Remove(e);
            }
            else e.State.Position+=delta;
        }
        void Wall(Entity e, double now, float dt, IReadOnlyList<PrototypePlayer> players)
        {
            if (now < e.NextAction) return;
            e.State.Health = Mathf.Max(0, e.State.Health - e.Config.Durability.health / (float)e.Config.Wall.lifetime * dt);
            if (e.State.Health <= 0) { Remove(e); return; }
            var rotation = Quaternion.Euler(0, e.State.Yaw, 0);
            foreach (var p in players)
            {
                if (!Enemy(e, p)) continue;
                var local = Quaternion.Inverse(rotation) * (p.Snapshot.Value.Position - e.State.Position);
                if (Mathf.Abs(local.x) < e.Config.Wall.size.x * .5f + .4f && Mathf.Abs(local.z) < e.Config.Wall.size.z * .5f + .45f && local.y > -1 && local.y < e.Config.Wall.size.y)
                    Contact(e, p, now, e.Config.Wall.damage, e.Config.Wall.interval);
            }
        }
        void Contact(Entity e, PrototypePlayer p, double now, float damage, double interval)
        { if (e.ContactTimes.TryGetValue(p.PlayerId, out var last) && now - last < interval) return; e.ContactTimes[p.PlayerId] = now; DamagePlayer(e, p, damage); }
        void Explode(Entity e, double now, PrototypePlayer direct = null, uint? directObject = null)
        {
            if (e.Removed) return;
            var c = e.Config; bool droplet = e.State.Phase == SubEntityPhase.Droplet;
            float radius = droplet ? c.Torpedo.dropletBlastRadius : c.Blast.outerRadius;
            if (c.Type == SubWeaponType.CurlingBomb) radius = Mathf.Lerp(radius, c.Curling.outerRadius, e.State.Charge);
            if (c.Type == SubWeaponType.FizzyBomb && e.State.Explosions > 0) radius = e.State.Explosions == 1 ? c.Fizzy.outerRadii.x : c.Fizzy.outerRadii.y;
            var match = PrototypeMatch.Current;
            if (match != null)
                foreach (var p in match.Players)
                {
                    if (!Enemy(e, p) || !InkExplosionRules.ClosestPoint(p, e.State.Position, out var point)) continue;
                    float distance = Vector3.Distance(point, e.State.Position);
                    bool hitDirect = direct == p && c.Blast.directDamage > 0;
                    float damage = hitDirect ? c.Blast.directDamage : droplet ? (distance <= radius ? c.Torpedo.dropletDamage : 0) : c.Damage(distance, e.State.Charge, e.State.Explosions);
                    if (damage > 0 && (hitDirect || Visible(e, point))) DamagePlayer(e, p, damage);
                    if (c.Type == SubWeaponType.InkMine && distance <= c.Mark.radius) Mark(p, e.State.Team, now + c.Mark.duration);
                }
            _blastTargets.Clear(); foreach (var other in _entities) if (!other.Removed && other != e && other.State.Team != e.State.Team && other.State.Health > 0) _blastTargets.Add(other);
            foreach (var target in _blastTargets)
            {
                var point=target.HitTarget!=null?target.HitTarget.HitCollider.ClosestPoint(e.State.Position):target.State.Position;
                float distance = Vector3.Distance(e.State.Position, point);
                bool directHit=!droplet&&target.State.Id==directObject&&c.Blast.directDamage>0;
                float damage = directHit?c.Blast.directDamage:droplet ? distance <= radius ? c.Torpedo.dropletDamage : 0 : c.Damage(distance, e.State.Charge, e.State.Explosions);
                if (damage > 0 && (directHit||Visible(e, point, target.State.Id))) DamageObject(target.State.Id, e.State.Team, damage*SubWeaponObjectDamage.SubMultiplier(c.Type,target.State.Type,droplet));
            }
            if(match!=null)foreach(var target in match.SpecialWeapons.Entities)
            {
                if(target.Removed||target.Target==null||target.State.Team==e.State.Team)continue;
                Vector3 point=target.Target.HitCollider.ClosestPoint(e.State.Position);float distance=Vector3.Distance(point,e.State.Position);
                float damage=droplet?(distance<=radius?c.Torpedo.dropletDamage:0):c.Damage(distance,e.State.Charge,e.State.Explosions);
                if(damage>0&&SpecialWeaponService.Visible(e.State.Position,point))match.SpecialWeapons.DamageObject(target.State.Id,e.State.Team,damage*(droplet?1:SpecialObjectDamage.FromSub(c.Type)));
            }
            float paintRadius = droplet ? c.Torpedo.dropletPaintRadius : c.Type == SubWeaponType.CurlingBomb ? Mathf.Lerp(c.Paint.radius, c.Curling.paintRadius, e.State.Charge) : c.Paint.radius;
            PaintExplosion(e, paintRadius); Effect(e, radius);
            if (SubWeaponMotion.NextFizzyBurst(e, now)) { Publish(e, SubLifecycleKind.Phase); return; }
            if (c.Type == SubWeaponType.Torpedo && !droplet && e.Target.HasValue)
            {
                for (int i = 0; i < c.Torpedo.droplets; i++)
                {
                    var state = e.State; state.ParentId = e.State.Id; state.Id = ++_id; state.Phase = SubEntityPhase.Droplet; state.Health = 0; state.Explosions = 0; state.Born = now;
                    float a = i * Mathf.PI * 2 / c.Torpedo.droplets;
                    state.Position += Vector3.up * .1f;
                    state.Velocity = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (c.Torpedo.dropletRadius / .5f) + Vector3.up * 4;
                    state.Expires = now + 3;
                    var child = new Entity(state, c) { Fuse = now + .5 + c.Torpedo.dropletFuse }; _entities.Add(child); Publish(child, SubLifecycleKind.Spawn);
                }
            }
            Remove(e);
        }
        void Effect(Entity e, float radius, SubEffectKind kind = SubEffectKind.Explosion) => Effects.Add(new SubEffectEvent { Sequence = ++_effect, Round = e.State.Round, EntityId = e.State.Id, Action = e.State.Action, Kind = kind, At = _now, Normal = e.State.Normal, Position = e.State.Position, Radius = radius, Team = e.State.Team, Hero = e.State.Hero, SubWeaponId = e.State.SubWeaponId, ConfigRevision = e.State.ConfigRevision });
        static bool Enemy(Entity e, PrototypePlayer p) => p != null && p.IsSpawned && p.Snapshot.Value.Health > 0 && p.Snapshot.Value.Team != e.State.Team;
        bool Visible(Entity e, Vector3 target, uint targetId = 0)
        {
            Vector3 start = e.State.Position + e.State.Normal * .05f, delta = target - start;
            return !_aim.ClosestCast(start, delta, Mathf.Max(0, delta.magnitude - .06f), .01f, e.State.Owner, out var hit, false, e.State.Team) || targetId != 0 && hit.Collider.GetComponent<SubWeaponTarget>()?.Id == targetId;
        }
        PrototypePlayer Nearest(Entity e, IReadOnlyList<PrototypePlayer> players, float radius)
        {
            PrototypePlayer result = null; float nearest = radius;
            foreach (var p in players) if (Enemy(e, p) && InkExplosionRules.ClosestPoint(p, e.State.Position, out var point))
            { float d = Vector3.Distance(e.State.Position, point); if (d < nearest && Visible(e, point)) { nearest = d; result = p; } }
            return result;
        }
        static PrototypePlayer Find(IReadOnlyList<PrototypePlayer> players, ulong? id)
        { foreach (var p in players) if (p != null && p.PlayerId == id && p.Snapshot.Value.Health > 0) return p; return null; }
        static void DamagePlayer(Entity e, PrototypePlayer p, float damage)
        { if (p.Snapshot.Value.Team != e.State.Team) p.ReceiveDamage(e.State.Team, damage, p.Snapshot.Value.Position - e.State.Position, e.State.Owner); }
        public void DamageObject(uint id, byte team, float damage)
        {
            if (!float.IsFinite(damage) || damage <= 0) return;
            foreach (var e in _entities)
            {
                if (e.Removed || e.State.Id != id || e.State.Team == team || e.State.Health <= 0) continue;
                e.State.Health = Mathf.Max(0, e.State.Health - damage);
                // Destruction intentionally produces no attack explosion (particularly torpedoes).
                if (e.State.Health <= 0) { Effect(e, .6f, SubEffectKind.Destroyed); Remove(e); } else { Publish(e, SubLifecycleKind.Phase); Effect(e,.35f,SubEffectKind.Hit); } return;
            }
        }
        public void DamageObjectsFromMainExplosion(InkShot shot, Vector3 position, bool collision, uint? directObject = null)
        {
            var ammo = shot.Configuration?.Ammo; if (ammo == null) return;
            foreach (var e in _entities)
            {
                if (e.Removed || e.State.Team == shot.Team || e.State.Health <= 0 || ammo.ExcludeDirectHitFromExplosion && e.State.Id == directObject) continue;
                var point = e.HitTarget != null ? e.HitTarget.HitCollider.ClosestPoint(position) : e.State.Position;
                var delta = point - position;
                if (_aim.ClosestCast(position, delta, Mathf.Max(0, delta.magnitude - .08f), .01f, shot.Shooter, out var hit, false, shot.Team) && hit.Collider.GetComponent<SubWeaponTarget>()?.Id != e.State.Id) continue;
                DamageObject(e.State.Id, shot.Team, InkExplosionRules.Damage(ammo, collision, delta.magnitude) * SubWeaponObjectDamage.Multiplier(shot.Configuration,e.State.Type,true));
            }
        }
        void PaintExplosion(Entity e, float radius)
        {
            if (radius <= 0) return;
            _painted.Clear(); PaintRay(e, e.State.Position + e.State.Normal * .04f, -e.State.Normal, radius + .2f, radius, true);
            for (int i = 0; i < 32; i++)
            {
                float y = 1 - 2 * ((i + .5f) / 32), angle = i * 2.39996323f, r = Mathf.Sqrt(1 - y * y);
                PaintRay(e, e.State.Position, new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r), radius, radius, true);
            }
        }
        void PaintRay(Entity e, Vector3 start, Vector3 direction, float distance, float radius, bool unique = false)
        {
            if (radius <= 0 || !_aim.ClosestCast(start, direction, distance, 0, e.State.Owner, out var hit, false, e.State.Team)) return;
            var surface = hit.Collider.GetComponentInParent<PaintSurface>();
            if (surface == null) return;
            var point=hit.Point; Vector4 clip0=default,clip1=default;
            if(unique)
            {
                float plane=Vector3.Dot(point,hit.Normal);
                foreach(var previous in _painted)if(previous.surface==surface.SurfaceId&&Vector3.Dot(previous.normal,hit.Normal)>.999f&&Mathf.Abs(previous.plane-plane)<.025f)return;
                _painted.Add((surface.SurfaceId,hit.Normal,plane));
                float offset=Vector3.Dot(e.State.Position-point,hit.Normal);
                radius=Mathf.Sqrt(Mathf.Max(0,radius*radius-offset*offset));if(radius<=.001f)return;
                var projection=e.State.Position-hit.Normal*offset;
                if(_aim.ClosestCast(projection+hit.Normal*.025f,-hit.Normal,.075f,0,e.State.Owner,out var projected,false,e.State.Team)&&projected.Collider.GetComponentInParent<PaintSurface>()==surface)point=projected.Point;
                var stamp=new PaintStamp{Position=point,Normal=hit.Normal};InkShapeAtlas.StampBasis(stamp,out var tangent,out var bitangent);
                var center=point+hit.Normal*.025f;
                for(int i=0;i<8;i++)
                {
                    float angle=i*Mathf.PI/4,extent=radius;var radial=tangent*Mathf.Cos(angle)+bitangent*Mathf.Sin(angle);
                    if(_aim.ClosestCast(center,radial,extent,.01f,e.State.Owner,out var edge,false,e.State.Team)&&edge.Collider.GetComponentInParent<PaintSurface>()!=surface)extent=Mathf.Max(0,edge.Distance-.025f);
                    if(!Visible(e,center+radial*extent))
                    { float lo=0,hi=extent;for(int k=0;k<10;k++){float mid=(lo+hi)*.5f;if(Visible(e,center+radial*mid))lo=mid;else hi=mid;}extent=Mathf.Max(0,lo-.025f); }
                    if(i<4)clip0[i]=extent;else clip1[i-4]=extent;
                }
            }
            PrototypeMatch.Current?.Paint(surface, point, hit.Normal, radius, e.State.Team, e.Config.Paint.hardness, e.Config.Paint.strength,
                e.State.Id * 2654435761u + (uint)e.State.Explosions,clipEnabled:unique,clip0:clip0,clip1:clip1,credit:new PaintCredit(e.State.Owner,e.State.Team,e.State.Round,e.State.HeroRevision,PaintAttackKind.Sub));
        }
        void CreateTarget(Entity e)
        {
            var go = new GameObject("SubTarget_" + e.State.Id); e.HitTarget = go.AddComponent<SubWeaponTarget>();
            e.HitTarget.Initialize(this, e.State, e.Config);
        }
        bool CancelInTornado(Entity e,Vector3 end,double now)
        {
            if(e.State.Type>SubWeaponType.Torpedo||e.State.Phase==SubEntityPhase.Spray||e.State.Phase==SubEntityPhase.Line)return false;
            if(PrototypeMatch.Current?.SpecialWeapons.AbsorbsBomb(e.State.Position,end,e.Config.Flight.radius,e.State.Team,now)!=true)return false;
            // No explosion, damage, paint, secondary droplets or explosion presentation.
            Remove(e);return true;
        }
        void Remove(Entity e)
        { if (e.Removed) return; e.Removed = true; Publish(e, SubLifecycleKind.Remove); if (e.HitTarget != null) { e.HitTarget.gameObject.SetActive(false); UnityEngine.Object.Destroy(e.HitTarget.gameObject); e.HitTarget = null; } }
        public void ClearOwner(ulong owner, bool persistentOnly = false)
        { foreach (var e in _entities) if (e.State.Owner == owner && (!persistentOnly || IsPersistent(e.State.Type))) Remove(e); }
        public void Clear()
        { foreach (var e in _entities) Remove(e); _entities.Clear(); Effects.Clear(); _capture.Clear(); }
        public static Vector3 FlightStep(ref Vector3 velocity, SubFlight flight, float dt, bool noDrag = false)
        {
            if (!noDrag) velocity *= Mathf.Exp(-flight.drag * dt);
            var delta = velocity * dt + Vector3.down * (flight.gravity * dt * dt * .5f);
            velocity += Vector3.down * (flight.gravity * dt); return delta;
        }
        // PhysX sweep hits starting inside a collider can report point=(0,0,0).
        // Resolve overlaps first so close throws never teleport to the world origin.
        public static bool FlightCollision(TpsAimSolver aim, Vector3 from, Vector3 delta, float radius, ulong owner, byte team, out TpsCollision hit)
        {
            if (aim.Overlap(from, radius, owner, delta.sqrMagnitude > .00001f ? -delta.normalized : Vector3.up, out hit, null, team, floatingMesh:true))
            { TpsAimSolver.UnembedFloatingContact(ref hit, radius); return true; }
            return aim.ClosestCast(from, delta, delta.magnitude, radius, owner, out hit, null, team);
        }
    }
}
