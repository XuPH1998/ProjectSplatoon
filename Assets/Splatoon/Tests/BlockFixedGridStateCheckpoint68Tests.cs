#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
using Object = UnityEngine.Object;
using Probe = Splatoon.Prototype.BlockFixedGridAcceptanceProbe;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridStateCheckpoint68Tests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        sealed class Environment : IBlockFixedGridEnvironment
        {
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            { support = new BlockFixedGridMapSupport(7, 0, new BlockSurfaceKey(11, cell.x, cell.z)); return true; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase,
                bool relocating, out string reason) { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
        }

        static BlockFixedGridWorld World()
        {
            var surface = new BlockSurfaceWorld(7);
            surface.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward,
                new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.left });
            return new BlockFixedGridWorld(new BlockWorld(new Vector2(30, 30), 7, -8), surface, new Environment());
        }
        static void Populate(BlockFixedGridWorld world)
        {
            Assert.That(world.Paint(new[] { new BlockSurfaceKey(11, 0, 0) }, 1, 1).Added, Is.EqualTo(1));
            Assert.That(world.Paint(new[] { new BlockSurfaceKey(11, 1, 0) }, 2, 1).Added, Is.EqualTo(1));
            Assert.That(world.Build(new[] { new Vector3Int(0, 3, 0) }, 1, 1).Added, Is.EqualTo(1));
            Assert.That(world.Build(new[] { new Vector3Int(1, 3, 0) }, 2, 1).Added, Is.EqualTo(1));
        }
        static string Hash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, Fields).GetValue(value);
        static void Set(object value, string field, object data) => value.GetType().GetField(field, Fields).SetValue(value, data);
        static object Call(object value, string name) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null);

        [Test] public void ActualWorldCaptureUsesOriginalCodecAndIndependentCanonicalCellOrder()
        {
            var world = World(); Populate(world);
            var actual = Probe.CheckpointWorldCapture.Capture(world);
            Assert.That(actual.WorldHash, Is.EqualTo(Hash(BlockFixedGridCodec.Encode(world.Capture(true)))));
            Assert.That(actual.PaintHash, Is.EqualTo(Hash(world.Capture(true).SurfaceBytes)));
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                foreach (var state in world.Blocks.Values.OrderBy(s => s.Handle.Id))
                {
                    writer.Write(state.Handle.Id); writer.Write(state.Handle.Generation); writer.Write(state.Owner);
                    writer.Write(state.Cell.x); writer.Write(state.Cell.y); writer.Write(state.Cell.z);
                    writer.Write(state.Support.Id); writer.Write(state.Support.BaseCellY); writer.Write(state.Support.Height);
                    writer.Write(state.Support.SurfaceKey.FaceId); writer.Write(state.Support.SurfaceKey.U); writer.Write(state.Support.SurfaceKey.V);
                }
            Assert.That(actual.CellHash, Is.EqualTo(Hash(bytes.ToArray())));
            Assert.That(actual.Snapshot.Upserts.Length, Is.EqualTo(2));
        }

        [Test] public void EveryObservationRecapturesAndDoesNotAliasLaterWorldCommits()
        {
            var world = World(); Populate(world);
            var first = Probe.CheckpointWorldCapture.Capture(world);
            byte[] original = BlockFixedGridCodec.Encode(first.Snapshot);
            Assert.That(world.Paint(new[] { new BlockSurfaceKey(11, -1, 0) }, 2, 1).Added, Is.EqualTo(1));
            var second = Probe.CheckpointWorldCapture.Capture(world);
            Assert.That(second.Snapshot, Is.Not.SameAs(first.Snapshot));
            Assert.That(second.WorldHash, Is.Not.EqualTo(first.WorldHash));
            Assert.That(second.PaintHash, Is.Not.EqualTo(first.PaintHash));
            Assert.That(second.CellHash, Is.EqualTo(first.CellHash));
            Assert.That(BlockFixedGridCodec.Encode(first.Snapshot), Is.EqualTo(original));
        }

        [TestCase("paint")] [TestCase("build")] [TestCase("freeze")] [TestCase("clear")]
        [TestCase("same-version-import")] [TestCase("face-registration")] [TestCase("world")]
        [TestCase("match")] [TestCase("terrain")] [TestCase("command")] [TestCase("generation")] [TestCase("stage")]
        public void RealLifecycleChangesRejectAnEarlierTicket(string change)
        {
            var world = World(); Populate(world);
            object match = new(), terrain = new(); int command = 3, generation = 1; string stage = "drain";
            var lifecycle = new Probe.CheckpointLifecycle();
            var ticket = lifecycle.Begin(match, terrain, world, command, generation, stage);
            Assert.That(Probe.CheckpointLifecycle.Matches(ticket, match, terrain, world, command, generation, stage), Is.True);
            switch (change)
            {
                case "paint": world.Paint(new[] { new BlockSurfaceKey(11, -1, 0) }, 1, 1); break;
                case "build": world.Build(new[] { Vector3Int.up }, 1, 1); break;
                case "freeze": Assert.That(world.Freeze(), Is.True); break;
                case "clear": world.Clear(8); break;
                case "same-version-import":
                    var snapshot = world.Capture(true);
                    Assert.That(world.TryPrepareImport(snapshot, out var prepared, out string error), Is.True, error);
                    Assert.That(world.CommitImport(prepared), Is.True, world.LastFailure);
                    Assert.That(world.Revision, Is.EqualTo(ticket.Stamp.revision), "Replacement must be detected even without a version change.");
                    break;
                case "face-registration": world.Surfaces.RegisterFace(12, Vector3.up, Vector3.right, Vector3.forward, new[] { Vector2Int.zero }); break;
                case "world": world = World(); Populate(world); break;
                case "match": match = new object(); break;
                case "terrain": terrain = new object(); break;
                case "command": command++; break;
                case "generation": generation++; break;
                case "stage": stage = "quit-clients"; break;
            }
            Assert.That(Probe.CheckpointLifecycle.Matches(ticket, match, terrain, world, command, generation, stage), Is.False, change);
        }

        [Test] public void ReferenceReplacementAdvancesLocalSerialWithoutInventingANewWorldVersion()
        {
            var world = World(); Populate(world); var life = new Probe.CheckpointLifecycle();
            object match = new(), terrain = new();
            var first = life.Begin(match, terrain, world, 2, 1, "drain");
            Assert.That(world.TryPrepareImport(world.Capture(true), out var prepared, out string error), Is.True, error);
            Assert.That(world.CommitImport(prepared), Is.True);
            var second = life.Begin(match, terrain, world, 2, 1, "drain");
            Assert.That(second.Stamp.worldSerial, Is.EqualTo(first.Stamp.worldSerial));
            Assert.That(second.Stamp.storageSerial, Is.GreaterThan(first.Stamp.storageSerial));
            Assert.That(second.Stamp.surfaceSerial, Is.GreaterThan(first.Stamp.surfaceSerial));
            Assert.That(second.Stamp.revision, Is.EqualTo(first.Stamp.revision));
        }

        [Serializable] sealed class LedgerPayload
        { public bool complete = true; public List<Probe.IssuedCheckpoint> issuedCheckpoints = new(); }
        [Serializable] sealed class LedgerRead
        { public bool complete; public Probe.IssuedCheckpoint[] issuedCheckpoints; }

        [Test] public void TerminalCanonicalWriterPreservesExactCheckpointBytesAcrossBothSerializerFormats()
        {
            string path = Path.Combine(Path.GetTempPath(), "Splatoon-checkpoint68-" + Guid.NewGuid().ToString("N"));
            var world = World(); Populate(world);
            var dto = new Probe.StateCheckpoint {
                session = "session\n\"68", producerId = new string('a', 32), observationSequence = 1,
                current = new Probe.StateSample { sampledAt = 1.0000000000000002, serverTime = 123456.78901234567,
                    stage = "drain", cellHash = new string('b', 64) },
                lifecycle = new Probe.CheckpointLifecycle().Begin(new object(), new object(), world, 3, 1, "drain").Stamp,
                rawFence = new Probe.CheckpointFence { lastFrameId = -1 }
            };
            string json = JsonUtility.ToJson(dto, false);
            var source = new LedgerPayload();
            source.issuedCheckpoints.Add(new Probe.IssuedCheckpoint { observationSequence = 1, payloadJson = json,
                payloadSha256 = Hash(Encoding.UTF8.GetBytes(json)), producerId = dto.producerId, stage = "drain", command = 3, generation = 1 });
            var detached = (LedgerPayload)BlockProbeReportSnapshot.Capture(source);
            source.issuedCheckpoints[0].payloadJson = "changed"; source.issuedCheckpoints.Clear();
            dto.current.sampledAt = 42; dto.rawFence.frameRecords = 999;
            try
            {
                using var file = BlockProbeReportFile.Capture(path, detached);
                string terminal = File.ReadAllText(file.Path);
                var read = JsonUtility.FromJson<LedgerRead>(terminal);
                Assert.That(read.complete, Is.True);
                Assert.That(read.issuedCheckpoints, Has.Length.EqualTo(1));
                Assert.That(Encoding.UTF8.GetBytes(read.issuedCheckpoints[0].payloadJson), Is.EqualTo(Encoding.UTF8.GetBytes(json)));
                Assert.That(read.issuedCheckpoints[0].payloadSha256, Is.EqualTo(Hash(Encoding.UTF8.GetBytes(json))));
                Assert.That(file.Sha256, Is.EqualTo(Hash(File.ReadAllBytes(file.Path))));
                Assert.That(JsonUtility.FromJson<Probe.StateCheckpoint>(read.issuedCheckpoints[0].payloadJson).rawFence.frameRecords, Is.Zero);
            }
            finally
            {
                foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + ".*")) File.Delete(file);
            }
        }

        sealed class WriterFixture : IDisposable
        {
            internal readonly GameObject Root;
            internal readonly Probe Instance;
            internal readonly object Report;
            internal readonly BlockProbeReportWriter Writer;
            internal readonly string DirectoryPath;
            internal WriterFixture(Action<string, string> write)
            {
                DirectoryPath = Path.Combine(Path.GetTempPath(), "Splatoon-statewriter68-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath);
                Root = new GameObject("Inactive state-checkpoint68 test"); Root.SetActive(false); Instance = Root.AddComponent<Probe>();
                Report = Activator.CreateInstance(typeof(Probe).GetNestedType("Report", BindingFlags.NonPublic), true);
                Writer = new BlockProbeReportWriter(Path.Combine(DirectoryPath, "state.json"), write);
                Set(Instance, "_report", Report); Set(Instance, "_stateCheckpointWriter", Writer);
                Assert.That(Get<double>(Instance, "_boot"), Is.Zero, "Awake must not start a session.");
            }
            public void Dispose()
            {
                Writer.Close(5000); Writer.Dispose(); Set(Instance, "_stateCheckpointWriter", null);
                Object.DestroyImmediate(Root);
                if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
            }
        }

        [Test] public void BusyStateWriterIsPolledBeforeCloseAndCanPublishBeforeTerminalReceipt()
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var fixture = new WriterFixture((path, json) => {
                entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Test publisher gate timed out.");
                File.WriteAllText(path, json);
            });
            try
            {
                fixture.Writer.SubmitProgress("{\"realCheckpoint\":1}"); Assert.That(entered.Wait(5000), Is.True);
                Assert.That(Call(fixture.Instance, "FinishStateCheckpointWriter"), Is.False);
                Assert.That(fixture.Writer.ReadStatistics().closed, Is.False, "Close(0) must not poison an in-flight writer.");
                release.Set(); Assert.That(fixture.Writer.WaitForIdle(5000), Is.True);
                Assert.That(Call(fixture.Instance, "FinishStateCheckpointWriter"), Is.True);
                Assert.That(fixture.Writer.ReadStatistics().closed, Is.True);
                Assert.That(fixture.Writer.ReadStatistics().drainCompleted, Is.True);
                Assert.That(fixture.Writer.ReadStatistics().completedWrites, Is.EqualTo(1));
                Assert.That(Call(fixture.Instance, "FinishStateCheckpointWriter"), Is.True);
            }
            finally { release.Set(); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ActualWriterFailureCannotDisappearAtTheTerminalCutoff(bool cutoff)
        {
            using var fixture = new WriterFixture((_, _) => throw new IOException("Injected checkpoint write failure"));
            Set(fixture.Instance, "_terminalCutoff", cutoff);
            fixture.Writer.SubmitProgress("{}"); Assert.That(fixture.Writer.WaitForIdle(5000), Is.True);
            Call(fixture.Instance, "PollStateCheckpointWriter"); Call(fixture.Instance, "PollStateCheckpointWriter");
            Assert.That(fixture.Writer.ReadStatistics().failedWrites, Is.EqualTo(1));
            Assert.That(Get<int>(fixture.Report, "errors"), Is.EqualTo(cutoff ? 0 : 1));
            Assert.That(Get<int>(fixture.Instance, "_terminalLateErrors"), Is.EqualTo(cutoff ? 1 : 0));
        }
    }
}
#endif
