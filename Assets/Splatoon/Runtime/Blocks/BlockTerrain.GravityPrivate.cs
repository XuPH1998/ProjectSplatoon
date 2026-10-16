using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        sealed class GravityPrivateRequest
        {
            public BlockPublicationWindow Window;
            public GravityMotionId Motion;
            public Vector2Int Column;
            public FallingCohortReplacement Replacement;
            public uint Epoch;
            public double SourceTime;
        }
        GravityPrivateRequest _privateGravityRequest, _activePrivateGravityRequest;
        FallingPrivateReplacement _privateGravityPreparation;
        BlockWorld _privateGravityNext;
        GravityJournal _privateGravityJournal;
        int _privateGravityChildCursor, _privateGravityExistingCursor;
        bool _privateGravityParentRemoved, _privateGravityHeadsCopied;
        IEnumerator<GravityMotionKey> _privateGravityHeadSource;
        SortedSet<GravityMotionKey> _privateGravityPreparedHeads;
        bool _privateGravityProofInitialized;
        FallingPrivateLandingProofRequest _privateGravityProof;
        int _privateGravityProofPhase, _privateGravityProofMinimum, _privateGravityProofScan, _privateGravityProofLanding, _privateGravityProofValidate;
        public long PrivatePreparationStarts, PrivatePreparationStages, PrivatePreparationWork, PrivatePreparationAborts;
        public ulong PrivatePreparationProgressVersion { get; private set; }
        public bool HasPrivatePreparation => _privateGravityRequest != null || _privateGravityPreparation != null;

        void AbortPrivatePreparation()
        {
            _gravityRetryPublicationWindow = null;
            AbortDependentEditPreparation();
            AbortHeadPreparation();
            AbortBuildOccupancyPreparation();
            AbortHistoricalRecovery();
            if (_privateGravityPreparation != null || _privateGravityRequest != null || _activePrivateGravityRequest != null) PrivatePreparationAborts++;
            _privateGravityPreparation?.Dispose();
            _privateGravityHeadSource?.Dispose(); _privateGravityHeadSource = null; _privateGravityPreparedHeads = null;
            _privateGravityPreparation = null; _privateGravityRequest = _activePrivateGravityRequest = null;
            _privateGravityNext = null; _privateGravityJournal = null;
            _privateGravityChildCursor = _privateGravityExistingCursor = 0; _privateGravityParentRemoved = _privateGravityHeadsCopied = false;
            _privateGravityProofInitialized = false; _privateGravityProofPhase = _privateGravityProofValidate = 0;
            _privateGravityStaticProofs = new LinkedList<GravityStaticProof>();
        }
        bool AdvancePrivateLandingProof(FallingPrivateReplacement prepare, ref int checks, int maximum, Stopwatch timer, double milliseconds)
        {
            if (!_privateGravityProofInitialized)
            {
                if (!SpendGravity(ref checks, 1, maximum, timer, milliseconds)) return false;
                _privateGravityProof = prepare.LandingProofRequest;
                if (_privateGravityProof.LiveLowerId != 0 && _privateGravityProof.TargetCohortId == 0)
                    throw new InvalidOperationException("A long live support chain still requires a budgeted bottom-up support proof.");
                _privateGravityProofInitialized = true; _privateGravityProofPhase = _privateGravityProofValidate = 0;
                PrivatePreparationProgressVersion++;
            }
            var proof = _privateGravityProof;
            int span = proof.SourceTopExclusive - proof.SourceBottom.y;
            if (span <= 0 || span > BlockWorld.MaxCohortCells || _privateGravityProofPhase < 0 || _privateGravityProofPhase > 2
                || _privateGravityProofValidate < 0 || _privateGravityProofValidate > span)
                throw new InvalidOperationException("The private landing proof cursor is outside its finite span.");
            if (_privateGravityProofPhase == 0)
            {
                // The source World is locked, so stable cells are the same in the
                // private clone. Static support queries exclude terrain colliders.
                // A full hit buffer cannot certify the closest eligible support.
                if (!SpendGravity(ref checks, 67, maximum, timer, milliseconds)) return false;
                var position = BlockWorld.Center(proof.SourceBottom) - Vector3.up * proof.SourceOffsetAtSourceTime;
                if (!QueryStaticProof(position, World.Bottom, out _privateGravityProofMinimum))
                    throw new InvalidOperationException("The private static support query saturated its bounded hit buffer.");
                // One fixed linked node per admitted query; no hidden array growth.
                // Keep every owner/upper proof until the same actual publication.
                _privateGravityStaticProofs.AddLast(new GravityStaticProof(position, World.Bottom, _privateGravityProofMinimum));
                _privateGravityProofScan = Math.Min(proof.SourceBottom.y, Mathf.FloorToInt(position.y / BlockWorld.CellSize));
                _privateGravityProofLanding = _privateGravityProofMinimum; _privateGravityProofPhase = 1; _privateGravityProofValidate = 0;
                PrivatePreparationProgressVersion++;
            }
            while (_privateGravityProofPhase == 1 && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendGravity(ref checks, 2, maximum, timer, milliseconds)) return false;
                if (_privateGravityProofScan < _privateGravityProofMinimum)
                { _privateGravityProofPhase = 2; _privateGravityProofValidate = 0; }
                else
                {
                    var at = new Vector3Int(proof.SourceBottom.x, _privateGravityProofScan, proof.SourceBottom.z);
                    bool ownAt = at.y >= proof.SourceDetachCursor && at.y < proof.SourceTopExclusive;
                    bool ownBelow = at.y - 1 >= proof.SourceDetachCursor && at.y - 1 < proof.SourceTopExclusive;
                    if (!ownAt && World.TryGet(at, out _)) { _privateGravityProofLanding = at.y + 1; _privateGravityProofPhase = 2; _privateGravityProofValidate = 0; }
                    else if (!ownBelow && World.TryGet(at + Vector3Int.down, out _)) { _privateGravityProofLanding = at.y; _privateGravityProofPhase = 2; _privateGravityProofValidate = 0; }
                    else _privateGravityProofScan--;
                }
                PrivatePreparationProgressVersion++;
            }
            while (_privateGravityProofPhase == 2 && _privateGravityProofValidate < span && HasStageBudget(checks, maximum, timer, milliseconds))
            {
                if (!SpendGravity(ref checks, 1, maximum, timer, milliseconds)) return false;
                var destination = new Vector3Int(proof.SourceBottom.x, _privateGravityProofLanding + _privateGravityProofValidate, proof.SourceBottom.z);
                bool ownPendingSource = destination.y >= proof.SourceDetachCursor && destination.y < proof.SourceTopExclusive;
                if (!ownPendingSource && World.TryGet(destination, out _))
                { _privateGravityProofLanding = destination.y + 1 - _privateGravityProofValidate; _privateGravityProofValidate = 0; }
                else _privateGravityProofValidate++;
                if (_privateGravityProofLanding > proof.SourceBottom.y)
                    throw new InvalidOperationException("Private landing cannot resolve a stable overlap by moving a falling child upward.");
                PrivatePreparationProgressVersion++;
            }
            if (_privateGravityProofPhase != 2 || _privateGravityProofValidate != span) return false;
            if (!SpendGravity(ref checks, 2, maximum, timer, milliseconds)) return false;
            if ((proof.SourceBottom.y - _privateGravityProofLanding) * BlockWorld.CellSize < proof.SourceOffsetAtSourceTime - .000001f)
                throw new HistoricalRecoveryRequired(new HistoricalRecoveryRequest
                {
                    // A new private child has no source-world identity yet;
                    // classify its existing published parent if the old pose
                    // already crossed the newly certified own floor.
                    Motion = proof.TargetCohortId == 0 || !World.Cohorts.ContainsKey(proof.TargetCohortId)
                        ? _activePrivateGravityRequest.Motion : new GravityMotionId(proof.TargetCohortId, true),
                    Column = new Vector2Int(proof.SourceBottom.x, proof.SourceBottom.z), Epoch = _epoch, Window = _activePrivateGravityRequest.Window
                });
            prepare.ProvideLandingProof(World, (proof.SourceBottom.y - _privateGravityProofLanding) * BlockWorld.CellSize);
            _privateGravityProofInitialized = false; PrivatePreparationProgressVersion++; return true;
        }
        bool AdvancePrivatePreparation()
        {
            if (_privateGravityPreparation == null && _privateGravityRequest == null) return false;
            // As with an existing pending bake, queued commands remain FIFO and
            // retain their published Source/SourceTime. This finite transaction
            // serializes mutations while its private snapshot is being built.
            bool commandWork = _commands.Count > 0;
            int maximum = commandWork ? 1024 : MaxChecks, checks = 0;
            double milliseconds = commandWork ? .25 : .75;
            var timer = Stopwatch.StartNew(); long before = GravityWorkChecks;
            AuthorityStageCount++; PrivatePreparationStages++; LastCommandStageMilliseconds = 0;
            try
            {
                if (_privateGravityPreparation == null)
                {
                    if (!SpendGravity(ref checks, 16, maximum, timer, milliseconds)) return true;
                    var startingRequest = _privateGravityRequest; _privateGravityRequest = null;
                    _activePrivateGravityRequest = startingRequest;
                    if (startingRequest.Epoch != _epoch || startingRequest.Replacement == null || !startingRequest.Replacement.IsValid(World))
                    { AbortPrivatePreparation(); return true; }
                    _privateGravityPreparation = World.BeginPrivateReplacement(startingRequest.Replacement, startingRequest.SourceTime);
                    _privateGravityNext = null; _privateGravityJournal = null; _privateGravityChildCursor = _privateGravityExistingCursor = 0;
                    _privateGravityParentRemoved = _privateGravityHeadsCopied = false;
                    _privateGravityProofInitialized = false; _privateGravityProofPhase = _privateGravityProofValidate = 0;
                    PrivatePreparationStarts++;
                }
                var prepare = _privateGravityPreparation;
                if (_activePrivateGravityRequest.Epoch != _epoch || !prepare.IsValid(World) || prepare.Failed)
                { AbortPrivatePreparation(); return true; }
                while (!prepare.Ready && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    if (prepare.WaitingForLandingProof)
                    {
                        if (!AdvancePrivateLandingProof(prepare, ref checks, maximum, timer, milliseconds)) return true;
                        continue;
                    }
                    int cost = prepare.EstimateNextStepWork();
                    if (cost <= 0 || cost > maximum) throw new InvalidOperationException("Private gravity action cannot fit the unchanged stage budget.");
                    if (!SpendGravity(ref checks, cost, maximum, timer, milliseconds)) break;
                    ulong version = prepare.ProgressVersion;
                    prepare.Step(World, cost, out int actual);
                    if (actual > cost || prepare.Failed || prepare.ProgressVersion == version)
                        throw new InvalidOperationException("Private gravity preparation failed or made no admitted progress.");
                    PrivatePreparationWork += actual; PrivatePreparationProgressVersion++;
                }
                if (!prepare.Ready) return true;
                if (_privateGravityNext == null)
                {
                    if (!SpendGravity(ref checks, 8, maximum, timer, milliseconds)) return true;
                    _privateGravityNext = prepare.TakePreparedWorld();
                    _privateGravityJournal = new GravityJournal { Next = _privateGravityNext, Window = _activePrivateGravityRequest.Window,
                        ColumnCursor = _cohortColumnCursor, MotionCursor = _cohortMotionCursor };
                }
                _gravityJournal = _privateGravityJournal;
                var request = _activePrivateGravityRequest;
                if (!_privateGravityParentRemoved)
                {
                    // Source-coordinate reuse and stale imported IDs can make the
                    // complete column index larger than one cohort. Copy it one
                    // admitted key at a time instead of requiring one large COW.
                    if (_privateGravityPreparedHeads == null)
                    {
                        _privateGravityPreparedHeads = new SortedSet<GravityMotionKey>();
                        _privateGravityHeadSource = _cohortHeads.TryGetValue(request.Column, out var heads) ? heads.GetEnumerator() : null;
                    }
                    while (!_privateGravityHeadsCopied && HasStageBudget(checks, maximum, timer, milliseconds))
                    {
                        // A red-black tree with an int-sized Count has height at
                        // most 62. Admit both iterator advance and key insertion.
                        if (!SpendGravity(ref checks, 128, maximum, timer, milliseconds)) return true;
                        if (_privateGravityHeadSource == null || !_privateGravityHeadSource.MoveNext())
                            _privateGravityHeadsCopied = true;
                        else _privateGravityPreparedHeads.Add(_privateGravityHeadSource.Current);
                        PrivatePreparationProgressVersion++;
                    }
                    if (!_privateGravityHeadsCopied || !SpendGravity(ref checks, 26, maximum, timer, milliseconds)) return true;
                    _privateGravityHeadSource?.Dispose(); _privateGravityHeadSource = null;
                    _gravityJournal.Heads[request.Column] = _privateGravityPreparedHeads;
                    if (_cohortMotionWork.TryGetValue(request.Motion, out var parent)) RemoveMotion(request.Motion, parent);
                    _privateGravityParentRemoved = true; PrivatePreparationProgressVersion++;
                }
                while (_privateGravityChildCursor < prepare.Children.Count && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    // Children contain at most 400 source cells in this parent,
                    // while the complete reused column index can be larger.
                    var child = prepare.Children[_privateGravityChildCursor];
                    var id = new GravityMotionId(child.Id, true);
                    if (!SpendGravity(ref checks, EstimateQueueMotionWork(id, request.Column), maximum, timer, milliseconds)) return true;
                    QueueMotion(id, request.Column, child.Bottom.y, child.TopExclusive); _privateGravityChildCursor++;
                    PrivatePreparationProgressVersion++;
                }
                if (_privateGravityChildCursor != prepare.Children.Count) return true;
                while (_privateGravityExistingCursor < prepare.PreparedExistingCohorts.Count && HasStageBudget(checks, maximum, timer, milliseconds))
                {
                    var owner = prepare.PreparedExistingCohorts[_privateGravityExistingCursor];
                    var id = new GravityMotionId(owner.Id, true);
                    bool indexed = _cohortMotionWork.TryGetValue(id, out var oldWork);
                    int cost = indexed ? 32 : EstimateQueueMotionWork(id, request.Column);
                    if (!SpendGravity(ref checks, cost, maximum, timer, milliseconds)) return true;
                    if (indexed)
                    {
                        if (oldWork.SourceBottom != owner.Bottom.y || oldWork.Column != request.Column)
                            throw new InvalidOperationException("Existing private support owner changed its source-order key.");
                        // Discard the old unpublished proof cursor, not its World
                        // cells. This fixed record avoids copying a large COW proof.
                        _gravityJournal.Motions[id] = new GravityMotionWork
                            { Column = request.Column, SourceBottom = owner.Bottom.y, SourceTop = owner.TopExclusive };
                    }
                    else QueueMotion(id, request.Column, owner.Bottom.y, owner.TopExclusive);
                    _privateGravityExistingCursor++; PrivatePreparationProgressVersion++;
                }
                if (_privateGravityExistingCursor != prepare.PreparedExistingCohorts.Count) return true;
                if (!SpendGravity(ref checks, 16, maximum, timer, milliseconds)) return true;
                var final = _privateGravityNext; var journal = _privateGravityJournal;
                final.Seal(Revision + 1, Array.Empty<Vector3Int>());
                BeginWindowTransaction(final, new HashSet<Vector3Int>(), false, null, journal.Window);
                _pending.Gravity = journal; _pending.PrivateGravity = true; _pending.StaticProofs = _privateGravityStaticProofs;
                _pending.PrivateSource = prepare.SourceWorld; _pending.PrivateSourceRevision = prepare.SourceRevision;
                _pending.PrivateSourceRound = prepare.SourceRound; _pending.PrivateSourceMutation = prepare.SourceMutationGeneration;
                _privateGravityPreparation.Dispose(); _privateGravityPreparation = null;
                _privateGravityNext = null; _privateGravityJournal = null; _activePrivateGravityRequest = null; _privateGravityPreparedHeads = null;
                _privateGravityStaticProofs = new LinkedList<GravityStaticProof>();
                return true;
            }
            catch (Exception exception)
            {
                // The published source, command cursors, collision and score did
                // not change. Release the lock and let the source proof retry.
                bool historical = exception is HistoricalRecoveryRequired;
                if (!historical)
                { UnityEngine.Debug.LogWarning("[BLOCK] Private gravity preparation rejected: " + exception.Message); FailedTransactions++; }
                AbortPrivatePreparation();
                if (_pending != null && _pending.PrivateGravity) AbortPendingCore(PreserveFairSeedForPreparation(_pending));
                if (exception is HistoricalRecoveryRequired recovery) _historicalRecoveryRequest = recovery.Request;
                return true;
            }
            finally
            {
                _gravityJournal = null; LastGravityStageMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (GravityWorkChecks != before) GravityProgressStages++;
            }
        }
    }
}
