using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        sealed class GravityBindingContinuity
        {
            public FallingCohort Source, Next;
            public Vector2Int Column;
            public float Offset, Speed, PublicationOffset, PublicationCap;
        }
        sealed class LegacyLandingContinuity
        {
            public FallingRun Source, Next;
            public Vector2Int Column;
            public float Offset, Speed, PublicationOffset, PublicationCap;
        }
        public long GravityBindingPublicationDeferrals, LastGravityBindingPublicationWork;
        sealed class RejectedGravityBinding
        {
            public BlockWorld Source;
            public ulong Mutation, Lower;
        }
        Dictionary<ulong, RejectedGravityBinding> _rejectedGravityBindings = new();
        BlockWorld _rejectedBindingSource;
        ulong _rejectedBindingMutation;
        void RefreshRejectedBindingSource()
        {
            if (ReferenceEquals(_rejectedBindingSource, World) && _rejectedBindingMutation == World.MutationGeneration) return;
            _rejectedBindingSource = World; _rejectedBindingMutation = World.MutationGeneration;
            _rejectedGravityBindings = new Dictionary<ulong, RejectedGravityBinding>();
        }
        bool GravityBindingWasRejected(ulong upper, ulong lower)
        {
            RefreshRejectedBindingSource();
            return _rejectedGravityBindings.TryGetValue(upper, out var rejected) && rejected.Lower == lower;
        }
        GravityMotionId? _rejectedGravityBinding;
        Vector2Int _rejectedGravityBindingColumn;
        void RecordGravityBindingContinuity(BlockWorld next, FallingCohort group, Vector2Int column)
        {
            if (!World.Cohorts.TryGetValue(group.Id, out var source) || source.Phase != FallingCohortPhase.Falling) return;
            _gravityJournal.BindingContinuity[group.Id] = new GravityBindingContinuity
                { Source = source, Next = next.Cohorts[group.Id], Column = column };
        }
        void RecordLegacyLandingContinuity(BlockWorld next, FallingRun run, Vector2Int column)
        {
            if (!World.Falling.TryGetValue(run.Id, out var source) || source.Cohort != null) return;
            _gravityJournal.LegacyContinuity[run.Id] = new LegacyLandingContinuity
                { Source = source, Next = next.Falling[run.Id], Column = column };
        }
        bool ValidateGravityBindingPublication(Pending pending)
        {
            LastGravityBindingPublicationWork = 0; _rejectedGravityBinding = null;
            if (pending.Gravity == null) return true;
            RefreshRejectedBindingSource();
            // Sample every original pose before reading any candidate graph.
            // The final lower pose must not depend on dictionary iteration order.
            foreach (var pair in pending.Gravity.BindingContinuity)
            {
                var guard = pair.Value;
                if (!pending.Next.Cohorts.TryGetValue(pair.Key, out guard.Next)) continue;
                guard.Offset = guard.Source.Offset(_now); guard.Speed = guard.Source.Speed(_now);
                LastGravityBindingPublicationWork += 2L * BlockWorld.MaxCohortCells + 2;
            }
            foreach (var pair in pending.Gravity.LegacyContinuity)
            {
                var guard = pair.Value;
                if (!pending.Next.Falling.TryGetValue(pair.Key, out guard.Next)) continue;
                if (guard.Next.Cohort != null) { guard.Next = null; continue; }
                guard.Offset = guard.Source.Offset(_now); guard.Speed = guard.Source.Speed(_now);
                LastGravityBindingPublicationWork += 2;
            }
            if (pending.BuildGuard != null)
                foreach (var cap in pending.BuildGuard.Caps.Values)
                {
                    cap.PublicationApplies = BuildPublicationCapApplies(pending, cap);
                    LastGravityBindingPublicationWork += 4;
                }
            var offsets = new Dictionary<ulong, float>();
            foreach (var pair in pending.Gravity.BindingContinuity)
            {
                var guard = pair.Value; if (guard.Next == null) continue;
                FinalBindingOffset(pending, guard.Next, offsets, 0);
                float cap = guard.PublicationCap;
                // Sampling and rebasing are independent atomic-publication work.
                // The validated live+retained motion depth is at most400.
                LastGravityBindingPublicationWork += 6;
                if (cap + .000001f < guard.Offset)
                {
                    _rejectedGravityBinding = new GravityMotionId(pair.Key, true);
                    _rejectedGravityBindings[pair.Key] = new RejectedGravityBinding { Source = World,
                        Mutation = World.MutationGeneration, Lower = guard.Next.LowerSupport?.Id ?? 0 };
                    pending.AbortReason = PendingAbortReason.BindingPoseAboveNewCap;
                    _rejectedGravityBindingColumn = guard.Column; return false;
                }
            }
            foreach (var pair in pending.Gravity.LegacyContinuity)
            {
                var guard = pair.Value; if (guard.Next == null) continue;
                float cap = guard.Next.LandingKnown ? guard.Next.LandingOffset : float.PositiveInfinity;
                if (pending.BuildGuard != null && pending.BuildGuard.Caps.TryGetValue(new GravityMotionId(pair.Key, false), out var buildCap))
                    cap = Mathf.Min(cap, buildCap.Cap);
                guard.PublicationCap = cap; guard.PublicationOffset = Mathf.Min(guard.Offset, cap);
                LastGravityBindingPublicationWork += 6;
                if (cap + .000001f < guard.Offset)
                {
                    _rejectedGravityBinding = new GravityMotionId(pair.Key, false);
                    pending.AbortReason = PendingAbortReason.BindingPoseAboveNewCap;
                    _rejectedGravityBindingColumn = guard.Column; return false;
                }
            }
            if (pending.BuildGuard != null)
                foreach (var pair in pending.BuildGuard.Caps)
                {
                    var buildCap = pair.Value;
                    if (!buildCap.PublicationApplies) continue;
                    if (buildCap.Group != null) FinalBindingOffset(pending, buildCap.Group, offsets, 0);
                    else
                    {
                        buildCap.PublicationCap = buildCap.Cap;
                        buildCap.PublicationOffset = pending.Gravity.LegacyContinuity.TryGetValue(pair.Key.Id, out var guard) && guard.Next != null
                            ? guard.PublicationOffset : Mathf.Min(buildCap.Sample.Offset, buildCap.Cap);
                    }
                    // Keep the immutable raw sample for rejection, even when a
                    // final lower pose selected a slightly nearer contact.
                    LastGravityBindingPublicationWork += 6;
                    if (buildCap.PublicationCap + .000001f >= buildCap.Sample.Offset) continue;
                    _rejectedGravityBinding = pair.Key;
                    var bottom = buildCap.Group != null ? buildCap.Group.Bottom : buildCap.Legacy.Bottom;
                    _rejectedGravityBindingColumn = new Vector2Int(bottom.x, bottom.z);
                    if (buildCap.Group != null)
                        _rejectedGravityBindings[pair.Key.Id] = new RejectedGravityBinding { Source = World,
                            Mutation = World.MutationGeneration, Lower = buildCap.Group.LowerSupport?.Id ?? 0 };
                    pending.AbortReason = PendingAbortReason.BindingPoseAboveNewCap; return false;
                }
            return true;
        }
        float PublicationOwnLandingCap(Pending pending, FallingCohort group)
        {
            if (!group.LandingKnown) return float.PositiveInfinity;
            float cap = group.LandingOffset;
            // Predict the existing activation writer without mutating its cap.
            // Verified private crossings still fail in activation before any write.
            if (!pending.Remote && pending.Next.IsPreparedCohort(group.Id)
                && group.RebasePending && group.PreparedRebase != null)
            {
                LastGravityBindingPublicationWork += group.PreparedRebase.EvaluateOnce(_now, out _);
                cap = Mathf.Max(cap, group.PreparedRebase.Offset);
            }
            return cap;
        }
        float FinalBindingPublicationCap(Pending pending, FallingCohort group, Dictionary<ulong, float> memo, int depth)
        {
            float cap = PublicationOwnLandingCap(pending, group);
            if (pending.BuildGuard != null && pending.BuildGuard.Caps.TryGetValue(new GravityMotionId(group.Id, true), out var buildCap))
                cap = Mathf.Min(cap, buildCap.Cap);
            if (group.LowerSupport != null)
                cap = Mathf.Min(cap, FinalBindingOffset(pending, group.LowerSupport, memo, depth + 1) + group.SupportClearance);
            else if (group.RetainedSupportMotion.Length != 0)
            {
                if (depth + 1 + group.RetainedSupportMotion.Length > BlockWorld.MaxCohortCells)
                    throw new InvalidOperationException("Invalid retained final support depth.");
                FrozenCohortMotion.Evaluate(group.RetainedSupportMotion, _now, out float lowerOffset, out _);
                LastGravityBindingPublicationWork += group.RetainedSupportMotion.Length;
                cap = Mathf.Min(cap, lowerOffset + group.RetainedSupportClearance);
            }
            return cap;
        }
        float FinalBindingOffset(Pending pending, FallingCohort group, Dictionary<ulong, float> memo, int depth)
        {
            if (depth >= BlockWorld.MaxCohortCells) throw new InvalidOperationException("Invalid final support depth.");
            if (memo.TryGetValue(group.Id, out float cached)) return cached;
            LastGravityBindingPublicationWork += 2;
            pending.Gravity.BindingContinuity.TryGetValue(group.Id, out var guard);
            BuildLandingCap buildCap = null;
            bool appliesBuild = pending.BuildGuard != null
                && pending.BuildGuard.Caps.TryGetValue(new GravityMotionId(group.Id, true), out buildCap)
                && buildCap.PublicationApplies;
            if ((guard != null && guard.Next != null) || appliesBuild)
            {
                float cap = FinalBindingPublicationCap(pending, group, memo, depth);
                float raw = guard != null && guard.Next != null ? guard.Offset : buildCap.Sample.Offset;
                float selected = Mathf.Min(raw, cap);
                if (guard != null && guard.Next != null)
                { guard.PublicationCap = cap; guard.PublicationOffset = selected; }
                if (appliesBuild)
                { buildCap.PublicationCap = cap; buildCap.PublicationOffset = selected; }
                return memo[group.Id] = selected;
            }
            if (group.RebasePending && group.PreparedRebase != null)
            {
                LastGravityBindingPublicationWork += group.PreparedRebase.EvaluateOnce(_now, out _);
                return memo[group.Id] = group.PreparedRebase.Offset;
            }
            float lowerOffset = 0, lowerSpeed = 0; bool hasLower = false;
            if (group.LowerSupport != null)
            { lowerOffset = FinalBindingOffset(pending, group.LowerSupport, memo, depth + 1); hasLower = true; }
            else if (group.RetainedSupportMotion.Length != 0)
            {
                if (depth + 1 + group.RetainedSupportMotion.Length > BlockWorld.MaxCohortCells)
                    throw new InvalidOperationException("Invalid retained final support depth.");
                FrozenCohortMotion.Evaluate(group.RetainedSupportMotion, _now, out lowerOffset, out lowerSpeed);
                LastGravityBindingPublicationWork += group.RetainedSupportMotion.Length; hasLower = true;
            }
            var node = new FrozenCohortMotion(group.Phase, group.StartedAt, group.InitialOffset, group.InitialSpeed,
                group.LandingKnown, group.LandingOffset, group.LowerSupport != null ? group.SupportClearance : group.RetainedSupportClearance);
            FrozenCohortMotion.EvaluateNode(node, _now, hasLower, lowerOffset, lowerSpeed, out float offset, out _);
            return memo[group.Id] = offset;
        }
        void ApplyGravityBindingContinuity(Pending pending)
        {
            if (pending.Gravity == null) return;
            foreach (var guard in pending.Gravity.BindingContinuity.Values)
            {
                if (guard.Next == null) continue;
                // Preserve the sampled trajectory, canonicalizing only contact
                // discrepancies already admitted by the raw-pose guard. Removing
                // a support cannot release old accumulated free-fall displacement.
                guard.Next.StartedAt = _now; guard.Next.InitialOffset = guard.PublicationOffset; guard.Next.InitialSpeed = guard.Speed;
                LastGravityBindingPublicationWork += 4;
            }
            foreach (var guard in pending.Gravity.LegacyContinuity.Values)
            {
                if (guard.Next == null) continue;
                guard.Next.StartedAt = _now; guard.Next.InitialOffset = guard.PublicationOffset; guard.Next.InitialSpeed = guard.Speed;
                LastGravityBindingPublicationWork += 4;
            }
        }
        void RetryGravityBindingPublication(Pending pending)
        {
            var motion = _rejectedGravityBinding;
            if (pending.BuildGuard != null) RetryBuildPublication(pending);
            else
            {
                RecordPendingTerminal(pending, PendingWallOutcome.Aborted);
                _retired.Enqueue(new Retired { Pending = pending }); _pending = null;
                _buildOccupancyPreparation = new BuildOccupancyPreparation { Retry = true, Window = pending.Window,
                    Checkpoints = pending.CommandCheckpoints, RetryCursor = pending.CommandCheckpoints?.Last };
            }
            GravityBindingPublicationDeferrals++;
            if (motion.HasValue) RequestWindowHistoricalRecovery(motion.Value, _rejectedGravityBindingColumn, pending.Window);
        }
    }
}
