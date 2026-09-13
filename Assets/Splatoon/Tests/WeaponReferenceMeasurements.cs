#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    // Controlled geometry uses real Physics casts, projectile code and ownership grids.
    // Measurements describe the project; they are not evidence of original-game equivalence.
    public static class WeaponReferenceMeasurements
    {
        [Serializable] public sealed class Result
        {
            public int weapon, driverHz, shotCount, paintStamps, impacts, peakProjectiles;
            public string scenario, gridHash;
            public float charge, targetPaintRange, lastPaintForward, maxOwnedForward, ownedWidth, ownedDepth, centerlineContinuous;
            public double ownedArea, simulationMilliseconds;
            public bool targetValidated; // No versioned original capture is bundled.
        }
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        static readonly Vector3 Offset = new(1000, 1000, 1000);
        static string F(double value) => value.ToString("R", Culture);
        static string V(Vector3 value) => $"{F(value.x)},{F(value.y)},{F(value.z)}";
        public static void LoadTables() => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
            new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));

        public static List<Result> CaptureAll(string directory)
        {
            if (PrototypeMatch.Current != null) throw new InvalidOperationException("武器测量需要退出房间。");
            Directory.CreateDirectory(directory); LoadTables(); GameplayConfig.Validate();
            var results = new List<Result>();
            string[] scenarios = { "flat", "up30", "down30", "wall-near", "wall-middle", "wall-far", "slope", "high-drop", "occluded" };
            foreach (var w in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                var charges = WeaponSimulation.IsCharge(w) ? new[] { 0f, .5f, 59f / 60, 1f } : new[] { 0f };
                foreach (float charge in charges)
                    foreach (string scenario in scenarios)
                        results.Add(Capture(w.Id, charge, scenario, 60, 1, directory));
                foreach (int rate in new[] { 30, 60, 144 })
                    results.Add(Capture(w.Id, WeaponSimulation.IsCharge(w) ? 1 : 0, "continuous", rate, 20, directory));
            }
            var summary = new StringBuilder("weapon,charge,scenario,driverHz,shots,stamps,impacts,maxOwnedForwardM,ownedWidthM,ownedDepthM,ownedAreaM2,centerlineContinuousM,targetPaintRangeM,targetValidated,gridHash,peakProjectiles,simulationMilliseconds\n");
            foreach (var r in results)
                summary.AppendLine($"{r.weapon},{F(r.charge)},{r.scenario},{r.driverHz},{r.shotCount},{r.paintStamps},{r.impacts},{F(r.maxOwnedForward)},{F(r.ownedWidth)},{F(r.ownedDepth)},{F(r.ownedArea)},{F(r.centerlineContinuous)},{F(r.targetPaintRange)},false,{r.gridHash},{r.peakProjectiles},{F(r.simulationMilliseconds)}");
            File.WriteAllText(Path.Combine(directory, "summary.csv"), summary.ToString());
            File.WriteAllText(Path.Combine(directory, "conditions.txt"),
                "Project simulation measurement, NOT original-game acceptance.\n" +
                "Unity=" + Application.unityVersion + "; playing=" + Application.isPlaying + "\n" +
                "Muzzle=1.5m (high-drop=5m); seeded shots; launch spread disabled to isolate ballistics; grid=0.125m.\n" +
                "Trace includes collision contact at termination; stamp coordinates are actual paint requests.\n" +
                "Continuous=20 automatic/burst shots or 20 fully charged releases using current action timing.\n" +
                "driverHz=external Simulate(now) call frequency in a synchronous loop; NOT actual render FPS. Normal network simulation uses configured fixed ticks.\n" +
                "Width/depth/area are sampled surface ownership, including wall regions; forward range and continuous centerline refer to the horizontal floor.\n" +
                "A blank centerline segment terminates continuity. Targets remain unvalidated: no qualified 11.3.0 original capture.\n" +
                "CSV simulationMilliseconds is synchronous local physics/ownership cost, not gameplay P95 or network performance.\n");
            return results;
        }

        public static Result Capture(int weapon, float charge, string scenario, int rate, int shotCount, string directory)
        {
            var roots = new List<GameObject>(); var surfaces = new Dictionary<int, PaintSurface>();
            var trace = new StringBuilder("shot,ageSeconds,x,y,z\n");
            var stamps = new StringBuilder("surface,x,y,z,normalX,normalY,normalZ,radiusM,hardness,strength\n");
            var w = GameplayConfig.GetHero(weapon);
            var result = new Result { weapon = weapon, charge = charge, scenario = scenario, driverHz = rate, shotCount = shotCount,
                targetPaintRange = WeaponSimulation.IsCharge(w) ? Mathf.Lerp(w.ChargeMinPaintRange, w.PaintRange, charge) : w.PaintRange };
            PaintSurface Surface(int id, Vector3 position, Vector2 size, Quaternion rotation, bool floor)
            {
                var go = new GameObject("Measurement " + id); roots.Add(go); go.SetActive(false);
                go.transform.SetPositionAndRotation(Offset + position, rotation);
                var collider = go.AddComponent<BoxCollider>(); collider.center = Vector3.down * .05f; collider.size = new Vector3(size.x, .1f, size.y);
                var surface = go.AddComponent<PaintSurface>(); surface.enabled = false;
                surface.SurfaceId = id; surface.Scores = floor; surface.WalkableSize = size;
                if (!floor) surface.WallRegions = new[] { new PaintRegion { Id = 1, Size = size, Climbable = true } };
                surface.InitializeOwnership(.125f); go.SetActive(true); surfaces.Add(id, surface); return surface;
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var floor = Surface(1, new Vector3(0, 0, 30), new Vector2(16, 80), Quaternion.identity, true);
                if (scenario.StartsWith("wall-"))
                {
                    float distance = scenario == "wall-near" ? 1 : scenario == "wall-middle" ? 6 : 12;
                    Surface(2, new Vector3(0, 5, distance), new Vector2(16, 10), Quaternion.Euler(-90, 0, 0), false);
                }
                if (scenario == "slope") Surface(2, new Vector3(0, 2, 12), new Vector2(12, 24), Quaternion.Euler(-20, 0, 0), false);
                if (scenario == "occluded")
                {
                    var wall = new GameObject("Unpaintable occluder"); roots.Add(wall); wall.transform.position = Offset + new Vector3(0, 2.5f, 3);
                    wall.AddComponent<BoxCollider>().size = new Vector3(16, 5, .05f);
                }
                Physics.SyncTransforms();
                var service = new InkProjectileService();
                service.TraceObserved = (shot, age, point) => trace.AppendLine($"{shot.Id},{F(age)},{V(point - Offset)}");
                service.PaintObserved = stamp =>
                {
                    result.paintStamps++; result.lastPaintForward = Mathf.Max(result.lastPaintForward, stamp.Position.z - Offset.z);
                    surfaces[stamp.SurfaceId].ApplyRegions(stamp);
                    stamps.AppendLine($"{stamp.SurfaceId},{V(stamp.Position - Offset)},{V(stamp.Normal)},{F(stamp.Radius)},{F(stamp.Hardness)},{F(stamp.Strength)}");
                };
                float angle = scenario == "up30" ? -30 : scenario == "down30" ? 30 : 0;
                var origin = Offset + Vector3.up * (scenario == "high-drop" ? 5 : 1.5f);
                var velocity = Quaternion.Euler(angle, 0, 0) * Vector3.forward * WeaponSimulation.Speed(w, charge);
                int launched = 0; double nextShot = 0;
                double fullDuration = shotCount * (w.StartFrames + w.ChargeFrames + w.FireIntervalFrames + w.BurstRecoveryFrames) / 60.0 + w.Lifetime + 1;
                for (int frame = 0; frame <= (int)Math.Ceiling(fullDuration * rate); frame++)
                {
                    double now = frame / (double)rate;
                    while (launched < shotCount && nextShot <= now + 1e-8)
                    {
                        launched++;
                        service.SpawnForMeasurement(new InkShot { Id = (uint)launched, Seed = (uint)(12345 + launched), Round = 1,
                            HeroId = weapon, Charge = charge, Born = nextShot, Origin = origin, Velocity = velocity, Team = 1, ShotSequence = (uint)launched });
                        int gap = WeaponSimulation.IsCharge(w) ? w.StartFrames + w.ChargeFrames + w.FireIntervalFrames :
                            w.FireMode == 1 && launched % w.BurstCount == 0 ? w.BurstRecoveryFrames : w.FireIntervalFrames;
                        nextShot += gap / 60.0;
                    }
                    result.peakProjectiles = Math.Max(result.peakProjectiles, service.ActiveCount);
                    service.Simulate(now);
                    if (launched == shotCount && service.ActiveCount == 0) break;
                }
                result.impacts = service.Impacts.Count;
                using var hash = SHA256.Create(); using var bytes = new MemoryStream();
                float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
                foreach (var surface in surfaces.Values)
                    foreach (var region in surface.GameplayRegions)
                    {
                        var capture = region.Grid.Capture(); bytes.Write(capture, 0, capture.Length); result.ownedArea += region.Grid.PinkArea;
                        for (int cell = 0; cell < region.Grid.Cells.Length; cell++) if (region.Grid.Cells[cell] == 1)
                        {
                            Vector3 point = region.Matrix(surface).MultiplyPoint3x4(region.Grid.Center(cell)) - Offset;
                            minX = Mathf.Min(minX, point.x - .0625f); maxX = Mathf.Max(maxX, point.x + .0625f);
                            minZ = Mathf.Min(minZ, point.z - .0625f); maxZ = Mathf.Max(maxZ, point.z + .0625f);
                            if (surface == floor) result.maxOwnedForward = Mathf.Max(result.maxOwnedForward, point.z + .0625f);
                        }
                    }
                result.gridHash = BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
                if (result.ownedArea > 0) { result.ownedWidth = maxX - minX; result.ownedDepth = maxZ - minZ; }
                for (float distance = .0625f; distance < 70; distance += .125f)
                {
                    var local = floor.transform.InverseTransformPoint(Offset + Vector3.forward * distance);
                    if (floor.Ownership.At(local) != 1) break;
                    result.centerlineContinuous = distance + .0625f;
                }
                result.simulationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                if (directory != null)
                {
                    string name = $"w{weapon}-q{Mathf.RoundToInt(charge * 60)}-{scenario}-{rate}";
                    File.WriteAllText(Path.Combine(directory, name + ".trace.csv"), trace.ToString());
                    File.WriteAllText(Path.Combine(directory, name + ".paint.csv"), stamps.ToString());
                    File.WriteAllText(Path.Combine(directory, name + ".json"), JsonUtility.ToJson(result, true));
                }
                return result;
            }
            finally { foreach (var go in roots) UnityEngine.Object.DestroyImmediate(go); Physics.SyncTransforms(); }
        }
    }

    public sealed class WeaponReferenceMeasurementTests
    {
        [Test] public void CaptureAllFiveWeaponsWithRealProjectileAndOwnershipCode()
        {
            var results = WeaponReferenceMeasurements.CaptureAll("Logs/WeaponReference/EditMode");
            Assert.That(results.Count, Is.EqualTo(87));
            foreach (var r in results)
            {
                Assert.That(r.impacts, Is.EqualTo(r.shotCount), r.scenario);
                Assert.That(double.IsFinite(r.ownedArea), Is.True); Assert.That(r.targetValidated, Is.False);
            }
            Assert.That(results.Where(r => r.scenario == "flat").All(r => r.paintStamps > 0), Is.True);
        }
        [Test] public void RepeatingMeasurementUsesIdenticalSeededTrajectoryAndCoverage()
        {
            WeaponReferenceMeasurements.LoadTables();
            var a = WeaponReferenceMeasurements.Capture(3, 0, "flat", 60, 1, null);
            var b = WeaponReferenceMeasurements.Capture(3, 0, "flat", 60, 1, null);
            Assert.That(a.gridHash, Is.EqualTo(b.gridHash)); Assert.That(a.paintStamps, Is.EqualTo(b.paintStamps));
        }
        [UnityTest] public IEnumerator PlayModeUsesTheSameMeasuredPhysicsAndCoverage()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while ((PrototypeApp.Current == null || !PrototypeApp.Current.Ready) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (PrototypeApp.Current != null && !string.IsNullOrEmpty(PrototypeApp.Current.Error)) break;
                yield return null;
            }
            Assert.That(PrototypeApp.Current, Is.Not.Null, "Real application bootstrap must run before manually loading measurement tables.");
            Assert.That(PrototypeApp.Current.Ready, Is.True, PrototypeApp.Current.Error);
            Assert.That(LubanConfigService.Current.ContentSignature, Has.Length.EqualTo(32));
            Assert.That(GameplayConfig.GetHero(2).ShotInk, Is.EqualTo(.5f));
            Assert.That(GameplayConfig.GetHero(2).InkRecoverLockFrames, Is.EqualTo(15));
            Assert.That(GameplayConfig.GetHero(3).ShotInk, Is.EqualTo(1.5f));
            Assert.That(GameplayConfig.GetHero(3).FireIntervalFrames, Is.EqualTo(9));
            Assert.That(GameplayConfig.GetHero(3).FireRate, Is.EqualTo(60f / 9).Within(.00001));
            Assert.That(GameplayConfig.GetHero(4).ShotInk, Is.EqualTo(1.1f));
            Assert.That(GameplayConfig.GetHero(4).InkRecoverLockFrames, Is.EqualTo(25));
            Assert.That(GameplayConfig.GetHero(5).Damage, Is.EqualTo(160));
            Assert.That(GameplayConfig.DefaultHero.SwimRecoverInk, Is.EqualTo(100f / 3).Within(.00001));
            Directory.CreateDirectory("Logs/WeaponReference/PlayMode");
            File.WriteAllText("Logs/WeaponReference/PlayMode/addressables.txt",
                "Real PrototypeApp initialization reached Ready; all 9 changed scalar cells verified before measurement LoadTables().\n" +
                "ContentSignature=" + BitConverter.ToString(LubanConfigService.Current.ContentSignature).Replace("-", "").ToLowerInvariant() + "\n");
            var results = WeaponReferenceMeasurements.CaptureAll("Logs/WeaponReference/PlayMode");
            foreach (var r in results)
            {
                string file = $"Logs/WeaponReference/EditMode/w{r.weapon}-q{Mathf.RoundToInt(r.charge * 60)}-{r.scenario}-{r.driverHz}.json";
                Assert.That(File.Exists(file), Is.True, "Run the EditMode measurement first: " + file);
                Assert.That(r.gridHash, Is.EqualTo(JsonUtility.FromJson<WeaponReferenceMeasurements.Result>(File.ReadAllText(file)).gridHash));
                Assert.That(r.impacts, Is.EqualTo(r.shotCount));
            }
            yield return new ExitPlayMode();
        }
    }
}
#endif
