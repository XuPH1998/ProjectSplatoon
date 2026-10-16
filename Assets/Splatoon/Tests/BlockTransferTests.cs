using System;
using NUnit.Framework;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class BlockTransferTests
    {
        static BlockTransferFragment Part(byte[] bytes, int index)
        {
            int offset = index * BlockTransferFragment.PayloadBytes;
            int count = Math.Min(BlockTransferFragment.PayloadBytes, bytes.Length - offset);
            var data = new byte[count]; Buffer.BlockCopy(bytes, offset, data, 0, count);
            return new BlockTransferFragment { Round = 4, Id = 9, BaseRevision = 20, Revision = 24,
                Length = bytes.Length, Hash = BlockTransferFragment.Checksum(bytes), Index = index, Data = data };
        }
        [Test] public void ReorderedAndRepeatedFragmentsAssembleOnce()
        {
            var bytes = new byte[2049]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31);
            var assembly = new BlockTransferAssembly(Part(bytes, 2));
            Assert.That(assembly.Add(Part(bytes, 2)), Is.True);
            Assert.That(assembly.Add(Part(bytes, 2)), Is.True);
            Assert.That(assembly.ReceivedCount, Is.EqualTo(1));
            Assert.That(assembly.Add(Part(bytes, 0)), Is.True); Assert.That(assembly.Complete, Is.False);
            Assert.That(assembly.Add(Part(bytes, 1)), Is.True); Assert.That(assembly.Verify(), Is.True);
            CollectionAssert.AreEqual(bytes, assembly.Bytes);
        }
        [Test] public void NewRoundAndChangedManifestCannotMixIntoTransfer()
        {
            var bytes = new byte[1000]; var assembly = new BlockTransferAssembly(Part(bytes, 0));
            var changed = Part(bytes, 0); changed.Round++; Assert.That(assembly.Add(changed), Is.False);
            changed = Part(bytes, 0); changed.Revision++; Assert.That(assembly.Add(changed), Is.False);
            changed = Part(bytes, 0); changed.BaseRevision--; Assert.That(assembly.Add(changed), Is.False);
            changed = Part(bytes, 0); changed.Full = true; Assert.That(assembly.Add(changed), Is.False);
            Assert.That(assembly.ReceivedCount, Is.Zero);
        }
        [Test] public void CorruptionAndMalformedPayloadDoNotPassVerification()
        {
            var bytes = new byte[1000]; var assembly = new BlockTransferAssembly(Part(bytes, 0));
            var bad = Part(bytes, 0); bad.Data[12] = 1; Assert.That(assembly.Add(bad), Is.True);
            Assert.That(assembly.Add(Part(bytes, 1)), Is.True); Assert.That(assembly.Verify(), Is.False);
            bad = Part(bytes, 1); bad.Data = new byte[BlockTransferFragment.PayloadBytes]; Assert.That(bad.Valid, Is.False);
            bad = Part(bytes, 0); bad.Length = BlockTransferFragment.MaxTransferBytes + 1; Assert.That(bad.Valid, Is.False);
        }
        // Sender setup branch only: no socket, RPC execution, real ACK or network acceptance is claimed.
        [Test]
        public void BatchSenderSharesOnlyPayloadAndKeepsEveryPeersTransferStateIndependent()
        {
            var terrainObject = new UnityEngine.GameObject("Batch sender terrain");
            var matchObject = new UnityEngine.GameObject("Inactive batch sender"); matchObject.SetActive(false);
            try
            {
                var terrain = terrainObject.AddComponent<Splatoon.Blocks.BlockTerrain>(); terrain.Initialize(null);
                var world = terrain.World; world.Set(UnityEngine.Vector3Int.zero, Splatoon.Blocks.BlockCell.Create(1, true));
                world.Seal(1, new[] { UnityEngine.Vector3Int.zero });
                world.Set(UnityEngine.Vector3Int.right, Splatoon.Blocks.BlockCell.Create(2)); world.Seal(2, new[] { UnityEngine.Vector3Int.zero });
                matchObject.AddComponent<Unity.Netcode.NetworkObject>(); var match = matchObject.AddComponent<Splatoon.Prototype.PrototypeMatch>();
                typeof(Splatoon.Prototype.PrototypeMatch).GetProperty("Blocks").SetValue(match, terrain);
                var peers = (System.Collections.IList)BatchRead(match, "_blockTransferStarts");
                var requests = (System.Collections.IList)BatchRead(match, "_blockTransferRequests");
                var peerType = typeof(Splatoon.Prototype.PrototypeMatch).GetNestedType("BlockPeer", System.Reflection.BindingFlags.NonPublic);
                var input = new[] { new Splatoon.Blocks.BlockWorld.EncodingRequest(false, 0), new Splatoon.Blocks.BlockWorld.EncodingRequest(false, 0),
                    new Splatoon.Blocks.BlockWorld.EncodingRequest(true), new Splatoon.Blocks.BlockWorld.EncodingRequest(false, 1) };
                var owners = new object[input.Length];
                for (int i = 0; i < input.Length; i++)
                {
                    owners[i] = Activator.CreateInstance(peerType, true); BatchWrite(owners[i], "Ready", !input[i].Full);
                    BatchWrite(owners[i], "Applied", input[i].Since); peers.Add(owners[i]); requests.Add(input[i]);
                }
                BatchCall(match, "StartPendingBlockTransfers"); Assert.That(peers, Is.Empty); Assert.That(requests, Is.Empty);
                var sends = Array.ConvertAll(owners, owner => BatchRead(owner, "Send"));
                Assert.That(BatchRead(sends[0], "Bytes"), Is.SameAs(BatchRead(sends[1], "Bytes")));
                var ids = new System.Collections.Generic.HashSet<uint>();
                for (int i = 0; i < sends.Length; i++)
                {
                    Assert.That(ids.Add((uint)BatchRead(sends[i], "Id")), Is.True);
                    var bytes = (byte[])BatchRead(sends[i], "Bytes");
                    CollectionAssert.AreEqual(input[i].Full ? world.EncodeSnapshot() : world.EncodeDelta(input[i].Since), bytes);
                    Assert.That(BatchRead(sends[i], "Base"), Is.EqualTo(input[i].Since)); Assert.That(BatchRead(sends[i], "Full"), Is.EqualTo(input[i].Full));
                    Assert.That(BatchRead(sends[i], "Revision"), Is.EqualTo(world.Revision));
                    Assert.That(BatchRead(sends[i], "Hash"), Is.EqualTo(BlockTransferFragment.Checksum(bytes)));
                    Assert.That(BatchRead(owners[i], "Applied"), Is.EqualTo(input[i].Since)); Assert.That(BatchRead(owners[i], "Ready"), Is.EqualTo(!input[i].Full));
                    if (i > 0) { Assert.That(BatchRead(sends[i], "Ack"), Is.Not.SameAs(BatchRead(sends[0], "Ack"))); Assert.That(BatchRead(sends[i], "Sent"), Is.Not.SameAs(BatchRead(sends[0], "Sent"))); }
                }
                var originalStarted = Array.ConvertAll(sends, send => (double)BatchRead(send, "Started"));
                ((bool[])BatchRead(sends[0], "Ack"))[0] = true; ((double[])BatchRead(sends[0], "Sent"))[0] = 123;
                BatchWrite(sends[0], "Cursor", 7); BatchWrite(sends[0], "Started", 123d);
                for (int i = 1; i < sends.Length; i++)
                {
                    Assert.That(((bool[])BatchRead(sends[i], "Ack"))[0], Is.False); Assert.That(((double[])BatchRead(sends[i], "Sent"))[0], Is.Zero);
                    Assert.That(BatchRead(sends[i], "Cursor"), Is.EqualTo(0)); Assert.That(BatchRead(sends[i], "Started"), Is.EqualTo(originalStarted[i]));
                }
                // The outside-Update snapshot/retry path still builds an independent transfer.
                BatchCall(match, "StartBlockTransfer", owners[1], false); var retry = BatchRead(owners[1], "Send");
                Assert.That(BatchRead(retry, "Id"), Is.Not.EqualTo(BatchRead(sends[1], "Id")));
                Assert.That(BatchRead(retry, "Bytes"), Is.Not.SameAs(BatchRead(sends[1], "Bytes")));
                Assert.That(((bool[])BatchRead(retry, "Ack"))[0], Is.False); Assert.That(((double[])BatchRead(retry, "Sent"))[0], Is.Zero);
                CollectionAssert.AreEqual(world.EncodeDelta(0), (byte[])BatchRead(retry, "Bytes"));
            }
            finally { UnityEngine.Object.DestroyImmediate(matchObject); UnityEngine.Object.DestroyImmediate(terrainObject); }
        }
        static object BatchRead(object owner, string name)
            => owner.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(owner);
        static void BatchWrite(object owner, string name, object value)
            => owner.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).SetValue(owner, value);
        static void BatchCall(object owner, string name, params object[] args)
            => owner.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(owner, args);

    }
}
