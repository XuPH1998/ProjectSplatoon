using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Linq;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
#endif

namespace Splatoon.Prototype
{
    public sealed partial class BlockNetworkProbe
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        const int FallFixtureCells = 32;
        const float FallFixtureRadius = .3f;
        const int FallFixtureHeadroomCells = 1;
        const float FallFixtureMinimumClearance = 1f;
        const double DrainQuietSeconds = 1;

        [Serializable] public sealed class DrainInputSample
        {
            public ulong owner;
            public uint life, acknowledgedInput;
        }
        [Serializable] sealed class FallCommandSample
        {
            public string id;
            public ulong owner;
            public byte team;
            public uint round, baselineRevision, completedRevision, completedWorldRevision;
            public Vector3 lane, point;
            public Vector3Int firstCell;
            public float radius;
            public string placementRule = "world-top-reserved-32-v1";
            public float worldTop, worldBottom, cellSize, minimumFallClearance, supportHeight;
            public int topCellExclusive, seedFirstY, reservedLayers, headroomCells;
            public bool worldBoundsClear, staticVolumeClear;
            public int requested, callbackCount, added, removedEnemy, removedFriendly;
            public long queuedOrdinal;
            public double queuedRealtime, queuedServerTime, completedRealtime, completedServerTime;
            public double queuedHeadAgeMs, completedHeadAgeMs;
            public int queuedPending, completedPending;
            public bool baselineEmpty;
        }
        [Serializable] sealed class FallCellSample
        {
            public ulong runId;
            public Vector3Int originCell;
            public byte owner;
            public Vector3 liveCenter;
            public float offset;
        }
        [Serializable] sealed class FallPublishSample
        {
            public string fixtureId;
            public ulong owner;
            public byte team;
            public uint round, revision;
            public double serverTime, terrainTime, realtime, headAgeMs;
            public int liveCount, newlyWitnessedCount, pending;
            public List<FallCellSample> cells = new();
        }
        [Serializable] sealed class FallCoverageReport
        {
            public int schema = 2, command, expectedPlayers, errors;
            public string scope = "one finite 32-cell world-top-bounded build per participant after an intact quiet drain; actual published new run cells, unchanged canonical finish and exact peer checkpoints";
            public bool drainAccepted, allCommandsCompleted, allCellsWitnessed, finishEligible, finishRequested;
            public uint round, baselineRevision, finishRequestRevision;
            public ulong baselineNextRunId;
            public int baselinePink, baselineBlue, baselineTotal, activeFixtureFallingCells;
            public double drainQuietSeconds, requestedRealtime, finishRequestRealtime;
            public long baselineQueued, baselineCompleted;
            public TerrainSample drainState;
            public List<DrainInputSample> drainInputs = new();
            public List<FallCommandSample> commands = new();
            public List<FallPublishSample> publications = new();
        }

        BlockTerrain _fallCoverageTerrain;
        readonly Dictionary<string, HashSet<Vector3Int>> _fallWitnessedCells = new();
        double _drainQuietAt = -1;
        long _drainQuietQueued = -1;
        bool _fallFixtureQueued;

        static List<DrainInputSample> CaptureDrainInputs(PrototypeMatch match)
            => match.Players.Where(p => p != null && p.IsSpawned).Select(p => new DrainInputSample
            { owner = p.PlayerId, life = p.Snapshot.Value.Revision, acknowledgedInput = p.Snapshot.Value.AcknowledgedInput }).ToList();

        static bool DrainIsEmpty(PrototypeMatch match) => match != null && match.IsSpawned && match.IsServer
            && match.State.Value.Phase == MatchPhase.Playing && match.Blocks != null && match.Blocks.Initialized
            && match.Blocks.PendingTransactions == 0 && match.Blocks.UnstableColumns == 0
            && match.Blocks.FallingCount == 0 && match.Blocks.CommandsQueued == match.Blocks.CommandsCompleted
            && match.Projectiles.PendingCount == 0 && !match.SubWeapons.Entities.Any(e => !e.Removed)
            && !match.SpecialWeapons.Entities.Any(e => !e.Removed);

