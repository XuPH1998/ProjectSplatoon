// Reports-only eval_file candidate. Not compiled or executed by its author.
// Run only after root's tests/build/Players end, against a finished immutable capture.
// Copy the template to extraction-request.json and review absolute paths and caps first.
// This temporarily loads a Profiler dataset; prior data is backed up and restored in finally.
var requestPath = "D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows37CpuDiagnosis02/cpu-extract01.request.json";
var request = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(requestPath));
var inputProfile = System.IO.Path.GetFullPath(request["inputProfile"].ToObject<string>());
var outputDirectory = System.IO.Path.GetFullPath(request["outputDirectory"].ToObject<string>());
var reportsPrefix = System.IO.Path.GetFullPath("D:/XPHUNITY/ProjectSplatoon/Reports/").TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
if (!outputDirectory.StartsWith(reportsPrefix, System.StringComparison.OrdinalIgnoreCase))
    throw new System.InvalidOperationException("Extraction output must be a fresh directory under ProjectSplatoon Reports.");
if (!System.IO.File.Exists(inputProfile)) throw new System.IO.FileNotFoundException("Finished input profile is missing.", inputProfile);
if (System.IO.Directory.Exists(outputDirectory) || System.IO.File.Exists(outputDirectory))
    throw new System.IO.IOException("Preserve the existing extraction; choose a fresh output directory.");
if (UnityEngine.Application.unityVersion != "6000.3.9f1")
    throw new System.InvalidOperationException("Reverify the API candidate before using another Unity version.");
if (!UnityEngine.Application.isEditor || UnityEngine.Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode
    || UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
    throw new System.InvalidOperationException("Offline extraction requires an idle non-Play Editor after tests/build/Players finish.");
if (UnityEditorInternal.ProfilerDriver.enabled)
    throw new System.InvalidOperationException("Stop recording through root's controlled workflow before offline extraction.");
var availableConnections = UnityEditorInternal.ProfilerDriver.GetAvailableProfilers();
if (UnityEditorInternal.ProfilerDriver.connectedProfiler != -1 || availableConnections.Length != 1 || availableConnections[0] != -1)
    throw new System.InvalidOperationException("Offline extraction requires only the local Editor connection after own Players exit.");
int observedNewFrames = 0, observedLoads = 0, observedClears = 0;
int extractingNewFrames = 0, extractingLoads = 0, extractingClears = 0;
string observationPhase = "before_load";
System.Action<int, int> onNewFrame = (connection, frameId) => {
    System.Threading.Interlocked.Increment(ref observedNewFrames);
    if (observationPhase == "extracting") System.Threading.Interlocked.Increment(ref extractingNewFrames);
};
System.Action onLoad = () => {
    System.Threading.Interlocked.Increment(ref observedLoads);
    if (observationPhase == "extracting") System.Threading.Interlocked.Increment(ref extractingLoads);
};
System.Action onClear = () => {
    System.Threading.Interlocked.Increment(ref observedClears);
    if (observationPhase == "extracting") System.Threading.Interlocked.Increment(ref extractingClears);
};
int maxScanned = request["maxScannedFrames"].ToObject<int>();
int maxSeeds = request["maxSeedFrames"].ToObject<int>();
int neighbors = request["neighborFrames"].ToObject<int>();
int maxSelected = request["maxSelectedFrames"].ToObject<int>();
int maxThreads = request["maxThreadsPerFrame"].ToObject<int>();
int maxSamples = request["maxSamplesPerThread"].ToObject<int>();
int maxTotal = request["maxTotalRetainedSamples"].ToObject<int>();
double threshold = request["longFrameThresholdMs"].ToObject<double>();
if (maxScanned < 1 || maxScanned > 100000 || maxSeeds < 1 || maxSeeds > 256 || neighbors < 0 || neighbors > 4
    || maxSelected < 1 || maxSelected > 1024 || maxThreads < 1 || maxThreads > 4096 || maxSamples < 1 || maxSamples > 1000000
    || maxTotal < 1 || maxTotal > 5000000 || double.IsNaN(threshold) || double.IsInfinity(threshold) || threshold <= 0)
    throw new System.ArgumentException("Invalid or unbounded extraction configuration.");
var requestedFrames = new System.Collections.Generic.List<int>();
foreach (var token in (Newtonsoft.Json.Linq.JArray)request["profilerFrameIndices"])
{
    if (token.Type != Newtonsoft.Json.Linq.JTokenType.Integer) throw new System.ArgumentException("Profiler frame indices must be JSON integers.");
    int value = token.ToObject<int>();
    if (value < 0 || requestedFrames.Contains(value)) throw new System.ArgumentException("Profiler frame indices must be nonnegative and distinct.");
    requestedFrames.Add(value);
}
System.Func<string, string> digest = path =>
{
    using (var hash = System.Security.Cryptography.SHA256.Create())
    using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
        return System.BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
};
System.Func<float, int> floatBits = value => System.BitConverter.ToInt32(System.BitConverter.GetBytes(value), 0);
System.Func<double, object> finiteOrNull = value => double.IsNaN(value) || double.IsInfinity(value) ? null : (object)value;
System.Func<string, System.IO.StreamWriter> createWriter = name => new System.IO.StreamWriter(
    new System.IO.FileStream(System.IO.Path.Combine(outputDirectory, name), System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.Read),
    new System.Text.UTF8Encoding(false));
System.Action<System.IO.StreamWriter, object> writeLine = (writer, value) =>
    writer.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.None));
