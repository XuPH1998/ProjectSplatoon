using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine.Rendering;
using Splatoon.Painting;
#endif

namespace Splatoon.Prototype
{
    // Explicit development fixture; neither generates input nor runs in ordinary sessions.
    public sealed class InkEdgeNetworkProbe : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Serializable] sealed class SurfaceState { public int id, bytes; public uint hash; }
        [Serializable] sealed class Sample
        {
            public uint sequence, ownershipHash;
            public double snapshotSeconds;
            public long residentBytes, checkpointReserveBytes;
            public SurfaceState[] surfaces;
        }
        [Serializable] sealed class Result { public List<Sample> samples = new(); }
        readonly Result _result = new();
        readonly Queue<PaintStamp> _paint = new();
        PrototypeMatch _match;
        double _connectedAt, _changedAt;
        uint _observed, _captured = uint.MaxValue;
        bool _first, _continued, _reading;
        string _output, _continueFile;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Environment.GetCommandLineArgs().Contains("-inkEdgeProbe")) return;
            var go = new GameObject("Ink edge network fixture"); DontDestroyOnLoad(go); go.AddComponent<InkEdgeNetworkProbe>();
        }
        static string Arg(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        void Awake()
        {
            _output = Arg("-inkEdgeProbeOutput", "Reports/InkEdges/network-state.json");
            _continueFile = Arg("-inkEdgeContinueFile", "");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
        }
        void Update()
        {
            var match = PrototypeMatch.Current;
            if (match == null || !match.IsSpawned || !match.InitialSyncComplete) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_match != match) { _match = match; _connectedAt = _changedAt = now; _observed = match.AppliedPaintSequence; _captured = uint.MaxValue; }
            if (match.IsServer && !_first && now - _connectedAt > 2)
            {
                _first = true;
                foreach (var stamp in InkEdgeFixture.Stamps(match.Arena)) { _paint.Enqueue(stamp); _paint.Enqueue(stamp); }
            }
            if (match.IsServer && _first && !_continued && _paint.Count == 0 && !string.IsNullOrEmpty(_continueFile) && File.Exists(_continueFile))
            {
                _continued = true;
                foreach (var original in InkEdgeFixture.Stamps(match.Arena))
                { var stamp = original; stamp.Team = (byte)(3 - stamp.Team); stamp.Radius *= .7f; stamp.ShapeSeed ^= 127u << 6; _paint.Enqueue(stamp); }
            }
            for (int i = 0; i < 8 && _paint.Count > 0; i++)
            {
                var stamp = _paint.Dequeue();
                match.Paint(match.Arena.Surfaces[stamp.SurfaceId], stamp.Position, stamp.Normal, stamp.Radius, stamp.Team, stamp.Hardness, stamp.Strength, stamp.ShapeSeed);
            }
            if (_observed != match.AppliedPaintSequence) { _observed = match.AppliedPaintSequence; _changedAt = now; }
            if (!_reading && _observed > 0 && _observed != _captured && _paint.Count == 0 && now - _changedAt > 3) Capture(match).Forget();
        }
        async UniTaskVoid Capture(PrototypeMatch match)
        {
            _reading = true;
            uint sequence = match.AppliedPaintSequence;
            var sample = new Sample { sequence = sequence, ownershipHash = match.Arena.OwnershipHash(), snapshotSeconds = match.LastSnapshotSeconds,
                residentBytes = PaintSurface.AllocatedBytes, checkpointReserveBytes = PaintSurface.CheckpointBytes };
            var states = new List<SurfaceState>();
            try
            {
                foreach (var surface in match.Arena.Surfaces.Values)
                {
                    var request = AsyncGPUReadback.Request(surface.Mask, 0, TextureFormat.RGBA32);
                    await UniTask.WaitUntil(() => request.done);
                    if (request.hasError) throw new InvalidOperationException("Ink edge readback failed");
                    uint hash = 2166136261; var data = request.GetData<byte>();
                    foreach (byte value in data) hash = unchecked((hash ^ value) * 16777619);
                    states.Add(new SurfaceState { id = surface.SurfaceId, bytes = data.Length, hash = hash });
                }
                if (match != null && match.IsSpawned && sequence == match.AppliedPaintSequence)
                {
                    sample.surfaces = states.ToArray(); _result.samples.Add(sample); _captured = sequence;
                    File.WriteAllText(_output, JsonUtility.ToJson(_result, true));
                    Debug.Log($"[INK-EDGES-NET] Settled sequence={sequence} ownership={sample.ownershipHash} surfaces={states.Count}");
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { _reading = false; }
        }
#endif
    }
}
