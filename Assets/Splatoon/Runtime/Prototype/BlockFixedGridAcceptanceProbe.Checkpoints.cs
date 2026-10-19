#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Separate from the raw report. A checkpoint authorizes stage progress only;
    // the exact terminal raw, writer receipt and normal exit remain mandatory.
    public sealed partial class BlockFixedGridAcceptanceProbe
    {
        [Serializable] public sealed class CheckpointFence
        {
            public int frameRecords, playingFrames, cpuRawValues, cpuRecorderStates, cpuSampleCounts,
                paidEvents, actualEvents, collisions, blasts, routeSamples, lastFrameId;
            public long eventSequence, paidCallbacks, actualCallbacks;
            public bool pendingPlayingFrame;
        }

        [Serializable] public sealed class CheckpointStamp
        {
            public long worldSerial, storageSerial, surfaceSerial;
            public uint epoch, round, revision, surfaceRevision, storageRound, storageRevision, surfaceRound;
            public ulong nextId;
            public bool frozen;
            public int faceCount;
            public float dimensionsX, dimensionsY, bottom;
        }

        [Serializable] public sealed class StateCheckpoint
        {
            public int checkpointSchema = 1, checkpointEvidenceSchema = 1, probeRevision = 68;
            public int schema = 4, evidenceSchema = 4, weaponSimulation = GameplayContentSignature.WeaponSimulationVersion,
                blockProtocol = GameplayContentSignature.BlockProtocolVersion, fixedProtocol = BlockFixedGridCodec.ProtocolVersion,
                simulationVersion = BlockFixedGridWorld.SimulationVersion;
            public string kind = "fixed-world-observation", session, label, mode, role, stage, producerId,
                sourceManifestSha256, buildId;
            public long observationSequence;
            public int connectionGenerations, errors;
            public bool initialSyncComplete, equipmentReady, completedNormalRound, lifecycleValidated;
            public double elapsedServerSeconds, configuredSeconds;
            public StateSample current;
            public CheckpointStamp lifecycle;
            public CheckpointFence rawFence;
        }

        [Serializable] public sealed class IssuedCheckpoint
        {
            public long observationSequence;
            public string payloadSha256, payloadJson, producerId, stage;
            public int command, generation;
        }

        // Own exact references, rather than identity hash codes. Local serials
        // describe replacement only and are never compared across processes.
        public sealed class CheckpointLifecycle
        {
            BlockFixedGridWorld _world;
            BlockWorld _storage;
            BlockSurfaceWorld _surface;
            long _worldSerial, _storageSerial, _surfaceSerial;

            public sealed class Ticket
            {
                internal object Match, Terrain;
                internal BlockFixedGridWorld World;
                internal BlockWorld Storage;
                internal BlockSurfaceWorld Surface;
                internal int Command, Generation;
                internal string Stage;
                public CheckpointStamp Stamp;
            }

            public Ticket Begin(object match, object terrain, BlockFixedGridWorld world, int command, int generation, string stage)
            {
                if (world == null) throw new ArgumentNullException(nameof(world));
                if (!ReferenceEquals(_world, world)) { _world = world; _worldSerial = checked(_worldSerial + 1); }
                if (!ReferenceEquals(_storage, world.Storage)) { _storage = world.Storage; _storageSerial = checked(_storageSerial + 1); }
                if (!ReferenceEquals(_surface, world.Surfaces)) { _surface = world.Surfaces; _surfaceSerial = checked(_surfaceSerial + 1); }
                return new Ticket { Match = match, Terrain = terrain, World = world, Storage = world.Storage, Surface = world.Surfaces,
                    Command = command, Generation = generation, Stage = stage, Stamp = Read(world, _worldSerial, _storageSerial, _surfaceSerial) };
            }

            static CheckpointStamp Read(BlockFixedGridWorld world, long worldSerial, long storageSerial, long surfaceSerial)
                => new() { worldSerial = worldSerial, storageSerial = storageSerial, surfaceSerial = surfaceSerial,
                    epoch = world.Epoch, round = world.Round, revision = world.Revision, surfaceRevision = world.Surfaces.Revision,
                    nextId = world.NextId, frozen = world.Frozen, storageRound = world.Storage.Round, storageRevision = world.Storage.Revision,
                    surfaceRound = world.Surfaces.Round, faceCount = world.Surfaces.FaceCount,
                    dimensionsX = world.Storage.Dimensions.x, dimensionsY = world.Storage.Dimensions.y, bottom = world.Storage.Bottom };

            public static bool Matches(Ticket ticket, object match, object terrain, BlockFixedGridWorld world,
                int command, int generation, string stage)
            {
                if (ticket == null || world == null || !ReferenceEquals(ticket.Match, match) || !ReferenceEquals(ticket.Terrain, terrain) ||
                    !ReferenceEquals(ticket.World, world) || !ReferenceEquals(ticket.Storage, world.Storage) || !ReferenceEquals(ticket.Surface, world.Surfaces) ||
                    ticket.Command != command || ticket.Generation != generation || ticket.Stage != stage) return false;
                var old = ticket.Stamp;
                return old.epoch == world.Epoch && old.round == world.Round && old.revision == world.Revision &&
                    old.surfaceRevision == world.Surfaces.Revision && old.nextId == world.NextId && old.frozen == world.Frozen &&
                    old.storageRound == world.Storage.Round && old.storageRevision == world.Storage.Revision &&
                    old.surfaceRound == world.Surfaces.Round && old.faceCount == world.Surfaces.FaceCount &&
                    old.dimensionsX == world.Storage.Dimensions.x && old.dimensionsY == world.Storage.Dimensions.y && old.bottom == world.Storage.Bottom;
            }
        }

        public sealed class CheckpointWorldCapture
        {
            public BlockFixedGridSnapshot Snapshot;
            public string WorldHash, CellHash, PaintHash;
            public static CheckpointWorldCapture Capture(BlockFixedGridWorld world)
            {
                // Same complete bytes and canonical cell order as Capture(match).
                // No world hash or earlier observation is reused.
                var snapshot = world.Capture(true);
                byte[] bytes = BlockFixedGridCodec.Encode(snapshot);
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    foreach (var state in snapshot.Upserts)
                    {
                        writer.Write(state.Handle.Id); writer.Write(state.Handle.Generation); writer.Write(state.Owner);
                        writer.Write(state.Cell.x); writer.Write(state.Cell.y); writer.Write(state.Cell.z);
                        writer.Write(state.Support.Id); writer.Write(state.Support.BaseCellY); writer.Write(state.Support.Height);
                        writer.Write(state.Support.SurfaceKey.FaceId); writer.Write(state.Support.SurfaceKey.U); writer.Write(state.Support.SurfaceKey.V);
                    }
                return new CheckpointWorldCapture { Snapshot = snapshot, WorldHash = Digest(bytes),
                    CellHash = Digest(stream.ToArray()), PaintHash = Digest(snapshot.SurfaceBytes) };
            }
        }

        readonly CheckpointLifecycle _stateLifecycle = new();
        BlockProbeReportWriter _stateCheckpointWriter;
        bool _stateCheckpointsEnabled, _stateWriterClosed;
        string _stateProducerId;
        long _stateObservationSequence, _stateWriteFailures;

        void InitializeStateCheckpoints()
        {
            _report.probeRevision = 68;
            _report.sourceManifestSha256 = Arg("-fixedProbeManifestSha256", "");
            _report.buildId = Arg("-fixedProbeBuildId", "");
            _stateCheckpointsEnabled = _report.mode == "natural" && Arg("-fixedProbeCheckpoint", "0") == "1";
            _report.checkpointEvidenceSchema = _stateCheckpointsEnabled ? 1 : 0;
            if (!_stateCheckpointsEnabled) return;
            if (_report.sourceManifestSha256.Length != 64 || !_report.sourceManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                _report.buildId.Length != 64 || !_report.buildId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidDataException("Natural checkpoint68 needs frozen source-manifest and build-record byte SHA256 identities.");
            _stateProducerId = Guid.NewGuid().ToString("N");
            _stateCheckpointWriter = new BlockProbeReportWriter(_output + ".state.json");
        }

        CheckpointFence ReadCheckpointFence() => new()
        {
            frameRecords = _report.frameRecords.Count, playingFrames = _report.frameEvidence.playingFrames,
            cpuRawValues = _report.cpuRawValues.Count, cpuRecorderStates = _report.cpuRecorderStates.Count,
            cpuSampleCounts = _report.cpuSampleCounts.Count, paidEvents = _report.paidEvents.Count, actualEvents = _report.actualEvents.Count,
            collisions = _report.collisions.Count, blasts = _report.blasts.Count, routeSamples = _report.routeSamples.Count,
            lastFrameId = _report.frameEvidence.lastFrameId, eventSequence = _rawEventSequence,
            paidCallbacks = _report.frameEvidence.observedPaidCallbacks, actualCallbacks = _report.frameEvidence.observedActualCallbacks,
            pendingPlayingFrame = _hasPendingFrame && _pendingFrame.playing
        };

        void PollStateCheckpointWriter()
        {
            if (_stateCheckpointWriter == null) return;
            var stats = _stateCheckpointWriter.ReadStatistics();
            if (stats.failedWrites > _stateWriteFailures)
            {
                _stateWriteFailures = stats.failedWrites;
                Error("State checkpoint writer failed: " + stats.lastError);
            }
        }

        bool TryPublishStateCheckpoint(PrototypeMatch match)
        {
            if (!_stateCheckpointsEnabled || _finished || _stateWriterClosed) return false;
            var round = match.State.Value;
            if (round.Phase != MatchPhase.Finished) return false;
            // The Host can remain alive while clients serialize terminal raw.
            // Do not restart full raw publication in quit-clients or teardown.
            if (_stage is not ("combat" or "drain")) return true;
            // An active publisher applies backpressure before any complete world
            // traversal. Raw frame/event collection continues at its normal rate.
            if (!_stateCheckpointWriter.WaitForIdle(0)) return true;
            using var marker = CaptureCpu.Auto();
            var terrain = match.Blocks; var world = terrain.FixedBlocks;
            var ticket = _stateLifecycle.Begin(match, terrain, world, _command, _report.connectionGenerations, _stage);
            var evidence = CheckpointWorldCapture.Capture(world);
            var admissionCache = terrain.CaptureFixedAdmissionMeshQueryCacheStatistics();
            var sample = new StateSample { stage = _stage, phase = round.Phase.ToString(), command = _command, generation = _report.connectionGenerations,
                players = round.PlayerCount, epoch = world.Epoch, round = world.Round, revision = world.Revision, surfaceRevision = world.Surfaces.Revision,
                requiredRevision = round.FixedBlockRevision, captureTick = match.FixedGridCaptureTick, fixedBlocks = world.Count,
                paintCells = world.Surfaces.PinkCount + world.Surfaces.BlueCount, pink = terrain.PinkCount, blue = terrain.BlueCount,
                scorePink = round.PinkBlockCount, scoreBlue = round.BlueBlockCount, pending = terrain.PendingTransactions, failures = terrain.FailedTransactions,
                projectiles = match.IsServer ? match.Projectiles.PendingCount : 0, ready = match.InitialSyncComplete && match.FixedGridBlockSyncReady,
                frozen = world.Frozen, worldHash = evidence.WorldHash, cellHash = evidence.CellHash, paintHash = evidence.PaintHash,
                admissionMeshCacheQueries = admissionCache.Queries, admissionMeshCacheHits = admissionCache.Hits,
                admissionMeshCacheMisses = admissionCache.Misses, admissionMeshCacheCapacityClears = admissionCache.CapacityClears,
                admissionMeshCachePublishedProofs = admissionCache.Proofs, admissionMeshCacheEntries = admissionCache.Entries,
                admissionMeshCacheSumOfProofPeakEntries = admissionCache.SumOfProofPeakEntries,
                serverTime = match.NetworkManager.ServerTime.Time, sampledAt = Time.realtimeSinceStartupAsDouble,
                sync = match.InspectFixedGridSyncStatistics(), peers = match.InspectFixedGridSyncPeers(), observerFailure = world.LastObserverException,
                legacyBodies = FindObjectsByType<BlockDynamicBody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length };
            if (!CheckpointLifecycle.Matches(ticket, PrototypeMatch.Current, match.Blocks, match.Blocks.FixedBlocks,
                    _command, _report.connectionGenerations, _stage) || evidence.Snapshot.Epoch != sample.epoch || evidence.Snapshot.Round != sample.round ||
                evidence.Snapshot.Revision != sample.revision || evidence.Snapshot.SurfaceRevision != sample.surfaceRevision || evidence.Snapshot.NextId != world.NextId ||
                evidence.Snapshot.Frozen != sample.frozen || evidence.Snapshot.Upserts.Length != sample.fixedBlocks)
            { Error("State checkpoint capture changed lifecycle before publication."); return true; }
            var fence = ReadCheckpointFence();
            if (fence.pendingPlayingFrame) { Error("State checkpoint has an uncommitted Playing frame."); return true; }
            var payload = new StateCheckpoint { session = _report.session, label = _report.label, mode = _report.mode, role = _report.role,
                stage = _stage, producerId = _stateProducerId, sourceManifestSha256 = _report.sourceManifestSha256, buildId = _report.buildId,
                observationSequence = checked(_stateObservationSequence + 1), connectionGenerations = _report.connectionGenerations, errors = _report.errors,
                initialSyncComplete = match.InitialSyncComplete, equipmentReady = _report.equipmentReady, completedNormalRound = _report.completedNormalRound,
                elapsedServerSeconds = _report.elapsedServerSeconds, configuredSeconds = GameplayConfig.Mode.MatchSeconds, current = sample,
                lifecycle = ticket.Stamp, lifecycleValidated = true, rawFence = fence };
            // The small DTO is detached by immediate serialization on its owner;
            // neither this immutable string nor its ledger row aliases raw lists.
            string json; using (SerializeCpu.Auto()) json = JsonUtility.ToJson(payload, false);
            string hash = Digest(Encoding.UTF8.GetBytes(json));
            var issued = new IssuedCheckpoint { observationSequence = payload.observationSequence, payloadSha256 = hash, payloadJson = json,
                producerId = _stateProducerId, stage = _stage, command = _command, generation = _report.connectionGenerations,
            };
            // Reserve before Submit so allocation failure cannot publish an
            // unledgered observation. The list mutation cannot allocate afterward.
            int requiredCapacity = checked(_report.issuedCheckpoints.Count + 1);
            if (_report.issuedCheckpoints.Capacity < requiredCapacity)
                _report.issuedCheckpoints.Capacity = Math.Max(requiredCapacity,
                    checked(Math.Max(4, _report.issuedCheckpoints.Capacity * 2)));
            long sequence = _stateCheckpointWriter.SubmitProgress(json);
            if (sequence <= 0) throw new InvalidOperationException("State writer rejected a checkpoint after admission.");
            _stateObservationSequence = payload.observationSequence; _report.issuedCheckpoints.Add(issued);
            return true;
        }

        bool FinishStateCheckpointWriter()
        {
            if (_stateCheckpointWriter == null) return true;
            PollStateCheckpointWriter();
            if (_stateWriterClosed) return true;
            // Close(0) is not a polling API. Only call once the writer is idle,
            // then close synchronously without joining an active IO operation.
            if (!_stateCheckpointWriter.WaitForIdle(0)) return false;
            bool closed = _stateCheckpointWriter.Close(0);
            _stateWriterClosed = true;
            if (!closed) Error("State checkpoint writer could not close cleanly.");
            return true;
        }
    }
}
#endif
