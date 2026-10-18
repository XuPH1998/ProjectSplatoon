using System;
using System.Collections.Generic;
using Splatoon.Prototype;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Blocks
{
    [Serializable]
    public struct BlockDynamicState
    {
        public ulong Id;
        public uint Round;
        public byte Owner;
        public Vector3 Position, Velocity, AngularVelocity;
        public Quaternion Rotation;
        public bool Sleeping;
    }

    /// <summary>Host PhysX simulation and client collision replicas, independent of mesh publication.</summary>
    [DefaultExecutionOrder(-200)]
    public sealed partial class BlockDynamicWorld : MonoBehaviour
    {
        public const int Layer = 28;
        public const int CollisionPolicyVersion = 1;
        public const CollisionDetectionMode AuthorityCollisionMode = CollisionDetectionMode.ContinuousDynamic;
        public const CollisionDetectionMode FrozenAuthorityCollisionMode = CollisionDetectionMode.ContinuousSpeculative;
        public const CollisionDetectionMode ReplicaCollisionMode = CollisionDetectionMode.Discrete;
        public const float Mass = .5f, StaticFriction = .35f, DynamicFriction = .2f, PushForce = 100f, PushSpeed = 3f;
        public const float BuildRadiusScale = BlockWorld.CellSize / .10f;
        static readonly ProfilerMarker BuildCpu = new("Splatoon.Blocks.DynamicBuild");
        static readonly ProfilerMarker DrawCpu = new("Splatoon.Blocks.DynamicRender");
        static readonly ProfilerMarker DynamicStateReadCpu = new("Splatoon.Blocks.DynamicStateRead");
        static readonly ProfilerMarker DynamicStateSortCpu = new("Splatoon.Blocks.DynamicStateSort");
        const int CachedBuildExtent = 64, CachedBuildDisks = 64;
        readonly Dictionary<(int extent, float radius), Vector2Int[]> _buildDisks = new();
        readonly Queue<(int extent, float radius)> _buildDiskOrder = new();
        readonly List<Vector2Int> _verticalBuildOffsets = new();
        readonly Dictionary<Vector2Int, bool> _buildColumnProtection = new();
        readonly Dictionary<ulong, BlockDynamicBody> _bodies = new();
        readonly Stack<BlockDynamicBody> _pool = new();
        readonly List<ulong> _removed = new();
        readonly HashSet<ulong> _removalIds = new();
        readonly List<Bounds> _supportChanges = new();
        readonly List<BlockDynamicBody> _targets = new();
        readonly HashSet<ulong> _snapshotIds = new();
        readonly Matrix4x4[] _matrices = new Matrix4x4[1023];
        readonly RaycastHit[] _visibility = new RaycastHit[64];
        BlockTerrain _terrain;
        PhysicsMaterial _physics;
        Material _pinkMaterial, _blueMaterial;
        Mesh _mesh;
        ulong _nextId;
        int _pink, _blue;
        bool _disposed;
        public uint Round { get; private set; }
        public uint LifecycleRevision { get; private set; }
        public bool Frozen { get; private set; }
        public bool IsAuthority { get; private set; }
        public int Count => _bodies.Count;
        public int PinkCount => _pink;
        public int BlueCount => _blue;
        public IReadOnlyDictionary<ulong, BlockDynamicBody> Bodies => _bodies;
        public long CreatedCount { get; private set; }
        public long RemovedCount { get; private set; }
        public event Action Changed;
        public event Action<int, double> BodiesBuilt;
        [Serializable] public struct BuildWorkDiagnostics
        {
            public long calls, requested, added, candidateChecks, protectionChecks;
            public long boundsCandidatesSkipped, protectedCandidatesSkipped;
            public double cpuMilliseconds, maximumCpuMilliseconds;
        }
        BuildWorkDiagnostics _buildWork;
        public BuildWorkDiagnostics InspectBuildWork() => _buildWork;
        public BuildWorkDiagnostics LastBuildWork { get; private set; }
        public event Action<BuildWorkDiagnostics> BuildObserved;
        // Acceptance sampling is enabled only outside measured gameplay intervals.
        public bool CaptureGeometryEvidence { get; set; }
        public event Action<Mesh, Matrix4x4, int, bool> GeometryDrawObserved;

        public static void ConfigureCollisionLayers()
        {
            Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, true);
            Physics.IgnoreLayerCollision(Layer, Layer, false);
            Physics.IgnoreLayerCollision(Layer, 8, false);
            Physics.IgnoreLayerCollision(Layer, BlockTerrain.TerrainLayer, false);
        }

        public void Initialize(BlockTerrain terrain)
        {
            _terrain = terrain; _disposed = false;
            if (_physics != null) return;
            Round = terrain.Round;
            _physics = new PhysicsMaterial("Free block friction") { staticFriction = StaticFriction,
                dynamicFriction = DynamicFriction, bounciness = 0, frictionCombine = PhysicsMaterialCombine.Average,
                bounceCombine = PhysicsMaterialCombine.Average };
            ConfigureCollisionLayers();
        }

        public bool Contains(BlockDynamicBody body) => body != null && body.World == this &&
            _bodies.TryGetValue(body.Id, out var current) && current == body;

        public bool TryGet(ulong id, out BlockDynamicBody body) => _bodies.TryGetValue(id, out body);

        public bool TrySpawn(byte owner, Vector3 position, Quaternion rotation, out BlockDynamicBody body)
        {
            body = null;
            if (!IsAuthority || Frozen || owner is not (1 or 2) || _terrain == null || !Finite(position) ||
                !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)) return false;
            float rotationLength = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (!float.IsFinite(rotationLength) || rotationLength < .000001f) return false;
            rotation = Quaternion.Normalize(rotation);
            if (!_terrain.IsDynamicSpawnAllowed(position, rotation)) return false;
            body = Acquire(++_nextId, owner, position, rotation); CreatedCount++; LifecycleRevision++;
            Physics.SyncTransforms(); Changed?.Invoke(); return true;
        }

        public void SetAuthority(bool authority)
        {
            if (IsAuthority == authority) return;
            ClearActivationState(false);
            IsAuthority = authority;
            foreach (var body in _bodies.Values) { body.HasReplicaPose = false; ConfigureBody(body); }
        }

        void ConfigureBody(BlockDynamicBody block)
        {
            bool kinematic = !IsAuthority || Frozen;
            if (block.Body.isKinematic != kinematic) block.Body.isKinematic = kinematic;
            bool gravity = IsAuthority && !Frozen;
            if (block.Body.useGravity != gravity) block.Body.useGravity = gravity;
            var collisionMode = !IsAuthority ? ReplicaCollisionMode
                : Frozen ? FrozenAuthorityCollisionMode : AuthorityCollisionMode;
            if (block.Body.collisionDetectionMode != collisionMode) block.Body.collisionDetectionMode = collisionMode;
        }

        BlockDynamicBody Acquire(ulong id, byte owner, Vector3 position, Quaternion rotation)
        {
            BlockDynamicBody block;
            if (_pool.Count != 0) block = _pool.Pop();
            else
            {
                var obj = new GameObject("Free block") { layer = Layer };
                obj.SetActive(false); obj.transform.SetParent(transform, false);
                block = obj.AddComponent<BlockDynamicBody>();
                block.Collider = obj.AddComponent<BoxCollider>();
                block.Collider.size = Vector3.one * BlockWorld.CellSize;
                block.Collider.sharedMaterial = _physics;
                block.Body = obj.AddComponent<Rigidbody>();
                block.Body.mass = Mass; block.Body.maxAngularVelocity = 20;
                block.Body.interpolation = RigidbodyInterpolation.None;
                block.Body.solverIterations = 6; block.Body.solverVelocityIterations = 2;
            }
            block.Id = id; block.Owner = owner; block.World = this; block.MotionSequence = 0; block.RemoteSleeping = false;
            block.HasFrozenState = block.HasReplicaPose = block.Parked = false; block.StableSleepSamples = 0;
            block.RemoteVelocity = block.RemoteAngularVelocity = Vector3.zero;
            rotation = Quaternion.Normalize(rotation);
            block.transform.SetPositionAndRotation(position, rotation);
            block.Body.position = position; block.Body.rotation = rotation;
            ConfigureBody(block);
            if (!block.Body.isKinematic) { block.Body.linearVelocity = Vector3.zero; block.Body.angularVelocity = Vector3.zero; }
            block.Collider.enabled = true; block.gameObject.SetActive(true);
            // PhysX ignores contactOffset writes while this collider is inactive.
            // Apply the authored margin after activation, including pooled births.
            block.Collider.contactOffset = .002f;
            _bodies.Add(id, block); if (owner == 1) _pink++; else _blue++;
            return block;
        }

        void Release(BlockDynamicBody block, bool notifySupport = true)
        {
            // Include native sleepers with no management tag. Reset removes the
            // entire world and skips repeated support graph construction.
            if (notifySupport) NotifySupportGeometryChanging(block.Collider.bounds);
            DropManagement(block);
            _bodies.Remove(block.Id); if (block.Owner == 1) _pink--; else _blue--;
            block.Collider.enabled = false;
            block.Body.isKinematic = true; block.gameObject.SetActive(false);
            block.World = null; block.Owner = 0; block.Parked = block.HasReplicaPose = false;
            block.StableSleepSamples = 0; _pool.Push(block);
        }

        public int Build(Vector3 point, Vector3 normal, float radius, byte owner, int count)
        {
            if (!IsAuthority || Frozen || _terrain == null || count <= 0 || owner is not (1 or 2) || !Finite(point)) return 0;
            using var marker = BuildCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var work = new BuildWorkDiagnostics { calls = 1, requested = count };
            try
            {
                Vector3Int n = Axis(normal), u = n.y != 0 ? Vector3Int.right : Vector3Int.up;
                Vector3Int v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
                var first = BlockWorld.CellAt(point + (Vector3)n * (BlockWorld.CellSize * .51f));
                int extent = Mathf.CeilToInt(radius / BlockWorld.CellSize), added = 0;
                var disk = CachedDisk(extent, radius);
                ClipBuildLayers(first, n, count, out int startLayer, out int endLayer);
                if (disk != null) work.boundsCandidatesSkipped += (long)(count - (endLayer - startLayer)) * disk.Length;
                if (startLayer == endLayer) return 0;
                _buildColumnProtection.Clear();
                bool filterVertical = disk != null && n.y != 0;
                if (filterVertical) _verticalBuildOffsets.Clear();
                // Keep the authored disk prefix. The first layer proves columns
                // lazily, so a successful first candidate does not scan the disk.
                // Only bounds/protection are invariant; all occupancy stays live.
                IEnumerable<Vector2Int> orderedOffsets = (IEnumerable<Vector2Int>)disk ?? Disk(extent, radius);
                for (int layer = startLayer; layer < endLayer && added < count; layer++)
                {
                    foreach (var offset in orderedOffsets)
                    {
                        var cell = first + n * layer + u * offset.x + v * offset.y;
                        var center = BlockWorld.Center(cell);
                        if (!filterVertical || layer == startLayer)
                        {
                            int copies = filterVertical ? endLayer - startLayer : 1;
                            if (!BuildTransverseInBounds(center, n)) { work.boundsCandidatesSkipped += copies; continue; }
                            var column = new Vector2Int(cell.x, cell.z);
                            if (!_buildColumnProtection.TryGetValue(column, out bool protectedColumn))
                            {
                                work.protectionChecks++;
                                protectedColumn = _terrain.IsDynamicColumnProtected(center);
                                _buildColumnProtection.Add(column, protectedColumn);
                            }
                            if (protectedColumn) { work.protectedCandidatesSkipped += copies; continue; }
                            if (filterVertical) _verticalBuildOffsets.Add(offset);
                        }
                        work.candidateChecks++;
                        if (!_terrain.IsDynamicSpawnAllowed(center, true)) continue;
                        // An empty endpoint can lie beyond a thin floor or wall.
                        // Keep the disk order, but only birth on the hit side of
                        // authored map geometry. Free/attached blocks do not mask
                        // later candidates in this same synchronous build.
                        Vector3 source = point + (Vector3)n * .002f;
                        // The reverse query also catches an embedded contact:
                        // a ray starting inside solid map geometry misses its exit.
                        if (!Visible(source, center) || !Visible(center, source)) continue;
                        Acquire(++_nextId, owner, center, Quaternion.identity); added++; CreatedCount++;
                        if (added == count) break;
                    }
                    if (filterVertical && layer == startLayer)
                    {
                        // Every later layer has these exact x/z columns. If the
                        // entire disk was refused, no layer can admit a birth.
                        if (_verticalBuildOffsets.Count == 0) break;
                        orderedOffsets = _verticalBuildOffsets;
                    }
                }
                work.added = added;
                if (added > 0)
                {
                    LifecycleRevision++; Physics.SyncTransforms(); Changed?.Invoke();
                    BodiesBuilt?.Invoke(added, (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                return added;
            }
            finally
            {
                work.cpuMilliseconds = work.maximumCpuMilliseconds =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                LastBuildWork = work;
                _buildWork.calls++; _buildWork.requested += work.requested; _buildWork.added += work.added;
                _buildWork.candidateChecks += work.candidateChecks; _buildWork.protectionChecks += work.protectionChecks;
                _buildWork.boundsCandidatesSkipped += work.boundsCandidatesSkipped;
                _buildWork.protectedCandidatesSkipped += work.protectedCandidatesSkipped;
                _buildWork.cpuMilliseconds += work.cpuMilliseconds;
                _buildWork.maximumCpuMilliseconds = Math.Max(_buildWork.maximumCpuMilliseconds, work.cpuMilliseconds);
                BuildObserved?.Invoke(work);
            }
        }

        Vector2Int[] CachedDisk(int extent, float radius)
        {
            // Cache size is a memory bound, never a candidate or payment limit.
            // Very large authored disks retain their original streaming path.
            if (extent > CachedBuildExtent) return null;
            var key = (extent, radius);
            if (_buildDisks.TryGetValue(key, out var disk)) return disk;
            var offsets = new List<Vector2Int>();
            foreach (var offset in Disk(extent, radius)) offsets.Add(offset);
            disk = offsets.ToArray();
            if (_buildDisks.Count == CachedBuildDisks) _buildDisks.Remove(_buildDiskOrder.Dequeue());
            _buildDisks.Add(key, disk); _buildDiskOrder.Enqueue(key); return disk;
        }

        bool BuildTransverseInBounds(Vector3 center, Vector3Int n)
        {
            float half = BlockWorld.CellSize * .5f;
            return (n.x != 0 || Mathf.Abs(center.x) + half <= _terrain.World.Dimensions.x * .5f) &&
                (n.z != 0 || Mathf.Abs(center.z) + half <= _terrain.World.Dimensions.y * .5f) &&
                (n.y != 0 || center.y - half >= _terrain.World.Bottom && center.y + half <= BlockWorld.Top);
        }

        void ClipBuildLayers(Vector3Int first, Vector3Int n, int count, out int start, out int end)
        {
            start = 0; end = count;
            int axis = n.x != 0 ? 0 : n.y != 0 ? 1 : 2, direction = n[axis];
            long last = (long)first[axis] + (long)direction * (count - 1);
            // Preserve the old integer-wrapping path for exotic invalid lattice
            // coordinates. Normal world coordinates always use the exact clip.
            if (last < int.MinValue || last > int.MaxValue) return;
            bool Past(int layer, bool entering)
            {
                var cell = first; cell[axis] = (int)((long)first[axis] + (long)direction * layer);
                float center = BlockWorld.Center(cell)[axis], half = BlockWorld.CellSize * .5f;
                bool below = axis == 1 ? center - half < _terrain.World.Bottom
                    : center < 0 && Mathf.Abs(center) + half > (axis == 0 ? _terrain.World.Dimensions.x : _terrain.World.Dimensions.y) * .5f;
                bool above = axis == 1 ? center + half > BlockWorld.Top
                    : center >= 0 && Mathf.Abs(center) + half > (axis == 0 ? _terrain.World.Dimensions.x : _terrain.World.Dimensions.y) * .5f;
                return entering ? direction > 0 ? !below : !above : direction > 0 ? above : below;
            }
            int Boundary(bool entering)
            {
                int low = 0, high = count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (Past(middle, entering)) high = middle; else low = middle + 1;
                }
                return low;
            }
            start = Boundary(true); end = Math.Max(start, Boundary(false));
        }

        static IEnumerable<Vector2Int> Disk(int extent, float radius)
        {
            yield return Vector2Int.zero;
            float squared = radius * radius;
            for (int ring = 1; ring <= extent; ring++)
            {
                for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    if ((x * x + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= squared + .00001f) yield return new Vector2Int(x, ring * side);
                for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    if ((y * y + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= squared + .00001f) yield return new Vector2Int(ring * side, y);
            }
        }

        static Vector3Int Axis(Vector3 n) => Mathf.Abs(n.y) >= Mathf.Max(Mathf.Abs(n.x), Mathf.Abs(n.z)) ? (n.y >= 0 ? Vector3Int.up : Vector3Int.down)
            : Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? (n.x >= 0 ? Vector3Int.right : Vector3Int.left) : (n.z >= 0 ? Vector3Int.forward : Vector3Int.back);

        public BlockMutationResult DestroyInRadius(Vector3 point, float enemyRadius, float friendlyRadius, byte team, bool enemyOnly, int maxEnemy = int.MaxValue)
        {
            var result = new BlockMutationResult();
            if (!IsAuthority || Frozen) return result;
            _targets.Clear();
            foreach (var block in _bodies.Values)
            {
                bool friendly = block.Owner == team;
                if (friendly && enemyOnly) continue;
                float radius = friendly ? friendlyRadius : enemyRadius;
                if (radius <= 0 || (block.Body.position - point).sqrMagnitude > Mathf.Pow(radius + BlockWorld.CellSize * .5f + .001f, 2) || !Visible(point, block.Body.position)) continue;
                _targets.Add(block);
            }
            _targets.Sort((a, b) => { int d = (a.Body.position - point).sqrMagnitude.CompareTo((b.Body.position - point).sqrMagnitude); return d != 0 ? d : a.Id.CompareTo(b.Id); });
            _removed.Clear(); _supportChanges.Clear();
            foreach (var block in _targets)
            {
                if (block.Owner == team) result.RemovedFriendly++;
                else { if (result.RemovedEnemy >= maxEnemy) continue; result.RemovedEnemy++; }
                _removed.Add(block.Id); _supportChanges.Add(block.Collider.bounds);
            }
            RemoveSelectedBodies();
            if (result.Changed > 0) { LifecycleRevision++; Changed?.Invoke(); }
            result.Revision = LifecycleRevision; return result;
        }

        public int DestroyIds(IReadOnlyList<ulong> ids, byte enemyOf)
        {
            if (!IsAuthority || Frozen) return 0;
            _removed.Clear(); _supportChanges.Clear(); _removalIds.Clear();
            foreach (ulong id in ids)
                if (_removalIds.Add(id) && _bodies.TryGetValue(id, out var block) && block.Owner != enemyOf)
                { _removed.Add(id); _supportChanges.Add(block.Collider.bounds); }
            int removed = _removed.Count;
            RemoveSelectedBodies();
            if (removed != 0) { LifecycleRevision++; Changed?.Invoke(); }
            return removed;
        }

        void RemoveSelectedBodies()
        {
            if (_removed.Count == 0) return;
            // Resolve the selected contacts once, before any collider vanishes.
            // A blast can remove many masses from the same sleeping pile.
            NotifySupportGeometryChanging(_supportChanges);
            foreach (ulong id in _removed) { Release(_bodies[id], false); RemovedCount++; }
        }

        public bool Visible(Vector3 from, Vector3 to)
        {
            var delta = to - from; float distance = delta.magnitude;
            if (distance < .001f) return true;
            int count = Physics.RaycastNonAlloc(from + delta.normalized * .002f, delta.normalized, _visibility, Mathf.Max(0, distance - .004f),
                ~((1 << Layer) | (1 << BlockTerrain.TerrainLayer) | (1 << 8) | (1 << 10)), QueryTriggerInteraction.Ignore);
            if (count == _visibility.Length) return false;
            for (int i = 0; i < count; i++) if (_terrain.IsAuthoredMapCollider(_visibility[i].collider)) return false;
            return true;
        }

        public void CaptureStates(List<BlockDynamicState> target)
        {
            using (DynamicStateReadCpu.Auto())
            {
                target.Clear();
                foreach (var block in _bodies.Values)
                {
                    if (Frozen && block.HasFrozenState) { target.Add(block.FrozenState); continue; }
                    // Native contact/force can wake a managed mass during this
                    // solve, before the next range pass clears its stale tag.
                    bool sleeping = Frozen || (IsAuthority ? block.IsAtRest : block.RemoteSleeping);
                    target.Add(new BlockDynamicState { Id = block.Id, Round = Round, Owner = block.Owner, Position = block.Body.position,
                        Rotation = block.Body.rotation, Velocity = Frozen ? Vector3.zero : IsAuthority ? block.Body.isKinematic ? Vector3.zero : block.Body.linearVelocity : block.RemoteVelocity,
                        AngularVelocity = Frozen ? Vector3.zero : IsAuthority ? block.Body.isKinematic ? Vector3.zero : block.Body.angularVelocity : block.RemoteAngularVelocity, Sleeping = sleeping });
                }
            }
            using (DynamicStateSortCpu.Auto()) target.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        public bool ApplySnapshot(uint round, uint revision, ulong sequence, IReadOnlyList<BlockDynamicState> states, bool frozen)
        {
            if (IsAuthority || round < Round || round == Round && revision < LifecycleRevision || states == null) return false;
            _snapshotIds.Clear();
            foreach (var state in states)
                if (!Valid(state, round) || !_snapshotIds.Add(state.Id) || round == Round && _bodies.TryGetValue(state.Id, out var existing) && existing.Owner != state.Owner) return false;
            if (round != Round) Reset(round);
            Frozen = frozen; _removed.Clear();
            foreach (var pair in _bodies) if (!_snapshotIds.Contains(pair.Key)) _removed.Add(pair.Key);
            foreach (ulong id in _removed) Release(_bodies[id]);
            foreach (var state in states)
            {
                if (!_bodies.TryGetValue(state.Id, out var block)) block = Acquire(state.Id, state.Owner, state.Position, state.Rotation);
                ApplyState(block, state, sequence, frozen);
            }
            LifecycleRevision = revision; Physics.SyncTransforms(); Changed?.Invoke(); return true;
        }

        public bool ApplyMotion(uint round, uint revision, ulong sequence, IReadOnlyList<BlockDynamicState> states)
        {
            if (IsAuthority || Frozen || round != Round || revision != LifecycleRevision || states == null) return false;
            foreach (var state in states) if (!Valid(state, round)) return false;
            foreach (var state in states)
                if (_bodies.TryGetValue(state.Id, out var block) && state.Owner == block.Owner) ApplyState(block, state, sequence, false);
            Physics.SyncTransforms(); return true;
        }

        void ApplyState(BlockDynamicBody block, BlockDynamicState state, ulong sequence, bool force)
        {
            if (!force && sequence < block.MotionSequence) return;
            if (force) RememberFrozenState(block, state);
            else block.HasFrozenState = false;
            block.MotionSequence = sequence; block.RemoteSleeping = state.Sleeping;
            block.RemoteVelocity = state.Velocity; block.RemoteAngularVelocity = state.AngularVelocity;
            if (block.HasReplicaPose && block.ReplicaWirePosition.Equals(state.Position) && block.ReplicaWireRotation.Equals(state.Rotation))
            { _activation.skippedReplicaPoseWrites++; return; }
            var nativeRotation = Quaternion.Normalize(state.Rotation);
            block.Body.position = state.Position; block.Body.rotation = nativeRotation;
            block.transform.SetPositionAndRotation(state.Position, nativeRotation);
            block.ReplicaWirePosition = state.Position; block.ReplicaWireRotation = state.Rotation;
            block.HasReplicaPose = true; _activation.replicaPoseWrites++;
        }

        static bool Valid(BlockDynamicState state, uint round) => state.Id != 0 && state.Round == round && state.Owner is 1 or 2 &&
            Finite(state.Position) && Finite(state.Velocity) && Finite(state.AngularVelocity) &&
            float.IsFinite(state.Rotation.x) && float.IsFinite(state.Rotation.y) && float.IsFinite(state.Rotation.z) && float.IsFinite(state.Rotation.w) &&
            state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y + state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w > .5f;
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        public void Freeze()
        {
            if (Frozen) return;
            Frozen = true;
            ClearActivationState(false);
            foreach (var body in _bodies.Values)
            {
                RememberFrozenState(body, new BlockDynamicState { Id = body.Id, Round = Round, Owner = body.Owner,
                    Position = body.Body.position, Rotation = body.Body.rotation });
                if (!body.Body.isKinematic) { body.Body.linearVelocity = Vector3.zero; body.Body.angularVelocity = Vector3.zero; }
                ConfigureBody(body);
            }
            LifecycleRevision++; Changed?.Invoke();
        }

        static void RememberFrozenState(BlockDynamicBody body, BlockDynamicState state)
        {
            state.Sleeping = true; state.Velocity = state.AngularVelocity = Vector3.zero;
            body.FrozenState = state; body.HasFrozenState = true;
        }

        public void Reset(uint round)
        {
            ClearActivationState(true);
            _removed.Clear(); foreach (ulong id in _bodies.Keys) _removed.Add(id);
            foreach (ulong id in _removed) Release(_bodies[id], false);
            Round = round; LifecycleRevision = 0; Frozen = false; CreatedCount = RemovedCount = 0;
            _buildWork = default; LastBuildWork = default;
            Changed?.Invoke();
        }

        void EnsureRendering()
        {
            if (_mesh != null) return;
            var template = Resources.Load<Material>("BlockProjectileInstanced");
            if (template == null) return;
            _pinkMaterial = new Material(template) { enableInstancing = true };
            _blueMaterial = new Material(template) { enableInstancing = true };
            _pinkMaterial.SetColor("_TeamColor", PrototypeArena.Pink); _blueMaterial.SetColor("_TeamColor", PrototypeArena.Blue);
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube); primitive.SetActive(false);
            _mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Destroy(primitive); else DestroyImmediate(primitive);
        }

        void LateUpdate()
        {
            if (_bodies.Count == 0 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsInstancing) return;
            using var marker = DrawCpu.Auto(); EnsureRendering(); if (_mesh == null) return;
            for (byte owner = 1; owner <= 2; owner++)
            {
                int count = 0; var material = owner == 1 ? _pinkMaterial : _blueMaterial;
                foreach (var block in _bodies.Values)
                {
                    if (block.Owner != owner) continue;
                    _matrices[count++] = Matrix4x4.TRS(block.Body.position, block.Body.rotation, Vector3.one * BlockWorld.CellSize);
                    if (count == _matrices.Length) { DrawBatch(material, count); count = 0; }
                }
                if (count != 0) DrawBatch(material, count);
            }
        }

        void DrawBatch(Material material, int count)
        {
            Graphics.DrawMeshInstanced(_mesh, 0, material, _matrices, count, null, ShadowCastingMode.On, true, Layer);
            // Observe the actual mesh and submitted instance matrix after the draw call.
            if (CaptureGeometryEvidence)
                GeometryDrawObserved?.Invoke(_mesh, _matrices[0], count, material != null && material.shader != null && material.shader.isSupported);
        }

        public void DisposeWorld()
        {
            if (_disposed) return;
            _disposed = true; Reset(Round);
            while (_pool.Count > 0) { var body = _pool.Pop(); if (body != null) { if (Application.isPlaying) Destroy(body.gameObject); else DestroyImmediate(body.gameObject); } }
            ReleaseAsset(_physics); ReleaseAsset(_pinkMaterial); ReleaseAsset(_blueMaterial); _physics = null; _pinkMaterial = _blueMaterial = null;
        }
        void ReleaseAsset(UnityEngine.Object asset) { if (asset == null) return; if (Application.isPlaying) Destroy(asset); else DestroyImmediate(asset); }
        void OnDestroy() => DisposeWorld();
    }
}
