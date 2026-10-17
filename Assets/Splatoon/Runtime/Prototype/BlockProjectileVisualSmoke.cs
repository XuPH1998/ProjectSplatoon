#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in packaged GPU display check; synthetic shots never change combat or terrain.</summary>
    public sealed class BlockProjectileVisualSmoke : MonoBehaviour
    {
        const string OutputFlag = "-blockProjectileVisualOutput";
        const double TimeoutSeconds = 120;
        static BlockProjectileVisualSmoke _instance;
        static readonly Vector3 Fixture = new(4000, 4000, 4000);
        [Serializable] sealed class CaseResult
        {
            public int hero, team, shots, expectedCubes, width, height, changedPixels, teamPixels, otherTeamPixels;
            public int cameraId, roiX, roiY, roiWidth, roiHeight;
            public bool passed, normalMainCamera;
            public string controlImage, sampleImage;
            public Color expectedTeamColor;
        }
        [Serializable] sealed class Result
        {
            public int schema = 2;
            public string scope = "GPU display only: real configurations -> synthetic InkShot/InkExplosionEvent/SubEffectEvent -> production Spawn/Explosion/Effect -> normal Update/LateUpdate -> actual main-camera backbuffer. No paid firing, authority collision, damage or RPC acceptance.";
            public string runtimeMvid, runtimeAssembly, unityVersion, gpu, graphicsApi, finishedUtc, error;
            public double timeoutSeconds = TimeoutSeconds, elapsedSeconds;
            public int oldCameraId, mainCameraId;
            public bool passed, editor, oldCameraStillExistsAndDisabled, mainCameraChanged, noRenderTexture, prototypeGuiSuppressed;
            public List<CaseResult> cases = new();
            public List<ExplosionResult> explosionCases = new();
            public int expectedMainExplosionCases = 12, expectedSubExplosionCases = 16;
            public bool explosionsPassed;
            public List<string> errors = new();
        }
        [Serializable] sealed class ExplosionResult
        {
            public string kind, label, originalPrefab, controlImage, sampleImage;
            public int hero, subWeaponId, team, cameraId, cullingMask, effectInstanceId, originalPrefabId;
            public int activeOriginalInstances, totalOriginalInstances, prefabParticleSystems, instanceParticleSystems;
            public int rendererCount, liveParticles, changedPixels, teamPixels, otherTeamPixels, eventCalls;
            public bool collision, originalPrefabReference, teamColorsMatch, scaleMatches, duplicateSuppressed;
            public bool naturalRecycle, returnedToOriginalPool, poolReuseRequired, poolReused, passed;
            public bool normalMainCamera, onlyOriginalPrefabPixels;
            public Vector3 expectedScale, actualScale, cameraPosition, cameraForward;
            public Color expectedTeamColor;
            public float sampledAge, recycleWaitSeconds;
            public int observedFrame, observedLiveParticles, sampledFrame;
            public float observedDeltaTime, observedParticleTime, sampledDeltaTime, sampledParticleTime;
            public double controlCaptureWallSeconds;
        }
        sealed class Frame
        {
            public int Width, Height;
            public Color32[] Pixels;
            public byte[] Png;
        }
        readonly Result _result = new();
        string _output;
        double _started;
        bool _finished, _oldEnabled, _ownsOutput, _appEnabled;
        Camera _oldCamera, _camera;
        InkPresentation _presentation;
        SubWeaponPresentation _subPresentation;
        PrototypeApp _app;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, OutputFlag);
            if (_instance != null || at < 0) return;
            if (at + 1 == args.Length || args[at + 1].StartsWith("-", StringComparison.Ordinal))
            { Debug.LogError(OutputFlag + " requires a fresh output directory"); Application.Quit(2); return; }
            var go = new GameObject("Block projectile visual smoke"); DontDestroyOnLoad(go);
            _instance = go.AddComponent<BlockProjectileVisualSmoke>();
            _instance._output = Path.GetFullPath(args[at + 1]);
        }
        IEnumerator Start()
        {
            _started = Time.realtimeSinceStartupAsDouble; Application.runInBackground = true;
            Application.logMessageReceived += OnLog;
            var assembly = typeof(InkPresentation).Assembly;
            _result.runtimeMvid = assembly.ManifestModule.ModuleVersionId.ToString();
            _result.runtimeAssembly = assembly.Location; _result.unityVersion = Application.unityVersion;
            _result.editor = Application.isEditor; _result.gpu = SystemInfo.graphicsDeviceName;
            _result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            var run = Run();
            while (!_finished)
            {
                object next = null; bool moved = false;
                try { moved = run.MoveNext(); if (moved) next = run.Current; }
                catch (Exception error) { Finish(error.ToString()); }
                if (_finished || !moved) break;
                yield return next;
            }
            if (!_finished) Finish(null);
        }
        void Update()
        {
            if (!_finished && _started > 0 && Time.realtimeSinceStartupAsDouble - _started >= TimeoutSeconds)
                Finish("Visual probe exceeded its 120 second wall-clock limit");
        }
        IEnumerator Run()
        {
            Need(!Directory.Exists(_output) && !File.Exists(_output), "Preserve existing visual probe output");
            Directory.CreateDirectory(_output); _ownsOutput = true;
            Need(string.Equals(Environment.GetEnvironmentVariable("SPLATOON_DISABLE_SYSTEM_CURSOR"), "1", StringComparison.Ordinal)
                || Array.Exists(Environment.GetCommandLineArgs(), argument => string.Equals(argument, "-noSystemCursor", StringComparison.OrdinalIgnoreCase)),
                "Launch the dedicated probe with -noSystemCursor or SPLATOON_DISABLE_SYSTEM_CURSOR=1");
            Need(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "A graphical Player is required");
            while (PrototypeApp.Current == null || !PrototypeApp.Current.Ready) yield return null;
            var app = PrototypeApp.Current;
            Need(!app.InRoom && !app.Busy, "Use a fresh Player with no existing room");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Need(app.InRoom, app.Error);
            app.CaptureMouse(false);
            while (PrototypePlayer.Local == null || PrototypeMatch.Current == null || !PrototypeMatch.Current.InitialSyncComplete
                || InkPresentation.Current == null || !InkPresentation.Current.BlockMode || Camera.main == null) yield return null;
            _presentation = InkPresentation.Current;
            // Only this explicitly requested display fixture suppresses the normal IMGUI overlay.
            _app = app; _appEnabled = app.enabled; app.enabled = false;
            _result.prototypeGuiSuppressed = !app.enabled;
            _oldCamera = Camera.main; _oldEnabled = _oldCamera.enabled; _result.oldCameraId = _oldCamera.GetInstanceID();
            // Let ordinary LateUpdate use the existing main camera before disabling that live object.
            yield return null; yield return new WaitForEndOfFrame();
            _camera = new GameObject("Block projectile smoke main camera").AddComponent<Camera>();
            _camera.CopyFrom(_oldCamera); _camera.tag = "MainCamera";
            _camera.targetTexture = null; _camera.targetDisplay = 0; _camera.rect = new Rect(0, 0, 1, 1);
            _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(.025f, .025f, .025f, 1);
            _camera.cullingMask = 1 << BlockFlightPresentation.Layer; _camera.fieldOfView = 45;
            _camera.nearClipPlane = .05f; _camera.farClipPlane = 40;
            _camera.transform.SetPositionAndRotation(Fixture + Vector3.back * 2, Quaternion.identity);
            _oldCamera.enabled = false; _camera.enabled = true;
            yield return null; yield return new WaitForEndOfFrame();
            _result.mainCameraId = _camera.GetInstanceID();
            _result.oldCameraStillExistsAndDisabled = _oldCamera != null && !_oldCamera.isActiveAndEnabled;
            _result.mainCameraChanged = Camera.main == _camera && _result.oldCameraId != _result.mainCameraId;
            _result.noRenderTexture = _camera.targetTexture == null;
            Need(_result.oldCameraStillExistsAndDisabled && _result.mainCameraChanged && _result.noRenderTexture,
                "Disabled cached main camera must be replaced by the active fixture main camera");
            uint id = 0x70000000;
            for (int hero = 1; hero <= 9; hero++) for (byte team = 1; team <= 2; team++)
            {
                Need(Camera.main == _camera && _camera.isActiveAndEnabled && _camera.targetTexture == null, "Normal fixture main camera changed");
                var weapon = GameplayConfig.GetWeapon(hero); Need(weapon != null, "Missing weapon " + hero);
                _presentation.ClearFlights();
                yield return null; yield return new WaitForEndOfFrame();
                var control = Capture(); string label = "hero-" + hero + "-team-" + team;
                WriteNew(label + "-control.png", control.Png);
                double born = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
                for (byte pellet = 0; pellet < weapon.PelletCount; pellet++)
                {
                    var offset = weapon.PelletCount > 1 ? new Vector3((pellet % 3 - 1) * .24f, (pellet / 3 - .5f) * .24f, 0) : Vector3.zero;
                    _presentation.Spawn(new InkShot { Id = ++id, Round = PrototypeMatch.Current.State.Value.Round,
                        HeroId = hero, Team = team, Shooter = ulong.MaxValue, Born = born, Seed = id,
                        Origin = Fixture + offset, Velocity = Vector3.forward * weapon.SpeedMin, Charge = 1,
                        PelletIndex = pellet, Configuration = weapon, ConfigurationRevision = WeaponConfigService.Current.Revision(hero) });
                }
                var row = new CaseResult { hero = hero, team = team, shots = weapon.PelletCount,
                    expectedCubes = weapon.PelletCount * BlockFlightPresentation.CubesPerShot(weapon),
                    cameraId = _camera.GetInstanceID(), normalMainCamera = true, expectedTeamColor = PrototypeArena.TeamColor(team),
                    controlImage = label + "-control.png", sampleImage = label + ".png" };
                Frame best = null;
                // Each sample follows normal LateUpdate. Never submit directly to a chosen camera.
                for (int sample = 0; sample < 3; sample++)
                {
                    yield return null; yield return new WaitForEndOfFrame();
                    var image = Capture(); var observed = Compare(control, image, team);
                    if (best == null || observed.teamPixels > row.teamPixels)
                    {
                        best = image; row.width = image.Width; row.height = image.Height;
                        row.roiX = image.Width / 4; row.roiY = image.Height / 4;
                        row.roiWidth = image.Width * 3 / 4 - row.roiX;
                        row.roiHeight = image.Height * 3 / 4 - row.roiY;
                        row.changedPixels = observed.changedPixels; row.teamPixels = observed.teamPixels;
                        row.otherTeamPixels = observed.otherTeamPixels;
                    }
                }
                row.passed = row.changedPixels > 25 && row.teamPixels > 25 && row.teamPixels > row.otherTeamPixels * 2;
                WriteNew(row.sampleImage, best.Png); _result.cases.Add(row);
            }
            // This explicit fixture alone observes pooled originals. Ordinary game flow is unchanged.
            var explosions = RunExplosions();
            while (explosions.MoveNext()) yield return explosions.Current;
        }

        // Diagnostic reads only; production event methods remain the sole creators/recyclers.
        static object Read(object instance, string name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Need(field != null, "Presentation diagnostic schema changed: " + name);
            return field.GetValue(instance);
        }
        List<(GameObject effect, float until, GameObject prefab, int layers)> MainEffects() =>
            (List<(GameObject effect, float until, GameObject prefab, int layers)>)Read(_presentation, "_explosionEffects");
        Dictionary<GameObject, Queue<GameObject>> MainPool() =>
            (Dictionary<GameObject, Queue<GameObject>>)Read(_presentation, "_explosionPool");
        List<GameObject> SubEffects() => ((IEnumerable)Read(_subPresentation, "_effects")).Cast<object>()
            .Where(item => (bool)Read(item, "Pooled")).Select(item => (GameObject)Read(item, "Object")).ToList();
        IDictionary SubPoolItems() => (IDictionary)Read(Read(_subPresentation, "_pool"), "_all");
        int SubInstances(GameObject prefab) => SubPoolItems().Values.Cast<object>().Count(item => (GameObject)Read(item, "Source") == prefab);
        bool SubReturned(GameObject prefab, GameObject instance)
        {
            var free = (IDictionary)Read(Read(_subPresentation, "_pool"), "_free");
            return instance != null && !instance.activeSelf && free.Contains(prefab)
                && ((IEnumerable)free[prefab]).Cast<object>().Any(item => (GameObject)Read(item, "Object") == instance);
        }
        static int EffectLayers(GameObject prefab)
        {
            int mask = 0;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true)) mask |= 1 << renderer.gameObject.layer;
            Need(mask != 0, "Original explosion prefab has no Renderer");
            return mask;
        }
        static void Inspect(ExplosionResult row, GameObject instance, GameObject prefab, byte team, float scale)
        {
            var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            row.effectInstanceId = instance.GetInstanceID(); row.originalPrefabId = prefab.GetInstanceID();
            row.originalPrefab = prefab.name; row.expectedTeamColor = PrototypeArena.TeamColor(team);
            row.prefabParticleSystems = prefab.GetComponentsInChildren<ParticleSystem>(true).Length;
            row.instanceParticleSystems = systems.Length; row.rendererCount = instance.GetComponentsInChildren<Renderer>(true).Length;
            row.teamColorsMatch = systems.Length > 0 && systems.All(ps =>
                ps.main.startColor.mode == ParticleSystemGradientMode.Color && ColorDistance(ps.main.startColor.color, row.expectedTeamColor) < .001f);
            row.expectedScale = Vector3.one * scale; row.actualScale = instance.transform.localScale;
            row.scaleMatches = Vector3.Distance(row.expectedScale, row.actualScale) < .0001f;
            row.observedFrame = Time.frameCount; row.observedDeltaTime = Time.deltaTime;
            row.observedLiveParticles = systems.Sum(ps => ps.particleCount);
            row.observedParticleTime = systems.Length > 0 ? systems.Max(ps => ps.time) : 0;
        }
        static float ColorDistance(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a);
        ExplosionResult ExplosionRow(string kind, string label, int hero, int subId, byte team, bool collision)
        {
            return new ExplosionResult { kind = kind, label = label, hero = hero, subWeaponId = subId, team = team,
                collision = collision, cameraId = _camera.GetInstanceID(), cullingMask = _camera.cullingMask,
                cameraPosition = _camera.transform.position, cameraForward = _camera.transform.forward,
                normalMainCamera = Camera.main == _camera && _camera.targetTexture == null,
                controlImage = label + "-control.png", sampleImage = label + ".png", eventCalls = 2 };
        }
        IEnumerator SampleExplosion(ExplosionResult row, Frame control, GameObject instance, double born)
        {
            Frame best = null;
            // Natural particle advancement only. Never Simulate/Emit a replacement effect.
            double until = Time.realtimeSinceStartupAsDouble + .22;
            do
            {
                yield return null; yield return new WaitForEndOfFrame();
                var frame = Capture(); var observed = Compare(control, frame, (byte)row.team);
                if (best == null || observed.teamPixels > row.teamPixels)
                {
                    best = frame; row.changedPixels = observed.changedPixels; row.teamPixels = observed.teamPixels;
                    row.otherTeamPixels = observed.otherTeamPixels;
                    row.sampledAge = (float)(Time.realtimeSinceStartupAsDouble - born);
                    var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
                    row.liveParticles = systems.Sum(ps => ps.particleCount);
                    row.sampledFrame = Time.frameCount; row.sampledDeltaTime = Time.deltaTime;
                    row.sampledParticleTime = systems.Length > 0 ? systems.Max(ps => ps.time) : 0;
                }
            } while (Time.realtimeSinceStartupAsDouble < until && instance != null && instance.activeInHierarchy);
            Need(best != null, "No explosion backbuffer sample"); WriteNew(row.sampleImage, best.Png);
        }
        static void CompleteExplosion(ExplosionResult row)
        {
            row.passed = row.normalMainCamera && row.onlyOriginalPrefabPixels && row.originalPrefabReference
                && row.activeOriginalInstances == 1 && row.totalOriginalInstances == 1
                && row.prefabParticleSystems > 0 && row.instanceParticleSystems == row.prefabParticleSystems
                && row.teamColorsMatch && row.scaleMatches && row.duplicateSuppressed
                && row.naturalRecycle && row.returnedToOriginalPool && row.poolReused
                && row.liveParticles > 0 && row.changedPixels > 25 && row.teamPixels > 25
                && row.teamPixels > row.otherTeamPixels * 2;
        }
        IEnumerator RunExplosions()
        {
            _presentation.Clear(); _subPresentation = PrototypeMatch.Current.SubPresentation;
            Need(_subPresentation != null, "Production sub-weapon presentation missing"); _subPresentation.Clear();
            // Common explosion pose across all main and sub cases; retain authored effect layers.
            _camera.transform.SetPositionAndRotation(Fixture + Vector3.back * 12, Quaternion.identity);
            var mainInstances = new Dictionary<GameObject, int>(); uint eventId = 0x71000000;
            foreach (int hero in new[] { 3, 5, 9 }) foreach (bool collision in new[] { false, true })
            foreach (byte team in new byte[] { 1, 2 })
            {
                var config = GameplayConfig.GetWeapon(hero); var ammo = config.Ammo;
                Need(ammo.HasExplosionVisual, "Main weapon original explosion missing: " + hero);
                var prefab = ammo.ExplosionPrefab; _camera.cullingMask = EffectLayers(prefab);
                _presentation.ClearFlights();
                yield return null; yield return new WaitForEndOfFrame();
                var row = ExplosionRow("main", "main-explosion-hero-" + hero + "-collision-" + collision + "-team-" + team, hero, 0, team, collision);
                double controlCaptureStarted = Time.realtimeSinceStartupAsDouble;
                var control = Capture(); WriteNew(row.controlImage, control.Png);
                row.controlCaptureWallSeconds = Time.realtimeSinceStartupAsDouble - controlCaptureStarted;
                // Fixture preparation: finish the control GPU readback/PNG write frame before queuing a new effect.
                // Particle playback and sampling keep their ordinary natural simulation.
                yield return null;
                double at = PrototypeMatch.Current.NetworkManager.ServerTime.Time, born = Time.realtimeSinceStartupAsDouble;
                var e = new InkExplosionEvent { Round = PrototypeMatch.Current.State.Value.Round, ShotId = ++eventId,
                    HeroId = hero, ConfigurationRevision = WeaponConfigService.Current.Revision(hero), Team = team,
                    Position = Fixture, Normal = Vector3.forward, Seed = eventId, Time = at, Collision = collision, Shooter = ulong.MaxValue };
                _presentation.Explosion(e); _presentation.Explosion(e);
                double deadline = Time.realtimeSinceStartupAsDouble + 2;
                while (!MainEffects().Any(item => item.prefab == prefab) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                var effects = MainEffects().Where(item => item.prefab == prefab).ToList();
                Need(effects.Count == 1, "One original main explosion required after duplicate event");
                var effect = effects[0]; var instance = effect.effect;
                row.originalPrefabReference = effect.prefab == prefab; row.activeOriginalInstances = effects.Count;
                row.totalOriginalInstances = effects.Count + (MainPool().TryGetValue(prefab, out var existing) ? existing.Count : 0);
                Inspect(row, instance, prefab, team, InkExplosionRules.Radius(ammo, collision));
                row.poolReuseRequired = mainInstances.TryGetValue(prefab, out int previous);
                row.poolReused = !row.poolReuseRequired || previous == instance.GetInstanceID(); mainInstances[prefab] = instance.GetInstanceID();
                row.duplicateSuppressed = effects.Count == 1;
                // Clear only the generated block flights/fragments; retain the production original burst.
                _presentation.ClearFlights(); row.onlyOriginalPrefabPixels = _presentation.BlockDebrisCount == 0;
                var sample = SampleExplosion(row, control, instance, born); while (sample.MoveNext()) yield return sample.Current;
                float recycleStarted = Time.realtimeSinceStartup;
                while (MainEffects().Any(item => item.effect == instance) && Time.time <= effect.until + .5f) yield return null;
                row.recycleWaitSeconds = Time.realtimeSinceStartup - recycleStarted;
                row.naturalRecycle = !MainEffects().Any(item => item.effect == instance) && instance != null && !instance.activeSelf;
                row.returnedToOriginalPool = MainPool().TryGetValue(prefab, out var pooled) && pooled.Contains(instance);
                CompleteExplosion(row); _result.explosionCases.Add(row);
            }
            var subInstances = new Dictionary<GameObject, int>();
            foreach (var type in new[] { SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb,
                SubWeaponType.CurlingBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.InkMine })
            foreach (byte team in new byte[] { 1, 2 })
            {
                int subId = SubWeaponConfigService.Id(type); var config = SubWeaponConfigService.Current.GetById(subId);
                var prefab = config.Visuals.impactPrefab; Need(prefab != null, "Explosive sub-weapon original impact missing: " + type);
                _camera.cullingMask = EffectLayers(prefab); _presentation.ClearFlights();
                yield return null; yield return new WaitForEndOfFrame();
                var row = ExplosionRow("sub", "sub-explosion-" + type + "-team-" + team, 1, subId, team, false);
                double controlCaptureStarted = Time.realtimeSinceStartupAsDouble;
                var control = Capture(); WriteNew(row.controlImage, control.Png);
                row.controlCaptureWallSeconds = Time.realtimeSinceStartupAsDouble - controlCaptureStarted;
                // Fixture preparation: finish the control GPU readback/PNG write frame before queuing a new effect.
                // Particle playback and sampling keep their ordinary natural simulation.
                yield return null;
                double at = PrototypeMatch.Current.NetworkManager.ServerTime.Time, born = Time.realtimeSinceStartupAsDouble;
                var e = new SubEffectEvent { Sequence = ++eventId, Round = PrototypeMatch.Current.State.Value.Round,
                    EntityId = eventId, SubWeaponId = subId, ConfigRevision = SubWeaponConfigService.Current.RevisionById(subId),
                    Kind = SubEffectKind.Explosion, Hero = 1, Team = team, At = at, Position = Fixture, Normal = Vector3.forward, Radius = config.Blast.outerRadius };
                _subPresentation.Effect(e); _subPresentation.Effect(e);
                double deadline = Time.realtimeSinceStartupAsDouble + 2;
                while (SubEffects().Count == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                var effects = SubEffects(); Need(effects.Count == 1, "One original sub explosion required after duplicate event");
                var instance = effects[0]; var item = SubPoolItems()[instance];
                row.originalPrefabReference = item != null && (GameObject)Read(item, "Source") == prefab;
                row.activeOriginalInstances = effects.Count; row.totalOriginalInstances = SubInstances(prefab);
                Inspect(row, instance, prefab, team, Mathf.Max(.4f, e.Radius * .35f));
                row.poolReuseRequired = subInstances.TryGetValue(prefab, out int previous);
                row.poolReused = !row.poolReuseRequired || previous == instance.GetInstanceID(); subInstances[prefab] = instance.GetInstanceID();
                row.duplicateSuppressed = effects.Count == 1;
                // The diagnostic excludes the generic ring and fragments from original-prefab pixel proof.
                var lines = _subPresentation.GetComponentsInChildren<LineRenderer>().Where(line => line.enabled && !line.transform.IsChildOf(instance.transform)).ToArray();
                foreach (var line in lines) line.enabled = false;
                try
                {
                    _presentation.ClearFlights(); row.onlyOriginalPrefabPixels = _presentation.BlockDebrisCount == 0 && lines.All(line => line == null || !line.enabled);
                    var sample = SampleExplosion(row, control, instance, born); while (sample.MoveNext()) yield return sample.Current;
                    float recycleStarted = Time.realtimeSinceStartup;
                    double end = at + Math.Max(.3, config.Visuals.impactLifetime) + .6;
                    while (SubEffects().Contains(instance) && PrototypeMatch.Current.NetworkManager.ServerTime.Time < end) yield return null;
                    row.recycleWaitSeconds = Time.realtimeSinceStartup - recycleStarted;
                    row.naturalRecycle = !SubEffects().Contains(instance) && instance != null && !instance.activeSelf;
                    row.returnedToOriginalPool = SubReturned(prefab, instance);
                }
                finally { foreach (var line in lines) if (line != null) line.enabled = true; }
                CompleteExplosion(row); _result.explosionCases.Add(row);
            }
            _result.explosionsPassed = _result.explosionCases.Count == 28 && _result.explosionCases.TrueForAll(row => row.passed);
        }
        Frame Capture()
        {
            Need(Camera.main == _camera && _camera.isActiveAndEnabled && _camera.targetTexture == null, "Backbuffer camera is not the active main camera");
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                Need(texture != null && texture.width == Screen.width && texture.height == Screen.height, "Actual backbuffer capture failed");
                return new Frame { Width = texture.width, Height = texture.height, Pixels = texture.GetPixels32(), Png = texture.EncodeToPNG() };
            }
            finally { if (texture != null) Destroy(texture); }
        }
        static CaseResult Compare(Frame control, Frame image, byte team)
        {
            Need(control.Width == image.Width && control.Height == image.Height, "Backbuffer resolution changed during a case");
            var row = new CaseResult(); var expected = Chroma(PrototypeArena.TeamColor(team));
            var opposite = Chroma(PrototypeArena.TeamColor(team == 1 ? (byte)2 : (byte)1));
            // Fixed central half of the actual backbuffer; the distant solid background contains no actors or terrain.
            for (int y = image.Height / 4; y < image.Height * 3 / 4; y++)
            for (int x = image.Width / 4; x < image.Width * 3 / 4; x++)
            {
                int at = y * image.Width + x; var a = control.Pixels[at]; var b = image.Pixels[at];
                if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) <= 30) continue;
                row.changedPixels++; if (Math.Max(b.r, Math.Max(b.g, b.b)) < 32) continue;
                var actual = Chroma(b); float ours = Vector3.Distance(actual, expected), theirs = Vector3.Distance(actual, opposite);
                if (ours < .55f && ours + .1f < theirs) row.teamPixels++;
                else if (theirs < .55f && theirs + .1f < ours) row.otherTeamPixels++;
            }
            return row;
        }
        static Vector3 Chroma(Color color)
        {
            float maximum = Mathf.Max(.0001f, Mathf.Max(color.r, Mathf.Max(color.g, color.b)));
            return new Vector3(color.r, color.g, color.b) / maximum;
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void WriteNew(string name, byte[] bytes)
        {
            using var file = new FileStream(Path.Combine(_output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes, 0, bytes.Length);
        }
        void OnLog(string message, string trace, LogType type)
        {
            if (!_finished && (type == LogType.Error || type == LogType.Exception) && _result.errors.Count < 20)
                _result.errors.Add(message);
        }
        void Finish(string error)
        {
            if (_finished) return; _finished = true; Application.logMessageReceived -= OnLog;
            _result.error = error; _result.elapsedSeconds = Time.realtimeSinceStartupAsDouble - _started;
            _result.finishedUtc = DateTime.UtcNow.ToString("O");
            _result.passed = error == null && _result.errors.Count == 0 && _result.cases.Count == 18
                && _result.cases.TrueForAll(row => row.passed) && _result.explosionsPassed && _result.mainCameraChanged && _result.oldCameraStillExistsAndDisabled;
            try
            {
                if (_ownsOutput) WriteNew("summary.json", System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(_result, true)));
                else Debug.LogError("Visual probe could not create its output: " + error);
            }
            catch (Exception writeError) { _result.passed = false; Debug.LogError("Visual probe report failed: " + writeError.Message); }
            _presentation?.ClearFlights();
            _presentation?.Clear(); _subPresentation?.Clear();
            if (_camera != null) { _camera.enabled = false; Destroy(_camera.gameObject); }
            if (_oldCamera != null) _oldCamera.enabled = _oldEnabled;
            if (_app != null) _app.enabled = _appEnabled;
            PrototypeApp.Current?.CaptureMouse(false);
            if (Application.isEditor) { if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) PrototypeApp.Current.Leave().Forget(); }
            else Application.Quit(_result.passed ? 0 : 2);
        }
    }
}
#endif
