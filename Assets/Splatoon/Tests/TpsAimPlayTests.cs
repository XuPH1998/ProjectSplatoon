#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class TpsAimPlayTests
    {
        const string Output = "Reports/TpsConvergence/PlayMode";
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static IEnumerator Wait(Func<bool> ready, string reason, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, reason);
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator HostFireReticleAndRenderedFlightAcrossFiveHeroes()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator ExitAfterFailure()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static GameObject Box(string name, Vector3 position, Vector3 scale)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = name;
            box.transform.position = position; box.transform.localScale = scale; Physics.SyncTransforms(); return box;
        }
        static void SetPose(PrototypePlayer player, Vector3 position)
        {
            var state = player.Snapshot.Value; state.Position = position; state.Yaw = state.Pitch = state.BodyYaw = 0;
            state.Velocity = state.PlanarVelocity = Vector3.zero; state.VerticalSpeed = 0; state.Grounded = true; state.Ink = state.Health = 100;
            state.LifeState = PlayerLifeState.Alive; state.ProtectedUntil = 0;
            state.Swimming = state.CompactBody = false; state.PaperPose = PaperPose.None; state.Movement = MovementMode.Human;
            state.SwimSource = state.AirSwimSource = SwimSurface.None; state.AirHumanOffset = state.CameraRebaseOffset = 0;
            state.PaperCenter = Vector3.zero; state.PaperRotation = Quaternion.identity;
            player.Snapshot.Value = state;
            ((PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(player)).Restore(state);
            typeof(PrototypePlayer).GetField("_look", Private).SetValue(player, Vector2.zero);
            Physics.SyncTransforms();
        }
        static void Capture(Camera camera, string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor")
                .GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
            type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, name, Output });
        }
        static void FlushPresentation(PrototypeMatch match, string clockLabel = null,
            double manualNow = 0, double sampleAt = 0, InkShot[] clockShots = null)
        {
            // Observe the existing RPC and presentation passes without advancing either clock.
            double serverBeforeRpc = clockShots != null ? match.NetworkManager.ServerTime.Time : 0;
            int queuedBeforeRpc = clockShots != null ? match.Projectiles.Spawned.Count : 0;
            typeof(PrototypeMatch).GetMethod("ServerTick", Private).Invoke(match, null);
            double serverBeforeLate = clockShots != null ? match.NetworkManager.ServerTime.Time : 0;
            var blockFlights = clockShots != null ? (BlockFlightPresentation)typeof(InkPresentation)
                .GetField("_blocks", Private).GetValue(InkPresentation.Current) : null;
            int acceptedBeforeLate = blockFlights?.ActiveShots ?? -1;
            typeof(InkPresentation).GetMethod("LateUpdate", Private).Invoke(InkPresentation.Current, null);
            if (clockShots != null)
            {
                double serverAfterLate = match.NetworkManager.ServerTime.Time;
                foreach (var shot in clockShots)
                {
                    double renderDelay = WeaponSimulation.UsesBubbleMesh(shot.Configuration) ? BubbleFlightPresentation.RenderDelay : 0;
                    double expiresAt = shot.Born + shot.Configuration.Lifetime + renderDelay;
                    Debug.Log(FormattableString.Invariant($"[TPS-FLIGHT-CLOCK] label={clockLabel} hero={shot.HeroId} id={shot.Id} round={shot.Round} manualNow={manualNow:R} sampleAt={sampleAt:R} born={shot.Born:R} sampleAge={sampleAt - shot.Born:R} lifetime={shot.Configuration.Lifetime:R} renderDelay={renderDelay:R} serverBeforeRpc={serverBeforeRpc:R} serverBeforeLate={serverBeforeLate:R} serverAfterLate={serverAfterLate:R} expiredBeforeRpc={serverBeforeRpc >= expiresAt} expiredBeforeLate={serverBeforeLate >= expiresAt} queuedBeforeRpc={queuedBeforeRpc} acceptedBeforeLate={acceptedBeforeLate} retainedAfterLate={blockFlights?.ActiveShots ?? -1} presentationAfterLate={InkPresentation.Current.ActiveShots}"));
                }
            }
        }
        // This is an independent scalar oracle for logical aim depth, rather than
        // comparing WeaponLaunch.Geometry to another call of itself.
        static Vector3 ExpectedCorrection(TpsAimSolution aim, WeaponRuntimeConfig weapon, float charge)
        {
            float depth;
            if (weapon.AimMode == WeaponAimMode.WeaponReference)
            {
                float speed = WeaponLaunch.CenterSpeed(weapon, charge);
                float range = InkBallistics.Position(Vector3.zero, Vector3.forward * speed, weapon,
                    Math.Min(weapon.ShotGuideSeconds, weapon.Lifetime)).z;
                depth = Vector3.Dot(aim.Muzzle - aim.CameraOrigin, aim.Forward) + Mathf.Max(.0001f, range);
            }
            else depth = aim.AimHit.Collider != null
                ? Mathf.Max(aim.AimHit.Distance, GameplayConfig.Global.AimCorrectionDistance)
                : GameplayConfig.Global.AimFarCorrectionDistance;
            return aim.CameraOrigin + aim.Forward * depth;
        }
        static Vector3 ExpectedFrontContact(InkShot shot, WeaponRuntimeConfig weapon, float plane)
        {
            // The authority sweeps complete fixed-step trajectory chords. Reconstruct
            // the front-plane intersection without using the actual PhysX hit point.
            double step = weapon.ReferenceRules || WeaponSimulation.IsBlaster(weapon) || DualiesNormalSimulation.Enabled(weapon)
                ? 1.0 / 60 : 1.0 / GameplayConfig.Global.ProjectileStepRate;
            Vector3 from = InkBallistics.Position(shot, weapon, 0);
            for (double end = step; end <= weapon.Lifetime + 1e-8; end += step)
            {
                var to = InkBallistics.Position(shot, weapon, end);
                float radius = WeaponSimulation.IsExplosher(weapon) ? ExplosherSimulation.Radius(weapon, end, false) : weapon.CollisionRadius;
                float centerPlane = plane - radius;
                if (from.z <= centerPlane && to.z >= centerPlane)
                    return Vector3.Lerp(from, to, (centerPlane - from.z) / (to.z - from.z)) + Vector3.forward * radius;
                from = to;
            }
            Assert.Fail("Configured trajectory cannot reach the target front plane"); return default;
        }
        static Vector3[] RenderedCenters(InkPresentation presentation, InkShot[] shots, double sampleAt)
        {
            presentation.UpdateFlights(sampleAt, null);
            Assert.That(presentation.ActiveShots, Is.EqualTo(shots.Length), "Only real RPC-delivered shots are sampled");
            int cubes = BlockFlightPresentation.CubesPerShot(shots[0].Configuration), count = shots.Length * cubes;
            var instances = BlockDefaultContractFixture.RenderedInstances(presentation, shots[0].Team, count);
            var centers = new Vector3[shots.Length];
            for (int index = 0; index < shots.Length; index++)
            {
                for (int cube = 0; cube < cubes; cube++) centers[index] += (Vector3)instances[index * cubes + cube].GetColumn(3);
                centers[index] /= cubes;
                // The production renderer visits shots in reverse order.
                var shot = shots[shots.Length - 1 - index];
                var expected = InkBallistics.Position(shot, shot.Configuration, sampleAt - shot.Born);
                Assert.That(Vector3.Distance(centers[index], expected), Is.LessThan(.0001f), "Submitted cube transforms follow authority trajectory");
            }
            return centers;
        }
        static Camera EvidenceCamera(string label, Vector3 center, Vector3 offset)
        {
            var camera = new GameObject(label).AddComponent<Camera>(); camera.CopyFrom(Camera.main);
            camera.enabled = false; camera.aspect = 1; camera.transform.position = center + offset; camera.transform.LookAt(center);
            return camera;
        }
        static int VisibleFlightPixels(InkPresentation presentation, InkShot[] shots, double sampleAt, string label, Vector3 offset)
        {
            var centers = RenderedCenters(presentation, shots, sampleAt);
            var center = centers.Aggregate(Vector3.zero, (sum, point) => sum + point) / centers.Length;
            var shownCamera = EvidenceCamera("Shown " + label, center, offset);
            var controlCamera = EvidenceCamera("Control " + label, center, offset);
            // DrawMeshInstanced is submitted to one specific camera. The separate,
            // identical control camera sees the same scene without those instances.
            // Hiding Layer 0 would also hide the map and invalidate this comparison.
            try
            {
                presentation.UpdateFlights(sampleAt, shownCamera); Capture(shownCamera, label); Capture(controlCamera, label + "-control");
                var shown = new Texture2D(2, 2); var hidden = new Texture2D(2, 2);
                try
                {
                    shown.LoadImage(File.ReadAllBytes(Output + "/" + label + ".png")); hidden.LoadImage(File.ReadAllBytes(Output + "/" + label + "-control.png"));
                    Assert.That(shown.width, Is.EqualTo(hidden.width)); Assert.That(shown.height, Is.EqualTo(hidden.height));
                    int changed = shown.GetPixels32().Zip(hidden.GetPixels32(), (a, b) => Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b)).Count(d => d > 30);
                    Assert.That(changed, Is.GreaterThan(25), "RPC-delivered cube instances contribute visible GPU pixels"); return changed;
                }
                finally { Object.Destroy(shown); Object.Destroy(hidden); }
            }
            finally { Object.Destroy(shownCamera.gameObject); Object.Destroy(controlCamera.gameObject); }
        }
        static void SimulateInput(PrototypePlayer player, PlayerInputFrame input, double now)
        {
            var state = player.Snapshot.Value; input.Sequence = state.AcknowledgedInput + 1;
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(player, input);
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
            player.Simulate(BlockDefaultContractFixture.Dt, now, PrototypeMatch.Current.State.Value.Phase);
            Assert.That(player.Snapshot.Value.InputTimedOut, Is.False); Physics.SyncTransforms();
        }
        static IEnumerator Scenario()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap ready");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null, "Real host and presentation");
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current; var solver = new TpsAimSolver();
            var platform = Box("TPS test platform", new Vector3(0, 5, 0), new Vector3(30, 1, 40));
            Vector3 feet = new(0, 5.54f, 0);
            var report = new List<string>();
            var csv = new List<string> { "hero,age,old_x,old_y,old_z,new_x,new_y,new_z" };
            var flightPresentation = InkPresentation.Current;
            int visiblePixels = 0;
            bool presentationEnabled = flightPresentation.enabled;
            match.enabled = player.enabled = flightPresentation.enabled = false;
            using (var blocks = new BlockDefaultContractFixture(match))
            {
            foreach (int hero in new[] { 1, 2, 3, 4, 5 })
            {
                match.enabled = true; player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
                yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == hero, "Switch hero " + hero);
                match.enabled = false; blocks.Now = Math.Max(blocks.Now, player.Snapshot.Value.SimulatedAt);
                SetPose(player, feet); yield return null;
                foreach (float distance in new[] { 5f, 12f })
                {
                    SetPose(player, feet);
                    blocks.PayMain(player, hero);
                    var state = player.Snapshot.Value; state.CurrentSpread=state.LastShotSpread=.000001f;state.LastShotVerticalSpread=.000001f; state.LastShotCharge = 1;
                    var weapon = GameplayConfig.GetWeapon(hero);
                    var aim = solver.Resolve(player, state, state.LastShotMuzzle);
                    // Authored camera offsets can put a 5m camera plane inside the pivot-to-muzzle path.
                    // Place the target front beyond that path and the full field radius; keep real obstruction checks.
                    float launchDepth = Mathf.Max(Vector3.Dot(aim.Pivot - aim.CameraOrigin, aim.Forward), Vector3.Dot(aim.Muzzle - aim.CameraOrigin, aim.Forward));
                    float clearanceRadius = Mathf.Max(weapon.CollisionRadius, WeaponSimulation.IsExplosher(weapon) ? weapon.ExplosherFieldInitialRadius : 0);
                    float targetDepth = Mathf.Max(distance, launchDepth + clearanceRadius + .04f + .01f);
                    var target = Box("TPS target", aim.CameraOrigin + aim.Forward * targetDepth, new Vector3(3, 3, .08f));
                    var impacts = new List<InkImpact>();
                    var lastTrace = new Dictionary<uint, string>();
                    match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.ClearFlights();
                    double born = blocks.Now;
                    match.Projectiles.Spawn(player, state, born, match.State.Value.Round);
                    var shots = match.Projectiles.Spawned.ToArray();
                    var resolved = solver.Resolve(player, state, state.LastShotMuzzle);
                    Debug.Log($"[TPS-CONVERGENCE] hero={hero} requestedCameraDepth={distance} targetCameraDepth={targetDepth:F4} muzzle={resolved.Muzzle:F4} blocked={resolved.MuzzleBlocked} aim={resolved.AimPoint:F4} target={target.transform.position:F4} immediateImpacts={match.Projectiles.Impacts.Count}");
                    Assert.That(shots.Length, Is.EqualTo(GameplayConfig.GetWeapon(hero).PelletCount));
                    Assert.That(shots.All(s => s.PostCorrectionVelocity == s.Velocity), Is.True, "No projectile turns after convergence");
                    Assert.That(resolved.MuzzleBlocked, Is.False, "The unobstructed launch fixture must remain physically clear");
                    var expectedTarget = ExpectedCorrection(resolved, weapon, state.LastShotCharge);
                    Assert.That(Vector3.Distance(resolved.CorrectionPoint, expectedTarget), Is.LessThan(.0001f));
                    var expectedDirection = (expectedTarget - resolved.Muzzle).normalized;
                    if (WeaponSimulation.IsExplosher(weapon))
                    {
                        var configuredLaunch = expectedDirection * weapon.SpeedMin;
                        configuredLaunch.y += new Vector2(configuredLaunch.x, configuredLaunch.z).magnitude * weapon.ExplosherUpwardRate;
                        expectedDirection = configuredLaunch.normalized;
                    }
                    Assert.That(Vector3.Angle(shots[0].Velocity, expectedDirection), Is.LessThan(.03f));
                    var previousTrace = match.Projectiles.TraceObserved; var previousContact = match.Projectiles.ContactObserved;
                    match.Projectiles.TraceObserved = (shot, age, point) =>
                    {
                        previousTrace?.Invoke(shot, age, point); lastTrace[shot.Id] = $"id={shot.Id} age={age:F4} point={point:F4}";
                    };
                    match.Projectiles.ContactObserved = (shot, collider, point, normal, age) =>
                    {
                        previousContact?.Invoke(shot, collider, point, normal, age);
                        if (collider == target.GetComponent<Collider>()) impacts.Add(new InkImpact { Position = point, Id = shot.Id });
                    };
                    try
                    {
                        double hitDeadline = Time.realtimeSinceStartupAsDouble + 3;
                        while (impacts.Count < shots.Length && Time.realtimeSinceStartupAsDouble < hitDeadline)
                        { blocks.Tick(); match.Projectiles.Simulate(blocks.Now); FlushPresentation(match); yield return null; }
                        Assert.That(impacts.Count, Is.EqualTo(shots.Length), $"Authoritative target hit hero={hero} distance={distance}; last traces: {string.Join("; ", lastTrace.Values)}");
                        foreach (var impact in impacts)
                        {
                            var shot = shots.Single(value => value.Id == impact.Id);
                            var expected = ExpectedFrontContact(shot, weapon, target.transform.position.z - .04f);
                            Assert.That(Vector3.Distance(impact.Position, expected), Is.LessThan(.003f), "Authority contact follows falling trajectory, not reticle height");
                        }
                    }
                    finally { match.Projectiles.TraceObserved = previousTrace; match.Projectiles.ContactObserved = previousContact; }
                    yield return blocks.Publish();
                    report.Add($"Hero {hero}: requested camera target {distance:F0}m, actual camera depth {targetDepth:F4}m, {shots.Length} authoritative projectiles hit; single launch direction verified");
                    if (hero == 1) Capture(Camera.main, distance <= 6 ? "rifle-near-target" : "rifle-far-target");
                    // EnterPlayMode tests may resume before deferred Destroy is processed.
                    // Remove the old target from physics before resolving the next shot.
                    target.SetActive(false); Physics.SyncTransforms(); Object.Destroy(target); yield return null;
                }
                // Show real RPC-delivered cubes in clear air, at configured speed/spread and gravity.
                SetPose(player, feet + Vector3.up * 4); blocks.PayMain(player, hero);
                var live = player.Snapshot.Value; live.LastShotCharge = 1;
                var miss = solver.Resolve(player, live, live.LastShotMuzzle);
                Assert.That(miss.AimHit.Collider, Is.Null, "Clear-air case has no hit within 100 metres");
                Assert.That(Vector3.Distance(miss.CorrectionPoint, ExpectedCorrection(miss, GameplayConfig.GetWeapon(hero), live.LastShotCharge)), Is.LessThan(.0001f));
                match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.ClearFlights();
                // The real host RPC and LateUpdate use ServerTime, even while manual simulation is paused.
                // Align only the fixed-age evidence sample; do not change payment or terrain simulation time.
                double manualSampleNow = blocks.Now;
                double sampleAt = Math.Max(manualSampleNow, match.NetworkManager.ServerTime.Time);
                match.Projectiles.Spawn(player, live, sampleAt - .05, match.State.Value.Round);
                var flights = match.Projectiles.Spawned.ToArray(); var flight = flights[0]; var w = GameplayConfig.GetWeapon(hero);
                // Fixed-age capture keeps a slow importing Editor from expiring shots.
                FlushPresentation(match, "hero-" + hero + "-clear-air", manualSampleNow, sampleAt, flights);
                int count = flights.Length * BlockFlightPresentation.CubesPerShot(w);
                Assert.That(count, Is.GreaterThanOrEqualTo(w.PelletCount));
                int changed = VisibleFlightPixels(flightPresentation, flights, sampleAt, "hero-" + hero + "-flight", new Vector3(1.8f, .6f, -1.8f));
                visiblePixels += changed;
                // Compare the previous six-metre bend with the new fixed launch direction.
                var solution = solver.Resolve(player, live, live.LastShotMuzzle);
                var centerShot = flight; centerShot.Velocity = solution.InitialDirection * flight.Velocity.magnitude;
                InkBallistics.ApplyCorrection(ref centerShot, solution, w);
                var priorShot = centerShot;
                Vector3 oldDelta = solution.CameraOrigin + solution.Forward * 6 - solution.Muzzle;
                priorShot.Velocity = oldDelta.normalized * centerShot.Velocity.magnitude;
                priorShot.FirstSegmentLength = oldDelta.magnitude;
                priorShot.PostCorrectionVelocity = solution.Forward * centerShot.Velocity.magnitude;
                for (int i = 0; i <= 120; i++)
                {
                    double age = i / 120.0;
                    var oldPoint = InkBallistics.Position(priorShot, w, age) - feet;
                    var newPoint = InkBallistics.Position(centerShot, w, age) - feet;
                    csv.Add(string.Join(",", new[] { hero.ToString(), age.ToString("F6", CultureInfo.InvariantCulture), oldPoint.x.ToString("F6", CultureInfo.InvariantCulture), oldPoint.y.ToString("F6", CultureInfo.InvariantCulture), oldPoint.z.ToString("F6", CultureInfo.InvariantCulture), newPoint.x.ToString("F6", CultureInfo.InvariantCulture), newPoint.y.ToString("F6", CultureInfo.InvariantCulture), newPoint.z.ToString("F6", CultureInfo.InvariantCulture) }));
                }
                report.Add($"Hero {hero}: host RPC + GPU cube instances={count}, visiblePixels={changed}, fallStart={flight.GravityStartAge:F5}s");
            }
            // Sample before the configured camera target, after the weapon straight period.
            // Flush the real host RPC and render in this frame so clock scheduling cannot skip the interval.
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.ClearFlights();
            var gravityWeapon = GameplayConfig.GetWeapon(3);
            float straightRange = InkBallistics.Position(Vector3.zero, Vector3.forward * gravityWeapon.SpeedMin, gravityWeapon, gravityWeapon.StraightSeconds).z;
            float gravityDepth = Mathf.Max(GameplayConfig.Global.AimCorrectionDistance, straightRange * 2);
            // The current Explosher remains straight past six metres; put the camera-hit
            // target beyond that authored interval to retain the before-target gravity test.
            var gravityAim = TpsAimSolver.Geometry(Vector3.up * 20, Vector3.forward, new Vector3(-.6f, 20, 0),
                GameplayConfig.Global.AimCorrectionDistance, GameplayConfig.Global.AimFarCorrectionDistance, gravityDepth);
            var gravityShot = new InkShot { Id = uint.MaxValue, HeroId = 3, Team = player.Snapshot.Value.Team,
                Shooter = ulong.MaxValue, Round = match.State.Value.Round, Seed = 71, Origin = gravityAim.Muzzle,
                Configuration = gravityWeapon, ConfigurationRevision = WeaponConfigService.Current.Revision(3),
                Velocity = gravityAim.InitialDirection * gravityWeapon.SpeedMin };
            InkBallistics.ApplyCorrection(ref gravityShot, gravityAim, gravityWeapon);
            double convergenceAge = InkBallistics.CorrectionAge(gravityShot, gravityWeapon);
            double gravityAge = (gravityShot.GravityStartAge + convergenceAge) * .5;
            Assert.That(gravityAge, Is.GreaterThan(gravityShot.GravityStartAge).And.LessThan(convergenceAge));
            double gravityManualNow = blocks.Now;
            double gravitySampleAt = Math.Max(gravityManualNow, match.NetworkManager.ServerTime.Time);
            gravityShot.Born = gravitySampleAt - gravityAge;
            match.Projectiles.SpawnForMeasurement(gravityShot);
            FlushPresentation(match, "gravity-before-convergence", gravityManualNow, gravitySampleAt, new[] { gravityShot });
            var expectedPosition = InkBallistics.Position(gravityShot, gravityWeapon, gravityAge);
            var gravityCenter = RenderedCenters(flightPresentation, new[] { gravityShot }, gravitySampleAt).Single();
            Assert.That(Vector3.Distance(gravityCenter, expectedPosition), Is.LessThan(.0001f), "Submitted GPU cubes use the complete authority trajectory");
            float travel = (expectedPosition.z - gravityShot.Origin.z) / gravityShot.Velocity.z;
            float withoutGravityY = gravityShot.Origin.y + gravityShot.Velocity.y * travel;
            float drop = withoutGravityY - gravityCenter.y;
            Assert.That(drop, Is.GreaterThan(.001f), "Actual submitted cube centers fall before the camera target");
            visiblePixels += VisibleFlightPixels(flightPresentation, new[] { gravityShot }, gravitySampleAt, "gravity-before-convergence", new Vector3(.6f, .15f, -.6f));
            report.Add($"GPU cubes before convergence: age={gravityAge:F6}s, targetAge={convergenceAge:F6}s, gravityStart={gravityShot.GravityStartAge:F6}s, drop={drop:F6}m; RPC and authority position agree");
            match.Projectiles.Clear(); InkPresentation.Current.Clear();
                // This fixture deliberately backdates subsequent synthetic shots.
                InkPresentation.Current.ClearFlights();
            match.enabled = true; player.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !player.HeroChangePending && player.Snapshot.Value.HeroId == 1, "Return to rifle");
            match.enabled = false; blocks.Now = Math.Max(blocks.Now, player.Snapshot.Value.SimulatedAt);
            SetPose(player, feet);
            var readyState = player.Snapshot.Value; readyState.WeaponPhase = WeaponPhase.Idle; readyState.AttackNeedsRelease = false;
            readyState.NextShotAt = readyState.WeaponReadyAt = readyState.AttackRecoveryUntil = readyState.BurstReadyAt = 0;
            readyState.BurstRemaining = 0; player.Snapshot.Value = readyState;
            var before = player.Snapshot.Value;
            var input = new PlayerInputFrame { Fire = false, FireSequence = before.ConsumedFire, Revision = before.Revision,
                HeroRevision = before.HeroRevision, Look = Vector2.zero, Move = Vector2.right };
            double now = blocks.Now;
            // Switching heroes requires a released input before accepting the next press.
            SimulateInput(player, input, now);
            input.Fire = true; input.FireSequence++;
            for (int tick = 1; tick <= WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(1).StartSeconds) + 3; tick++)
                SimulateInput(player, input, now + tick / 60.0);
            blocks.Now = player.Snapshot.Value.SimulatedAt;
            Assert.That(player.Snapshot.Value.ShotSequence, Is.GreaterThan(before.ShotSequence));
            Assert.That(player.Snapshot.Value.Position.x, Is.GreaterThan(before.Position.x));
            // This fixture disables the normal player rendering loop during manual simulation.
            // Present the newly bound hero once before the existing shot feedback sample.
            // The first Present resets a new view; the following real LateUpdate must retain recoil.
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            player.CharacterView.Shot(0); app.CaptureMouse(true);
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            Assert.That(player.CharacterView.CameraKick.sqrMagnitude, Is.GreaterThan(0));
            var current = player.PresentedState; current.Yaw = player.Look.x; current.Pitch = player.Look.y;
            var expectedAim = solver.Resolve(player, current, current.NextMuzzle);
            var currentWeapon = GameplayConfig.GetWeapon(current.HeroId);
            var guide = WeaponImpactPrediction.Guide(solver, expectedAim, currentWeapon, current, player.PlayerId);
            var projectedAim = TpsAimSolver.ReticleViewport(Camera.main, guide.Point);
            float kickOffset = Vector2.Distance(projectedAim, Vector2.one * .5f);
            Assert.That(kickOffset, Is.GreaterThan(1e-7f), "Camera kick has a measurable projection offset");
            // Test tracking relative to the actual shake, which decays with editor frame time.
            // A fixed centre reticle must fail even when the remaining kick is very small.
            Assert.That(Vector2.Distance(player.ReticleViewport, projectedAim), Is.LessThan(kickOffset * .1f));
            report.Add("Moving production input-frame simulation fires and pays material; camera kick retained; live reticle follows configured ballistic-guide projection");
            var blocker = Box("Muzzle blocker", expectedAim.Muzzle, Vector3.one * .1f);
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            Assert.That(player.MuzzleBlocked, Is.True, "Live HUD obstruction state");
            report.Add("Embedded muzzle updates live HUD obstruction state");
            Capture(Camera.main, "rifle-moving-obstruction");
            blocker.SetActive(false); platform.SetActive(false); Physics.SyncTransforms();
            Object.Destroy(blocker); Object.Destroy(platform);
            yield return null;
            // Drain accepted simulated attacks before replacing diagnostic structures.
            for (int tick = 0; tick < 600 && (match.Projectiles.PendingCount > 0 || match.Blocks.PendingTransactions > 0); tick++)
            { blocks.Tick(); match.Projectiles.Simulate(blocks.Now); FlushPresentation(match); yield return null; }
            Assert.That(match.Projectiles.PendingCount, Is.Zero); yield return blocks.Publish();
            yield return blocks.Seed(blocks.Empty());
            }
            match.enabled = player.enabled = true; flightPresentation.enabled = presentationEnabled;
            // Finish against an actual authored surface, using normal InputSystem,
            // server payment, projectile, collision and score publication paths.
            var surface = match.Arena.Surfaces.Values.Single(value => value.name == "EndBoundary_-1");
            var surfaceCollider = surface.GetComponent<Collider>(); var center = surfaceCollider.bounds.center; center.x = 12;
            Assert.That(surfaceCollider.Raycast(new Ray(center + Vector3.forward * 3, Vector3.back), out var mapHit, 5), Is.True);
            Assert.That(match.Arena.IsInHeroChangeZone(1, mapHit.point) || match.Arena.IsInHeroChangeZone(2, mapHit.point), Is.False);
            using (var rig = new PaidBlockHostPlayRig(app, player))
            {
                yield return rig.Ready(); yield return rig.SelectHero(1);
                int beforeCount = match.Blocks.PinkCount + match.Blocks.BlueCount;
                yield return rig.FireAt(mapHit.point, mapHit.point + mapHit.normal * 4);
                Assert.That(rig.MaterialPaid, Is.GreaterThan(0));
                Assert.That(rig.LastTokens.Sum(token => token.Added), Is.GreaterThan(0));
                Assert.That(match.Blocks.PinkCount + match.Blocks.BlueCount, Is.GreaterThan(beforeCount));
                Assert.That(rig.ContactPositions.Count, Is.GreaterThan(0)); Assert.That(rig.Publications, Is.GreaterThan(0));
                PaidBlockHostPlayRig.AssertStableCollision(match.Blocks);
                Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(match.Blocks.Revision));
                Assert.That(match.PaintSequence, Is.Zero); Assert.That(match.AppliedPaintSequence, Is.Zero);
                report.Add("Corrected real InputSystem shot pays material and builds published cubes on the authored TrainingGround surface; collision and score share the revision");
            }
            File.WriteAllLines(Output + "/trajectories.csv", csv);
            File.WriteAllLines(Output + "/results.txt", report.Append("Total visible changed pixels: " + visiblePixels));
            yield return app.Leave().ToCoroutine();
        }
    }
}
#endif
