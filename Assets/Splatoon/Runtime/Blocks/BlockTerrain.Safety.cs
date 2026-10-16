using System;
using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        const int MaxSafetyPlayers = 8, SafetySearchCells = 10;
        static readonly Vector3Int[] SafetyOffsets = CreateSafetyOffsets();
        HeroChangeZone[] _constructionZones = Array.Empty<HeroChangeZone>();
        ConstructionZoneGeometry[] _stageConstructionZones = Array.Empty<ConstructionZoneGeometry>();
        int _stageConstructionZoneCount;
        bool _stageConstructionZonesActive;
        ulong _stageConstructionZoneGeneration;
        const int MaxCachedConstructionZoneColumns = 4096;
        readonly Dictionary<Vector2Int, bool> _constructionZoneColumnCache = new();
        ConstructionZoneGeometry[] _constructionZoneColumnCacheZones = Array.Empty<ConstructionZoneGeometry>();
        int _constructionZoneColumnCacheZoneCount;
        float _constructionZoneColumnCacheBottom, _constructionZoneColumnCacheTop;
        bool _constructionZoneColumnCacheValid;
        readonly struct ConstructionZoneGeometry
        {
            public readonly Vector3 Center, Extent, X, Y, Z;
            public ConstructionZoneGeometry(Vector3 center, Vector3 extent, Quaternion rotation)
            {
                Center = center; Extent = extent;
                X = rotation * Vector3.right; Y = rotation * Vector3.up; Z = rotation * Vector3.forward;
            }
            public bool Matches(ConstructionZoneGeometry other)
                => Center.Equals(other.Center) && Extent.Equals(other.Extent) &&
                    X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }
        void ResetConstructionZoneColumnCache()
        {
            _constructionZoneColumnCache.Clear(); _constructionZoneColumnCacheValid = false;
            _constructionZoneColumnCacheZoneCount = 0;
        }
        void UpdateConstructionZoneColumnCache()
        {
            // Only identical effective SAT inputs can retain a column proof across
            // stages. Vector3.Equals is exact; the approximate == operator is unsafe here.
            bool same = _constructionZoneColumnCacheValid &&
                _constructionZoneColumnCacheBottom.Equals(World.Bottom) &&
                _constructionZoneColumnCacheTop.Equals(BlockWorld.Top) &&
                _constructionZoneColumnCacheZoneCount == _stageConstructionZoneCount;
            for (int i = 0; same && i < _stageConstructionZoneCount; i++)
                same = _constructionZoneColumnCacheZones[i].Matches(_stageConstructionZones[i]);
            if (same) return;
            ResetConstructionZoneColumnCache();
            if (_constructionZoneColumnCacheZones.Length < _stageConstructionZoneCount)
                Array.Resize(ref _constructionZoneColumnCacheZones, _stageConstructionZoneCount);
            Array.Copy(_stageConstructionZones, _constructionZoneColumnCacheZones, _stageConstructionZoneCount);
            _constructionZoneColumnCacheZoneCount = _stageConstructionZoneCount;
            _constructionZoneColumnCacheBottom = World.Bottom;
            _constructionZoneColumnCacheTop = BlockWorld.Top;
            _constructionZoneColumnCacheValid = true;
        }
        void CaptureStageConstructionZones()
        {
            _stageConstructionZoneCount = 0; _stageConstructionZonesActive = false;
            unchecked { _stageConstructionZoneGeneration++; }
            if (_stageConstructionZones.Length < _constructionZones.Length)
                Array.Resize(ref _stageConstructionZones, _constructionZones.Length);
            foreach (var zone in _constructionZones)
            {
                if (zone == null || !zone.isActiveAndEnabled) continue;
                var volume = zone.Volume;
                if (volume == null || !volume.enabled || !volume.isTrigger) continue;
                Vector3 scale = volume.transform.lossyScale;
                Vector3 extent = Vector3.Scale(volume.size * .5f, Abs(scale));
                if (extent.x <= 0 || extent.y <= 0 || extent.z <= 0) continue;
                _stageConstructionZones[_stageConstructionZoneCount++] = new ConstructionZoneGeometry(
                    volume.transform.TransformPoint(volume.center), extent, volume.transform.rotation);
            }
            UpdateConstructionZoneColumnCache();
            _stageConstructionZonesActive = true;
        }
        readonly SafetyActor[] _safetyPlayers = new SafetyActor[MaxSafetyPlayers];
        readonly Dictionary<PrototypePlayer, SafePosition> _safePositions = new(MaxSafetyPlayers);
        readonly List<PrototypePlayer> _staleSafetyPlayers = new(MaxSafetyPlayers);
        int _safetyPlayerCount;
        public int SafetyRecoveries { get; private set; }
        public int SafetyRecoveryFailures { get; private set; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct SafetyRecoveryObservation
        {
            public readonly PlayerSnapshot Before, After;
            public readonly uint Round, TerrainRevision;
            public readonly int RecoveryCount, FailureCount;
            public readonly double Realtime, TerrainTime;
            public readonly float RawDistance, HumanDistance;
            public readonly bool CapsuleEmbedded, PaperEmbedded, Recovered;
            internal SafetyRecoveryObservation(BlockTerrain terrain, PlayerSnapshot before, PlayerSnapshot after,
                bool capsuleEmbedded, bool paperEmbedded, bool recovered)
            {
                Before = before; After = after; Round = terrain.Round; TerrainRevision = terrain.Revision;
                RecoveryCount = terrain.SafetyRecoveries; FailureCount = terrain.SafetyRecoveryFailures;
                Realtime = Time.realtimeSinceStartupAsDouble; TerrainTime = terrain.SimulationTime;
                CapsuleEmbedded = capsuleEmbedded; PaperEmbedded = paperEmbedded; Recovered = recovered;
                RawDistance = Vector3.Distance(before.Position, after.Position);
                HumanDistance = Vector3.Distance(PlayerMotorSimulation.HumanPosition(before), PlayerMotorSimulation.HumanPosition(after));
            }
        }
        public static event Action<BlockTerrain, PrototypePlayer, SafetyRecoveryObservation> SafetyRecoveryObserved;
        public int SafetyRecoveryObserverErrors { get; private set; }
#endif

        readonly struct SafePosition
        {
            public readonly Vector3 Position;
            readonly uint _life, _hero;
            readonly byte _team, _slot;
            public SafePosition(PlayerSnapshot state)
            { Position = PlayerMotorSimulation.HumanPosition(state); _life = state.Revision; _hero = state.HeroRevision; _team = state.Team; _slot = state.Slot; }
            public bool Matches(PlayerSnapshot state) => _life == state.Revision && _hero == state.HeroRevision && _team == state.Team && _slot == state.Slot;
        }
        readonly struct SafetyActor
        {
            public readonly PrototypePlayer Player;
            public readonly CharacterController Controller;
            public readonly PlayerSnapshot State;
            public readonly HeroBodyShape Shape;
            public readonly Vector2 PaperSize;
            public readonly float PaperThickness;
            public readonly IReadOnlyList<Rect> PaperRects;
            public SafetyActor(PrototypePlayer player, PlayerSnapshot state)
                : this(player, state, player.SwimBody != null ? player.SwimBody.HitRects : null) { }
            public SafetyActor(PrototypePlayer player, PlayerSnapshot state, IReadOnlyList<Rect> paperRects)
            {
                Player = player; Controller = player.GetComponent<CharacterController>();
                Shape = HeroBodyShape.For(state.HeroId);
                var body = player.SwimBody; var profile = body != null ? body.Profile : player.Presentation?.Paper;
                if (state.ShowsSwimBody && state.PaperPose == PaperPose.None)
                    PaperPoseSimulation.Resolve(ref state, default, profile, state.SimulatedAt);
                State = state; PaperSize = profile != null ? profile.Size : new Vector2(.9f, 1.8f);
                PaperThickness = profile != null ? profile.Thickness : .04f;
                PaperRects = paperRects;
            }
        }

        void InitializeSafety()
        {
            ClearSafetyState();
            _constructionZones = _arena != null ? _arena.GetComponentsInChildren<HeroChangeZone>(true)
                : FindObjectsByType<HeroChangeZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }
        void ClearSafetyState()
        {
            ResetConstructionZoneColumnCache();
            _safePositions.Clear(); _staleSafetyPlayers.Clear();
            Array.Clear(_safetyPlayers, 0, _safetyPlayers.Length); _safetyPlayerCount = 0;
            SafetyRecoveries = SafetyRecoveryFailures = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SafetyRecoveryObserverErrors = 0;
#endif
        }
        void CaptureSafetyPlayers()
        {
            Array.Clear(_safetyPlayers, 0, _safetyPlayerCount); _safetyPlayerCount = 0;
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (_safetyPlayerCount == MaxSafetyPlayers) break;
                if (player == null || !player.IsSpawned || !player.Snapshot.Value.IsAlive || player.GetComponent<CharacterController>() == null) continue;
                var actor = new SafetyActor(player, player.Snapshot.Value); _safetyPlayers[_safetyPlayerCount++] = actor;
                if (_safePositions.TryGetValue(player, out var previous) && !previous.Matches(actor.State)) _safePositions.Remove(player);
                // Cache a position that also fits the standing body, so a form change cannot invalidate a rescue.
                if (RecoveryPositionFits(actor, PlayerMotorSimulation.HumanPosition(actor.State)))
                    _safePositions[player] = new SafePosition(actor.State);
            }
            _staleSafetyPlayers.Clear();
            foreach (var player in _safePositions.Keys)
            {
                bool present = false;
                for (int i = 0; i < _safetyPlayerCount; i++) if (_safetyPlayers[i].Player == player) { present = true; break; }
                if (!present) _staleSafetyPlayers.Add(player);
            }
            foreach (var player in _staleSafetyPlayers) _safePositions.Remove(player);
        }

        bool ExcludedConstruction(Vector3Int cell)
        {
            Vector3 center = BlockWorld.Center(cell); float half = BlockWorld.CellSize * .5f;
            Vector3 columnCenter = new(center.x, (World.Bottom + BlockWorld.Top) * .5f, center.z);
            Vector3 columnHalf = new(half, (BlockWorld.Top - World.Bottom) * .5f, half);
            if (_stageConstructionZonesActive)
            {
                // Protection excludes the complete vertical column, so y is not
                // part of this key. Never cache player or static-collider occupancy.
                var column = new Vector2Int(cell.x, cell.z);
                if (!_constructionZoneColumnCache.TryGetValue(column, out bool protectedByZone))
                {
                    for (int i = 0; i < _stageConstructionZoneCount; i++)
                    {
                        var zone = _stageConstructionZones[i];
                        if (!AabbIntersectsBox(columnCenter, columnHalf, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z)) continue;
                        protectedByZone = true; break;
                    }
                    // Keep a fixed bound. At capacity, uncached columns retain the
                    // ordinary SAT path; do not evict useful proofs or skip queries.
                    if (_constructionZoneColumnCache.Count < MaxCachedConstructionZoneColumns)
                        _constructionZoneColumnCache.Add(column, protectedByZone);
                }
                if (protectedByZone) { BuildProtectedByZone++; return true; }
            }
            else
            {
                // Calls outside synchronous command staging retain the live query.
                foreach (var zone in _constructionZones)
                {
                    if (zone == null || !zone.isActiveAndEnabled) continue;
                    var volume = zone.Volume;
                    if (volume == null || !volume.enabled || !volume.isTrigger) continue;
                    Vector3 scale = volume.transform.lossyScale;
                    Vector3 extent = Vector3.Scale(volume.size * .5f, Abs(scale));
                    if (extent.x <= 0 || extent.y <= 0 || extent.z <= 0) continue;
                    // Exclude the complete vertical construction column; unsupported blocks cannot start above a safe spawn and fall into it.
                    if (AabbIntersectsBox(columnCenter, columnHalf, volume.transform.TransformPoint(volume.center), extent, volume.transform.rotation))
                    { BuildProtectedByZone++; return true; }
                }
            }
            for (int i = 0; i < _safetyPlayerCount; i++) if (CellOccupiesActor(cell, _safetyPlayers[i]))
            { BuildOccupiedByPlayer++; return true; }
            return false;
        }
        static bool CellOccupiesActor(Vector3Int cell, SafetyActor actor)
        {
            var s = actor.State; var shape = actor.Shape;
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            Vector3 center = BlockWorld.Center(cell); float half = BlockWorld.CellSize * .5f;
            // The live motor capsule is part of the occupied space, including compact movement.
            if (CellIntersectsCapsule(center, half, s.Position, height, shape.Radius)) return true;
            if (!s.ShowsSwimBody || !CellIntersectsBox(center, half, s.PaperCenter,
                new Vector3(actor.PaperSize.x * .5f, actor.PaperSize.y * .5f, actor.PaperThickness * .5f), s.PaperRotation)) return false;
            if (actor.PaperRects == null) return true;
            // The same animated silhouette rectangles generate the actual paper hit mesh.
            foreach (var rect in actor.PaperRects)
                if (CellIntersectsBox(center, half, s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                    new Vector3(rect.width * .5f, rect.height * .5f, actor.PaperThickness * .5f), s.PaperRotation)) return true;
            return false;
        }
        static bool CellIntersectsCapsule(Vector3 center, float half, Vector3 feet, float height, float radius)
        {
            float low = feet.y + radius, high = feet.y + Mathf.Max(radius, height - radius);
            float x = Mathf.Max(0, Mathf.Abs(center.x - feet.x) - half), z = Mathf.Max(0, Mathf.Abs(center.z - feet.z) - half);
            float y = Mathf.Max(0, Mathf.Max(low - center.y - half, center.y - half - high));
            return x * x + y * y + z * z <= radius * radius + .0000001f;
        }
        static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        // Separating-axis test: the full cell cube against an oriented collider/animated paper rectangle.
        // A centre-only test leaks at a protected volume boundary; world AABBs overexclude rotated corners.
        static bool CellIntersectsBox(Vector3 cell, float half, Vector3 center, Vector3 extent, Quaternion rotation)
            => AabbIntersectsBox(cell, Vector3.one * half, center, extent, rotation);
        static bool AabbIntersectsBox(Vector3 cell, Vector3 half, Vector3 center, Vector3 extent, Quaternion rotation)
            => AabbIntersectsBox(cell, half, center, extent, rotation * Vector3.right, rotation * Vector3.up, rotation * Vector3.forward);
        static bool AabbIntersectsBox(Vector3 cell, Vector3 half, Vector3 center, Vector3 extent, Vector3 x, Vector3 y, Vector3 z)
        {
            Vector3 delta = center - cell;
            Vector3 broad = Abs(x) * extent.x + Abs(y) * extent.y + Abs(z) * extent.z + half;
            if (Mathf.Abs(delta.x) > broad.x || Mathf.Abs(delta.y) > broad.y || Mathf.Abs(delta.z) > broad.z) return false;
            if (Separates(Vector3.right) || Separates(Vector3.up) || Separates(Vector3.forward) || Separates(x) || Separates(y) || Separates(z)) return false;
            if (Separates(Vector3.Cross(Vector3.right, x)) || Separates(Vector3.Cross(Vector3.right, y)) || Separates(Vector3.Cross(Vector3.right, z)) ||
                Separates(Vector3.Cross(Vector3.up, x)) || Separates(Vector3.Cross(Vector3.up, y)) || Separates(Vector3.Cross(Vector3.up, z)) ||
                Separates(Vector3.Cross(Vector3.forward, x)) || Separates(Vector3.Cross(Vector3.forward, y)) || Separates(Vector3.Cross(Vector3.forward, z))) return false;
            return true;
            bool Separates(Vector3 axis)
            {
                if (axis.sqrMagnitude < .00000001f) return false;
                float a = Vector3.Dot(half, Abs(axis));
                float b = extent.x * Mathf.Abs(Vector3.Dot(x, axis)) + extent.y * Mathf.Abs(Vector3.Dot(y, axis)) + extent.z * Mathf.Abs(Vector3.Dot(z, axis));
                return Mathf.Abs(Vector3.Dot(delta, axis)) > a + b + .000001f;
            }
        }

        bool ActorEmbedded(SafetyActor actor, out bool capsuleEmbedded, out bool paperEmbedded)
        {
            var s = actor.State; var shape = actor.Shape;
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            capsuleEmbedded = CapsuleOverlapsWorld(s.Position, height, shape.Radius, shape.SkinWidth);
            paperEmbedded = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            bool collectAllReasons = SafetyRecoveryObserved != null;
#else
            const bool collectAllReasons = false;
#endif
            if (capsuleEmbedded && !collectAllReasons) return true;
            if (!s.ShowsSwimBody) return capsuleEmbedded;
            if (actor.PaperRects == null) paperEmbedded = PaperOverlapsWorld(actor, new Rect(-actor.PaperSize * .5f, actor.PaperSize));
            else foreach (var rect in actor.PaperRects)
                if (PaperOverlapsWorld(actor, rect)) { paperEmbedded = true; break; }
            return capsuleEmbedded || paperEmbedded;
        }
        bool PaperOverlapsWorld(SafetyActor actor, Rect rect, Vector3 displacement = default)
        {
            var s = actor.State;
            Vector3 center = s.PaperCenter + displacement + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
            Vector3 half = new(Mathf.Max(.001f, rect.width * .5f - .001f), Mathf.Max(.001f, rect.height * .5f - .001f), Mathf.Max(.001f, actor.PaperThickness * .5f - .001f));
            return Physics.OverlapBoxNonAlloc(center, half, _overlaps, s.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) > 0;
        }
        bool CapsuleOverlapsWorld(Vector3 feet, float height, float radius, float skin)
        {
            float queryRadius = Mathf.Max(.01f, radius - Mathf.Max(.005f, skin));
            float segment = Mathf.Max(0, height * .5f - radius); Vector3 center = feet + Vector3.up * (height * .5f);
            return Physics.OverlapCapsuleNonAlloc(center - Vector3.up * segment, center + Vector3.up * segment,
                queryRadius, _overlaps, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) > 0;
        }
        bool RecoveryPositionFits(SafetyActor actor, Vector3 feet)
        {
            var shape = actor.Shape;
            if (!Finite(feet) || World == null || Mathf.Abs(feet.x) + shape.Radius > World.Dimensions.x * .5f ||
                Mathf.Abs(feet.z) + shape.Radius > World.Dimensions.y * .5f || feet.y < World.Bottom || feet.y + shape.Height > BlockWorld.Top) return false;
            return !CapsuleOverlapsWorld(feet, shape.Height, shape.Radius, shape.SkinWidth);
        }
        bool CurrentAirPaperPositionFits(SafetyActor actor, Vector3 displacement)
        {
            var s = actor.State; var shape = actor.Shape; Vector3 feet = s.Position + displacement;
            float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            if (!Finite(feet) || World == null || Mathf.Abs(feet.x) + shape.Radius > World.Dimensions.x * .5f ||
                Mathf.Abs(feet.z) + shape.Radius > World.Dimensions.y * .5f || feet.y < World.Bottom || feet.y + height > BlockWorld.Top ||
                CapsuleOverlapsWorld(feet, height, shape.Radius, shape.SkinWidth)) return false;
            if (actor.PaperRects == null) return Fits(new Rect(-actor.PaperSize * .5f, actor.PaperSize));
            foreach (var rect in actor.PaperRects) if (!Fits(rect)) return false;
            return true;

            bool Fits(Rect rect)
            {
                Vector3 center = s.PaperCenter + displacement + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                // Check the full live silhouette against world bounds; collision queries retain
                // the same small contact tolerance as embedded detection.
                Vector3 extent = Abs(s.PaperRotation * Vector3.right) * (rect.width * .5f) +
                    Abs(s.PaperRotation * Vector3.up) * (rect.height * .5f) +
                    Abs(s.PaperRotation * Vector3.forward) * (actor.PaperThickness * .5f);
                if (!Finite(center) || !Finite(extent) || Mathf.Abs(center.x) + extent.x > World.Dimensions.x * .5f ||
                    Mathf.Abs(center.z) + extent.z > World.Dimensions.y * .5f || center.y - extent.y < World.Bottom ||
                    center.y + extent.y > BlockWorld.Top) return false;
                return !PaperOverlapsWorld(actor, rect, displacement);
            }
        }
        /// <summary>Query the published world using this candidate's capsule and immutable
        /// authored contour. Does not relocate state or change live capture/hit publication.</summary>
        public bool PublishedTraversalPoseFits(PrototypePlayer player, PlayerSnapshot state)
        {
            if (!Initialized || World == null || player == null) return false;
            var rects = state.ShowsSwimBody && player.SwimBody != null
                ? player.SwimBody.QueryTraversalRects(state) : Array.Empty<Rect>();
            return CurrentAirPaperPositionFits(new SafetyActor(player, state, rects), Vector3.zero);
        }

        /// <summary>Shared-motor recovery for explicit snapshots, current Physics
        /// collisions and arena/spawn configuration. Does not read the authority-only saved-position
        /// cache, move a player, update scores or publish live hit geometry.</summary>
        public bool TryRecoverPublishedTraversalPose(PrototypePlayer player, PlayerSnapshot state,
            PlayerSnapshot previous, out PlayerSnapshot recovered)
        {
            recovered = state;
            if (!Initialized || World == null || player == null) return false;
            var rects = state.ShowsSwimBody && player.SwimBody != null
                ? player.SwimBody.QueryTraversalRects(state) : Array.Empty<Rect>();
            var actor = new SafetyActor(player, state, rects);
            if (state.ShowsSwimBody && !state.Grounded && state.Movement == MovementMode.Air && state.PaperPose == PaperPose.Air &&
                FindNearestCurrentAirPaperPosition(actor, out var displacement))
            {
                recovered.Position += displacement; recovered.PaperCenter += displacement;
                return PublishedTraversalPoseFits(player, recovered);
            }

            // Reuse the established standing-body search and protected-spawn
            // policy. The explicit previous snapshot replaces hidden authority
            // history, so prediction/replay chooses the same fallback.
            bool found = FindNearestSafetyPosition(actor, PlayerMotorSimulation.HumanPosition(state), out var safe);
            if (!found && previous.Revision == state.Revision && previous.HeroRevision == state.HeroRevision &&
                previous.HeroId == state.HeroId && previous.Team == state.Team && previous.Slot == state.Slot &&
                RecoveryPositionFits(actor, PlayerMotorSimulation.HumanPosition(previous)))
            { safe = PlayerMotorSimulation.HumanPosition(previous); found = true; }
            if (!found)
            {
                var arena = _arena != null ? _arena : PrototypeArena.Current;
                int spawn = (state.Team - 1) * TeamSelectionRules.Capacity + state.Slot;
                if (state.Team >= 1 && state.Team <= 2 && state.Slot < TeamSelectionRules.Capacity && arena != null &&
                    arena.SpawnPoints != null && spawn >= 0 && spawn < arena.SpawnPoints.Length && arena.SpawnPoints[spawn] != null)
                    found = FindNearestSafetyPosition(actor, arena.SpawnPoints[spawn].position, out safe);
            }
            if (!found) return false;

            recovered.Position = safe; recovered.Velocity = recovered.PlanarVelocity = Vector3.zero; recovered.VerticalSpeed = 0;
            recovered.Grounded = recovered.Swimming = recovered.CompactBody = recovered.FriendlyInkContact = recovered.SwimWasHeld = false;
            recovered.AirHumanOffset = 0; recovered.SwimSource = recovered.AirSwimSource = SwimSurface.None; recovered.Movement = MovementMode.Air;
            recovered.SupportCell = default; recovered.SupportTerrainRevision = 0; recovered.GroundNormal = Vector3.up;
            recovered.WallNormal = recovered.WallPoint = recovered.MantleFrom = recovered.MantleTo = Vector3.zero;
            recovered.WallSurfaceId = recovered.WallRegionId = 0;
            recovered.PaperPose = PaperPose.None; recovered.PaperCenter = safe; recovered.PaperRotation = Quaternion.identity;
            recovered.PaperAnimationTime = 0; recovered.PaperMove = Vector2.zero;
            // Paid attacks, resources, time/tick and special-impulse state remain
            // those of the explicit completed snapshot; recovery changes locomotion.
            return PublishedTraversalPoseFits(player, recovered);
        }

        bool FindNearestCurrentAirPaperPosition(SafetyActor actor, out Vector3 displacement)
        {
            foreach (var offset in SafetyOffsets)
            {
                var candidate = (Vector3)offset * BlockWorld.CellSize;
                if (CurrentAirPaperPositionFits(actor, candidate)) { displacement = candidate; return true; }
            }
            displacement = default; return false;
        }
        static Vector3Int[] CreateSafetyOffsets()
        {
            var offsets = new List<Vector3Int>();
            for (int x = -SafetySearchCells; x <= SafetySearchCells; x++)
            for (int y = -SafetySearchCells; y <= SafetySearchCells; y++)
            for (int z = -SafetySearchCells; z <= SafetySearchCells; z++)
                if (x * x + y * y + z * z <= SafetySearchCells * SafetySearchCells) offsets.Add(new Vector3Int(x, y, z));
            offsets.Sort((a, b) =>
            {
                int order = a.sqrMagnitude.CompareTo(b.sqrMagnitude); if (order != 0) return order;
                order = b.y.CompareTo(a.y); if (order != 0) return order;
                order = a.x.CompareTo(b.x); return order != 0 ? order : a.z.CompareTo(b.z);
            });
            return offsets.ToArray();
        }
        bool FindNearestSafetyPosition(SafetyActor actor, Vector3 origin, out Vector3 safe)
        {
            foreach (var offset in SafetyOffsets)
            {
                var candidate = origin + (Vector3)offset * BlockWorld.CellSize;
                if (RecoveryPositionFits(actor, candidate)) { safe = candidate; return true; }
            }
            safe = default; return false;
        }
        bool ResolveSafetyPosition(SafetyActor actor, out Vector3 safe)
        {
            if (FindNearestSafetyPosition(actor, PlayerMotorSimulation.HumanPosition(actor.State), out safe)) return true;
            if (_safePositions.TryGetValue(actor.Player, out var saved) && saved.Matches(actor.State) && RecoveryPositionFits(actor, saved.Position))
            { safe = saved.Position; return true; }
            var arena = _arena != null ? _arena : PrototypeArena.Current;
            int spawn = (actor.State.Team - 1) * TeamSelectionRules.Capacity + actor.State.Slot;
            if (actor.State.Team < 1 || actor.State.Team > 2 || actor.State.Slot >= TeamSelectionRules.Capacity ||
                arena == null || arena.SpawnPoints == null || spawn < 0 || spawn >= arena.SpawnPoints.Length || arena.SpawnPoints[spawn] == null) return false;
            // Protected spawns should always fit. A map misconfiguration still cannot teleport into a wall.
            return FindNearestSafetyPosition(actor, arena.SpawnPoints[spawn].position, out safe);
        }
        void RecoverEmbeddedPlayers()
        {
            if (!_authority) return;
            bool moved = false;
            for (int i = 0; i < _safetyPlayerCount; i++)
            {
                var player = _safetyPlayers[i].Player;
                if (player == null || !player.IsSpawned || !player.Snapshot.Value.IsAlive) continue;
                var actor = new SafetyActor(player, player.Snapshot.Value);
                if (!ActorEmbedded(actor, out bool capsuleEmbedded, out bool paperEmbedded)) continue;
                var s = actor.State;
                // Air paper has no attachment to reproject on the next motor step. Keep
                // its reversible origin when the complete current form has a safe exit.
                Vector3 displacement = default;
                bool preserveAirPaper = s.ShowsSwimBody && !s.Grounded &&
                    s.Movement == MovementMode.Air && s.PaperPose == PaperPose.Air && s.SupportTerrainRevision == 0 &&
                    FindNearestCurrentAirPaperPosition(actor, out displacement);
                if (preserveAirPaper)
                {
                    s.Position += displacement; s.PaperCenter += displacement;
                }
                else if (ResolveSafetyPosition(actor, out var safe))
                {
                    s.Position = safe; s.Velocity = s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = 0;
                    s.Grounded = s.Swimming = s.CompactBody = s.FriendlyInkContact = s.SwimWasHeld = false;
                    s.AirHumanOffset = 0; s.SwimSource = s.AirSwimSource = SwimSurface.None; s.Movement = MovementMode.Air;
                    s.SupportCell = default; s.SupportTerrainRevision = 0; s.GroundNormal = Vector3.up;
                    s.WallNormal = s.WallPoint = s.MantleFrom = s.MantleTo = Vector3.zero; s.WallSurfaceId = s.WallRegionId = 0;
                    s.PaperPose = PaperPose.None; s.PaperCenter = safe; s.PaperRotation = Quaternion.identity;
                }
                else
                {
                    SafetyRecoveryFailures++;
                    RecordSafetyRecovery(player, actor.State, actor.State, capsuleEmbedded, paperEmbedded, false);
                    continue;
                }
                actor.Controller.enabled = false; actor.Shape.Apply(actor.Controller, s.Swimming || s.CompactBody); player.transform.position = s.Position;
                actor.Controller.enabled = true; player.Snapshot.Value = s; player.SwimBody?.ApplyCollision(s);
                if (RecoveryPositionFits(actor, PlayerMotorSimulation.HumanPosition(s))) _safePositions[player] = new SafePosition(s);
                SafetyRecoveries++; moved = true;
                RecordSafetyRecovery(player, actor.State, s, capsuleEmbedded, paperEmbedded, true);
            }
            if (moved) Physics.SyncTransforms();
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        void RecordSafetyRecovery(PrototypePlayer player, PlayerSnapshot before, PlayerSnapshot after,
            bool capsuleEmbedded, bool paperEmbedded, bool recovered)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var observers = SafetyRecoveryObserved;
            if (observers == null) return;
            var observation = new SafetyRecoveryObservation(this, before, after, capsuleEmbedded, paperEmbedded, recovered);
            foreach (Action<BlockTerrain, PrototypePlayer, SafetyRecoveryObservation> observer in observers.GetInvocationList())
            {
                try { observer(this, player, observation); }
                catch (Exception exception) { SafetyRecoveryObserverErrors++; Debug.LogException(exception); }
            }
#endif
        }
    }
}
