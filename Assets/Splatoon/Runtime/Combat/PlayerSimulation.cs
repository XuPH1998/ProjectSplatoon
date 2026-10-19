using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Splatoon.Blocks;

namespace Splatoon.Combat
{
    public enum MovementMode : byte { Human, Air, GroundInk, WallInk, Mantle, Dead, Bubble, Reefslider }
    public enum SwimSurface : byte { None, Friendly, Neutral }
    public enum WeaponPhase : byte { Idle, Starting, Firing, Ending, Charging, BurstCooldown }

    public static class ResourceSimulation
    {
        public static void Step(ref PlayerSnapshot s, cfg.HeroConfig c, bool enemyInk, bool triggerHeld, float dt, double now)
        {
            if (s.Health <= 0) return;
            // Block ownership restricts traversal; standing on an enemy block is not a damage source.
            if (PlayerMotorSimulation.BlockTraversalEnabled) enemyInk = false;
            bool inkRecovery = s.HasInkRecovery && !enemyInk;
            if (now + 1e-8 >= s.InkRecoverAt && s.SubPhase == SubWeaponPhase.Idle && now >= s.MistUntil && (!triggerHeld || s.Swimming) && s.WeaponPhase == WeaponPhase.Idle)
                s.Ink = PrototypeRules.Recover(s.Ink, c.MaxInk, inkRecovery ? c.SwimRecoverInk : c.RecoverInk, dt);
            bool rain=now<s.RainRecoveryUntil;
            bool reef=SpecialWeaponSimulation.Active(s)&&s.SpecialWeaponId>0&&SpecialWeaponSimulation.Invincible(s,SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId),now);
            if (enemyInk&&!reef)
            {
                if (now >= s.ProtectedUntil) s.Health -= Mathf.Min(Mathf.Max(0, s.Health - c.EnemyInkHealthFloor), c.EnemyInkDamageRate * dt);
                s.LastDamageAt = now;
            }
            else if (now - s.LastDamageAt + 1e-8 >= c.HealthRecoverDelay)
                s.Health = Mathf.Min(c.MaxHealth, s.Health + (inkRecovery || rain ? c.SwimHealthRecoverRate : c.HealthRecoverRate) * dt);
        }
    }

    /// <summary>The same collision/movement rules run on the authority and the predicting owner.</summary>
    public sealed partial class PlayerMotorSimulation
    {
        static readonly Unity.Profiling.ProfilerMarker MotorCanStandCpu = new("Splatoon.Player.Motor.CanStand");
        static readonly Unity.Profiling.ProfilerMarker MotorStepMovementCpu = new("Splatoon.Player.Motor.StepMovement");
        static readonly Unity.Profiling.ProfilerMarker MotorResolveGroundCpu = new("Splatoon.Player.Motor.ResolveGround");
        static readonly Unity.Profiling.ProfilerMarker MotorResolveSupportCpu = new("Splatoon.Player.Motor.ResolveSupport");
        static readonly Unity.Profiling.ProfilerMarker MotorMapGroundCpu = new("Splatoon.Player.Motor.TryGetMapGround");
        static readonly Unity.Profiling.ProfilerMarker MotorWallGeometryCpu = new("Splatoon.Player.Motor.WallGeometryFits");
        public readonly struct TraversalContact
        {
            public readonly byte Owner;
            public readonly Vector3 Point, Normal;
            public readonly bool Climbable, IsBlock, Stable;
            public readonly int SurfaceId, RegionId;
            public readonly Vector3Int Cell;
            public readonly uint Revision;
            public readonly BlockContactKind Kind;
            public readonly BlockSurfaceKey SurfaceKey;
            public readonly BlockFixedGridHandle FixedHandle;
            public TraversalContact(InkContact contact)
            {
                Owner = contact.Owner; Point = contact.Point; Normal = contact.Normal;
                Climbable = contact.Climbable; IsBlock = false; Stable = true;
                SurfaceId = contact.Surface.SurfaceId; RegionId = contact.Region.Id;
                Cell = default; Revision = 0; Kind = BlockContactKind.Legacy; SurfaceKey = default; FixedHandle = default;
            }
            public TraversalContact(BlockContact contact)
            {
                Owner = contact.Owner; Point = contact.Point; Normal = contact.Normal;
                Climbable = contact.Stable && contact.Climbable; IsBlock = true; Stable = contact.Stable;
                SurfaceId = RegionId = 0; Cell = contact.Cell; Revision = contact.Revision;
                Kind = contact.Kind; SurfaceKey = contact.SurfaceKey; FixedHandle = contact.FixedHandle;
            }
            public TraversalContact(RaycastHit hit, bool allowWall = false)
            {
                Owner = 0; Point = hit.point; Normal = hit.normal;
                Climbable = allowWall && Mathf.Abs(hit.normal.y) < .7f; IsBlock = false; Stable = true;
                SurfaceId = RegionId = 0; Cell = default; Revision = 0;
                Kind = BlockContactKind.Legacy; SurfaceKey = default; FixedHandle = default;
            }
        }
        public static bool BlockTraversalEnabled => BlockTerrain.Current != null && BlockTerrain.Current.Initialized;
        // Attached surface blocks carry gameplay ownership, while the authored
        // map remains the character's physical floor/wall. Free blocks stay solid.
        public const int WorldMask = ~((1 << 8) | (1 << SwimBody.HitProxyLayer) | (1 << SubWeaponTarget.Layer) | (1 << BlockTerrain.TerrainLayer));
        readonly CharacterController _controller;
        readonly PrototypeArena _arena;
        PrototypeArena Arena => _arena != null ? _arena : PrototypeArena.Current;
        readonly Transform _root;
        HeroBodyShape _shape;
        float CompactCenterY => _shape.CompactHeight * .5f;
        readonly Collider[] _overlap = new Collider[32];
        PaperBodyProfile PaperProfile => _root.GetComponent<PrototypePlayer>()?.SwimBody?.Profile;
        public static Vector3 HumanPosition(PlayerSnapshot s) => s.Position - Vector3.up * s.AirHumanOffset;
        public PlayerMotorSimulation(CharacterController controller, PrototypeArena arena = null)
        { _controller = controller; _arena = arena; _root = controller.transform; _shape = new HeroBodyShape(controller); }
        public void BindBody(int heroId) => _shape = HeroBodyShape.For(heroId);
        public bool CanFitHero(int heroId, Vector3 feet) => HeroBodyShape.For(heroId).Fits(feet, _overlap, WorldMask) && FixedShapeFits(HeroBodyShape.For(heroId), feet);
        public void Restore(PlayerSnapshot s, bool updateHitGeometry = true)
        {
            BindBody(s.HeroId);
            _controller.enabled = false; _root.position = s.Position;
            SetShape(s.Swimming || s.CompactBody); _controller.enabled = s.Health > 0;
            if (updateHitGeometry) _root.GetComponent<PrototypePlayer>()?.SwimBody?.ApplyCollision(s);
            Physics.SyncTransforms();
        }
        void SetShape(bool ink) => _shape.Apply(_controller, ink);
        void Rebase(ref PlayerSnapshot s, float height)
        {
            if (Mathf.Abs(height) < .00001f) return;
            // Coordinate conversion, not movement: do not create velocity or
            // sweep the old form through the space occupied by the new form.
            bool enabled = _controller.enabled;
            _controller.enabled = false;
            s.Position += Vector3.up * height; _root.position = s.Position;
            _controller.enabled = enabled;
            s.CameraRebaseOffset -= height;
        }
        void EnterAirPaper(ref PlayerSnapshot s, bool hadPaper)
        {
            var profile = PaperProfile;
            float surfaceOffset = profile != null ? profile.SurfaceOffset : .03f;
            float center = hadPaper && s.PaperPose != PaperPose.None ? s.PaperCenter.y
                : s.Position.y + (profile != null ? profile.Size.y : 1.8f) / 2;
            float shift = center - s.Position.y - surfaceOffset;
            var traceBefore = MotorTracing ? s : default;
            Rebase(ref s, shift);
            s.AirHumanOffset += shift;
            TraceMotor(MotorTraceBranch.AirEntry, traceBefore, s, rebaseHeight: shift);
        }
        public bool CanStand(Vector3 position)
        {
            using var timing = MotorCanStandCpu.Auto();
            return _shape.Fits(position, _overlap, WorldMask) && FixedShapeFits(_shape, position);
        }
        public static bool Query(Vector3 origin, Vector3 direction, float distance, out TraversalContact contact)
        {
            bool mapHit = Physics.Raycast(origin, direction, out var hit, distance, WorldMask, QueryTriggerInteraction.Ignore);
            if (FixedGridActive && BlockTerrain.Current.TryCastFixed(origin, direction, distance, 0, out var fixedHit) &&
                (!mapHit || fixedHit.Distance <= hit.distance))
            { contact = new TraversalContact(BlockTerrain.FixedContact(fixedHit)); return true; }
            if (mapHit)
            {
                if (BlockTraversalEnabled)
                    return TryBlockTraversalContact(hit, out contact);
                var surface = hit.collider.GetComponentInParent<PaintSurface>();
                if (surface != null && surface.QueryRegion(hit.point, hit.normal, out var ink))
                { contact = new TraversalContact(ink); return true; }
            }
            contact = default; return false;
        }
        internal static bool TryBlockTraversalContact(RaycastHit hit, out TraversalContact contact)
        {
            var terrain = BlockTerrain.Current;
            if (hit.collider == null)
            { contact = default; return false; }
            var looseBody = hit.collider.GetComponentInParent<BlockDynamicBody>();
            if (looseBody != null)
            {
                // A loose body can physically support a human capsule, but never
                // supplies ink recovery, wall attachment or neutral swimming.
                if (!looseBody.TryContact(hit.point, hit.normal, out var dynamicContact))
                { contact = default; return false; }
                contact = new TraversalContact(dynamicContact);
                return true;
            }
            if (hit.collider.gameObject.layer == PlayerBlockPushBridge.DynamicLayer)
            { contact = default; return false; }
            if (terrain.TrySurfaceContactFromMap(hit, out var block) || terrain.TryContact(hit, out block))
            { contact = new TraversalContact(block); return true; }
            // A failed voxel lookup is not bare map geometry. In particular, an
            // empty cell in a published terrain collider cannot grant attachment.
            if (BlockTerrain.IsBlockGeometry(hit.collider))
            { contact = default; return false; }
            contact = new TraversalContact(hit, true); return true;
        }
        public static bool TryGetGround(Vector3 feet, out TraversalContact contact, float slopeLimit = 45, float footprint = .35f)
            => TryGetGround(feet, out contact, out _, slopeLimit, footprint);
        public static bool TryGetGround(Vector3 feet, out TraversalContact contact, out GroundQueryDetails details, float slopeLimit = 45, float footprint = .35f)
        {
            bool map = TryGetMapGround(feet, out contact, out details, slopeLimit, footprint);
            if (FixedGridActive && BlockFixedGridQueries.Support(BlockTerrain.Current.FixedBlocks, feet, Mathf.Max(0, footprint), .08001f,
                out var fixedHit, Mathf.Cos(slopeLimit * Mathf.Deg2Rad)) && (!map || fixedHit.Point.y >= contact.Point.y - .001f))
            {
                contact = new TraversalContact(BlockTerrain.FixedContact(fixedHit));
                details.EvaluatedPaths |= GroundEvaluated.FixedGrid; details.Winner = GroundWinner.FixedGrid;
                return true;
            }
            return map;
        }
        static bool TryGetMapGround(Vector3 feet, out TraversalContact contact, out GroundQueryDetails details, float slopeLimit, float footprint)
        {
            using var timing = MotorMapGroundCpu.Auto();
            contact = default;
            bool blocks = BlockTraversalEnabled;
            details = new GroundQueryDetails { Evaluated = true, BlockTraversal = blocks, Feet = feet, SlopeLimit = slopeLimit, Footprint = footprint,
                EvaluatedPaths = GroundEvaluated.Center };
            if (!GroundHit(feet, slopeLimit, out var hit))
            {
                // Preserve the exact original centre/cardinal priority and physics calls.
                if (!blocks) return false;
                if (!FootprintHit(feet, slopeLimit, Mathf.Max(0, footprint) * .8f, out hit, ref details))
                    return SphereFootprintGround(feet, slopeLimit, Mathf.Max(0, footprint), out contact, ref details);
            }
            else details.Winner = GroundWinner.Center;
            if (blocks)
                return TryBlockTraversalContact(hit, out contact);
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            contact = surface != null && surface.QueryRegion(hit.point, hit.normal, out var ink)
                ? new TraversalContact(ink) : new TraversalContact(hit);
            return true;
        }
        static bool GroundHit(Vector3 feet, float slopeLimit, out RaycastHit hit) =>
            Physics.Raycast(feet + Vector3.up * .08f, Vector3.down, out hit, .16f, WorldMask, QueryTriggerInteraction.Ignore) &&
            hit.normal.y >= Mathf.Cos(slopeLimit * Mathf.Deg2Rad);
        static bool FootprintHit(Vector3 feet, float slopeLimit, float radius, out RaycastHit hit, ref GroundQueryDetails details)
        {
            hit = default;
            details.EvaluatedPaths |= GroundEvaluated.Right;
            if (GroundHit(feet + Vector3.right * radius, slopeLimit, out hit)) { details.Winner = GroundWinner.Right; return true; }
            details.EvaluatedPaths |= GroundEvaluated.Left;
            if (GroundHit(feet - Vector3.right * radius, slopeLimit, out hit)) { details.Winner = GroundWinner.Left; return true; }
            details.EvaluatedPaths |= GroundEvaluated.Forward;
            if (GroundHit(feet + Vector3.forward * radius, slopeLimit, out hit)) { details.Winner = GroundWinner.Forward; return true; }
            details.EvaluatedPaths |= GroundEvaluated.Back;
            if (GroundHit(feet - Vector3.forward * radius, slopeLimit, out hit)) { details.Winner = GroundWinner.Back; return true; }
            return false;
        }
        public static bool IsEnemy(byte owner, byte team) => owner != 0 && owner != 255 && owner != team;
        public static bool CanClimb(TraversalContact contact, byte team) => contact.Climbable &&
            (BlockTraversalEnabled ? CanUseBlockTraversalSurface(contact, team) : contact.Owner == 0 || contact.Owner == team);
        static bool CanUseBlockTraversalSurface(TraversalContact contact, byte team) => contact.Stable &&
            (contact.IsBlock ? contact.Owner == team && team != 0 && team != 255 : contact.Owner == 0);
        static bool CanSwimOnGround(TraversalContact contact, byte team) => BlockTraversalEnabled
            ? CanUseBlockTraversalSurface(contact, team) : !IsEnemy(contact.Owner, team);
        static float WallSpeed(SwimSurface source, cfg.HeroConfig hero) => source == SwimSurface.Neutral ? hero.NeutralSwimSpeed : hero.WallSwimSpeed;
        public void Step(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, bool wantsFire, float shootMoveSpeed = -1, bool shootingMovement = false)
        {
            using var timing = FixedMotorCpu.Auto();
            BindBody(s.HeroId);
            var blockPush = BeginBlockMovement(dt);
            bool committed = false;
            try
            {
                // Committed recovery survives trigger release, cancellation and UI focus changes.
                if (WeaponSimulation.RecoveryLocked(s, now) || now < s.SubRecoveryUntil) { input.Swim = false; shootingMovement = true; }
                if (now + 1e-8 < s.AttackMoveUntil) shootingMovement = true;
                BeginMotorTrace(input, wantsFire, shootingMovement);
                BeginMotorDetails(s);
                var previous = s;
                s.CameraRebaseOffset *= Mathf.Exp(-18 * dt);
                StepMovement(ref s, input, dt, now, wantsFire, shootMoveSpeed, shootingMovement);
                CompleteTraversalFrame(ref s, previous, dt, now);
                ValidateCompletedTraversal(ref s, previous, input, dt, now, wantsFire, shootMoveSpeed, shootingMovement);
                TraceMotor(MotorTraceBranch.StepComplete, previous, s);
                EndMotorDetails();
                committed = true;
            }
            finally { blockPush.EndStep(committed); }
        }

        void CompleteTraversalFrame(ref PlayerSnapshot s, PlayerSnapshot previous, float dt, double now)
        {
            ResolveSupport(ref s, previous, dt,
                out var groundEvaluated, out var groundFound, out var ground,
                out var wallEvaluated, out var wallFound, out var wall);
            // Traversal source may survive takeoff or seam grace; recovery requires fresh contact.
            s.FriendlyInkContact = HasFriendlyInkContactFromSupport(s,
                groundEvaluated, groundFound, ground, wallEvaluated, wallFound, wall);
            var profile = PaperProfile;
            PaperPoseSimulation.Resolve(ref s, previous, profile, now);
            PaperAnimation.Step(ref s, previous, dt, profile);
        }

        void ValidateCompletedTraversal(ref PlayerSnapshot s, PlayerSnapshot previous, PlayerInputFrame input,
            float dt, double now, bool wantsFire, float shootMoveSpeed, bool shootingMovement)
        {
            if (!BlockTraversalEnabled || s.Health <= 0 || !s.ShowsSwimBody || PaperProfile == null || WallGeometryFits(s)) return;
            // Animation, pose and physical motion form one transaction. A blocked
            // final contour cannot be committed merely because its earlier key fit.
            if (TryRetainSafeWallFrame(ref s, previous, input, dt, now, wantsFire)) return;
            if (TryAttachCompletedHorizontalFrame(ref s, previous, input, dt, now, wantsFire)) return;

            // Undo the entire provisional movement/origin conversion and execute
            // the ordinary unattached path once from the exact motor entry. The
            // first attempt has not run resources, weapons or live publication.
            RollbackWallMove(ref s, previous, previous.Position);
            s.CameraRebaseOffset *= Mathf.Exp(-18 * dt);
            StepMovement(ref s, input, dt, now, wantsFire, shootMoveSpeed, shootingMovement, false);
            CompleteTraversalFrame(ref s, previous, dt, now);
            if (WallGeometryFits(s)) return;

            var player = _root.GetComponent<PrototypePlayer>();
            if (!BlockTerrain.Current.TryRecoverPublishedTraversalPose(player, s, previous, out var recovered))
            {
                // A malformed map with neither local clearance nor a safe team
                // spawn has no valid state to commit. Do not publish an embedded
                // frame as a successful step or silently preserve a stale hitmesh.
                RollbackWallMove(ref s, previous, previous.Position);
                throw new InvalidOperationException("无法为积木纸片移动找到已验证的安全位置；本次模拟结果未提交。");
            }
            s = recovered;
            Restore(s, false); // Exactly the verified snapshot; no second animation advance.
        }

        bool TryAttachCompletedHorizontalFrame(ref PlayerSnapshot s, PlayerSnapshot previous, PlayerInputFrame input,
            float dt, double now, bool wantsFire)
        {
            var profile = PaperProfile;
            // Called only after this completed horizontal candidate failed full
            // geometry validation. Keep ordinary probe range and same-tick wall
            // detach/mantle cancellation semantics unchanged.
            if (profile == null || !input.Swim || wantsFire || input.Move.y <= 0 ||
                input.JumpSequence != previous.ConsumedJump ||
                previous.Movement == MovementMode.WallInk || previous.Movement == MovementMode.Mantle ||
                (s.PaperPose != PaperPose.Ground && s.PaperPose != PaperPose.Air) ||
                Vector3.Dot(s.PaperRotation * Vector3.forward, Vector3.up) < .65f) return false;

            var player = _root.GetComponent<PrototypePlayer>();
            Vector3 forward = Quaternion.Euler(0, s.Yaw, 0) * Vector3.forward;
            if (!BlockTerrain.Current.TryQueryObstructingTraversalWall(player, s,
                s.PaperCenter, forward,
                GameplayConfig.GetHero(s.HeroId).WallProbeDistance, _shape.SkinWidth, out var wall) ||
                !CanClimb(wall, s.Team)) return false;

            var completed = s; Vector3 rootBefore = _root.position;
            bool hasFootSupport = s.VerticalSpeed <= 0 && TryGetGround(s.Position, out _, _controller.slopeLimit, _controller.radius);
            var candidate = s;
            if (!TryAttach(ref candidate, wall, dt, hasFootSupport))
            {
                RollbackWallMove(ref s, completed, rootBefore); return false;
            }
            candidate.WallSeenAt = now; candidate.Movement = MovementMode.WallInk;
            candidate.Swimming = true; candidate.Grounded = false; candidate.VerticalSpeed = 0; candidate.PlanarVelocity = Vector3.zero;
            candidate.CompactBody = false; candidate.AirHumanOffset = 0;
            // Re-derive pose and animation from the same original motor input.
            // The provisional completed frame's animation increment is replaced,
            // never added twice; resources/weapons have not run here.
            CompleteTraversalFrame(ref candidate, previous, dt, now);
            if (!WallGeometryFits(candidate))
            {
                RollbackWallMove(ref s, completed, rootBefore); return false;
            }
            s = candidate; return true;
        }

        bool TryRetainSafeWallFrame(ref PlayerSnapshot s, PlayerSnapshot previous, PlayerInputFrame input,
            float dt, double now, bool wantsFire)
        {
            if (previous.Movement != MovementMode.WallInk || previous.PaperPose != PaperPose.Wall || !previous.ShowsSwimBody ||
                (s.Movement != MovementMode.WallInk && s.Movement != MovementMode.Mantle) ||
                !input.Swim || wantsFire || input.JumpSequence != previous.ConsumedJump) return false;
            if (!Query(previous.Position + Vector3.up * CompactCenterY, -previous.WallNormal,
                _controller.radius + .065f, out var wall) || !CanClimb(wall, previous.Team) ||
                Vector3.Dot(wall.Normal, previous.WallNormal) < .99f) return false;

            // previous already has this invocation's tick/time. Preserve the whole
            // safe paper frame, including its serialized animation key, while
            // consuming ordinary input and applying camera damping exactly once.
            var retained = previous;
            retained.Yaw = s.Yaw; retained.Pitch = s.Pitch; retained.ConsumedJump = s.ConsumedJump;
            retained.CameraRebaseOffset *= Mathf.Exp(-18 * dt);
            retained.Velocity = retained.PlanarVelocity = Vector3.zero; retained.VerticalSpeed = 0;
            retained.TurnDirection = 0; retained.WallSeenAt = now;
            BindWall(ref retained, wall);
            ResolveSupport(ref retained, previous, dt,
                out var groundEvaluated, out var groundFound, out var ground,
                out var wallEvaluated, out var wallFound, out var supportWall);
            retained.FriendlyInkContact = HasFriendlyInkContactFromSupport(retained,
                groundEvaluated, groundFound, ground, wallEvaluated, wallFound, supportWall);
            if ((wall.IsBlock && (!retained.FriendlyInkContact || retained.SupportTerrainRevision == 0)) || !WallGeometryFits(retained)) return false;
            s = retained;
            Restore(s, false);
            return true;
        }

        void ResolveSupport(ref PlayerSnapshot s, PlayerSnapshot previous, float dt,
            out bool groundEvaluated, out bool groundFound, out TraversalContact ground,
            out bool wallEvaluated, out bool wallFound, out TraversalContact wall)
        {
            using var timing = MotorResolveSupportCpu.Auto();
            groundEvaluated = groundFound = wallEvaluated = wallFound = false;
            ground = wall = default;
            s.SupportCell = default; s.SupportTerrainRevision = 0; s.GroundNormal = Vector3.up;
            s.SupportIsFixedGrid = false; s.SupportFixedId = 0; s.SupportFixedGeneration = 0;
            var fixedWorld = FixedGridActive ? BlockTerrain.Current.FixedBlocks : null;
            s.FixedBlockRevision = fixedWorld != null ? fixedWorld.Revision : 0;
            s.FixedBlockEpoch = fixedWorld != null ? fixedWorld.Epoch : 0;
            s.FixedBlockRound = fixedWorld != null ? fixedWorld.Round : 0;
            s.SupportIsSurfacePaint = false; s.SupportSurfaceFace = s.SupportSurfaceU = s.SupportSurfaceV = 0;
            if (!BlockTraversalEnabled || s.Health <= 0) return;
            if (s.Movement == MovementMode.WallInk)
            {
                wallEvaluated = true;
                wallFound = Query(s.Position + Vector3.up * CompactCenterY,
                    -s.WallNormal, _controller.radius + .065f, out wall);
                if (wallFound && CanClimb(wall, s.Team))
                {
                    if (wall.IsBlock) SetSupport(ref s, wall);
                    s.GroundNormal = wall.Normal;
                    return;
                }
            }
            if (!s.Grounded) return;
            groundEvaluated = true;
            groundFound = TryGetGround(s.Position, out ground, _controller.slopeLimit, _controller.radius);
            if (!groundFound || !ground.IsBlock || !ground.Stable) return;
            SetSupport(ref s, ground);
            Vector3 normal = ground.Normal;
            if (s.Swimming && ground.Owner == s.Team)
                normal = BlockTerrain.Current.QueryOrientation(ground.Point, ground.Normal, s.Team, _controller.radius);
            // The same simulated frame drives the display and weapon hit geometry.
            bool continuous = previous.Grounded && previous.Movement == MovementMode.GroundInk &&
                previous.SupportTerrainRevision != 0 && previous.GroundNormal.y >= Mathf.Cos(_controller.slopeLimit * Mathf.Deg2Rad);
            s.GroundNormal = continuous ? Vector3.Slerp(previous.GroundNormal, normal, 1 - Mathf.Exp(-18 * dt)).normalized : normal;
        }

        static void SetSupport(ref PlayerSnapshot state, TraversalContact contact)
        {
            state.SupportCell = contact.Cell; state.SupportTerrainRevision = contact.Revision;
            state.SupportIsFixedGrid = contact.Kind == BlockContactKind.FixedGrid;
            state.SupportFixedId = state.SupportIsFixedGrid ? contact.FixedHandle.Id : 0;
            state.SupportFixedGeneration = state.SupportIsFixedGrid ? contact.FixedHandle.Generation : 0;
            state.SupportIsSurfacePaint = contact.Kind == BlockContactKind.SurfacePaint;
            state.SupportSurfaceFace = state.SupportIsSurfacePaint ? contact.SurfaceKey.FaceId : 0;
            state.SupportSurfaceU = state.SupportIsSurfacePaint ? contact.SurfaceKey.U : 0;
            state.SupportSurfaceV = state.SupportIsSurfacePaint ? contact.SurfaceKey.V : 0;
        }

        bool HasFriendlyInkContact(PlayerSnapshot s)
            => HasFriendlyInkContactFromSupport(s, false, false, default, false, false, default);

        bool HasFriendlyInkContactFromSupport(PlayerSnapshot s,
            bool groundEvaluated, bool groundFound, TraversalContact ground,
            bool wallEvaluated, bool wallFound, TraversalContact wall)
        {
            if (s.Health <= 0 || !s.Swimming || s.Team == 0 || s.Team == 255) return false;
            if (s.Movement == MovementMode.GroundInk && s.Grounded)
                return (groundEvaluated ? groundFound : TryGetGround(s.Position, out ground, _controller.slopeLimit, _controller.radius)) &&
                    ground.Owner == s.Team && ground.Stable;
            if (s.Movement != MovementMode.WallInk) return false;
            return (wallEvaluated ? wallFound : Query(s.Position + Vector3.up * CompactCenterY,
                -s.WallNormal, _controller.radius + .065f, out wall)) &&
                wall.Owner == s.Team && wall.Climbable && Vector3.Dot(wall.Normal, s.WallNormal) > .99f;
        }

        void StepMovement(ref PlayerSnapshot s, PlayerInputFrame input, float dt, double now, bool wantsFire, float shootMoveSpeed, bool shootingMovement, bool allowWallTraversal = true)
        {
            using var timing = MotorStepMovementCpu.Auto();
            var c = GameplayConfig.GetHero(s.HeroId);
            var weapon = GameplayConfig.GetWeapon(s.HeroId);
            if (s.Health <= 0) { StepDead(ref s, dt); return; }
            s.Yaw = input.Look.x; s.Pitch = input.Look.y;
            bool jump = input.JumpSequence != s.ConsumedJump; s.ConsumedJump = input.JumpSequence;
            bool groundContact = TryGetGround(s.Position, out var floorContact, out var entryGroundDetails, _controller.slopeLimit, _controller.radius);
            TraceMotor(MotorTraceBranch.EntryGround, s, s, probeEvaluated: true, probeSucceeded: groundContact, contact: floorContact);
            EmitMotorDetails(MotorDetailKind.GroundQuery, MotorTraceBranch.EntryGround, ground: entryGroundDetails);
            byte floor = groundContact ? floorContact.Owner : (byte)255;
            bool grounded = s.VerticalSpeed <= 0 && s.Grounded && (BlockTraversalEnabled ? groundContact : _controller.isGrounded || groundContact);
            // A flight retains its source across human/paper switches. A fresh
            // airborne spawn (or hero change) defaults to neutral swimming.
            if (s.Movement == MovementMode.WallInk || s.Movement == MovementMode.Mantle)
                s.AirSwimSource = s.SwimSource;
            else if (!grounded && s.AirSwimSource == SwimSurface.None)
                s.AirSwimSource = s.Grounded && s.Swimming && s.SwimSource == SwimSurface.Friendly ? SwimSurface.Friendly : SwimSurface.Neutral;
            bool wasCompact = s.Swimming || s.CompactBody;
            bool wasAirPaper = wasCompact && !s.Grounded && s.Movement == MovementMode.Air;
            bool canStand = !wasCompact || CanStand(HumanPosition(s)), detached = false;
            if (s.Movement == MovementMode.Mantle)
            {
                bool lostTarget = BlockTraversalEnabled && (!TryGetGround(s.MantleTo, out var mantleGround, _controller.slopeLimit, 0) ||
                    !CanSwimOnGround(mantleGround, s.Team));
                if (!allowWallTraversal || !input.Swim || wantsFire || jump || lostTarget) s.Movement = MovementMode.Air;
                else
                {
                    float t = Mathf.Clamp01((float)(now - s.MantleStartedAt) / c.MantleSeconds);
                    var apex = new Vector3(s.MantleFrom.x, s.MantleTo.y + .15f, s.MantleFrom.z);
                    Vector3 target = t < .5f ? Vector3.Lerp(s.MantleFrom, apex, t * 2) : Vector3.Lerp(apex, s.MantleTo, (t - .5f) * 2);
                    SetShape(true); MoveWithFreeBlocks(target - _root.position, dt);
                    s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position; s.Swimming = true;
                    s.CompactBody = false;
                    s.Grounded = _controller.isGrounded || FixedGridActive && TryGetGround(s.Position, out _, _controller.slopeLimit, _controller.radius); s.TurnDirection = 0;
                    if (t < 1) return;
                    s.Movement = MovementMode.Air; s.VerticalSpeed = -2;
                    grounded = TryGetGround(s.Position, out floorContact, _controller.slopeLimit, _controller.radius);
                    floor = grounded ? floorContact.Owner : (byte)255;
                }
            }
            if (s.Movement == MovementMode.WallInk)
            {
                var wallDetails = new WallDetachDetails { Evaluated = true, BlockTraversal = BlockTraversalEnabled, JumpRequested = jump,
                    BeforeMovement = s.Movement, CurrentEvaluated = true };
                bool currentFound = Query(s.Position + Vector3.up * CompactCenterY + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out var currentWall);
                wallDetails.CurrentFound = currentFound; wallDetails.Current = new DetailContact(currentWall);
                bool blocked = currentFound ? !ObserveWallBool(CanClimb(currentWall, s.Team), ref wallDetails.CurrentClimbEvaluated, ref wallDetails.CurrentCanClimb) : BlockTraversalEnabled;
                if (currentFound && !blocked) BindWall(ref s, currentWall);
                if (!blocked && allowWallTraversal && input.Swim && !wantsFire && !jump)
                {
                    Vector3 along = Vector3.Cross(s.WallNormal, Vector3.up).normalized;
                    Vector3 direction = Vector3.up * input.Move.y + along * input.Move.x;
                    float wallSpeed = WallSpeed(s.SwimSource, c);
                    if (now < s.MistUntil) wallSpeed *= Mathf.Clamp01(s.MistMoveRate);
                    Vector3 delta = direction * wallSpeed * dt;
                    var next = s.Position + delta;
                    wallDetails.NextEvaluated = true;
                    bool found = Query(next + Vector3.up * CompactCenterY + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out var wall);
                    wallDetails.InitialNextFound = found; wallDetails.InitialNext = new DetailContact(wall);
                    if (found && ObserveWallBool(CanClimb(wall, s.Team), ref wallDetails.NeutralConditionClimbEvaluated, ref wallDetails.NeutralConditionCanClimb) && wall.Owner == 0 && wallSpeed > c.NeutralSwimSpeed)
                    {
                        delta = direction * c.NeutralSwimSpeed * dt; next = s.Position + delta;
                        wallDetails.NeutralRetryEvaluated = true;
                        found = Query(next + Vector3.up * CompactCenterY + s.WallNormal * .08f, -s.WallNormal, c.WallProbeDistance + .08f, out wall);
                    }
                    wallDetails.NextFound = found; wallDetails.Next = new DetailContact(wall);
                    if (found && ObserveWallBool(CanClimb(wall, s.Team), ref wallDetails.NextClimbEvaluated, ref wallDetails.NextCanClimb) && ObserveWallNormal(Vector3.Dot(wall.Normal, s.WallNormal), ref wallDetails))
                    {
                        var wallMoveBefore = s;
                        s.WallSeenAt = now; BindWall(ref s, wall);
                        next += s.WallNormal * (_controller.radius + .015f - Vector3.Dot(next + Vector3.up * CompactCenterY - wall.Point, s.WallNormal));
                        bool movedOnWall = MoveOnWall(ref s, next - _root.position, input, dt, wantsFire, wallMoveBefore);
                        if (movedOnWall && s.Movement == MovementMode.WallInk && input.Move.y > 0) TryMantle(ref s, now);
                        return;
                    }
                    if (!found && input.Move.y > 0 && ObserveWallBool(TryMantle(ref s, now), ref wallDetails.MantleEvaluated, ref wallDetails.MantleSucceeded)) return;
                    // Grace keeps the original query and short-circuit order.
                    SwimSurface seamSource = default;
                    if (!found && ObserveWallBool(now - s.WallSeenAt < c.WallGraceSeconds, ref wallDetails.GraceEvaluated, ref wallDetails.GraceAvailable) &&
                        ObserveWallBool(ClimbableSeam(next, s.WallNormal, s.Team, c.WallProbeDistance, out seamSource), ref wallDetails.SeamEvaluated, ref wallDetails.SeamSucceeded))
                    {
                        s.SwimSource = s.AirSwimSource = seamSource;
                        delta = direction * WallSpeed(seamSource, c) * dt;
                        MoveOnWall(ref s, delta, input, dt, wantsFire); return;
                    }
                    if (wallDetails.SeamEvaluated) wallDetails.SeamSource = seamSource;
                }
                wallDetails.Reason = !allowWallTraversal ? WallDetachReason.FinalPaperObstructed
                    : blocked ? currentFound ? WallDetachReason.CurrentNotClimbable : WallDetachReason.CurrentMissingInBlockMode
                    : !input.Swim ? WallDetachReason.SwimReleased : wantsFire ? WallDetachReason.WantsFire : jump ? WallDetachReason.JumpRequested
                    : wallDetails.NextFound ? !wallDetails.NextCanClimb ? WallDetachReason.NextNotClimbable : WallDetachReason.NextNormalMismatch
                    : WallDetachReason.NextMissingAfterRecovery;
                s.Movement = MovementMode.Air;
                detached = true;
                s.PlanarVelocity = s.WallNormal * (jump ? c.WallJumpSpeed : .8f);
                s.VerticalSpeed = jump ? c.JumpSpeed : 0;
                // Jumping preserves the ink form; a failed wall contact simply detaches.
                s.AirSwimSource = s.SwimSource;
                grounded = false;
                wallDetails.AfterMovement = s.Movement;
                EmitMotorDetails(MotorDetailKind.WallDetach, wall: wallDetails);
            }
            bool useInk = input.Swim && !wantsFire && (!grounded || CanSwimOnGround(floorContact, s.Team));
            if (useInk) s.SwimSource = grounded ? (floor == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral) : s.AirSwimSource;
            if (!useInk) s.SwimSource = SwimSurface.None;
            if (grounded) s.AirSwimSource = useInk ? s.SwimSource : SwimSurface.Neutral;
            s.Swimming = useInk;
            s.CompactBody = !useInk && wasCompact && !canStand;
            if (!useInk && !s.CompactBody && s.AirHumanOffset != 0)
            {
                var traceBeforeExit = MotorTracing ? s : default;
                Rebase(ref s, -s.AirHumanOffset);
                s.AirHumanOffset = 0;
                TraceMotor(MotorTraceBranch.AirExit, traceBeforeExit, s, rebaseHeight: -traceBeforeExit.AirHumanOffset);
            }
            SetShape(useInk || s.CompactBody);
            var aim = Quaternion.Euler(0, s.Yaw, 0);
            if (allowWallTraversal && !detached && (grounded || useInk) && !(!BlockTraversalEnabled && grounded && IsEnemy(floor, s.Team)) && input.Swim && !wantsFire && !jump && input.Move.y > 0 &&
                Query(s.Position + Vector3.up * CompactCenterY, aim * Vector3.forward, c.WallProbeDistance, out var entry) && CanClimb(entry, s.Team) &&
                TryAttach(ref s, entry, dt, groundContact && s.VerticalSpeed <= 0))
            {
                s.WallSeenAt = now; s.Movement = MovementMode.WallInk;
                s.Swimming = true; s.Grounded = false; s.VerticalSpeed = 0; s.PlanarVelocity = Vector3.zero;
                s.CompactBody = false;
                s.AirHumanOffset = 0;
                SetShape(true); return;
            }
            bool airSwim = useInk && (!grounded || jump);
            if (!grounded && (useInk || s.CompactBody) && !wasAirPaper)
                EnterAirPaper(ref s, wasCompact);
            float speed = airSwim ? c.AirSwimSpeed : useInk ? (s.SwimSource == SwimSurface.Neutral ? c.NeutralSwimSpeed : c.SwimSpeed)
                : wantsFire || shootingMovement ? (shootMoveSpeed > 0 ? shootMoveSpeed : weapon.ShootMoveSpeed) : c.MoveSpeed;
            if (!BlockTraversalEnabled && grounded && IsEnemy(floor, s.Team)) speed *= c.EnemyInkMultiplier;
            if (now < s.MistUntil) speed *= Mathf.Clamp01(s.MistMoveRate);
            var desired = aim * new Vector3(input.Move.x, 0, input.Move.y) * speed;
            s.PlanarVelocity = Vector3.MoveTowards(s.PlanarVelocity, desired, (useInk ? c.SwimAcceleration : c.MoveAcceleration) * dt);
            if (grounded && s.VerticalSpeed < 0) s.VerticalSpeed = -2;
            if (jump && grounded) s.VerticalSpeed = WeaponSimulation.IsSplatling(weapon) && wantsFire && s.SplatlingRemaining == 0 && s.WeaponPhase != WeaponPhase.Ending
                ? weapon.SplatlingChargeJumpSpeed : c.JumpSpeed;
            s.VerticalSpeed = AirSwimSimulation.VerticalSpeed(s.VerticalSpeed, airSwim, c, dt);
            var traceBeforeMove = MotorTracing ? s : default;
            var destinationCapsule = MotorDetailTracing ? CaptureMoveCapsule(_controller) : default;
            Vector3 requestedMove = (s.PlanarVelocity + Vector3.up * s.VerticalSpeed) * dt;
            var collisions = MoveWithFreeBlocks(requestedMove, dt);
            if ((collisions & CollisionFlags.Above) != 0 && s.VerticalSpeed > 0) s.VerticalSpeed = 0;
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position;
            s.Grounded = _controller.isGrounded || (collisions & CollisionFlags.Below) != 0;
            TraceMotor(MotorTraceBranch.DestinationMove, traceBeforeMove, s, collisions: collisions, requestedMove: requestedMove);
            EmitMotorDetails(MotorDetailKind.Move, MotorTraceBranch.DestinationMove, capsule: destinationCapsule);
            // Resolve the destination before resources/presentation, including landing and fresh enemy paint.
            if (s.Grounded)
            {
                ResolveGround(ref s, input, wantsFire);
                useInk = s.Swimming;
            }
            s.Movement = !s.Grounded ? MovementMode.Air : useInk ? MovementMode.GroundInk : MovementMode.Human;
            if (!s.Grounded && !useInk) s.WallSurfaceId = s.WallRegionId = 0;
        }
        bool TryAttach(ref PlayerSnapshot s, TraversalContact entry, float dt, bool hasFootSupport)
        {
            var before = s;
            Vector3 rootBefore = _root.position;
            // A fresh foot contact supplies the ground pivot even while native
            // isGrounded has not yet caught up. Unsupported air rotates at its centre.
            bool convertAirFrame = BlockTraversalEnabled && !hasFootSupport && s.Movement == MovementMode.Air && s.PaperPose == PaperPose.Air;
            float conversion = 0;
            SetShape(true);
            if (convertAirFrame)
            {
                // Air uses the sheet's support point; a wall uses its lower end.
                // Rotate around the actual sheet centre, including zero/negative
                // saved human offsets, then validate the resulting capsule.
                float height = PaperProfile != null ? PaperProfile.Size.y : 1.8f;
                conversion = s.PaperCenter.y - height * .5f - s.Position.y;
                Vector3 feet = s.Position + Vector3.up * conversion;
                float radius = Mathf.Max(.05f, _shape.Radius - .025f);
                if (Physics.CheckCapsule(feet + Vector3.up * radius,
                    feet + Vector3.up * Mathf.Max(radius, _shape.CompactHeight - radius), radius,
                    WorldMask, QueryTriggerInteraction.Ignore) ||
                    FixedGridActive && BlockTerrain.Current.FixedCapsuleOverlaps(feet + Vector3.up * (_shape.CompactHeight * .5f),
                        Mathf.Max(0, _shape.CompactHeight * .5f - radius), radius))
                {
                    TraceMotor(MotorTraceBranch.WallAttachEvaluation, before, s, probeEvaluated: true,
                        probeSucceeded: false, contact: entry, rebaseHeight: conversion);
                    EmitMotorDetails(MotorDetailKind.Move, MotorTraceBranch.WallAttachEvaluation);
                    TraceMotor(MotorTraceBranch.WallAttachResolved, before, s, probeEvaluated: true, probeSucceeded: false, contact: entry);
                    return false;
                }
                Rebase(ref s, conversion);
            }
            Vector3 physicalStart = _root.position;
            float gap = Vector3.Dot(physicalStart + Vector3.up * CompactCenterY - entry.Point, entry.Normal);
            Vector3 requested = entry.Normal * (_controller.radius + .015f - gap);
            var attachCapsule = MotorDetailTracing ? CaptureMoveCapsule(_controller) : default;
            var collisions = MoveWithFreeBlocks(requested, dt);
            // An obstacle can stop the snap. Bind only after the capsule reaches
            // the wall at its converted height, and record the actual contact.
            gap = Vector3.Dot(_root.position + Vector3.up * CompactCenterY - entry.Point, entry.Normal);
            bool found = Query(_root.position + Vector3.up * CompactCenterY + entry.Normal * .08f,
                -entry.Normal, Mathf.Max(0, gap) + .1f, out var actual);
            bool attached = gap <= _controller.radius + .065f && found && CanClimb(actual, s.Team) &&
                Vector3.Dot(actual.Normal, entry.Normal) >= .99f &&
                Vector3.Dot(_root.position + Vector3.up * CompactCenterY - actual.Point, actual.Normal) <= _controller.radius + .065f;
            if (attached)
            {
                var candidate = s;
                candidate.Position = _root.position; candidate.Movement = MovementMode.WallInk;
                candidate.Swimming = true; candidate.Grounded = false; candidate.CompactBody = false;
                candidate.AirHumanOffset = 0;
                BindWall(ref candidate, actual);
                PaperPoseSimulation.Resolve(ref candidate, before, PaperProfile, s.SimulatedAt);
                attached = WallGeometryFits(candidate);
            }
            TraceMotor(MotorTraceBranch.WallAttachEvaluation, before, s, probeEvaluated: true, probeSucceeded: attached,
                contact: actual, collisions: collisions, requestedMove: requested, rebaseHeight: conversion);
            EmitMotorDetails(MotorDetailKind.Move, MotorTraceBranch.WallAttachEvaluation, capsule: attachCapsule);
            if (!attached)
            {
                // Geometry failure rejects the entire provisional snap as well
                // as an optional air-origin conversion. Do not publish the wall frame.
                RollbackWallMove(ref s, before, rootBefore);
                TraceMotor(MotorTraceBranch.WallAttachResolved, before, s, probeEvaluated: true, probeSucceeded: false, contact: actual);
                return false;
            }
            BindWall(ref s, actual);
            s.Velocity = (_root.position - (convertAirFrame ? physicalStart : before.Position)) / dt; s.Position = _root.position;
            if (convertAirFrame) s.AirHumanOffset = 0;
            TraceMotor(MotorTraceBranch.WallAttachResolved, before, s, probeEvaluated: true, probeSucceeded: true,
                contact: actual, collisions: collisions, requestedMove: requested, rebaseHeight: conversion);
            return true;
        }
        bool WallGeometryFits(PlayerSnapshot candidate)
        {
            using var timing = MotorWallGeometryCpu.Auto();
            if (!BlockTraversalEnabled) return true;
            var player = _root.GetComponent<PrototypePlayer>();
            // An isolated controller has no authored sheet. Do not invent a
            // default rectangle around synthetic or capsule-only motor fixtures.
            if (player == null || PaperProfile == null) return true;
            return BlockTerrain.Current.PublishedTraversalPoseFits(player, candidate);
        }
        void RollbackWallMove(ref PlayerSnapshot s, PlayerSnapshot before, Vector3 rootBefore)
        {
            _blockPush?.DiscardContacts();
            bool enabled = _controller.enabled; _controller.enabled = false;
            _root.position = rootBefore; s = before; SetShape(s.Swimming || s.CompactBody);
            _controller.enabled = enabled;
        }
        bool MoveOnWall(ref PlayerSnapshot s, Vector3 delta, PlayerInputFrame input, float dt, bool wantsFire, PlayerSnapshot? rollbackState = null)
        {
            var beforeWall = rollbackState ?? s; Vector3 rootBefore = _root.position;
            var traceBeforeWall = MotorTracing ? beforeWall : default;
            SetShape(true); var wallCapsule = MotorDetailTracing ? CaptureMoveCapsule(_controller) : default;
            var collisions = MoveWithFreeBlocks(delta, dt);
            s.Velocity = (_root.position - s.Position) / dt; s.Position = _root.position;
            s.Swimming = true; s.TurnDirection = 0; s.VerticalSpeed = 0; s.PlanarVelocity = Vector3.zero;
            s.CompactBody = false;
            s.Grounded = input.Move.y < 0 && (collisions & CollisionFlags.Below) != 0;
            if (!s.Grounded)
            {
                var candidate = s;
                PaperPoseSimulation.Resolve(ref candidate, beforeWall, PaperProfile, s.SimulatedAt);
                if (!WallGeometryFits(candidate))
                {
                    RollbackWallMove(ref s, beforeWall, rootBefore);
                    // A rejected sweep did not produce motion. The stable previous
                    // attachment remains intact; held swim cannot cause reattach churn.
                    s.Velocity = Vector3.zero;
                    TraceMotor(MotorTraceBranch.WallMove, traceBeforeWall, s, collisions: collisions, requestedMove: delta);
                    EmitMotorDetails(MotorDetailKind.Move, MotorTraceBranch.WallMove, capsule: wallCapsule);
                    return false;
                }
            }
            TraceMotor(MotorTraceBranch.WallMove, traceBeforeWall, s, collisions: collisions, requestedMove: delta);
            EmitMotorDetails(MotorDetailKind.Move, MotorTraceBranch.WallMove, capsule: wallCapsule);
            if (s.Grounded) ResolveGround(ref s, input, wantsFire);
            return true;
        }
        void ResolveGround(ref PlayerSnapshot s, PlayerInputFrame input, bool wantsFire)
        {
            using var timing = MotorResolveGroundCpu.Auto();
            var traceBeforeGround = MotorTracing ? s : default;
            // The support point has now reached the floor. Standing from here
            // uses this contact, not the old human origin below the sheet.
            bool groundFound = TryGetGround(s.Position, out var ground, out var resolvedGroundDetails, _controller.slopeLimit, _controller.radius);
            TraceMotor(MotorTraceBranch.GroundEvaluation, traceBeforeGround, s, probeEvaluated: true, probeSucceeded: groundFound, contact: ground);
            EmitMotorDetails(MotorDetailKind.GroundQuery, MotorTraceBranch.GroundEvaluation, ground: resolvedGroundDetails);
            if (BlockTraversalEnabled && !groundFound)
            {
                s.Grounded = false;
                TraceMotor(MotorTraceBranch.GroundResolved, traceBeforeGround, s, probeEvaluated: true, probeSucceeded: false, contact: ground);
                return;
            }
            s.AirHumanOffset = 0;
            byte floor = ground.Owner;
            bool allowed = input.Swim && !wantsFire && CanSwimOnGround(ground, s.Team);
            bool standingEvaluated = !allowed && (s.Swimming || s.CompactBody);
            bool standingFits = standingEvaluated && CanStand(s.Position);
            s.CompactBody = standingEvaluated && !standingFits;
            s.Swimming = allowed;
            s.SwimSource = allowed ? (floor == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral) : SwimSurface.None;
            s.Movement = allowed ? MovementMode.GroundInk : MovementMode.Human;
            s.AirSwimSource = SwimSurface.None;
            s.WallSurfaceId = s.WallRegionId = 0; s.VerticalSpeed = -2;
            SetShape(allowed || s.CompactBody);
            TraceMotor(MotorTraceBranch.GroundResolved, traceBeforeGround, s, probeEvaluated: true, probeSucceeded: groundFound,
                contact: ground, standingEvaluated: standingEvaluated, standingFits: standingFits);
        }
        static void BindWall(ref PlayerSnapshot s, TraversalContact c)
        {
            s.WallSurfaceId = c.SurfaceId; s.WallRegionId = c.RegionId; s.WallNormal = c.Normal; s.WallPoint = c.Point;
            s.SwimSource = s.AirSwimSource = c.Owner == s.Team ? SwimSurface.Friendly : SwimSurface.Neutral;
        }
        bool ClimbableSeam(Vector3 position, Vector3 normal, byte team, float distance, out SwimSurface source)
        {
            source = SwimSurface.None;
            // Empty voxel cells are actual holes, never an authored coplanar seam.
            if (BlockTraversalEnabled) return false;
            Vector3 origin = position + Vector3.up * CompactCenterY + normal * .08f;
            foreach (var along in new[] { Vector3.Cross(normal, Vector3.up).normalized, Vector3.up })
            {
                // Cover both sides of the authored 8 cm seam even when the slower
                // neutral step stops near one edge instead of its centre.
                if (!Query(origin + along * .09f, -normal, distance + .08f, out var a) ||
                    !Query(origin - along * .09f, -normal, distance + .08f, out var b)) continue;
                if (CanClimb(a, team) && CanClimb(b, team) &&
                    (a.SurfaceId != b.SurfaceId || a.RegionId != b.RegionId) && Vector3.Dot(a.Normal, normal) > .999f && Vector3.Dot(b.Normal, normal) > .999f && Mathf.Abs(Vector3.Dot(a.Point - b.Point, normal)) < .01f)
                { source = a.Owner == team && b.Owner == team ? SwimSurface.Friendly : SwimSurface.Neutral; return true; }
            }
            return false;
        }
        bool TryMantle(ref PlayerSnapshot s, double now)
        {
            Vector3 probe = s.Position + Vector3.up - s.WallNormal * .6f;
            if (!Query(probe, Vector3.down, 1.05f, out var ground) || ground.Normal.y < .65f) return false;
            if (ground.Point.y < s.Position.y + .1f || ground.Point.y > s.Position.y + .95f) return false;
            Vector3 target = ground.Point + Vector3.up * .025f;
            if (BlockTraversalEnabled && !CanSwimOnGround(ground, s.Team)) return false;
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
            if (FixedGridActive && BlockFixedGridQueries.CastCapsule(BlockTerrain.Current.FixedBlocks,
                from + Vector3.up * (_shape.CompactHeight * .5f), Mathf.Max(0, _shape.CompactHeight * .5f - radius),
                radius, delta, delta.magnitude, out _)) return false;
            return !Physics.CapsuleCast(from + Vector3.up * radius, from + Vector3.up * (_shape.CompactHeight-radius), radius,
                delta.normalized, delta.magnitude, WorldMask, QueryTriggerInteraction.Ignore);
        }
        void StepDead(ref PlayerSnapshot s, float dt)
        {
            _controller.enabled = false; s.Movement = MovementMode.Dead; s.Swimming = s.Firing = false; s.WeaponPhase = WeaponPhase.Idle; s.TurnDirection = 0;
            s.SwimSource = s.AirSwimSource = SwimSurface.None; s.CompactBody = false;
            s.AirHumanOffset = 0;
            s.VerticalSpeed -= GameplayConfig.GetHero(s.HeroId).CharacterGravity * dt;
            Vector3 direction = s.VerticalSpeed > 0 ? Vector3.up : Vector3.down;
            float distance = Mathf.Abs(s.VerticalSpeed * dt); s.Grounded = false;
            Vector3 origin = s.Position + Vector3.up * .3f;
            bool blocked = Physics.SphereCast(origin, .25f, direction, out var hit, distance + .05f, WorldMask, QueryTriggerInteraction.Ignore);
            float nearest = blocked ? hit.distance : float.PositiveInfinity;
            if (FixedGridActive && BlockTerrain.Current.TryCastFixed(origin, direction, distance + .05f, .25f, out var fixedHit) &&
                fixedHit.Distance < nearest) { blocked = true; nearest = fixedHit.Distance; }
            if (blocked) { distance = Mathf.Max(0, nearest - .05f); s.VerticalSpeed = 0; s.Grounded = direction.y < 0; }
            s.Position += direction * distance; _root.position = s.Position; s.Velocity = direction * (distance / dt);
        }
    }
}
