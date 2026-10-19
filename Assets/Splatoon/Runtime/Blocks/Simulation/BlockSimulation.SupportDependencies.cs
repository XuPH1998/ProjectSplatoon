using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Splatoon.Blocks.Simulation
{
    // Per-Step support work only; API mutations before Step are intentionally excluded.
    // MapBurstMilliseconds is scheduling plus Complete, nested inside MapBatchMilliseconds.
    public struct BlockSupportDiagnostics
    {
        public int RefreshCalls, MapBatches;
        public long DirtyBodies, GeometryDirtyBodies, MapQueries, MovedBodies;
        public double MovementMilliseconds, PoseMilliseconds, MapBatchMilliseconds, MapBurstMilliseconds;
        public double EdgeRepairMilliseconds, ClosureMilliseconds, CleanupMilliseconds;
        public double RefreshMilliseconds, TotalMilliseconds;
    }

    public sealed partial class BlockSimulation
    {
        BlockSupportDiagnostics _supportDiagnostics;
        public BlockSupportDiagnostics SupportDiagnostics => _supportDiagnostics;

        const byte SupportAbsent = 0, SupportPresent = 1, SupportPending = 2;
        const byte SupportGeometryDirty = 1, SupportMapDirty = 2;

        struct SupportMapRootResult
        {
            public byte MapRoot, ScratchCapacityGrew;
        }

        struct SupportNode
        {
            public float3 Position;
            public int DirtySlot;
            public byte MapRoot, Status, Frozen, Dirty, Queued;
        }

        // Persistent full-handle adjacency, independent of contact generation and solver islands.
        // A sleeping/sleeping edge stays present until its actual geometry or lifecycle changes.
        NativeParallelHashMap<BlockSimHandle, SupportNode> _supportNodes;
        NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle> _supportLowers, _supportUppers;
        NativeParallelHashMap<BlockSimHandle, byte> _supportVisited;
        NativeList<BlockSimHandle> _supportDirty, _supportClosure, _supportScratch;
        NativeList<int> _supportMoved;
        NativeList<BlockStaticHit> _supportMapHits;
        NativeList<SupportMapRootResult> _supportMapRoots;
        NativeReference<int> _supportFault;
        int _supportEdgeCount;

        void InitializeSupportDependencies(int capacity)
        {
            try
            {
                _supportNodes = new NativeParallelHashMap<BlockSimHandle, SupportNode>(capacity, Allocator.Persistent);
                _supportLowers = new NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle>(capacity, Allocator.Persistent);
                _supportUppers = new NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle>(capacity, Allocator.Persistent);
                _supportVisited = new NativeParallelHashMap<BlockSimHandle, byte>(capacity, Allocator.Persistent);
                _supportDirty = new NativeList<BlockSimHandle>(capacity, Allocator.Persistent);
                _supportClosure = new NativeList<BlockSimHandle>(capacity, Allocator.Persistent);
                _supportScratch = new NativeList<BlockSimHandle>(capacity, Allocator.Persistent);
                _supportMoved = new NativeList<int>(capacity, Allocator.Persistent);
                _supportMapHits = new NativeList<BlockStaticHit>(16, Allocator.Persistent);
                _supportMapRoots = new NativeList<SupportMapRootResult>(capacity, Allocator.Persistent);
                _supportFault = new NativeReference<int>(Allocator.Persistent);
            }
            catch { DisposeSupportDependencies(); throw; }
        }

        void DisposeSupportDependencies()
        {
            if (_supportNodes.IsCreated) _supportNodes.Dispose();
            if (_supportLowers.IsCreated) _supportLowers.Dispose();
            if (_supportUppers.IsCreated) _supportUppers.Dispose();
            if (_supportVisited.IsCreated) _supportVisited.Dispose();
            if (_supportDirty.IsCreated) _supportDirty.Dispose();
            if (_supportClosure.IsCreated) _supportClosure.Dispose();
            if (_supportScratch.IsCreated) _supportScratch.Dispose();
            if (_supportMoved.IsCreated) _supportMoved.Dispose();
            if (_supportMapHits.IsCreated) _supportMapHits.Dispose();
            if (_supportMapRoots.IsCreated) _supportMapRoots.Dispose();
            if (_supportFault.IsCreated) _supportFault.Dispose();
        }

        void ClearSupportDependencies()
        {
            _supportNodes.Clear(); _supportLowers.Clear(); _supportUppers.Clear(); _supportVisited.Clear();
            _supportDirty.Clear(); _supportClosure.Clear(); _supportScratch.Clear(); _supportMoved.Clear();
            _supportMapHits.Clear(); _supportMapRoots.Clear(); _supportFault.Value = 0; _supportEdgeCount = 0;
        }

        void EnsureSupportBodyCapacity(int required)
        {
            if (_supportNodes.Capacity < required)
            { _supportNodes.Capacity = Grow(required); _diagnostics.CapacityGrowths++; }
            if (_supportVisited.Capacity < required)
            { _supportVisited.Capacity = Grow(required); _diagnostics.CapacityGrowths++; }
            SupportListCapacity(ref _supportMoved, required);
        }

        void SupportListCapacity<T>(ref NativeList<T> list, int required) where T : unmanaged
        {
            if (list.Capacity >= required) return;
            list.Capacity = Grow(required); _diagnostics.CapacityGrowths++;
        }

        void SupportAppend<T>(ref NativeList<T> list, T value) where T : unmanaged
        {
            if (list.Length == int.MaxValue) throw new InvalidOperationException("Support storage exceeds addressable capacity; no dependency was omitted.");
            SupportListCapacity(ref list, list.Length + 1); list.Add(value);
        }

        void MarkSupportDirty(BlockSimHandle handle, byte flags)
        {
            if (!_supportNodes.TryGetValue(handle, out var node)) return;
            node.Dirty |= flags;
            if (node.Queued == 0)
            { node.DirtySlot = _supportDirty.Length; SupportAppend(ref _supportDirty, handle); node.Queued = 1; }
            _supportNodes[handle] = node;
        }

        void RemoveSupportDirty(BlockSimHandle handle)
        {
            if (!_supportNodes.TryGetValue(handle, out var node) || node.Queued == 0) return;
            int slot = node.DirtySlot, last = _supportDirty.Length - 1;
            if (slot < 0 || slot > last || !_supportDirty[slot].Equals(handle))
                throw new InvalidOperationException("Support dirty queue identity mismatch.");
            BlockSimHandle moved = _supportDirty[last]; _supportDirty.RemoveAtSwapBack(slot);
            if (slot != last)
            {
                var movedNode = _supportNodes[moved]; movedNode.DirtySlot = slot; _supportNodes[moved] = movedNode;
            }
            node.Dirty = node.Queued = 0; node.DirtySlot = -1; _supportNodes[handle] = node;
        }

        void BeginSupportClosure()
        { _supportClosure.Clear(); _supportVisited.Clear(); }

        void QueueSupportClosure(BlockSimHandle handle)
        {
            if (!_supportNodes.ContainsKey(handle) || _supportVisited.ContainsKey(handle)) return;
            EnsureSupportBodyCapacity(Count);
            if (!_supportVisited.TryAdd(handle, 1))
                throw new InvalidOperationException("Support visit insertion failed; no partial dependency traversal was accepted.");
            SupportAppend(ref _supportClosure, handle);
        }

        void SupportBodyAdded(int index)
        {
            EnsureSupportBodyCapacity(Count);
            BlockSimHandle handle = _handles[index];
            var node = new SupportNode { Position = _positions[index], MapRoot = SupportPending,
                Status = _modes[index] == BlockSimMode.Frozen ? SupportPresent : SupportPending,
                Frozen = _modes[index] == BlockSimMode.Frozen ? (byte)1 : (byte)0 };
            if (!_supportNodes.TryAdd(handle, node))
                throw new InvalidOperationException("Duplicate support identity; full handles must be unique.");
            MarkSupportDirty(handle, SupportMapDirty);
            BeginSupportClosure(); QueueSupportClosure(handle); RepairSupportEdges(handle);
            RecomputeSupportClosure(false, false);
        }

        // Called after SetState and the persistent center index have accepted the new authoritative pose.
        // No borrowed map snapshot is retained between API calls: a moved map root stays Pending until Step.
        void SupportBodyChanged(int index)
        {
            BlockSimHandle handle = _handles[index];
            if (!_supportNodes.TryGetValue(handle, out var node))
                throw new InvalidOperationException("Missing support identity during SetState.");
            BeginSupportClosure(); RemoveIncidentSupportEdges(handle); QueueSupportClosure(handle);
            node.Position = _positions[index]; node.Frozen = _modes[index] == BlockSimMode.Frozen ? (byte)1 : (byte)0;
            node.MapRoot = SupportPending; _supportNodes[handle] = node;
            MarkSupportDirty(handle, SupportMapDirty);
            RepairSupportEdges(handle); RecomputeSupportClosure(true, false);
        }

        // Called while the removed handle and all dense arrays still exist, before swap-back.
        void SupportBodyRemoving(int index)
        {
            BlockSimHandle handle = _handles[index];
            BeginSupportClosure(); RemoveIncidentSupportEdges(handle);
            RemoveSupportDirty(handle);
            if (!_supportNodes.Remove(handle)) throw new InvalidOperationException("Missing removed support identity.");
            // The removed body's old uppers were queued before its outgoing edges disappeared.
            RecomputeSupportClosure(true, false);
        }

        void SupportMapChanged(BlockAabb bounds)
        {
            EnsureQueryHash(); BeginSupportClosure(); bounds = bounds.Expanded(ContactSkin);
            int3 lo = Grid(bounds.Min - HalfSize), hi = Grid(bounds.Max + HalfSize);
            for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
            {
                if (!_hash.TryGetFirstValue(new int3(x, y, z), out int i, out var iterator)) continue;
                do
                {
                    if (!bounds.Overlaps(new BlockAabb(_positions[i], HalfSize))) continue;
                    BlockSimHandle handle = _handles[i];
                    var node = _supportNodes[handle]; node.MapRoot = SupportPending; _supportNodes[handle] = node;
                    MarkSupportDirty(handle, SupportMapDirty); QueueSupportClosure(handle);
                } while (_hash.TryGetNextValue(out i, ref iterator));
            }
            // Revalidation deliberately waits for Step's replacement snapshot. The pre-replacement
            // map can still contain the removed feature, and may be disposed immediately after notification.
            // Pending is propagated without waking: intervening mutations cannot treat the old map
            // root as a certified alternate root, or declare it absent before seeing the replacement.
            RecomputeSupportClosure(false, false);
        }

        void SupportBeforeTick(BlockMapSnapshot map)
        {
            _supportDiagnostics = default;
            long started = Stopwatch.GetTimestamp();
            try { RefreshSupportDependencies(map, true); }
            finally { _supportDiagnostics.TotalMilliseconds += SupportElapsed(started); }
        }

        void SupportBeforeFinish(BlockMapSnapshot map)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                long stage = started;
                EnsureSupportBodyCapacity(Count); _supportMoved.Clear(); _supportFault.Value = 0;
                new CollectSupportMovementJob { Handles = _handles.AsArray(), Positions = _positions.AsArray(),
                    Modes = _modes.AsArray(), Nodes = _supportNodes, Moved = _supportMoved, Fault = _supportFault }.Schedule().Complete();
                if (_supportFault.Value != 0) throw new InvalidOperationException("Support pose scan found a missing stable identity; tick was rolled back.");
                _supportDiagnostics.MovedBodies += _supportMoved.Length;
                for (int n = 0; n < _supportMoved.Length; n++)
                    MarkSupportDirty(_handles[_supportMoved[n]], SupportGeometryDirty | SupportMapDirty);
                _supportDiagnostics.MovementMilliseconds += StageElapsed(ref stage);
                RefreshSupportDependencies(map, true);
            }
            finally { _supportDiagnostics.TotalMilliseconds += SupportElapsed(started); }
        }

        static double SupportElapsed(long started) =>
            (Stopwatch.GetTimestamp() - started) * (1000d / Stopwatch.Frequency);

        // Everything called here runs between StepCore's stopwatch start and stop. Full publication,
        // support queries, incremental repairs and wake processing all remain in the prototype CPU gate.
        void RefreshSupportDependencies(BlockMapSnapshot map, bool wakeUnsupported)
        {
            if (_supportDirty.Length == 0) return;
            long started = Stopwatch.GetTimestamp();
            try
            {
                long stage = started;
                _supportDiagnostics.RefreshCalls++; _supportDiagnostics.DirtyBodies += _supportDirty.Length;
                EnsureQueryHash(); BeginSupportClosure();
                // Update all dirty poses before map querying or discovering any new edges; the batch
                // must never compare a moved body against another dirty body's previous pose.
                bool refreshMap = false;
                for (int n = 0; n < _supportDirty.Length; n++)
                {
                    BlockSimHandle handle = _supportDirty[n];
                    if (!_supportNodes.TryGetValue(handle, out var node) || !_indices.TryGetValue(handle, out int index) ||
                        index < 0 || index >= Count || !_handles[index].Equals(handle))
                        throw new InvalidOperationException("Support dirty pose found a missing stable identity; tick was rolled back.");
                    if ((node.Dirty & SupportGeometryDirty) != 0)
                    { _supportDiagnostics.GeometryDirtyBodies++; RemoveIncidentSupportEdges(handle); }
                    node.Position = _positions[index]; node.Frozen = _modes[index] == BlockSimMode.Frozen ? (byte)1 : (byte)0;
                    if ((node.Dirty & SupportMapDirty) != 0)
                    { refreshMap = true; _supportDiagnostics.MapQueries++; }
                    _supportNodes[handle] = node; QueueSupportClosure(handle);
                }
                _supportDiagnostics.PoseMilliseconds += StageElapsed(ref stage);
                // Complete and commit every exact map result before the original edge-repair phase.
                if (refreshMap) RefreshMapSupportBatch(map);
                _supportDiagnostics.MapBatchMilliseconds += StageElapsed(ref stage);
                for (int n = 0; n < _supportDirty.Length; n++)
                {
                    BlockSimHandle handle = _supportDirty[n];
                    if (_supportNodes.TryGetValue(handle, out var node) && (node.Dirty & SupportGeometryDirty) != 0)
                        RepairSupportEdges(handle);
                }
                _supportDiagnostics.EdgeRepairMilliseconds += StageElapsed(ref stage);
                RecomputeSupportClosure(wakeUnsupported, true);
                _supportDiagnostics.ClosureMilliseconds += StageElapsed(ref stage);
                for (int n = 0; n < _supportDirty.Length; n++)
                {
                    BlockSimHandle handle = _supportDirty[n];
                    if (!_supportNodes.TryGetValue(handle, out var node)) continue;
                    node.Dirty = node.Queued = 0; node.DirtySlot = -1; _supportNodes[handle] = node;
                }
                _supportDirty.Clear();
                _supportDiagnostics.CleanupMilliseconds += StageElapsed(ref stage);
            }
            finally { _supportDiagnostics.RefreshMilliseconds += SupportElapsed(started); }
        }

        void RefreshMapSupportBatch(BlockMapSnapshot map)
        {
            SupportListCapacity(ref _supportMapRoots, _supportDirty.Length);
            _supportMapRoots.ResizeUninitialized(_supportDirty.Length); _supportFault.Value = 0;
            _supportDiagnostics.MapBatches++;
            long started = Stopwatch.GetTimestamp();
            new QueryMapSupportJob { Map = map, Dirty = _supportDirty.AsArray(), Nodes = _supportNodes,
                MapScratch = _supportMapHits, Roots = _supportMapRoots.AsArray(), Fault = _supportFault }.Schedule().Complete();
            _supportDiagnostics.MapBurstMilliseconds += SupportElapsed(started);
            if (_supportFault.Value != 0)
                throw new InvalidOperationException("Support map batch found a missing stable identity; tick was rolled back.");
            for (int n = 0; n < _supportDirty.Length; n++)
            {
                BlockSimHandle handle = _supportDirty[n];
                if (!_supportNodes.TryGetValue(handle, out var node))
                    throw new InvalidOperationException("Support map commit found a missing stable identity; tick was rolled back.");
                SupportMapRootResult result = _supportMapRoots[n];
                if (result.ScratchCapacityGrew != 0) _diagnostics.CapacityGrowths++;
                if ((node.Dirty & SupportMapDirty) == 0) continue;
                node.MapRoot = result.MapRoot; _supportNodes[handle] = node;
            }
            _supportMapRoots.Clear();
        }

        [BurstCompile]
        struct QueryMapSupportJob : IJob
        {
            [ReadOnly] public BlockMapSnapshot Map;
            [ReadOnly] public NativeArray<BlockSimHandle> Dirty;
            [ReadOnly] public NativeParallelHashMap<BlockSimHandle, SupportNode> Nodes;
            public NativeList<BlockStaticHit> MapScratch;
            public NativeArray<SupportMapRootResult> Roots;
            public NativeReference<int> Fault;
            public void Execute()
            {
                for (int n = 0; n < Dirty.Length; n++)
                {
                    if (!Nodes.TryGetValue(Dirty[n], out var node)) { Fault.Value = 2; return; }
                    var result = new SupportMapRootResult { MapRoot = node.MapRoot };
                    if ((node.Dirty & SupportMapDirty) != 0)
                    {
                        // Preserve the entire merged contact list and its traversal order. An
                        // existential feature predicate is not equivalent at the normal threshold.
                        MapScratch.Clear(); int capacity = MapScratch.Capacity;
                        BlockMapQueries.CollectContacts(Map, node.Position, HalfSize, MapScratch, ContactSkin);
                        result.ScratchCapacityGrew = MapScratch.Capacity != capacity ? (byte)1 : (byte)0;
                        result.MapRoot = SupportAbsent;
                        for (int c = 0; c < MapScratch.Length; c++)
                            if (MapScratch[c].Normal.y > .01f) { result.MapRoot = SupportPresent; break; }
                    }
                    Roots[n] = result;
                }
            }
        }

        static bool IsActualBlockSupport(float3 lower, float3 upper)
        {
            float height = upper.y - lower.y;
            float2 overlap = new float2(Edge) - math.abs((upper - lower).xz);
            return height > 0 && math.abs(height - Edge) <= ContactSkin && math.all(overlap > .000001f);
        }

        void RepairSupportEdges(BlockSimHandle handle)
        {
            if (!_supportNodes.TryGetValue(handle, out var node)) return;
            float3 extent = new float3(Edge + ContactSkin);
            int3 lo = Grid(node.Position - extent), hi = Grid(node.Position + extent);
            for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
            {
                if (!_hash.TryGetFirstValue(new int3(x, y, z), out int i, out var iterator)) continue;
                do
                {
                    BlockSimHandle other = _handles[i];
                    if (other.Equals(handle) || !_supportNodes.TryGetValue(other, out var neighbour)) continue;
                    if (IsActualBlockSupport(node.Position, neighbour.Position)) AddSupportEdge(handle, other);
                    else if (IsActualBlockSupport(neighbour.Position, node.Position)) AddSupportEdge(other, handle);
                } while (_hash.TryGetNextValue(out i, ref iterator));
            }
        }

        void AddSupportEdge(BlockSimHandle lower, BlockSimHandle upper)
        {
            if (HasSupportValue(_supportLowers, upper, lower)) return;
            if (_supportEdgeCount == int.MaxValue)
                throw new InvalidOperationException("Support edge capacity exceeds addressable storage; no edge was omitted.");
            int required = _supportEdgeCount + 1;
            if (_supportLowers.Capacity < required)
            { _supportLowers.Capacity = Grow(required); _diagnostics.CapacityGrowths++; }
            if (_supportUppers.Capacity < required)
            { _supportUppers.Capacity = Grow(required); _diagnostics.CapacityGrowths++; }
            _supportLowers.Add(upper, lower); _supportUppers.Add(lower, upper); _supportEdgeCount++;
            QueueSupportClosure(upper);
        }

        static bool HasSupportValue(NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle> map,
            BlockSimHandle key, BlockSimHandle value)
        {
            if (!map.TryGetFirstValue(key, out var found, out var iterator)) return false;
            do { if (found.Equals(value)) return true; } while (map.TryGetNextValue(out found, ref iterator));
            return false;
        }

        static bool RemoveSupportValue(NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle> map,
            BlockSimHandle key, BlockSimHandle value)
        {
            if (!map.TryGetFirstValue(key, out var found, out var iterator)) return false;
            do
            {
                if (!found.Equals(value)) continue;
                map.Remove(iterator); return true;
            } while (map.TryGetNextValue(out found, ref iterator));
            return false;
        }

        void RemoveSupportEdge(BlockSimHandle lower, BlockSimHandle upper)
        {
            if (!HasSupportValue(_supportLowers, upper, lower) || !HasSupportValue(_supportUppers, lower, upper))
                throw new InvalidOperationException("Support adjacency mismatch before edge removal.");
            bool a = RemoveSupportValue(_supportLowers, upper, lower);
            bool b = RemoveSupportValue(_supportUppers, lower, upper);
            if (!a || !b) throw new InvalidOperationException("Support adjacency mismatch; no partial edge removal was accepted.");
            _supportEdgeCount--; QueueSupportClosure(upper);
        }

        void CopySupportNeighbours(NativeParallelMultiHashMap<BlockSimHandle, BlockSimHandle> map, BlockSimHandle handle)
        {
            _supportScratch.Clear();
            if (!map.TryGetFirstValue(handle, out var other, out var iterator)) return;
            do { SupportAppend(ref _supportScratch, other); } while (map.TryGetNextValue(out other, ref iterator));
        }

        void RemoveIncidentSupportEdges(BlockSimHandle handle)
        {
            CopySupportNeighbours(_supportLowers, handle);
            for (int n = 0; n < _supportScratch.Length; n++) RemoveSupportEdge(_supportScratch[n], handle);
            CopySupportNeighbours(_supportUppers, handle);
            for (int n = 0; n < _supportScratch.Length; n++) RemoveSupportEdge(handle, _supportScratch[n]);
        }

        void RecomputeSupportClosure(bool wakeUnsupported, bool duringTick)
        {
            // An incident-edge removal may have queued the body that is itself being deleted.
            for (int q = _supportClosure.Length - 1; q >= 0; q--)
                if (!_supportNodes.ContainsKey(_supportClosure[q])) _supportClosure.RemoveAtSwapBack(q);
            // Expand the current upward DAG only. Old uppers of removed edges were seeded separately.
            for (int q = 0; q < _supportClosure.Length; q++)
            {
                if (!_supportUppers.TryGetFirstValue(_supportClosure[q], out var upper, out var iterator)) continue;
                do { QueueSupportClosure(upper); } while (_supportUppers.TryGetNextValue(out upper, ref iterator));
            }
            _supportClosure.AsArray().Sort(new SupportHeightComparer { Nodes = _supportNodes });
            for (int q = 0; q < _supportClosure.Length; q++)
            {
                BlockSimHandle handle = _supportClosure[q];
                if (!_supportNodes.TryGetValue(handle, out var node) || !_indices.TryGetValue(handle, out int index)) continue;
                byte status = node.Frozen != 0 || node.MapRoot == SupportPresent ? SupportPresent :
                    node.MapRoot == SupportPending ? SupportPending : SupportAbsent;
                if (status != SupportPresent && _supportLowers.TryGetFirstValue(handle, out var lower, out var iterator))
                {
                    do
                    {
                        if (!_supportNodes.TryGetValue(lower, out var source))
                            throw new InvalidOperationException("Stale full handle in support adjacency.");
                        if (source.Status == SupportPresent) { status = SupportPresent; break; }
                        if (source.Status == SupportPending) status = SupportPending;
                    } while (_supportLowers.TryGetNextValue(out lower, ref iterator));
                }
                node.Status = status; _supportNodes[handle] = node;
                _supported[index] = status == SupportPresent ? (byte)1 : (byte)0;
                if (!wakeUnsupported || status != SupportAbsent || _modes[index] != BlockSimMode.Resting) continue;
                _modes[index] = BlockSimMode.Active; _quiet[index] = 0; _validateSupport[index] = 0;
                if (duringTick) { var work = _work.Value; work.SupportWakes++; _work.Value = work; }
                else _diagnostics.SupportWakes++;
            }
        }

        // This is the exception path, using the still-live snapshot supplied to the failing Step.
        // Rebuild from restored authoritative poses, never from partially moved scratch state. No
        // wake occurs here: restored modes/velocities remain intact, with unsupported roots queued for retry.
        void SupportAfterRollback(BlockMapSnapshot map)
        {
            ClearSupportDependencies(); EnsureQueryHash(); EnsureSupportBodyCapacity(Count);
            for (int i = 0; i < Count; i++)
            {
                var node = new SupportNode { Position = _positions[i], MapRoot = SupportPending,
                    Status = _modes[i] == BlockSimMode.Frozen ? SupportPresent : SupportPending,
                    Frozen = _modes[i] == BlockSimMode.Frozen ? (byte)1 : (byte)0 };
                if (!_supportNodes.TryAdd(_handles[i], node)) throw new InvalidOperationException("Duplicate rollback support identity.");
                MarkSupportDirty(_handles[i], SupportGeometryDirty | SupportMapDirty);
            }
            RefreshSupportDependencies(map, false);
            for (int i = 0; i < Count; i++)
                if (_modes[i] == BlockSimMode.Resting && _supported[i] == 0)
                { _validateSupport[i] = 1; MarkSupportDirty(_handles[i], SupportMapDirty); }
        }

        struct SupportHeightComparer : IComparer<BlockSimHandle>
        {
            [ReadOnly] public NativeParallelHashMap<BlockSimHandle, SupportNode> Nodes;
            public int Compare(BlockSimHandle a, BlockSimHandle b)
            {
                int height = Nodes[a].Position.y.CompareTo(Nodes[b].Position.y);
                return height != 0 ? height : CompareHandle(a, b);
            }
        }

        [BurstCompile]
        struct CollectSupportMovementJob : IJob
        {
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            [ReadOnly] public NativeParallelHashMap<BlockSimHandle, SupportNode> Nodes;
            public NativeList<int> Moved;
            public NativeReference<int> Fault;
            public void Execute()
            {
                for (int i = 0; i < Handles.Length; i++)
                {
                    if (!Nodes.TryGetValue(Handles[i], out var node)) { Fault.Value = 1; return; }
                    if (math.any(node.Position != Positions[i]) || node.Frozen != (Modes[i] == BlockSimMode.Frozen ? 1 : 0)) Moved.Add(i);
                }
            }
        }
    }
}
