using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Splatoon.Prototype;
using Splatoon.Blocks.Simulation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Unity.Profiling;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        public const int FixedMaximumLayers = 4;
        public const float FixedBuildRadiusScale = BlockWorld.CellSize / .10f;
        static readonly ProfilerMarker FixedBuildCpu = new("Splatoon.Fixed.BuildImpact");
        static readonly ProfilerMarker FixedDestroyCpu = new("Splatoon.Fixed.DestroyImpact");
        static readonly ProfilerMarker FixedAdmissionCpu = new("Splatoon.Fixed.Admission");
        static readonly ProfilerMarker FixedAdmissionPlayersCpu = new("Splatoon.Fixed.Admission.Players");
        static readonly ProfilerMarker FixedAdmissionMapCpu = new("Splatoon.Fixed.Admission.Map");
        static readonly ProfilerMarker FixedAdmissionOverlapCpu = new("Splatoon.Fixed.Admission.NativeOverlap");
        static readonly ProfilerMarker FixedAdmissionPenetrationCpu = new("Splatoon.Fixed.Admission.NativePenetration");
        static readonly ProfilerMarker FixedAdmissionShapeReadyCpu = new("Splatoon.Fixed.Admission.ShapeReady");
        static readonly ProfilerMarker FixedAdmissionProtectedCpu = new("Splatoon.Fixed.Admission.Protected");
        static readonly ProfilerMarker FixedMapIndexCpu = new("Splatoon.Fixed.MapSupportIndex");
        static readonly ProfilerMarker FixedRenderCpu = new("Splatoon.Fixed.RenderUpdate");
        public bool FixedGridMode => FixedBlocks != null;
        public BlockFixedGridWorld FixedBlocks { get; private set; }
        Dictionary<Vector2Int, FixedMapLayer[]> _fixedMapLayers = new();
        readonly Dictionary<Vector3Int, Mesh> _fixedMeshes = new();
        readonly HashSet<Vector3Int> _fixedRenderDirty = new();
        Task<(Vector3Int key, BlockMeshData mesh)[]> _fixedRenderTask;
        int _fixedRenderEpoch;
        BoxCollider _fixedAdmissionBox;
        Scene _fixedAdmissionScene;
        bool _fixedAdmissionInUse, _fixedAdmissionShapeFaultReported;
        int _fixedAdmissionThreadId;
        bool _fixedAdmissionMeshProofFaultReported;
        bool _fixedAdmissionMapRefreshFailed;
        Bounds _fixedAdmissionPendingGeometry;
        Dictionary<MeshCollider, FixedAdmissionMeshProof> _fixedAdmissionMeshProofs = new();
        FixedAdmissionMapChange _activeFixedMapChange;

        sealed class FixedAdmissionMapChange
        {
            public readonly Dictionary<MeshCollider, FixedAdmissionMeshProof> Proofs;
            public readonly Dictionary<Vector2Int, FixedMapLayer[]> Layers;
            public readonly List<Vector2Int> Columns = new();
            public readonly List<FixedAdmissionMeshProof> Created = new();
            public FixedAdmissionMapChange(Dictionary<MeshCollider, FixedAdmissionMeshProof> proofs, Dictionary<Vector2Int, FixedMapLayer[]> layers)
            { Proofs = new(proofs); Layers = new(layers); }
            public void DisposeCreated() { foreach (var proof in Created) proof.Dispose(); Created.Clear(); }
        }


        // A proof owns immutable world-space geometry. Cache only its complete pure
        // BVH result; the caller still validates the live source before every lookup.
        readonly struct FixedAdmissionMeshQuery : IEquatable<FixedAdmissionMeshQuery>
        {
            readonly int3 _center, _half, _supportPoint, _supportNormal;
            public FixedAdmissionMeshQuery(Vector3 center, Vector3 half, Vector3 supportPoint, Vector3 supportNormal)
            { _center = Bits(center); _half = Bits(half); _supportPoint = Bits(supportPoint); _supportNormal = Bits(supportNormal); }
            static int3 Bits(Vector3 value) => math.asint(new float3(value.x, value.y, value.z));
            public bool Equals(FixedAdmissionMeshQuery other)
                => math.all(_center == other._center) && math.all(_half == other._half) &&
                   math.all(_supportPoint == other._supportPoint) && math.all(_supportNormal == other._supportNormal);
            public override bool Equals(object other) => other is FixedAdmissionMeshQuery key && Equals(key);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _center.GetHashCode(); hash = hash * 397 ^ _half.GetHashCode();
                    hash = hash * 397 ^ _supportPoint.GetHashCode(); return hash * 397 ^ _supportNormal.GetHashCode();
                }
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct FixedAdmissionMeshQueryCacheStatistics
        {
            public readonly long Queries, Hits, Misses, CapacityClears;
            public readonly int Proofs, Entries, SumOfProofPeakEntries;
            public FixedAdmissionMeshQueryCacheStatistics(long queries, long hits, long misses, long clears,
                int proofs, int entries, int sumOfProofPeakEntries)
            { Queries = queries; Hits = hits; Misses = misses; CapacityClears = clears;
              Proofs = proofs; Entries = entries; SumOfProofPeakEntries = sumOfProofPeakEntries; }
        }

        // Diagnostic capture only: no allocation, and no per-query scan of all proofs.
        // Retired/failed staged proofs are excluded; peaks are per-proof sums, not a
        // claim about simultaneous whole-world peak memory or lifetime work totals.
        public FixedAdmissionMeshQueryCacheStatistics CaptureFixedAdmissionMeshQueryCacheStatistics()
        {
            long queries = 0, hits = 0, misses = 0, clears = 0;
            int entries = 0, peaks = 0;
            foreach (var proof in _fixedAdmissionMeshProofs.Values)
            {
                queries += proof.QueryCalls; hits += proof.QueryCacheHits; misses += proof.QueryCacheMisses;
                clears += proof.QueryCacheCapacityClears; entries += proof.QueryCacheEntries;
                peaks += proof.QueryCachePeakEntries;
            }
            return new FixedAdmissionMeshQueryCacheStatistics(queries, hits, misses, clears,
                _fixedAdmissionMeshProofs.Count, entries, peaks);
        }
