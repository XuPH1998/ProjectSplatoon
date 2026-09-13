using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Splatoon.Networking
{
    public sealed class LanRoomAdvertisement
    {
        public Guid RoomId;
        public ushort GamePort;
        public int ModeId, MapId, PlayerCount, MaxPlayers, PlayerProtocol, PaintProtocol;
        public string ModeName, MapName, ConfigDigest;
        public MatchPhase Phase;
        public bool IsCompatible(int playerProtocol, int paintProtocol, string digest) =>
            PlayerProtocol == playerProtocol && PaintProtocol == paintProtocol && ConfigDigest == digest;
    }

    public sealed class LanRoomEndpoint
    {
        public string Address { get; internal set; }
        public ushort Port { get; internal set; }
        public double LastSeen { get; internal set; }
        public double ProbeSentAt { get; internal set; }
        public double RttMilliseconds { get; internal set; }
    }

    public sealed class LanRoomInfo
    {
        internal readonly List<LanRoomEndpoint> MutableEndpoints = new();
        internal double MetadataSentAt = -1;
        public LanRoomAdvertisement Advertisement { get; internal set; }
        public IReadOnlyList<LanRoomEndpoint> Endpoints => MutableEndpoints;
        public LanRoomEndpoint BestEndpoint => MutableEndpoints.OrderByDescending(e => e.ProbeSentAt).ThenBy(e => e.RttMilliseconds).FirstOrDefault();
        public double? ProbeMilliseconds(double now) => BestEndpoint is { } e && now - e.LastSeen < LanDiscoveryProtocol.RefreshSeconds + 1 ? e.RttMilliseconds : null;
    }

    public interface ILanDiscoveryService : IDisposable
    {
        IReadOnlyList<LanRoomInfo> Rooms { get; }
        string LastError { get; }
        void StartBrowsing();
        void StartAdvertising(Func<LanRoomAdvertisement> snapshot);
        void Refresh();
        void Tick();
        void Stop();
    }

    // A small, bounded envelope. A game protocol mismatch remains visible; an unknown wire format is ignored.
    public static class LanDiscoveryProtocol
    {
        public const ushort Port = 47777;
        public const int MaxPacketBytes = 1024;
        public const double RefreshSeconds = 2, ExpirySeconds = 6, ProbeTimeoutSeconds = 1;
        private const uint Magic = 0x534C414E;
        private const byte Version = 1;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public static bool ValidGamePort(int port) => port > 0 && port <= ushort.MaxValue && port != Port;
        public static string Digest(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        public static bool Valid(LanRoomAdvertisement a) => a != null && a.RoomId != Guid.Empty && ValidGamePort(a.GamePort) &&
            a.ModeId > 0 && a.MapId > 0 && a.MaxPlayers > 0 && a.MaxPlayers <= 256 && a.PlayerCount >= 1 && a.PlayerCount <= a.MaxPlayers &&
            a.PlayerProtocol > 0 && a.PaintProtocol > 0 && (byte)a.Phase <= (byte)MatchPhase.Finished &&
            ValidText(a.ModeName) && ValidText(a.MapName) && a.ConfigDigest?.Length == 64 &&
            a.ConfigDigest.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        private static bool ValidText(string s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 64 && !s.Any(char.IsControl);
        public static byte[] Encode(Guid request, LanRoomAdvertisement room = null)
        {
            if (request == Guid.Empty || room != null && !Valid(room)) throw new ArgumentException("无效的房间发现数据");
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream, Utf8);
            w.Write(Magic); w.Write(Version); w.Write((byte)(room == null ? 0 : 1)); w.Write(request.ToByteArray());
            if (room != null)
            {
                w.Write(room.RoomId.ToByteArray()); w.Write(room.GamePort); w.Write(room.ModeId); w.Write(room.MapId);
                w.Write(room.PlayerCount); w.Write(room.MaxPlayers); w.Write((byte)room.Phase);
                w.Write(room.PlayerProtocol); w.Write(room.PaintProtocol);
                WriteText(w, room.ModeName); WriteText(w, room.MapName); WriteText(w, room.ConfigDigest);
            }
            return stream.ToArray();
        }
        private static void WriteText(BinaryWriter w, string value)
        { byte[] bytes = Utf8.GetBytes(value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        private static string ReadText(BinaryReader r)
        {
            int count = r.ReadUInt16();
            if (count > 256 || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException();
            return Utf8.GetString(r.ReadBytes(count));
        }
        public static bool TryDecode(byte[] data, int count, out Guid request, out LanRoomAdvertisement room)
        {
            request = Guid.Empty; room = null;
            if (data == null || count < 22 || count > MaxPacketBytes || count > data.Length) return false;
            try
            {
                using var stream = new MemoryStream(data, 0, count, false);
                using var r = new BinaryReader(stream, Utf8);
                if (r.ReadUInt32() != Magic || r.ReadByte() != Version) return false;
                byte kind = r.ReadByte(); if (kind > 1) return false;
                request = new Guid(r.ReadBytes(16)); if (request == Guid.Empty) return false;
                if (kind == 1)
                {
                    room = new LanRoomAdvertisement
                    {
                        RoomId = new Guid(r.ReadBytes(16)), GamePort = r.ReadUInt16(), ModeId = r.ReadInt32(), MapId = r.ReadInt32(),
                        PlayerCount = r.ReadInt32(), MaxPlayers = r.ReadInt32(), Phase = (MatchPhase)r.ReadByte(),
                        PlayerProtocol = r.ReadInt32(), PaintProtocol = r.ReadInt32(),
                        ModeName = ReadText(r), MapName = ReadText(r), ConfigDigest = ReadText(r)
                    };
                    if (!Valid(room)) { room = null; return false; }
                }
                return stream.Position == stream.Length;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException) { room = null; return false; }
        }
    }

    // Pure state/clock logic, separate from sockets and Unity so packet ordering and expiry can be tested.
    public sealed class LanRoomDirectory
    {
        private readonly Dictionary<Guid, double> _requests = new();
        private readonly List<LanRoomInfo> _rooms = new();
        public IReadOnlyList<LanRoomInfo> Rooms => _rooms;
        public void Register(Guid request, double now) => _requests[request] = now;
        public void Clear() { _requests.Clear(); _rooms.Clear(); }
        public bool Accept(Guid request, LanRoomAdvertisement a, IPAddress address, double now)
        {
            if (!LanDiscoveryProtocol.Valid(a) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) || address.GetAddressBytes()[0] >= 224 ||
                !_requests.TryGetValue(request, out double sent) || now < sent || now - sent > LanDiscoveryProtocol.ProbeTimeoutSeconds) return false;
            var room = _rooms.Find(x => x.Advertisement.RoomId == a.RoomId);
            if (room == null)
            {
                if (_rooms.Count >= 256) return false;
                room = new LanRoomInfo { Advertisement = a }; _rooms.Add(room);
            }
            string ip = address.ToString();
            var endpoint = room.MutableEndpoints.Find(e => e.Address == ip && e.Port == a.GamePort);
            if (endpoint != null && sent <= endpoint.ProbeSentAt) return false; // duplicate or reordered reply
            if (endpoint == null)
            {
                if (room.MutableEndpoints.Count >= 16) return false;
                endpoint = new LanRoomEndpoint { Address = ip, Port = a.GamePort }; room.MutableEndpoints.Add(endpoint);
            }
            endpoint.LastSeen = now; endpoint.ProbeSentAt = sent; endpoint.RttMilliseconds = (now - sent) * 1000;
            if (sent >= room.MetadataSentAt) { room.Advertisement = a; room.MetadataSentAt = sent; }
            return true;
        }
        public void Expire(double now)
        {
            foreach (Guid id in _requests.Where(p => now - p.Value > LanDiscoveryProtocol.ProbeTimeoutSeconds).Select(p => p.Key).ToArray()) _requests.Remove(id);
            foreach (var room in _rooms) room.MutableEndpoints.RemoveAll(e => now - e.LastSeen >= LanDiscoveryProtocol.ExpirySeconds);
            _rooms.RemoveAll(room => room.MutableEndpoints.Count == 0);
        }
    }
}
