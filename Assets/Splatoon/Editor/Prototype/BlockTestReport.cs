#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Splatoon.Editor
{
    /// <summary>Keep the full NUnit result, including tests omitted from CLI previews.</summary>
    [InitializeOnLoad]
    public static class BlockTestReport
    {
        public static string LastProgressWriteError { get; private set; }
        static BlockTestReport()
        {
            TestRunnerApi.RegisterTestCallback(new Reports());
        }
        sealed class Reports : ICallbacks
        {
            const string Folder = "Reports/BlockTerrain/UnityTests";
            static readonly object ProgressGate = new();
            static readonly Encoding ProgressEncoding = new UTF8Encoding(false);
            // A domain identifier, not a run identifier or a cumulative counter.
            static readonly string CollectorDomain = Guid.NewGuid().ToString("N");
            [Serializable] sealed class ProgressRow
            {
                public int schema = 1;
                public string kind, utc, collectorDomain, testId, uniqueName, fullName, resultState;
                public string message, stackTrace;
                public bool isSuite, hasResult;
                public int testCaseCount, passCount, failCount, skipCount, inconclusiveCount, assertCount;
                public double durationSeconds;
            }
            [Serializable] sealed class ProgressWriteError
            {
                public string utc, kind, collectorDomain, exceptionType, message;
            }
            static void Progress(string kind, ITestAdaptor test, ITestResultAdaptor result, bool leafOnly = false)
            {
                try
                {
                    test ??= result?.Test;
                    if (test == null) throw new InvalidOperationException("The progress callback has no test adaptor.");
                    // Parameterized parents and fixtures are suites, even when they have no children.
                    if (leafOnly && (test.IsSuite || test.HasChildren)) return;
                    var row = new ProgressRow
                    {
                        kind = kind, utc = DateTime.UtcNow.ToString("O"), collectorDomain = CollectorDomain,
                        testId = test.Id, uniqueName = test.UniqueName, fullName = result?.FullName ?? test.FullName,
                        isSuite = test.IsSuite, testCaseCount = test.TestCaseCount, hasResult = result != null,
                        resultState = result?.ResultState ?? "", passCount = result?.PassCount ?? -1,
                        failCount = result?.FailCount ?? -1, skipCount = result?.SkipCount ?? -1,
                        inconclusiveCount = result?.InconclusiveCount ?? -1, assertCount = result?.AssertCount ?? -1,
                        durationSeconds = result != null && double.IsFinite(result.Duration) ? result.Duration : -1
                    };
                    // Keep complete failure diagnostics in the leaf callback; successful rows stay compact.
                    if (!test.IsSuite && !test.HasChildren && result != null
                        && row.resultState.StartsWith("Failed", StringComparison.Ordinal))
                    {
                        row.message = result.Message ?? "";
                        row.stackTrace = result.StackTrace ?? "";
                    }
                    lock (ProgressGate)
                    {
                        Directory.CreateDirectory(Folder);
                        // Open, append one compact JSON line, and close in this callback.
                        // Normal domain reloads never retain a stream or truncate previous rows.
                        File.AppendAllText(Path.Combine(Folder, "progress.ndjson"), JsonUtility.ToJson(row) + "\n", ProgressEncoding);
                    }
                }
                catch (Exception exception)
                {
                    // Observation failures must not escape into UTF or create test-log assertions.
                    LastProgressWriteError = "Progress collection failed; detailed diagnostics are unavailable.";
                    try
                    {
                        var error = new ProgressWriteError { utc = DateTime.UtcNow.ToString("O"), kind = kind,
                            collectorDomain = CollectorDomain, exceptionType = exception.GetType().FullName, message = exception.Message };
                        LastProgressWriteError = error.utc + " " + kind + ": " + error.exceptionType + ": " + error.message;
                        lock (ProgressGate)
                        {
                            Directory.CreateDirectory(Folder);
                            File.AppendAllText(Path.Combine(Folder, "progress-write-errors.ndjson"), JsonUtility.ToJson(error) + "\n", ProgressEncoding);
                        }
                    }
                    catch (Exception) { } // The public diagnostic remains available if all file writes fail.
                }
            }
            public void RunStarted(ITestAdaptor testsToRun) => Progress("RunStarted", testsToRun, null);
            public void TestStarted(ITestAdaptor test) => Progress("TestStarted", test, null, true);
            public void TestFinished(ITestResultAdaptor result) => Progress("TestFinished", null, result, true);
            public void RunFinished(ITestResultAdaptor result)
            {
                Progress("RunFinished", null, result);
                const string folder = "Reports/BlockTerrain/UnityTests";
                Directory.CreateDirectory(folder);
                string path = Path.GetFullPath(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".xml"));
                TestRunnerApi.SaveResultToFile(result, path);
                Debug.Log("[BLOCK-TEST] Complete NUnit result: " + path);
            }
        }
    }
}
#endif