#endif

        sealed class FixedAdmissionMeshProof : IDisposable
        {
            public readonly BlockMapCollisionData Data;
            readonly Mesh _mesh;
            readonly Matrix4x4 _transform;
            readonly MeshColliderCookingOptions _cooking;
            public readonly Bounds Bounds;
            public const int MaximumQueryCacheEntries = 4096;
            Dictionary<FixedAdmissionMeshQuery, bool> _queries;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public long QueryCalls, QueryCacheHits, QueryCacheMisses, QueryCacheCapacityClears;
            public int QueryCachePeakEntries;
            public int QueryCacheEntries => _queries?.Count ?? 0;
#endif
            public FixedAdmissionMeshProof(MeshCollider source, BlockMapCollisionData data)
            { Data = data; _mesh = source.sharedMesh; _transform = source.transform.localToWorldMatrix; _cooking = source.cookingOptions; Bounds = source.bounds; }
            public bool Matches(MeshCollider source)
                => source != null && source.enabled && source.gameObject.activeInHierarchy && !source.isTrigger && !source.convex &&
                   source.sharedMesh == _mesh && source.transform.localToWorldMatrix.Equals(_transform) && source.cookingOptions == _cooking && Data.Snapshot.IsCreated;
            public bool OutsideSupportBlocked(Vector3 center, Vector3 half, Vector3 supportPoint, Vector3 supportNormal)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                QueryCalls++;
#endif
                var key = new FixedAdmissionMeshQuery(center, half, supportPoint, supportNormal);
                if (_queries != null && _queries.TryGetValue(key, out bool cached))
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    QueryCacheHits++;
#endif
                    return cached;
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                QueryCacheMisses++;
#endif
                // Do not cache faults, invalid arguments, or incomplete traversal.
                bool blocked = BlockMapQueries.OverlapOutsideSupportPlane(Data.Snapshot,
                    new float3(center.x, center.y, center.z), new float3(half.x, half.y, half.z),
                    new float3(supportPoint.x, supportPoint.y, supportPoint.z),
                    new float3(supportNormal.x, supportNormal.y, supportNormal.z), out _);
                _queries ??= new Dictionary<FixedAdmissionMeshQuery, bool>(MaximumQueryCacheEntries);
                if (_queries.Count >= MaximumQueryCacheEntries)
                {
                    // Eviction only changes work. The exact completed query above has
                    // already proved this result; no collision candidate is truncated.
                    _queries.Clear();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    QueryCacheCapacityClears++;
#endif
                }
                _queries.Add(key, blocked);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                QueryCachePeakEntries = Math.Max(QueryCachePeakEntries, _queries.Count);
#endif
                return blocked;
            }
            public void Dispose() { _queries = null; Data.Dispose(); }
        }
        Collider[] _fixedAdmissionOverlaps = new Collider[16];
        readonly List<FixedAdmissionActor> _fixedAdmissionActors = new(8);
        List<FixedAdmissionActor> _activeFixedAdmissionActors;

        readonly struct FixedAdmissionActor
        {
            public readonly SafetyActor Actor;
            public readonly Bounds Bounds;
            public FixedAdmissionActor(SafetyActor actor) { Actor = actor; Bounds = SafetyActorBounds(actor); }
            public bool Occupies(Vector3Int cell)
                => Bounds.Intersects(new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize)) && CellOccupiesActor(cell, Actor);
        }

        void BuildFixedCandidates(BlockFixedGridTransaction tx, IEnumerable<Vector3Int> candidates, byte team, int count)
        {
            // Capture only for this synchronous build, never across attacks or player steps.
            // A nested build receives its own list and restores the outer view afterwards.
            var previous = _activeFixedAdmissionActors;
            var actors = previous == null ? _fixedAdmissionActors : new List<FixedAdmissionActor>(8);
            bool ownsZones = !_stageConstructionZonesActive;
            try
            {
                actors.Clear();
                foreach (var player in PrototypePlayer.ByOwner.Values)
                {
                    if (player == null || !player.IsSpawned) continue;
                    var state = player.Snapshot.Value;
                    if (state.IsAlive) actors.Add(new FixedAdmissionActor(new SafetyActor(player, state)));
                }
                _activeFixedAdmissionActors = actors;
                if (ownsZones) CaptureStageConstructionZones();
                tx.Build(candidates, team, count);
            }
            finally
            {
                _activeFixedAdmissionActors = previous; actors.Clear();
                if (ownsZones) { _stageConstructionZonesActive = false; _stageConstructionZoneCount = 0; }
            }
        }

        bool FixedCellOccupiedByPlayer(Vector3Int cell)
        {
            using var timing = FixedAdmissionPlayersCpu.Auto();
            if (_activeFixedAdmissionActors != null)
            {
                foreach (var actor in _activeFixedAdmissionActors) if (actor.Occupies(cell)) return true;
                return false;
            }
            // Direct data-world callers retain a live admission check.
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (player == null || !player.IsSpawned) continue;
                var state = player.Snapshot.Value;
                if (state.IsAlive && new FixedAdmissionActor(new SafetyActor(player, state)).Occupies(cell)) return true;
            }
            return false;
        }

        readonly struct FixedMapLayer
        {
            public readonly BlockFixedGridMapSupport Support;
            public readonly RaycastHit Hit;
            public FixedMapLayer(BlockFixedGridMapSupport support, RaycastHit hit) { Support = support; Hit = hit; }
        }

        sealed class FixedEnvironment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard, IBlockFixedGridPaintAdmission, IBlockFixedGridRecoveryRollback
        {
            readonly BlockTerrain _terrain;
            public FixedEnvironment(BlockTerrain terrain) { _terrain = terrain; }
            public bool TryGetMapSupport(Vector3Int fromCell, out BlockFixedGridMapSupport support)
                => _terrain.TryFixedMapSupport(fromCell, out support);
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase, bool relocating, out string reason)
                => _terrain.FixedPlacementAllowed(cell, support, embeddedBase, relocating, out reason);
            public bool Visible(Vector3 from, Vector3 to) => _terrain.Visible(from, to);
            public bool PaintAllowed(BlockSurfaceKey key, out string reason)
            {
                reason = null;
                if (_terrain._fixedAdmissionMapRefreshFailed && _terrain._activeFixedMapChange == null) { reason = "map-support-refresh-unavailable"; return false; }
                if (!_terrain.SurfacePaints.TryGetCenter(key, out var center) || !_terrain.SurfacePaintFitsWorld(key, center) ||
                    _terrain.IsDynamicColumnProtected(center)) { reason = "paint-footprint"; return false; }
                return true;
            }
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason)
                => _terrain.PrepareFixedSafety(preview, out reason);
            public void CommitPreparedRecovery() => _terrain.CommitFixedSafety();
            public void RollbackPreparedRecovery() => _terrain.RollbackFixedSafety();
        }

        /// <summary>Production path: occupancy is authoritative at commit; meshes are presentation only.</summary>
        public void EnableFixedGridMode()
        {
            if (FixedGridMode) return;
            if (!Initialized) throw new InvalidOperationException("Initialize terrain before enabling fixed grid mode.");
            if (World.PinkCount != 0 || World.BlueCount != 0 || Dynamic != null)
                throw new InvalidOperationException("Fixed grid mode must start from an empty new world; legacy data cannot be migrated.");
            SurfaceOnly = true;
            if (SurfacePaints == null) InitializeSurfacePaints();
            _epoch++; AbortPending(); _remoteQueued = null; _blastBatch = null;
            while (_commands.Count > 0) Complete(_commands.Dequeue(), default);
            _gravityColumns.Clear(); _gravityCursor.Clear(); ResetGravityCohorts(); ResetBlockSpread();
            foreach (var view in _views.Values) Dispose(view);
            _views.Clear(); _colliderChunks.Clear();
            CreateFixedAdmissionShape();
            try
            {
                CreateFixedAdmissionMeshProofs();
                var fixedBlocks = new BlockFixedGridWorld(World, SurfacePaints, new FixedEnvironment(this));
                fixedBlocks.Committed += FixedCommitted;
                FixedBlocks = fixedBlocks;
            }
            catch { DisposeFixedAdmissionMeshProofs(); DisposeFixedAdmissionShape(); throw; }
        }

        // The support index and this proof share the authored map's initialization-static
        // geometry contract between explicit NotifyFixedMapSupportChanged adapter calls.
        void CreateFixedAdmissionMeshProofs()
        {
            if (_fixedAdmissionMeshProofs.Count != 0) throw new InvalidOperationException("Admission mesh proofs already have an owner.");
            foreach (var collider in _staticColliders)
            {
                if (collider == null || collider is not MeshCollider mesh || mesh.convex || !mesh.enabled || !mesh.gameObject.activeInHierarchy || mesh.isTrigger) continue;
                var proof = CaptureFixedAdmissionMeshProof(mesh);
                try { _fixedAdmissionMeshProofs.Add(mesh, proof); }
                catch { proof.Dispose(); throw; }
            }
        }

        FixedAdmissionMeshProof CaptureFixedAdmissionMeshProof(MeshCollider mesh)
        {
            BlockMapCollisionData data = null;
            try
            {
                // Complete capture rejects unreadable/unsupported/degenerate input before publication.
                // Capture occurs only at initialization or explicit map invalidation, never per tick.
                data = BlockMapCollisionCapture.Capture(new Collider[] { mesh });
                if (!data.Snapshot.IsCreated || data.Snapshot.FeatureCount == 0)
                    throw new InvalidOperationException("A supporting concave mesh needs a complete admission bake.");
                var proof = new FixedAdmissionMeshProof(mesh, data); data = null; return proof;
            }
            finally { data?.Dispose(); }
        }

        FixedAdmissionMapChange PrepareFixedMapChange(Bounds changedGeometry)
        {
            // Both published caches remain owned and unchanged until the world accepts
            // reconciliation. This view is used only by the synchronous transaction below.
            var stage = new FixedAdmissionMapChange(_fixedAdmissionMeshProofs, _fixedMapLayers);
            try
            {
                var low = BlockWorld.CellAt(changedGeometry.min); var high = BlockWorld.CellAt(changedGeometry.max);
                for (int z = low.z; z <= high.z; z++) for (int x = low.x; x <= high.x; x++)
                { var column = new Vector2Int(x, z); stage.Layers.Remove(column); stage.Columns.Add(column); }
                foreach (var pair in _fixedAdmissionMeshProofs)
                    if (pair.Key == null || pair.Value.Bounds.Intersects(changedGeometry)) stage.Proofs.Remove(pair.Key);
                foreach (var collider in _staticColliders)
                {
                    if (collider == null || collider is not MeshCollider mesh) continue;
                    bool affected = mesh.bounds.Intersects(changedGeometry) ||
                        _fixedAdmissionMeshProofs.TryGetValue(mesh, out var old) && old.Bounds.Intersects(changedGeometry);
                    if (!affected) continue;
                    stage.Proofs.Remove(mesh);
                    if (mesh.convex || !mesh.enabled || !mesh.gameObject.activeInHierarchy || mesh.isTrigger) continue;
                    var proof = CaptureFixedAdmissionMeshProof(mesh);
                    try { stage.Created.Add(proof); } catch { proof.Dispose(); throw; }
                    stage.Proofs.Add(mesh, proof);
                }
                return stage;
            }
            catch { stage.DisposeCreated(); throw; }
        }

        void DisposeFixedAdmissionMeshProofs()
        {
            foreach (var proof in _fixedAdmissionMeshProofs.Values) proof.Dispose();
            _fixedAdmissionMeshProofs.Clear();
            _fixedAdmissionMeshProofFaultReported = false;
            _fixedAdmissionMapRefreshFailed = false; _fixedAdmissionPendingGeometry = default;
        }

        bool FixedAdmissionMeshClear(Collider collider, Vector3 center, Vector3 half, Vector3 supportPoint, Vector3 supportNormal, out string reason)
        {
            reason = null;
            if (_fixedAdmissionMapRefreshFailed && _activeFixedMapChange == null) { reason = "map-support-refresh-unavailable"; return false; }
            if (collider is not MeshCollider mesh || mesh.convex) return true;
            Exception failure = null;
            try
            {
                var proofs = _activeFixedMapChange?.Proofs ?? _fixedAdmissionMeshProofs;
                if (!proofs.TryGetValue(mesh, out var proof) || !proof.Matches(mesh))
                    throw new InvalidOperationException("The initialized supporting mesh admission bake is missing or has changed.");
                if (proof.OutsideSupportBlocked(center, half, supportPoint, supportNormal))
                { reason = "support-extra-face"; return false; }
                return true;
            }
            catch (Exception error) { failure = error; }
            // The helper is disabled here; diagnostics cannot run inside its enabled window.
            reason = "admission-mesh-proof-unavailable";
            if (!_fixedAdmissionMeshProofFaultReported)
            {
                _fixedAdmissionMeshProofFaultReported = true; FailedTransactions++;
                Debug.LogError("[BLOCK FIXED] Supporting mesh proof unavailable; construction rejected before commit.\n" + failure);
            }
            return false;
        }

        void CreateFixedAdmissionShape()
        {
            if (_fixedAdmissionBox != null || _fixedAdmissionScene.IsValid())
                throw new InvalidOperationException("Fixed admission shape already has an owner.");
            GameObject admission = null;
            try
            {
                _fixedAdmissionScene = gameObject.scene;
                if (!_fixedAdmissionScene.IsValid() || !_fixedAdmissionScene.isLoaded ||
                    !_fixedAdmissionScene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene))
                    throw new InvalidOperationException("Fixed admission requires the gameplay default physics scene.");
                // A single independent root keeps owner transforms out of this explicit-pose box.
                // It remains disabled between synchronous narrow-phase calls, away from gameplay.
                admission = new GameObject("Fixed grid admission shape") { hideFlags = HideFlags.HideAndDontSave, layer = TerrainLayer };
                admission.SetActive(false);
                SceneManager.MoveGameObjectToScene(admission, _fixedAdmissionScene);
                admission.transform.SetPositionAndRotation(new Vector3(0, BlockWorld.Top + 1024, 0), Quaternion.identity);
                admission.transform.localScale = Vector3.one;
                _fixedAdmissionBox = admission.AddComponent<BoxCollider>();
                _fixedAdmissionBox.center = Vector3.zero;
                _fixedAdmissionBox.size = Vector3.one * (BlockWorld.CellSize - .0002f);
                _fixedAdmissionBox.isTrigger = true;
                _fixedAdmissionBox.enabled = false;
                _fixedAdmissionInUse = _fixedAdmissionShapeFaultReported = false;
                _fixedAdmissionThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                admission.SetActive(true);
                if (!FixedAdmissionShapeReady()) throw new InvalidOperationException("Fixed admission shape was not prepared correctly.");
            }
            catch
            {
                if (_fixedAdmissionBox == null && admission != null) { admission.SetActive(false); DisposeObject(admission); }
                DisposeFixedAdmissionShape(); throw;
            }
        }

        bool FixedAdmissionShapeReady()
        {
            using var timing = FixedAdmissionShapeReadyCpu.Auto();
            if (_fixedAdmissionInUse || _fixedAdmissionShapeFaultReported ||
                _fixedAdmissionThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId) return false;
            var box = _fixedAdmissionBox;
            return box != null && !box.enabled && box.isTrigger && box.gameObject.activeInHierarchy &&
                box.attachedRigidbody == null && box.transform.parent == null && box.transform.lossyScale.Equals(Vector3.one) &&
                box.transform.rotation.Equals(Quaternion.identity) && box.center.Equals(Vector3.zero) &&
                box.size.Equals(Vector3.one * (BlockWorld.CellSize - .0002f)) &&
                _fixedAdmissionScene.IsValid() && _fixedAdmissionScene.isLoaded &&
                box.gameObject.scene.handle == _fixedAdmissionScene.handle &&
                box.gameObject.scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene);
        }

        bool RejectFixedAdmissionShape(Exception error = null)
        {
            if (!_fixedAdmissionInUse && _fixedAdmissionThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId &&
                _fixedAdmissionBox != null) _fixedAdmissionBox.enabled = false;
            if (!_fixedAdmissionShapeFaultReported)
            {
                _fixedAdmissionShapeFaultReported = true; FailedTransactions++;
                const string message = "[BLOCK FIXED] Admission shape unavailable; construction rejected before commit.";
                Debug.LogError(error == null ? message : message + "\n" + error);
            }
            return false;
        }

        bool TryFixedAdmissionPenetration(Vector3 center, Collider collider, out bool penetrating, out Vector3 direction, out float depth)
        {
            penetrating = false; direction = Vector3.zero; depth = 0;
            if (!FixedAdmissionShapeReady() || collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                !collider.gameObject.scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene)) return RejectFixedAdmissionShape();
            var box = _fixedAdmissionBox;
            // Capture the map pose before activation. No other query, callback, await or simulation
            // runs while this sole helper is registered. Reentrant/off-thread calls fail closed.
            Vector3 position = collider.transform.position; Quaternion rotation = collider.transform.rotation;
            _fixedAdmissionInUse = true;
            Exception failure = null;
            try
            {
                box.enabled = true;
                using (FixedAdmissionPenetrationCpu.Auto())
                    penetrating = Physics.ComputePenetration(box, center, Quaternion.identity,
                        collider, position, rotation, out direction, out depth);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (box != null) box.enabled = false;
                _fixedAdmissionInUse = false;
            }
            // Logging can invoke application callbacks, so report only after retiring the shape.
            return failure == null || RejectFixedAdmissionShape(failure);
        }

        void DisposeFixedAdmissionShape()
        {
            var box = _fixedAdmissionBox;
            _fixedAdmissionBox = null; _fixedAdmissionScene = default; _fixedAdmissionInUse = false; _fixedAdmissionThreadId = 0;
            if (box != null)
            {
                // Retire immediately even when normal Player destruction is deferred.
                box.enabled = false; box.gameObject.SetActive(false); DisposeObject(box.gameObject);
            }
        }

        void FixedCommitted(BlockFixedGridCommit commit)
        {
            // Rebind both references before any external event can observe this commit.
            World = FixedBlocks.Storage; SurfacePaints = FixedBlocks.Surfaces;
            ResetBuildOccupancyQueryCache();
            var chunks = new HashSet<Vector3Int>();
            foreach (var cell in commit.ChangedCells) IncludeCell(chunks, cell);
            foreach (var chunk in chunks) _fixedRenderDirty.Add(chunk);
            foreach (var key in commit.ChangedPaintKeys) DirtySurfacePaint(key);
            if (commit.ChangedPaintKeys.Length != 0) SurfacePaintChanged?.Invoke();
            PublishedCommit?.Invoke(new BlockTerrainCommit(FixedBlocks.Revision, Round, chunks.ToArray()));
        }

        void ResetFixedRound(uint round)
        {
            if (round >= Round) { FixedBlocks.Clear(round); return; }
            // Leaving a match starts a fresh lifecycle at round zero. Epoch still advances,
            // so messages and handles from the previous room cannot alias this world.
            if (FixedBlocks.Epoch == uint.MaxValue) throw new InvalidOperationException("Fixed world epoch exhausted.");
            uint epoch = FixedBlocks.Epoch + 1;
            FixedBlocks.Committed -= FixedCommitted;
            var nextWorld = new BlockWorld(World.Dimensions, round, World.Bottom);
            var nextSurface = SurfacePaints.Clone(); nextSurface.Clear(round);
            World = nextWorld; SurfacePaints = nextSurface;
            FixedBlocks = new BlockFixedGridWorld(nextWorld, nextSurface, new FixedEnvironment(this), epoch);
            FixedBlocks.Committed += FixedCommitted;
        }

        FixedMapLayer[] FixedMapLayers(Vector2Int column)
        {
            var cache = _activeFixedMapChange?.Layers ?? _fixedMapLayers;
            if (cache.TryGetValue(column, out var layers)) return layers;
            using var timing = FixedMapIndexCpu.Auto();
            Vector3 center = BlockWorld.Center(new Vector3Int(column.x, 0, column.y));
            // Index every real support level once. RaycastAll deliberately has no fixed-size truncation.
            Vector3 origin = new(center.x, BlockWorld.Top + 1, center.z);
            var result = new List<FixedMapLayer>();
            // A single RaycastAll returns only the first triangle of each collider.
            // Continue below each hit to retain several floors in the same map mesh.
            while (origin.y > World.Bottom)
            {
                var hits = Physics.RaycastAll(origin, Vector3.down, origin.y - World.Bottom + .001f,
                    SurfaceMapMask, QueryTriggerInteraction.Ignore);
                float nearest = float.PositiveInfinity; RaycastHit hit = default;
                foreach (var candidate in hits)
                    if (_staticColliders.Contains(candidate.collider) && candidate.distance < nearest)
                    { nearest = candidate.distance; hit = candidate; }
                if (hit.collider == null) break;
                if (hit.normal.y > .05f)
                {
                    BlockSurfaceKey key = default;
                    if (TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint)) key = paint.Key;
                    int id = key.FaceId != 0 ? key.FaceId : hit.collider.GetInstanceID();
                    if (!result.Any(p => p.Support.Id == id && Mathf.Abs(p.Support.Height - hit.point.y) < .00001f))
                        result.Add(new FixedMapLayer(new BlockFixedGridMapSupport(id, hit.point.y, key), hit));
                }
                origin.y = Mathf.Min(origin.y - .0001f, hit.point.y - .0001f);
            }
            layers = result.ToArray(); cache.Add(column, layers); return layers;
        }

        bool TryFixedMapSupport(Vector3Int fromCell, out BlockFixedGridMapSupport support)
        {
            support = default;
            if (_fixedAdmissionMapRefreshFailed && _activeFixedMapChange == null) return false;
            // A map plane may intersect the global-grid base cube, but never lie above that cube.
            float ceiling = (fromCell.y + 1) * BlockWorld.CellSize - .00001f;
            foreach (var layer in FixedMapLayers(new Vector2Int(fromCell.x, fromCell.z)))
                if (layer.Support.Height <= ceiling && layer.Support.Height >= World.Bottom)
                { support = layer.Support; return true; }
            return false;
        }

        bool FixedPlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase, bool relocating, out string reason)
        {
            using var timing = FixedAdmissionCpu.Auto();
            reason = null;
            if (!FixedAdmissionShapeReady())
            { reason = "admission-shape-unavailable"; return RejectFixedAdmissionShape(); }
            // A failed declared map change leaves the published world intact, but its old
            // support/proof caches cannot authorize any new construction until refreshed.
            if (_fixedAdmissionMapRefreshFailed && _activeFixedMapChange == null) { reason = "map-support-refresh-unavailable"; return false; }
            if (!World.InBounds(cell)) { reason = "bounds"; return false; }
            if (cell.y < support.BaseCellY || cell.y >= support.BaseCellY + FixedMaximumLayers ||
                (cell.y + 1) * BlockWorld.CellSize - support.Height > 1.2f + .00001f)
            { reason = "height"; return false; }
            using (FixedAdmissionProtectedCpu.Auto())
                if (ExcludedConstruction(cell)) { reason = "protected"; return false; }
            if (!relocating && FixedCellOccupiedByPlayer(cell)) { reason = "player"; return false; }
            using var mapTiming = FixedAdmissionMapCpu.Auto();
            FixedMapLayer map = default; bool found = false;
            foreach (var layer in FixedMapLayers(new Vector2Int(cell.x, cell.z)))
                if (layer.Support.Id == support.Id && Mathf.Abs(layer.Support.Height - support.Height) < .00001f)
                { map = layer; found = true; break; }
            Vector3 center = BlockWorld.Center(cell), half = Vector3.one * (BlockWorld.CellSize * .5f - .0001f);
            // One concave collider may contain both this support and independent walls/ceilings.
            // Check every finite face before granting its support exemption, even if a native
            // overlap/MTD reports only the floor or produces no single separation result.
            if (found && !FixedAdmissionMeshClear(map.Hit.collider, center, half, map.Hit.point, map.Hit.normal, out reason)) return false;
            int overlapCount;
            using (FixedAdmissionOverlapCpu.Auto())
                while ((overlapCount = Physics.OverlapBoxNonAlloc(center, half, _fixedAdmissionOverlaps, Quaternion.identity,
                    SurfaceMapMask, QueryTriggerInteraction.Ignore)) == _fixedAdmissionOverlaps.Length)
                {
                    // A full buffer cannot prove completeness. Grow and repeat the exact query.
                    Array.Resize(ref _fixedAdmissionOverlaps, checked(_fixedAdmissionOverlaps.Length * 2));
                }
            try
            {
                for (int i = 0; i < overlapCount; i++)
                {
                    var collider = _fixedAdmissionOverlaps[i];
                    if (!_staticColliders.Contains(collider)) { reason = "obstacle"; return false; }
                    if (!embeddedBase || !found || collider != map.Hit.collider) { reason = "map-overlap"; return false; }
                    // Exempt penetration only along the actual supporting face. A wall or ceiling
                    // on the same mesh must still fail the narrow-phase separation proof.
                    if (!TryFixedAdmissionPenetration(center, collider, out bool penetrating, out var direction, out float depth))
                    { reason = "admission-shape-unavailable"; return false; }
                    if (!penetrating) continue;
                    if (Vector3.Dot(direction, map.Hit.normal) < .99f || direction.y <= .05f)
                    { reason = "support-side-overlap"; return false; }
                    float planeHighest = map.Hit.point.y +
                        (Mathf.Abs(map.Hit.normal.x) + Mathf.Abs(map.Hit.normal.z)) * half.x / map.Hit.normal.y;
                    float maximumVertical = Mathf.Max(0, planeHighest - (center.y - half.y));
                    if (depth / Mathf.Max(.05f, direction.y) > maximumVertical + .001f)
                    { reason = "support-depth"; return false; }
                }
            }
            finally { Array.Clear(_fixedAdmissionOverlaps, 0, overlapCount); }
            return true;
        }

        IEnumerable<Vector3Int> FixedImpactCandidates(BlockFixedGridTransaction transaction, Vector3 point, Vector3 normal, float radius)
        {
            var n = Axis(normal); var u = n.y != 0 ? Vector3Int.right : Vector3Int.up;
            var v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
            // An epsilon selects the adjacent cell even where the authored plane is off lattice.
            var first = BlockWorld.CellAt(point + (Vector3)n * .001f);
            int span = Mathf.CeilToInt(Mathf.Max(World.Dimensions.x, World.Dimensions.y) / BlockWorld.CellSize);
            int extent = Mathf.CeilToInt(Mathf.Min(radius / BlockWorld.CellSize, span));
            Vector3 source = point + (Vector3)n * .002f;
            foreach (var offset in EnumerateBuildDisk(extent, radius * radius))
            {
                var cell = first + u * offset.x + v * offset.y;
                // Read this live transaction overlay before the expensive map/visibility queries.
                // Build already rejects this same occupied candidate without spending budget.
                if (transaction.TryGet(cell, out _)) continue;
                Vector3 endpoint = BlockWorld.Center(cell);
                if (TryFixedMapSupport(cell, out var support)) endpoint.y = Mathf.Max(endpoint.y, support.Height + .002f);
                if (Visible(source, endpoint) && Visible(endpoint, source)) yield return cell;
            }
        }

        public BlockMutationResult BuildFixedImpact(Vector3 point, Vector3 normal, float radius, byte team, int count, bool mapImpact)
        {
            using var timing = FixedBuildCpu.Auto();
            if (!FixedGridMode || !_accepting || FixedBlocks.Frozen || _fixedAdmissionMapRefreshFailed || _activeFixedMapChange != null || team is not (1 or 2) || count <= 0 ||
                !Finite(point) || !Finite(normal) || normal.sqrMagnitude < .000001f || !ValidRadius(radius)) return default;
            var tx = FixedBlocks.BeginMutation();
            if (mapImpact)
            {
                if (!TryGetSurfacePaint(point, normal, out var surface)) return default;
                if (surface.Owner != team)
                    tx.Paint(SurfacePaints.EnumerateDisk(surface.Key.FaceId, surface.Key, radius)
                        .Where(key => SurfacePaints.TryGetCenter(key, out var center) && SurfacePaintFitsWorld(key, center) && !IsDynamicColumnProtected(center)), team, count);
                else BuildFixedCandidates(tx, FixedImpactCandidates(tx, point, normal, radius), team, count);
            }
            else BuildFixedCandidates(tx, FixedImpactCandidates(tx, point, normal, radius), team, count);
            if (tx.TryCommit(out var result, out string error)) return result;
            RecordFixedFailure(error); return default;
        }

        public BlockMutationResult DestroyFixedImpact(Vector3 point, float enemyRadius, float friendlyRadius, byte team,
            bool enemyOnly, int maxEnemy = int.MaxValue, int maxFriendly = int.MaxValue)
        {
            using var timing = FixedDestroyCpu.Auto();
            if (!FixedGridMode || !_accepting || FixedBlocks.Frozen || !Finite(point) ||
                !ValidRadius(enemyRadius) || !ValidRadius(friendlyRadius) || team is not (1 or 2)) return default;
            var selected = new List<(float distance, BlockFixedGridHandle handle)>();
            float radius = Mathf.Max(enemyRadius, enemyOnly ? 0 : friendlyRadius) + BlockRadiusPadding;
            var low = BlockWorld.CellAt(point - Vector3.one * radius); var high = BlockWorld.CellAt(point + Vector3.one * radius);
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
            {
                if (!FixedBlocks.TryGet(new Vector3Int(x, y, z), out var state)) continue;
                bool friendly = state.Owner == team; if (friendly && enemyOnly) continue;
                float reach = friendly ? friendlyRadius : enemyRadius;
                Vector3 center = BlockWorld.Center(state.Cell); float squared = (center - point).sqrMagnitude;
                if (reach > 0 && squared <= (reach + BlockRadiusPadding) * (reach + BlockRadiusPadding) && Visible(point, center))
                    selected.Add((squared, state.Handle));
            }
            selected.Sort((a, b) => { int c = a.distance.CompareTo(b.distance); return c != 0 ? c : a.handle.Id.CompareTo(b.handle.Id); });
            var tx = FixedBlocks.BeginMutation();
            tx.Destroy(selected.Select(s => s.handle), team, maxEnemy, enemyOnly ? 0 : maxFriendly);
            // Paint destruction is part of this same paired transaction, before settling columns.
            var paints = new List<(float distance, BlockSurfaceKey key, byte owner)>();
            foreach (var paint in enemyOnly ? Enumerable.Empty<BlockSurfaceCell>() : SurfacePaints.EnumeratePainted())
            {
                bool friendly = paint.Owner == team; if (friendly && enemyOnly) continue;
                float reach = friendly ? friendlyRadius : enemyRadius;
                Vector3 center = SurfacePaintCenter(paint.Key); float squared = (center - point).sqrMagnitude;
                if (reach > 0 && squared <= (reach + BlockRadiusPadding) * (reach + BlockRadiusPadding) && SurfacePaintVisible(point, center, paint.Key))
                    paints.Add((squared, paint.Key, paint.Owner));
            }
            paints.Sort((a, b) => a.distance.CompareTo(b.distance));
            tx.RemovePaint(paints.Select(p => p.key), team, maxEnemy, enemyOnly ? 0 : maxFriendly);
            if (tx.TryCommit(out var result, out string error)) return result;
            RecordFixedFailure(error); return default;
        }

        BlockMutationResult DestroyFixedSurfacePaint(Vector3 point, float enemyRadius, float friendlyRadius, byte team, bool enemyOnly, int maxEnemy)
        {
            if (!_accepting || FixedBlocks.Frozen || !Finite(point) || !ValidRadius(enemyRadius) || !ValidRadius(friendlyRadius)) return default;
            var keys = SurfacePaints.EnumeratePainted().Where(p =>
            {
                if (enemyOnly && p.Owner == team) return false;
                float reach = p.Owner == team ? friendlyRadius : enemyRadius;
                Vector3 center = SurfacePaintCenter(p.Key);
                return reach > 0 && (center - point).sqrMagnitude <= (reach + BlockRadiusPadding) * (reach + BlockRadiusPadding) &&
                    SurfacePaintVisible(point, center, p.Key);
            }).OrderBy(p => (SurfacePaintCenter(p.Key) - point).sqrMagnitude).Select(p => p.Key);
            using var tx = FixedBlocks.BeginMutation();
            tx.RemovePaint(keys, team, maxEnemy, enemyOnly ? 0 : int.MaxValue);
            if (tx.TryCommit(out var result, out string error)) return result;
            RecordFixedFailure(error); return default;
        }

        void RecordFixedFailure(string reason)
        { FailedTransactions++; Debug.LogError("[BLOCK FIXED] Atomic commit rejected: " + reason); }

        /// <summary>Explicit map-adapter invalidation; ordinary stationary maps need no polling.</summary>
        public bool NotifyFixedMapSupportChanged(Bounds changedGeometry)
        {
            if (!FixedGridMode || !_authority || !_accepting || FixedBlocks.Frozen || _activeFixedMapChange != null) return false;
            if (_fixedAdmissionMapRefreshFailed) changedGeometry.Encapsulate(_fixedAdmissionPendingGeometry);
            FixedAdmissionMapChange stage;
            try { stage = PrepareFixedMapChange(changedGeometry); }
            catch (Exception error)
            {
                _fixedAdmissionMapRefreshFailed = true; _fixedAdmissionPendingGeometry = changedGeometry;
                RecordFixedFailure("map support geometry bake failed: " + error.GetType().Name + ": " + error.Message); return false;
            }
            bool committed = false; string failure = null;
            var previousProofs = _fixedAdmissionMeshProofs;
            _activeFixedMapChange = stage;
            try
            {
                using var tx = FixedBlocks.BeginMutation(); tx.ReconcileColumns(stage.Columns);
                committed = tx.TryCommit(out _, out failure);
                if (committed)
                {
                    _fixedAdmissionMeshProofs = stage.Proofs; _fixedMapLayers = stage.Layers;
                    _fixedAdmissionMapRefreshFailed = false; _fixedAdmissionPendingGeometry = default;
                    _fixedAdmissionMeshProofFaultReported = false;
                }
            }
            catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
            finally
            {
                _activeFixedMapChange = null;
                if (!committed)
                {
                    _fixedAdmissionMapRefreshFailed = true; _fixedAdmissionPendingGeometry = changedGeometry;
                    stage.DisposeCreated();
                }
            }
            if (!committed) { RecordFixedFailure(failure); return false; }
            foreach (var pair in previousProofs)
                if (!stage.Proofs.TryGetValue(pair.Key, out var retained) || !ReferenceEquals(pair.Value, retained)) pair.Value.Dispose();
            // Retained/new proofs now belong to the published cache, not the temporary view.
            stage.Created.Clear(); return true;
        }

        void AdvanceFixedRendering()
        {
            using var timing = FixedRenderCpu.Auto();
            if (_fixedRenderTask != null)
            {
                if (!_fixedRenderTask.IsCompleted) return;
                var task = _fixedRenderTask; _fixedRenderTask = null;
                if (task.IsFaulted) { Debug.LogException(task.Exception); return; }
                foreach (var piece in task.Result)
                {
                    if (_fixedMeshes.Remove(piece.key, out var old)) DisposeObject(old);
                    if (piece.mesh.Vertices.Length != 0) _fixedMeshes[piece.key] = piece.mesh.Upload("Fixed block chunk " + piece.key);
                }
            }
            if (_fixedRenderDirty.Count == 0 || _destroying) return;
            var keys = _fixedRenderDirty.Take(8).ToArray(); foreach (var key in keys) _fixedRenderDirty.Remove(key);
            var snapshot = World.Clone();
            _fixedRenderTask = Task.Run(() => keys.Select(key => (key, BlockMeshBuilder.Build(snapshot, key, false))).ToArray());
        }

        void DrawFixedRendering()
        {
            if (_material == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            foreach (var pair in _fixedMeshes)
                Graphics.DrawMesh(pair.Value, Matrix4x4.Translate((Vector3)BlockWorld.ChunkOrigin(pair.Key) * BlockWorld.CellSize),
                    _material, TerrainLayer, null, 0, null, ShadowCastingMode.On, true);
        }

        void ClearFixedRendering()
        {
            _fixedRenderEpoch++; _fixedRenderTask = null;
            foreach (var mesh in _fixedMeshes.Values) DisposeObject(mesh);
            _fixedMeshes.Clear(); _fixedRenderDirty.Clear(); _fixedMapLayers.Clear();
        }

        void DisposeFixedGrid()
        {
            if (FixedBlocks != null) FixedBlocks.Committed -= FixedCommitted;
            ClearFixedRendering();
            DisposeFixedAdmissionMeshProofs();
            DisposeFixedAdmissionShape();
        }
    }
}