string inputShaBefore = digest(inputProfile);
int originalFirst = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
int originalLast = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
if ((originalFirst < 0) != (originalLast < 0)) throw new System.InvalidOperationException("Unexpected prior Profiler range; preserve it for root inspection.");
if (originalFirst >= 0 && originalFirst > originalLast) throw new System.InvalidOperationException("Prior Profiler frame range is reversed; preserve it for root inspection.");
bool hadOriginalFrames = originalFirst >= 0 && originalLast >= 0;
string backupPath = System.IO.Path.Combine(outputDirectory, "original-profiler-data-backup.raw");
string backupSha = null;
bool loadAttempted = false, loadSucceeded = false, extractionCompleted = false, restorationAttempted = false, restorationSucceeded = false;
bool scanReachedLast = false, scannedFramesCapped = false, selectionCapped = false, anyThreadEnumerationCapped = false, anySamplesCapped = false;
int loadedFirst = -1, loadedLast = -1, restoredFirst = -1, restoredLast = -1, retainedTotal = 0, treeErrorTotal = 0;
string inputShaAfter = null;
System.Exception extractionError = null, outputCloseError = null, restorationError = null;
System.IO.StreamWriter frameWriter = null, threadWriter = null, sampleWriter = null, metadataWriter = null;
var windowMetadataGuid = new System.Guid("b7fa6a6f-1e90-4a32-a3c5-6d68c2bfb954");
int windowMetadataTag = 0x42574301;
int maxMetadataChunksPerFrame = request["maxMetadataChunksPerFrame"].ToObject<int>();
int maxMetadataChunksTotal = request["maxMetadataChunksTotal"].ToObject<int>();
if (maxMetadataChunksPerFrame < 1 || maxMetadataChunksPerFrame > 65536 || maxMetadataChunksTotal < 1 || maxMetadataChunksTotal > 131072)
    throw new System.ArgumentException("Metadata chunk caps are invalid or unbounded.");
