#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // INSTALLABLE REVIEW DRAFT ONLY. No compilation or execution is claimed.
    // Actual NGO Host/Bot + a separately initialized production BlockTerrain.
    // This fixture does not test match.Blocks synchronization or remote Players.
    [Category("BlockFixedGridActualSafety")]
    public sealed class BlockFixedGridActualSafetyPlayTests
    {
        const float Dt = 1f / 60;
        const string Prefab = "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab";
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo[] SnapshotFields = typeof(PlayerSnapshot).GetFields(BindingFlags.Instance | BindingFlags.Public);
        PrototypeMatch match;
        PrototypeApp cursorApp;
        PrototypePlayer host;
        PrototypeArena arena;
        BlockTerrain terrain;
        GameObject authoredRoot;
        BoxCollider bridge;
        readonly List<PrototypePlayer> actors = new();
        readonly List<(Behaviour component, bool enabled)> paused = new();
        readonly List<(Collider collider, bool enabled)> oldMap = new();
        readonly List<Action> detach = new();
        readonly List<string> trace = new();
        Action serverTick, sendInput;
        bool networkUpdatesDetached;
        double now;
        float bridgeTop;
        int commits, publications, effects, payments;
        readonly List<(PrototypePlayer player, BlockTerrain.SafetyRecoveryObservation observation)> recoveries = new();
        BlockFixedGridState[] expectedRestack = Array.Empty<BlockFixedGridState>();
        Exception observerFailure;

        static void AssertSystemCursorSuppressed()
        {
            // This must already be true BEFORE Boot/PrototypeApp initialization.
            // The production suppression flag is cached; do not patch it or set
            // an environment variable after that cache has been initialized.
            var property = typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, "Actual production cursor suppression contract");
            Assert.That((bool)property.GetValue(null), Is.True,
                "PRECONDITION BLOCKED: isolated Editor must launch with system-cursor writes suppressed before PrototypeApp initializes");
        }

        void AssertNoSystemCursorWrites()
        {
            AssertSystemCursorSuppressed();
            Assert.That(cursorApp, Is.Not.Null);
            Assert.That(cursorApp.SystemCursorWriteCount, Is.EqualTo(0),
                "Automated fixture must perform zero system-cursor writes, including release writes");
        }

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 60;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Boot()
        {
            trace.Clear(); recoveries.Clear(); actors.Clear(); paused.Clear(); oldMap.Clear(); detach.Clear();
            commits = publications = effects = payments = 0; observerFailure = null;
            cursorApp = null;
            expectedRestack = Array.Empty<BlockFixedGridState>();
            AssertSystemCursorSuppressed();
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Real Boot/config startup");
            cursorApp = PrototypeApp.Current; AssertNoSystemCursorWrites();
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypePlayer.Local.IsSpawned &&
                PrototypePlayer.Local.SwimBody?.Profile != null && PrototypeMatch.Current?.Blocks?.FixedGridMode == true,
                "Real spawned Host, formal Paper profile and fixed production mode");
            host = PrototypePlayer.Local; match = PrototypeMatch.Current;
            Assert.That(match.IsHost && match.IsServer && match.NetworkManager.IsListening, Is.True);
            Assert.That(match.State.Value.Phase, Is.EqualTo(MatchPhase.Practice));
            Assert.That(match.Blocks.Dynamic, Is.Null);
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f));
            Assert.That(BlockWorld.Top, Is.EqualTo(8f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4));
            Assert.That(BlockFixedGridWorld.MaximumStackHeight, Is.EqualTo(1.2f));
            Assert.That(PrototypePlayer.ByOwner.Values.Count(p => p != null && p.IsSpawned), Is.EqualTo(1));
            // Change only logical capture intent; production suppression must
            // prevent even a native release write in automated tests.
            PrototypeApp.Current.CaptureMouse(false);
            AssertNoSystemCursorWrites();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            for (int i = detach.Count - 1; i >= 0; i--) detach[i]();
            detach.Clear();
            RestoreAutomaticUpdates();
            foreach (var item in oldMap) if (item.collider != null) item.collider.enabled = item.enabled;
            oldMap.Clear();
            int cursorWritesBeforeLeave = ReferenceEquals(cursorApp, null) ? 0 : cursorApp.SystemCursorWriteCount;
            if (Application.isPlaying && PrototypeApp.Current != null)
                yield return PrototypeApp.Current.Leave("65 actual safety fixture cleanup").ToCoroutine();
            int cursorWritesAfterLeave = ReferenceEquals(cursorApp, null) ? 0 : cursorApp.SystemCursorWriteCount;
            if (trace.Count != 0)
            {
                string directory = "Reports/BlockSimulation20261018/Staged65/Acceptance/Safety/Runs";
                Directory.CreateDirectory(directory);
                trace.Add(JsonUtility.ToJson(new EndRecord { stage = "end", test = TestContext.CurrentContext.Test.Name,
                    result = TestContext.CurrentContext.Result.Outcome.ToString(), message = TestContext.CurrentContext.Result.Message,
                    cursorWritesBeforeLeave = cursorWritesBeforeLeave, cursorWritesAfterLeave = cursorWritesAfterLeave }));
                File.WriteAllLines(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                    TestContext.CurrentContext.Test.Name + ".jsonl"), trace);
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(cursorWritesBeforeLeave, Is.EqualTo(0), "Zero system-cursor writes through actual gameplay fixture");
            Assert.That(cursorWritesAfterLeave, Is.EqualTo(0), "Actual Leave also must not write the shared system cursor");
        }

        void PauseAutomaticUpdates()
        {
            actors.Clear();
            actors.AddRange(PrototypePlayer.ByOwner.Values.Where(p => p != null && p.IsSpawned));
            Assert.That(actors.All(p => p.IsServer && p.Snapshot.Value.IsAlive), Is.True);
            Assert.That(actors.All(p => p.Snapshot.Value.HeroId == host.Snapshot.Value.HeroId), Is.True, "This fixture uses the actual Host hero/profile for all actors");
            void Pause(Behaviour component)
            { paused.Add((component, component.enabled)); component.enabled = false; }
            Pause(match); Pause(match.Blocks);
            foreach (var player in actors) Pause(player);
            serverTick = (Action)Delegate.CreateDelegate(typeof(Action), match,
                typeof(PrototypeMatch).GetMethod("ServerTick", Private));
            match.NetworkManager.NetworkTickSystem.Tick -= serverTick;
            sendInput = (Action)Delegate.CreateDelegate(typeof(Action), host,
                typeof(PrototypePlayer).GetMethod("SendInput", Private));
            match.NetworkManager.NetworkTickSystem.Tick -= sendInput;
            match.UnregisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
            match.UnregisterNetworkUpdate(NetworkUpdateStage.Update);
            networkUpdatesDetached = true;
            Assert.That(match.Projectiles.LiveShots().Any(), Is.False, "No old attacks may enter the independent world");
            foreach (var collider in PrototypeArena.Current.GetComponentsInChildren<Collider>(true))
            { oldMap.Add((collider, collider.enabled)); collider.enabled = false; }
            now = Math.Max(host.Snapshot.Value.SimulatedAt, match.NetworkManager.ServerTime.Time);
        }

        void RestoreAutomaticUpdates()
        {
            if (match != null && match.IsSpawned && match.NetworkManager?.NetworkTickSystem != null)
            {
                if (serverTick != null) match.NetworkManager.NetworkTickSystem.Tick += serverTick;
                if (sendInput != null && host != null && host.IsSpawned) match.NetworkManager.NetworkTickSystem.Tick += sendInput;
                if (networkUpdatesDetached)
                { match.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate); match.RegisterNetworkUpdate(NetworkUpdateStage.Update); }
            }
            serverTick = sendInput = null; networkUpdatesDetached = false;
            foreach (var item in paused) if (item.component != null) item.component.enabled = item.enabled;
            paused.Clear();
        }

        // Map authoring is complete before activation/Awake and terrain.Initialize.
        // ThickShell's narrow vertical aperture admits a .30m cube, never a capsule.
        void CreateWorld(bool thickShell = false)
        {
            PauseAutomaticUpdates();
            authoredRoot = new GameObject("65 actual safety authored map"); authoredRoot.SetActive(false);
            arena = authoredRoot.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(16, 16); arena.OwnershipCellSize = GameplayConfig.Map.CellSize;
            arena.SpawnPoints = new Transform[8];
            var chamberCenter = new Vector3(.15f, .04f, .15f);
            var standing = HeroBodyShape.For(host.Snapshot.Value.HeroId);
            bridgeTop = Mathf.Max(2.1f, Mathf.CeilToInt((standing.Height + .30f - .00001f) / BlockWorld.CellSize) * BlockWorld.CellSize);
            Assert.That(bridgeTop + 2 * BlockWorld.CellSize, Is.LessThan(BlockWorld.Top));
            for (int i = 0; i < arena.SpawnPoints.Length; i++)
            {
                var spawn = new GameObject("65 safety spawn " + i).transform;
                spawn.SetParent(authoredRoot.transform, false);
                spawn.position = thickShell ? chamberCenter : new Vector3(i % 4 - 1.5f, .04f, i < 4 ? -3 : 3);
                arena.SpawnPoints[i] = spawn;
            }
            Solid("65 floor", new Vector3(0, -2, 0), new Vector3(12, 4, 12), 6511);
            if (!thickShell) bridge = Solid("65 removable bridge", new Vector3(0, bridgeTop - .05f, 0), new Vector3(8, .1f, 8), 6512);
            else
            {
                var shape = HeroBodyShape.For(host.Snapshot.Value.HeroId);
                float width = shape.Radius * 2 + .20f;
                Assert.That(width, Is.GreaterThan(.32f)); Assert.That(width, Is.LessThan(.95f));
                float low = .15f - width * .5f, high = .15f + width * .5f;
                Solid("65 thick west shell", new Vector3((low - 6) * .5f, 2, .15f), new Vector3(low + 6, 12, 12));
                Solid("65 thick east shell", new Vector3((high + 6) * .5f, 2, .15f), new Vector3(6 - high, 12, 12));
                Solid("65 thick south shell", new Vector3(.15f, 2, (low - 6) * .5f), new Vector3(width, 12, low + 6));
                Solid("65 thick north shell", new Vector3(.15f, 2, (high + 6) * .5f), new Vector3(width, 12, 6 - high));
                float ceiling = shape.Height + .08f, top = 8.1f;
                float holeLow = -.01f, holeHigh = .31f, middleY = (ceiling + top) * .5f;
                Solid("65 roof west", new Vector3((holeLow - 6) * .5f, middleY, 0), new Vector3(holeLow + 6, top - ceiling, 12));
                Solid("65 roof east", new Vector3((holeHigh + 6) * .5f, middleY, 0), new Vector3(6 - holeHigh, top - ceiling, 12));
                Solid("65 roof south", new Vector3(.15f, middleY, (holeLow - 6) * .5f), new Vector3(.32f, top - ceiling, holeLow + 6));
                Solid("65 roof north", new Vector3(.15f, middleY, (holeHigh + 6) * .5f), new Vector3(.32f, top - ceiling, 6 - holeHigh));
                bridge = Solid("65 aperture removable bridge", new Vector3(.15f, bridgeTop - .05f, .15f), new Vector3(.30f, .1f, .30f), 6512);
                Assert.That(.32f, Is.LessThan(2 * (shape.Radius - Mathf.Max(.005f, shape.SkinWidth))), "Capsule cannot fit the cube aperture");
            }
            arena.BakedTopology = arena.ComputeTopology(); authoredRoot.SetActive(true); arena.InitializeRuntime();
            Assert.That(arena.TotalArea, Is.GreaterThan(0));
            terrain = new GameObject("65 independent actual safety terrain").AddComponent<BlockTerrain>();
            terrain.Initialize(arena); terrain.EnableFixedGridMode(); terrain.enabled = false;
            terrain.Tick(true, now, Dt); Physics.SyncTransforms();
            Assert.That(ReferenceEquals(BlockTerrain.Current, terrain) && ReferenceEquals(PrototypeArena.Current, arena), Is.True);
            Assert.That(ReferenceEquals(match.Blocks, terrain), Is.False, "Independent production guard fixture, not synchronization evidence");
            Assert.That(terrain.IsAuthoredMapCollider(bridge), Is.True);
            Assert.That(terrain.FixedBlocks.Environment.GetType().FullName, Does.Contain("BlockTerrain+FixedEnvironment"));
        }

        BoxCollider Solid(string name, Vector3 center, Vector3 size, int paintId = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.SetParent(authoredRoot.transform, false); go.transform.position = center;
            var mesh = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], size);
            mesh.vertices = vertices; mesh.RecalculateBounds(); go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var box = go.GetComponent<BoxCollider>(); box.size = size;
            if (paintId != 0)
            {
                var paint = go.AddComponent<PaintSurface>(); paint.SurfaceId = paintId;
                paint.Scores = true; paint.WalkableSize = new Vector2(size.x, size.z);
                paint.ShapeAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
                Assert.That(paint.ShapeAtlas, Is.Not.Null);
            }
            return box;
        }

        void Place(PrototypePlayer player, Vector3 position)
        {
            Assert.That(player.IsSpawned && match.Players.Contains(player), Is.True);
            typeof(PrototypePlayer).GetMethod("DiagnosticPlace", Private).Invoke(player, new object[] { position, 0f });
            Assert.That(player.Snapshot.Value.Position, Is.EqualTo(position));
            Physics.SyncTransforms();
        }

        void StepAll(bool swimHost = false, uint? jumpHost = null)
        {
            AssertNoSystemCursorWrites();
            now = Math.Max(now, actors.Max(p => p.Snapshot.Value.SimulatedAt)) + Dt;
            terrain.Tick(true, now, Dt);
            foreach (var player in actors)
            {
                var s = player.Snapshot.Value;
                var frame = new PlayerInputFrame { Revision = s.Revision, HeroRevision = s.HeroRevision,
                    Sequence = s.AcknowledgedInput + 1, Tick = s.SimulationTick + 1,
                    Look = new Vector2(s.Yaw, s.Pitch), Swim = player == host && swimHost,
                    JumpSequence = player == host && jumpHost.HasValue ? jumpHost.Value : s.ConsumedJump,
                    FireSequence = s.ConsumedFire, ReleaseSequence = s.ConsumedRelease, InteractSequence = s.ConsumedInteract,
                    SubPressSequence = s.SubConsumedPress, SubReleaseSequence = s.SubConsumedRelease,
                    SpecialSequence = s.SpecialConsumed };
                var queue = (SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(player);
                queue.Clear(); queue.Add(frame.Sequence, frame);
                typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(player, player.NetworkManager.ServerTime.Time);
                var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(player);
                Assert.That(motor, Is.Not.Null);
                int observerErrorsBefore = PlayerMotorSimulation.MotorBranchObserverErrors;
                var completions = new List<PlayerMotorSimulation.MotorBranchObservation>();
                void ObserveStep(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
                {
                    if (ReferenceEquals(source, motor) && observation.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete)
                        completions.Add(observation);
                }
                PlayerMotorSimulation.MotorBranchObserved += ObserveStep;
                try { player.Simulate(Dt, now, match.State.Value.Phase); }
                finally { PlayerMotorSimulation.MotorBranchObserved -= ObserveStep; }
                var after = player.Snapshot.Value;
                trace.Add(JsonUtility.ToJson(new ManualStepRecord { stage = "actual-single-outer-step", id = player.PlayerId,
                    before = s, after = after, input = frame, requestedTime = now, motorCompleteCount = completions.Count,
                    observerErrorsBefore = observerErrorsBefore, observerErrorsAfter = PlayerMotorSimulation.MotorBranchObserverErrors }));
                Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors, Is.EqualTo(observerErrorsBefore),
                    "A swallowed motor observer fault cannot count as a completed step");
                Assert.That(completions.Count, Is.EqualTo(1), "Each manual authority Simulate must execute exactly one actual Motor.Step");
                Assert.That(after.SimulationTick, Is.EqualTo(s.SimulationTick + 1), "The outer simulation tick advances exactly once");
                Assert.That(after.SimulationTick, Is.EqualTo(frame.Tick));
                Assert.That(after.SimulatedAt, Is.EqualTo(now), "The outer simulation uses this helper's single authoritative time");
                Assert.That(after.SimulatedAt, Is.GreaterThan(s.SimulatedAt));
                Assert.That(completions[0].SimulationTick, Is.EqualTo(after.SimulationTick));
                Assert.That(completions[0].SimulatedAt, Is.EqualTo(now));
                Assert.That(completions[0].Input.Sequence, Is.EqualTo(frame.Sequence));
                Assert.That(after.AcknowledgedInput, Is.EqualTo(frame.Sequence));
                Assert.That(after.InputTimedOut, Is.False);
                AssertNoSystemCursorWrites();
                Record("manual-input", player, frame);
            }
            Physics.SyncTransforms();
        }

        RaycastHit MapHit(Vector3 origin, Collider required)
        {
            var hits = Physics.RaycastAll(origin, Vector3.down, 5, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            var matches = hits.Where(h => h.collider == required).OrderBy(h => h.distance).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1), "Actual pre-registered map face"); return matches[0];
        }
        void PaintFloor(Vector2Int column, byte team)
        {
            var center = BlockWorld.Center(new Vector3Int(column.x, 0, column.y));
            var floor = authoredRoot.transform.Find("65 floor").GetComponent<BoxCollider>();
            var hit = MapHit(new Vector3(center.x, 1, center.z), floor);
            Assert.That(terrain.TryGetSurfacePaint(hit.collider, hit.point, hit.normal, out var paint), Is.True);
            if (paint.Owner != team)
                Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, team, 1, true).Added, Is.EqualTo(1));
            Assert.That(terrain.SurfacePaints.TryGet(paint.Key, out var current) && current.Owner == team, Is.True);
        }
        BlockFixedGridState[] BuildBridgeColumn(Vector2Int column, byte owner)
        {
            PaintFloor(column, owner);
            var center = BlockWorld.Center(new Vector3Int(column.x, BlockFixedGridMapSupport.GridBaseY(bridgeTop), column.y));
            var hit = MapHit(new Vector3(center.x, bridgeTop + 1.4f, center.z), bridge);
            Assert.That(hit.point.y, Is.EqualTo(bridgeTop).Within(.00001f));
            Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, owner, 1, true).Added, Is.EqualTo(1), "Blank bridge first receives paint");
            Assert.That(terrain.BuildFixedImpact(hit.point, hit.normal, 0, owner, 1, true).Added, Is.EqualTo(1), "Own paint creates the bottom fixed block");
            var bottom = new Vector3Int(column.x, BlockFixedGridMapSupport.GridBaseY(hit.point.y), column.y);
            Assert.That(terrain.FixedBlocks.TryGet(bottom, out var first), Is.True);
            Vector3 upperHit = BlockWorld.Center(bottom) + Vector3.up * (BlockWorld.CellSize * .5f);
            Assert.That(terrain.BuildFixedImpact(upperHit, Vector3.up, 0, owner, 1, false).Added, Is.EqualTo(1));
            Assert.That(terrain.FixedBlocks.TryGet(bottom + Vector3Int.up, out var second), Is.True);
            Assert.That(first.Owner == owner && second.Owner == owner, Is.True);
            return new[] { first, second };
        }

        void Observe()
        {
            commits = publications = effects = payments = 0; recoveries.Clear(); observerFailure = null;
            Action<BlockFixedGridCommit> committed = commit =>
            {
                commits++;
                try
                {
                    Assert.That(terrain.World, Is.SameAs(terrain.FixedBlocks.Storage));
                    Assert.That(terrain.SurfacePaints, Is.SameAs(terrain.FixedBlocks.Surfaces));
                    AssertRelocated(expectedRestack);
                    foreach (var actor in actors) AssertActualPose(actor);
                    Record("committed-observer");
                }
                catch (Exception exception) { observerFailure ??= exception; }
            };
            Action<BlockTerrainCommit> published = commit =>
            {
                publications++;
                try { AssertRelocated(expectedRestack); foreach (var actor in actors) AssertActualPose(actor); Record("published-observer"); }
                catch (Exception exception) { observerFailure ??= exception; }
            };
            Action<BlockTerrain, PrototypePlayer, BlockTerrain.SafetyRecoveryObservation> recovered = (source, player, observation) =>
            {
                if (source != terrain) return;
                recoveries.Add((player, observation));
                try { AssertRelocated(expectedRestack); foreach (var actor in actors) AssertActualPose(actor); Record("safety-observer"); }
                catch (Exception exception) { observerFailure ??= exception; }
            };
            Action<BlockAttackObservation.ActualResult> actual = result => { effects++; };
            Action<BlockAttackObservation.Payment> payment = value => { payments++; };
            terrain.FixedBlocks.Committed += committed; terrain.PublishedCommit += published;
            BlockTerrain.SafetyRecoveryObserved += recovered;
            BlockAttackObservation.ActualResultCommitted += actual; BlockAttackObservation.PaymentAccepted += payment;
            detach.Add(() => terrain.FixedBlocks.Committed -= committed); detach.Add(() => terrain.PublishedCommit -= published);
            detach.Add(() => BlockTerrain.SafetyRecoveryObserved -= recovered);
            detach.Add(() => BlockAttackObservation.ActualResultCommitted -= actual);
            detach.Add(() => BlockAttackObservation.PaymentAccepted -= payment);
        }

        Bounds RemoveBridge()
        { var bounds = bridge.bounds; bridge.enabled = false; Physics.SyncTransforms(); return bounds; }

        void AssertRelocated(BlockFixedGridState[] original)
        {
            foreach (var group in original.GroupBy(s => new Vector2Int(s.Cell.x, s.Cell.z)))
            {
                int y = 0;
                foreach (var before in group.OrderBy(s => s.Cell.y))
                {
                    Assert.That(terrain.FixedBlocks.TryGet(before.Handle, out var after), Is.True);
                    Assert.That(after.Handle, Is.EqualTo(before.Handle)); Assert.That(after.Owner, Is.EqualTo(before.Owner));
                    Assert.That(after.Cell, Is.EqualTo(new Vector3Int(before.Cell.x, y++, before.Cell.z)));
                    Assert.That(terrain.FixedBlocks.TryGet(before.Cell, out _), Is.False);
                    Assert.That(after.Revision, Is.EqualTo(terrain.Revision));
                }
            }
            Assert.That(terrain.FixedBlocks.LastRelocatedCount, Is.EqualTo(original.Length));
            Assert.That(terrain.FixedBlocks.LastConvertedCount, Is.Zero);
        }

        void AssertOneSuccessfulCommit(int expectedActors, int expectedRecoveries)
        {
            Assert.That(observerFailure, Is.Null, observerFailure?.ToString());
            Assert.That(commits, Is.EqualTo(1)); Assert.That(publications, Is.EqualTo(1));
            Assert.That(effects, Is.EqualTo(1)); Assert.That(payments, Is.Zero, "Reconcile is not a paid attack");
            Assert.That(recoveries.Count, Is.EqualTo(expectedRecoveries));
            Assert.That(recoveries.All(r => r.observation.Recovered && r.observation.TerrainRevision == terrain.Revision), Is.True);
            Assert.That(recoveries.Select(r => r.player).Distinct().Count(), Is.EqualTo(expectedActors));
            foreach (var recovery in recoveries)
            {
                AssertFields(recovery.player.Snapshot.Value, recovery.observation.After, "Observer final snapshot");
                Assert.That(recovery.observation.Round, Is.EqualTo(terrain.Round));
            }
            foreach (var actor in actors) AssertActualPose(actor);
        }

        [UnityTest, Timeout(600000)] public IEnumerator RestackRecoversActualStandingPlayerBeforeCommitIsObserved()
        {
            CreateWorld(); Place(host, new Vector3(.15f, .04f, .15f));
            for (int i = 0; i < 8; i++) StepAll();
            Assert.That(host.Snapshot.Value.Grounded && !host.Snapshot.Value.ShowsSwimBody, Is.True);
            var blocks = BuildBridgeColumn(new Vector2Int(0, 0), host.Snapshot.Value.Team);
            terrain.Tick(true, now, Dt); AssertActualPose(host);
            var before = new ActorStamp(host); var world = new WorldStamp(terrain);
            Assert.That(CapsuleHits(before.State, new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 1, 0) }), Is.True);
            expectedRestack = blocks; Observe(); uint revision = terrain.Revision; int count = terrain.SafetyRecoveries;
            var changed = RemoveBridge(); Record("before-restack");
            Assert.That(terrain.NotifyFixedMapSupportChanged(changed), Is.True, terrain.FixedBlocks.LastFailure);
            Assert.That(terrain.Revision, Is.EqualTo(revision + 1)); AssertRelocated(blocks);
            Assert.That(terrain.FixedBlocks.PinkCount, Is.EqualTo(world.Pink)); Assert.That(terrain.FixedBlocks.BlueCount, Is.EqualTo(world.Blue));
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(count + 1));
            Assert.That(recoveries.Single().observation.CapsuleEmbedded, Is.True);
            AssertFields(before.State, recoveries.Single().observation.Before, "Observer original snapshot");
            AssertRecoveryFields(before.State, host.Snapshot.Value);
            AssertOneSuccessfulCommit(1, 1); Record("after-restack"); yield break;
        }

        [UnityTest, Timeout(600000)] public IEnumerator RestackRecoversActualAirPaperUsingItsExactCandidateContour()
        {
            CreateWorld(); Place(host, new Vector3(.15f, .04f, .15f));
            byte owner = host.Snapshot.Value.Team;
            // Only genuine floor paint; no fabricated Air/Swimming snapshot.
            for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) PaintFloor(new Vector2Int(x, z), owner);
            for (int i = 0; i < 8; i++) StepAll();
            for (int i = 0; i < 8; i++) StepAll(true);
            Assert.That(host.Snapshot.Value.Grounded && host.Snapshot.Value.Swimming, Is.True, "Real own-paint swim entry");
            uint jump = host.Snapshot.Value.ConsumedJump + 1;
            bool found = false; Vector2Int witness = default; Rect[] witnessRects = null;
            using (var oracle = new PaperCapture(host.SwimBody.Profile))
            {
                for (int tick = 0; tick < 180 && !found; tick++)
                {
                    StepAll(true, jump); var s = host.Snapshot.Value;
                    if (!s.ShowsSwimBody || s.Grounded || s.Movement != MovementMode.Air || s.PaperPose != PaperPose.Air) continue;
                    oracle.SampleUncachedForDiagnostics(s); var rects = oracle.HitRects.ToArray();
                    Assert.That(rects.Length, Is.GreaterThan(4));
                    Assert.That(host.SwimBody.HitRects, Is.EqualTo(rects), "Independent uncached full contour agrees with actual published key");
                    if (!PoseFits(s, terrain.FixedBlocks.Blocks.Values.Select(b => b.Cell), rects)) continue;
                    for (int x = -4; x <= 4 && !found; x++) for (int z = -4; z <= 4 && !found; z++)
                    {
                        var cells = new[] { new Vector3Int(x, 0, z), new Vector3Int(x, 1, z) };
                        if (CapsuleHits(s, cells) || !PaperHits(s, rects, cells)) continue;
                        // The initial real bridge must also clear the compact capsule and contour.
                        int bridgeY = BlockFixedGridMapSupport.GridBaseY(bridgeTop);
                        if (CapsuleHits(s, new[] { new Vector3Int(x, bridgeY, z), new Vector3Int(x, bridgeY + 1, z) }) ||
                            PaperHits(s, rects, new[] { new Vector3Int(x, bridgeY, z), new Vector3Int(x, bridgeY + 1, z) })) continue;
                        witness = new Vector2Int(x, z); witnessRects = rects; found = true;
                    }
                    Record(found ? "real-air-witness-found" : "real-air-witness-search");
                }
            }
            Assert.That(found, Is.True, "Strong prerequisite: real input must expose a capsule-clear full-contour witness; no Ignore/fake state fallback");
            trace.Add(JsonUtility.ToJson(new WitnessRecord { stage = "independent-real-air-witness", column = witness,
                snapshot = host.Snapshot.Value, rects = witnessRects, profileHash = host.SwimBody.Profile.ContentHash }));
            var blocks = BuildBridgeColumn(witness, owner); terrain.Tick(true, now, Dt);
            Assert.That(host.SwimBody.Profile.CapturePrefab, Is.Not.Null);
            Assert.That(host.SwimBody.Profile.ContentHash, Is.Not.Null.And.Not.Empty);
            var before = new ActorStamp(host); AssertActualPose(host);
            var future = new[] { new Vector3Int(witness.x, 0, witness.y), new Vector3Int(witness.x, 1, witness.y) };
            Assert.That(CapsuleHits(before.State, future), Is.False); Assert.That(PaperHits(before.State, witnessRects, future), Is.True);
            expectedRestack = blocks; Observe(); var changed = RemoveBridge(); Record("before-paper-restack");
            Assert.That(terrain.NotifyFixedMapSupportChanged(changed), Is.True, terrain.FixedBlocks.LastFailure);
            AssertRelocated(blocks); AssertRecoveryFields(before.State, host.Snapshot.Value);
            Assert.That(recoveries.Single().observation.CapsuleEmbedded, Is.False);
            Assert.That(recoveries.Single().observation.PaperEmbedded, Is.True);
            AssertFields(before.State, recoveries.Single().observation.Before, "Observer original Air snapshot");
            AssertOneSuccessfulCommit(1, 1); Record("after-paper-restack"); yield break;
        }

        [UnityTest, Timeout(600000)] public IEnumerator NoActualRecoveryPositionRejectsEntireRestackWithoutPublication()
        {
            CreateWorld(true); Place(host, new Vector3(.15f, .04f, .15f));
            for (int i = 0; i < 8; i++) StepAll();
            Assert.That(host.Snapshot.Value.Grounded && !host.Snapshot.Value.ShowsSwimBody, Is.True);
            var blocks = BuildBridgeColumn(new Vector2Int(0, 0), host.Snapshot.Value.Team);
            terrain.Tick(true, now, Dt); AssertActualPose(host);
            var changed = RemoveBridge();
            var prospective = new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 1, 0) };
            // Independent capsule-vs-map/AABB oracle over every actual production search root.
            // This deliberately proves no endpoint exists, not merely a blocked path.
            var roots = new[] { PlayerMotorSimulation.HumanPosition(host.Snapshot.Value) }
                .Concat(arena.SpawnPoints.Select(t => t.position)).Distinct().ToArray();
            int tested = 0;
            foreach (var origin in roots)
            for (int x = -10; x <= 10; x++) for (int y = -10; y <= 10; y++) for (int z = -10; z <= 10; z++)
            {
                if (x * x + y * y + z * z > 100) continue;
                var candidate = host.Snapshot.Value; candidate.Position = origin + new Vector3(x, y, z) * BlockWorld.CellSize;
                Assert.That(StandingFits(candidate, prospective), Is.False, "Independent endpoint " + candidate.Position);
                tested++;
            }
            Assert.That(tested, Is.GreaterThan(4000)); Record("all-real-search-endpoints-blocked");
            trace.Add(JsonUtility.ToJson(new SearchRecord { stage = "independent-search-proof", origins = roots,
                testedEndpoints = tested, radiusCells = 10, cellSize = BlockWorld.CellSize, droppedCells = prospective }));
            var before = new ActorStamp(host); var world = new WorldStamp(terrain);
            int failed = terrain.FailedTransactions; expectedRestack = blocks; Observe();
            LogAssert.Expect(LogType.Error, new Regex("\\[BLOCK FIXED\\] Atomic commit rejected: .*no verified safe player recovery"));
            Assert.That(terrain.NotifyFixedMapSupportChanged(changed), Is.False);
            Assert.That(terrain.FixedBlocks.LastFailure, Does.Contain("no verified safe player recovery"));
            Assert.That(terrain.FailedTransactions, Is.EqualTo(failed + 1));
            world.AssertUnchanged(terrain); before.AssertUnchanged(host);
            foreach (var block in blocks) Assert.That(terrain.FixedBlocks.TryGet(block.Handle, out var current) && current.Equals(block), Is.True);
            Assert.That(commits + publications + effects + payments + recoveries.Count, Is.Zero);
            Assert.That(observerFailure, Is.Null); Record("rejected-without-publication"); yield break;
        }

        sealed class InjectedSecondRecoveryException : Exception
        { public InjectedSecondRecoveryException() : base("65 second actual Snapshot apply fault") { } }

        [UnityTest, Timeout(600000)] public IEnumerator SecondActualActorApplyExceptionRollsBackBothPlayersAndWorld()
        {
            var bot = match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab));
            Assert.That(bot, Is.Not.Null, match.TestBotMessage); Assert.That(bot.IsSpawned && bot.IsTestBot, Is.True);
            Assert.That(PrototypePlayer.ByOwner.Values.Count(p => p != null && p.IsSpawned), Is.EqualTo(2));
            CreateWorld();
            // Same enumeration and eligibility as production CaptureSafetyPlayers; do not assume Host first.
            var order = PrototypePlayer.ByOwner.Values.Where(p => p != null && p.IsSpawned && p.Snapshot.Value.IsAlive &&
                p.GetComponent<CharacterController>() != null).ToArray();
            Assert.That(order.Length, Is.EqualTo(2));
            var columns = new[] { new Vector2Int(-4, 0), new Vector2Int(3, 0) };
            for (int i = 0; i < order.Length; i++)
            { var c = BlockWorld.Center(new Vector3Int(columns[i].x, 0, columns[i].y)); Place(order[i], new Vector3(c.x, .04f, c.z)); }
            for (int i = 0; i < 8; i++) StepAll();
            var blocks = order.SelectMany((p, i) => BuildBridgeColumn(columns[i], p.Snapshot.Value.Team)).ToArray();
            terrain.Tick(true, now, Dt); foreach (var player in order) AssertActualPose(player);
            var changed = RemoveBridge(); var stamps = order.Select(p => new ActorStamp(p)).ToArray();
            var world = new WorldStamp(terrain); int failureCount = terrain.FailedTransactions;
            expectedRestack = blocks; Observe(); bool injected = false; uint wantedRevision = terrain.Revision + 1;
            NetworkVariable<PlayerSnapshot>.OnValueChangedDelegate injection = null;
            injection = (previous, value) =>
            {
                if (injected || value.FixedBlockRevision != wantedRevision) return;
                // Detach first: a failed prerequisite is a real failure and must not poison rollback.
                order[1].Snapshot.OnValueChanged -= injection; injected = true;
                Assert.That(value.FixedBlockEpoch, Is.EqualTo(terrain.FixedBlocks.Epoch));
                Assert.That(value.FixedBlockRound, Is.EqualTo(terrain.Round));
                Assert.That(value.Position, Is.Not.EqualTo(stamps[1].State.Position));
                Assert.That(order[1].Snapshot.Value.Position, Is.EqualTo(value.Position), "NGO setter already wrote second actor before callback");
                AssertRecoveryFields(stamps[0].State, order[0].Snapshot.Value); AssertActualPose(order[0]);
                Assert.That(terrain.SafetyRecoveries, Is.EqualTo(world.Recoveries + 1));
                Assert.That(order[1].transform.position, Is.EqualTo(value.Position), "Second actor transform was applied before Snapshot setter");
                Record("actual-second-setter-exception-injection");
                throw new InjectedSecondRecoveryException();
            };
            order[1].Snapshot.OnValueChanged += injection;
            detach.Add(() => { if (order[1] != null) order[1].Snapshot.OnValueChanged -= injection; });
            LogAssert.Expect(LogType.Error, new Regex("\\[BLOCK FIXED\\] Atomic commit rejected: InjectedSecondRecoveryException: 65 second actual Snapshot apply fault"));
            Assert.That(terrain.NotifyFixedMapSupportChanged(changed), Is.False);
            Assert.That(injected, Is.True, "Both real actors must reach the intended partial-apply injection");
            Assert.That(terrain.FixedBlocks.LastFailure, Does.Contain("InjectedSecondRecoveryException: 65 second actual Snapshot apply fault"));
            Assert.That(terrain.FailedTransactions, Is.EqualTo(failureCount + 1));
            world.AssertUnchanged(terrain); for (int i = 0; i < order.Length; i++) stamps[i].AssertUnchanged(order[i]);
            Assert.That(commits + publications + effects + payments + recoveries.Count, Is.Zero);
            Record("world-and-both-real-actors-rolled-back");
            // Genuine retry: same external bridge removal, production transaction and actual guard.
            Assert.That(terrain.NotifyFixedMapSupportChanged(changed), Is.True, terrain.FixedBlocks.LastFailure);
            AssertRelocated(blocks); AssertOneSuccessfulCommit(2, 2);
            for (int i = 0; i < order.Length; i++) AssertRecoveryFields(stamps[i].State, order[i].Snapshot.Value);
            Assert.That(terrain.SafetyRecoveries, Is.EqualTo(world.Recoveries + 2));
            Record("same-production-transaction-retry-succeeded"); yield break;
        }

        static void AssertFields(PlayerSnapshot expected, PlayerSnapshot actual, string label)
        {
            foreach (var field in SnapshotFields)
                Assert.That(field.GetValue(actual), Is.EqualTo(field.GetValue(expected)), label + "." + field.Name);
        }
        void AssertRecoveryFields(PlayerSnapshot before, PlayerSnapshot after)
        {
            Assert.That(after.Position, Is.Not.EqualTo(before.Position), "A recovery actually moved this actor");
            var expected = before;
            if (after.ShowsSwimBody && after.Movement == MovementMode.Air && after.PaperPose == PaperPose.Air)
            {
                expected.Position = after.Position;
                expected.PaperCenter += after.Position - before.Position;
            }
            else
            {
                expected.Position = after.Position; expected.Velocity = expected.PlanarVelocity = Vector3.zero; expected.VerticalSpeed = 0;
                expected.Grounded = expected.Swimming = expected.CompactBody = expected.FriendlyInkContact = expected.SwimWasHeld = false;
                expected.AirHumanOffset = 0; expected.SwimSource = expected.AirSwimSource = SwimSurface.None; expected.Movement = MovementMode.Air;
                expected.SupportCell = default; expected.SupportTerrainRevision = 0; expected.GroundNormal = Vector3.up;
                expected.SupportIsFixedGrid = false; expected.SupportFixedId = 0; expected.SupportFixedGeneration = 0;
                expected.SupportIsSurfacePaint = false; expected.SupportSurfaceFace = expected.SupportSurfaceU = expected.SupportSurfaceV = 0;
                expected.WallNormal = expected.WallPoint = expected.MantleFrom = expected.MantleTo = Vector3.zero;
                expected.WallSurfaceId = expected.WallRegionId = 0; expected.PaperPose = PaperPose.None;
                expected.PaperCenter = after.Position; expected.PaperRotation = Quaternion.identity; expected.PaperAnimationTime = 0; expected.PaperMove = Vector2.zero;
            }
            expected.FixedBlockEpoch = terrain.FixedBlocks.Epoch; expected.FixedBlockRound = terrain.Round; expected.FixedBlockRevision = terrain.Revision;
            AssertFields(expected, after, "recovery field contract (includes resources/actions/edges/life/time/tick/impulse)");
        }

        void AssertActualPose(PrototypePlayer player)
        {
            var s = player.Snapshot.Value; var controller = player.GetComponent<CharacterController>(); var shape = HeroBodyShape.For(s.HeroId);
            Assert.That(player.IsSpawned && s.IsAlive && controller.enabled, Is.True);
            Assert.That(player.transform.position, Is.EqualTo(s.Position));
            Assert.That(controller.radius, Is.EqualTo(shape.Radius));
            Assert.That(controller.height, Is.EqualTo(s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height));
            Assert.That(controller.center, Is.EqualTo(s.Swimming || s.CompactBody ? shape.CompactCenter : shape.Center));
            Assert.That(controller.skinWidth, Is.EqualTo(shape.SkinWidth)); Assert.That(controller.stepOffset, Is.EqualTo(shape.StepOffset));
            var body = player.SwimBody;
            Assert.That(body.CapsuleHitVolume.enabled, Is.EqualTo(s.IsAlive && !body.FlatHitActive && (s.Swimming || !controller.enabled)));
            Assert.That(body.CapsuleHitVolume.radius, Is.EqualTo(shape.Radius));
            Assert.That(body.CapsuleHitVolume.height, Is.EqualTo(s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height));
            Assert.That(body.CapsuleHitVolume.center, Is.EqualTo(s.Swimming || s.CompactBody ? shape.CompactCenter : shape.Center));
            Rect[] rects = Array.Empty<Rect>();
            if (s.ShowsSwimBody)
            {
                using var oracle = new PaperCapture(player.SwimBody.Profile); oracle.SampleUncachedForDiagnostics(s); rects = oracle.HitRects.ToArray();
                Assert.That(player.SwimBody.HitRects, Is.EqualTo(rects));
                Assert.That(player.SwimBody.FlatHitActive, Is.True);
                Assert.That(player.SwimBody.HitVolume.transform.position, Is.EqualTo(s.PaperCenter));
                Assert.That(player.SwimBody.HitVolume.transform.rotation, Is.EqualTo(s.PaperRotation));
                Assert.That(player.SwimBody.HitVolume.sharedMesh.vertices, Is.EqualTo(oracle.HitMesh.vertices));
                Assert.That(player.SwimBody.HitVolume.sharedMesh.triangles, Is.EqualTo(oracle.HitMesh.triangles));
                Assert.That(player.SwimBody.HitVolume.sharedMesh.bounds, Is.EqualTo(oracle.HitMesh.bounds));
            }
            else Assert.That(player.SwimBody.FlatHitActive, Is.False);
            Assert.That(PoseFits(s, terrain.FixedBlocks.Blocks.Values.Select(b => b.Cell), rects), Is.True, "Independent full geometry oracle");
            Assert.That(terrain.PublishedTraversalPoseFits(player, s), Is.True, "Production query separately agrees");
        }

        // These oracles consume only actual map colliders, complete captured rects,
        // public fixed cell values and authored shape. They never call fixed queries.
        bool StandingFits(PlayerSnapshot s, IEnumerable<Vector3Int> cells)
        {
            var shape = HeroBodyShape.For(s.HeroId); var p = s.Position;
            if (Mathf.Abs(p.x) + shape.Radius > terrain.World.Dimensions.x * .5f || Mathf.Abs(p.z) + shape.Radius > terrain.World.Dimensions.y * .5f ||
                p.y < terrain.World.Bottom || p.y + shape.Height > BlockWorld.Top) return false;
            return !CapsuleMapHits(s, false) && !CapsuleHits(s, cells, false);
        }
        bool PoseFits(PlayerSnapshot s, IEnumerable<Vector3Int> cells, IReadOnlyList<Rect> rects)
        {
            var shape = HeroBodyShape.For(s.HeroId); var p = s.Position; float height = s.Swimming || s.CompactBody ? shape.CompactHeight : shape.Height;
            if (Mathf.Abs(p.x) + shape.Radius > terrain.World.Dimensions.x * .5f || Mathf.Abs(p.z) + shape.Radius > terrain.World.Dimensions.y * .5f ||
                p.y < terrain.World.Bottom || p.y + height > BlockWorld.Top || CapsuleMapHits(s) || CapsuleHits(s, cells)) return false;
            if (!s.ShowsSwimBody) return true;
            foreach (var rect in rects)
            {
                Vector3 center = s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                Vector3 half = PaperHalf(s, rect); float thickness = host.SwimBody.Profile.Thickness;
                var exactExtent = Abs(s.PaperRotation * Vector3.right) * (rect.width * .5f) +
                    Abs(s.PaperRotation * Vector3.up) * (rect.height * .5f) + Abs(s.PaperRotation * Vector3.forward) * (thickness * .5f);
                if (Mathf.Abs(center.x) + exactExtent.x > terrain.World.Dimensions.x * .5f || Mathf.Abs(center.z) + exactExtent.z > terrain.World.Dimensions.y * .5f ||
                    center.y - exactExtent.y < terrain.World.Bottom || center.y + exactExtent.y > BlockWorld.Top ||
                    Physics.CheckBox(center, half, s.PaperRotation, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return false;
            }
            return !PaperHits(s, rects, cells);
        }
        static bool CapsuleMapHits(PlayerSnapshot s, bool? compact = null)
        {
            var shape = HeroBodyShape.For(s.HeroId); float height = (compact ?? (s.Swimming || s.CompactBody)) ? shape.CompactHeight : shape.Height;
            float radius = Mathf.Max(.01f, shape.Radius - Mathf.Max(.005f, shape.SkinWidth));
            float segment = Mathf.Max(0, height * .5f - shape.Radius); var center = s.Position + Vector3.up * (height * .5f);
            return Physics.CheckCapsule(center - Vector3.up * segment, center + Vector3.up * segment, radius,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
        }
        static bool CapsuleHits(PlayerSnapshot s, IEnumerable<Vector3Int> cells, bool? compact = null)
        {
            var shape = HeroBodyShape.For(s.HeroId); float height = (compact ?? (s.Swimming || s.CompactBody)) ? shape.CompactHeight : shape.Height;
            float radius = Mathf.Max(.01f, shape.Radius - Mathf.Max(.005f, shape.SkinWidth));
            float segment = Mathf.Max(0, height * .5f - shape.Radius); Vector3 center = s.Position + Vector3.up * (height * .5f);
            foreach (var cell in cells)
            {
                var box = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize + Vector3.up * (2 * segment));
                if ((center - box.ClosestPoint(center)).sqrMagnitude < radius * radius - .0000001f) return true;
            }
            return false;
        }
        Vector3 PaperHalf(PlayerSnapshot s, Rect rect) => new(Mathf.Max(.001f, rect.width * .5f - .001f),
            Mathf.Max(.001f, rect.height * .5f - .001f), Mathf.Max(.001f, host.SwimBody.Profile.Thickness * .5f - .001f));
        bool PaperHits(PlayerSnapshot s, IReadOnlyList<Rect> rects, IEnumerable<Vector3Int> cells)
        {
            foreach (var cell in cells) foreach (var rect in rects)
            {
                Vector3 center = s.PaperCenter + s.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0);
                if (ObbIntersectsAabb(center, PaperHalf(s, rect), s.PaperRotation, BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .5f))) return true;
            }
            return false;
        }
        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        static bool ObbIntersectsAabb(Vector3 c, Vector3 half, Quaternion q, Vector3 other, Vector3 boxHalf)
        {
            var basis = new[] { q * Vector3.right, q * Vector3.up, q * Vector3.forward };
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            bool Separates(Vector3 axis)
            {
                if (axis.sqrMagnitude < .0000000001f) return false;
                float a = half.x * Mathf.Abs(Vector3.Dot(axis, basis[0])) + half.y * Mathf.Abs(Vector3.Dot(axis, basis[1])) + half.z * Mathf.Abs(Vector3.Dot(axis, basis[2]));
                float b = boxHalf.x * Mathf.Abs(axis.x) + boxHalf.y * Mathf.Abs(axis.y) + boxHalf.z * Mathf.Abs(axis.z);
                return Mathf.Abs(Vector3.Dot(c - other, axis)) > a + b + .000001f;
            }
            foreach (var axis in axes) if (Separates(axis)) return false;
            foreach (var axis in basis) if (Separates(axis)) return false;
            foreach (var axis in axes) foreach (var otherAxis in basis) if (Separates(Vector3.Cross(axis, otherAxis))) return false;
            return true;
        }

        sealed class ActorStamp
        {
            public readonly PlayerSnapshot State;
            readonly Vector3 position, scale, center;
            readonly Quaternion rotation;
            readonly bool enabled, hitEnabled, capsuleEnabled, rendererEnabled;
            readonly float height, radius, skin, step, slope, minMove;
            readonly Vector3 hitPosition, capsuleCenter, renderPosition, renderScale;
            readonly Quaternion hitRotation, renderRotation;
            readonly float capsuleHeight, capsuleRadius;
            readonly Mesh mesh;
            readonly Vector3[] vertices;
            readonly int[] triangles;
            readonly Bounds bounds;
            readonly PaperCapture capture;
            readonly Rect[] rects;
            readonly int poseVersion;
            public ActorStamp(PrototypePlayer player)
            {
                State = player.Snapshot.Value; position = player.transform.position; rotation = player.transform.rotation; scale = player.transform.localScale;
                var c = player.GetComponent<CharacterController>(); enabled = c.enabled; height = c.height; radius = c.radius; center = c.center;
                skin = c.skinWidth; step = c.stepOffset; slope = c.slopeLimit; minMove = c.minMoveDistance;
                var body = player.SwimBody; hitEnabled = body.HitVolume.enabled; capsuleEnabled = body.CapsuleHitVolume.enabled;
                hitPosition = body.HitVolume.transform.position; hitRotation = body.HitVolume.transform.rotation; mesh = body.HitVolume.sharedMesh;
                vertices = mesh != null ? mesh.vertices : null; triangles = mesh != null ? mesh.triangles : null; bounds = mesh != null ? mesh.bounds : default;
                capsuleCenter = body.CapsuleHitVolume.center; capsuleHeight = body.CapsuleHitVolume.height; capsuleRadius = body.CapsuleHitVolume.radius;
                rendererEnabled = body.BodyRenderer.enabled; renderPosition = body.BodyRenderer.transform.position;
                renderRotation = body.BodyRenderer.transform.rotation; renderScale = body.BodyRenderer.transform.localScale;
                capture = body.Capture; rects = body.HitRects?.ToArray(); poseVersion = capture != null ? capture.PoseVersion : 0;
            }
            public void AssertUnchanged(PrototypePlayer player)
            {
                AssertFields(State, player.Snapshot.Value, "rollback snapshot");
                Assert.That(player.transform.position, Is.EqualTo(position)); Assert.That(player.transform.rotation, Is.EqualTo(rotation)); Assert.That(player.transform.localScale, Is.EqualTo(scale));
                var c = player.GetComponent<CharacterController>(); Assert.That(c.enabled, Is.EqualTo(enabled)); Assert.That(c.height, Is.EqualTo(height));
                Assert.That(c.radius, Is.EqualTo(radius)); Assert.That(c.center, Is.EqualTo(center)); Assert.That(c.skinWidth, Is.EqualTo(skin));
                Assert.That(c.stepOffset, Is.EqualTo(step)); Assert.That(c.slopeLimit, Is.EqualTo(slope)); Assert.That(c.minMoveDistance, Is.EqualTo(minMove));
                var body = player.SwimBody; Assert.That(body.HitVolume.enabled, Is.EqualTo(hitEnabled)); Assert.That(body.CapsuleHitVolume.enabled, Is.EqualTo(capsuleEnabled));
                Assert.That(body.HitVolume.transform.position, Is.EqualTo(hitPosition)); Assert.That(body.HitVolume.transform.rotation, Is.EqualTo(hitRotation));
                Assert.That(body.HitVolume.sharedMesh, Is.SameAs(mesh)); Assert.That(body.Capture, Is.SameAs(capture));
                Assert.That(body.HitRects?.ToArray(), Is.EqualTo(rects));
                Assert.That(body.Capture != null ? body.Capture.PoseVersion : 0, Is.EqualTo(poseVersion));
                if (mesh != null) { Assert.That(mesh.vertices, Is.EqualTo(vertices)); Assert.That(mesh.triangles, Is.EqualTo(triangles)); Assert.That(mesh.bounds, Is.EqualTo(bounds)); }
                Assert.That(body.CapsuleHitVolume.center, Is.EqualTo(capsuleCenter)); Assert.That(body.CapsuleHitVolume.height, Is.EqualTo(capsuleHeight)); Assert.That(body.CapsuleHitVolume.radius, Is.EqualTo(capsuleRadius));
                Assert.That(body.BodyRenderer.enabled, Is.EqualTo(rendererEnabled)); Assert.That(body.BodyRenderer.transform.position, Is.EqualTo(renderPosition));
                Assert.That(body.BodyRenderer.transform.rotation, Is.EqualTo(renderRotation)); Assert.That(body.BodyRenderer.transform.localScale, Is.EqualTo(renderScale));
            }
        }

        sealed class WorldStamp
        {
            readonly byte[] full, delta;
            readonly uint since, surfaceSince;
            readonly int journalCount, failures, observerErrors, relocated, converted;
            readonly Dictionary<object, object> safeCache;
            readonly BlockFixedGridState[] states;
            readonly QueryStamp[] queries;
            readonly BlockWorld storage;
            readonly BlockSurfaceWorld surfaces;
            public readonly int Pink, Blue, Recoveries;
            public WorldStamp(BlockTerrain terrain)
            {
                var world = terrain.FixedBlocks; since = world.Revision > 2 ? world.Revision - 2 : 0;
                storage = world.Storage; surfaces = world.Surfaces;
                surfaceSince = world.Surfaces.Revision > 2 ? world.Surfaces.Revision - 2 : 0;
                full = BlockFixedGridCodec.Encode(world.Capture(true)); delta = BlockFixedGridCodec.Encode(world.Capture(false, since, surfaceSince));
                Pink = world.PinkCount; Blue = world.BlueCount; Recoveries = terrain.SafetyRecoveries;
                failures = terrain.SafetyRecoveryFailures; observerErrors = terrain.SafetyRecoveryObserverErrors;
                relocated = world.LastRelocatedCount; converted = world.LastConvertedCount;
                journalCount = ((ICollection)typeof(BlockFixedGridWorld).GetField("_journal", Private).GetValue(world)).Count;
                safeCache = CaptureSafeCache(terrain); states = world.Blocks.Values.OrderBy(b => b.Handle.Id).ToArray();
                queries = states.SelectMany(s => new[] { s.Cell, new Vector3Int(s.Cell.x, 0, s.Cell.z), new Vector3Int(s.Cell.x, 1, s.Cell.z) })
                    .Distinct().Select(cell => new QueryStamp(terrain, cell)).ToArray();
            }
            static Dictionary<object, object> CaptureSafeCache(BlockTerrain terrain)
            {
                var result = new Dictionary<object, object>();
                foreach (DictionaryEntry entry in (IDictionary)typeof(BlockTerrain).GetField("_safePositions", Private).GetValue(terrain)) result.Add(entry.Key, entry.Value);
                return result;
            }
            public void AssertUnchanged(BlockTerrain terrain)
            {
                var world = terrain.FixedBlocks;
                Assert.That(world.Storage, Is.SameAs(storage)); Assert.That(world.Surfaces, Is.SameAs(surfaces));
                Assert.That(BlockFixedGridCodec.Encode(world.Capture(true)), Is.EqualTo(full), "All fixed+paint full snapshot fields including NextId");
                Assert.That(BlockFixedGridCodec.Encode(world.Capture(false, since, surfaceSince)), Is.EqualTo(delta), "Journal delta including lifecycle headers and surface bytes");
                Assert.That(((ICollection)typeof(BlockFixedGridWorld).GetField("_journal", Private).GetValue(world)).Count, Is.EqualTo(journalCount));
                Assert.That(world.PinkCount, Is.EqualTo(Pink)); Assert.That(world.BlueCount, Is.EqualTo(Blue));
                Assert.That(terrain.SafetyRecoveries, Is.EqualTo(Recoveries)); Assert.That(terrain.SafetyRecoveryFailures, Is.EqualTo(failures));
                Assert.That(terrain.SafetyRecoveryObserverErrors, Is.EqualTo(observerErrors));
                Assert.That(world.LastRelocatedCount, Is.EqualTo(relocated)); Assert.That(world.LastConvertedCount, Is.EqualTo(converted));
                Assert.That(CaptureSafeCache(terrain), Is.EquivalentTo(safeCache), "Exact saved-position values including private lifecycle fields");
                Assert.That(world.Blocks.Count, Is.EqualTo(states.Length));
                foreach (var state in states)
                { Assert.That(world.TryGet(state.Handle, out var actual), Is.True); Assert.That(actual, Is.EqualTo(state)); Assert.That(world.TryGet(state.Cell, out var cell), Is.True); Assert.That(cell, Is.EqualTo(state)); }
                foreach (var query in queries) query.AssertUnchanged(terrain);
                Assert.That(terrain.World, Is.SameAs(world.Storage)); Assert.That(terrain.SurfacePaints, Is.SameAs(world.Surfaces));
            }
        }
        sealed class QueryStamp
        {
            readonly Vector3Int cell;
            readonly bool cast, sphere, capsule, box, support;
            readonly BlockFixedGridHit castHit, sphereHit, supportHit;
            public QueryStamp(BlockTerrain terrain, Vector3Int cell)
            {
                this.cell = cell; Vector3 center = BlockWorld.Center(cell);
                cast = terrain.TryCastFixed(center + Vector3.up * 3, Vector3.down, 6, .02f, out castHit);
                sphere = terrain.TryOverlapFixed(center, .05f, Vector3.up, out sphereHit);
                capsule = terrain.FixedCapsuleOverlaps(center, .1f, .08f); box = terrain.FixedBoxOverlaps(center, Vector3.one * .12f, Quaternion.identity);
                support = terrain.TrySupportFixed(center + Vector3.up * .3f, .08f, 1, out supportHit);
            }
            public void AssertUnchanged(BlockTerrain terrain)
            {
                var other = new QueryStamp(terrain, cell);
                foreach (var field in typeof(QueryStamp).GetFields(Private)) Assert.That(field.GetValue(other), Is.EqualTo(field.GetValue(this)), "Committed query index " + cell + "." + field.Name);
            }
        }

        [Serializable] sealed class EndRecord
        { public string stage, test, result, message; public int cursorWritesBeforeLeave, cursorWritesAfterLeave; }
        [Serializable] sealed class ManualStepRecord
        {
            public string stage; public ulong id; public PlayerSnapshot before, after; public PlayerInputFrame input;
            public double requestedTime; public int motorCompleteCount, observerErrorsBefore, observerErrorsAfter;
        }
        [Serializable] sealed class WitnessRecord
        { public string stage, profileHash; public Vector2Int column; public PlayerSnapshot snapshot; public Rect[] rects; }
        [Serializable] sealed class SearchRecord
        { public string stage; public Vector3[] origins; public Vector3Int[] droppedCells; public int testedEndpoints, radiusCells; public float cellSize; }
        [Serializable] sealed class ActorRecord
        {
            public ulong id; public bool spawned, controllerEnabled, flatHitActive; public PlayerSnapshot snapshot; public PlayerInputFrame input;
            public Vector3 transformPosition, controllerCenter; public float height, radius, skin, step; public string profileHash;
            public int rects, poseVersion;
        }
        [Serializable] sealed class StageRecord
        {
            public string stage, test, mapTopology, fixedAndPaintBase64, dependencyEvidence = "Safety/source-evidence.json";
            public double time; public uint epoch, round, revision, surfaceRevision; public int safetyRecoveries, commits, publications, effects, payments;
            public ActorRecord[] actors;
        }
        void Record(string stage, PrototypePlayer inputActor = null, PlayerInputFrame input = default)
        {
            trace.Add(JsonUtility.ToJson(new StageRecord { stage = stage, test = TestContext.CurrentContext.Test.Name, mapTopology = arena.BakedTopology,
                fixedAndPaintBase64 = Convert.ToBase64String(BlockFixedGridCodec.Encode(terrain.FixedBlocks.Capture(true))),
                time = now, epoch = terrain.FixedBlocks.Epoch, round = terrain.Round, revision = terrain.Revision, surfaceRevision = terrain.SurfacePaints.Revision,
                safetyRecoveries = terrain.SafetyRecoveries, commits = commits, publications = publications, effects = effects, payments = payments,
                actors = actors.Select(p => { var c = p.GetComponent<CharacterController>(); return new ActorRecord { id = p.PlayerId, spawned = p.IsSpawned,
                    snapshot = p.Snapshot.Value, input = p == inputActor ? input : default, transformPosition = p.transform.position,
                    controllerEnabled = c.enabled, controllerCenter = c.center, height = c.height, radius = c.radius, skin = c.skinWidth, step = c.stepOffset,
                    flatHitActive = p.SwimBody.FlatHitActive, profileHash = p.SwimBody.Profile.ContentHash, rects = p.SwimBody.HitRects?.Count ?? 0,
                    poseVersion = p.SwimBody.Capture != null ? p.SwimBody.Capture.PoseVersion : 0 }; }).ToArray() }));
        }
    }
}
#endif
