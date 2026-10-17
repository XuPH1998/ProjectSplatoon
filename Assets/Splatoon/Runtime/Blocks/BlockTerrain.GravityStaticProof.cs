using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        sealed class GravityStaticProof
        {
            public readonly Vector3 Position;
            public readonly float WorldBottom;
            public readonly int Minimum;
            public GravityStaticProof(Vector3 position, float bottom, int minimum)
            { Position = position; WorldBottom = bottom; Minimum = minimum; }
        }
        LinkedList<GravityStaticProof> _privateGravityStaticProofs = new();
        public long StaticProofInvalidations;
        public int LastPublicationStaticProofs;
        public long OwnLandingPublicationDeferrals;
        public long GravitySupportPublicationRetries;
        public int LastPublicationSupportAbsenceRays;
        // Valid only during the synchronous authority phases, before external callbacks.
        bool _stageTransformsSynced;

        void RetainGravityStaticProof(GravityStaticProof proof)
        {
            if (proof != null && _gravityJournal.RetainedStaticProofs.Add(proof))
                _gravityJournal.StaticProofs.AddLast(proof);
        }
        void RetainOwnActivationProof(ulong id, GravityMotionWork proof)
        {
            _gravityJournal.OwnActivationProofs[id] = proof;
            RetainGravityStaticProof(proof.NativeProof);
        }
        bool ValidateOwnActivationProofs(Pending pending)
        {
            if (pending.Gravity == null) return true;
            foreach (var pair in pending.Gravity.OwnActivationProofs)
            {
                if (!pending.Next.Cohorts.TryGetValue(pair.Key, out var cohort)
                    || !pending.Next.IsPreparedCohort(pair.Key)) continue;
                var proof = pair.Value;
                uint generation = pending.Gravity.Generations.TryGetValue(proof.Column, out uint staged) ? staged
                    : _landingGeneration.TryGetValue(proof.Column, out uint committed) ? committed : 0;
                // Mono may retain extra precision in arithmetic; compare the stored single values.
                float expectedLandingOffset = (cohort.Bottom.y - proof.Landing) * BlockWorld.CellSize;
                if (!proof.ProofKnown || proof.NativeProof == null)
                { pending.AbortReason = PendingAbortReason.OwnActivationMissingProof; return false; }
                if (proof.Generation != generation)
                { pending.AbortReason = PendingAbortReason.OwnActivationGenerationChanged; return false; }
                if (!cohort.LandingKnown || cohort.NeedsLandingProof)
                { pending.AbortReason = PendingAbortReason.OwnActivationLandingState; return false; }
                if (cohort.Bottom.y != proof.SourceBottom || cohort.TopExclusive > proof.SourceTop)
                { pending.AbortReason = PendingAbortReason.OwnActivationSourceSpan; return false; }
                if (float.IsNaN(cohort.LandingOffset) || float.IsInfinity(cohort.LandingOffset)
                    || (System.BitConverter.SingleToInt32Bits(cohort.LandingOffset)
                        != System.BitConverter.SingleToInt32Bits(expectedLandingOffset)
                        && !(cohort.LandingOffset == 0f && expectedLandingOffset == 0f)))
                { pending.AbortReason = PendingAbortReason.OwnActivationLandingOffset; return false; }
            }
            return true;
        }
        void RetryOwnActivationPublication(Pending pending)
        {
            // A later command in the same draft may invalidate a stable-cell
            // proof. Retry its original admitted FIFO inputs, preserving budgets
            // and callbacks rather than activating against stale support facts.
            if (pending.BuildGuard != null) RetryBuildPublication(pending);
            else
            {
                RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
                _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
                _buildOccupancyPreparation = new BuildOccupancyPreparation { Retry = true, Window = pending.Window,
                    Checkpoints = pending.CommandCheckpoints, RetryCursor = pending.CommandCheckpoints?.Last };
            }
            OwnLandingPublicationDeferrals++;
        }

        void RetryGravitySupportPublication(Pending pending)
        {
            // A native absence cache invalidation is not a paid-attack result.
            // Restore the admitted inputs in the existing finite, budgeted FIFO
            // replay path. Published totals and callback/budget ownership survive.
            if (pending.BuildGuard != null) RetryBuildPublication(pending);
            else
            {
                RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
                _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
                _buildOccupancyPreparation = new BuildOccupancyPreparation { Retry = true, Window = pending.Window,
                    Checkpoints = pending.CommandCheckpoints, RetryCursor = pending.CommandCheckpoints?.Last };
            }
            GravitySupportPublicationRetries++;
        }
        BlockWorld TakeOwnedBuildGravityClone(BuildOccupancyPreparation preparation)
        {
            var journal = preparation.Journal; var clone = preparation.Clone; var draft = preparation.Draft;
            // Only this verified owned-build TakeWorld transition may migrate
            // a native proof binding. An arbitrary replacement draft still fails.
            if (journal == null || clone == null || !clone.Ready || clone.Failed || !clone.IsValid(draft)
                || !ReferenceEquals(clone.SourceWorld, draft) || !ReferenceEquals(journal.Next, draft)
                || journal.SupportSnapshot != null && !ReferenceEquals(journal.SupportSnapshot.Draft, draft)
                || journal.SupportProofDraft != null && !ReferenceEquals(journal.SupportProofDraft, draft))
                throw new System.InvalidOperationException("Owned build gravity proof clone does not match its admitted draft.");
            var owned = clone.TakeWorld();
            journal.Next = owned;
            if (journal.SupportSnapshot != null)
                journal.SupportSnapshot = new GravitySupportSnapshot(owned, journal.SupportSnapshot.Bounds);
            if (journal.SupportProofDraft != null) journal.SupportProofDraft = owned;
            // Column lists contain native bounds only, so equivalent world
            // cloning does not invalidate their geometry facts or captured spans.
            return owned;
        }

        bool QueryStaticProof(Vector3 position, float bottom, out int minimum)
        {
            // Reuse the original definition of eligible static support. Native
            // synchronization/query cost remains subject to real CPU/wall gates.
            // StageAuthority already synchronized this uninterrupted synchronous
            // phase. Publication preflight and calls outside that phase still
            // synchronize before observing native geometry.
            if (!_stageTransformsSynced) Physics.SyncTransforms();
            minimum = Mathf.CeilToInt(bottom / BlockWorld.CellSize);
            int hits = Physics.RaycastNonAlloc(position, Vector3.down, _rayHits,
                Mathf.Max(0, position.y - bottom + BlockWorld.CellSize), Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer), QueryTriggerInteraction.Ignore);
            if (hits == _rayHits.Length) return false;
            for (int h = 0; h < hits; h++) if (_staticColliders.Contains(_rayHits[h].collider) && _rayHits[h].normal.y > .2f)
                minimum = Mathf.Max(minimum, StaticLandingCellY(_rayHits[h].point.y));
            return true;
        }
        bool ValidateStaticProof(GravityStaticProof proof)
            => QueryStaticProof(proof.Position, proof.WorldBottom, out int current) && current == proof.Minimum;

        // A bounded, journal-local native broadphase negative proof. It describes
        // physical shapes only; it never caches BlockWorld occupancy/anchor facts.
        const int InlineGravitySupportColliderLimit = 128;
        readonly struct GravitySupportBounds
        {
            public readonly Collider Collider;
            public readonly bool Eligible;
            public readonly Vector3 Minimum, Maximum;
            public GravitySupportBounds(Collider collider, bool eligible, Vector3 minimum, Vector3 maximum)
            { Collider = collider; Eligible = eligible; Minimum = minimum; Maximum = maximum; }
        }
        sealed class GravitySupportSnapshot
        {
            public readonly BlockWorld Draft;
            public readonly GravitySupportBounds[] Bounds;
            public GravitySupportSnapshot(BlockWorld draft, GravitySupportBounds[] bounds)
            { Draft = draft; Bounds = bounds; }
        }
        readonly struct GravitySupportAbsenceSpan
        {
            public readonly Vector3Int Bottom;
            public readonly int Count;
            public GravitySupportAbsenceSpan(Vector3Int bottom, int count) { Bottom = bottom; Count = count; }
        }
        void RetainGravitySupportAbsence(Vector3Int bottom, int count, bool negativeBounds)
        {
            var journal = _gravityJournal;
            if (journal.SupportProofDraft != null && !ReferenceEquals(journal.SupportProofDraft, journal.Next))
                throw new System.InvalidOperationException("Gravity support absence belongs to another staging world.");
            journal.SupportProofDraft = journal.Next;
            if (negativeBounds) journal.RetainSupportSnapshot = true;
            else journal.SupportAbsenceSpans.AddLast(new GravitySupportAbsenceSpan(bottom, count));
        }
        static bool ReadGravitySupportBounds(Collider collider, out GravitySupportBounds result)
        {
            bool eligible = collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger
                && ((Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer)) & (1 << collider.gameObject.layer)) != 0;
            Vector3 minimum = default, maximum = default;
            if (eligible)
            {
                var bounds = collider.bounds; minimum = bounds.min; maximum = bounds.max;
                if (!Finite(minimum) || !Finite(maximum)
                    || minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z)
                { result = default; return false; }
            }
            result = new GravitySupportBounds(collider, eligible, minimum, maximum); return true;
        }
        bool TryGravityNoStaticSupport(Vector2Int column, int bottom, int count, out bool noStatic,
            ref int checks, int maxChecks, System.Diagnostics.Stopwatch timer, double milliseconds)
        {
            noStatic = false; var journal = _gravityJournal;
            if (!journal.SupportSnapshotAttempted)
            {
                if (_staticColliders.Count > InlineGravitySupportColliderLimit)
                { journal.SupportSnapshotAttempted = true; return true; }
                if (!SpendGravity(ref checks, 4 + _staticColliders.Count, maxChecks, timer, milliseconds)) return false;
                if (!_stageTransformsSynced) Physics.SyncTransforms();
                var bounds = new GravitySupportBounds[_staticColliders.Count]; int index = 0; bool valid = true;
                foreach (var collider in _staticColliders)
                {
                    if (!ReadGravitySupportBounds(collider, out bounds[index])) valid = false;
                    index++;
                }
                journal.SupportSnapshotAttempted = true;
                if (valid) journal.SupportSnapshot = new GravitySupportSnapshot(journal.Next, bounds);
            }
            var snapshot = journal.SupportSnapshot;
            if (snapshot == null) return true; // Large/unknown geometry retains the exact original rays.
            if (!ReferenceEquals(snapshot.Draft, journal.Next)) return false;
            float x = (column.x + .5f) * BlockWorld.CellSize, z = (column.y + .5f) * BlockWorld.CellSize;
            if (!journal.SupportColumns.TryGetValue(column, out var relevant))
            {
                if (!SpendGravity(ref checks, snapshot.Bounds.Length + 2, maxChecks, timer, milliseconds)) return false;
                relevant = new List<GravitySupportBounds>();
                foreach (var bounds in snapshot.Bounds)
                    if (bounds.Eligible && x >= bounds.Minimum.x && x <= bounds.Maximum.x && z >= bounds.Minimum.z && z <= bounds.Maximum.z)
                        relevant.Add(bounds);
                journal.SupportColumns.Add(column, relevant);
            }
            if (!SpendGravity(ref checks, relevant.Count + 2, maxChecks, timer, milliseconds)) return false;
            // Use the shared support Center and probe length arithmetic at both
            // ends. Inclusive intersections never receive a negative certificate.
            float minimumY = BlockWorld.Center(new Vector3Int(column.x, bottom, column.y)).y - StaticSupportProbeLength;
            float maximumY = BlockWorld.Center(new Vector3Int(column.x, bottom + count - 1, column.y)).y;
            foreach (var bounds in relevant)
                if (minimumY <= bounds.Maximum.y && maximumY >= bounds.Minimum.y) return true;
            noStatic = true; return true;
        }
        bool ValidateGravitySupportPublication(Pending pending)
        {
            LastPublicationSupportAbsenceRays = 0;
            var journal = pending.Gravity;
            if (journal == null || !journal.RetainSupportSnapshot && journal.SupportAbsenceSpans.Count == 0) return true;
            if (!ReferenceEquals(journal.Next, pending.Next) || !ReferenceEquals(journal.SupportProofDraft, journal.Next))
            { pending.AbortReason = PendingAbortReason.SupportDraftIdentity; return false; }
            // Native edits can happen while generation/baking is pending. All
            // exact absence rays and broadphase facts are read again on the main
            // publication boundary; no mesh-reference/AABB geometry identity is
            // claimed for a fallback span that intersects a collider's bounds.
            Physics.SyncTransforms();
            if (journal.RetainSupportSnapshot)
            {
                var snapshot = journal.SupportSnapshot;
                if (snapshot == null || !ReferenceEquals(snapshot.Draft, journal.Next)
                    || snapshot.Bounds.Length != _staticColliders.Count)
                { pending.AbortReason = PendingAbortReason.SupportSnapshotIdentity; return false; }
                foreach (var expected in snapshot.Bounds)
                    if (!_staticColliders.Contains(expected.Collider)
                        || !ReadGravitySupportBounds(expected.Collider, out var current)
                        || current.Eligible != expected.Eligible
                        || !current.Minimum.Equals(expected.Minimum) || !current.Maximum.Equals(expected.Maximum))
                    { pending.AbortReason = PendingAbortReason.SupportNativeBoundsChanged; return false; }
            }
            foreach (var span in journal.SupportAbsenceSpans)
                for (int i = 0; i < span.Count; i++)
                {
                    var cell = span.Bottom + Vector3Int.up * i;
                    int hits = Physics.RaycastNonAlloc(BlockWorld.Center(cell), Vector3.down, _rayHits,
                        StaticSupportProbeLength, Splatoon.Combat.PlayerMotorSimulation.WorldMask & ~(1 << TerrainLayer), QueryTriggerInteraction.Ignore);
                    LastPublicationSupportAbsenceRays++;
                    // A full NonAlloc buffer cannot prove that no registered
                    // eligible support was omitted. Replay instead of publishing.
                    if (hits == _rayHits.Length) { pending.AbortReason = PendingAbortReason.SupportRayBufferFull; return false; }
                    for (int h = 0; h < hits; h++)
                        if (IsQuantizedStaticSupport(cell, _rayHits[h]))
                        { pending.AbortReason = PendingAbortReason.SupportRayNowOccupied; return false; }
                }
            return true;
        }
        static void IncludeVerticalPiece(HashSet<Vector3Int> keys, Vector3Int bottom, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                var first = bottom + Vector3Int.up * offset;
                int length = System.Math.Min(count - offset, 16 - (first.y & 15));
                IncludeCell(keys, first);
                if (length > 1) IncludeCell(keys, first + Vector3Int.up * (length - 1));
                offset += length;
            }
        }
        bool ValidateStaticProofs(LinkedList<GravityStaticProof> proofs)
        {
            LastPublicationStaticProofs = 0;
            if (proofs == null) return true;
            foreach (var proof in proofs)
            { LastPublicationStaticProofs++; if (!ValidateStaticProof(proof)) return false; }
            return true;
        }
    }
}
