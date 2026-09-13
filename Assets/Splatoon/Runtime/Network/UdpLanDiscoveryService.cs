using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Splatoon.Networking
{
    // All methods are called on Unity's main thread. Nonblocking sockets avoid worker callbacks into Unity.
    public sealed class UdpLanDiscoveryService : ILanDiscoveryService
    {
        private sealed class ProbeSocket
        {
            public Socket Socket;
            public IPAddress Broadcast;
            public bool Loopback;
        }
        private readonly List<ProbeSocket> _browsers = new();
        private readonly LanRoomDirectory _directory = new();
        private readonly byte[] _buffer = new byte[LanDiscoveryProtocol.MaxPacketBytes + 1];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Socket _host;
        private Func<LanRoomAdvertisement> _snapshot;
        private bool _browsing;
        private double _nextProbe;
        public IReadOnlyList<LanRoomInfo> Rooms => _directory.Rooms;
        public string LastError { get; private set; } = "";

        private static Socket NewSocket() => new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        { Blocking = false, EnableBroadcast = true };

        public void StartBrowsing()
        {
            Stop(); _browsing = true;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork && ip.IPv4Mask != null)
                        {
                            byte[] a = ip.Address.GetAddressBytes(), mask = ip.IPv4Mask.GetAddressBytes();
                            if (mask.All(b => b == 255)) continue;
                            for (int i = 0; i < 4; i++) a[i] |= (byte)~mask[i];
                            AddBrowser(ip.Address, new IPAddress(a), false);
                        }
            }
            catch (NetworkInformationException) { LastError = "无法枚举局域网网卡，可刷新重试或使用房间码 / IP 加入。"; }
            AddBrowser(IPAddress.Loopback, IPAddress.Parse("127.255.255.255"), true);
            Refresh();
        }
        private void AddBrowser(IPAddress address, IPAddress broadcast, bool loopback)
        {
            Socket socket = null;
            try
            {
                socket = NewSocket(); socket.Bind(new IPEndPoint(address, 0));
                _browsers.Add(new ProbeSocket { Socket = socket, Broadcast = broadcast, Loopback = loopback });
            }
            catch (SocketException) { socket?.Dispose(); LastError = "部分网卡无法用于房间发现，可刷新重试或直接连接。"; }
        }
        public void StartAdvertising(Func<LanRoomAdvertisement> snapshot)
        {
            Stop();
            try
            {
                _host = NewSocket(); _host.ExclusiveAddressUse = false;
                _host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _host.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryProtocol.Port)); _snapshot = snapshot;
            }
            catch (SocketException) { _host?.Dispose(); _host = null; LastError = "房间已创建，但发现端口 47777 不可用；伙伴可用房间码 / IP 加入。"; }
        }
        public void Refresh() => _nextProbe = 0;
        public void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_browsing && now >= _nextProbe)
            {
                _nextProbe = now + LanDiscoveryProtocol.RefreshSeconds;
                foreach (var browser in _browsers)
                {
                    var request = Guid.NewGuid(); _directory.Register(request, now);
                    byte[] bytes = LanDiscoveryProtocol.Encode(request);
                    Send(browser.Socket, bytes, new IPEndPoint(browser.Broadcast, LanDiscoveryProtocol.Port));
                    if (browser.Loopback) Send(browser.Socket, bytes, new IPEndPoint(IPAddress.Loopback, LanDiscoveryProtocol.Port));
                }
            }
            if (_host != null) Receive(_host, true, now);
            foreach (var browser in _browsers) Receive(browser.Socket, false, now);
            _directory.Expire(now);
        }
        private void Send(Socket socket, byte[] bytes, EndPoint endpoint)
        {
            try { socket.SendTo(bytes, endpoint); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { }
            catch (SocketException) { LastError = "房间探测发送失败，请检查网卡或防火墙；仍可尝试直接连接。"; }
        }
        private void Receive(Socket socket, bool hosting, double now)
        {
            // Bound per-frame work even on a noisy LAN. Oversized datagrams are consumed and discarded.
            for (int i = 0; i < 64; i++)
            {
                EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try { count = socket.ReceiveFrom(_buffer, ref endpoint); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize || e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch (SocketException) { LastError = "房间探测接收失败，请刷新重试或直接连接。"; break; }
                if (!LanDiscoveryProtocol.TryDecode(_buffer, count, out Guid request, out var room)) continue;
                if (hosting && room == null)
                {
                    var current = _snapshot?.Invoke();
                    if (LanDiscoveryProtocol.Valid(current)) Send(socket, LanDiscoveryProtocol.Encode(request, current), endpoint);
                }
                else if (!hosting && room != null && ((IPEndPoint)endpoint).Port == LanDiscoveryProtocol.Port)
                    _directory.Accept(request, room, ((IPEndPoint)endpoint).Address, now);
            }
        }
        public double Now => _clock.Elapsed.TotalSeconds;
        public void Stop()
        {
            _browsing = false; _snapshot = null; _host?.Dispose(); _host = null;
            foreach (var browser in _browsers) browser.Socket.Dispose();
            _browsers.Clear(); _directory.Clear(); LastError = "";
        }
        public void Dispose() => Stop();
    }
}
