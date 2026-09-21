using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Splatoon.Painting;
#endif

namespace Splatoon.Prototype
{
    // Explicit Player acceptance fixture. Recording uses real main-camera frames, never Camera.Render.
    [DefaultExecutionOrder(20000)]
    public sealed class InkStaticScenario : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        string _scenario, _output; bool _ready, _recording; int _frame, _stroke;
        float _readyAt, _nextPaint; readonly List<PaintStamp> _stamps = new();
        readonly Queue<PaintStamp> _initialPaint = new();
        public static bool Settled { get; private set; } = true;
        static string Arg(string key, string fallback = "")
        { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, key); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (string.IsNullOrEmpty(Arg("-inkStaticScenario"))) return;
            var go = new GameObject("Ink static acceptance"); DontDestroyOnLoad(go); go.AddComponent<InkStaticScenario>();
        }
        void Awake()
        {
            Settled = false;
            _scenario = Arg("-inkStaticScenario"); _output = Arg("-inkStaticOutput", "Reports/InkStaticUpgrade/Video");
            if (_scenario == "video") Directory.CreateDirectory(_output);
        }
        void Update()
        {
            var match = PrototypeMatch.Current;
            if (match == null || !match.IsSpawned || !match.InitialSyncComplete) return;
            if (!_ready)
            {
                _ready = true;
                foreach (var stamp in InkEdgeFixture.Stamps(match.Arena)) _stamps.Add(stamp);
                if (match.IsServer)
                {
                    foreach (var surface in match.Arena.Surfaces.Values)
                    foreach (var region in surface.GameplayRegions)
                    {
                        var m = region.Matrix(surface);
                        for (float z = -region.Size.y / 2 + .45f; z < region.Size.y / 2; z += 1.1f)
                        for (float x = -region.Size.x / 2 + .45f; x < region.Size.x / 2; x += 1.1f)
                        {
                            var p = m.MultiplyPoint3x4(new Vector3(x, 0, z));
                            _initialPaint.Enqueue(new PaintStamp { SurfaceId = surface.SurfaceId, Position = p,
                                Normal = m.MultiplyVector(Vector3.up).normalized, Radius = 1.35f, Team = (byte)(p.x > 1 ? 2 : 1), Hardness = .65f, Strength = 1,
                                ShapeSeed = InkShapeAtlas.Pack(_stroke++ % 32, (uint)(_stroke * 9719) << 6) });
                        }
                    }
                }
                if (Arg("-inkStaticLook") == "legacy")
                {
                    foreach (var s in match.Arena.Surfaces.Values)
                    {
                        s.FlushDisplay(); var renderer = s.GetComponent<Renderer>(); var block = new MaterialPropertyBlock();
                        renderer.GetPropertyBlock(block); block.SetFloat("_InkAppearance", 0); renderer.SetPropertyBlock(block);
                    }
                    var probes = GameObject.Find("InkStaticReflections"); if (probes != null) probes.SetActive(false);
                }
                else if (Arg("-inkStaticLook") == "today" || Arg("-inkStaticLook") == "rounded")
                {
                    foreach (var s in match.Arena.Surfaces.Values)
                    {
                        s.FlushDisplay(); s.SetAppearance(true);
                        s.SetRoundedEdges(Arg("-inkStaticLook") == "rounded");
                    }
                }
            }
            // Pace the fixture through the normal authoritative API. A whole dense map in
            // one tick exceeds the existing RPC batch buffer and is not a gameplay workload.
            for (int i = 0; i < 8 && _initialPaint.Count > 0; i++)
            {
                var stamp = _initialPaint.Dequeue();
                match.Paint(match.Arena.Surfaces[stamp.SurfaceId], stamp.Position, stamp.Normal, stamp.Radius, stamp.Team, stamp.Hardness, stamp.Strength, stamp.ShapeSeed);
            }
            if (_initialPaint.Count > 0) return;
            if (!Settled) { Settled = true; _readyAt = Time.realtimeSinceStartup; Debug.Log("[INK-STATIC] Dense fixture settled"); }
            if (_scenario == "paint" && match.IsServer && Time.realtimeSinceStartup >= _nextPaint)
            {
                _nextPaint = Time.realtimeSinceStartup + 1f / 30;
                var stamp = _stamps[_stroke++ % _stamps.Count];
                match.Paint(match.Arena.Surfaces[stamp.SurfaceId], stamp.Position, stamp.Normal, stamp.Radius, (byte)(_stroke % 2 + 1), stamp.Hardness, .7f, stamp.ShapeSeed);
            }
            if (_scenario == "video" && !_recording && Time.realtimeSinceStartup - _readyAt > 10)
            { _recording = true; Time.captureFramerate = 30; StartCoroutine(Record()); }
        }
        void LateUpdate()
        {
            if (!_ready || Camera.main == null) return;
            var camera = Camera.main;
            float angle = _scenario == "video" ? (_frame / 30f - 4) * .07f : 0;
            camera.transform.position = new Vector3(Mathf.Sin(angle) * 8, _scenario == "video" ? 1.5f : 4, -Mathf.Cos(angle) * 8);
            camera.transform.LookAt(new Vector3(0, .1f, 1)); camera.fieldOfView = 60;
        }
        IEnumerator Record()
        {
            for (_frame = 0; _frame < 240; _frame++)
            {
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(_output, _frame.ToString("D4") + ".png"));
            }
            Time.captureFramerate = 0; File.WriteAllText(Path.Combine(_output, "capture.txt"), "240 consecutive main-camera Player frames; 1920x1080 requested; simulation capture rate 30; performance measured separately.");
            yield return new WaitForSecondsRealtime(3); Application.Quit(0);
        }
#endif
    }
}
