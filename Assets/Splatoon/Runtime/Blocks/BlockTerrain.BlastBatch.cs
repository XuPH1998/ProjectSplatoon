using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    // Reports-only candidate. No Assets source has been modified.
    public sealed partial class BlockTerrain
    {
        const int MaxCoalescedBlastCommands = 8;
        UnboundedBlastBatch _blastBatch;

        sealed class UnboundedBlastBatch
        {
            public Command[] Commands;
            public bool[] Grouped;
            public bool Captured, HasChunk, Dequeued;
            public Vector3Int Chunk;
            public int CommandIndex;
        }

        static bool UnboundedBlast(Command command) => command != null && !command.Build
            && !command.EnemyOnly && !command.Done && !command.Completed && command.Source != null
            && command.MaxEnemy == int.MaxValue && command.MaxFriendly == int.MaxValue;

        static bool SameUnboundedSource(Command command, Command first) => UnboundedBlast(command)
            && ReferenceEquals(command.Source, first.Source) && command.SourceTime == first.SourceTime;

        bool HasCoalescedBlastWork()
        {
            if (_blastBatch != null && _commands.Count != 0
                && ReferenceEquals(_commands.Peek(), _blastBatch.Commands[0])) return true;
            if (_commands.Count < 2) return false;
            var first = _commands.Peek();
            if (!UnboundedBlast(first) || first.TargetIndex != 0) return false;
            using var iterator = _commands.GetEnumerator();
            iterator.MoveNext();
            return iterator.MoveNext() && SameUnboundedSource(iterator.Current, first)
                && iterator.Current.TargetIndex == 0;
        }

        bool TryStageUnboundedBlastBatch(BlockWorld next, HashSet<Vector3Int> changed,
            List<(Command, BlockMutationResult)> results, ref int checks,
            System.Diagnostics.Stopwatch timer, double milliseconds, int maxChecks)
        {
            if (_blastBatch != null && (_commands.Count == 0
                || !ReferenceEquals(_commands.Peek(), _blastBatch.Commands[0]))) _blastBatch = null;
            if (_blastBatch == null)
            {
                if (!HasCoalescedBlastWork()) return false;
                var first = _commands.Peek(); var group = new List<Command>(MaxCoalescedBlastCommands);
                // A finite quota, build, different snapshot/time, or ninth command
                // remains an ordering barrier; no command is dropped or truncated.
                foreach (var command in _commands)
                {
                    if (group.Count == MaxCoalescedBlastCommands || !SameUnboundedSource(command, first)
                        || command.TargetIndex != 0 || !CanRunPublicationCommand(command)) break;
                    group.Add(command);
                }
                if (group.Count < 2) return false;
                _blastBatch = new UnboundedBlastBatch { Commands = group.ToArray(), Grouped = new bool[group.Count] };
            }
            var batch = _blastBatch;
            CheckpointCoalescedBlastBatch(batch);
            // A Gravity21 retry may have restored all indexes. Choosing the
            // minimum source-chunk head afresh also makes every new stage safe.
            batch.HasChunk = false; batch.CommandIndex = 0;
            if (!batch.Captured)
            {
                bool ready = true;
                // Each complete set is obtained by the existing occupied-row,
                // visibility, falling-time and unbounded sorting implementation.
                // A pending worker for an earlier command need not block capture
                // of later commands inside this same bounded main-thread stage.
                for (int i = 0; i < batch.Commands.Length; i++)
                {
                    var command = batch.Commands[i];
                    if (command.Targets == null)
                    {
                        if (!HasStageBudget(checks, maxChecks, timer, milliseconds)) { ready = false; break; }
                        if (!PrepareDestruction(command, timer, ref checks, milliseconds, maxChecks)) ready = false;
                    }
                    if (command.Targets != null && !batch.Grouped[i])
                    {
                        // Existing <=64 preparation sorts nearest-first. A fully
                        // unbounded batch may group that complete small set too;
                        // otherwise a later command could claim a duplicate
                        // before an earlier command reaches its second chunk.
                        if (command.Targets.Length <= 64) Array.Sort(command.Targets, CompareDestroyTargetsBySourceChunk);
                        batch.Grouped[i] = true;
                    }
                }
                if (!ready) return true;
                batch.Captured = true;
            }

            var changes = new BlockMutationResult[batch.Commands.Length];
            bool finished = false, limited = false;
            while (HasStageBudget(checks, maxChecks, timer, milliseconds))
            {
                checks++; CommandWorkChecks++;
                int owner = NextBatchCommand(batch);
                if (owner < 0) { finished = true; break; }
                var command = batch.Commands[owner];
                var target = command.Targets[command.TargetIndex];
                if (target.Run != 0)
                {
                    // Frozen source addresses remain valid across cohort fractures.
                    // Alias lookup is part of admission, including miss paths.
                    const int lookupCost = BlockWorld.MaxCohortPieceCells + 2;
                    if (checks + lookupCost > maxChecks || !HasStageBudget(checks, maxChecks, timer, milliseconds)) { limited = true; break; }
                    checks += lookupCost; CommandWorkChecks += lookupCost;
                    if (!next.TryGetFallingCell(target.Run, target.Index, out var run, out int resolvedIndex)) { command.TargetIndex++; continue; }
                    var column = new Vector2Int(run.Bottom.x, run.Bottom.z);
                    int motionCost = run.Cohort == null ? 0 : EstimateQueueMotionWork(new GravityMotionId(run.Cohort.Id, true), column);
                    int removeCost = next.EstimateRemoveFallingCellWork(target.Run, target.Index) + motionCost + EstimateQueueGravityWork(run.Bottom + Vector3Int.up * resolvedIndex);
                    if (checks + removeCost > maxChecks || !HasStageBudget(checks, maxChecks, timer, milliseconds)) { limited = true; break; }
                    checks += removeCost; CommandWorkChecks += removeCost; command.TargetIndex++;
                    if (run.Cells[resolvedIndex].Owner != target.Owner || !next.RemoveFallingCell(target.Run, target.Index, out _, out _)) continue;
                    QueueGravity(run.Bottom + Vector3Int.up * resolvedIndex);
                    if (run.Cohort != null) QueueMotion(new GravityMotionId(run.Cohort.Id, true), column, run.Cohort.Bottom.y, run.Cohort.TopExclusive);
                }
                else
                {
                    if (!next.TryGet(target.Cell, out var own) || own.Owner != target.Owner)
                    { command.TargetIndex++; continue; }
                    // The captured publication window includes neighbour
                    // meshes and shared gravity mutations.
                    if (!FitsBudget(_stagePublicationWindow, changed, target.Cell)) break;
                    command.TargetIndex++; next.Set(target.Cell, default);
                    IncludeCell(changed, target.Cell); QueueGravity(target.Cell);
                }
                if (target.Owner == command.Team) changes[owner].RemovedFriendly++;
                else changes[owner].RemovedEnemy++;
            }
            // The last removal must close the batch in this publication even
            // when it used the final timer/check unit, as ordinary quota Done does.
            if (!finished && !limited) finished = Array.TrueForAll(batch.Commands, command => command.TargetIndex >= command.Targets.Length);
            if (finished)
            {
                foreach (var command in batch.Commands)
                {
                    if (_commands.Count == 0 || !ReferenceEquals(_commands.Peek(), command))
                        throw new InvalidOperationException("Unbounded blast batch queue changed inside one simulation stage.");
                    command.Done = true; _commands.Dequeue();
                }
                batch.Dequeued = true; _blastBatch = null;
            }
            // Include zero-result participants too: a transaction failure must
            // retire all batch participants through the existing Fail/Abort path.
            // Totals and callbacks still change only after actual publication.
            for (int i = 0; i < batch.Commands.Length; i++) results.Add((batch.Commands[i], changes[i]));
            return true;
        }

        void CheckpointCoalescedBlastBatch(UnboundedBlastBatch batch)
        {
            // One head checkpoint owns this group's retry state. Eight ordinary
            // checkpoints would EnqueueFront participants still in the queue,
            // duplicating commands during Gravity21 proof/recovery retries.
            var checkpoint = CheckpointCommand(batch.Commands[0]);
            if (ReferenceEquals(checkpoint.BlastBatch, batch)) return;
            checkpoint.BlastBatch = batch;
            checkpoint.BlastTargetIndexes = new int[batch.Commands.Length];
            checkpoint.BlastDone = new bool[batch.Commands.Length];
            for (int i = 0; i < batch.Commands.Length; i++)
            {
                checkpoint.BlastTargetIndexes[i] = batch.Commands[i].TargetIndex;
                checkpoint.BlastDone[i] = batch.Commands[i].Done;
            }
        }

        bool RestoreCoalescedBlastCheckpoint(CommandCheckpoint checkpoint)
        {
            var batch = checkpoint.BlastBatch;
            if (batch == null) return false;
            for (int i = 0; i < batch.Commands.Length; i++)
            {
                batch.Commands[i].RetryWindow = checkpoint.Window;
                batch.Commands[i].TargetIndex = checkpoint.BlastTargetIndexes[i];
                batch.Commands[i].Done = checkpoint.BlastDone[i];
            }
            // Partial stages keep the whole group in its original queue slots.
            // A fully staged group was dequeued, so restore it exactly once in
            // front of the later commands, preserving original queue order.
            if (batch.Dequeued)
                for (int i = batch.Commands.Length - 1; i >= 0; i--) _commands.EnqueueFront(batch.Commands[i]);
            batch.Dequeued = false; batch.HasChunk = false; batch.CommandIndex = 0;
            _blastBatch = batch;
            return true;
        }

        static int CompareBatchChunks(Vector3Int a, Vector3Int b)
        {
            int order = a.x.CompareTo(b.x); if (order != 0) return order;
            order = a.y.CompareTo(b.y); return order != 0 ? order : a.z.CompareTo(b.z);
        }

        static int NextBatchCommand(UnboundedBlastBatch batch)
        {
            for (;;)
            {
                if (!batch.HasChunk)
                {
                    bool found = false; Vector3Int first = default;
                    foreach (var command in batch.Commands)
                    {
                        if (command.TargetIndex >= command.Targets.Length) continue;
                        var key = BlockWorld.ChunkAt(command.Targets[command.TargetIndex].Cell);
                        if (!found || CompareBatchChunks(key, first) < 0) { first = key; found = true; }
                    }
                    if (!found) return -1;
                    batch.Chunk = first; batch.CommandIndex = 0; batch.HasChunk = true;
                }
                for (; batch.CommandIndex < batch.Commands.Length; batch.CommandIndex++)
                {
                    var command = batch.Commands[batch.CommandIndex];
                    if (command.TargetIndex < command.Targets.Length
                        && BlockWorld.ChunkAt(command.Targets[command.TargetIndex].Cell) == batch.Chunk)
                        // All targets in a chunk for earlier queue participants
                        // execute first. A shared target is therefore credited
                        // to its earliest queued attacker, exactly once.
                        return batch.CommandIndex;
                }
                batch.HasChunk = false;
            }
        }
    }
}
