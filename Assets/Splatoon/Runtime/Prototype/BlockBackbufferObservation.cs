#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Prototype
{
    // Opt-in rendered-backbuffer provenance before formal benchmark sampling.
    // It neither renders a camera nor changes any display, input or benchmark rule.
    internal sealed class BlockBackbufferObservation
    {
        internal const int ContractVersion = 1, RecordLimit = 100;
        internal const double LifetimeSeconds = 20, IntervalSeconds = .2;
        [Serializable] sealed class Receipt
        {
            public int schema = ContractVersion, processId, sequence, unityFrame, formalSamples;
            public int screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight, targetDisplay;
            public string nonce, scenario, benchmarkOutput, runtimeMvid, unity, graphicsApi;
            public bool normalMainCamera, targetTextureNull, graphics, benchmarkBeginNegative;
            public double elapsedSinceObserverBootSeconds;
        }
        readonly string _path;
        readonly double _boot;
        readonly Receipt _receipt;
        double _nextWrite;
        int _lastFrame = -1;
        bool _closed;
        static string Argument(string key)
        {
            var arguments = Environment.GetCommandLineArgs(); int found = Array.IndexOf(arguments, key);
            return found >= 0 && found + 1 < arguments.Length ? arguments[found + 1] : null;
        }
        internal static BlockBackbufferObservation CreateIfRequested(string benchmarkOutput, string scenario)
        {
            string path = Argument("-blockBenchmarkBackbufferOutput");
            string nonce = Argument("-blockBenchmarkBackbufferNonce");
            if (path == null && nonce == null) return null;
            if (!Guid.TryParseExact(nonce, "N", out _) || string.IsNullOrEmpty(path)) return null;
            try
            {
                string output = Path.GetFullPath(benchmarkOutput);
                path = Path.GetFullPath(path);
                if (!string.Equals(path, output + ".backbuffer-observation.json", StringComparison.OrdinalIgnoreCase)) return null;
                return new BlockBackbufferObservation(path, output, nonce, scenario);
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
        BlockBackbufferObservation(string path, string benchmarkOutput, string nonce, string scenario)
        {
            _path = path; _boot = Time.realtimeSinceStartupAsDouble;
            _receipt = new Receipt { processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                benchmarkOutput = benchmarkOutput, nonce = nonce, scenario = scenario,
                runtimeMvid = typeof(BlockTerrainBenchmark).Assembly.ManifestModule.ModuleVersionId.ToString(),
                unity = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString() };
        }
        internal void ObserveCamera(Camera camera, double benchmarkBegin, bool done, int formalSamples)
        {
            if (_closed) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (benchmarkBegin >= 0 || done || formalSamples != 0 || now - _boot >= LifetimeSeconds || _receipt.sequence >= RecordLimit)
            { Close(); return; }
            if (now < _nextWrite || _lastFrame == Time.frameCount || camera == null || camera != Camera.main
                || camera.targetTexture != null || camera.targetDisplay != 0) return;
            _lastFrame = Time.frameCount; _nextWrite = now + IntervalSeconds;
            _receipt.sequence++; _receipt.unityFrame = Time.frameCount; _receipt.formalSamples = formalSamples;
            _receipt.screenWidth = Screen.width; _receipt.screenHeight = Screen.height;
            _receipt.cameraPixelWidth = camera.pixelWidth; _receipt.cameraPixelHeight = camera.pixelHeight;
            _receipt.targetDisplay = camera.targetDisplay; _receipt.normalMainCamera = camera == Camera.main;
            _receipt.targetTextureNull = camera.targetTexture == null; _receipt.benchmarkBeginNegative = benchmarkBegin < 0;
            _receipt.graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            _receipt.elapsedSinceObserverBootSeconds = now - _boot;
            string temporary = _path + ".writing";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(_receipt));
                if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { Close(); }
        }
        // No final write, deferred write, clock extension or callback after sampling.
        internal void Close() { _closed = true; }
    }
}
#endif