        void ObserveDrain(PrototypeMatch match, double now)
        {
            if (!DrainIsEmpty(match) || _drainQuietQueued != match.Blocks.CommandsQueued)
            {
                _drainQuietAt = DrainIsEmpty(match) ? now : -1;
                _drainQuietQueued = match.Blocks.CommandsQueued;
            }
            else if (_drainQuietAt < 0) _drainQuietAt = now;
        }

        void PrepareFallStageChange(PrototypeMatch match, string nextStage, int command)
        {
            if (nextStage == "drain")
            {
                _drainQuietAt = -1; _drainQuietQueued = -1;
                _report.drainInputObserved = false; _report.drainInputSequence = 0;
                _report.drainInputLife = 0; _report.drainInputOwner = 0;
            }
            if (nextStage != "fall" || match == null || !match.IsServer) return;
            StopFallCoverageCollection();
            _fallFixtureQueued = false; _fallWitnessedCells.Clear();
            bool ready = _stage == "drain" && DrainIsEmpty(match) && _drainQuietAt >= 0
                && Time.realtimeSinceStartupAsDouble - _drainQuietAt >= DrainQuietSeconds;
            // Capture the actual old stage immediately, without trusting the last half-second report.
            Capture(match);
            var terrain = match.Blocks;
            _report.fallCoverage = new FallCoverageReport
            {
                command = command, expectedPlayers = _report.configuredPlayers,
                round = terrain.Round, baselineRevision = terrain.Revision,
                baselineNextRunId = terrain.World.NextRunId,
                baselinePink = terrain.PinkCount, baselineBlue = terrain.BlueCount,
                baselineTotal = terrain.PinkCount + terrain.BlueCount,
                baselineQueued = terrain.CommandsQueued, baselineCompleted = terrain.CommandsCompleted,
                requestedRealtime = Time.realtimeSinceStartupAsDouble,
                drainQuietSeconds = _drainQuietAt < 0 ? 0 : Time.realtimeSinceStartupAsDouble - _drainQuietAt,
                drainAccepted = ready, drainState = _report.current, drainInputs = CaptureDrainInputs(match)
            };
            if (!ready) { FallCoverageFailure("Fall command arrived before a clean, unchanged one-second drain"); return; }
            _fallCoverageTerrain = terrain;
            terrain.PublishedCommit += OnFallCoveragePublished;
        }

        void RecordDrainInput(PrototypePlayer player, PlayerInputFrame input)
        {
            if (_stage != "drain" || _report.drainInputObserved && _report.drainInputLife == input.Revision
                && _report.drainInputOwner == player.PlayerId) return;
            _report.drainInputObserved = true; _report.drainInputSequence = input.Sequence;
            _report.drainInputOwner = player.PlayerId; _report.drainInputLife = input.Revision;
        }

        static bool InFixtureVolume(FallCommandSample fixture, Vector3Int cell)
        {
            int dx = cell.x - fixture.firstCell.x, dz = cell.z - fixture.firstCell.z;
            return cell.y >= fixture.firstCell.y && cell.y < fixture.firstCell.y + fixture.requested
                && (dx * dx + dz * dz) * BlockWorld.CellSize * BlockWorld.CellSize <= fixture.radius * fixture.radius + .00001f;
        }

        static bool FixtureVolumeEmpty(BlockWorld world, FallCommandSample fixture)
        {
            int extent = Mathf.CeilToInt(fixture.radius / BlockWorld.CellSize);
            for (int y = 0; y < fixture.requested; y++)
                for (int x = -extent; x <= extent; x++)
                    for (int z = -extent; z <= extent; z++)
                    {
                        var cell = fixture.firstCell + new Vector3Int(x, y, z);
                        if (InFixtureVolume(fixture, cell) && world.TryGet(cell, out _)) return false;
                    }
            return true;
        }

