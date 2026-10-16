using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        const int InlineHeadCopyLimit = 128;
        Vector2Int? _headCopyRequest;
        BlockPublicationWindow _headCopyWindow;
        Vector2Int _headCopyColumn;
        BlockWorld _headCopySource;
        uint _headCopyRevision, _headCopyRound, _headCopyEpoch;
        ulong _headCopyMutation;
        SortedSet<GravityMotionKey> _headCopyOriginal, _headCopyPrepared;
        IEnumerator<GravityMotionKey> _headCopyIterator;
        bool _headCopyComplete, _headCopyReady;
        sealed class PreparedHeadIndex
        {
            public SortedSet<GravityMotionKey> Original, Prepared;
            public BlockPublicationWindow Window;
        }
        Dictionary<Vector2Int, PreparedHeadIndex> _preparedHeadIndexes = new();
        BlockWorld _preparedHeadSource;
        uint _preparedHeadRevision, _preparedHeadRound, _preparedHeadEpoch;
        ulong _preparedHeadMutation;
        public long HeadPreparationStarts, HeadPreparationStages, HeadPreparationWork, HeadPreparationAborts;
        public ulong HeadPreparationProgressVersion { get; private set; }
        bool HasHeadPreparation => _headCopyRequest.HasValue || _headCopySource != null && !_headCopyReady;

        void AbortHeadPreparation()
        {
            if (HasHeadPreparation) HeadPreparationAborts++;
            _headCopyIterator?.Dispose(); _headCopyIterator = null;
            _headCopySource = null; _headCopyRequest = null; _headCopyWindow = null; _headCopyOriginal = _headCopyPrepared = null;
            _headCopyComplete = _headCopyReady = false;
            // A source-scoped cache is replaced in one fixed metadata operation.
            // Ready A must survive while a separate request prepares B.
            _preparedHeadIndexes = new Dictionary<Vector2Int, PreparedHeadIndex>(); _preparedHeadSource = null;
        }
        bool HeadCopySourceValid()
            => _headCopySource != null && _headCopyEpoch == _epoch && ReferenceEquals(_headCopySource, World)
                && Revision == _headCopyRevision && Round == _headCopyRound && World.MutationGeneration == _headCopyMutation
                && ReferenceEquals(_headCopyOriginal, _cohortHeads.TryGetValue(_headCopyColumn, out var heads) ? heads : null);
        bool PreparedHeadSourceValid()
            => _preparedHeadSource != null && ReferenceEquals(_preparedHeadSource, World)
                && _preparedHeadEpoch == _epoch && _preparedHeadRevision == Revision && _preparedHeadRound == Round
                && _preparedHeadMutation == World.MutationGeneration;
        bool HasPreparedHeads(Vector2Int column)
            => PreparedHeadSourceValid() && _preparedHeadIndexes.TryGetValue(column, out var value)
                && ReferenceEquals(value.Original, _cohortHeads.TryGetValue(column, out var original) ? original : null);
        SortedSet<GravityMotionKey> TakePreparedHeads(Vector2Int column)
        {
            if (!HasPreparedHeads(column)) throw new InvalidOperationException("A prepared head index belongs to a different published source.");
            var result = _preparedHeadIndexes[column].Prepared;
            _preparedHeadIndexes.Remove(column);
            return result;
        }
        BlockPublicationWindow PreparedHeadPublicationWindow()
        {
            if (!PreparedHeadSourceValid()) return null;
            foreach (var value in _preparedHeadIndexes.Values) return value.Window;
            return null;
        }
        void RequestHeadPreparation(Vector2Int column)
            => RequestWindowHeadPreparation(column, _stagePublicationWindow ?? _publicationPolicy.Capture(BlockPublicationOperation.Gravity));
        void RequestWindowHeadPreparation(Vector2Int column, BlockPublicationWindow window)
        {
            if (HasPreparedHeads(column)) return;
            if (_headCopySource != null && !HeadCopySourceValid()) AbortHeadPreparation();
            if (!HasHeadPreparation) { _headCopyRequest = column; _headCopyWindow = window; }
        }
        bool AdvanceHeadPreparation()
        {
            if (!HasHeadPreparation) return false;
            bool commandWork = _commands.Count > 0;
            int maximum = commandWork ? 1024 : MaxChecks, checks = 0;
            double milliseconds = commandWork ? .25 : .75;
            var timer = Stopwatch.StartNew(); long before = GravityWorkChecks;
            AuthorityStageCount++; HeadPreparationStages++; LastCommandStageMilliseconds = 0;
            try
            {
                if (_headCopySource == null)
                {
                    if (!SpendGravity(ref checks, 16, maximum, timer, milliseconds)) return true;
                    _headCopyColumn = _headCopyRequest.Value; _headCopyRequest = null;
                    _headCopySource = World; _headCopyEpoch = _epoch; _headCopyRevision = Revision; _headCopyRound = Round;
                    _headCopyMutation = World.MutationGeneration;
                    _headCopyOriginal = _cohortHeads.TryGetValue(_headCopyColumn, out var heads) ? heads : null;
                    _headCopyIterator = _headCopyOriginal?.GetEnumerator(); _headCopyPrepared = new SortedSet<GravityMotionKey>();
                    _headCopyComplete = false; HeadPreparationStarts++; HeadPreparationProgressVersion++;
                }
                if (!HeadCopySourceValid()) { AbortHeadPreparation(); return true; }
                // Finite metadata preparation serializes authority mutations, just
                // as an existing pending bake does. Published motion keeps running.
                while (!_headCopyComplete && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (!SpendGravity(ref checks, 128, maximum, timer, milliseconds)) return true;
                    if (_headCopyIterator == null || !_headCopyIterator.MoveNext()) _headCopyComplete = true;
                    else _headCopyPrepared.Add(_headCopyIterator.Current);
                    HeadPreparationProgressVersion++;
                }
                if (!_headCopyComplete || !SpendGravity(ref checks, 16, maximum, timer, milliseconds)) return true;
                _headCopyIterator?.Dispose(); _headCopyIterator = null;
                if (!PreparedHeadSourceValid())
                {
                    _preparedHeadIndexes = new Dictionary<Vector2Int, PreparedHeadIndex>();
                    _preparedHeadSource = _headCopySource; _preparedHeadEpoch = _headCopyEpoch;
                    _preparedHeadRevision = _headCopyRevision; _preparedHeadRound = _headCopyRound; _preparedHeadMutation = _headCopyMutation;
                }
                _preparedHeadIndexes[_headCopyColumn] = new PreparedHeadIndex { Original = _headCopyOriginal, Prepared = _headCopyPrepared, Window = _headCopyWindow };
                _headCopySource = null; _headCopyWindow = null; _headCopyOriginal = _headCopyPrepared = null; _headCopyReady = _headCopyComplete = false;
                HeadPreparationProgressVersion++; return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[BLOCK] Head metadata preparation rejected: " + exception.Message);
                AbortHeadPreparation(); return true;
            }
            finally
            {
                HeadPreparationWork += GravityWorkChecks - before; LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (GravityWorkChecks != before) GravityProgressStages++;
            }
        }
    }
}
