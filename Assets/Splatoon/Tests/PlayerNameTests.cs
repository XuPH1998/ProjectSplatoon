using System;
using System.Linq;
using NUnit.Framework;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class PlayerNameTests
    {
        [TestCase(" 墨水玩家 ", "墨水玩家")]
        [TestCase("Ink Player_42", "Ink Player_42")]
        [TestCase("<b>名字</b>", "<b>名字</b>")]
        [TestCase("😀墨水", "😀墨水")]
        public void NormalizesWithoutInterpretingText(string input, string expected)
        {
            Assert.That(PlayerNames.TryNormalize(input, out var result, out var error), Is.True, error);
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(new FixedString128Bytes(result).ToString(), Is.EqualTo(expected));
        }

        [TestCase(null)] [TestCase("")] [TestCase("   ")] [TestCase("\n玩家")]
        [TestCase("玩家\r")] [TestCase("玩\t家")] [TestCase("玩\0家")]
        [TestCase("玩\u2028家")] [TestCase("玩\u2029家")]
        [TestCase("12345678901234567")]
        public void RejectsInvalidNames(string input) => Assert.That(PlayerNames.TryNormalize(input, out _, out _), Is.False);

        [Test] public void UnicodeLimitCountsScalarsAndRejectsBrokenSurrogates()
        {
            string sixteen = string.Concat(Enumerable.Repeat("😀", 16));
            Assert.That(PlayerNames.TryNormalize(sixteen, out _, out _), Is.True);
            Assert.That(PlayerNames.TryNormalize(sixteen + "中", out _, out _), Is.False);
            Assert.That(PlayerNames.TryNormalize("\ud800", out _, out _), Is.False);
            Assert.That(PlayerNames.TryNormalize("\udc00", out _, out _), Is.False);
            byte[] signature = new byte[32];
            Assert.That(PlayerConnectionPayload.TryDecode(PlayerConnectionPayload.Encode(signature, sixteen), signature, out var name, out _), Is.True);
            Assert.That(name, Is.EqualTo(sixteen));
        }

        [Test] public void SaveReloadAndDefaultAreStable()
        {
            bool had = PlayerPrefs.HasKey(PlayerNames.PreferenceKey);
            string previous = PlayerPrefs.GetString(PlayerNames.PreferenceKey);
            try
            {
                PlayerPrefs.DeleteKey(PlayerNames.PreferenceKey);
                string generated = PlayerNames.Load();
                Assert.That(generated, Does.Match("^玩家[0-9]{4}$"));
                Assert.That(PlayerNames.Load(), Is.EqualTo(generated));
                Assert.That(PlayerNames.Save(" 测试玩家 "), Is.True);
                Assert.That(PlayerNames.Load(), Is.EqualTo("测试玩家"));
                Assert.That(PlayerNames.Save("\n"), Is.False);
                Assert.That(PlayerNames.Load(), Is.EqualTo("测试玩家"));
            }
            finally
            {
                if (had) PlayerPrefs.SetString(PlayerNames.PreferenceKey, previous); else PlayerPrefs.DeleteKey(PlayerNames.PreferenceKey);
                PlayerPrefs.Save();
            }
        }

        [Test] public void AdmissionRejectsOldProtocolMismatchTruncationAndMalformedUtf8()
        {
            byte[] signature = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            byte[] payload = PlayerConnectionPayload.Encode(signature, "墨水 Alice");
            Assert.That(PlayerConnectionPayload.TryDecode(payload, signature, out var name, out _), Is.True);
            Assert.That(name, Is.EqualTo("墨水 Alice"));
            Assert.That(PlayerConnectionPayload.TryDecode(signature, signature, out _, out _), Is.False, "old signature-only payload");
            var old = (byte[])payload.Clone(); old[0]--;
            Assert.That(PlayerConnectionPayload.TryDecode(old, signature, out _, out _), Is.False);
            var mismatch = (byte[])payload.Clone(); mismatch[4] ^= 1;
            Assert.That(PlayerConnectionPayload.TryDecode(mismatch, signature, out _, out _), Is.False);
            Assert.That(PlayerConnectionPayload.TryDecode(payload.Take(payload.Length - 1).ToArray(), signature, out _, out _), Is.False);
            Assert.That(PlayerConnectionPayload.TryDecode(payload.Concat(new byte[] { 0 }).ToArray(), signature, out _, out _), Is.False);
            var broken = (byte[])payload.Clone(); broken[37] = 0xff;
            Assert.That(PlayerConnectionPayload.TryDecode(broken, signature, out _, out _), Is.False);
            var blank = PlayerConnectionPayload.Encode(signature, "a"); blank[37] = 0x20;
            Assert.That(PlayerConnectionPayload.TryDecode(blank, signature, out _, out _), Is.False);
            blank[37] = 0x0a;
            Assert.That(PlayerConnectionPayload.TryDecode(blank, signature, out _, out _), Is.False);
        }

        [Test] public void NameVisibilityExcludesSelfDeathAndEveryPaperForm()
        {
            var state = new PlayerSnapshot { Health = 100 };
            Assert.That(PrototypeApp.NameTagEligible(false, state), Is.True);
            Assert.That(PrototypeApp.NameTagEligible(true, state), Is.False);
            state.Swimming = true; Assert.That(PrototypeApp.NameTagEligible(false, state), Is.False);
            state.Swimming = false; state.CompactBody = true; Assert.That(PrototypeApp.NameTagEligible(false, state), Is.False);
            state.CompactBody = false; state.Health = 0; Assert.That(PrototypeApp.NameTagEligible(false, state), Is.False);
        }

        [Test] public void OcclusionUsesHeadAndIgnoresTriggersAndPlayerLayer()
        {
            var cameraGo = new GameObject("Name visibility camera");
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var camera = cameraGo.AddComponent<Camera>(); camera.transform.position = new Vector3(0, 100, 0);
                Vector3 head = new(0, 100, 10), anchor = head + Vector3.up * .25f;
                wall.transform.position = new Vector3(0, 100, 5); wall.transform.localScale = new Vector3(3, 3, .2f);
                Physics.SyncTransforms();
                Assert.That(PrototypeApp.TryNameTagPosition(camera, head, anchor, out _), Is.False);
                wall.GetComponent<Collider>().isTrigger = true;
                Assert.That(PrototypeApp.TryNameTagPosition(camera, head, anchor, out _), Is.True);
                wall.GetComponent<Collider>().isTrigger = false; wall.layer = 8;
                Assert.That(PrototypeApp.TryNameTagPosition(camera, head, anchor, out _), Is.True);
                Assert.That(PrototypeApp.TryNameTagPosition(camera, head, new Vector3(0, 100, -10), out _), Is.False);
                Assert.That(PrototypeApp.TryNameTagPosition(camera, head, new Vector3(1000, 100, 10), out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(cameraGo); }
        }
    }
}
