using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public sealed partial class BlockSimulation
    {
        // The persistent index contains every body's current center exactly once. Sleeping does not
        // remove collision/query/wake visibility. Only crossing a cell boundary changes an entry.
        NativeList<int3> _indexedCells, _sweptLo, _sweptHi;
        NativeList<float3> _sweptMinimum, _sweptMaximum;
        NativeList<int> _broadActive, _broadCandidates;
        NativeParallelMultiHashMap<int3, int> _activeSweepHash;
        NativeReference<BroadphaseWork> _broadWork;

        struct BroadphaseWork
        {
            public int Fault, HashRequired, PairRequired, HashFilled;
        }

        void InitializeSpatialIndex(int capacity)
        {
            try
            {
                _indexedCells = new NativeList<int3>(capacity, Allocator.Persistent);
                _sweptLo = new NativeList<int3>(capacity, Allocator.Persistent);
                _sweptHi = new NativeList<int3>(capacity, Allocator.Persistent);
                _sweptMinimum = new NativeList<float3>(capacity, Allocator.Persistent);
                _sweptMaximum = new NativeList<float3>(capacity, Allocator.Persistent);
                _broadActive = new NativeList<int>(capacity, Allocator.Persistent);
                _broadCandidates = new NativeList<int>(capacity, Allocator.Persistent);
                _activeSweepHash = new NativeParallelMultiHashMap<int3, int>(capacity, Allocator.Persistent);
                _broadWork = new NativeReference<BroadphaseWork>(Allocator.Persistent);
            }
            catch { DisposeSpatialIndex(); throw; }
        }

        void SpatialBodyAdded(int index)
        {
            if (_hashDirty || _indexedCells.Length != Count - 1) { EnsureQueryHash(); return; }
            if (_hash.Capacity < Count) { _hash.Capacity = Grow(Count); _diagnostics.CapacityGrowths++; }
            int capacity = _indexedCells.Capacity;
            int3 cell = Grid(_positions[index]); _indexedCells.Add(cell); _hash.Add(cell, index);
            if (_indexedCells.Capacity != capacity) _diagnostics.CapacityGrowths++;
        }

        // Called before the authoritative arrays swap their last body into index. Dense indices never
        // escape as identity: repair both entries here, while the old positions/handles still exist.
        void SpatialBodyRemoving(int index, int last)
        {
            EnsureQueryHash();
            if (!RemoveCenterEntry(_hash, _indexedCells[index], index))
                throw new InvalidOperationException("Missing removed body in the persistent center index.");
            if (index != last)
            {
                int3 cell = _indexedCells[last];
                if (!RemoveCenterEntry(_hash, cell, last))
                    throw new InvalidOperationException("Missing swap-back body in the persistent center index.");
                _hash.Add(cell, index);
            }
            _indexedCells.RemoveAtSwapBack(index);
        }

        void SpatialBodyChanged(int index)
        {
            if (_hashDirty || _indexedCells.Length != Count) { EnsureQueryHash(); return; }
            if (!RelocateCenter(_hash, _indexedCells.AsArray(), index, _positions[index]))
                throw new InvalidOperationException("Missing moved body in the persistent center index.");
        }

        void ClearSpatialIndex()
        {
            _indexedCells.Clear(); _sweptLo.Clear(); _sweptHi.Clear(); _sweptMinimum.Clear(); _sweptMaximum.Clear();
            _broadActive.Clear(); _broadCandidates.Clear();
            _activeSweepHash.Clear(); _hash.Clear(); _hashDirty = false;
        }

        void DisposeSpatialIndex()
        {
            if (_indexedCells.IsCreated) _indexedCells.Dispose();
            if (_sweptLo.IsCreated) _sweptLo.Dispose();
            if (_sweptHi.IsCreated) _sweptHi.Dispose();
            if (_sweptMinimum.IsCreated) _sweptMinimum.Dispose();
            if (_sweptMaximum.IsCreated) _sweptMaximum.Dispose();
            if (_broadActive.IsCreated) _broadActive.Dispose();
            if (_broadCandidates.IsCreated) _broadCandidates.Dispose();
            if (_activeSweepHash.IsCreated) _activeSweepHash.Dispose();
            if (_broadWork.IsCreated) _broadWork.Dispose();
        }

        static bool RemoveCenterEntry(NativeParallelMultiHashMap<int3, int> hash, int3 cell, int index)
        {
            if (!hash.TryGetFirstValue(cell, out int found, out var iterator)) return false;
            do
            {
                if (found != index) continue;
                hash.Remove(iterator); return true;
            } while (hash.TryGetNextValue(out found, ref iterator));
            return false;
        }

        static bool RelocateCenter(NativeParallelMultiHashMap<int3, int> hash, NativeArray<int3> cells, int index, float3 position)
        {
            int3 cell = Grid(position), previous = cells[index];
            if (math.all(cell == previous)) return true;
            if (!RemoveCenterEntry(hash, previous, index)) return false;
            hash.Add(cell, index); cells[index] = cell; return true;
        }

        void EnsureQueryHash()
        {
            if (!_hashDirty && _indexedCells.Length == Count) return;
            int capacity = _indexedCells.Capacity;
            _indexedCells.ResizeUninitialized(Count);
            if (_indexedCells.Capacity != capacity) _diagnostics.CapacityGrowths++;
            if (_hash.Capacity < Count) { _hash.Capacity = Grow(Count); _diagnostics.CapacityGrowths++; }
            new RebuildCenterIndexJob { Positions = _positions.AsArray(), Cells = _indexedCells.AsArray(), Hash = _hash,
                Result = _broadWork }.Schedule().Complete();
            if (_broadWork.Value.Fault != 0)
                throw new InvalidOperationException("Persistent center index rebuild count mismatch; no partial index was published.");
            _hashDirty = false;
        }

        [BurstCompile]
        struct RebuildCenterIndexJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            public NativeArray<int3> Cells;
            public NativeParallelMultiHashMap<int3, int> Hash;
            public NativeReference<BroadphaseWork> Result;
            public void Execute()
            {
                Hash.Clear();
                for (int i = 0; i < Positions.Length; i++) { int3 cell = Grid(Positions[i]); Cells[i] = cell; Hash.Add(cell, i); }
                Result.Value = new BroadphaseWork { Fault = Hash.Count() == Positions.Length ? 0 : 1 };
            }
        }
    }
}
