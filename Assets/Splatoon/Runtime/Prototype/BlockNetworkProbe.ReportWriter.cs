using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
#endif

namespace Splatoon.Prototype
{
    public sealed partial class BlockNetworkProbe
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Preserve Report's serializer/reflection contract. Only bounded metadata and one
        // immutable row at a time reach JsonUtility; no complete report JSON is allocated.
        [Serializable] sealed class ReportJsonMetadata
        {
            public int schema = 1;
            public int reconcileEventSchema = 1, correctionSampleSchema = 1;
            public int safetyRecoveryEventSchema = 1;
            public int motorBranchEventSchema = 1;
            public bool motorTraceEnabled, motorBranchEventsComplete = true, motorBranchEventsIncluded;
            public int motorBranchConsumerErrors, motorBranchProviderErrors, motorBranchEventCount;
            public string motorTraceScope = "opt-in -blockMotorTrace; immutable motor branches; same-process invocation/time association only; independent of formal correction statistics";
            public string motorBranchProviderLastError;
            public string primaryCorrectionSource = "reconcileEvents: immutable reconciliation observations; metres is the unchanged raw correction";
            public string correctionSampleScope = "legacy Update polling retained for comparison; pending and intermediate/same-ack events may be absent";
            public string reconcileMotionScope = "motion between consecutive reconciliation events for the same player, life, terrain round and connection; first observation has no baseline";
            public string safetyCounterScope = "local terrain authority counters; client zero does not rule out host recovery";
            public bool reconcileEventsComplete = true;
            public bool reconcileEventsIncluded;
            public int reconcileConsumerErrors, reconcileEventCount;
            public bool safetyRecoveryEventsComplete = true, safetyRecoveryEventsIncluded;
            public int safetyRecoveryConsumerErrors, safetyRecoveryEventCount;
            public string reconcilePersistence = "all events retained in memory and included in the final report; live reports publish count and completeness only";
            public string label, role, session, probeScope = "same-machine independent Players; opt-in fixture telemetry; performance diagnostic only";
            public bool complete, connected, initialSyncComplete, reconnectRequested, reconnectDisconnected, reconnectCleanup, reconnectSuccess;
            public int configuredPlayers, connectionGenerations, errors, fixtureAdded, fixtureRemoved, friendlyMovementSamples, movingAckSamples;
            public double duration, firstReadySeconds = -1;
            public long blockBytesSent;
            public bool drainInputObserved;
            public uint drainInputSequence, drainInputLife;
            public ulong drainInputOwner;
            public TerrainSample current;
            public ReportJsonMetadata(Report source)
            {
                schema = source.schema;
                reconcileEventSchema = source.reconcileEventSchema; correctionSampleSchema = source.correctionSampleSchema;
                safetyRecoveryEventSchema = source.safetyRecoveryEventSchema;
                motorBranchEventSchema = source.motorBranchEventSchema;
                motorTraceEnabled = source.motorTraceEnabled; motorBranchEventsComplete = source.motorBranchEventsComplete; motorBranchEventsIncluded = source.motorBranchEventsIncluded;
                motorBranchConsumerErrors = source.motorBranchConsumerErrors; motorBranchProviderErrors = source.motorBranchProviderErrors; motorBranchEventCount = source.motorBranchEventCount;
                motorTraceScope = source.motorTraceScope;
                motorBranchProviderLastError = source.motorBranchProviderLastError;
                primaryCorrectionSource = source.primaryCorrectionSource;
                correctionSampleScope = source.correctionSampleScope;
                reconcileMotionScope = source.reconcileMotionScope;
                safetyCounterScope = source.safetyCounterScope;
                reconcileEventsComplete = source.reconcileEventsComplete;
                reconcileEventsIncluded = source.reconcileEventsIncluded;
                reconcileConsumerErrors = source.reconcileConsumerErrors; reconcileEventCount = source.reconcileEventCount;
                safetyRecoveryEventsComplete = source.safetyRecoveryEventsComplete; safetyRecoveryEventsIncluded = source.safetyRecoveryEventsIncluded;
                safetyRecoveryConsumerErrors = source.safetyRecoveryConsumerErrors; safetyRecoveryEventCount = source.safetyRecoveryEventCount;
                reconcilePersistence = source.reconcilePersistence;
                label = source.label; role = source.role; session = source.session; probeScope = source.probeScope;
                complete = source.complete; connected = source.connected; initialSyncComplete = source.initialSyncComplete; reconnectRequested = source.reconnectRequested; reconnectDisconnected = source.reconnectDisconnected; reconnectCleanup = source.reconnectCleanup; reconnectSuccess = source.reconnectSuccess;
                configuredPlayers = source.configuredPlayers; connectionGenerations = source.connectionGenerations; errors = source.errors; fixtureAdded = source.fixtureAdded; fixtureRemoved = source.fixtureRemoved; friendlyMovementSamples = source.friendlyMovementSamples; movingAckSamples = source.movingAckSamples;
                duration = source.duration; firstReadySeconds = source.firstReadySeconds;
                blockBytesSent = source.blockBytesSent;
                drainInputObserved = source.drainInputObserved;
                drainInputSequence = source.drainInputSequence; drainInputLife = source.drainInputLife;
                drainInputOwner = source.drainInputOwner;
                current = source.current;
            }
        }
        [Serializable] sealed class FallCoverageJsonMetadata
        {
            public int schema = 1, command, expectedPlayers, errors;
            public string scope = "one finite 32-cell 22m build per participant after an intact quiet drain; actual published new run cells, unchanged canonical finish and exact peer checkpoints";
            public bool drainAccepted, allCommandsCompleted, allCellsWitnessed, finishEligible, finishRequested;
            public uint round, baselineRevision, finishRequestRevision;
            public ulong baselineNextRunId;
            public int baselinePink, baselineBlue, baselineTotal, activeFixtureFallingCells;
            public double drainQuietSeconds, requestedRealtime, finishRequestRealtime;
            public long baselineQueued, baselineCompleted;
            public TerrainSample drainState;
            public FallCoverageJsonMetadata(FallCoverageReport source)
            {
                schema = source.schema; command = source.command; expectedPlayers = source.expectedPlayers; errors = source.errors;
                scope = source.scope;
                drainAccepted = source.drainAccepted; allCommandsCompleted = source.allCommandsCompleted; allCellsWitnessed = source.allCellsWitnessed; finishEligible = source.finishEligible; finishRequested = source.finishRequested;
                round = source.round; baselineRevision = source.baselineRevision; finishRequestRevision = source.finishRequestRevision;
                baselineNextRunId = source.baselineNextRunId;
                baselinePink = source.baselinePink; baselineBlue = source.baselineBlue; baselineTotal = source.baselineTotal; activeFixtureFallingCells = source.activeFixtureFallingCells;
                drainQuietSeconds = source.drainQuietSeconds; requestedRealtime = source.requestedRealtime; finishRequestRealtime = source.finishRequestRealtime;
                baselineQueued = source.baselineQueued; baselineCompleted = source.baselineCompleted;
                drainState = source.drainState;
            }
        }
        [Serializable] sealed class ReportJsonStringItem { public List<string> values = new() { null }; }
        [Serializable] sealed class ReportJsonNullFall { public FallCoverageReport fallCoverage; }

