#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class LanDiscoveryTests
    {
        static LanRoomAdvertisement Room(Guid? id = null, ushort port = 19777) => new()
        {
            RoomId = id ?? Guid.NewGuid(), GamePort = port, ModeId = 1, MapId = 1,
            ModeName = "三分钟涂地赛", MapName = "立体训练场", PlayerCount = 1, MaxPlayers = 4,
            PlayerProtocol = 7, PaintProtocol = 5, ConfigDigest = new string('a',64), Phase = MatchPhase.Practice
        };
        [Test] public void WireRoundTripAndMalformedPackets()
        {
            var request = Guid.NewGuid(); var a = Room(); var bytes = LanDiscoveryProtocol.Encode(request,a);
            Assert.That(bytes.Length,Is.LessThanOrEqualTo(LanDiscoveryProtocol.MaxPacketBytes));
            Assert.That(LanDiscoveryProtocol.TryDecode(bytes,bytes.Length,out var read,out var b),Is.True);
            Assert.That(read,Is.EqualTo(request)); Assert.That(b.RoomId,Is.EqualTo(a.RoomId));
            Assert.That(b.MapName,Is.EqualTo(a.MapName)); Assert.That(b.ConfigDigest,Is.EqualTo(a.ConfigDigest));
            var query = LanDiscoveryProtocol.Encode(request);
            Assert.That(LanDiscoveryProtocol.TryDecode(query,query.Length,out _,out var empty),Is.True); Assert.That(empty,Is.Null);
            for(int i=0;i<bytes.Length;i++) Assert.That(LanDiscoveryProtocol.TryDecode(bytes,i,out _,out _),Is.False,$"truncated at {i}");
            Assert.That(LanDiscoveryProtocol.TryDecode(bytes.Concat(new byte[1]).ToArray(),bytes.Length+1,out _,out _),Is.False);
            Assert.That(LanDiscoveryProtocol.TryDecode(new byte[1025],1025,out _,out _),Is.False);
            bytes[0]^=255; Assert.That(LanDiscoveryProtocol.TryDecode(bytes,bytes.Length,out _,out _),Is.False);
            a.PlayerCount=5; Assert.Throws<ArgumentException>(()=>LanDiscoveryProtocol.Encode(request,a));
            a.PlayerCount=1; a.MapName="bad\nname"; Assert.Throws<ArgumentException>(()=>LanDiscoveryProtocol.Encode(request,a));
        }
        [TestCase(0,false)] [TestCase(47777,false)] [TestCase(65536,false)] [TestCase(7777,true)] [TestCase(65535,true)]
        public void DiscoveryPortCannotBeUsedForGame(int port,bool valid) => Assert.That(LanDiscoveryProtocol.ValidGamePort(port),Is.EqualTo(valid));
        [Test] public void MultipleRoomsAndInterfacesAreMergedWithoutLosingEndpoints()
        {
            var d=new LanRoomDirectory();var q=Guid.NewGuid();d.Register(q,10);var a=Room();
            Assert.That(d.Accept(q,a,IPAddress.Parse("192.168.1.10"),10.025),Is.True);
            Assert.That(d.Accept(q,a,IPAddress.Parse("10.0.0.10"),10.050),Is.True);
            Assert.That(d.Accept(q,Room(port:19778),IPAddress.Parse("192.168.1.10"),10.060),Is.True);
            Assert.That(d.Rooms.Count,Is.EqualTo(2)); Assert.That(d.Rooms[0].Endpoints.Count,Is.EqualTo(2));
            Assert.That(d.Rooms[0].BestEndpoint.Address,Is.EqualTo("192.168.1.10"));
            Assert.That(d.Rooms[0].ProbeMilliseconds(10.1),Is.EqualTo(25).Within(.001));
            Assert.That(d.Rooms[0].ProbeMilliseconds(13.1),Is.Null);
        }
        [Test] public void UnsolicitedDuplicateExpiredAndReorderedRepliesDoNotRefreshRoom()
        {
            var d=new LanRoomDirectory();var a=Room();var old=Guid.NewGuid();var next=Guid.NewGuid();var ip=IPAddress.Loopback;
            Assert.That(d.Accept(old,a,ip,10),Is.False);d.Register(old,10);d.Register(next,10.1);
            var full=Room(a.RoomId);full.PlayerCount=4;full.Phase=MatchPhase.Playing;
            Assert.That(d.Accept(next,full,ip,10.15),Is.True);
            Assert.That(d.Accept(old,a,ip,10.2),Is.False);
            Assert.That(d.Accept(next,a,ip,10.3),Is.False);
            Assert.That(d.Rooms[0].Advertisement.PlayerCount,Is.EqualTo(4));
            Assert.That(d.Rooms[0].Advertisement.Phase,Is.EqualTo(MatchPhase.Playing));
            Assert.That(d.Accept(next,a,IPAddress.Parse("10.0.0.2"),11.2),Is.False);
            d.Expire(16.14);Assert.That(d.Rooms.Count,Is.EqualTo(1));
            d.Expire(16.16);Assert.That(d.Rooms,Is.Empty);
        }
        [Test] public void IncompatibleAndFullRoomsRemainVisible()
        {
            var d=new LanRoomDirectory();var a=Room();a.PlayerCount=4;a.PlayerProtocol=99;
            var q=Guid.NewGuid();d.Register(q,0);Assert.That(d.Accept(q,a,IPAddress.Loopback,.1),Is.True);
            Assert.That(d.Rooms.Count,Is.EqualTo(1));Assert.That(a.IsCompatible(7,5,new string('a',64)),Is.False);
            a.PlayerProtocol=7;Assert.That(a.IsCompatible(7,5,new string('b',64)),Is.False);
            Assert.That(a.IsCompatible(7,5,new string('a',64)),Is.True);
        }
        [Test] public void RealUdpDiscoversTwoHostsAndCanRestart()
        {
            using var host1=new UdpLanDiscoveryService();using var host2=new UdpLanDiscoveryService();using var browser=new UdpLanDiscoveryService();
            var a=Room();var b=Room(port:19778);
            host1.StartAdvertising(()=>a);host2.StartAdvertising(()=>b);
            Assert.That(host1.LastError,Is.Empty);Assert.That(host2.LastError,Is.Empty);
            for(int cycle=0;cycle<2;cycle++)
            {
                browser.StartBrowsing();var timeout=Stopwatch.StartNew();
                while(timeout.Elapsed.TotalSeconds<4 && browser.Rooms.Count(r=>r.Advertisement.RoomId==a.RoomId || r.Advertisement.RoomId==b.RoomId)<2)
                {browser.Tick();host1.Tick();host2.Tick();browser.Tick();Thread.Sleep(5);}
                Assert.That(browser.Rooms.Any(r=>r.Advertisement.RoomId==a.RoomId),Is.True,"host 1: "+browser.LastError);
                Assert.That(browser.Rooms.Any(r=>r.Advertisement.RoomId==b.RoomId),Is.True,"host 2: "+browser.LastError);
                browser.Stop();Assert.That(browser.Rooms,Is.Empty);
            }
            host1.Stop();host2.Stop();host1.StartAdvertising(()=>a);Assert.That(host1.LastError,Is.Empty);
        }
    }
}
#endif
