#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class InkImpactHostPlayTests
    {
        static IEnumerator Wait(Func<bool> predicate, string message, double seconds = 20)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(predicate(), Is.True, message);
        }

        [UnityTest, Timeout(600000)] public IEnumerator RealMapProjectileRpcPaintAndCosmeticIsolation()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Scenario();
            yield return new ExitPlayMode();
        }

        static byte[] ReadBytes(RenderTexture target)
        {
            var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            try { texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(); return texture.GetRawTextureData<byte>().ToArray(); }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(texture); }
        }

        static IEnumerator Scenario()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Bootstrap");
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var app = PrototypeApp.Current; yield return app.Connect(true, "127.0.0.1", port).ToCoroutine();
            Assert.That(app.InRoom, Is.True, app.Error);
            yield return Wait(() => PrototypePlayer.Local != null && InkPresentation.Current != null && PrototypeMatch.Current != null &&
                PrototypePlayer.Local.CharacterView != null && Camera.main != null && PrototypeMatch.Current.BlockSyncReady, "Host, hero content, blocks and camera");
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local; var presentation = InkPresentation.Current;
            Assert.That(match.NetworkManager, Is.Not.Null, "Spawned match network manager");
            var mismatch = (byte[])app.Manager.NetworkConfig.ConnectionData.Clone(); mismatch[0] ^= 1;
            var rejected = new Unity.Netcode.NetworkManager.ConnectionApprovalResponse();
            app.Manager.ConnectionApprovalCallback(new Unity.Netcode.NetworkManager.ConnectionApprovalRequest
                { ClientNetworkId = 900001, Payload = mismatch }, rejected);
            Assert.That(rejected.Approved, Is.False, "Different content/protocol signature is rejected");
            Assert.That(rejected.Reason, Does.Contain("协议或游戏内容不一致"));
            string output = PaidBlockHostPlayRig.NewOutput("Impact");
            var evidence = new System.Text.StringBuilder("hero,shots,paid_material,added,removed_enemy,pink,blue,revision,debris_seen,legacy_mask_pixels,legacy_display_pixels\n");
            var surface = match.Arena.Surfaces.Values.Single(s => s.name == "EndBoundary_-1");
            var collider = surface.GetComponent<Collider>(); var center = collider.bounds.center;
            Assert.That(collider.bounds.size.y, Is.GreaterThan(2)); Assert.That(collider.bounds.size.x, Is.GreaterThan(5)); Assert.That(collider.bounds.size.z, Is.LessThan(1));
            // Aim at the inner south wall, outside both protected spawn columns.
            // An arbitrary boundary's outward face can be outside the legal block world.
            center.x = 12;
            Assert.That(collider.Raycast(new Ray(center + Vector3.forward * 3, Vector3.back), out var hit, 5), Is.True);
            Assert.That(match.Arena.IsInHeroChangeZone(1, hit.point) || match.Arena.IsInHeroChangeZone(2, hit.point), Is.False);
            var camera = new GameObject("Real map block impact capture").AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.aspect = 1; camera.fieldOfView = 42;
            camera.transform.position = hit.point + hit.normal * 3.2f + Vector3.up * .3f + Vector3.right * .7f;
            camera.transform.LookAt(hit.point);
            try
            {
                using (var rig = new PaidBlockHostPlayRig(app, player))
                {
                    yield return rig.Ready();
                    foreach (int hero in new[] { 1, 2, 3, 4, 5 })
                    {
                        bool demolition = BlockAttackService.ExplosiveMain(GameplayConfig.GetWeapon(hero));
                        var attackTarget = demolition ? hit.point + Vector3.right * 1.8f : hit.point;
                        if (demolition)
                        {
                            // The enemy wall is made by ordinary paid shots after a normal team RPC.
                            yield return rig.SelectTeam(2); yield return rig.SelectHero(1);
                            for (int attempt = 0; attempt < 12 && !PaidBlockHostPlayRig.StableOwners(match.Blocks.World)
                                .Any(p => p.Value == 2 && Vector3.Distance(BlockWorld.Center(p.Key), attackTarget) < 1.1f); attempt++)
                                yield return rig.FireAt(attackTarget, attackTarget + hit.normal * 4);
                            Assert.That(PaidBlockHostPlayRig.StableOwners(match.Blocks.World)
                                .Any(p => p.Value == 2 && Vector3.Distance(BlockWorld.Center(p.Key), attackTarget) < 1.1f), Is.True, "Paid enemy wall near the actual impact target");
                            yield return rig.SelectTeam(1);
                        }
                        yield return rig.SelectHero(hero);
                        int pinkBefore = match.Blocks.PinkCount, blueBefore = match.Blocks.BlueCount;
                        yield return rig.FireAt(attackTarget, attackTarget + hit.normal * 4);
                        var tokens = rig.LastTokens;
                        Assert.That(rig.DebrisSeen, Is.True, "Actual authoritative shot/impact RPC produces the new cube fragments");
                        Assert.That(presentation.GetComponentsInChildren<InkImpactEffect>().Any(e => e.IsAlive), Is.False, "Default block mode retires the old layered ink splash");
                        Assert.That(rig.ContactPositions.Count, Is.GreaterThan(0), "Actual projectile reaches a collision");
                        if (demolition)
                        {
                            Assert.That(tokens.All(t => t.BuildLimit == 0 && t.Added == 0), Is.True, "Explosive main paths cannot build");
                            Assert.That(tokens.Sum(t => t.RemovedEnemy), Is.GreaterThan(0), "Paid explosion actually dismantles the enemy wall");
                            Assert.That(match.Blocks.BlueCount, Is.LessThan(blueBefore));
                            Assert.That(match.Blocks.PinkCount, Is.LessThanOrEqualTo(pinkBefore));
                        }
                        else
                        {
                            Assert.That(tokens.Sum(t => t.Added), Is.GreaterThan(0), "Paid ordinary impacts build persistent blocks");
                            Assert.That(match.Blocks.PinkCount, Is.GreaterThan(pinkBefore));
                        }
                        PaidBlockHostPlayRig.AssertStableCollision(match.Blocks);
                        surface.FlushDisplay();
                        Assert.That(surface.HasPaint, Is.False, "Default block impacts do not activate legacy paint");
                        Assert.That(surface.PendingPaintCount, Is.Zero);
                        Assert.That(surface.Mask, Is.Null, "The default arena does not allocate the retired paint plane");
                        Assert.That(surface.DisplayMask, Is.Null); Assert.That(surface.VisualState, Is.Null);
                        // Allocate only for this explicit legacy compatibility roundtrip.
                        surface.Restore(new byte[surface.TextureBytes], new byte[surface.TextureBytes / 2]);
                        surface.Clear();
                        var visual = InkAppearanceProfile.PackVisual(ReadBytes(surface.VisualState));
                        var raw = ReadBytes(surface.Mask); var display = ReadBytes(surface.DisplayMask);
                        int rawCount = raw.Where((v, i) => i % 4 == 3 && v > 0).Count();
                        int displayCount = display.Where((v, i) => i % 4 == 3 && v > 0).Count();
                        Assert.That(rawCount, Is.Zero, "Paid block impacts cannot leave old persistent ink");
                        Assert.That(displayCount, Is.Zero, "Default persistent terrain is blocks, not the old display mask");
                        var property = new MaterialPropertyBlock(); surface.GetComponent<Renderer>().GetPropertyBlock(property);
                        Assert.That(property.GetTexture("_MaskTexture"), Is.SameAs(surface.DisplayMask));
                        // Preserve the old graphics compatibility roundtrip, explicitly with unpainted data.
                        surface.Clear(); surface.Restore(raw, visual); surface.FlushDisplay();
                        CollectionAssert.AreEqual(raw, ReadBytes(surface.Mask), "Unpainted compatibility mask restores exactly");
                        CollectionAssert.AreEqual(display, ReadBytes(surface.DisplayMask), "Unpainted compatibility display restores exactly");
                        surface.Clear(); surface.ReleaseGraphics();
                        Assert.That(surface.Mask, Is.Null); Assert.That(surface.DisplayMask, Is.Null); Assert.That(surface.VisualState, Is.Null);
                        camera.transform.position = attackTarget + hit.normal * 3.2f + Vector3.up * .3f + Vector3.right * .7f;
                        camera.transform.LookAt(attackTarget);
                        presentation.UpdateFlights(app.Manager.ServerTime.Time, camera);
                        var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Splatoon.Editor").GetType("Splatoon.Editor.CombatGirlsGraphicsValidation");
                        type.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { camera, "hero-" + hero, output });
                        uint ownership = match.Arena.OwnershipHash(), revision = match.Blocks.Revision;
                        var terrainBytes = match.Blocks.ExportSnapshot(); var state = player.Snapshot.Value;
                        for (uint i = 0; i < 16; i++) presentation.Impact(new InkImpact { Id = 20000 + (uint)hero * 100 + i,
                            Round = match.State.Value.Round, Time = app.Manager.ServerTime.Time, Position = hit.point, Normal = hit.normal,
                            Team = (byte)(i % 2 + 1), Hit = true });
                        Assert.That(presentation.BlockDebrisCount, Is.GreaterThan(0), "Cosmetic cube fragments are visible data");
                        Assert.That(match.Arena.OwnershipHash(), Is.EqualTo(ownership), "Cosmetic impacts cannot paint ownership");
                        CollectionAssert.AreEqual(terrainBytes, match.Blocks.ExportSnapshot(), "Cosmetic impacts cannot build, dismantle or score blocks");
                        Assert.That(match.Blocks.Revision, Is.EqualTo(revision));
                        Assert.That(player.Snapshot.Value.Health, Is.EqualTo(state.Health), "Cosmetic impacts cannot deal damage");
                        Assert.That(player.Snapshot.Value.Ink, Is.EqualTo(state.Ink));
                        Assert.That(player.Snapshot.Value.SpecialPoints, Is.EqualTo(state.SpecialPoints));
                        evidence.AppendLine(FormattableString.Invariant($"{hero},{rig.AcceptedShots},{rig.MaterialPaid},{tokens.Sum(t => t.Added)},{tokens.Sum(t => t.RemovedEnemy)},{match.Blocks.PinkCount},{match.Blocks.BlueCount},{match.Blocks.Revision},{rig.DebrisSeen},{rawCount},{displayCount}"));
                        presentation.Clear();
                        Assert.That(presentation.BlockCubeCount + presentation.BlockDebrisCount + presentation.ActiveShots, Is.Zero, "Clearing retires flight and decorative data");
                        CollectionAssert.AreEqual(terrainBytes, match.Blocks.ExportSnapshot(), "Clearing cosmetics cannot clear gameplay terrain");
                    }
                    Assert.That(rig.Publications, Is.GreaterThan(0), "Observed real atomic mesh/collider/score commits");
                }
                File.WriteAllText(Path.Combine(output, "block-impact-evidence.csv"), evidence.ToString());
                presentation.Clear();
                yield return app.Leave().ToCoroutine();
                yield return Wait(() => InkPresentation.Current == null && BlockTerrain.Current == null, "Leaving unloads block terrain and pooled presentation");
                File.WriteAllText(Path.Combine(output, "result.txt"), "PASS actual normal Boot host and InputSystem -> server material payment -> projectile -> host RPC -> cube fragments / asynchronous block terrain. Heroes 1/2/4 construct; heroes 3/5 dismantle a wall built with actual paid enemy shots. Old masks remain unpainted and their compatibility restore is exact. Cosmetics preserve terrain bytes, score version, material, charge and health; clear/leave clean presentation. Mismatched content signature rejected. This is one host, not an independent-client gate; screenshots require visual review.");
            }
            finally { if (camera != null) UnityEngine.Object.Destroy(camera.gameObject); }
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
    }

    // Shared by the migrated host tests in this assembly. It observes normal
    // simulation and uses real mouse input; it never emits synthetic/free projectiles.
    internal sealed class PaidBlockHostPlayRig : IDisposable
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly PrototypeApp _app;
        readonly PrototypePlayer _player;
        readonly Keyboard _oldKeyboard, _keyboard;
        readonly Mouse _oldMouse, _mouse;
        readonly Action<InkShot, double, Vector3> _oldTrace;
        readonly Action<InkShot, Collider, Vector3, Vector3, double> _oldContact;
        readonly Action<PaintStamp> _oldPaint;
        readonly Dictionary<uint, InkShot> _shots = new();
        readonly List<ContactObservation> _contactObservations = new(8);
        TraceObservation _firstTrace, _lastTrace;
        int _traceCount, _contactCount;
        int _fireAtAttempts;
        Dictionary<Vector3Int, byte> _publishedOwners;
        bool _watchPayments;
        WeaponRuntimeConfig _paymentWeapon;
        PlayerSnapshot _paymentSource;
        public readonly List<Vector3> ContactPositions = new();
        public bool DebrisSeen { get; private set; }
        public int AcceptedShots { get; private set; }
        public float MaterialPaid { get; private set; }
        public int Publications { get; private set; }
        public BlockAttackToken[] LastTokens => _shots.Values.Select(s => s.BlockAttack).Where(t => t != null).Distinct().ToArray();
        readonly struct TraceObservation
        {
            public readonly uint Id, Round;
            public readonly int Hero;
            public readonly double Age;
            public readonly Vector3 Point, Origin, Velocity;
            public TraceObservation(InkShot shot, double age, Vector3 point)
            { Id = shot.Id; Round = shot.Round; Hero = shot.HeroId; Age = age; Point = point; Origin = shot.Origin; Velocity = shot.Velocity; }
        }
        readonly struct ContactObservation
        {
            public readonly TraceObservation Trace;
            public readonly Vector3 Normal;
            public readonly int ColliderId, Layer;
            public readonly bool Enabled, Trigger, MapSurface, StableBlock, PaymentMatches, GeometryMatches;
            public readonly ulong Shooter, CreditOwner;
            public readonly uint CreditRound;
            public readonly byte Team, CreditTeam;
            public ContactObservation(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age,
                bool mapSurface, bool stableBlock, bool paymentMatches, bool geometryMatches)
            {
                Trace = new TraceObservation(shot, age, point); Normal = normal;
                ColliderId = collider != null ? collider.GetInstanceID() : 0; Layer = collider != null ? collider.gameObject.layer : -1;
                Enabled = collider != null && collider.enabled; Trigger = collider != null && collider.isTrigger;
                MapSurface = mapSurface; StableBlock = stableBlock; PaymentMatches = paymentMatches; GeometryMatches = geometryMatches;
                Shooter = shot.Shooter; Team = shot.Team; CreditOwner = shot.BlockAttack?.Credit.Owner ?? 0;
                CreditRound = shot.BlockAttack?.Credit.Round ?? 0; CreditTeam = shot.BlockAttack?.Credit.Team ?? 0;
            }
        }
        public PaidBlockHostPlayRig(PrototypeApp app, PrototypePlayer player)
        {
            _app = app; _player = player;
            _oldKeyboard = Keyboard.current; _oldMouse = Mouse.current;
            _keyboard = InputSystem.AddDevice<Keyboard>(); _mouse = InputSystem.AddDevice<Mouse>();
            Release();
            var match = PrototypeMatch.Current;
            _oldTrace = match.Projectiles.TraceObserved; _oldContact = match.Projectiles.ContactObserved; _oldPaint = match.Projectiles.PaintObserved;
            match.Projectiles.TraceObserved = (shot, age, at) =>
            {
                _oldTrace?.Invoke(shot, age, at);
                _shots[shot.Id] = shot;
                var observation = new TraceObservation(shot, age, at);
                if (_traceCount++ == 0) _firstTrace = observation;
                _lastTrace = observation;
            };
            match.Projectiles.ContactObserved = ContactObserved;
            match.Projectiles.PaintObserved = stamp => { _oldPaint?.Invoke(stamp); Assert.Fail("Default paid blocks cannot submit a legacy paint stamp"); };
            _publishedOwners = StableOwners(match.Blocks.World);
            match.Blocks.PublishedCommit += Published;
            _player.Snapshot.OnValueChanged += PaymentObserved;
        }
        void ContactObserved(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age)
        {
            _oldContact?.Invoke(shot, collider, point, normal, age);
            var match = PrototypeMatch.Current; var terrain = match != null ? match.Blocks : null;
            var token = shot.BlockAttack;
            bool mapSurface = collider != null && collider.GetComponentInParent<PaintSurface>() != null;
            bool stableBlock = terrain != null && terrain.IsStableCollider(collider);
            bool geometryMatches = collider != null && collider.enabled && !collider.isTrigger
                && (PlayerMotorSimulation.WorldMask & (1 << collider.gameObject.layer)) != 0
                && collider.GetComponentInParent<PrototypePlayer>() == null && (mapSurface || stableBlock);
            bool paymentMatches = _watchPayments && match != null && match.IsServer && terrain != null && token != null
                && shot.Shooter == _player.PlayerId && token.Credit.Owner == shot.Shooter
                && token.Credit.Kind == PaintAttackKind.Main && token.Credit.IsBlockPoints
                && token.Credit.Team == shot.Team && shot.Team == _paymentSource.Team
                && token.Credit.Round == shot.Round && shot.Round == match.State.Value.Round && shot.Round == terrain.Round
                && token.Credit.Equipment == shot.HeroRevision && shot.HeroRevision == _paymentSource.HeroRevision
                && shot.Lifecycle == _paymentSource.Revision && shot.HeroId == _paymentSource.HeroId;
            _contactCount++;
            if (_contactObservations.Count < 8)
                _contactObservations.Add(new ContactObservation(shot, collider, point, normal, age,
                    mapSurface, stableBlock, paymentMatches, geometryMatches));
            // The production collision carries the actual collider and surface
            // point. Do not reinterpret a trajectory centre as a physics probe.
            if (paymentMatches && geometryMatches) ContactPositions.Add(point);
        }
        string ContactDiagnostics()
        {
            string Trace(TraceObservation value) => FormattableString.Invariant($"id={value.Id} round={value.Round} hero={value.Hero} age={value.Age:R} point=({value.Point.x:R},{value.Point.y:R},{value.Point.z:R}) origin=({value.Origin.x:R},{value.Origin.y:R},{value.Origin.z:R}) velocity=({value.Velocity.x:R},{value.Velocity.y:R},{value.Velocity.z:R})");
            var result = new System.Text.StringBuilder($"Contact observation traces={_traceCount} contacts={_contactCount} accepted={ContactPositions.Count}");
            if (_traceCount > 0) result.Append("\nfirstTrace ").Append(Trace(_firstTrace)).Append("\nlastTrace ").Append(Trace(_lastTrace));
            foreach (var value in _contactObservations)
                result.Append("\ncontact ").Append(Trace(value.Trace)).Append(FormattableString.Invariant($" normal=({value.Normal.x:R},{value.Normal.y:R},{value.Normal.z:R}) collider={value.ColliderId} layer={value.Layer} enabled={value.Enabled} trigger={value.Trigger} map={value.MapSurface} stable={value.StableBlock} geometryMatches={value.GeometryMatches} paymentMatches={value.PaymentMatches} shooter={value.Shooter} team={value.Team} creditOwner={value.CreditOwner} creditTeam={value.CreditTeam} creditRound={value.CreditRound}"));
            return result.ToString();
        }
        string GravityCompletionDiagnostics(string stage)
        {
            // At most two wait observations plus one timeout observation, enabled
            // only by InkSeam. Read published state; never drive or repair terrain.
            try
            {
                var match = PrototypeMatch.Current; var terrain = match.Blocks;
                var state = _player.Snapshot.Value; var controller = _player.GetComponent<CharacterController>();
                var bounds = controller.bounds; var world = terrain.World;
                var result = new System.Text.StringBuilder(FormattableString.Invariant($"[completion gravity {stage}] realtime={Time.realtimeSinceStartupAsDouble:R} frame={Time.frameCount} terrainTime={terrain.SimulationTime:R} revision={terrain.Revision} pending={terrain.PendingTransactions} fallingCells={terrain.FallingCount} fallingRuns={world.Falling.Count} cohorts={world.Cohorts.Count} unstableColumns={terrain.UnstableColumns} oldestPendingSeconds={terrain.OldestPendingSeconds:R}\n"));
                result.Append(FormattableString.Invariant($"actor snapshotPosition=({state.Position.x:R},{state.Position.y:R},{state.Position.z:R}) controllerPosition=({controller.transform.position.x:R},{controller.transform.position.y:R},{controller.transform.position.z:R}) boundsCenter=({bounds.center.x:R},{bounds.center.y:R},{bounds.center.z:R}) boundsExtents=({bounds.extents.x:R},{bounds.extents.y:R},{bounds.extents.z:R}) grounded={state.Grounded} swimming={state.Swimming} compact={state.CompactBody} safetyRecoveries={terrain.SafetyRecoveries} safetyFailures={terrain.SafetyRecoveryFailures}\n"));
                result.Append("commandHead ").Append(JsonUtility.ToJson(terrain.InspectCommandHead())).Append("\n")
                    .Append("pendingHead ").Append(JsonUtility.ToJson(terrain.InspectPendingTransaction())).Append("\n")
                    .Append("pendingWork ").Append(JsonUtility.ToJson(terrain.InspectPendingWork())).Append("\n")
                    .Append("buildWork ").Append(JsonUtility.ToJson(terrain.InspectBuildWork())).Append("\n");
                result.Append(FormattableString.Invariant($"progress authorityStages={terrain.AuthorityStageCount} gravityStages={terrain.GravityProgressStages} gravityChecks={terrain.GravityWorkChecks} commandStages={terrain.CommandProgressStages} failures={terrain.FailedTransactions} privateActive={terrain.HasPrivatePreparation} privateVersion={terrain.PrivatePreparationProgressVersion} privateStarts={terrain.PrivatePreparationStarts} privateStages={terrain.PrivatePreparationStages} privateAborts={terrain.PrivatePreparationAborts} headVersion={terrain.HeadPreparationProgressVersion} headStarts={terrain.HeadPreparationStarts} headStages={terrain.HeadPreparationStages} headAborts={terrain.HeadPreparationAborts} buildVersion={terrain.BuildOccupancyProgressVersion} buildStarts={terrain.BuildOccupancyPreparationStarts} buildStages={terrain.BuildOccupancyPreparationStages} historicalVersion={terrain.HistoricalRecoveryProgressVersion} historicalStarts={terrain.HistoricalRecoveryStarts} historicalStages={terrain.HistoricalRecoveryStages} historicalOutcome={terrain.LastHistoricalRecoveryOutcome} historicalRecovered={terrain.HistoricalRecoveryCount} historicalNoCapacity={terrain.HistoricalRecoveryNoCapacity} historicalConflicts={terrain.HistoricalRecoveryPublicationConflicts}\n"));
                result.Append(FormattableString.Invariant($"publication metadata={terrain.FairBackgroundMetadataPublications} terrain={terrain.FairBackgroundTerrainPublications} foreground={terrain.FairForegroundAtomicPublications} rebuilds={terrain.FairBackgroundRebuilds} blockedScans={terrain.FairCompleteBlockedScans} physicalFailures={terrain.FairPhysicalFailures} seedSeconds={terrain.FairCurrentSeedSeconds:R} staticProofInvalidations={terrain.StaticProofInvalidations} ownLandingDeferrals={terrain.OwnLandingPublicationDeferrals} supportRetries={terrain.GravitySupportPublicationRetries} bindingDeferrals={terrain.GravityBindingPublicationDeferrals} buildRetries={terrain.BuildPublicationRetries} cpuDeferrals={terrain.PublicationCpuDeferrals}\n"));
                // Pending timestamps may be untracked because this diagnostic
                // deliberately does not subscribe or change tracking behaviour.
                result.Append(UntrackedPendingGravityDiagnostics(terrain)).Append("\n");
                int shown = 0;
                foreach (var run in world.Falling.Values)
                {
                    if (shown++ >= 16) { result.Append("fallingRuns truncatedAt=16\n"); break; }
                    float offset = run.Offset(terrain.SimulationTime), speed = run.Speed(terrain.SimulationTime);
                    var at = BlockWorld.Center(run.Bottom) - Vector3.up * offset;
                    result.Append(FormattableString.Invariant($"run id={run.Id} bottom=({run.Bottom.x},{run.Bottom.y},{run.Bottom.z}) currentBottomCenter=({at.x:R},{at.y:R},{at.z:R}) storageCells={run.Cells.Length} occupiedCells={run.Cells.Count(c => c.Occupied)} startedAt={run.StartedAt:R} initialOffset={run.InitialOffset:R} initialSpeed={run.InitialSpeed:R} offset={offset:R} speed={speed:R} landingKnown={run.LandingKnown} landingOffset={run.LandingOffset:R}"));
                    var cohort = run.Cohort;
                    if (cohort != null)
                        result.Append(FormattableString.Invariant($" cohort={cohort.Id} membershipRevision={cohort.MembershipRevision} phase={cohort.Phase} bottom=({cohort.Bottom.x},{cohort.Bottom.y},{cohort.Bottom.z}) topExclusive={cohort.TopExclusive} detachCursor={cohort.DetachCursor} members={cohort.MemberIds.Count} needsLandingProof={cohort.NeedsLandingProof} needsRegroup={cohort.NeedsRegroup} lowerSupport={cohort.LowerSupport?.Id ?? 0} supportClearance={cohort.SupportClearance:R} rebasePending={cohort.RebasePending} rebaseSourceMoving={cohort.RebaseSourceMoving} rebaseSourceLandingKnown={cohort.RebaseSourceLandingKnown} rebaseSourceLandingOffset={cohort.RebaseSourceLandingOffset:R}"));
                    result.Append(" occupied=");
                    for (int i = 0; i < Math.Min(16, run.Cells.Length); i++)
                        if (run.Cells[i].Occupied) result.Append(i).Append(':').Append(run.Cells[i].Owner).Append(',');
                    result.Append("\n");
                }
                return result.ToString();
            }
            catch (Exception error)
            { return "completion gravity diagnostic unavailable: " + error.GetType().Name + ": " + error.Message; }
        }
        static string UntrackedPendingGravityDiagnostics(BlockTerrain terrain)
        {
            // Read task statuses only. No Result, Wait, invocation, subscription,
            // writable reference or retry is used, and at most 8 pieces are read.
            try
            {
                var pending = typeof(BlockTerrain).GetField("_pending", Private).GetValue(terrain);
                if (pending == null) return "pendingTask absent";
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object Read(object value, string name) => value.GetType().GetField(name, fields)?.GetValue(value);
                var task = Read(pending, "Generate") as System.Threading.Tasks.Task;
                var pieces = Read(pending, "Pieces") as Array;
                var next = Read(pending, "Next") as BlockWorld;
                var result = new System.Text.StringBuilder($"pendingTask generation={task?.Status.ToString() ?? "absent"} uploaded={Read(pending, "Uploaded")} pieces={pieces?.Length ?? -1} remote={Read(pending, "Remote")} privateGravity={Read(pending, "PrivateGravity")} pureAuthorityBackground={Read(pending, "PureAuthorityBackground")} fairForeground={Read(pending, "FairForeground")} fairDependency={Read(pending, "FairDependency")} nextRevision={next?.Revision} nextRuns={next?.Falling.Count} nextCohorts={next?.Cohorts.Count}");
                if (pieces != null)
                    for (int i = 0; i < Math.Min(8, pieces.Length); i++)
                    {
                        var piece = pieces.GetValue(i); var bake = piece == null ? null : Read(piece, "Bake") as System.Threading.Tasks.Task;
                        result.Append($" bake[{i}]={bake?.Status.ToString() ?? "absent"}");
                    }
                return result.ToString();
            }
            catch (Exception error)
            { return "pendingTask diagnostic unavailable: " + error.GetType().Name + ": " + error.Message; }
        }

        string CompletionDiagnostics()
        {
            var match = PrototypeMatch.Current; var terrain = match.Blocks; var state = _player.Snapshot.Value;
            // Preserve the original completion predicate; identify its outstanding
            // conjuncts once on timeout, without driving any simulation or input.
            return FormattableString.Invariant($"Completion pendingProjectiles={match.Projectiles.PendingCount} activeProjectiles={match.Projectiles.ActiveCount} pendingTerrain={terrain.PendingTransactions} falling={terrain.FallingCount} weapon={state.WeaponPhase} scoreRevision={match.State.Value.TerrainRevision} terrainRevision={terrain.Revision} terrainTime={terrain.SimulationTime:R} serverTime={match.NetworkManager.ServerTime.Time:R} playerTime={state.SimulatedAt:R} inputTimedOut={state.InputTimedOut} nextShotAt={state.NextShotAt:R} recoverAt={state.InkRecoverAt:R} ink={state.Ink:R}\n")
                + InputDiagnostics("completion timeout") + "\n" + ContactDiagnostics();
        }
        public static string NewOutput(string name)
        {
            string path = Path.GetFullPath("Reports/BlockTerrain/FunctionalCoverage89/ImpactSeamPaidCandidates/Executions/" +
                name + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        void Release()
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update();
        }
        void Observe()
        {
            var match = PrototypeMatch.Current;
            Assert.That(match.enabled && _player.enabled && match.Blocks.enabled, Is.True, "Normal simulation must remain enabled");
            Assert.That(Time.timeScale, Is.EqualTo(1f)); Assert.That(Time.captureFramerate, Is.Zero);
            if (InkPresentation.Current != null && InkPresentation.Current.BlockDebrisCount > 0) DebrisSeen = true;
        }
        public IEnumerator Wait(Func<bool> ready, string message, double seconds = 40, Func<string> failureDetails = null, Action observeWhileWaiting = null)
        {
            double end = Time.realtimeSinceStartupAsDouble + seconds;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) { Observe(); observeWhileWaiting?.Invoke(); yield return null; }
            Observe(); bool completed = ready();
            Assert.That(completed, Is.True, completed || failureDetails == null ? message : message + "\n" + failureDetails());
        }
        string InputDiagnostics(string stage)
        {
            // Two bounded observations only: after the real queued event and on
            // emission timeout. Read diagnostics without changing input state/focus.
            try
            {
                var state = _player.Snapshot.Value; var settings = InputSystem.settings;
                var window = EditorWindow.focusedWindow;
                var last = (PlayerInputFrame)typeof(PrototypePlayer).GetField("_lastInput", Private).GetValue(_player);
                var queued = (SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(_player);
                return FormattableString.Invariant($"[{stage}] frame={Time.frameCount} realtime={Time.realtimeSinceStartupAsDouble:R} inputUpdate={InputState.currentUpdateType} focus={Application.isFocused} focusedWindow={window?.GetType().Name}/{window?.titleContent.text} cursor={Cursor.lockState}\n")
                    + $"expectedMouse={MouseDiagnostics(_mouse)} currentMouse={MouseDiagnostics(Mouse.current)}\n"
                    + $"expectedKeyboard={KeyboardDiagnostics(_keyboard)} currentKeyboard={KeyboardDiagnostics(Keyboard.current)}\n"
                    + $"settings update={settings.updateMode} background={settings.backgroundBehavior} editor={settings.editorInputBehaviorInPlayMode}\n"
                    + $"control inRoom={_app.InRoom} busy={_app.Busy} overlay={_app.Overlay} hasControl={_app.HasControl} canFire={_app.CanFireInput} spawned={_player.IsSpawned} server={_player.IsServer} owner={_player.IsOwner} playerEnabled={_player.enabled} controllerEnabled={_player.GetComponent<CharacterController>().enabled}\n"
                    + FormattableString.Invariant($"snapshot hero={state.HeroId} team={state.Team} revision={state.Revision} heroRevision={state.HeroRevision} alive={state.IsAlive} canMove={state.CanMove} life={state.LifeState} health={state.Health:R} ink={state.Ink:R} swimming={state.Swimming} compact={state.CompactBody} weapon={state.WeaponPhase} needsRelease={state.AttackNeedsRelease} shots={state.ShotSequence} consumedFire={state.ConsumedFire} consumedRelease={state.ConsumedRelease} simulationTick={state.SimulationTick} simulatedAt={state.SimulatedAt:R} ack={state.AcknowledgedInput} timedOut={state.InputTimedOut} inputExpiredAt={state.InputExpiredAt:R} nextShotAt={state.NextShotAt:R} sub={state.SubPhase} special={state.SpecialPhase}\n")
                    + $"local sequence={typeof(PrototypePlayer).GetField("_sequence", Private).GetValue(_player)} fire={typeof(PrototypePlayer).GetField("_fireSequence", Private).GetValue(_player)} release={typeof(PrototypePlayer).GetField("_releaseSequence", Private).GetValue(_player)} lastReceivedAt={typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).GetValue(_player)} queuedInputs={queued.Count}\n"
                    + $"lastInput {InputFrameDiagnostics(last)}" + (queued.Count == 0 ? "" : "\nfirstQueuedInput " + InputFrameDiagnostics(queued.First().Value));
            }
            catch (Exception error) { return $"[{stage}] diagnostic unavailable: {error.GetType().Name}: {error.Message}"; }
        }
        static string MouseDiagnostics(Mouse mouse)
            => mouse == null ? "null" : $"id={mouse.deviceId},added={mouse.added},enabled={mouse.enabled},native={mouse.native},background={mouse.canRunInBackground},held={mouse.leftButton.isPressed}";
        static string KeyboardDiagnostics(Keyboard keyboard)
            => keyboard == null ? "null" : $"id={keyboard.deviceId},added={keyboard.added},enabled={keyboard.enabled},native={keyboard.native},background={keyboard.canRunInBackground},shift={keyboard.leftShiftKey.isPressed},escape={keyboard.escapeKey.isPressed},sub={keyboard.eKey.isPressed},special={keyboard.qKey.isPressed}";
        static string InputFrameDiagnostics(PlayerInputFrame input)
            => $"sequence={input.Sequence},tick={input.Tick},revision={input.Revision},heroRevision={input.HeroRevision},fire={input.Fire},cancelFire={input.CancelFire},fireSequence={input.FireSequence},releaseSequence={input.ReleaseSequence},swim={input.Swim},sub={input.SubHeld},cancelSub={input.CancelSub}";
        bool NeutralInputAcknowledged(uint after)
        {
            var state = _player.Snapshot.Value;
            var input = (PlayerInputFrame)typeof(PrototypePlayer).GetField("_lastInput", Private).GetValue(_player);
            return state.AcknowledgedInput > after && input.Sequence == state.AcknowledgedInput
                && input.Revision == state.Revision && input.HeroRevision == state.HeroRevision
                && !input.Fire && !input.CancelFire && !state.AttackNeedsRelease;
        }
        public IEnumerator Ready()
        {
            Assert.That(_app.IsWeaponDebugRoom, Is.False);
            Assert.That(PrototypeMatch.Current.State.Value.Phase, Is.EqualTo(MatchPhase.Practice));
            Assert.That(InkPresentation.Current.BlockMode, Is.True);
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); view.Show(); view.Focus();
            _app.CaptureMouse(true);
            yield return Wait(() => _app.CanFireInput && Application.isFocused, "Real focused input enabled after neutral release");
        }
        public IEnumerator SelectTeam(byte team)
        {
            Release();
            if (_player.Snapshot.Value.Team == team) yield break;
            Assert.That(_player.TeamChangeUnavailableReason, Is.Null);
            _player.RequestTeamChange();
            yield return Wait(() => !_player.TeamChangePending && _player.Snapshot.Value.Team == team, "Normal team request to " + team);
        }
        public IEnumerator SelectHero(int hero)
        {
            Release();
            _player.RequestHeroChange(hero, HeroSelectionOrigin.Warmup);
            yield return Wait(() => !_player.HeroChangePending && _player.Snapshot.Value.HeroId == hero &&
                _player.CharacterView != null && _player.Presentation != null, "Real warmup hero selection " + hero);
        }
        void PlaceAndAim(Vector3 target, Vector3 feetSeed)
        {
            Assert.That(Physics.Raycast(feetSeed + Vector3.up * 6, Vector3.down, out var floor, 20, PlayerMotorSimulation.WorldMask), Is.True, "Actual safe firing floor");
            var state = _player.Snapshot.Value;
            state.Position = floor.point + Vector3.up * .04f; state.VerticalSpeed = 0;
            state.Velocity = state.PlanarVelocity = Vector3.zero; state.Grounded = true;
            Assert.That(state.Swimming || state.CompactBody, Is.False, "Neutral input leaves a normal human body");
            Vector2 look = _player.Look;
            for (int i = 0; i < 6; i++)
            {
                state.Yaw = look.x; state.Pitch = look.y;
                var pivot = state.Position + PrototypePlayer.CameraPivotOffset(state, _player.Presentation);
                var origin = PrototypePlayer.CameraPosition(pivot, Quaternion.Euler(look.y, look.x, 0), _player.Presentation);
                var delta = target - origin;
                look = new Vector2(Mathf.Repeat(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 360),
                    Mathf.Clamp(-Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg, -65, 75));
            }
            state.Yaw = state.BodyYaw = look.x; state.Pitch = look.y;
            var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(_player);
            Assert.That(motor.CanStand(state.Position), Is.True, "The firing placement must fit the real body");
            motor.Restore(state); _player.Snapshot.Value = state;
            typeof(PrototypePlayer).GetField("_look", Private).SetValue(_player, look);
        }
        string PreFireIdleDiagnostics(int attempt, PlayerSnapshot first, double firstServerTime, double firstTerrainTime, int firstFrame, float maxInk)
        {
            var match = PrototypeMatch.Current; var terrain = match.Blocks; var state = _player.Snapshot.Value;
            double serverTime = _app.Manager.ServerTime.Time;
            // Read every original pre-fire conjunct once on timeout. Do not
            // advance clocks, recover resources, or drive another simulation.
            return FormattableString.Invariant($"[pre-fire idle timeout] attempt={attempt} hero={state.HeroId} shots={state.ShotSequence} firstShots={first.ShotSequence} frame={Time.frameCount} firstFrame={firstFrame} serverTime={serverTime:R} firstServerTime={firstServerTime:R} simulatedAt={state.SimulatedAt:R} firstSimulatedAt={first.SimulatedAt:R} terrainTime={terrain.SimulationTime:R} firstTerrainTime={firstTerrainTime:R}\n")
                + FormattableString.Invariant($"conjuncts projectilesIdle={match.Projectiles.PendingCount == 0} terrainIdle={terrain.PendingTransactions == 0} fallingIdle={terrain.FallingCount == 0} materialFull={state.Ink >= maxInk - .001f} weaponIdle={state.WeaponPhase == WeaponPhase.Idle} cooldownDone={serverTime >= state.NextShotAt}\n")
                + FormattableString.Invariant($"values pendingProjectiles={match.Projectiles.PendingCount} activeProjectiles={match.Projectiles.ActiveCount} pendingTerrain={terrain.PendingTransactions} falling={terrain.FallingCount} material={state.Ink:R} firstMaterial={first.Ink:R} maxMaterial={maxInk:R} weapon={state.WeaponPhase} firstWeapon={first.WeaponPhase} nextShotAt={state.NextShotAt:R} firstNextShotAt={first.NextShotAt:R} inkRecoverAt={state.InkRecoverAt:R} sub={state.SubPhase} mistUntil={state.MistUntil:R} scoreRevision={match.State.Value.TerrainRevision} terrainRevision={terrain.Revision}\n")
                + InputDiagnostics("pre-fire idle timeout") + "\n" + ContactDiagnostics();
        }
        public IEnumerator FireAt(Vector3 target, Vector3 feetSeed, bool observeCompletionGravity = false)
        {
            Release();
            var match = PrototypeMatch.Current; var terrain = match.Blocks; var presentation = InkPresentation.Current;
            var weapon = GameplayConfig.GetWeapon(_player.Snapshot.Value.HeroId); var hero = GameplayConfig.GetHero(_player.Snapshot.Value.HeroId);
            int attempt = ++_fireAtAttempts; var first = _player.Snapshot.Value;
            double firstServerTime = _app.Manager.ServerTime.Time, firstTerrainTime = terrain.SimulationTime; int firstFrame = Time.frameCount;
            yield return Wait(() => match.Projectiles.PendingCount == 0 && terrain.PendingTransactions == 0 && terrain.FallingCount == 0 &&
                _player.Snapshot.Value.Ink >= hero.MaxInk - .001f && _player.Snapshot.Value.WeaponPhase == WeaponPhase.Idle &&
                _app.Manager.ServerTime.Time >= _player.Snapshot.Value.NextShotAt, "Real recovery/cooldown and terrain idle before firing", failureDetails: () => PreFireIdleDiagnostics(attempt, first, firstServerTime, firstTerrainTime, firstFrame, hero.MaxInk));
            presentation.Clear(); double cutoff = _app.Manager.ServerTime.Time;
            yield return Wait(() => terrain.SimulationTime >= cutoff, "Normal simulation passes the cosmetic clear cutoff");
            PlaceAndAim(target, feetSeed);
            _app.CaptureMouse(true);
            yield return Wait(() => _app.CanFireInput, "Normal overlay releases its input latch");
            // The overlay latch can clear in Update before a normal authority
            // step has consumed release. Send neutral input through the same
            // InputSystem path and wait for its actual server acknowledgment.
            uint neutralAfter = _player.Snapshot.Value.AcknowledgedInput;
            Release();
            yield return Wait(() => NeutralInputAcknowledged(neutralAfter), "Real neutral input acknowledged before pressing fire", 20,
                () => InputDiagnostics("neutral input timeout"));
            _shots.Clear(); ContactPositions.Clear(); DebrisSeen = false;
            _contactObservations.Clear(); _traceCount = _contactCount = 0; _firstTrace = _lastTrace = default;
            var before = _player.Snapshot.Value;
            AcceptedShots = 0; MaterialPaid = 0; _paymentWeapon = weapon; _paymentSource = before; _watchPayments = true;
            InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Left)); InputSystem.Update();
            string submittedInput = InputDiagnostics("after queued press");
            try
            {
                if (WeaponSimulation.IsSplatling(weapon))
                {
                    yield return Wait(() => _player.Snapshot.Value.WeaponPhase == WeaponPhase.Charging &&
                        _player.Snapshot.Value.SplatlingLoaded > 0, "Real Splatling charge reserves at least one round", 20);
                    Assert.That(_player.Snapshot.Value.ShotSequence, Is.EqualTo(before.ShotSequence),
                        "Charging alone cannot emit a shot or create an attack budget");
                    Release();
                }
                yield return Wait(() => _player.Snapshot.Value.ShotSequence > before.ShotSequence, "Real InputSystem -> paid server emission", 20,
                    () => submittedInput + "\n" + InputDiagnostics("emission timeout") + "\n" + ContactDiagnostics());
                Release();
                double completionDiagnosticStarted = Time.realtimeSinceStartupAsDouble;
                var gravitySamples = observeCompletionGravity ? new List<string>(2) : null;
                void SampleCompletionGravity()
                {
                    if (gravitySamples == null || gravitySamples.Count >= 2) return;
                    double elapsed = Time.realtimeSinceStartupAsDouble - completionDiagnosticStarted;
                    if (gravitySamples.Count == 0 || elapsed >= 20)
                        gravitySamples.Add(GravityCompletionDiagnostics(gravitySamples.Count == 0 ? "begin" : "midpoint"));
                }
                string CompletionDetails() => CompletionDiagnostics() + (gravitySamples == null ? "" :
                    "\n" + string.Join("\n", gravitySamples) + "\n" + GravityCompletionDiagnostics("timeout"));
                yield return Wait(() => match.Projectiles.PendingCount == 0 && terrain.PendingTransactions == 0 && terrain.FallingCount == 0 &&
                    _player.Snapshot.Value.WeaponPhase == WeaponPhase.Idle && match.State.Value.TerrainRevision == terrain.Revision,
                    "Normal projectile/collision/score completion after the real release", failureDetails: CompletionDetails,
                    observeWhileWaiting: observeCompletionGravity ? SampleCompletionGravity : null);
                Assert.That(AcceptedShots, Is.EqualTo(checked((int)(_player.Snapshot.Value.ShotSequence - before.ShotSequence))));
                Assert.That(MaterialPaid, Is.GreaterThan(0), "Actual payment observations include late accepted shots before the release reaches the server");
            }
            finally { Release(); _watchPayments = false; }
            Assert.That(_shots.Count, Is.GreaterThanOrEqualTo(weapon.PelletCount), "Observed actual production Spawn trajectories");
            Assert.That(LastTokens.Length, Is.GreaterThan(0));
            Assert.That(LastTokens.All(t => t.Credit.Owner == _player.PlayerId && t.Credit.Team == before.Team && t.Credit.Kind == PaintAttackKind.Main), Is.True);
            Assert.That(match.PaintSequence, Is.Zero); Assert.That(match.AppliedPaintSequence, Is.Zero);
            if (ContactPositions.Count == 0) Debug.Log(ContactDiagnostics());
        }
        void PaymentObserved(PlayerSnapshot prior, PlayerSnapshot next)
        {
            if (!_watchPayments) return;
            Assert.That(next.Revision, Is.EqualTo(_paymentSource.Revision)); Assert.That(next.HeroRevision, Is.EqualTo(_paymentSource.HeroRevision));
            // Splatling charge moves material from the tank to a reservation. Only
            // an emitted round reduces their sum; release can return a fraction.
            bool reserved = WeaponSimulation.IsSplatling(_paymentWeapon);
            float priorMaterial = prior.Ink + (reserved ? prior.SplatlingReservedInk : 0);
            float nextMaterial = next.Ink + (reserved ? next.SplatlingReservedInk : 0);
            if (next.ShotSequence > prior.ShotSequence)
            {
                int count = checked((int)(next.ShotSequence - prior.ShotSequence));
                float debit = priorMaterial - nextMaterial;
                Assert.That(debit, Is.EqualTo(count * WeaponSimulation.InkCost(_paymentWeapon, next.LastShotCharge)).Within(.003f), "Authoritative snapshot mutation pays every accepted shot once");
                AcceptedShots += count; MaterialPaid += debit;
            }
            else
            {
                Assert.That(next.ShotSequence, Is.EqualTo(prior.ShotSequence));
                Assert.That(nextMaterial, Is.GreaterThanOrEqualTo(priorMaterial - .001f), "No second material debit on charge, terrain or credit snapshot updates; ordinary recovery remains enabled");
            }
        }
        void Published(BlockTerrainCommit commit)
        {
            var match = PrototypeMatch.Current; var terrain = match.Blocks;
            Assert.That(commit.Revision, Is.EqualTo(terrain.Revision)); Assert.That(commit.Round, Is.EqualTo(terrain.Round));
            Assert.That(match.State.Value.TerrainRevision, Is.EqualTo(terrain.Revision));
            Assert.That(match.State.Value.PinkBlockCount, Is.EqualTo(terrain.PinkCount));
            Assert.That(match.State.Value.BlueBlockCount, Is.EqualTo(terrain.BlueCount));
            var owners = StableOwners(terrain.World);
            Assert.That(owners.Count + terrain.FallingCount, Is.EqualTo(terrain.PinkCount + terrain.BlueCount));
            foreach (var pair in _publishedOwners)
                if (!owners.ContainsKey(pair.Key))
                    Assert.That(Physics.Raycast(BlockWorld.Center(pair.Key) + Vector3.up * .04f, Vector3.down, .08f, 1 << BlockTerrain.TerrainLayer), Is.False, "Removed cell cannot retain a stale collision face");
            AssertStableCollision(terrain);
            foreach (var mesh in terrain.GetComponentsInChildren<MeshCollider>())
                if (mesh.enabled && mesh.gameObject.layer == BlockTerrain.TerrainLayer)
                {
                    Assert.That(mesh.sharedMesh, Is.Not.Null);
                    var renderer = mesh.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        Assert.That(renderer.enabled && renderer.gameObject.activeInHierarchy, Is.True);
                        Assert.That(mesh.GetComponent<MeshFilter>().sharedMesh, Is.Not.Null, "Published visible geometry accompanies collision");
                    }
                }
            _publishedOwners = owners; Publications++;
        }
        public static Dictionary<Vector3Int, byte> StableOwners(BlockWorld world)
        {
            var result = new Dictionary<Vector3Int, byte>();
            foreach (var chunk in world.Chunks)
                for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
                    if (chunk.Value.Cells[index].Occupied) result.Add(BlockWorld.CellFromIndex(chunk.Key, index), chunk.Value.Cells[index].Owner);
            return result;
        }
        public static void AssertStableCollision(BlockTerrain terrain)
        {
            foreach (var pair in StableOwners(terrain.World))
                if (!terrain.World.TryGet(pair.Key + Vector3Int.up, out _))
                {
                    Assert.That(Physics.Raycast(BlockWorld.Center(pair.Key) + Vector3.up * .06f, Vector3.down, out var hit, .12f, 1 << BlockTerrain.TerrainLayer), Is.True, "Published exposed block top");
                    Assert.That(terrain.TryContact(hit, out var contact), Is.True);
                    Assert.That(contact.Stable, Is.True); Assert.That(contact.Cell, Is.EqualTo(pair.Key));
                    Assert.That(contact.Owner, Is.EqualTo(pair.Value));
                    Assert.That(contact.Revision, Is.EqualTo(terrain.ChunkRevision(BlockWorld.ChunkAt(pair.Key))));
                }
        }
        public void Dispose()
        {
            Release();
            var match = PrototypeMatch.Current;
            if (match != null) { match.Projectiles.TraceObserved = _oldTrace; match.Projectiles.ContactObserved = _oldContact; match.Projectiles.PaintObserved = _oldPaint; if (match.Blocks != null) match.Blocks.PublishedCommit -= Published; }
            if (_player != null) _player.Snapshot.OnValueChanged -= PaymentObserved;
            InputSystem.RemoveDevice(_keyboard); InputSystem.RemoveDevice(_mouse);
            if (_oldKeyboard != null) _oldKeyboard.MakeCurrent(); if (_oldMouse != null) _oldMouse.MakeCurrent();
        }
    }
}
#endif
