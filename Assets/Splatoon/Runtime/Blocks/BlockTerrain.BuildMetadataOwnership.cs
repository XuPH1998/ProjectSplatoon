using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // Exclusive to one unpublished build preparation. No callback, world
        // enumeration, sorting, worker or native proof migration occurs here.
        sealed class BuildMetadataOwnership : IDisposable
        {
            public int Stage, CapCount, BindingCount, LegacyCount, MaximumTargets;
            public int TargetsCollected, CellsRebound, CapsRebound, BindingsRebound, LegaciesRebound;
            public int MaximumAdmittedStep;
            public long OwnershipWork;
            public ulong Mutation, PreparationVersion;
            public readonly uint Revision, Round;
            public LinkedList<BlockBuildMetadataWriteTarget> Targets = new();
            public IEnumerator<KeyValuePair<GravityMotionId, BuildLandingCap>> Caps;
            public IEnumerator<KeyValuePair<ulong, GravityBindingContinuity>> Bindings;
            public IEnumerator<KeyValuePair<ulong, LegacyLandingContinuity>> Legacies;
            public BlockBuildMetadataOwnershipPreparation Owner;
            public LinkedListNode<BuildColumnProof> Column;
            public LinkedListNode<BuildOccupiedCell> Cell;
            public BuildMetadataOwnership(BlockWorld draft)
            {
                Revision = draft.Revision; Round = draft.Round;
                Mutation = draft.MutationGeneration; PreparationVersion = draft.DependentPreparationVersion;
            }
            public void Dispose()
            {
                Caps?.Dispose(); Caps = null; Bindings?.Dispose(); Bindings = null;
                Legacies?.Dispose(); Legacies = null; Owner?.Dispose(); Owner = null;
                // Drop the frozen list and cursors in fixed bookkeeping. Never
                // walk a cancelled private draft or fall back to cloning it.
                Targets = null; Column = null; Cell = null;
            }
        }

        static void ValidateBuildMetadataIdentity(BuildOccupancyPreparation preparation)
        {
            var draft = preparation.Draft; var journal = preparation.Journal;
            if (!ReferenceEquals(preparation.Next, draft) || journal == null
                || !ReferenceEquals(journal.Next, draft)
                || journal.SupportSnapshot != null && !ReferenceEquals(journal.SupportSnapshot.Draft, draft)
                || journal.SupportProofDraft != null && !ReferenceEquals(journal.SupportProofDraft, draft))
                throw new InvalidOperationException("Build metadata ownership lost its exact draft/proof identity.");
        }
        static void ValidateBuildMetadataToken(BuildOccupancyPreparation preparation, BuildMetadataOwnership metadata)
        {
            var draft = preparation.Draft;
            if (draft.Revision != metadata.Revision || draft.Round != metadata.Round
                || draft.EstimatedPreparedPublicationWork != 0
                || metadata.Owner == null && (draft.DependentEditIncomplete
                    || draft.MutationGeneration != metadata.Mutation
                    || draft.DependentPreparationVersion != metadata.PreparationVersion)
                || metadata.Owner != null && !metadata.Owner.IsValid(preparation.Source, draft))
                throw new InvalidOperationException("Build metadata ownership lost its exact draft token.");
            if (metadata.Stage != 0 && (preparation.Caps.Count != metadata.CapCount
                || preparation.Journal.BindingContinuity.Count != metadata.BindingCount
                || preparation.Journal.LegacyContinuity.Count != metadata.LegacyCount))
                throw new InvalidOperationException("Build metadata writer collections changed after proof completion.");
        }
        bool SpendBuildMetadata(ref int checks, int cost, int maximum, Stopwatch timer, double milliseconds,
            BuildMetadataOwnership metadata)
        {
            if (!SpendBuildPreparation(ref checks, cost, maximum, timer, milliseconds)) return false;
            metadata.MaximumAdmittedStep = Math.Max(metadata.MaximumAdmittedStep, cost); return true;
        }
        // Called only after all original phase-2 proofs are complete. The three
        // writer dictionaries belong to this frozen preparation/journal. Each
        // MoveNext, dictionary lookup, scalar append and reference rebind has
        // explicit admission under the ORIGINAL stage count and wall budgets.
        bool AdvanceBuildMetadataOwnership(BuildOccupancyPreparation preparation, ref int checks,
            int maximum, Stopwatch timer, double milliseconds)
        {
            var metadata = preparation.MetadataOwnership;
            if (metadata == null || !preparation.ZeroPreparedMetadata || preparation.Clone != null)
                throw new InvalidOperationException("Build metadata ownership requires the zero-prepared branch.");
            ValidateBuildMetadataIdentity(preparation);
            ValidateBuildMetadataToken(preparation, metadata);
            while (metadata.Stage < 9 && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!preparation.SourceValid(this))
                    throw new InvalidOperationException("Build metadata ownership source changed.");
                ValidateBuildMetadataToken(preparation, metadata);
                switch (metadata.Stage)
                {
                    case 0:
                        if (!SpendBuildMetadata(ref checks, 16, maximum, timer, milliseconds, metadata)) return false;
                        metadata.CapCount = preparation.Caps.Count;
                        metadata.BindingCount = preparation.Journal.BindingContinuity.Count;
                        metadata.LegacyCount = preparation.Journal.LegacyContinuity.Count;
                        metadata.MaximumTargets = checked(metadata.CapCount + metadata.BindingCount + metadata.LegacyCount);
                        metadata.Caps = preparation.Caps.GetEnumerator(); metadata.Stage = 1; break;
                    case 1:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Caps.MoveNext())
                        {
                            // All caps are writers, including currently infinite
                            // caps. Publication time determines applicability.
                            var id = metadata.Caps.Current.Key;
                            metadata.Targets.AddLast(new BlockBuildMetadataWriteTarget(id.Id, id.Cohort));
                            metadata.TargetsCollected++;
                        }
                        else
                        {
                            metadata.Caps.Dispose(); metadata.Caps = null;
                            metadata.Bindings = preparation.Journal.BindingContinuity.GetEnumerator(); metadata.Stage = 2;
                        }
                        break;
                    case 2:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Bindings.MoveNext())
                        {
                            ulong id = metadata.Bindings.Current.Key;
                            if (preparation.Draft.Cohorts.ContainsKey(id))
                            { metadata.Targets.AddLast(new BlockBuildMetadataWriteTarget(id, true)); metadata.TargetsCollected++; }
                        }
                        else
                        {
                            metadata.Bindings.Dispose(); metadata.Bindings = null;
                            metadata.Legacies = preparation.Journal.LegacyContinuity.GetEnumerator(); metadata.Stage = 3;
                        }
                        break;
                    case 3:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Legacies.MoveNext())
                        {
                            ulong id = metadata.Legacies.Current.Key;
                            if (preparation.Draft.Falling.TryGetValue(id, out var run) && run.Cohort == null)
                            { metadata.Targets.AddLast(new BlockBuildMetadataWriteTarget(id, false)); metadata.TargetsCollected++; }
                        }
                        else
                        {
                            metadata.Legacies.Dispose(); metadata.Legacies = null;
                            // The frozen LinkedList enumerator is scalar and O(1).
                            // Duplicates remain; the primitive skips owned writers.
                            metadata.Owner = new BlockBuildMetadataOwnershipPreparation(preparation.Source,
                                preparation.Draft, metadata.Targets, metadata.MaximumTargets);
                            metadata.Stage = 4;
                        }
                        break;
                    case 4:
                        if (!metadata.Owner.Ready)
                        {
                            int cost = metadata.Owner.EstimateNextStepWork();
                            if (!SpendBuildMetadata(ref checks, cost, maximum, timer, milliseconds, metadata)) return false;
                            ulong version = metadata.Owner.ProgressVersion;
                            metadata.Owner.Step(preparation.Source, preparation.Draft, cost, out int actual);
                            if (actual <= 0 || actual > cost || metadata.Owner.Failed || metadata.Owner.ProgressVersion == version)
                                throw new InvalidOperationException("Build metadata ownership failed its admitted step.");
                            metadata.OwnershipWork += actual; break;
                        }
                        if (!SpendBuildMetadata(ref checks, 16, maximum, timer, milliseconds, metadata)) return false;
                        if (!ReferenceEquals(metadata.Owner.TakeWorld(preparation.Source, preparation.Draft), preparation.Draft))
                            throw new InvalidOperationException("Build metadata ownership replaced the admitted draft.");
                        metadata.Owner.Dispose(); metadata.Owner = null; metadata.Targets = null;
                        metadata.Mutation = preparation.Draft.MutationGeneration;
                        metadata.PreparationVersion = preparation.Draft.DependentPreparationVersion;
                        metadata.Column = preparation.ColumnOrder.First; metadata.Stage = 5; break;
                    case 5:
                        // Column transitions and empty columns are also admitted.
                        if (metadata.Cell == null)
                        {
                            if (!SpendBuildMetadata(ref checks, 4, maximum, timer, milliseconds, metadata)) return false;
                            if (metadata.Column == null)
                            { metadata.Caps = preparation.Caps.GetEnumerator(); metadata.Stage = 6; break; }
                            metadata.Cell = metadata.Column.Value.Cells.First; metadata.Column = metadata.Column.Next;
                            break;
                        }
                        if (!SpendBuildMetadata(ref checks, BlockWorld.MaxCohortPieceCells + 18,
                            maximum, timer, milliseconds, metadata)) return false;
                        var cell = metadata.Cell.Value;
                        preparation.Next.TryGetFallingCell(cell.SourceRun, cell.SourceIndex, out cell.PreparedRun, out _);
                        metadata.CellsRebound++; metadata.Cell = metadata.Cell.Next; break;
                    case 6:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Caps.MoveNext())
                        {
                            var pair = metadata.Caps.Current; var cap = pair.Value;
                            if (pair.Key.Cohort)
                            {
                                if (!preparation.Next.Cohorts.TryGetValue(pair.Key.Id, out cap.Group))
                                    throw new InvalidOperationException("A frozen cohort cap disappeared.");
                                cap.Legacy = null;
                            }
                            else
                            {
                                if (!preparation.Next.Falling.TryGetValue(pair.Key.Id, out cap.Legacy) || cap.Legacy.Cohort != null)
                                    throw new InvalidOperationException("A frozen legacy cap disappeared or changed kind.");
                                cap.Group = null;
                            }
                            metadata.CapsRebound++;
                        }
                        else
                        {
                            metadata.Caps.Dispose(); metadata.Caps = null;
                            metadata.Bindings = preparation.Journal.BindingContinuity.GetEnumerator(); metadata.Stage = 7;
                        }
                        break;
                    case 7:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Bindings.MoveNext())
                        {
                            var pair = metadata.Bindings.Current;
                            preparation.Next.Cohorts.TryGetValue(pair.Key, out pair.Value.Next);
                            // Source intentionally remains the live source owner.
                            metadata.BindingsRebound++;
                        }
                        else
                        {
                            metadata.Bindings.Dispose(); metadata.Bindings = null;
                            metadata.Legacies = preparation.Journal.LegacyContinuity.GetEnumerator(); metadata.Stage = 8;
                        }
                        break;
                    case 8:
                        if (!SpendBuildMetadata(ref checks, 8, maximum, timer, milliseconds, metadata)) return false;
                        if (metadata.Legacies.MoveNext())
                        {
                            var pair = metadata.Legacies.Current;
                            preparation.Next.Falling.TryGetValue(pair.Key, out pair.Value.Next);
                            if (pair.Value.Next != null && pair.Value.Next.Cohort != null) pair.Value.Next = null;
                            metadata.LegaciesRebound++;
                        }
                        else
                        {
                            metadata.Legacies.Dispose(); metadata.Legacies = null;
                            ValidateBuildMetadataIdentity(preparation); metadata.Stage = 9; metadata.Dispose();
                        }
                        break;
                }
            }
            ValidateBuildMetadataIdentity(preparation);
            ValidateBuildMetadataToken(preparation, metadata);
            return metadata.Stage == 9;
        }
    }
}
