using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    /// <summary>
    /// A contact retained at the final committed positions. B is default for a map contact.
    /// Impulses are the final substep's retained warm-start ledger, with tangent projected into
    /// the current contact frame. They are not a sum over the tick; separated contacts are absent.
    /// </summary>
    public struct BlockSimContactEvidence
    {
        public BlockSimHandle A, B;
        public float3 Normal, TangentImpulse;
        public float Separation, NormalImpulse;
    }

    public sealed partial class BlockSimulation
    {
        NativeList<BlockSimContactEvidence> _contactReadback;
        BlockSimContactEvidence[] _managedContactReadback;

        /// <summary>
        /// Copies the currently available completed contact ledger. Call after a successful Step,
        /// before Add/Remove/SetState/Clear. Mutation or rollback invalidates this diagnostic view;
        /// no previous successful ledger is saved. Reused buffers avoid allocations after warm-up.
        /// This provides sampled solver evidence, not proof that every body/contact was pushed.
        /// </summary>
        public void CopyContactEvidence(List<BlockSimContactEvidence> destination)
        {
            CheckDisposed();
            if (destination == null) throw new System.ArgumentNullException(nameof(destination));
            int count = _contacts.Length;
            destination.Clear();
            if (count == 0) return;
            if (!_contactReadback.IsCreated)
            {
                _contactReadback = new NativeList<BlockSimContactEvidence>(Grow(count), Allocator.Persistent);
                _diagnostics.CapacityGrowths++;
            }
            int capacity = _contactReadback.Capacity;
            _contactReadback.ResizeUninitialized(count);
            if (_contactReadback.Capacity != capacity) _diagnostics.CapacityGrowths++;
            if (_managedContactReadback == null || _managedContactReadback.Length < count)
            {
                _managedContactReadback = new BlockSimContactEvidence[Grow(count)];
                _diagnostics.CapacityGrowths++;
            }
            new ReadContactEvidenceJob { Contacts = _contacts.AsArray(), Destination = _contactReadback.AsArray() }
                .Schedule(count, 128).Complete();
            NativeArray<BlockSimContactEvidence>.Copy(_contactReadback.AsArray(), 0, _managedContactReadback, 0, count);
            if (destination.Capacity < count) destination.Capacity = count;
            for (int i = 0; i < count; i++) destination.Add(_managedContactReadback[i]);
        }

        void DisposeContactEvidenceReadback()
        {
            if (_contactReadback.IsCreated) _contactReadback.Dispose();
            _managedContactReadback = null;
        }

        [BurstCompile]
        struct ReadContactEvidenceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SimContact> Contacts;
            [WriteOnly] public NativeArray<BlockSimContactEvidence> Destination;
            public void Execute(int index)
            {
                SimContact contact = Contacts[index];
                Destination[index] = new BlockSimContactEvidence { A = contact.Key.A, B = contact.Key.B,
                    Normal = contact.Normal, Separation = contact.Separation, NormalImpulse = contact.NormalImpulse,
                    TangentImpulse = contact.TangentImpulse };
            }
        }
    }
}
