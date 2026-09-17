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
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;
using Splatoon.Networking;

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
            public float charge, lastPaintForward, maxOwnedForward, ownedWidth, ownedDepth, centerlineContinuous, connectedReach;
            public double ownedArea, simulationMilliseconds;
            public double inkSpent, attackSeconds, ownedAreaPerSecond, ownedAreaPer100Ink;
            public int emittedProjectiles;
            public bool fullActions, fullTank;
            public bool targetValidated; // No versioned original capture is bundled.
        }
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        static readonly Vector3 Offset = new(1000, 1000, 1000);
        static string F(double value) => value.ToString("R", Culture);
        static string V(Vector3 value) => $"{F(value.x)},{F(value.y)},{F(value.z)}";
        public static void LoadTables() => HeroMigrationTests.Load();

        public static List<Result> CaptureAll(string directory)
        {
            if (PrototypeMatch.Current != null) throw new InvalidOperationException("武器测量需要退出房间。");
            Directory.CreateDirectory(directory); LoadTables(); GameplayConfig.Validate();
            var results = new List<Result>();
            string[] scenarios = { "flat", "up30", "down30", "wall-near", "wall-middle", "wall-far", "slope", "high-drop", "occluded" };
            foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                var w = GameplayConfig.GetWeapon(hero.Id);
                var charges = WeaponSimulation.IsSplatling(w) ? new[] { HeroFlatRange.MinimumCharge(w), (float)(w.SplatlingFirstChargeSeconds/w.ChargeSeconds), 1f }
                    : WeaponSimulation.IsCharge(w) ? new[] { 0f, .5f, 59f / 60, 1f } : new[] { 0f };
                foreach (float charge in charges)
                    foreach (string scenario in scenarios)
                        results.Add(Capture(hero.Id, charge, scenario, 60, 1, directory, fullActions: true));
                foreach (int rate in new[] { 30, 60, 144 })
                    results.Add(Capture(hero.Id, WeaponSimulation.IsCharge(w) || WeaponSimulation.IsSplatling(w) ? 1 : 0, "continuous", rate, WeaponSimulation.IsSplatling(w) ? 2 : 20, directory, fullActions: true));
            }
            var summary = new StringBuilder("weapon,charge,scenario,driverHz,shots,stamps,impacts,maxOwnedForwardM,ownedWidthM,ownedDepthM,ownedAreaM2,centerlineContinuousM,gridHash,peakProjectiles,simulationMilliseconds,emittedProjectiles,connectedReachM,inkSpent,attackSeconds,areaPerSecond,areaPer100Ink\n");
            foreach (var r in results)
                summary.AppendLine($"{r.weapon},{F(r.charge)},{r.scenario},{r.driverHz},{r.shotCount},{r.paintStamps},{r.impacts},{F(r.maxOwnedForward)},{F(r.ownedWidth)},{F(r.ownedDepth)},{F(r.ownedArea)},{F(r.centerlineContinuous)},{r.gridHash},{r.peakProjectiles},{F(r.simulationMilliseconds)},{r.emittedProjectiles},{F(r.connectedReach)},{F(r.inkSpent)},{F(r.attackSeconds)},{F(r.ownedAreaPerSecond)},{F(r.ownedAreaPer100Ink)}");
            File.WriteAllText(Path.Combine(directory, "summary.csv"), summary.ToString());
            File.WriteAllText(Path.Combine(directory, "conditions.txt"),
                "Project simulation measurement, NOT original-game acceptance.\n" +
                "Unity=" + Application.unityVersion + "; playing=" + Application.isPlaying + "\n" +
                "Formal hero camera/muzzle profiles; real Spawn path with seeded configured spread, alternating muzzles and all pellets; grid=0.125m.\n" +
                "Trace includes collision contact at termination; stamp coordinates are actual paint requests.\n" +
                "Continuous=20 accepted trigger actions (2 full charge magazines for spinner); bubble action=complete group. Accepted-action captures refill ink; fullTank captures never refill.\n" +
                "driverHz=external Simulate(now) call frequency in a synchronous loop; NOT actual render FPS. Normal network simulation uses configured fixed ticks.\n" +
                "Width/depth/area are sampled surface ownership, including wall regions; forward range and continuous centerline refer to the horizontal floor.\n" +
                "Centerline stops at first gap; connectedReach uses 4-neighbor ownership starting within 1.5m of the root. Targets remain unvalidated: no qualified 11.3.0 original capture.\n" +
                "CSV simulationMilliseconds is synchronous local physics/ownership cost, not gameplay P95 or network performance.\n");
            return results;
        }

        public static Result Capture(int weapon, float charge, string scenario, int rate, int shotCount, string directory,
            Action<PaintSurface, Vector3> inspectFloor = null, bool fullActions = false, bool fullTank = false)
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            var roots = new List<GameObject>(); var surfaces = new Dictionary<int, PaintSurface>();
            var trace = new StringBuilder("shot,ageSeconds,x,y,z\n");
            var stamps = new StringBuilder("surface,x,y,z,normalX,normalY,normalZ,radiusM,hardness,strength,directionX,directionY,directionZ,depthScale,shapeSeed\n");
            var w = GameplayConfig.GetWeapon(weapon);
            var result = new Result { weapon = weapon, charge = charge, scenario = scenario, driverHz = rate, shotCount = shotCount, fullActions = fullActions, fullTank = fullTank };
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
                    stamps.AppendLine($"{stamp.SurfaceId},{V(stamp.Position - Offset)},{V(stamp.Normal)},{F(stamp.Radius)},{F(stamp.Hardness)},{F(stamp.Strength)},{V(stamp.Direction)},{F(stamp.DepthScale)},{stamp.ShapeSeed}");
                };
                float angle = scenario == "up30" ? -30 : scenario == "down30" ? 30 : 0;
                var playerRoot = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
                roots.Add(playerRoot); playerRoot.SetActive(false);
                var player = playerRoot.GetComponent<PrototypePlayer>();
                string characterName = GameplayConfig.GetHero(weapon).CharacterPrefabAddress.Split('/').Last();
                player.CharacterView = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/GameResource/Characters/{characterName}/Prefabs/{characterName}Visual.prefab").GetComponent<InkCharacterView>();
                var state = new PlayerSnapshot { HeroId=weapon, Team=1, Health=100, Ink=100, Revision=1, Grounded=true,
                    Position=Offset+Vector3.up*(scenario=="high-drop"?3.5f:.04f), Pitch=angle, LastShotCharge=charge,
                    CurrentSpread=WeaponSimulation.Spread(w,false,charge), BurstShotIndex=1 };
                int launched = 0; double nextShot = 0;
                var timeline = fullActions ? ActionTimeline(w, state, charge, shotCount, fullTank) : null;
                int plannedShots = fullActions ? timeline.Count : shotCount;
                double fullDuration = shotCount * ((WeaponTimeFixture.ReferenceFrames(w.StartSeconds) + WeaponTimeFixture.ReferenceFrames(w.ChargeSeconds) + WeaponTimeFixture.ReferenceFrames(w.BurstRecoverySeconds)) / 60.0 + WeaponSimulation.FireInterval(w)) + w.Lifetime + 1;
                if(fullActions) fullDuration=timeline.Last().time+w.Lifetime+w.PaintDropLifetime+w.WallDropSeconds+1;
                for (int frame = 0; frame <= (int)Math.Ceiling(fullDuration * rate); frame++)
                {
                    double now = frame / (double)rate;
                    while (launched < plannedShots && (fullActions ? timeline[launched].time : nextShot) <= now + 1e-8)
                    {
                        if(fullActions)
                        {
                            var emission=timeline[launched++];state=emission.state;
                            if(scenario=="moving")state.Position+=Vector3.right*(float)(Math.Sin(emission.time*.4)*5);
                            if(scenario=="sweep")state.Yaw=(float)(Math.Sin(emission.time*1.5)*30);
                            service.Spawn(player,state,emission.time,1);continue;
                        }
                        launched++;
                        state.ShotSequence=(uint)launched; state.FireBurstSequence=(uint)launched;
                        state.LastShotMuzzle=(byte)(w.MuzzleMode==WeaponMuzzleMode.AlternatingRightLeft?(launched-1)%2:0);
                        // Explicit per-shot spread, independent of the external projectile driver rate.
                        var spread = WeaponSimulation.IsCharge(w) ? Vector2.one * WeaponSimulation.Spread(w, false, charge)
                            : SpreadSimulation.Angles(w, false, w.SpreadExpandSeconds <= 0 ? 1 : (float)nextShot / w.SpreadExpandSeconds);
                        if (DualiesNormalSimulation.Enabled(w)) state.LastShotSpreadBias = Mathf.Min(w.DualiesSpreadMaxBias, w.DualiesSpreadMinBias + (launched - 1) * w.DualiesSpreadPerShot);
                        if (ReferenceSpreadSimulation.Enabled(w)) state.LastShotSpreadBias=Mathf.Min(w.ReferenceBiasMax,w.ReferenceBiasMin+(launched-1)*w.ReferenceBiasPerShot);
                        if(WeaponSimulation.IsBubble(w))state.BurstShotIndex=(uint)((launched-1)%w.BurstCount+1);
                        state.LastShotSpread = spread.x; state.LastShotVerticalSpread = spread.y;
                        service.Spawn(player,state,nextShot,1);
                        double gap = WeaponSimulation.IsCharge(w) ? WeaponSimulation.Seconds(WeaponTimeFixture.ReferenceFrames(w.StartSeconds) + WeaponTimeFixture.ReferenceFrames(w.ChargeSeconds)) + WeaponSimulation.FireInterval(w) :
                            w.FireMode == WeaponFireMode.Burst && launched % w.BurstCount == 0 ? w.BurstRecoverySeconds : WeaponSimulation.FireInterval(w);
                        nextShot += gap;
                    }
                    result.peakProjectiles = Math.Max(result.peakProjectiles, service.ActiveCount);
                    service.Simulate(now);
                    if (launched == plannedShots && service.PendingCount == 0) break;
                }
                result.emittedProjectiles=launched*w.PelletCount;
                result.inkSpent=fullTank ? 100 - timeline.Last().state.Ink : fullActions ? WeaponSimulation.IsSplatling(w) ? launched*w.ShotInk : shotCount*w.ShotInk : launched*w.ShotInk;
                result.attackSeconds=fullActions?timeline.Last().time+WeaponSimulation.FireInterval(w):nextShot;
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
                result.connectedReach=ConnectedReach(floor,Offset);
                result.ownedAreaPerSecond=result.ownedArea/Math.Max(.00001,result.attackSeconds);
                result.ownedAreaPer100Ink=result.ownedArea/Math.Max(.00001,result.inkSpent)*100;
                inspectFloor?.Invoke(floor, Offset);
                if (directory != null)
                {
                    string name = $"w{weapon}-q{Mathf.RoundToInt(charge * 60)}-{scenario}-{rate}";
                    File.WriteAllText(Path.Combine(directory, name + ".trace.csv"), trace.ToString());
                    File.WriteAllText(Path.Combine(directory, name + ".paint.csv"), stamps.ToString());
                    File.WriteAllText(Path.Combine(directory, name + ".json"), JsonUtility.ToJson(result, true));
                    var grid=floor.Ownership;
                    var texture=new Texture2D(grid.Columns,grid.Rows,TextureFormat.RGBA32,false,true);
                    try
                    {
                        var pixels=new Color32[grid.Cells.Length];
                        for(int i=0;i<pixels.Length;i++)pixels[i]=grid.Cells[i]==1?new Color32(229,47,144,255):new Color32(248,248,248,255);
                        texture.SetPixels32(pixels);texture.Apply(false);
                        File.WriteAllBytes(Path.Combine(directory,name+".coverage.png"),texture.EncodeToPNG());
                    }
                    finally{UnityEngine.Object.DestroyImmediate(texture);}

                }
                return result;
            }
            finally { foreach (var go in roots) UnityEngine.Object.DestroyImmediate(go); Physics.SyncTransforms(); }
        }
        static List<(double time, PlayerSnapshot state)> ActionTimeline(WeaponRuntimeConfig w, PlayerSnapshot state, float charge, int actions, bool fullTank)
        {
            var result=new List<(double,PlayerSnapshot)>();int started=0;
            for(int frame=0;frame<120000;frame++)
            {
                double now=frame/60.0;bool spinner=WeaponSimulation.IsSplatling(w);
                double desiredCharge=Math.Max(w.SplatlingMinChargeSeconds,charge*w.ChargeSeconds);
                if(Math.Abs(desiredCharge-w.SplatlingFirstChargeSeconds)<1e-6)desiredCharge=w.SplatlingFirstChargeSeconds;
                if(Math.Abs(desiredCharge-w.ChargeSeconds)<1e-6)desiredCharge=w.ChargeSeconds;
                bool hold=started<actions;
                if(spinner && SplatlingSimulation.Charging(state) && now+1e-8>=state.ChargeStartedAt+desiredCharge)hold=false;
                bool wasFiring=state.WeaponPhase==WeaponPhase.Firing;
                if (!fullTank) state.Ink=100; // Accepted actions use an unlimited tank; fullTank never refills.
                if(WeaponSimulation.Step(ref state,new PlayerInputFrame{Fire=hold,FireSequence=1,Sequence=(uint)frame+1},w,now,false,true))
                {
                    result.Add((now,state));
                    if(spinner ? !wasFiring : WeaponSimulation.IsBubble(w) ? state.BurstShotIndex==1 : true)started++;
                }
                if(started>=actions && (spinner ? state.SplatlingRemaining==0 : !BubbleVolleySimulation.Pending(state)))return result;
            }
            throw new InvalidOperationException("Weapon action timeline did not finish");
        }
        static float ConnectedReach(PaintSurface floor, Vector3 origin)
        {
            var grid=floor.Ownership;int start=-1;float nearest=float.PositiveInfinity;
            for(int i=0;i<grid.Cells.Length;i++) if(grid.Cells[i]==1)
            {
                var p=floor.transform.TransformPoint(grid.Center(i))-origin;float d=new Vector2(p.x,p.z).sqrMagnitude;
                if(d<nearest && d<=2.25f){nearest=d;start=i;}
            }
            if(start<0)return 0;
            var seen=new bool[grid.Cells.Length];var queue=new Queue<int>();queue.Enqueue(start);seen[start]=true;float reach=0;
            while(queue.Count>0)
            {
                int i=queue.Dequeue();reach=Mathf.Max(reach,(floor.transform.TransformPoint(grid.Center(i))-origin).z+.0625f);
                foreach(int n in new[]{i-1,i+1,i-grid.Columns,i+grid.Columns})
                {
                    if(n<0||n>=grid.Cells.Length||seen[n]||grid.Cells[n]!=1)continue;
                    if(Math.Abs(n-i)==1 && n/grid.Columns!=i/grid.Columns)continue;
                    seen[n]=true;queue.Enqueue(n);
                }
            }
            return reach;
        }
    }

    public sealed class WeaponReferenceMeasurementTests
    {
        [Test] public void CaptureAllSevenWeaponsAndCompleteActionsWithRealPhysicsAndOwnership()
        {
            var results = WeaponReferenceMeasurements.CaptureAll("Reports/WeaponReference/EditMode");
            Assert.That(results.Count, Is.EqualTo(102));
            foreach (var r in results)
            {
                Assert.That(r.impacts, Is.EqualTo(r.emittedProjectiles), r.scenario);
                Assert.That(double.IsFinite(r.ownedArea), Is.True); Assert.That(r.targetValidated, Is.False);
            }
            Assert.That(results.Where(r => r.scenario == "flat").All(r => r.paintStamps > 0), Is.True);
            Directory.CreateDirectory("Reports/CombatGirls/FourHeroes");
            File.WriteAllText("Reports/CombatGirls/FourHeroes/paint-measurements.json", "["+string.Join(",",results.Where(r=>r.weapon>=2&&r.weapon<=4&&r.scenario=="flat").Select(r=>$"{{\"id\":{r.weapon},\"range\":{r.maxOwnedForward.ToString("R",CultureInfo.InvariantCulture)},\"method\":\"Physics Spawn + 0.125m ownership grid, fixed seeded volley\"}}"))+"]");
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
            Assert.That(GameplayConfig.GetWeapon(2).ShotInk, Is.EqualTo(1.4f));
            Assert.That(WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(2).InkRecoverLockSeconds), Is.EqualTo(20));
            Assert.That(GameplayConfig.GetWeapon(3).ShotInk, Is.EqualTo(4));
            Assert.That(GameplayConfig.GetWeapon(3).FireRate, Is.EqualTo(2.5f));
            Assert.That(GameplayConfig.GetWeapon(3).PelletCount, Is.EqualTo(8));
            Assert.That(GameplayConfig.GetWeapon(4).ShotInk, Is.EqualTo(.8f));
            Assert.That(WeaponTimeFixture.ReferenceFrames(GameplayConfig.GetWeapon(4).InkRecoverLockSeconds), Is.EqualTo(20));
            Assert.That(GameplayConfig.GetWeapon(5).FireMode, Is.EqualTo(WeaponFireMode.Blaster));
            Assert.That(GameplayConfig.GetWeapon(5).Damage, Is.EqualTo(85));
            Assert.That(GameplayConfig.DefaultHero.SwimRecoverInk, Is.EqualTo(100f / 3).Within(.00001));
            Directory.CreateDirectory("Reports/WeaponReference/PlayMode");
            File.WriteAllText("Reports/WeaponReference/PlayMode/addressables.txt",
                "Real PrototypeApp initialization reached Ready; all 9 changed scalar cells verified before measurement LoadTables().\n" +
                "ContentSignature=" + BitConverter.ToString(LubanConfigService.Current.ContentSignature).Replace("-", "").ToLowerInvariant() + "\n");
            var results = WeaponReferenceMeasurements.CaptureAll("Reports/WeaponReference/PlayMode");
            foreach (var r in results)
            {
                string file = $"Reports/WeaponReference/EditMode/w{r.weapon}-q{Mathf.RoundToInt(r.charge * 60)}-{r.scenario}-{r.driverHz}.json";
                Assert.That(File.Exists(file), Is.True, "Run the EditMode measurement first: " + file);
                Assert.That(r.gridHash, Is.EqualTo(JsonUtility.FromJson<WeaponReferenceMeasurements.Result>(File.ReadAllText(file)).gridHash));
                Assert.That(r.impacts, Is.EqualTo(r.emittedProjectiles));
            }
            yield return new ExitPlayMode();
        }
    }
}
#endif
