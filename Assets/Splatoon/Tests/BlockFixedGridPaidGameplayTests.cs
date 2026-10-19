#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // INSTALLATION DRAFT ONLY. This file has not been imported, compiled or executed.
    // Same-process semantic evidence; it cannot certify a remote owner or a Player package.
    [Category("BlockFixedGridPaidGameplay65")]
    public sealed partial class BlockFixedGridPaidGameplayTests
    {
        const float Dt = 1f / 60;
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo MatchFixedUpdate = typeof(PrototypeMatch).GetMethod("FixedUpdate", Private);
        static readonly FieldInfo MatchTime = typeof(PrototypeMatch).GetField("_simulationTime", Private);
        static readonly FieldInfo MatchOffset = typeof(PrototypeMatch).GetField("_networkFrameOffset", Private);
        PrototypeMatch _match;
        PrototypePlayer _host;
        Action _serverTick;
        bool _tickDetached, _matchWasEnabled, _matchPaused, _driving, _observing, _driverPrepared, _cursorBaselineCaptured;
        double _savedTime, _savedOffset, _now;
        int _cursorWrites, _observerFaults, _motorFaults;
        string _phase = "not-started";
        StreamWriter _writer;
        PaperCapture _oracle;
        readonly Dictionary<PrototypePlayer, bool> _playerEnabled = new();
        readonly List<BlockAttackObservation.Payment> _payments = new();
        readonly List<BlockAttackObservation.ActualResult> _actual = new();
        readonly List<SubLifecycleEvent> _lifecycle = new();
        readonly List<SubEffectEvent> _effects = new();
        readonly HashSet<uint> _seenLifecycle = new(), _seenEffects = new();
        readonly HashSet<ulong> _motorInvocations = new();
        BlockTerrain Terrain => _match.Blocks;
        BlockFixedGridWorld World => Terrain.FixedBlocks;
        byte Team => _host.Snapshot.Value.Team;
        byte Enemy => Team == 1 ? (byte)2 : (byte)1;
        PlayerMotorSimulation Motor => (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(_host);
        SortedDictionary<uint, PlayerInputFrame> Inputs => (SortedDictionary<uint, PlayerInputFrame>)
            typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(_host);

        [Serializable] sealed class Trace
        {
            public string test, stage, detail, snapshot, match, fixedSnapshot;
            public double simulationTime, networkTime;
            public uint epoch, round, revision, surfaceRevision;
            public int fixedCount, pinkCount, blueCount, cursorWrites;
            public Vector3 transformPosition, controllerCenter;
            public float controllerHeight, controllerRadius;
            public bool controllerEnabled, controllerGrounded, paperEnabled;
            public int paperPoseVersion, paperRectCount;
            public Vector3 paperPosition;
            public Quaternion paperRotation;
        }
        [Serializable] sealed class Identity
        {
            public string assembly, unity, buildGuid, roomSignature, mapPath, mapGuid, topology;
            public string profileGuid, profileContent, runnerIdentity;
            public int paintProtocol, blockProtocol, weaponSimulation, paperSimulation, fixedSimulation;
            public int playerCount, simulationRate, networkRate;
        }
        [Serializable] sealed class InputTrace
        { public PlayerInputFrame input; public PlayerSnapshot before, after; public int queuedBefore, queuedAfter, motorInvocations; }
        [Serializable] sealed class EntityTrace
        {
            public SubEntityState state;
            public string stateJson;
            public bool removed, attached, blockAttachment;
            public HandleTrace handle;
            public Vector3 localOffset;
            public double fuse;
            public int buildLimit, buildRemaining, added, removedEnemy;
            public double awardedPoints;
        }
        [Serializable] sealed class SourceTrace
        {
            public uint round, life, equipment, configRevision;
            public ulong owner, action;
            public byte team;
            public PaintAttackKind kind;
            public int hero, subWeapon;
            public SourceTrace(BlockAttackObservation.Source s)
            { round=s.Round; life=s.Life; equipment=s.Equipment; configRevision=s.ConfigRevision;
                owner=s.Owner; action=s.Action; team=s.Team; kind=s.Kind; hero=s.Hero; subWeapon=s.SubWeaponId; }
        }
        [Serializable] sealed class PaymentTrace
        { public SourceTrace source; public double material, serverTime; public uint networkTick; }
        [Serializable] sealed class ActualTrace
        {
            public SourceTrace source;
            public BlockAttackObservation.ActualOperation operation;
            public BlockAttackObservation.ActualCounts counts;
            public uint entity, ordinal, epoch, round, revision, surfaceRevision, networkTick;
            public double serverTime, milliseconds;
            public bool changes, auxiliary, contact;
            public int newFixed,newPaint,replacedPaint,removedEnemyFixed,removedFriendlyFixed,removedEnemyPaint,removedFriendlyPaint,converted,relocated;
        }
        [Serializable] sealed class HandleTrace
        {
            public uint epoch,round,generation; public ulong id;
            public HandleTrace(BlockFixedGridHandle h){epoch=h.Epoch;round=h.Round;generation=h.Generation;id=h.Id;}
        }
        [Serializable] sealed class StateTrace
        {
            public HandleTrace handle; public Vector3Int cell; public byte owner; public uint revision;
            public int supportId,baseCellY; public float supportHeight; public string supportKey;
            public StateTrace(BlockFixedGridState s){handle=new HandleTrace(s.Handle);cell=s.Cell;owner=s.Owner;revision=s.Revision;
                supportId=s.Support.Id;baseCellY=s.Support.BaseCellY;supportHeight=s.Support.Height;supportKey=s.Support.SurfaceKey.ToString();}
        }
        [Serializable] sealed class WorldTrace
        { public string wireBase64; public uint epoch,round,revision,surfaceRevision; public ulong nextId; public StateTrace[] states; }
        [Serializable] sealed class LifecycleTrace
        { public uint sequence,round,id,action; public SubLifecycleKind kind; public string stateJson; }
        [Serializable] sealed class MapTrace
        { public string collider,key; public int instance; public Vector3 point, normal; public byte owner; }
        [Serializable] sealed class WallTrace
        { public HandleTrace handle; public Vector3 point, normal; public Vector3Int cell; public byte owner; }
        [Serializable] sealed class PoseOracleTrace
        { public int rectangles, fixedCount; public bool capsuleClear, paperClear, productionFits; public string profileContent; }

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + 60;
            while (!ready() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        [UnitySetUp] public IEnumerator Setup()
        {
            _driverPrepared = _cursorBaselineCaptured = _matchPaused = false;
            // This must be configured by the runner BEFORE PrototypeApp's static initialization.
            // Never change the readonly suppression flag or make a system cursor call from this fixture.
            Assert.That((bool)typeof(PrototypeApp).GetProperty("SystemCursorWritesSuppressed",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.True,
                "Runner must use SPLATOON_DISABLE_SYSTEM_CURSOR=1 / -noSystemCursor (or batch mode).");
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            _payments.Clear(); _actual.Clear(); _lifecycle.Clear(); _effects.Clear();
            _seenLifecycle.Clear(); _seenEffects.Clear(); _motorInvocations.Clear(); _playerEnabled.Clear();
            _phase="setup"; _observing=_driving=_tickDetached=false;
            string folder = "Reports/BlockSimulation20261018/Gameplay65";
            Directory.CreateDirectory(folder);
            string safeName = string.Concat(TestContext.CurrentContext.Test.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            _writer = new StreamWriter(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + safeName + ".jsonl")) { AutoFlush = true };
            Log("setup.begin", "Uninstalled draft becomes evidence only after a real test execution.");
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Real Boot bootstrap");
            Assert.That(PrototypeApp.Current.SystemCursorWriteCount, Is.Zero,
                "Suppression must cover bootstrap, not only the measured test body.");
            _cursorWrites = PrototypeApp.Current.SystemCursorWriteCount; _cursorBaselineCaptured = true;
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local?.SwimBody?.Profile != null &&
                PrototypeMatch.Current?.IsSpawned == true && PrototypeMatch.Current.Blocks?.FixedGridMode == true,
                "Real NGO Host, authored paper profile, actual match fixed world");
            _host = PrototypePlayer.Local; _match = PrototypeMatch.Current;
            Assert.That(_host.IsSpawned && _host.IsServer && _host.IsOwner && _match.IsServer, Is.True);
            Assert.That(_host.NetworkObject.IsSpawned, Is.True);
            Assert.That(_match.Players.Count, Is.EqualTo(1), "Debug room must contain exactly the actually spawned Host.");
            Assert.That(PrototypeArena.Current.gameObject.scene.name, Is.EqualTo("TrainingGround"));
            Assert.That(BlockWorld.CellSize, Is.EqualTo(.30f)); Assert.That(BlockWorld.Top, Is.EqualTo(8f));
            Assert.That(BlockFixedGridWorld.MaximumStackLayers, Is.EqualTo(4));
            Assert.That(BlockFixedGridWorld.MaximumStackHeight, Is.EqualTo(1.2f));
            Assert.That(GameplayConfig.Global.SimulationRate, Is.EqualTo(60)); Assert.That(Terrain.Dynamic, Is.Null);
            _matchWasEnabled = _match.enabled; _match.enabled = false; _matchPaused = true;
            foreach (var p in _match.Players) { _playerEnabled.Add(p, p.enabled); p.enabled = false; }
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), _match, typeof(PrototypeMatch).GetMethod("ServerTick", Private));
            _match.NetworkManager.NetworkTickSystem.Tick -= _serverTick; _tickDetached = true;
            _savedTime = (double)MatchTime.GetValue(_match); _savedOffset = (double)MatchOffset.GetValue(_match);
            _driverPrepared = true;
            _now = Math.Max(_savedTime, Math.Max(Terrain.SimulationTime, Math.Max(_host.Snapshot.Value.SimulatedAt, _match.NetworkManager.ServerTime.Time))) + Dt;
            _observerFaults = BlockAttackObservation.ObserverFaultCount;
            _motorFaults = PlayerMotorSimulation.MotorBranchObserverErrors;
            BlockAttackObservation.PaymentAccepted += OnPayment;
            BlockAttackObservation.ActualResultCommitted += OnActual;
            PlayerMotorSimulation.MotorBranchObserved += OnMotor;
            _observing = true;
            _oracle = new PaperCapture(_host.SwimBody.Profile);
            _phase = "diagnostic-preparation";
            Terrain.Clear(_match.State.Value.Round); BlockAttackService.Clear();
            ResetPose(new Vector3(-.75f, 1, -20.25f), 0);
            Drive(Neutral(), "setup.authority-tick");
            var signature = (byte[])typeof(PrototypeApp).GetField("_signature", Private).GetValue(PrototypeApp.Current);
            Log("identity", new Identity {
                assembly=typeof(PrototypeMatch).Assembly.FullName, unity=Application.unityVersion, buildGuid=Application.buildGUID,
                roomSignature=BitConverter.ToString(signature ?? Array.Empty<byte>()).Replace("-", ""),
                mapPath=PrototypeArena.Current.gameObject.scene.path,
                mapGuid=AssetDatabase.AssetPathToGUID("Assets/GameResource/Gameplay/maps/TrainingGround.unity"),
                topology=PrototypeArena.Current.BakedTopology,
                profileGuid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_host.SwimBody.Profile)),
                profileContent=_host.SwimBody.Profile.ContentHash,
                runnerIdentity=Environment.GetEnvironmentVariable("SPLATOON_ACCEPTANCE_IDENTITY") ?? "not supplied; bind frozen source externally",
                paintProtocol=GameplayContentSignature.PaintProtocolVersion, blockProtocol=GameplayContentSignature.BlockProtocolVersion,
                weaponSimulation=GameplayContentSignature.WeaponSimulationVersion, paperSimulation=GameplayContentSignature.PaperTraversalSimulationVersion,
                fixedSimulation=BlockFixedGridWorld.SimulationVersion, playerCount=_match.Players.Count,
                simulationRate=GameplayConfig.Global.SimulationRate, networkRate=GameplayConfig.Global.NetworkTickRate });
            Assert.That(_payments, Is.Empty, "Preparation cannot stand in for a paid input attack.");
            LogWorld("setup.complete");
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            Log("cleanup.begin", TestContext.CurrentContext.Result.Outcome.ToString());
            if (_observing)
            {
                BlockAttackObservation.PaymentAccepted -= OnPayment;
                BlockAttackObservation.ActualResultCommitted -= OnActual;
                PlayerMotorSimulation.MotorBranchObserved -= OnMotor; _observing = false;
            }
            _oracle?.Dispose(); _oracle = null;
            if (_match != null)
            {
                if (_driverPrepared)
                { MatchTime.SetValue(_match, _savedTime); MatchOffset.SetValue(_match, _savedOffset); }
                if (_tickDetached && _match.NetworkManager?.NetworkTickSystem != null)
                    _match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
                _tickDetached = false;
                foreach (var pair in _playerEnabled) if (pair.Key != null) pair.Key.enabled = pair.Value;
                if (_matchPaused) _match.enabled = _matchWasEnabled;
            }
            _playerEnabled.Clear();
            if (Application.isPlaying && PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave("Gameplay65 regression cleanup").ToCoroutine();
            bool cursorUntouched = !_cursorBaselineCaptured || PrototypeApp.Current == null ||
                PrototypeApp.Current.SystemCursorWriteCount == _cursorWrites;
            // Log no world detail after Leave destroys the authoritative scene objects.
            _match = null; _host = null;
            Log("cleanup.cursor", cursorUntouched ? "No system cursor writes through Leave" : "SYSTEM CURSOR WRITE COUNT CHANGED");
            _writer?.Dispose(); _writer = null;
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(cursorUntouched, Is.True, "Automation must not control the system cursor.");
        }
        IEnumerator Run(string id, Action body)
        {
            Log(id + ".start", "Real input/payment/lifecycle assertions; precondition failures are failures, never skipped.");
            try
            {
                body();
                Assert.That(BlockAttackObservation.ObserverFaultCount, Is.EqualTo(_observerFaults), BlockAttackObservation.LastObserverError);
                Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors, Is.EqualTo(_motorFaults), PlayerMotorSimulation.LastMotorBranchObserverError);
                Log(id + ".assertions-complete", "NUnit teardown and external UTF receipt still required.");
            }
            catch (Exception e) { Log(id + ".failed-at-" + _phase, e.ToString()); throw; }
            yield return null;
        }
        void Restore(PlayerSnapshot s)
        {
            s.SimulatedAt = _now; Motor.Restore(s); _host.Snapshot.Value = s; Physics.SyncTransforms();
        }
        void ResetPose(Vector3 position, float yaw, float pitch = 0, bool suction = false)
        {
            var s = _host.Snapshot.Value;
            s.Position=position; s.Yaw=s.BodyYaw=yaw; s.Pitch=pitch; s.Health=GameplayConfig.GetHero(s.HeroId).MaxHealth;
            s.LifeState=PlayerLifeState.Alive; s.ProtectedUntil=0; s.Ink=GameplayConfig.GetHero(s.HeroId).MaxInk;
            s.Velocity=s.PlanarVelocity=Vector3.zero; s.VerticalSpeed=0; s.Grounded=false; s.Movement=MovementMode.Air;
            s.Swimming=s.CompactBody=s.SwimWasHeld=s.FriendlyInkContact=false;
            s.SwimSource=s.AirSwimSource=SwimSurface.None; s.PaperPose=PaperPose.None; s.PaperCenter=Vector3.zero;
            s.PaperRotation=Quaternion.identity; s.AirHumanOffset=s.CameraRebaseOffset=0;
            s.WeaponPhase=WeaponPhase.Idle; s.SubPhase=SubWeaponPhase.Idle; s.SpecialPhase=default;
            s.AttackNeedsRelease=s.SubNeedsRelease=s.Firing=s.ChargeReleasePending=false;
            s.AttackMoveUntil=s.AttackRecoveryUntil=s.SubRecoveryUntil=s.InkRecoverAt=s.SubReadyAt=0;
            s.MistUntil=s.MistExposure=0; s.BurstRemaining=0; s.NextShotAt=s.BurstReadyAt=s.WeaponReadyAt=0;
            s.SplatlingRemaining=s.SplatlingLoaded=0; s.SplatlingReservedInk=0;
            s.SpecialImpulseVelocity=Vector3.zero; s.SpecialImpulseRemaining=0; s.SpecialPoints=0;
            s.SupportIsFixedGrid=s.SupportIsSurfacePaint=false; s.SupportFixedId=0; s.SupportFixedGeneration=0;
            s.FixedBlockEpoch=World.Epoch; s.FixedBlockRound=World.Round; s.FixedBlockRevision=World.Revision;
            if (suction) s.SubWeaponId=SubWeaponConfigService.Id(SubWeaponType.SuctionBomb);
            Inputs.Clear(); Restore(s);
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(_host, Neutral());
            Log("diagnostic.initial-pose", s);
        }
        PlayerInputFrame Neutral(bool swim = false, Vector2 move = default)
        {
            var s = _host.Snapshot.Value;
            return new PlayerInputFrame { Revision=s.Revision, HeroRevision=s.HeroRevision, Move=move, Swim=swim,
                Look=new Vector2(s.Yaw,s.Pitch), JumpSequence=s.ConsumedJump, FireSequence=s.ConsumedFire,
                ReleaseSequence=s.ConsumedRelease, SpecialSequence=s.SpecialConsumed, InteractSequence=s.ConsumedInteract,
                SubPressSequence=s.SubConsumedPress, SubReleaseSequence=s.SubConsumedRelease };
        }
        void Drive(PlayerInputFrame input, string stage)
        {
            var before = _host.Snapshot.Value;
            Assert.That(Inputs.Count, Is.Zero, "No auto/manual double consumption or leftover input.");
            _now += Dt; input.Sequence=before.AcknowledgedInput+1; input.Tick=(uint)Math.Max(0, Math.Round(_now*60));
            input.Revision=before.Revision; input.HeroRevision=before.HeroRevision;
            Inputs.Add(input.Sequence,input);
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(_host, _host.NetworkManager.ServerTime.Time);
            // Only the deterministic test clock is adjusted. Invoke the complete production order once;
            // no direct player Step/payment Commit/SubWeapons.Spawn call is made by this fixture.
            MatchTime.SetValue(_match,_now); MatchOffset.SetValue(_match,_now-Time.fixedTimeAsDouble);
            _motorInvocations.Clear(); _driving=true;
            try { MatchFixedUpdate.Invoke(_match,null); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            finally { _driving=false; }
            Physics.SyncTransforms();
            var after = _host.Snapshot.Value;
            HarvestServiceEvents(stage);
            Log(stage, new InputTrace { input=input, before=before, after=after, queuedBefore=1,
                queuedAfter=Inputs.Count, motorInvocations=_motorInvocations.Count });
            foreach (var e in _match.SubWeapons.Entities) LogEntity(stage+".entity",e);
            Assert.That(after.AcknowledgedInput, Is.EqualTo(input.Sequence), stage+" real queue acknowledgement");
            Assert.That(after.SimulationTick, Is.EqualTo(before.SimulationTick+1), stage+" exactly one outer Step");
            Assert.That(after.SimulatedAt, Is.EqualTo(_now).Within(1e-7), stage+" deterministic production timestamp");
            Assert.That(after.InputTimedOut, Is.False, stage);
            if (_match.State.Value.Phase != MatchPhase.Finished)
                Assert.That(_motorInvocations.Count, Is.EqualTo(1), stage+" exactly one actual motor invocation");
            Assert.That(Inputs.Count, Is.Zero); Assert.That(Terrain.Dynamic, Is.Null);
        }
        void OnMotor(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation o)
        {
            if (!_driving || !ReferenceEquals(motor,Motor)) return;
            _motorInvocations.Add(o.Invocation);
            if (o.Branch == PlayerMotorSimulation.MotorTraceBranch.WallAttachResolved || o.After.Movement == MovementMode.Mantle)
                Log("motor.branch", new InputTrace { input=o.Input, before=o.Before, after=o.After });
        }
        void HarvestServiceEvents(string stage)
        {
            foreach(var e in _match.SubWeapons.Lifecycle)
                if(_seenLifecycle.Add(e.Sequence)){_lifecycle.Add(e);Log(stage+".lifecycle",new LifecycleTrace {
                    sequence=e.Sequence,kind=e.Kind,round=e.State.Round,id=e.State.Id,action=e.State.Action,stateJson=JsonUtility.ToJson(e.State) });}
            foreach(var e in _match.SubWeapons.Effects)
                if(_seenEffects.Add(e.Sequence)){_effects.Add(e);Log(stage+".effect",e);}
        }
        void OnPayment(BlockAttackObservation.Payment p)
        {
            _payments.Add(p);
            Log(_phase+".payment", new PaymentTrace { source=new SourceTrace(p.Source), material=p.Material, serverTime=p.ServerTime, networkTick=p.NetworkTick });
        }
        void OnActual(BlockAttackObservation.ActualResult a)
        {
            _actual.Add(a);
            Log(_phase+".actual", new ActualTrace { source=new SourceTrace(a.Source), operation=a.Operation, counts=a.Counts,
                entity=a.Entity,ordinal=a.Ordinal,epoch=a.Epoch,round=a.Round,revision=a.Revision,surfaceRevision=a.SurfaceRevision,
                networkTick=a.NetworkTick,serverTime=a.ServerTime,milliseconds=a.Milliseconds,changes=a.HadChanges,auxiliary=a.Auxiliary,contact=a.HadContact,
                newFixed=a.Counts.NewFixed,newPaint=a.Counts.NewPaint,replacedPaint=a.Counts.EnemyPaintReplaced,
                removedEnemyFixed=a.Counts.EnemyFixedRemoved,removedFriendlyFixed=a.Counts.FriendlyFixedRemoved,
                removedEnemyPaint=a.Counts.EnemyPaintRemoved,removedFriendlyPaint=a.Counts.FriendlyPaintRemoved,
                converted=a.Counts.Converted,relocated=a.Counts.Relocated });
        }
        void LogEntity(string stage, SubWeaponService.Entity e)
        {
            Log(stage,new EntityTrace { state=e.State,stateJson=JsonUtility.ToJson(e.State), removed=e.Removed, attached=e.Attached, blockAttachment=e.BlockAttachment,
                handle=new HandleTrace(e.FixedAttachmentHandle),localOffset=e.FixedAttachmentOffset,fuse=e.Fuse,
                buildLimit=e.BlockAttack?.BuildLimit??-1,buildRemaining=e.BlockAttack?.BuildRemaining??-1,
                added=e.BlockAttack?.Added??-1,removedEnemy=e.BlockAttack?.RemovedEnemy??-1,awardedPoints=e.BlockAttack?.AwardedPoints??-1 });
        }
        string WorldIdentity() => Convert.ToBase64String(BlockFixedGridCodec.Encode(World.Capture(true)));
        void LogWorld(string stage) => Log(stage,new WorldTrace { wireBase64=WorldIdentity(),epoch=World.Epoch,round=World.Round,
            revision=World.Revision,surfaceRevision=World.Surfaces.Revision,nextId=World.NextId,
            states=World.Blocks.Values.OrderBy(s=>s.Handle.Id).Select(s=>new StateTrace(s)).ToArray() });
        void Log(string stage, object detail)
        {
            if (_writer == null) return;
            var t = new Trace { test=TestContext.CurrentContext.Test.FullName,stage=stage,
                detail=detail is string text ? text : detail == null ? "null" : JsonUtility.ToJson(detail), simulationTime=_now };
            if (_match != null && _host != null)
            {
                t.networkTime=_match.NetworkManager.ServerTime.Time; t.snapshot=JsonUtility.ToJson(_host.Snapshot.Value);
                t.match=JsonUtility.ToJson(_match.State.Value); t.epoch=World.Epoch; t.round=World.Round;
                t.revision=World.Revision; t.surfaceRevision=World.Surfaces.Revision; t.fixedCount=World.Count;
                t.pinkCount=World.PinkCount; t.blueCount=World.BlueCount; t.transformPosition=_host.transform.position;
                var cc=_host.GetComponent<CharacterController>();
                t.controllerCenter=cc.center;t.controllerHeight=cc.height;t.controllerRadius=cc.radius;
                t.controllerEnabled=cc.enabled;t.controllerGrounded=cc.isGrounded;
                var body=_host.SwimBody; t.paperEnabled=body.FlatHitActive;
                t.paperPoseVersion=body.Capture?.PoseVersion??-1;t.paperRectCount=body.HitRects?.Count??0;
                t.paperPosition=body.HitVolume.transform.position;t.paperRotation=body.HitVolume.transform.rotation;
                t.cursorWrites=PrototypeApp.Current.SystemCursorWriteCount;
            }
            _writer.WriteLine(JsonUtility.ToJson(t));
        }

        RaycastHit MapHit(Vector3 origin, float length, string label)
        {
            Assert.That(Physics.Raycast(origin, Vector3.down, out var hit, length, PlayerMotorSimulation.WorldMask,
                QueryTriggerInteraction.Ignore), Is.True, label);
            Assert.That(Terrain.IsAuthoredMapCollider(hit.collider), Is.True, label+" original initialized map collider");
            Assert.That(Terrain.TryGetSurfacePaint(hit.collider,hit.point,hit.normal,out var paint), Is.True,
                label+" one complete authored surface square");
            Log(label,new MapTrace { collider=hit.collider.name,instance=hit.collider.GetInstanceID(),point=hit.point,normal=hit.normal,key=paint.Key.ToString(),owner=paint.Owner });
            return hit;
        }
        BlockFixedGridState[] PrepareColumn(Vector2Int column, int layers, byte owner, float rayY = 2)
        {
            int paymentCount=_payments.Count; float ink=_host.Snapshot.Value.Ink; double points=_host.Snapshot.Value.SpecialPoints;
            var center=BlockWorld.Center(new Vector3Int(column.x,0,column.y));
            var map=MapHit(new Vector3(center.x,rayY,center.z),rayY+1,"scaffold.map");
            Terrain.TryGetSurfacePaint(map.collider,map.point,map.normal,out var paint);
            if (paint.Owner!=owner)
                Assert.That(Terrain.BuildFixedImpact(map.point,map.normal,0,owner,1,true).Added, Is.EqualTo(1));
            var states=new BlockFixedGridState[layers];
            for (int y=0;y<layers;y++)
            {
                var ids=World.Blocks.Keys.ToHashSet();
                Vector3 point=y==0?map.point:BlockWorld.Center(states[y-1].Cell)+Vector3.up*(BlockWorld.CellSize*.5f);
                Assert.That(Terrain.BuildFixedImpact(point,Vector3.up,0,owner,1,y==0).Added, Is.EqualTo(1), World.LastFailure);
                var created=World.Blocks.Values.Where(s=>!ids.Contains(s.Handle.Id)).ToArray();
                Assert.That(created.Length, Is.EqualTo(1)); states[y]=created[0];
                Assert.That(states[y].Cell.y, Is.EqualTo(BlockFixedGridMapSupport.GridBaseY(map.point.y)+y));
                Assert.That(states[y].Owner, Is.EqualTo(owner));
                Assert.That(BlockFixedGridWorld.GetBounds(states[y]).max.y-map.point.y, Is.LessThanOrEqualTo(1.20001f));
            }
            Assert.That(_payments.Count, Is.EqualTo(paymentCount), "Scaffold is diagnostic, never a paid shot.");
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(ink)); Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(points));
            return states;
        }
        BlockFixedGridState PreparePad(byte owner)
        {
            _phase="diagnostic-pad"; BlockFixedGridState center=default;
            for (int x=-4;x<=4;x++) for (int z=-72;z<=-64;z++)
            { var col=PrepareColumn(new Vector2Int(x,z),1,owner); if(x==0&&z==-68)center=col[0]; }
            LogWorld("scaffold.pad-complete"); return center;
        }
        void LandOn(BlockFixedGridState target)
        {
            var center=BlockWorld.Center(target.Cell); float top=BlockFixedGridWorld.GetBounds(target).max.y;
            ResetPose(new Vector3(center.x,top+1,center.z),0);
            _phase="ordinary-motor-landing";
            bool sawAir=false;
            for (int i=0;i<150;i++)
            {
                Drive(Neutral(),"land"); var s=_host.Snapshot.Value; sawAir|=!s.Grounded;
                AssertLegalPose("land.pose");
                if(s.Grounded&&s.SupportIsFixedGrid)break;
            }
            Assert.That(sawAir, Is.True, "Initial condition must fall through actual motor integration.");
            AssertFixedGround(target.Owner);
        }
        void AssertFixedGround(byte owner)
        {
            var s=_host.Snapshot.Value;
            Assert.That(s.Grounded&&s.SupportIsFixedGrid, Is.True);
            Assert.That(PlayerMotorSimulation.TryGetGround(s.Position,out var floor), Is.True);
            Assert.That(floor.Kind, Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(floor.Owner, Is.EqualTo(owner));
            Assert.That(floor.Stable, Is.True); Assert.That(World.TryGet(floor.FixedHandle,out var support), Is.True);
            Assert.That(s.SupportFixedId, Is.EqualTo(support.Handle.Id));
            Assert.That(s.SupportFixedGeneration, Is.EqualTo(support.Handle.Generation));
        }
        void AssertLegalPose(string stage)
        {
            var s=_host.Snapshot.Value; var shape=HeroBodyShape.For(s.HeroId);
            float height=s.ShowsSwimBody?shape.CompactHeight:shape.Height;
            float radius=shape.Radius-Mathf.Max(.005f,shape.SkinWidth);
            float segment=Mathf.Max(0,height*.5f-shape.Radius);
            var center=s.Position+Vector3.up*(height*.5f);
            bool capsuleClear=!Physics.CheckCapsule(center-Vector3.up*segment,center+Vector3.up*segment,radius,
                PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore);
            foreach(var block in World.Blocks.Values)
            {
                var expanded=new Bounds(BlockWorld.Center(block.Cell),Vector3.one*BlockWorld.CellSize+Vector3.up*(2*segment));
                if((center-expanded.ClosestPoint(center)).sqrMagnitude<radius*radius-1e-7f)capsuleClear=false;
            }
            bool paperClear=true; int rectangles=0;
            if(s.ShowsSwimBody)
            {
                // Independent uncached full authored contour; no broad rectangle or production data-query oracle.
                _oracle.SampleUncachedForDiagnostics(s); rectangles=_oracle.HitRects.Count;
                Assert.That(rectangles, Is.GreaterThan(0));
                var axes=new[]{s.PaperRotation*Vector3.right,s.PaperRotation*Vector3.up,s.PaperRotation*Vector3.forward};
                foreach(var rect in _oracle.HitRects)
                {
                    var pc=s.PaperCenter+s.PaperRotation*new Vector3(rect.center.x,rect.center.y,0);
                    var half=new Vector3(Mathf.Max(.001f,rect.width*.5f-.001f),Mathf.Max(.001f,rect.height*.5f-.001f),
                        Mathf.Max(.001f,_host.SwimBody.Profile.Thickness*.5f-.001f));
                    if(Physics.CheckBox(pc,half,s.PaperRotation,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))paperClear=false;
                    foreach(var block in World.Blocks.Values)
                        if(CubeObb(BlockWorld.Center(block.Cell),BlockWorld.CellSize*.5f,pc,half,axes))paperClear=false;
                }
            }
            bool production=Terrain.PublishedTraversalPoseFits(_host,s);
            Log(stage,new PoseOracleTrace { rectangles=rectangles,fixedCount=World.Count,capsuleClear=capsuleClear,
                paperClear=paperClear,productionFits=production,profileContent=_host.SwimBody.Profile.ContentHash });
            Assert.That(capsuleClear&&paperClear&&production, Is.True, stage+" complete real map + fixed data pose");
            Assert.That(Vector3.Distance(_host.transform.position,s.Position), Is.LessThan(1e-5f));
        }
        static bool CubeObb(Vector3 cube,float cubeHalf,Vector3 center,Vector3 half,Vector3[] axes)
        {
            var worldAxes=new[]{Vector3.right,Vector3.up,Vector3.forward};
            var candidates=new List<Vector3>(worldAxes);candidates.AddRange(axes);
            foreach(var a in worldAxes)foreach(var b in axes)candidates.Add(Vector3.Cross(a,b));
            foreach(var axis in candidates)
            {
                if(axis.sqrMagnitude<1e-12f)continue;
                float cubeR=cubeHalf*(Mathf.Abs(axis.x)+Mathf.Abs(axis.y)+Mathf.Abs(axis.z));
                float boxR=half.x*Mathf.Abs(Vector3.Dot(axes[0],axis))+half.y*Mathf.Abs(Vector3.Dot(axes[1],axis))+half.z*Mathf.Abs(Vector3.Dot(axes[2],axis));
                if(Mathf.Abs(Vector3.Dot(center-cube,axis))>cubeR+boxR+1e-7f)return false;
            }
            return true;
        }
    }
}
#endif
