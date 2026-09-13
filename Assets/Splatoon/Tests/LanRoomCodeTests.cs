#if UNITY_EDITOR
using NUnit.Framework;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
namespace Splatoon.Tests
{
    public sealed class LanRoomCodeTests
    {
        [TestCase("192.168.1.8", 7777)] [TestCase("10.0.12.3", 65535)]
        [TestCase("172.16.0.1", 1)] [TestCase("127.0.0.1", 7788)]
        public void AddressAndPortRoundTrip(string ip, int port)
        {
            var code = LanRoomCode.Encode(ip, (ushort)port);
            Assert.That(LanRoomCode.TryDecode(code, out var endpoint, out var error), Is.True, error);
            Assert.That(endpoint.Address, Is.EqualTo(ip)); Assert.That(endpoint.Port, Is.EqualTo(port));
        }
        [Test] public void PasteAllowsSpacesLowercaseAndAmbiguousGlyphs()
        {
            var code = LanRoomCode.Encode("192.168.1.8", 7777).ToLowerInvariant().Replace("0", "o").Replace("1", "l");
            Assert.That(LanRoomCode.TryDecode(" \n"+code.Replace("-", " \t")+"\r\n", out var ep, out _), Is.True);
            Assert.That(ep.Address, Is.EqualTo("192.168.1.8"));
        }
        [Test] public void EverySingleCharacterTypoIsRejected()
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var code = LanRoomCode.Encode("192.168.1.8", 7777).Replace("-", "");
            for (int i = 0; i < code.Length; i++) foreach (char c in alphabet)
            {
                if (c == code[i]) continue;
                var chars = code.ToCharArray(); chars[i] = c;
                Assert.That(LanRoomCode.TryDecode(new string(chars), out _, out _), Is.False, new string(chars));
            }
        }
        [TestCase(null)] [TestCase("")] [TestCase("1234")] [TestCase("!!!!!!!!!!!!!")]
        [TestCase("ZZZZZZZZZZZZZ")] [TestCase("0000000000000")]
        public void InvalidInputHasChineseError(string input)
        { Assert.That(LanRoomCode.TryDecode(input, out _, out var error), Is.False); Assert.That(error, Does.Contain("房间码")); }
        [Test] public void InvalidEndpointCannotBeEncoded()
        {
            foreach (string ip in new[]{"0.0.0.0","255.255.255.255","224.0.0.1","::1","example.com"})
                Assert.Throws<System.ArgumentException>(() => LanRoomCode.Encode(ip, 7777));
            Assert.Throws<System.ArgumentException>(() => LanRoomCode.Encode("127.0.0.1", 0));
        }
        [Test] public void AvailableAddressesAreUniqueAndLoopbackRemainsAvailable()
        {
            var addresses = LanRoomCode.LocalAddresses();
            Assert.That(addresses, Is.Unique); Assert.That(addresses, Does.Contain("127.0.0.1"));
            foreach (string ip in addresses) Assert.DoesNotThrow(() => LanRoomCode.Encode(ip, 7777));
        }
        [Test] public void ChineseUiFontContainsRepresentativeGlyphs()
        {
            var font = ChineseText.CreateFont();
            try { foreach (char c in "喷墨对战房间码复制粘贴粉蓝队获胜潜墨生命网络连接失败") Assert.That(font.HasCharacter(c), Is.True, "缺字："+c); }
            finally { Object.DestroyImmediate(font); }
        }
    }
}
#endif
