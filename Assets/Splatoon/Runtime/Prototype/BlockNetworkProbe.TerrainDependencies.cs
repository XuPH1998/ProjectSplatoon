#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockNetworkProbe
    {
        [Serializable] sealed class TerrainTraceIdentity
        {
            public int matchObjectId, playerObjectId, connectionGeneration, traceConnectionOrdinal;
            public ulong owner, networkObjectId;
            public uint life;
            public bool playerIdentityAvailable, connectionGenerationKnown, traceConnectionOrdinalKnown;
        }
        [Serializable] sealed class ReplayDependencyEntrySample
        {
            public Vector3Int chunk;
            public uint requiredRevision, appliedRevision;
            public bool appliedAvailable;
        }
        [Serializable] sealed class ReplayDependencySetSample
        {
            public bool available, truncated;
            public int totalCount, terrainObjectId;
            public uint terrainRound, worldRevision;
            public uint surfaceRevision;
            public bool worldAvailable;
            public bool surfaceWorldAvailable;
            public ReplayDependencyEntrySample[] rows;
            public SurfaceReplayDependencyEntrySample[] surfaceRows;
            internal ReplayDependencySetSample(PrototypePlayer.ReplayDependencyReadSet source)
            {
                available = source.Available; truncated = source.Truncated; totalCount = source.TotalCount;
                terrainObjectId = source.TerrainObjectId; terrainRound = source.TerrainRound;
                worldRevision = source.WorldRevision; worldAvailable = source.WorldAvailable;
                surfaceRevision = source.SurfaceRevision; surfaceWorldAvailable = source.SurfaceWorldAvailable;
                rows = new ReplayDependencyEntrySample[source.Rows?.Length ?? 0];
                for (int i = 0; i < rows.Length; i++) rows[i] = new ReplayDependencyEntrySample
                { chunk = source.Rows[i].Chunk, requiredRevision = source.Rows[i].Required,
                    appliedRevision = source.Rows[i].Applied, appliedAvailable = source.Rows[i].AppliedAvailable };
                surfaceRows = new SurfaceReplayDependencyEntrySample[source.SurfaceRows?.Length ?? 0];
                for (int i = 0; i < surfaceRows.Length; i++) surfaceRows[i] = new SurfaceReplayDependencyEntrySample
                { face = source.SurfaceRows[i].Key.FaceId, u = source.SurfaceRows[i].Key.U, v = source.SurfaceRows[i].Key.V,
                    requiredRevision = source.SurfaceRows[i].Required, appliedRevision = source.SurfaceRows[i].Applied,
                    appliedAvailable = source.SurfaceRows[i].AppliedAvailable };
            }
        }
        [Serializable] sealed class SurfaceReplayDependencyEntrySample
        {
            public int face, u, v;
            public uint requiredRevision, appliedRevision;
            public bool appliedAvailable;
        }
        [Serializable] sealed class SurfacePaintCellSample
        {
            public int face, u, v;
            internal SurfacePaintCellSample(BlockSurfaceKey key) { face = key.FaceId; u = key.U; v = key.V; }
        }
        [Serializable] sealed class ReplayDependencyCheckSample
        {
            public long eventId, invocation, beginStamp, endStamp;
            public string source, stage;
            public TerrainTraceIdentity identity;
            public uint previousLife, previousRound, terrainRound, requiredSupportRevision;
            public Vector3Int supportCell;
            public bool supportIsSurfacePaint;
            public int supportSurfaceFace, supportSurfaceU, supportSurfaceV;
            public bool previousRoundKnown, terrainAvailable, boundary, dead, cannotMove, inputTimedOut, ready;
            public bool satisfactionCheckPerformed, satisfiedTruncated;
            public int satisfiedCount;
            public int satisfiedSurfaceCount;
            public Vector3Int[] satisfiedChunks;
            public SurfacePaintCellSample[] satisfiedSurfaceCells;
            public ReplayDependencySetSample before, after;
        }
        [Serializable] sealed class TerrainReconcileAssociationSample
        {
            public long sourceEventId, dependencyInvocation, dependencyEventId, reconcileInvocation, stamp;
            public TerrainTraceIdentity identity;
            public string stage;
            public bool sourceEventAvailable, dependencyEventAvailable;
        }
        [Serializable] sealed class TerrainTransferSample
        {
            public long eventId, stamp;
            public string stage, phase, reason;
            public TerrainTraceIdentity identity;
            public uint id, round, baseRevision, revision, hash;
            public int index, length, fragmentCount, receivedBefore, receivedAfter;
            public bool full, resultKnown, result;
            public TerrainInstallTokenSample actualPublication, expectedInstall, associatedPublication;
            public string associationAvailability;
            public bool originalManifestBranchMatched;
        }
        [Serializable] sealed class TerrainInstallTokenSample
        {
            public int terrainObjectId, matchObjectId, length, fragmentCount;
            public long installId, pendingId, terminalStamp;
            public uint id, round, baseRevision, revision, hash;
            public bool full, acceptedAvailable, publishedAvailable;
            internal TerrainInstallTokenSample(BlockTerrain.RemoteInstallToken token)
            {
                terrainObjectId = token.TerrainObjectId; installId = token.InstallId; pendingId = token.PendingId; terminalStamp = token.TerminalStamp;
                acceptedAvailable = token.AcceptedAvailable; publishedAvailable = token.PublishedAvailable;
                var transfer = token.Transfer; matchObjectId = transfer.MatchObjectId; id = transfer.Id; round = transfer.Round;
                baseRevision = transfer.BaseRevision; revision = transfer.Revision; hash = transfer.Hash; full = transfer.Full;
                length = transfer.Length; fragmentCount = transfer.FragmentCount;
            }
        }
        [Serializable] sealed class TerrainTransferLifecycleSample
        {
            public string key, disposition = "Open", lastPhase;
            public TerrainTraceIdentity identity;
            public uint id, round, baseRevision, revision, hash;
            public int length, fragmentCount, receivedCount;
            public bool full, open = true, accepted, published, callbackExact, ackExact, providerSnapshotConfirmed;
            public long createdStamp, lastStamp, endedStamp, acceptedInstallId, publishedPendingId;
        }
        [Serializable] sealed class TerrainTransferEndOpenSample
        {
            public TerrainTraceIdentity identity;
            public string key, reason;
            public bool snapshotAvailable, incomingPresent, installing;
            public uint id, round, baseRevision, revision, hash;
            public int receivedCount, fragmentCount, length;
            public bool full;
        }
        [Serializable] sealed class TerrainPublicationSample
        {
            public long eventId, stamp, installId, pendingId, pendingCreatedStamp;
            public string stage, phase;
            public int terrainObjectId;
            public TerrainTraceIdentity identity;
            public uint round, revision, transferId, transferRound, transferBaseRevision, transferRevision, transferHash;
            public bool transferScopeAvailable, full, timingAvailable;
            public int transferLength, transferFragmentCount;
            public bool generationObserved, uploadCompleted, bakeObserved, publishEntered;
            public double generationObservedMs, uploadMs, bakeObservedTailMs, readyWaitMs, publicationMs, totalMs;
        }
        [Serializable] sealed class TerrainDependenciesReport
        {
            public int schema = 2, dependencyEventSchema = 1, transferEventSchema = 2, publicationEventSchema = 2, sourceReconcileSchema = 1;
            public string session, label, role, sourceProbePath;
            public string scope = "opt-in replay gate dependencies and explicit accepted/pending/terminal/callback/ACK tokens; immutable trace connection ordinal separate from source generation; assembly endOpen; same-process Stopwatch; formal raw statistics unchanged";
            public long clockFrequency, probeBootStamp, finishStamp;
            public double probeBootRealtime;
            public bool enabled = true, captureComplete, sourceReconcileComplete, sourceProbeComplete, clientScopeApplicable;
            public int dependencyLimit = 32768, transferLimit = 32768, publicationLimit = 8192, associationLimit = 16384;
            public int consumerErrors, providerErrors, sourceReconcileErrors, openInstalls, unavailableAssociations, unavailableIdentityRows;
            public int matchLimit = 32, lifecycleLimit = 8192, sourceAppendLimit = 16384, openTransfers, unknownLifecycleRows, snapshotErrors, unconsumedSourceAppends;
            public long dependencySeen, transferSeen, publicationSeen, associationSeen, droppedRows;
            public bool readSetTruncated;
            public List<ReplayDependencyCheckSample> dependencyChecks = new();
            public List<TerrainTransferSample> transfers = new();
            public List<TerrainPublicationSample> publications = new();
            public List<TerrainReconcileAssociationSample> reconcileAssociations = new();
            public List<TerrainTransferLifecycleSample> transferLifecycles = new();
            public List<TerrainTransferEndOpenSample> endOpen = new();
        }
        TerrainDependenciesReport _terrainDependencies;
        readonly Dictionary<(int match, int player, long invocation), long> _terrainDependencyRows = new();
        readonly HashSet<(int terrain, long install)> _terrainOpenInstalls = new();
        sealed class TerrainMatchRegistration
        {
            public PrototypeMatch Match;
            public int ObjectId, TerrainObjectId, Ordinal, SourceGeneration;
            public bool SourceGenerationKnown;
        }
        readonly List<TerrainMatchRegistration> _terrainMatches = new();
        readonly Dictionary<string, TerrainTransferLifecycleSample> _terrainTransfers = new();
        readonly struct TerrainSourceAppendToken
        {
            public readonly long EventId;
            public readonly int MatchObjectId, TraceOrdinal, SourceGeneration;
            public TerrainSourceAppendToken(long id, TerrainMatchRegistration match, int generation)
            { EventId = id; MatchObjectId = match?.ObjectId ?? 0; TraceOrdinal = match?.Ordinal ?? 0; SourceGeneration = generation; }
        }
        readonly Dictionary<(int player, long invocation), TerrainSourceAppendToken> _terrainSourceAppends = new();
        int _terrainDependencyProviderBaseline, _terrainTransferProviderBaseline, _terrainInstallProviderBaseline;
        bool _terrainDependencySubscribed, _terrainDependencyFinalized;
        public bool TerrainDependenciesWritten { get; private set; }
        public string TerrainDependenciesWriteError { get; private set; }

        void BeginTerrainDependencyCollection()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-blockTerrainDependencies") < 0) return;
            try
            {
                _terrainDependencies = new TerrainDependenciesReport { session = _report.session, label = _report.label,
                    sourceProbePath = _output, clockFrequency = Stopwatch.Frequency,
                    probeBootStamp = Stopwatch.GetTimestamp(), probeBootRealtime = _boot };
                _terrainDependencyProviderBaseline = PrototypePlayer.ReplayDependencyProviderErrors;
                _terrainTransferProviderBaseline = PrototypeMatch.TerrainTransferProviderErrors;
                _terrainInstallProviderBaseline = BlockTerrain.RemoteInstallProviderErrors;
                _terrainDependencySubscribed = true;
                PrototypePlayer.ReplayDependencyObserved += OnReplayDependencyObserved;
                PrototypeMatch.TerrainTransferObserved += OnTerrainTransferObserved;
                BlockTerrain.RemoteInstallObserved += OnRemoteInstallObserved;
                // Main consumer writes an explicit token only after its actual append succeeds.
                PrototypePlayer.ReconcileObserved += OnTerrainDependencyReconcile;
            }
            catch (Exception error) { TerrainDependenciesWriteError = "Initialization: " + error.GetType().Name; StopTerrainDependencyCollection(); }
        }
        TerrainMatchRegistration RegisterTerrainMatch(PrototypeMatch match)
        {
            if (match == null) return null;
            foreach (var entry in _terrainMatches)
                if (ReferenceEquals(entry.Match, match))
                {
                    if (entry.TerrainObjectId == 0 && match.Blocks != null) entry.TerrainObjectId = match.Blocks.GetInstanceID();
                    return entry;
                }
            if (_terrainMatches.Count >= _terrainDependencies.matchLimit) { _terrainDependencies.droppedRows++; return null; }
            var created = new TerrainMatchRegistration { Match = match, ObjectId = match.GetInstanceID(),
                TerrainObjectId = match.Blocks != null ? match.Blocks.GetInstanceID() : 0, Ordinal = _terrainMatches.Count + 1 };
            _terrainMatches.Add(created); return created;
        }
        TerrainMatchRegistration FindTerrainMatch(int objectId, int terrainObjectId)
        {
            TerrainMatchRegistration found = null;
            foreach (var entry in _terrainMatches)
                if (entry.ObjectId == objectId && entry.TerrainObjectId == terrainObjectId)
                { if (found != null) return null; found = entry; }
            // No fallback to global current generation or closest revision.
            return found;
        }
        TerrainTraceIdentity TerrainIdentity(PrototypeMatch match, PrototypePlayer player = null, uint life = 0)
            => TerrainIdentity(RegisterTerrainMatch(match), player, life);
        TerrainTraceIdentity TerrainIdentity(TerrainMatchRegistration match, PrototypePlayer player = null, uint life = 0)
        {
            return new TerrainTraceIdentity { matchObjectId = match?.ObjectId ?? 0,
                playerObjectId = player != null ? player.GetInstanceID() : 0, life = life,
                owner = player != null ? player.PlayerId : 0,
                networkObjectId = player != null && player.IsSpawned ? player.NetworkObjectId : 0,
                playerIdentityAvailable = player != null && player.IsSpawned,
                traceConnectionOrdinalKnown = match != null, traceConnectionOrdinal = match?.Ordinal ?? 0,
                connectionGenerationKnown = match != null && match.SourceGenerationKnown,
                connectionGeneration = match != null && match.SourceGenerationKnown ? match.SourceGeneration : 0 };
        }
        void MarkTerrainSourceAppend(PrototypeMatch match, PrototypePlayer player, PrototypePlayer.ReconcileTelemetryEvent observation, long eventId, int generation)
        {
            if (!TerrainTraceActive) return;
            try
            {
                var registered = RegisterTerrainMatch(match);
                if (registered != null)
                {
                    if (registered.SourceGenerationKnown && registered.SourceGeneration != generation)
                    { _terrainDependencies.unavailableAssociations++; return; }
                    registered.SourceGeneration = generation; registered.SourceGenerationKnown = true;
                }
                if (_terrainSourceAppends.Count >= _terrainDependencies.sourceAppendLimit) { _terrainDependencies.droppedRows++; return; }
                var key = (player.GetInstanceID(), observation.ReconcileInvocation);
                if (observation.ReconcileInvocation <= 0 || _terrainSourceAppends.ContainsKey(key))
                { _terrainDependencies.unavailableAssociations++; return; }
                _terrainSourceAppends.Add(key, new TerrainSourceAppendToken(eventId, registered, generation));
            }
            catch (Exception) { _terrainDependencies.consumerErrors++; }
        }
        bool TerrainTraceActive => !_finished && !_terrainDependencyFinalized && _terrainDependencies != null;
        void OnReplayDependencyObserved(PrototypePlayer player, PrototypePlayer.ReplayDependencyObservation source)
        {
            if (!TerrainTraceActive || player == null || player.IsServer) return;
            try
            {
                var report = _terrainDependencies; report.dependencySeen++;
                if (report.dependencyChecks.Count >= report.dependencyLimit) { report.droppedRows++; return; }
                var row = new ReplayDependencyCheckSample { eventId = report.dependencyChecks.Count + 1L,
                    invocation = source.Invocation, beginStamp = source.BeginStamp, endStamp = source.EndStamp,
                    source = source.Source == 1 ? "RefreshPrecheck" : source.Source == 2 ? "Reconcile" : source.Source == 3 ? "PredictInput" : "Unknown",
                    stage = _stage, identity = TerrainIdentity(PrototypeMatch.Current, player, source.State.Revision),
                    previousLife = source.PreviousLife, previousRound = source.PreviousRound, previousRoundKnown = source.PreviousRoundKnown,
                    terrainAvailable = source.TerrainAvailable, terrainRound = source.TerrainRound, boundary = source.Boundary,
                    dead = source.State.Health <= 0, cannotMove = !source.State.CanMove, inputTimedOut = source.State.InputTimedOut,
                    supportCell = source.State.SupportCell, requiredSupportRevision = source.State.SupportTerrainRevision, ready = source.Ready,
                    supportIsSurfacePaint = source.State.SupportIsSurfacePaint, supportSurfaceFace = source.State.SupportSurfaceFace,
                    supportSurfaceU = source.State.SupportSurfaceU, supportSurfaceV = source.State.SupportSurfaceV,
                    before = new ReplayDependencySetSample(source.Before), after = new ReplayDependencySetSample(source.After),
                    satisfactionCheckPerformed = source.SatisfactionCheckPerformed, satisfiedCount = source.SatisfiedCount,
                    satisfiedTruncated = source.SatisfiedTruncated, satisfiedChunks = (Vector3Int[])source.SatisfiedChunks.Clone(),
                    satisfiedSurfaceCount = source.SatisfiedSurfaceCount,
                    satisfiedSurfaceCells = new SurfacePaintCellSample[source.SatisfiedSurfaceCells?.Length ?? 0] };
                for (int i = 0; i < row.satisfiedSurfaceCells.Length; i++)
                    row.satisfiedSurfaceCells[i] = new SurfacePaintCellSample(source.SatisfiedSurfaceCells[i]);
                report.dependencyChecks.Add(row); _terrainDependencyRows.Add((row.identity.matchObjectId, player.GetInstanceID(), source.Invocation), row.eventId);
                if (!row.identity.traceConnectionOrdinalKnown || !row.identity.playerIdentityAvailable) report.unavailableIdentityRows++;
                report.readSetTruncated |= row.before.truncated || row.after.truncated || row.satisfiedTruncated;
            }
            catch (Exception) { _terrainDependencies.consumerErrors++; }
        }
        void OnTerrainDependencyReconcile(PrototypePlayer player, PrototypePlayer.ReconcileTelemetryEvent observation)
        {
            if (!TerrainTraceActive || player == null || player.IsServer) return;
            try
            {
                var report = _terrainDependencies; report.associationSeen++;
                if (report.reconcileAssociations.Count >= report.associationLimit) { report.droppedRows++; return; }
                var row = new TerrainReconcileAssociationSample { dependencyInvocation = observation.DependencyCheckInvocation,
                    reconcileInvocation = observation.ReconcileInvocation, stamp = observation.WallStamp, stage = _stage,
                    identity = TerrainIdentity(PrototypeMatch.Current, player, observation.Authority.Life) };
                var appendKey = (player.GetInstanceID(), observation.ReconcileInvocation);
                if (_terrainSourceAppends.TryGetValue(appendKey, out var token))
                {
                    _terrainSourceAppends.Remove(appendKey);
                    if (token.EventId > 0 && token.MatchObjectId == row.identity.matchObjectId && token.TraceOrdinal > 0 &&
                        token.TraceOrdinal == row.identity.traceConnectionOrdinal && row.identity.connectionGenerationKnown &&
                        token.SourceGeneration == row.identity.connectionGeneration)
                    { row.sourceEventAvailable = true; row.sourceEventId = token.EventId; }
                }
                row.dependencyEventAvailable = _terrainDependencyRows.TryGetValue((row.identity.matchObjectId, player.GetInstanceID(), row.dependencyInvocation), out row.dependencyEventId);
                if (!row.sourceEventAvailable || !row.dependencyEventAvailable) report.unavailableAssociations++;
                if (!row.identity.traceConnectionOrdinalKnown || !row.identity.playerIdentityAvailable) report.unavailableIdentityRows++;
                report.reconcileAssociations.Add(row);
            }
            catch (Exception) { _terrainDependencies.consumerErrors++; }
        }
        void OnTerrainTransferObserved(PrototypeMatch match, PrototypeMatch.TerrainTransferObservation source)
        {
            if (!TerrainTraceActive || match == null || match.IsServer) return;
            try
            {
                var report = _terrainDependencies; report.transferSeen++;
                if (report.transfers.Count >= report.transferLimit) { report.droppedRows++; return; }
                var row = new TerrainTransferSample { eventId = report.transfers.Count + 1L,
                    stamp = source.Stamp, stage = _stage, phase = source.Phase, reason = source.Reason, identity = TerrainIdentity(match),
                    id = source.Id, round = source.Round, baseRevision = source.BaseRevision, revision = source.Revision, hash = source.Hash,
                    index = source.Index, length = source.Length, fragmentCount = source.FragmentCount, full = source.Full,
                    resultKnown = source.ResultKnown, result = source.Result, receivedBefore = source.ReceivedBefore, receivedAfter = source.ReceivedAfter,
                    actualPublication = new TerrainInstallTokenSample(source.ActualPublication), expectedInstall = new TerrainInstallTokenSample(source.ExpectedInstall),
                    associatedPublication = new TerrainInstallTokenSample(source.AssociatedPublication), associationAvailability = source.AssociationAvailability,
                    originalManifestBranchMatched = source.OriginalManifestBranchMatched };
                report.transfers.Add(row);
                if (!row.identity.traceConnectionOrdinalKnown) report.unavailableIdentityRows++;
                ObserveTerrainTransferLifecycle(row);
            }
            catch (Exception) { _terrainDependencies.consumerErrors++; }
        }
        void OnRemoteInstallObserved(BlockTerrain terrain, BlockTerrain.RemoteInstallObservation source)
        {
            if (!TerrainTraceActive) return;
            try
            {
                var report = _terrainDependencies; report.publicationSeen++;
                if (report.publications.Count >= report.publicationLimit) { report.droppedRows++; return; }
                var context = source.Transfer;
                var registered = FindTerrainMatch(context.MatchObjectId, terrain.GetInstanceID());
                if (registered == null)
                {
                    var current = PrototypeMatch.Current;
                    if (current != null && !current.IsServer && current.GetInstanceID() == context.MatchObjectId && current.Blocks == terrain)
                        registered = RegisterTerrainMatch(current);
                }
                var row = new TerrainPublicationSample { eventId = report.publications.Count + 1L,
                    stamp = source.Stamp, stage = _stage, phase = source.Phase, terrainObjectId = terrain.GetInstanceID(),
                    installId = source.InstallId, pendingId = source.PendingId, pendingCreatedStamp = source.PendingCreatedStamp,
                    identity = TerrainIdentity(registered), round = source.Round, revision = source.Revision,
                    transferScopeAvailable = context.Available && registered != null && context.MatchObjectId == registered.ObjectId,
                    transferId = context.Id, transferRound = context.Round, transferBaseRevision = context.BaseRevision,
                    transferRevision = context.Revision, transferHash = context.Hash, transferLength = context.Length,
                    transferFragmentCount = context.FragmentCount, full = context.Full, timingAvailable = source.TimingAvailable };
                if (source.TimingAvailable)
                {
                    var timing = source.Timing; row.generationObserved = timing.GenerationObserved; row.uploadCompleted = timing.UploadCompleted;
                    row.bakeObserved = timing.BakeObserved; row.publishEntered = timing.PublishEntered;
                    row.generationObservedMs = timing.GenerationObservedMs; row.uploadMs = timing.UploadMs;
                    row.bakeObservedTailMs = timing.BakeObservedTailMs; row.readyWaitMs = timing.ReadyWaitMs;
                    row.publicationMs = timing.PublicationMs; row.totalMs = timing.TotalMs;
                }
                report.publications.Add(row);
                var identity = (terrain.GetInstanceID(), source.InstallId);
                if (source.Phase == "Queued") _terrainOpenInstalls.Add(identity);
                else if (source.Phase != "Staged") _terrainOpenInstalls.Remove(identity);
                if (source.InstallId <= 0 || !row.transferScopeAvailable) report.unavailableAssociations++;
                if (!row.identity.traceConnectionOrdinalKnown) report.unavailableIdentityRows++;
                ObserveTerrainPublicationLifecycle(row);
            }
            catch (Exception) { _terrainDependencies.consumerErrors++; }
        }
        static string TerrainTransferKey(int ordinal, uint round, uint id, uint baseRevision, uint revision, uint hash, int length, int count, bool full)
            => ordinal + ":" + round + ":" + id + ":" + baseRevision + ":" + revision + ":" + hash + ":" + length + ":" + count + ":" + (full ? "F" : "D");
        string TerrainTransferKey(TerrainTransferSample row)
            => TerrainTransferKey(row.identity.traceConnectionOrdinal, row.round, row.id, row.baseRevision, row.revision, row.hash, row.length, row.fragmentCount, row.full);
        static void EndTerrainTransfer(TerrainTransferLifecycleSample lifecycle, string disposition, long stamp)
        { lifecycle.open = false; lifecycle.disposition = disposition; lifecycle.endedStamp = stamp; }
        void ObserveTerrainTransferLifecycle(TerrainTransferSample row)
        {
            var report = _terrainDependencies;
            var key = TerrainTransferKey(row);
            if (row.phase == "AssemblyCreated")
            {
                if (!row.identity.traceConnectionOrdinalKnown || _terrainTransfers.ContainsKey(key)) { report.unknownLifecycleRows++; return; }
                if (_terrainTransfers.Count >= report.lifecycleLimit) { report.droppedRows++; return; }
                var created = new TerrainTransferLifecycleSample { key = key, identity = row.identity,
                    id = row.id, round = row.round, baseRevision = row.baseRevision, revision = row.revision, hash = row.hash,
                    length = row.length, fragmentCount = row.fragmentCount, full = row.full, createdStamp = row.stamp, lastStamp = row.stamp, lastPhase = row.phase };
                _terrainTransfers.Add(key, created); report.transferLifecycles.Add(created); return;
            }
            if (row.phase == "CompletedDuplicateAckCallReturned")
            {
                if (row.associationAvailability != "Exact" || !row.associatedPublication.publishedAvailable)
                    report.unavailableAssociations++;
                return;
            }
            if (row.phase == "Received" || row.phase == "Ignored" ||
                row.phase == "AddReturned" && row.resultKnown && !row.result) return;
            if (!_terrainTransfers.TryGetValue(key, out var lifecycle)) { report.unknownLifecycleRows++; return; }
            lifecycle.lastPhase = row.phase; lifecycle.lastStamp = row.stamp;
            if (row.receivedAfter >= 0) lifecycle.receivedCount = row.receivedAfter;
            if (row.phase == "VerifyReturned" && row.resultKnown && !row.result) EndTerrainTransfer(lifecycle, "VerifyRejected", row.stamp);
            else if (row.phase == "ApplyReturned" && row.resultKnown && !row.result) EndTerrainTransfer(lifecycle, "ApplyRejected", row.stamp);
            else if (row.phase == "ApplyReturned" && row.resultKnown && row.result) lifecycle.accepted = true;
            else if (row.phase == "Abandoned") EndTerrainTransfer(lifecycle, "Abandoned:" + row.reason, row.stamp);
            else if (row.phase == "PublishedCommitCallback")
            {
                bool exact = row.associationAvailability == "Exact" && row.associatedPublication.publishedAvailable &&
                    lifecycle.acceptedInstallId == row.associatedPublication.installId && lifecycle.published &&
                    lifecycle.publishedPendingId == row.associatedPublication.pendingId;
                lifecycle.callbackExact |= exact;
                if (!exact) report.unavailableAssociations++;
            }
            else if (row.phase == "AppliedAckCallReturned")
            {
                bool exact = row.associationAvailability == "Exact" && row.associatedPublication.publishedAvailable &&
                    lifecycle.callbackExact && lifecycle.acceptedInstallId == row.associatedPublication.installId &&
                    lifecycle.publishedPendingId == row.associatedPublication.pendingId;
                lifecycle.ackExact = exact;
                if (exact) EndTerrainTransfer(lifecycle, "AppliedAckCallReturned", row.stamp);
                else { report.unavailableAssociations++; lifecycle.disposition = "AckAssociationUnavailable"; }
            }
        }
        void ObserveTerrainPublicationLifecycle(TerrainPublicationSample row)
        {
            var key = TerrainTransferKey(row.identity.traceConnectionOrdinal, row.transferRound, row.transferId, row.transferBaseRevision,
                row.transferRevision, row.transferHash, row.transferLength, row.transferFragmentCount, row.full);
            if (!_terrainTransfers.TryGetValue(key, out var lifecycle)) { _terrainDependencies.unknownLifecycleRows++; return; }
            lifecycle.lastPhase = row.phase; lifecycle.lastStamp = row.stamp;
            if (row.phase == "Queued") { lifecycle.accepted = true; lifecycle.acceptedInstallId = row.installId; }
            else if (lifecycle.acceptedInstallId != row.installId) _terrainDependencies.unavailableAssociations++;
            else if (row.phase == "Published")
            { lifecycle.published = true; lifecycle.publishedPendingId = row.pendingId; }
            else if (row.phase != "Staged") EndTerrainTransfer(lifecycle, "InstallTerminal:" + row.phase, row.stamp);
        }
        bool HasOpenTerrainTransfer(int ordinal)
        {
            foreach (var lifecycle in _terrainTransfers.Values) if (lifecycle.open && lifecycle.identity.traceConnectionOrdinal == ordinal) return true;
            return false;
        }
        void InspectTerrainTransfersAtFinish()
        {
            var report = _terrainDependencies;
            // A final current provider check catches an assembly even if its opening row was missed.
            var current = PrototypeMatch.Current;
            if (current != null && !current.IsServer) RegisterTerrainMatch(current);
            foreach (var match in _terrainMatches)
            {
                if (match.Match == null)
                {
                    if (HasOpenTerrainTransfer(match.Ordinal))
                    {
                        report.snapshotErrors++;
                        report.endOpen.Add(new TerrainTransferEndOpenSample { identity = TerrainIdentity(match), reason = "OpenTransferProviderDestroyed" });
                    }
                    continue;
                }
                try
                {
                    var snapshot = match.Match.InspectTerrainTransferObservation();
                    if (!snapshot.Available) throw new InvalidOperationException("Transfer snapshot unavailable");
                    if (!snapshot.IncomingPresent) continue;
                    var manifest = snapshot.Manifest;
                    string key = TerrainTransferKey(match.Ordinal, manifest.Round, manifest.Id, manifest.BaseRevision, manifest.Revision,
                        manifest.Hash, manifest.Length, snapshot.FragmentCount, manifest.Full);
                    if (_terrainTransfers.TryGetValue(key, out var lifecycle))
                    {
                        lifecycle.providerSnapshotConfirmed = true; lifecycle.receivedCount = snapshot.ReceivedCount;
                        if (!lifecycle.open) report.unknownLifecycleRows++;
                    }
                    else report.unknownLifecycleRows++;
                    report.endOpen.Add(new TerrainTransferEndOpenSample { identity = TerrainIdentity(match), key = key,
                        reason = snapshot.Complete ? "ProviderIncomingAwaitingDisposition" : "PartialAssemblyAtFinish",
                        snapshotAvailable = true, incomingPresent = true, installing = snapshot.Installing,
                        receivedCount = snapshot.ReceivedCount, fragmentCount = snapshot.FragmentCount, id = manifest.Id, round = manifest.Round,
                        baseRevision = manifest.BaseRevision, revision = manifest.Revision, hash = manifest.Hash, length = manifest.Length, full = manifest.Full });
                }
                catch (Exception)
                {
                    report.snapshotErrors++;
                    report.endOpen.Add(new TerrainTransferEndOpenSample { identity = TerrainIdentity(match), reason = "ProviderSnapshotFailed" });
                }
            }
            foreach (var lifecycle in _terrainTransfers.Values)
            {
                if (!lifecycle.open) continue;
                report.openTransfers++;
                bool included = false;
                foreach (var row in report.endOpen) if (row.key == lifecycle.key) { included = true; break; }
                if (!included) report.endOpen.Add(new TerrainTransferEndOpenSample { identity = lifecycle.identity, key = lifecycle.key,
                    reason = "RegistryAwaitingDisposition:" + lifecycle.lastPhase, receivedCount = lifecycle.receivedCount, fragmentCount = lifecycle.fragmentCount,
                    id = lifecycle.id, round = lifecycle.round, baseRevision = lifecycle.baseRevision, revision = lifecycle.revision,
                    hash = lifecycle.hash, length = lifecycle.length, full = lifecycle.full });
            }
        }
        void FinishTerrainDependencyCollection()
        {
            if (_terrainDependencies == null || _terrainDependencyFinalized) return;
            _terrainDependencyFinalized = true;
            StopTerrainDependencyCollection();
            try
            {
                var report = _terrainDependencies; report.role = _report.role; report.finishStamp = Stopwatch.GetTimestamp();
                report.sourceReconcileComplete = _report.reconcileEventsComplete && _report.reconcileEventsIncluded;
                report.sourceProbeComplete = _report.complete; report.sourceReconcileErrors = _report.reconcileConsumerErrors;
                report.clientScopeApplicable = report.role == "client";
                report.providerErrors = Math.Max(0, PrototypePlayer.ReplayDependencyProviderErrors - _terrainDependencyProviderBaseline) +
                    Math.Max(0, PrototypeMatch.TerrainTransferProviderErrors - _terrainTransferProviderBaseline) +
                    Math.Max(0, BlockTerrain.RemoteInstallProviderErrors - _terrainInstallProviderBaseline);
                report.openInstalls = _terrainOpenInstalls.Count;
                InspectTerrainTransfersAtFinish();
                report.unconsumedSourceAppends = _terrainSourceAppends.Count;
                report.captureComplete = report.clientScopeApplicable && report.sourceProbeComplete && report.sourceReconcileComplete && report.sourceReconcileErrors == 0 &&
                    report.consumerErrors == 0 && report.providerErrors == 0 && report.droppedRows == 0 && !report.readSetTruncated &&
                    report.openInstalls == 0 && report.openTransfers == 0 && report.endOpen.Count == 0 && report.unknownLifecycleRows == 0 &&
                    report.snapshotErrors == 0 && report.unconsumedSourceAppends == 0 && report.unavailableAssociations == 0 && report.unavailableIdentityRows == 0;
                string path = _output + ".terrain-dependencies.json"; Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".writing"; File.WriteAllText(temporary, JsonUtility.ToJson(report, false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                TerrainDependenciesWritten = true;
            }
            catch (Exception error) { TerrainDependenciesWriteError = error.GetType().Name + ": " + error.Message; }
        }
        void StopTerrainDependencyCollection()
        {
            if (!_terrainDependencySubscribed) return;
            PrototypePlayer.ReplayDependencyObserved -= OnReplayDependencyObserved;
            PrototypeMatch.TerrainTransferObserved -= OnTerrainTransferObserved;
            BlockTerrain.RemoteInstallObserved -= OnRemoteInstallObserved;
            PrototypePlayer.ReconcileObserved -= OnTerrainDependencyReconcile;
            _terrainDependencySubscribed = false;
        }
    }
}
#endif