long reportedMetadataChunks = 0;
int retainedMetadataChunks = 0, malformedMetadataChunks = 0;
bool metadataChunksCapped = false;
var metadataFrameSummaries = new System.Collections.Generic.List<object>();
var scannedFrames = new System.Collections.Generic.List<int>();
var longFrames = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, float>>();
var selectedFrames = new System.Collections.Generic.HashSet<int>();
var seedFrames = new System.Collections.Generic.List<int>();
System.IO.Directory.CreateDirectory(outputDirectory);
UnityEditorInternal.ProfilerDriver.NewProfilerFrameRecorded += onNewFrame;
UnityEditorInternal.ProfilerDriver.profileLoaded += onLoad;
UnityEditorInternal.ProfilerDriver.profileCleared += onClear;
try
{
    observationPhase = "loading";
    frameWriter = createWriter("frames.jsonl"); threadWriter = createWriter("threads.jsonl"); sampleWriter = createWriter("samples.jsonl"); metadataWriter = createWriter("frame-metadata.jsonl");
    if (hadOriginalFrames)
    {
        if (!UnityEditorInternal.ProfilerDriver.SaveProfile(backupPath) || !System.IO.File.Exists(backupPath) || new System.IO.FileInfo(backupPath).Length == 0)
            throw new System.IO.IOException("Prior Profiler data backup failed; input profile was not loaded.");
        backupSha = digest(backupPath);
    }
    loadAttempted = true;
    loadSucceeded = UnityEditorInternal.ProfilerDriver.LoadProfile(inputProfile, false);
    if (!loadSucceeded) throw new System.IO.InvalidDataException("LoadProfile returned false; do not extract residual history as the new capture.");
    loadedFirst = UnityEditorInternal.ProfilerDriver.firstFrameIndex; loadedLast = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
    if (loadedFirst < 0 || loadedLast < 0 || loadedFirst > loadedLast) throw new System.IO.InvalidDataException("Loaded capture has no valid available frame range.");
    observationPhase = "extracting";
    var visited = new System.Collections.Generic.HashSet<int>();
    int frame = loadedFirst;
    while (true)
    {
        if (!visited.Add(frame)) throw new System.IO.InvalidDataException("Profiler frame traversal cycled before reaching lastFrameIndex.");
        if (scannedFrames.Count == maxScanned) { scannedFramesCapped = true; break; }
        scannedFrames.Add(frame);
        using (var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, 0))
        {
            var row = new System.Collections.Generic.Dictionary<string, object> { { "profileSha256", inputShaBefore }, { "profilerFrameIndex", frame }, { "probeThreadIndex", 0 }, { "valid", view.valid } };
            if (view.valid)
            {
                if (view.frameIndex != frame || view.threadIndex != 0) throw new System.IO.InvalidDataException("Raw view identity differs from the requested frame/thread.");
                // Read aliases only while their owning view lives. Never Dispose the borrowed NativeArray.
                int chunks = view.GetFrameMetaDataCount(windowMetadataGuid, windowMetadataTag);
                if (chunks < 0) throw new System.IO.InvalidDataException("Negative frame metadata chunk count.");
                reportedMetadataChunks += chunks;
                int retainedChunks = System.Math.Min(chunks, System.Math.Min(maxMetadataChunksPerFrame, maxMetadataChunksTotal - retainedMetadataChunks));
                if (retainedChunks < chunks) metadataChunksCapped = true;
                for (int chunkIndex = 0; chunkIndex < retainedChunks; chunkIndex++)
                {
                    var packet = view.GetFrameMetaData<long>(windowMetadataGuid, windowMetadataTag, chunkIndex);
                    int elementCount = packet.Length;
                    var packetBytes = view.GetFrameMetaData<byte>(windowMetadataGuid, windowMetadataTag, chunkIndex);
                    int rawByteCount = packetBytes.Length;
                    bool expectedFourLongs = packet.IsCreated && packetBytes.IsCreated && elementCount == 4 && rawByteCount == 32;
                    var values = new long[expectedFourLongs ? 4 : 0];
                    if (expectedFourLongs) for (int element = 0; element < 4; element++) values[element] = packet[element];
                    else malformedMetadataChunks++;
                    writeLine(metadataWriter, new System.Collections.Generic.Dictionary<string, object> {
                        { "profileSha256", inputShaBefore }, { "profilerFrameIndex", frame }, { "threadIndex", 0 }, { "threadId", view.threadId },
                        { "metadataGuid", windowMetadataGuid.ToString("D") }, { "metadataTag", windowMetadataTag },
                        { "chunkIndex", chunkIndex }, { "reportedChunkCount", chunks }, { "longElementCount", elementCount }, { "rawByteCount", rawByteCount },
                        { "validExpectedFourLongs", expectedFourLongs }, { "payloadLongsExact", values }
                    });
                    retainedMetadataChunks++;
                }
                metadataFrameSummaries.Add(new { profilerFrameIndex = frame, reportedChunks = chunks, retainedChunks = retainedChunks });
                float frameMs = view.frameTimeMs;
                row["threadId"] = view.threadId; row["threadName"] = view.threadName; row["threadGroupName"] = view.threadGroupName;
                row["frameTimeMsFiniteOrNull"] = finiteOrNull(frameMs); row["frameTimeMsBits"] = floatBits(frameMs);
                row["frameTimeNs"] = view.frameTimeNs; row["frameStartTimeNs"] = view.frameStartTimeNs;
                row["frameStartTimeMsBits"] = System.BitConverter.DoubleToInt64Bits(view.frameStartTimeMs);
                row["sampleCount"] = view.sampleCount;
                if (!float.IsNaN(frameMs) && !float.IsInfinity(frameMs) && frameMs > threshold)
                    longFrames.Add(new System.Collections.Generic.KeyValuePair<int, float>(frame, frameMs));
            }
            writeLine(frameWriter, row);
        }
        if (frame == loadedLast) { scanReachedLast = true; break; }
        int next = UnityEditorInternal.ProfilerDriver.GetNextFrameIndex(frame);
        if (next <= frame || next > loadedLast) throw new System.IO.InvalidDataException("Profiler traversal ended, reversed, or exceeded the saved frame range before lastFrameIndex.");
        frame = next;
    }
    if (requestedFrames.Count > 0)
    {
        foreach (int requested in requestedFrames)
        {
            if (!scannedFrames.Contains(requested)) throw new System.ArgumentException("Requested Profiler frame was not in the scanned available sequence: " + requested);
            seedFrames.Add(requested);
        }
    }
    else
    {
        longFrames.Sort((a, b) => { int durationOrder = b.Value.CompareTo(a.Value); return durationOrder != 0 ? durationOrder : a.Key.CompareTo(b.Key); });
        selectionCapped = longFrames.Count > maxSeeds;
        for (int index = 0; index < System.Math.Min(maxSeeds, longFrames.Count); index++) seedFrames.Add(longFrames[index].Key);
    }
    foreach (int seed in seedFrames)
    {
        int position = scannedFrames.IndexOf(seed);
        for (int offset = -neighbors; offset <= neighbors; offset++)
        {
            int neighbor = position + offset;
            if (neighbor >= 0 && neighbor < scannedFrames.Count) selectedFrames.Add(scannedFrames[neighbor]);
        }
    }
    if (selectedFrames.Count > maxSelected) throw new System.ArgumentException("Selected frames plus neighbors exceed maxSelectedFrames; no requested frame was silently removed.");
    foreach (int selected in scannedFrames)
    {
        if (!selectedFrames.Contains(selected)) continue;
        bool sawFirstInvalidThread = false;
        for (int thread = 0; thread < maxThreads; thread++)
        {
            using (var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(selected, thread))
            {
                if (!view.valid)
                {
                    writeLine(threadWriter, new System.Collections.Generic.Dictionary<string, object> { { "profileSha256", inputShaBefore }, { "profilerFrameIndex", selected }, { "threadIndex", thread }, { "valid", false }, { "terminatesThreadEnumeration", true } });
                    sawFirstInvalidThread = true; break;
                }
                if (view.frameIndex != selected || view.threadIndex != thread) throw new System.IO.InvalidDataException("Selected Raw view identity mismatch.");
                int reported = view.sampleCount;
                if (reported < 0) throw new System.IO.InvalidDataException("Negative Raw sample count.");
                int retained = System.Math.Min(reported, System.Math.Min(maxSamples, maxTotal - retainedTotal));
                if (retained < reported) anySamplesCapped = true;
                var spanEnds = new long[retained]; var children = new int[retained]; var seenChildren = new int[retained];
                var openParents = new System.Collections.Generic.List<int>();
                var treeErrors = new System.Collections.Generic.List<string>(); int threadTreeErrors = 0; bool topologyAvailable = true;
                System.Action<string> treeError = message => { threadTreeErrors++; if (treeErrors.Count < 32) treeErrors.Add(message); topologyAvailable = false; };
                for (int sample = 0; sample < retained; sample++)
                {
                    while (topologyAvailable && openParents.Count > 0 && sample > spanEnds[openParents[openParents.Count - 1]])
                    {
                        int closed = openParents[openParents.Count - 1]; openParents.RemoveAt(openParents.Count - 1);
                        if (seenChildren[closed] != children[closed]) treeError("Direct child count mismatch at sample " + closed);
                    }
                    int parent = topologyAvailable && openParents.Count > 0 ? openParents[openParents.Count - 1] : -1;
                    int direct = view.GetSampleChildrenCount(sample), recursive = view.GetSampleChildrenCountRecursive(sample);
                    long end = (long)sample + recursive;
                    if (direct < 0 || recursive < direct || end >= reported || (direct == 0 && recursive != 0)
                        || (topologyAvailable && parent >= 0 && end > spanEnds[parent])) treeError("Invalid subtree span/count at sample " + sample);
                    bool parentAvailable = topologyAvailable;
                    int depth = parentAvailable ? openParents.Count : -1;
                    if (parentAvailable)
                    {
                        if (parent >= 0) seenChildren[parent]++;
                        spanEnds[sample] = end; children[sample] = direct;
                        if (recursive > 0) openParents.Add(sample);
                    }
                    float timeMs = view.GetSampleTimeMs(sample); double startMs = view.GetSampleStartTimeMs(sample);
                    writeLine(sampleWriter, new System.Collections.Generic.Dictionary<string, object> {
                        { "profileSha256", inputShaBefore }, { "profilerFrameIndex", selected }, { "threadIndex", thread }, { "threadId", view.threadId },
                        { "sampleIndex", sample }, { "markerId", view.GetSampleMarkerId(sample) }, { "name", view.GetSampleName(sample) },
                        { "markerFlags", (uint)view.GetSampleFlags(sample) }, { "categoryIndex", view.GetSampleCategoryIndex(sample) },
                        { "directChildrenCount", direct }, { "recursiveChildrenCount", recursive }, { "subtreeEndSampleIndexDerived", end },
                        { "parentIdentityDerivedAvailable", parentAvailable }, { "parentSampleIndexDerived", parentAvailable ? (object)parent : null },
                        { "parentIdentityValidationStatus", parentAvailable ? "pending_final_thread_summary" : "unavailable_after_topology_error" },
                        { "depthDerived", parentAvailable ? (object)depth : null }, { "parentRule", "raw_preorder_recursive_span_with_direct_count_validation" },
                        { "startTimeMsFiniteOrNull", finiteOrNull(startMs) }, { "startTimeMsBits", System.BitConverter.DoubleToInt64Bits(startMs) },
                        { "timeMsFiniteOrNull", finiteOrNull(timeMs) }, { "timeMsBits", floatBits(timeMs) },
                        { "startTimeNs", view.GetSampleStartTimeNs(sample) }, { "timeNs", view.GetSampleTimeNs(sample) }
                    });
                    retainedTotal++;
                }
                bool openSpansFullyRetained = true;
                if (topologyAvailable)
                    foreach (int remaining in openParents)
                    {
                        if (spanEnds[remaining] >= retained) openSpansFullyRetained = false;
                        else if (seenChildren[remaining] != children[remaining]) treeError("Final direct child count mismatch at sample " + remaining);
                    }
                treeErrorTotal += threadTreeErrors;
                writeLine(threadWriter, new System.Collections.Generic.Dictionary<string, object> {
                    { "profileSha256", inputShaBefore }, { "profilerFrameIndex", selected }, { "threadIndex", thread }, { "threadId", view.threadId },
                    { "threadName", view.threadName }, { "threadGroupName", view.threadGroupName }, { "valid", true },
                    { "reportedSampleCount", reported }, { "retainedSampleCount", retained }, { "samplesCapped", retained < reported }, { "maxDepthReported", view.maxDepth },
                    { "treeValidationErrorCount", threadTreeErrors }, { "treeValidationErrors", treeErrors }, { "treeValidationErrorMessagesCapped", treeErrors.Count < threadTreeErrors },
                    { "retainedPrefixValidationHasNoKnownErrors", threadTreeErrors == 0 }, { "parentIdentitiesRequireWholeThreadValidation", true },
                    { "wholeThreadTreeValidationComplete", retained == reported && openSpansFullyRetained && threadTreeErrors == 0 }
                });
            }
        }
        if (!sawFirstInvalidThread) anyThreadEnumerationCapped = true;
    }
    if (UnityEditorInternal.ProfilerDriver.firstFrameIndex != loadedFirst || UnityEditorInternal.ProfilerDriver.lastFrameIndex != loadedLast
        || UnityEditorInternal.ProfilerDriver.enabled || UnityEditorInternal.ProfilerDriver.connectedProfiler != -1
        || extractingNewFrames != 0 || extractingLoads != 0 || extractingClears != 0)
        throw new System.InvalidOperationException("Profiler dataset or recording/connection changed during extraction; preserve raw observations as unavailable.");
    inputShaAfter = digest(inputProfile);
    if (inputShaBefore != inputShaAfter) throw new System.IO.IOException("Input capture changed during offline extraction; output cannot be treated as one immutable profile.");
    extractionCompleted = true;
}
catch (System.Exception exception) { extractionError = exception; }
finally
{
    observationPhase = "restoring";
    // A stream flush/close failure must not skip Profiler restoration or closing other streams.
    foreach (var writer in new[] { metadataWriter, sampleWriter, threadWriter, frameWriter })
        if (writer != null)
            try { writer.Dispose(); }
            catch (System.Exception exception) { if (outputCloseError == null) outputCloseError = exception; }
    if (loadAttempted)
    {
        restorationAttempted = true;
        try
        {
            if (hadOriginalFrames)
            {
                if (!UnityEditorInternal.ProfilerDriver.LoadProfile(backupPath, false)) throw new System.IO.IOException("Original Profiler backup reload returned false.");
            }
            else UnityEditorInternal.ProfilerDriver.ClearAllFrames();
            restoredFirst = UnityEditorInternal.ProfilerDriver.firstFrameIndex; restoredLast = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
            if (hadOriginalFrames && (restoredFirst < 0 || restoredLast < 0 || restoredFirst > restoredLast)) throw new System.IO.InvalidDataException("Backup reload succeeded without a valid available restored frame range.");
            if (!hadOriginalFrames && (restoredFirst >= 0 || restoredLast >= 0)) throw new System.IO.InvalidDataException("ClearAllFrames left a nonempty range after originally empty history.");
            if (UnityEditorInternal.ProfilerDriver.enabled) throw new System.InvalidOperationException("Profiler unexpectedly enabled after offline load/restore.");
            restorationSucceeded = true;
        }
        catch (System.Exception exception) { restorationError = exception; }
    }
    UnityEditorInternal.ProfilerDriver.NewProfilerFrameRecorded -= onNewFrame;
    UnityEditorInternal.ProfilerDriver.profileLoaded -= onLoad;
    UnityEditorInternal.ProfilerDriver.profileCleared -= onClear;
    observationPhase = "finished";
}
var manifest = new System.Collections.Generic.Dictionary<string, object> {
    { "sourceCandidateSha256", "fa98a34dec5eee74833d6412eef9e26ce69aa671492ed019a40db5b9f94bc7ff" },
    { "connectionPolicy", "only local Editor; disabled recording; no Players; event/range/connection checks during extraction" },
    { "observedNewProfilerFrames", observedNewFrames }, { "observedProfileLoads", observedLoads }, { "observedProfileClears", observedClears },
    { "extractingNewProfilerFrames", extractingNewFrames }, { "extractingProfileLoads", extractingLoads }, { "extractingProfileClears", extractingClears },
    { "metadataGuid", windowMetadataGuid.ToString("D") }, { "metadataTag", windowMetadataTag },
    { "maxMetadataChunksPerFrame", maxMetadataChunksPerFrame }, { "maxMetadataChunksTotal", maxMetadataChunksTotal },
    { "reportedMetadataChunks", reportedMetadataChunks }, { "retainedMetadataChunks", retainedMetadataChunks },
    { "malformedMetadataChunks", malformedMetadataChunks }, { "metadataChunksCapped", metadataChunksCapped },
    { "metadataFrameSummaries", metadataFrameSummaries }, { "metadataPayloadsComparedToRuntimeSidecar", false },
    { "schemaVersion", 1 }, { "purpose", "Offline raw Profiler observation; no performance acceptance or causal accounting" }, { "unity", UnityEngine.Application.unityVersion },
    { "inputProfile", inputProfile }, { "inputSha256Before", inputShaBefore }, { "inputSha256After", inputShaAfter }, { "request", request },
    { "originalFirstFrameIndex", originalFirst }, { "originalLastFrameIndex", originalLast }, { "originalProfileBackup", hadOriginalFrames ? backupPath : null }, { "originalProfileBackupSha256", backupSha },
    { "loadAttempted", loadAttempted }, { "loadSucceeded", loadSucceeded }, { "loadedFirstFrameIndex", loadedFirst }, { "loadedLastFrameIndex", loadedLast },
    { "frameIds", scannedFrames }, { "scanReachedLastFrame", scanReachedLast }, { "scannedFramesCapped", scannedFramesCapped },
    { "thresholdLongFrameCountInScannedPrefix", longFrames.Count }, { "selectionSeedFrames", seedFrames }, { "selectedProfilerFrameIndices", selectedFrames }, { "seedSelectionCapped", selectionCapped },
    { "anyThreadEnumerationCapped", anyThreadEnumerationCapped }, { "anySamplesCapped", anySamplesCapped }, { "retainedRawSamples", retainedTotal }, { "treeValidationErrorCount", treeErrorTotal },
    { "extractionCompleted", extractionCompleted && outputCloseError == null }, { "extractionError", extractionError == null ? null : extractionError.ToString() },
    { "outputCloseError", outputCloseError == null ? null : outputCloseError.ToString() },
    { "restorationAttempted", restorationAttempted }, { "restorationApiSucceeded", restorationSucceeded }, { "restoredFirstFrameIndex", restoredFirst }, { "restoredLastFrameIndex", restoredLast },
    { "restorationError", restorationError == null ? null : restorationError.ToString() }, { "restoredFrameIndicesProveSameRawIdentity", false },
    { "profilerFrameIndexMappedToTimeFrameCount", false }, { "profileClockMappedToStopwatchOrFrameTimingEpoch", false },
    { "parentIdentitiesRequireFinalWholeThreadSummaryValidation", true }, { "treeValidationErrorMessageCapPerThread", 32 },
    { "nestedMarkerDurationsSummed", false }, { "crossThreadDurationsSummed", false }, { "formalPerformanceGatesChanged", false }, { "performanceAcceptance", false }
};
using (var manifestWriter = createWriter("manifest.json"))
    manifestWriter.Write(Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
if (extractionError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(extractionError).Throw();
if (outputCloseError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outputCloseError).Throw();
if (restorationError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(restorationError).Throw();
return Newtonsoft.Json.JsonConvert.SerializeObject(new System.Collections.Generic.Dictionary<string, object> {
    { "outputDirectory", outputDirectory }, { "extractionCompleted", extractionCompleted }, { "restorationApiSucceeded", restorationSucceeded },
    { "selectedFrames", selectedFrames.Count }, { "retainedSamples", retainedTotal }, { "treeValidationErrors", treeErrorTotal },
    { "capsPresent", scannedFramesCapped || selectionCapped || anyThreadEnumerationCapped || anySamplesCapped }, { "performanceAcceptance", false }
});