        static bool FixtureSiteClear(BlockWorld world, FallCommandSample fixture)
        {
            int extent = Mathf.CeilToInt(fixture.radius / BlockWorld.CellSize);
            fixture.worldBoundsClear = true;
            for (int y = 0; y < fixture.requested; y++)
                for (int x = -extent; x <= extent; x++)
                    for (int z = -extent; z <= extent; z++)
                    {
                        var cell = fixture.firstCell + new Vector3Int(x, y, z);
                        if (InFixtureVolume(fixture, cell) && !world.InBounds(cell)) fixture.worldBoundsClear = false;
                    }
            float seedBottom = fixture.firstCell.y * BlockWorld.CellSize;
            int staticMask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer);
            Vector3 center = BlockWorld.Center(fixture.firstCell) + Vector3.up * ((fixture.requested - 1) * BlockWorld.CellSize * .5f);
            Vector3 half = new((extent + .5f) * BlockWorld.CellSize, fixture.requested * BlockWorld.CellSize * .5f,
                (extent + .5f) * BlockWorld.CellSize);
            fixture.staticVolumeClear = !Physics.CheckBox(center, half, Quaternion.identity, staticMask, QueryTriggerInteraction.Ignore);
            fixture.supportHeight = world.Bottom;
            for (int x = -extent; x <= extent; x++)
                for (int z = -extent; z <= extent; z++)
                {
                    var column = fixture.firstCell + new Vector3Int(x, 0, z);
                    if (!InFixtureVolume(fixture, column)) continue;
                    Vector3 origin = BlockWorld.Center(column); origin.y = seedBottom;
                    if (Physics.Raycast(origin, Vector3.down, out var hit, Mathf.Max(0, seedBottom - world.Bottom) + BlockWorld.CellSize,
                        staticMask, QueryTriggerInteraction.Ignore)) fixture.supportHeight = Mathf.Max(fixture.supportHeight, hit.point.y);
                    for (int y = Mathf.FloorToInt(world.Bottom / BlockWorld.CellSize); y < fixture.firstCell.y; y++)
                        if (world.TryGet(new Vector3Int(column.x, y, column.z), out _))
                            fixture.supportHeight = Mathf.Max(fixture.supportHeight, (y + 1) * BlockWorld.CellSize);
                }
            return fixture.worldBoundsClear && fixture.staticVolumeClear
                && seedBottom - Mathf.Max(fixture.lane.y, fixture.supportHeight) >= fixture.minimumFallClearance - .0001f;
        }

        void QueueFiniteFallFixture(PrototypeMatch match, double now)
        {
            var coverage = _report.fallCoverage;
            if (coverage != null && coverage.finishEligible && !coverage.finishRequested && coverage.errors == 0)
            {
                var terrain = match.Blocks;
                if (terrain.Round != coverage.round || terrain.Revision != coverage.finishRequestRevision
                    || terrain.FallingCount <= 0 || terrain.CommandsQueued != terrain.CommandsCompleted
                    || match.State.Value.Phase != MatchPhase.Playing)
                { FallCoverageFailure("The published live fall proof was no longer current before the canonical timer request"); return; }
                // This runs in Probe.Update, after FixedUpdate's local MatchState copy.
                // Writing inside PublishedCommit could otherwise be overwritten by that old copy.
                coverage.finishRequested = true; coverage.finishRequestRealtime = Time.realtimeSinceStartupAsDouble;
                var state = match.State.Value; state.EndsAt = match.NetworkManager.ServerTime.Time - 1; match.State.Value = state;
            }
            if (_fallFixtureQueued || coverage == null || !coverage.drainAccepted || coverage.errors != 0) return;
            _fallFixtureQueued = true;
            var players = match.Players.Where(p => p != null && p.IsSpawned).OrderBy(p => p.PlayerId).ToArray();
            if (players.Length != coverage.expectedPlayers || players.Any(p => !_lanes.ContainsKey(p.PlayerId)))
            { FallCoverageFailure("Finite fall fixture lacks all configured, seeded participants"); return; }
            float worldTop = BlockWorld.Top, cellSize = BlockWorld.CellSize;
            int topCellExclusive = Mathf.FloorToInt(worldTop / cellSize);
            int seedFirstY = topCellExclusive - FallFixtureCells - FallFixtureHeadroomCells;
            // Capture every intended site before submitting any command. No repeating pulse or ordinary fire is enabled here.
            foreach (var player in players)
            {
                var point = _lanes[player.PlayerId] + Vector3.right * 2;
                // Reserve even the worst-case full 32 candidate layers below the
                // real top. A quarter-cell point avoids a CellAt boundary tie.
                point.y = (seedFirstY + .25f) * cellSize;
                var fixture = new FallCommandSample
                {
                    id = coverage.round + ":" + coverage.command + ":" + player.PlayerId,
                    owner = player.PlayerId, team = player.Snapshot.Value.Team, round = coverage.round,
                    baselineRevision = coverage.baselineRevision, lane = _lanes[player.PlayerId], point = point, radius = FallFixtureRadius,
                    firstCell = BlockWorld.CellAt(point + Vector3.up * (BlockWorld.CellSize * .51f)), requested = FallFixtureCells,
                    worldTop = worldTop, worldBottom = match.Blocks.World.Bottom, cellSize = cellSize,
                    topCellExclusive = topCellExclusive, seedFirstY = seedFirstY, reservedLayers = FallFixtureCells,
                    headroomCells = FallFixtureHeadroomCells, minimumFallClearance = FallFixtureMinimumClearance
                };
                fixture.baselineEmpty = FixtureVolumeEmpty(match.Blocks.World, fixture);
                coverage.commands.Add(fixture); _fallWitnessedCells.Add(fixture.id, new HashSet<Vector3Int>());
                if (!FixtureSiteClear(match.Blocks.World, fixture))
                { FallCoverageFailure("World-top falling fixture lacks in-bounds static clearance or one metre above its lane/support: " + fixture.id); return; }
            }
            if (coverage.commands.Any(c => !c.baselineEmpty))
            { FallCoverageFailure("A requested world-top falling site is occupied by pre-existing stable cells"); return; }
            for (int a = 0; a < coverage.commands.Count; a++)
                for (int b = a + 1; b < coverage.commands.Count; b++)
                {
                    var first = coverage.commands[a]; var second = coverage.commands[b];
                    var delta = first.firstCell - second.firstCell;
                    float horizontal = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z) * BlockWorld.CellSize;
                    if (horizontal <= first.radius + second.radius)
                    { FallCoverageFailure("Two world-top falling fixture footprints overlap; provenance would be ambiguous"); return; }
                }
            foreach (var fixture in coverage.commands)
            {
                var terrain = match.Blocks;
                fixture.queuedOrdinal = terrain.CommandsQueued + 1;
                fixture.queuedRealtime = now; fixture.queuedServerTime = match.NetworkManager.ServerTime.Time;
                fixture.queuedPending = terrain.PendingTransactions; fixture.queuedHeadAgeMs = terrain.InspectCommandHead().ageMs;
                terrain.QueueBuild(fixture.point, Vector3.up, fixture.team, fixture.requested, fixture.radius, false, result =>
                {
                    fixture.callbackCount++; fixture.added = result.Added;
                    fixture.removedEnemy = result.RemovedEnemy; fixture.removedFriendly = result.RemovedFriendly;
                    fixture.completedRevision = result.Revision; fixture.completedWorldRevision = terrain.Revision;
                    fixture.completedRealtime = Time.realtimeSinceStartupAsDouble;
                    fixture.completedServerTime = match.NetworkManager.ServerTime.Time;
                    fixture.completedPending = terrain.PendingTransactions; fixture.completedHeadAgeMs = terrain.InspectCommandHead().ageMs;
                    _report.fixtureAdded += result.Added;
                    if (fixture.callbackCount != 1 || result.Added != fixture.requested || result.RemovedEnemy != 0
                        || result.RemovedFriendly != 0 || result.Revision <= fixture.baselineRevision || terrain.Round != fixture.round)
                        FallCoverageFailure("Finite fall command did not complete exactly once with its full published addition: " + fixture.id);
                });
            }
        }

        void OnFallCoveragePublished(BlockTerrainCommit commit)
        {
            try
            {
                var coverage = _report.fallCoverage; var match = PrototypeMatch.Current; var terrain = _fallCoverageTerrain;
                if (coverage == null || coverage.errors != 0 || coverage.finishRequested || _stage != "fall"
                    || match == null || !match.IsSpawned || !match.IsServer || terrain == null || match.Blocks != terrain
                    || commit.Round != coverage.round || commit.Revision <= coverage.baselineRevision) return;
                coverage.finishEligible = false;
                int active = 0;
                foreach (var fixture in coverage.commands)
                {
                    var row = new FallPublishSample { fixtureId = fixture.id, owner = fixture.owner, team = fixture.team,
                        round = commit.Round, revision = commit.Revision, serverTime = match.NetworkManager.ServerTime.Time,
                        terrainTime = terrain.SimulationTime, realtime = Time.realtimeSinceStartupAsDouble,
                        headAgeMs = terrain.InspectCommandHead().ageMs, pending = terrain.PendingTransactions };
                    var witnessed = _fallWitnessedCells[fixture.id];
                    foreach (var run in terrain.World.Falling.Values)
                    {
                        if (run.Id < coverage.baselineNextRunId) continue;
                        for (int index = 0; index < run.Cells.Length; index++)
                        {
                            var cell = run.Cells[index]; var origin = run.Bottom + Vector3Int.up * index;
                            if (!cell.Occupied || !InFixtureVolume(fixture, origin)) continue;
                            if (cell.Owner != fixture.team)
                            { FallCoverageFailure("Published fixture cell has a different owner: " + fixture.id); return; }
                            float offset = run.Offset(terrain.SimulationTime);
                            row.cells.Add(new FallCellSample { runId = run.Id, originCell = origin, owner = cell.Owner,
                                offset = offset, liveCenter = BlockWorld.Center(origin) + Vector3.down * offset });
                            if (witnessed.Add(origin)) row.newlyWitnessedCount++;
                        }
                    }
                    row.liveCount = row.cells.Count; active += row.liveCount;
                    if (row.liveCount > 0) coverage.publications.Add(row);
                }
                coverage.activeFixtureFallingCells = active;
                coverage.allCommandsCompleted = coverage.commands.Count == coverage.expectedPlayers
                    && coverage.commands.All(c => c.callbackCount == 1 && c.added == c.requested && c.completedRevision > c.baselineRevision);
                coverage.allCellsWitnessed = coverage.commands.Count == coverage.expectedPlayers
                    && coverage.commands.All(c => _fallWitnessedCells[c.id].Count == c.requested);
                if (!coverage.allCommandsCompleted || !coverage.allCellsWitnessed || active <= 0 || terrain.PendingTransactions != 0) return;
                int addedPink = coverage.commands.Where(c => c.team == 1).Sum(c => c.added);
                int addedBlue = coverage.commands.Where(c => c.team == 2).Sum(c => c.added);
                if (terrain.CommandsQueued - coverage.baselineQueued != coverage.expectedPlayers
                    || terrain.CommandsCompleted - coverage.baselineCompleted != coverage.expectedPlayers
                    || terrain.PinkCount != coverage.baselinePink + addedPink || terrain.BlueCount != coverage.baselineBlue + addedBlue)
                { FallCoverageFailure("Finite fall interval contains extra, incomplete or non-conserving mutations"); return; }
                if (match.State.Value.Phase != MatchPhase.Playing)
                { FallCoverageFailure("Actual falling proof did not occur during the live round"); return; }
                // Latch this real world/collider commit. Probe.Update requests the existing timer outside FixedUpdate's state copy.
                // No direct FinishRound call, descriptor mutation or Python polling race is introduced.
                coverage.finishEligible = true; coverage.finishRequestRevision = commit.Revision;
            }
            catch (Exception exception) { FallCoverageFailure("Published fall coverage consumer failed: " + exception); }
        }

        void FallCoverageFailure(string message)
        {
            if (_report.fallCoverage != null) _report.fallCoverage.errors++;
            _report.errors++;
            if (_report.errorMessages.Count < 100) _report.errorMessages.Add(message);
        }

        void StopFallCoverageCollection()
        {
            if (_fallCoverageTerrain != null) _fallCoverageTerrain.PublishedCommit -= OnFallCoveragePublished;
            _fallCoverageTerrain = null;
        }
#endif
    }
}
