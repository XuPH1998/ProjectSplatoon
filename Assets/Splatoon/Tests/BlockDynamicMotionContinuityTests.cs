#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    // Uses the production selection, ACK, lifecycle-commit and motion-apply seams with a
    // deterministic delivery clock. This is not a socket, independent Player or PhysX-step test.
    [Category("BlockSurfacePaint")]
    public sealed class BlockDynamicMotionContinuityTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const ulong ClientId = 22;
        readonly List<GameObject> _roots = new();
        PrototypeMatch _server, _client;
        object _peer;
        static object Field(object target, string name) => target.GetType().GetField(name, Private | BindingFlags.Public).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private | BindingFlags.Public).SetValue(target, value);
        static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethod(name, Private).Invoke(target, args); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        static BlockDynamicState Body(ulong id = 1, float x = 0, uint round = 1) => new()
        { Id = id, Round = round, Owner = 1, Position = new Vector3(x, 2, 0), Rotation = Quaternion.identity };
        static BlockSurfaceWorld Surface(uint round = 1)
        {
            var surface = new BlockSurfaceWorld(round); var cells = new List<Vector2Int>();
            for (int i = 0; i < 65; i++) cells.Add(new Vector2Int(i, 0));
            surface.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, cells); return surface;
        }
        PrototypeMatch Match(string name)
        {
            var root = new GameObject(name); _roots.Add(root); root.AddComponent<NetworkObject>();
            var terrain = root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
            terrain.Clear(1);
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.SurfacePaints)).SetValue(terrain, Surface());
            var match = root.AddComponent<PrototypeMatch>();
            typeof(PrototypeMatch).GetProperty(nameof(PrototypeMatch.Blocks)).SetValue(match, terrain);
            Set(match, "_paintRound", 1u); return match;
        }
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _server = Match("Motion production sender"); _client = Match("Motion production receiver");
            _server.Blocks.Dynamic.SetAuthority(true);
            Assert.That(_server.Blocks.Dynamic.TrySpawn(1, Body().Position, Quaternion.identity, out _), Is.True);
            _peer = Activator.CreateInstance(typeof(PrototypeMatch).GetNestedType("DynamicPeer", BindingFlags.NonPublic), true);
            ((IDictionary)Field(_server, "_dynamicPeers")).Add(ClientId, _peer);
            Paint(1); Start(); Commit(Send()); Assert.That(Ack(Send()), Is.True);
        }
        [TearDown] public void Cleanup()
        {
            foreach (var root in _roots) if (root != null) UnityEngine.Object.DestroyImmediate(root);
            _roots.Clear(); Physics.IgnoreLayerCollision(BlockTerrain.TerrainLayer, 8, false);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Paint(byte owner)
        {
            for (int i = 0; i < 65; i++) Assert.That(_server.Blocks.SurfacePaints.TrySet(new BlockSurfaceKey(11, i, 0), owner, out _), Is.True);
        }
        void Capture() => Call(_server, "CaptureDynamicStates", 0d);
        void Start() { Capture(); Call(_server, "StartDynamicTransfer", _peer, 0d); }
        (BlockDynamicFragment manifest, byte[] bytes, uint surface) Send()
        {
            var send = Field(_peer, "Send");
            return ((BlockDynamicFragment)Field(send, "Manifest"), (byte[])Field(send, "Bytes"), (uint)Field(send, "SurfaceRevision"));
        }
        void Commit((BlockDynamicFragment manifest, byte[] bytes, uint surface) send)
            => Assert.That((uint)Call(_client, "ApplyDynamicLifecycle", send.manifest, send.bytes), Is.EqualTo(send.surface));
        bool Ack((BlockDynamicFragment manifest, byte[] bytes, uint surface) send, uint? surface = null)
            => (bool)Call(_server, "ApplyDynamicAcknowledgement", ClientId, send.manifest.Round, send.manifest.Transfer,
                send.manifest.Revision, surface ?? send.surface);
        bool Receive(BlockDynamicMotion motion) => (bool)Call(_client, "ApplyDynamicMotion", motion);
        static BlockDynamicMotion Motion(ulong sequence, float x, uint revision = 1, ulong id = 1, uint round = 1)
            => new() { Round = round, Revision = revision, Tick = 999, Sequence = sequence, States = new[] { Body(id, x, round) } };

        sealed class MotionSource : BlockDynamicSendScheduler.ISource
        {
            public PrototypeMatch Match;
            public object Peer;
            public readonly List<BlockDynamicMotion> Sent = new();
            public BlockDynamicSendScheduler.Peer GetPeer(int index) => (BlockDynamicSendScheduler.Peer)Field(Peer, "Schedule");
            public int TrySend(int index, BlockDynamicSendScheduler.Kind kind, BlockDynamicSendScheduler.Window window)
            {
                if (kind != BlockDynamicSendScheduler.Kind.Motion) return 0;
                object[] args = { Peer, window, default(BlockDynamicMotion), default(BlockDynamicSendScheduler.MotionCursor.Selection) };
                if (!(bool)Call(Match, "TryPrepareDynamicMotion", args)) return 0;
                var motion = (BlockDynamicMotion)args[2];
                GetPeer(index).Motion.Commit((BlockDynamicSendScheduler.MotionCursor.Selection)args[3], true);
                Sent.Add(motion); return 100 + motion.States.Length * BlockDynamicCodec.StateBytes;
            }
        }
        List<BlockDynamicMotion> Prepare()
        {
            var source = new MotionSource { Match = _server, Peer = _peer };
            double room = 10000, dynamic = 10000;
            new BlockDynamicSendScheduler(() => 0).Run(1, source, ref room, ref dynamic); return source.Sent;
        }

        [TestCase(100)] [TestCase(200)]
        public void ContinuousPaintDeltasAndPendingReliableFragmentsDoNotStarveAnAckedLiveId(int rttMilliseconds)
        {
            int oneWay = rttMilliseconds / 20; // One-way latency in deterministic 10 ms slots.
            var deliveries = new SortedDictionary<int, List<Action>>(); int received = 0, prior = -1, maximumGap = 0;
            void At(int slot, Action action)
            {
                if (!deliveries.TryGetValue(slot, out var list)) deliveries.Add(slot, list = new());
                list.Add(action);
            }
            int current = 0; byte owner = 1;
            for (; current < 100; current++)
            {
                if (deliveries.TryGetValue(current, out var due)) { foreach (var action in due) action(); deliveries.Remove(current); }
                owner = owner == 1 ? (byte)2 : (byte)1; Paint(owner);
                var body = _server.Blocks.Dynamic.Bodies[1]; body.Body.position = Body(x: current * .01f).Position;
                if (Field(_peer, "Send") == null)
                {
                    Start(); var transfer = Send(); int begun = current;
                    Assert.That(transfer.manifest.Full, Is.False); Assert.That(transfer.manifest.Count, Is.GreaterThan(1));
                    At(begun + oneWay, () =>
                    {
                        var part = transfer.manifest; part.Index = 0; part.Data = new byte[BlockDynamicFragment.PayloadBytes];
                        Array.Copy(transfer.bytes, part.Data, part.Data.Length);
                        var assembly = new BlockDynamicAssembly(part); Assert.That(assembly.Add(part), Is.True);
                        Set(_client, "_dynamicIncoming", assembly);
                    });
                    At(begun + oneWay + 2, () => Commit(transfer));
                    At(begun + oneWay * 2 + 2, () => Assert.That(Ack(transfer), Is.True));
                }
                else Capture();
                var packets = Prepare();
                Assert.That(packets, Has.Count.EqualTo(1), "Paint revisions and pending ACKs must not suppress this live known body");
                var packet = packets[0]; int sent = current;
                Assert.That(packet.Tick, Is.Zero);
                At(sent + oneWay, () =>
                {
                    Assert.That(Receive(packet), Is.True);
                    if (prior >= 0) maximumGap = Math.Max(maximumGap, sent - prior);
                    prior = sent; received++;
                });
            }
            Assert.That(received, Is.GreaterThan(85)); Assert.That(maximumGap, Is.EqualTo(1));
            Assert.That(_client.Blocks.SurfacePaints.Revision, Is.GreaterThan(65));
            Assert.That(_client.DynamicBlockCaptureTick, Is.Zero, "A sparse receiver has no proved common pose tick");
        }

        [Test] public void PendingBirthWaitsForMatchingAckWhileRetainedLiveIdsKeepMoving()
        {
            Assert.That(_server.Blocks.Dynamic.TrySpawn(1, new Vector3(1, 2, 0), Quaternion.identity, out var created), Is.True);
            Start(); var transfer = Send(); var beforeAck = Prepare();
            Assert.That(beforeAck, Has.Count.EqualTo(1)); Assert.That(beforeAck[0].States, Has.Length.EqualTo(1));
            Assert.That(beforeAck[0].States[0].Id, Is.EqualTo(1));
            Assert.That(Ack(transfer, transfer.surface + 1), Is.False);
            Assert.That(((HashSet<ulong>)Field(_peer, "Ids")).Contains(created.Id), Is.False);
            Commit(transfer); Assert.That(Receive(beforeAck[0]), Is.True, "Old approved revision survives the ACK return trip");
            Assert.That(Ack(transfer), Is.True); Capture();
            var afterAck = Prepare(); Assert.That(afterAck, Has.Count.EqualTo(1));
            Assert.That(afterAck[0].States, Has.Length.EqualTo(2)); Assert.That(Receive(afterAck[0]), Is.True);
        }

        [Test] public void RetainedDeltaPoseKeepsItsSequenceAndDeletedIdsCannotReturn()
        {
            Assert.That(Receive(Motion(10, 1)), Is.True);
            Set(_server, "_dynamicSequence", 19ul); Paint(2); Start(); var transfer = Send();
            Assert.That(transfer.manifest.Sequence, Is.EqualTo(20)); Commit(transfer);
            Assert.That(Receive(Motion(15, 2)), Is.True);
            Assert.That(_client.Blocks.Dynamic.Bodies[1].Body.position.x, Is.EqualTo(2),
                "Retaining pose sequence 10 must not relabel it as lifecycle sequence 20");
            Assert.That(Ack(transfer), Is.True);
            Assert.That(_server.Blocks.Dynamic.DestroyIds(new[] { 1ul }, 2), Is.EqualTo(1));
            Start(); Commit(Send()); Assert.That(Receive(Motion(30, 3)), Is.False);
            Assert.That(_client.Blocks.Dynamic.Count, Is.Zero);
        }

        [Test] public void FullResyncPreservesNewerMotionButFrozenFullCommitsTheFinalPose()
        {
            Set(_peer, "Ready", false); Set(_server, "_dynamicSequence", 19ul); Start(); var full = Send();
            Assert.That(Receive(Motion(30, 3)), Is.True); Commit(full);
            Assert.That(_client.Blocks.Dynamic.Bodies[1].Body.position.x, Is.EqualTo(3));
            Assert.That(Receive(Motion(18, 8)), Is.False, "A full baseline establishes the new motion epoch sequence");
            Assert.That(Ack(full), Is.True);
            _server.Blocks.Dynamic.Bodies[1].Body.position = Body(x: 4).Position;
            _server.Blocks.Dynamic.Freeze(); Set(_server, "_dynamicSequence", 39ul); Start(); var frozen = Send();
            Assert.That(frozen.manifest.Full && frozen.manifest.Frozen, Is.True); Commit(frozen);
            Assert.That(_client.Blocks.Dynamic.Bodies[1].Body.position.x, Is.EqualTo(4));
            Assert.That(Receive(Motion(50, 5, frozen.manifest.Revision)), Is.False);
            Assert.Throws<InvalidOperationException>(() => Commit(full));
            Assert.That(_client.Blocks.Dynamic.Bodies[1].Body.position.x, Is.EqualTo(4));
        }

        [Test] public void UnknownBaselineWrongRoundInvalidBatchAndResetEpochCannotMoveBodies()
        {
            Assert.That(Receive(Motion(10, 1, 0)), Is.False); Assert.That(Receive(Motion(10, 1, 2)), Is.False);
            Assert.That(Receive(Motion(10, 1, round: 2)), Is.False);
            var invalid = Motion(10, 1); invalid.States[0].Position.x = float.NaN;
            Assert.That(Receive(invalid), Is.False);
            var duplicate = Motion(10, 1); duplicate.States = new[] { Body(), Body() }; Assert.That(Receive(duplicate), Is.False);
            Call(_client, "ResetDynamicSync"); Assert.That(Receive(Motion(10, 1)), Is.False);
            Assert.That(_client.Blocks.Dynamic.Bodies[1].Body.position.x, Is.Zero);
            Assert.That(_client.DynamicBlockCaptureTick, Is.Zero);
        }

        [Test] public void NativeCapturesFullImportsAndSparseMotionNeverInventACommonTick()
        {
            Capture(); Assert.That((uint)Field(_server, "_dynamicCapturedTick"), Is.Zero);
            Set(_peer, "Ready", false); Start(); var transfer = Send();
            Assert.That(transfer.manifest.Tick, Is.Zero);
            var manifest = transfer.manifest; manifest.Tick = 12345;
            Commit((manifest, transfer.bytes, transfer.surface));
            Assert.That(_client.DynamicBlockCaptureTick, Is.Zero);
            Assert.That(Receive(Motion(100, 3)), Is.True);
            Assert.That(_client.DynamicBlockCaptureTick, Is.Zero, "Even a packet tick cannot label the whole mixed live world");
        }
    }
}
#endif
