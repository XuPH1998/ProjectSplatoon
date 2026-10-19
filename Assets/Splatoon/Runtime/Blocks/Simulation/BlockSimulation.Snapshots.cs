using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public sealed partial class BlockSimulation
    {
        NativeList<BlockSimState> _stateReadback;
        BlockSimState[] _managedReadback;

        // Readback is deliberately still full-world and included by the prototype CPU gate.
        // Gather in Burst and bulk-copy once rather than invoking six native safety checks per body in Mono.
        void ReadCommittedStates(List<BlockSimState> destination)
        {
            int count = Count;
            destination.Clear();
            if (count == 0) return;
            if (!_stateReadback.IsCreated)
            {
                _stateReadback = new NativeList<BlockSimState>(Grow(count), Allocator.Persistent);
                _diagnostics.CapacityGrowths++;
            }
            int capacity = _stateReadback.Capacity;
            _stateReadback.ResizeUninitialized(count);
            if (_stateReadback.Capacity != capacity) _diagnostics.CapacityGrowths++;
            if (_managedReadback == null || _managedReadback.Length < count)
            {
                _managedReadback = new BlockSimState[Grow(count)];
                _diagnostics.CapacityGrowths++;
            }
            new ReadCommittedStatesJob
            {
                Handles = _handles.AsArray(), Owners = _owners.AsArray(), Positions = _positions.AsArray(),
                Velocities = _velocities.AsArray(), Modes = _modes.AsArray(), Versions = _versions.AsArray(),
                Destination = _stateReadback.AsArray()
            }.Schedule(count, 128).Complete();
            NativeArray<BlockSimState>.Copy(_stateReadback.AsArray(), 0, _managedReadback, 0, count);
            if (destination.Capacity < count) destination.Capacity = count;
            for (int i = 0; i < count; i++) destination.Add(_managedReadback[i]);
        }

        void DisposeStateReadback()
        {
            if (_stateReadback.IsCreated) _stateReadback.Dispose();
            _managedReadback = null;
        }

        [BurstCompile]
        struct ReadCommittedStatesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            [ReadOnly] public NativeArray<byte> Owners;
            [ReadOnly] public NativeArray<float3> Positions, Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            [ReadOnly] public NativeArray<uint> Versions;
            [WriteOnly] public NativeArray<BlockSimState> Destination;
            public void Execute(int index)
            {
                Destination[index] = new BlockSimState { handle = Handles[index], owner = Owners[index],
                    position = Positions[index], velocity = Velocities[index], mode = Modes[index], poseVersion = Versions[index] };
            }
        }
    }
}
