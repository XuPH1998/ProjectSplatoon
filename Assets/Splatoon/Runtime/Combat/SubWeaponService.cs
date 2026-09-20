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
    public enum SubWeaponEntityPhase : byte { Flying, Sliding, Hovering, Tracking, Arming, Active }
    public enum SubWeaponEventType : byte { Spawned, Triggered, Exploded, Destroyed, Recalled }

    public struct SubWeaponEntityState : INetworkSerializable
    {
        public uint Id, Round, OwnerHeroRevision;
        public ulong OwnerId, TargetId;
        public byte Team;
        public SubWeaponKind Kind;
        public SubWeaponEntityPhase Phase;
        public Vector3 Position, Velocity, Normal, Scale;
        public Quaternion Rotation;
        public double SpawnedAt, PhaseChangedAt, ExpiresAt, NextEffectAt;
        public float Health, Travelled;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref OwnerHeroRevision);
            s.SerializeValue(ref OwnerId); s.SerializeValue(ref TargetId); s.SerializeValue(ref Team);
            s.SerializeValue(ref Kind); s.SerializeValue(ref Phase); s.SerializeValue(ref Position); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Normal); s.SerializeValue(ref Scale); s.SerializeValue(ref Rotation); s.SerializeValue(ref SpawnedAt);
            s.SerializeValue(ref PhaseChangedAt); s.SerializeValue(ref ExpiresAt); s.SerializeValue(ref NextEffectAt);
            s.SerializeValue(ref Health); s.SerializeValue(ref Travelled);
        }
    }

    public struct SubWeaponEvent : INetworkSerializable
    {
        public uint Id, Round;
        public SubWeaponEventType Type;
        public SubWeaponKind Kind;
        public byte Team;
        public Vector3 Position, Normal;
        public double Time;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Type); s.SerializeValue(ref Kind);
            s.SerializeValue(ref Team); s.SerializeValue(ref Position); s.SerializeValue(ref Normal); s.SerializeValue(ref Time);
        }
    }

    public interface ISubWeaponDamageable
    {
        uint EntityId { get; }
        byte Team { get; }
        bool ReceiveSubWeaponDamage(byte attackerTeam, float damage);
    }

    public static class SubWeaponRules
    {
        public static bool CanDeploy(PlayerSnapshot state, PlayerInputFrame input, SubWeaponRuntimeConfig config, MatchPhase phase, double now) =>
            config != null && !input.CancelFire && phase != MatchPhase.Finished && state.Health > 0 && !state.Swimming && input.HeroRevision == state.HeroRevision &&
            state.Ink + .0001f >= config.InkCost && now + 1e-8 >= state.SubWeaponCooldownUntil;
        public static int ReplacementsAfterSpawn(int activeCount, int maxActive) => Mathf.Max(0, activeCount - Mathf.Max(1, maxActive));
        public static SubWeaponSurfaceMask Surface(Vector3 normal) => normal.y >= .65f ? SubWeaponSurfaceMask.Floor :
            normal.y <= -.65f ? SubWeaponSurfaceMask.Ceiling : SubWeaponSurfaceMask.Wall;
        public static bool Supports(SubWeaponSurfaceMask mask, Vector3 normal) => (mask & Surface(normal)) != 0;
    }

    /// <summary>One fixed-step authority for every secondary weapon configuration.</summary>
    public sealed class SubWeaponService
    {
        readonly PrototypeMatch _match;
        readonly List<SubWeaponEntityState> _entities = new(16);
        readonly Dictionary<uint, SubWeaponRuntimeConfig> _configs = new();
        readonly List<SubWeaponEvent> _events = new(16);
        RaycastHit[] _castHits = new RaycastHit[32];
        RaycastHit[] _occlusionHits = new RaycastHit[32];
        uint _nextId;

        public IReadOnlyList<SubWeaponEntityState> Entities => _entities;
        public List<SubWeaponEvent> PendingEvents => _events;
        public SubWeaponService(PrototypeMatch match) => _match = match;

        public void ProcessInput(PrototypePlayer player, ref PlayerSnapshot state, PlayerInputFrame input, double now, MatchPhase phase)
        {
            var config = SubWeaponConfigService.Current.Get(state.HeroId);
            bool pressed = input.SubWeaponPressSequence != state.ConsumedSubWeaponPress;
            bool released = input.SubWeaponReleaseSequence != state.ConsumedSubWeaponRelease;
            if (pressed) state.ConsumedSubWeaponPress = input.SubWeaponPressSequence;
            if (released) state.ConsumedSubWeaponRelease = input.SubWeaponReleaseSequence;
            if (config == null) { state.ActiveSubWeapons = 0; return; }
            bool deploy = config.DeploymentMode == SubWeaponDeploymentMode.Immediate ? pressed : released;
            if (!deploy || !SubWeaponRules.CanDeploy(state, input, config, phase, now)) return;
            if (!TryCreate(player, state, config, now, out var entity)) return;

            state.Ink -= config.InkCost;
            state.InkRecoverAt = Math.Max(state.InkRecoverAt, now + .35);
            state.SubWeaponCooldownUntil = now + config.CooldownSeconds;
            _entities.Add(entity); _configs.Add(entity.Id, config);
            _events.Add(Event(entity, SubWeaponEventType.Spawned, now));

            int ownedCount = OwnedCount(player.PlayerId);
            while (ownedCount > config.MaxActive)
            {
                int oldest = OldestOwnedIndex(player.PlayerId);
                if (oldest < 0) break;
                RemoveAt(oldest, SubWeaponEventType.Recalled, now); ownedCount--;
            }
            state.ActiveSubWeapons = (byte)Mathf.Min(255, ownedCount);
        }

        bool TryCreate(PrototypePlayer player, PlayerSnapshot owner, SubWeaponRuntimeConfig config, double now, out SubWeaponEntityState entity)
        {
            entity = default;
            Vector3 forward = Quaternion.Euler(owner.Pitch, owner.Yaw, 0) * Vector3.forward;
            Vector3 planar = Quaternion.Euler(0, owner.Yaw, 0) * Vector3.forward;
            Vector3 position;
            Quaternion rotation;
            Vector3 normal;
            SubWeaponEntityPhase phase;
            Vector3 velocity;
            if (config.DeploymentMode == SubWeaponDeploymentMode.Immediate)
            {
                normal = Vector3.up; rotation = Quaternion.LookRotation(planar, Vector3.up);
                position = owner.Position + Vector3.up * (config.Kind == SubWeaponKind.CurlingBomb ? .3f : 1.05f) + planar * .65f;
                velocity = config.Kind == SubWeaponKind.CurlingBomb ? planar * config.LaunchSpeed : forward * config.LaunchSpeed + Vector3.up * 1.5f;
                phase = config.Kind == SubWeaponKind.CurlingBomb ? SubWeaponEntityPhase.Sliding : SubWeaponEntityPhase.Flying;
            }
            else
            {
                Quaternion view = Quaternion.Euler(owner.Pitch, owner.Yaw, 0);
                Vector3 pivot = owner.Position + PrototypePlayer.CameraPivotOffset(owner, player.Presentation);
                Vector3 origin = PrototypePlayer.CameraPosition(pivot, view, player.Presentation);
                if (!Physics.Raycast(origin, forward, out var hit, config.PlacementRange, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) ||
                    hit.collider.GetComponentInParent<PaintSurface>() == null || !SubWeaponRules.Supports(config.AttachSurfaces, hit.normal)) return false;
                normal = hit.normal.normalized; position = hit.point + normal * .025f;
                Vector3 tangentForward = Vector3.ProjectOnPlane(planar, normal).normalized;
                if (tangentForward.sqrMagnitude < .001f) tangentForward = Vector3.Cross(normal, Vector3.right).normalized;
                rotation = Quaternion.LookRotation(tangentForward, normal); velocity = Vector3.zero;
                phase = config.Kind == SubWeaponKind.InkMine && config.ArmingSeconds > 0 ? SubWeaponEntityPhase.Arming : SubWeaponEntityPhase.Active;
            }
            uint id = ++_nextId;
            entity = new SubWeaponEntityState { Id = id, Round = _match.State.Value.Round, OwnerId = player.PlayerId,
                OwnerHeroRevision = owner.HeroRevision, Team = owner.Team, Kind = config.Kind, Phase = phase,
                Position = position, Velocity = velocity, Normal = normal, Rotation = rotation, SpawnedAt = now,
                Scale = config.Kind == SubWeaponKind.InkCurtain ? new Vector3(config.Width, config.Height, .12f) :
                    config.Kind is SubWeaponKind.SpeedPad or SubWeaponKind.JumpPad ? new Vector3(config.Width, config.Height, 1.2f) :
                    config.Kind == SubWeaponKind.Sprinkler ? new Vector3(config.Width, config.Height, config.Width) :
                    new Vector3(config.Width, config.Height, config.Width),
                PhaseChangedAt = now, ExpiresAt = now + config.LifetimeSeconds, NextEffectAt = now + config.EffectInterval,
                Health = config.MaxHealth, TargetId = ulong.MaxValue };
            return true;
        }

        public void Simulate(float dt, double now)
        {
            PruneOwners(now);
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                var entity = _entities[i];
                if (!_configs.TryGetValue(entity.Id, out var config)) { _entities.RemoveAt(i); continue; }
                bool removed = entity.Kind switch
                {
                    SubWeaponKind.Torpedo => SimulateTorpedo(ref entity, config, dt, now),
                    SubWeaponKind.CurlingBomb => SimulateCurling(ref entity, config, dt, now),
                    SubWeaponKind.InkMine => SimulateMine(ref entity, config, now),
                    SubWeaponKind.SpeedPad or SubWeaponKind.JumpPad => SimulatePad(ref entity, config, now),
                    SubWeaponKind.Sprinkler => SimulateSprinkler(ref entity, config, now),
                    _ => now >= entity.ExpiresAt && RemoveAt(i, SubWeaponEventType.Recalled, now)
                };
                if (removed) continue;
                if (i < _entities.Count && _entities[i].Id == entity.Id) _entities[i] = entity;
            }
            PaintBoostTrails(now);
            RefreshCounts();
        }

        bool SimulateTorpedo(ref SubWeaponEntityState entity, SubWeaponRuntimeConfig config, float dt, double now)
        {
            if (now >= entity.ExpiresAt) return Explode(entity.Id, now);
            if (entity.Phase == SubWeaponEntityPhase.Flying)
            {
                Vector3 nextVelocity = entity.Velocity + Vector3.down * config.Gravity * dt;
                Vector3 delta = (entity.Velocity + nextVelocity) * (.5f * dt);
                if (TryEntityCast(entity, config.CollisionRadius, delta, out var hit, out var proxy) || now - entity.SpawnedAt >= .45)
                {
                    if (proxy != null) { Damage(proxy.EntityId, entity.Team, config.Damage); return Explode(entity.Id, now); }
                    if (hit.collider != null) entity.Position = hit.point + hit.normal * config.CollisionRadius;
                    else entity.Position += delta;
                    entity.Velocity = Vector3.zero; entity.Phase = SubWeaponEntityPhase.Hovering; entity.PhaseChangedAt = now;
                }
                else { entity.Position += delta; entity.Velocity = nextVelocity; }
                return false;
            }
            PrototypePlayer target = FindTarget(entity.Position, entity.Team, config.ScanRadius, entity.TargetId);
            if (target != null)
            {
                entity.TargetId = target.PlayerId; entity.Phase = SubWeaponEntityPhase.Tracking;
                Vector3 destination = target.Snapshot.Value.Position + Vector3.up * .9f;
                Vector3 delta = destination - entity.Position;
                if (delta.magnitude <= config.CollisionRadius + .45f) return Explode(entity.Id, now);
                entity.Velocity = delta.normalized * config.LaunchSpeed;
                entity.Rotation = Quaternion.LookRotation(entity.Velocity, Vector3.up);
                entity.Position += entity.Velocity * dt;
            }
            else { entity.TargetId = ulong.MaxValue; entity.Phase = SubWeaponEntityPhase.Hovering; }
            return false;
        }

        bool SimulateCurling(ref SubWeaponEntityState entity, SubWeaponRuntimeConfig config, float dt, double now)
        {
            if (now >= entity.ExpiresAt || entity.Travelled >= config.MaxTravelDistance) return Explode(entity.Id, now);
            Vector3 velocity = Vector3.ProjectOnPlane(entity.Velocity, Vector3.up);
            Vector3 delta = velocity * dt;
            if (delta.sqrMagnitude > 0 && TryEntityCast(entity, config.CollisionRadius, delta, out var hit, out var proxy, Vector3.up * config.CollisionRadius))
            {
                if (proxy != null) { Damage(proxy.EntityId, entity.Team, config.Damage); return Explode(entity.Id, now); }
                entity.Position = hit.point + hit.normal * config.CollisionRadius;
                entity.Velocity = Vector3.Reflect(velocity, hit.normal) * config.BounceRetention;
            }
            else { entity.Position += delta; entity.Velocity = velocity; }
            entity.Travelled += delta.magnitude;
            if (Physics.Raycast(entity.Position + Vector3.up, Vector3.down, out var floor, 2, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
            { entity.Position.y = floor.point.y + .03f; entity.Normal = floor.normal; }
            if (now + 1e-8 >= entity.NextEffectAt) { PaintAt(entity, config, entity.Position + Vector3.up * .2f, Vector3.down, config.PaintRadius * .45f); entity.NextEffectAt = now + config.EffectInterval; }
            return false;
        }

        bool SimulateMine(ref SubWeaponEntityState entity, SubWeaponRuntimeConfig config, double now)
        {
            if (now >= entity.ExpiresAt) return Remove(entity.Id, SubWeaponEventType.Recalled, now);
            if (entity.Phase == SubWeaponEntityPhase.Arming && now - entity.PhaseChangedAt >= config.ArmingSeconds)
            { entity.Phase = SubWeaponEntityPhase.Active; entity.PhaseChangedAt = now; }
            if (entity.Phase == SubWeaponEntityPhase.Active && FindTarget(entity.Position, entity.Team, config.TriggerRadius, ulong.MaxValue) != null)
            { _events.Add(Event(entity, SubWeaponEventType.Triggered, now)); return Explode(entity.Id, now); }
            return false;
        }

        bool SimulatePad(ref SubWeaponEntityState entity, SubWeaponRuntimeConfig config, double now)
        {
            if (now >= entity.ExpiresAt) return Remove(entity.Id, SubWeaponEventType.Recalled, now);
            foreach (var player in _match.Players)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                if (state.Health <= 0 || (entity.Kind == SubWeaponKind.SpeedPad && state.Team != entity.Team) ||
                    Vector3.Distance(state.Position, entity.Position) > config.TriggerRadius || now < state.SubWeaponTriggerCooldownUntil) continue;
                Vector3 facing = Quaternion.Euler(0, state.Yaw, 0) * Vector3.forward;
                state.SubWeaponTriggerCooldownUntil = now + config.PerPlayerTriggerCooldown;
                if (entity.Kind == SubWeaponKind.SpeedPad)
                {
                    state.SubWeaponMoveVelocity = facing * config.BoostSpeed;
                    state.SubWeaponMoveStartedAt = now; state.SubWeaponMoveUntil = now + config.BoostSeconds;
                }
                else
                {
                    state.PlanarVelocity += facing * config.JumpForwardSpeed;
                    state.VerticalSpeed = Mathf.Max(state.VerticalSpeed, config.JumpUpSpeed); state.Grounded = false;
                }
                player.Snapshot.Value = state;
                _events.Add(Event(entity, SubWeaponEventType.Triggered, now));
            }
            return false;
        }

        bool SimulateSprinkler(ref SubWeaponEntityState entity, SubWeaponRuntimeConfig config, double now)
        {
            if (now >= entity.ExpiresAt) return Remove(entity.Id, SubWeaponEventType.Recalled, now);
            if (now + 1e-8 < entity.NextEffectAt) return false;
            Vector3 tangent = entity.Rotation * Vector3.right;
            Vector3 bitangent = Vector3.Cross(entity.Normal, tangent).normalized;
            for (int i = 0; i < 6; i++)
            {
                float angle = i * Mathf.PI / 3;
                Vector3 offset = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * config.PaintRadius * .55f;
                PaintAt(entity, config, entity.Position + entity.Normal * .25f + offset, -entity.Normal, config.PaintRadius * .42f);
            }
            entity.NextEffectAt = now + config.EffectInterval;
            return false;
        }

        PrototypePlayer FindTarget(Vector3 position, byte team, float radius, ulong preferred)
        {
            PrototypePlayer best = null; float bestDistance = radius * radius;
            foreach (var player in _match.Players)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                if (state.Health <= 0 || state.Team == team) continue;
                float distance = (state.Position + Vector3.up * .9f - position).sqrMagnitude;
                if (player.PlayerId == preferred && distance <= radius * radius) return player;
                if (distance <= bestDistance) { bestDistance = distance; best = player; }
            }
            return best;
        }

        bool TryEntityCast(SubWeaponEntityState entity, float radius, Vector3 delta, out RaycastHit result, out SubWeaponHitProxy proxy, Vector3 offset = default)
        {
            result = default; proxy = null;
            if (delta.sqrMagnitude <= .000001f) return false;
            int count;
            do
            {
                count = Physics.SphereCastNonAlloc(entity.Position + offset, radius, delta.normalized, _castHits, delta.magnitude, ~0, QueryTriggerInteraction.Collide);
                if (count < _castHits.Length) break;
                Array.Resize(ref _castHits, _castHits.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = _castHits[i];
                if (hit.collider.GetComponentInParent<PrototypePlayer>() != null) continue;
                var candidate = hit.collider.GetComponentInParent<SubWeaponHitProxy>();
                if (candidate != null)
                {
                    if (candidate.EntityId == entity.Id || candidate.Team == entity.Team) continue;
                    if (hit.distance < nearest) { nearest = hit.distance; result = hit; proxy = candidate; }
                    continue;
                }
                if (hit.collider.isTrigger) continue;
                if (hit.distance < nearest) { nearest = hit.distance; result = hit; proxy = null; }
            }
            return nearest < float.PositiveInfinity;
        }

        public bool Damage(uint id, byte attackerTeam, float amount)
        {
            int index = _entities.FindIndex(e => e.Id == id);
            if (index < 0 || amount <= 0) return false;
            var entity = _entities[index];
            if (entity.Team == attackerTeam || !_configs.TryGetValue(id, out var config) || config.MaxHealth <= 0) return false;
            entity.Health -= amount;
            if (entity.Health > 0) { _entities[index] = entity; return true; }
            return RemoveAt(index, SubWeaponEventType.Destroyed, _match.NetworkManager.ServerTime.Time);
        }

        bool Explode(uint id, double now)
        {
            int index = _entities.FindIndex(e => e.Id == id);
            if (index < 0 || !_configs.TryGetValue(id, out var config)) return false;
            var entity = _entities[index];
            foreach (var player in _match.Players)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                Vector3 target = state.Position + Vector3.up * .8f, delta = target - entity.Position;
                if (state.Health <= 0 || state.Team == entity.Team || delta.magnitude > config.ExplosionRadius || Occluded(entity.Position, target, entity.Team)) continue;
                player.ReceiveDamage(entity.Team, config.Damage, delta.normalized, entity.OwnerId);
            }
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                var other = _entities[i];
                if (other.Id == id || other.Team == entity.Team || !_configs.TryGetValue(other.Id, out var otherConfig) || otherConfig.MaxHealth <= 0 ||
                    Vector3.Distance(other.Position, entity.Position) > config.ExplosionRadius || Occluded(entity.Position, other.Position, entity.Team)) continue;
                other.Health -= config.Damage;
                if (other.Health <= 0) RemoveAt(i, SubWeaponEventType.Destroyed, now); else _entities[i] = other;
            }
            PaintExplosion(entity, config);
            index = _entities.FindIndex(e => e.Id == id);
            return index >= 0 && RemoveAt(index, SubWeaponEventType.Exploded, now);
        }

        void PaintExplosion(SubWeaponEntityState entity, SubWeaponRuntimeConfig config)
        {
            if (config.PaintRadius <= 0) return;
            for (int i = 0; i < 17; i++)
            {
                float radius = i == 0 ? 0 : config.ExplosionRadius * ((i - 1) / 8 + 1) * .5f;
                float angle = (i - 1) * Mathf.PI / 4;
                Vector3 point = entity.Position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                if (i > 0 && Occluded(entity.Position + Vector3.up * .1f, point + Vector3.up * .1f, entity.Team)) continue;
                PaintAt(entity, config, point + Vector3.up * (config.ExplosionRadius + .5f), Vector3.down, Mathf.Max(.25f, config.PaintRadius / 3));
            }
        }

        bool Occluded(Vector3 origin, Vector3 target, byte attackingTeam)
        {
            Vector3 delta = target - origin;
            if (delta.sqrMagnitude < .0001f) return false;
            int count;
            do
            {
                count = Physics.RaycastNonAlloc(origin, delta.normalized, _occlusionHits, delta.magnitude - .02f, ~0, QueryTriggerInteraction.Collide);
                if (count < _occlusionHits.Length) break;
                Array.Resize(ref _occlusionHits, _occlusionHits.Length * 2);
            } while (true);
            for (int i = 0; i < count; i++)
            {
                var hit = _occlusionHits[i];
                if (hit.collider.GetComponentInParent<PrototypePlayer>() != null) continue;
                var proxy = hit.collider.GetComponentInParent<SubWeaponHitProxy>();
                if (proxy != null) { if (proxy.Team != attackingTeam && proxy.Kind == SubWeaponKind.InkCurtain) return true; continue; }
                return true;
            }
            return false;
        }

        void PaintAt(SubWeaponEntityState entity, SubWeaponRuntimeConfig config, Vector3 origin, Vector3 direction, float radius)
        {
            if (radius <= 0 || !Physics.Raycast(origin, direction, out var hit, Mathf.Max(1, config.PaintRadius + .5f), PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return;
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            if (surface != null) _match.Paint(surface, hit.point, hit.normal, radius, entity.Team, config.PaintHardness, config.PaintStrength,
                InkShapeAtlas.Hash(entity.Id ^ (uint)Mathf.RoundToInt((float)(entity.NextEffectAt * 60))));
        }

        void PaintBoostTrails(double now)
        {
            foreach (var player in _match.Players)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                if (now >= state.SubWeaponMoveUntil || state.Health <= 0) continue;
                if (!Physics.Raycast(state.Position + Vector3.up * .5f, Vector3.down, out var hit, 1.5f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) continue;
                var surface = hit.collider.GetComponentInParent<PaintSurface>();
                if (surface != null) _match.Paint(surface, hit.point, hit.normal, .45f, state.Team, .5f, 1);
            }
        }

        void PruneOwners(double now)
        {
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                var entity = _entities[i];
                PrototypePlayer owner = null;
                for (int j = 0; j < _match.Players.Count; j++)
                {
                    var candidate = _match.Players[j];
                    if (candidate != null && candidate.IsSpawned && candidate.PlayerId == entity.OwnerId) { owner = candidate; break; }
                }
                if (owner == null || owner.Snapshot.Value.Team != entity.Team || owner.Snapshot.Value.HeroRevision != entity.OwnerHeroRevision)
                    RemoveAt(i, SubWeaponEventType.Recalled, now);
            }
        }

        void RefreshCounts()
        {
            foreach (var player in _match.Players)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                byte count = (byte)Mathf.Min(255, OwnedCount(player.PlayerId));
                if (state.ActiveSubWeapons == count) continue;
                state.ActiveSubWeapons = count; player.Snapshot.Value = state;
            }
        }

        int OwnedCount(ulong ownerId)
        {
            int count = 0;
            for (int i = 0; i < _entities.Count; i++) if (_entities[i].OwnerId == ownerId) count++;
            return count;
        }

        int OldestOwnedIndex(ulong ownerId)
        {
            int oldest = -1;
            for (int i = 0; i < _entities.Count; i++)
            {
                var candidate = _entities[i];
                if (candidate.OwnerId != ownerId) continue;
                if (oldest < 0 || candidate.SpawnedAt < _entities[oldest].SpawnedAt ||
                    candidate.SpawnedAt == _entities[oldest].SpawnedAt && candidate.Id < _entities[oldest].Id) oldest = i;
            }
            return oldest;
        }

        public void RemoveOwner(ulong ownerId)
        { for (int i = _entities.Count - 1; i >= 0; i--) if (_entities[i].OwnerId == ownerId) RemoveAt(i, SubWeaponEventType.Recalled, 0); }
        public void Clear()
        { _entities.Clear(); _configs.Clear(); _events.Clear(); SubWeaponPresentation.Current?.Clear(); }
        public void CopyStates(List<SubWeaponEntityState> target) { target.Clear(); target.AddRange(_entities); }

        bool Remove(uint id, SubWeaponEventType type, double now)
        { int index = _entities.FindIndex(e => e.Id == id); return index >= 0 && RemoveAt(index, type, now); }
        bool RemoveAt(int index, SubWeaponEventType type, double now)
        {
            if (index < 0 || index >= _entities.Count) return false;
            var entity = _entities[index]; _events.Add(Event(entity, type, now));
            _configs.Remove(entity.Id); _entities.RemoveAt(index);
            return true;
        }
        static SubWeaponEvent Event(SubWeaponEntityState e, SubWeaponEventType type, double now) => new()
        { Id = e.Id, Round = e.Round, Type = type, Kind = e.Kind, Team = e.Team, Position = e.Position, Normal = e.Normal, Time = now };
    }
}