        static void WriteReportJson(TextWriter writer, Report report)
        {
            WriteReportObjectPrefix(writer, new ReportJsonMetadata(report));
            if (report.fallCoverage == null)
            {
                // Let Unity retain its exact inline-class null representation.
                string emptyFall = JsonUtility.ToJson(new ReportJsonNullFall());
                if (emptyFall.Length > 2)
                { writer.Write(','); writer.Write(emptyFall.Substring(1, emptyFall.Length - 2)); }
            }
            else
            {
                writer.Write(",\"fallCoverage\":");
                var fall = report.fallCoverage;
                WriteReportObjectPrefix(writer, new FallCoverageJsonMetadata(fall));
                WriteReportObjectArray(writer, "drainInputs", fall.drainInputs);
                WriteReportObjectArray(writer, "commands", fall.commands);
                WriteReportObjectArray(writer, "publications", fall.publications);
                writer.Write('}');
            }
            WriteReportObjectArray(writer, "states", report.states);
            WriteReportObjectArray(writer, "corrections", report.corrections);
            WriteReportObjectArray(writer, "reconcileEvents", report.reconcileEvents);
            WriteReportObjectArray(writer, "safetyRecoveryEvents", report.safetyRecoveryEvents);
            WriteReportObjectArray(writer, "motorBranchEvents", report.motorBranchEvents);
            WriteReportStringArray(writer, "motorBranchErrorMessages", report.motorBranchErrorMessages);
            WriteReportStringArray(writer, "errorMessages", report.errorMessages);
            writer.Write('}');
        }

        static void WriteReportObjectPrefix(TextWriter writer, object metadata)
        {
            string json = JsonUtility.ToJson(metadata);
            writer.Write(json.Substring(0, json.Length - 1));
        }

        static void WriteReportArrayPrefix(TextWriter writer, string name)
        { writer.Write(",\""); writer.Write(name); writer.Write("\":["); }

        static void WriteReportObjectArray<T>(TextWriter writer, string name, List<T> values) where T : class
        {
            WriteReportArrayPrefix(writer, name);
            if (values != null)
                for (int index = 0; index < values.Count; index++)
                {
                    // Probe producers only append complete, non-null captured DTOs.
                    // A malformed population must not replace the last valid report.
                    if (values[index] == null) throw new InvalidOperationException("Null probe row in " + name);
                    if (index != 0) writer.Write(',');
                    writer.Write(JsonUtility.ToJson(values[index]));
                }
            writer.Write(']');
        }

        static void WriteReportStringArray(TextWriter writer, string name, List<string> values)
        {
            WriteReportArrayPrefix(writer, name);
            var item = new ReportJsonStringItem();
            if (values != null)
                for (int index = 0; index < values.Count; index++)
                {
                    if (index != 0) writer.Write(',');
                    item.values[0] = values[index];
                    // Native escaping also preserves quotes, control characters,
                    // backslashes, Unicode and Unity's treatment of null strings.
                    string json = JsonUtility.ToJson(item);
                    const string prefix = "{\"values\":[";
                    if (!json.StartsWith(prefix, StringComparison.Ordinal) || !json.EndsWith("]}", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected probe string wrapper JSON");
                    writer.Write(json.Substring(prefix.Length, json.Length - prefix.Length - 2));
                }
            writer.Write(']');
        }
#endif
    }
}
