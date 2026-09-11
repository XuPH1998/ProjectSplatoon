using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Splatoon.Networking
{
    /// <summary>局域网地址码：版本、IPv4、UDP 端口及 CRC-8；无需互联网或广播服务。
    /// 不是密码，同一地址和端口生成相同的码。CRC 仅用于发现输错字符。</summary>
    public static class LanRoomCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public static string Encode(string address, ushort port)
        {
            if (!IPAddress.TryParse(address, out var ip) || !Usable(ip) || port == 0)
                throw new ArgumentException("房间码需要有效的 IPv4 地址和 1～65535 的端口。");
            var bytes = new byte[8]; bytes[0] = 1;
            Array.Copy(ip.GetAddressBytes(), 0, bytes, 1, 4);
            bytes[5] = (byte)(port >> 8); bytes[6] = (byte)port; bytes[7] = Checksum(bytes);
            ulong value = 0; foreach (byte b in bytes) value = (value << 8) | b;
            var chars = new char[13];
            for (int i = 12; i >= 0; i--) { chars[i] = Alphabet[(int)(value & 31)]; value >>= 5; }
            var raw = new string(chars);
            return raw.Substring(0, 4) + "-" + raw.Substring(4, 4) + "-" + raw.Substring(8);
        }
        public static bool TryDecode(string code, out LanJoinOptions endpoint, out string error)
        {
            endpoint = default; error = "";
            var normalized = new StringBuilder();
            foreach (char c in code ?? "")
            {
                if (char.IsWhiteSpace(c) || c == '-') continue;
                char upper = char.ToUpperInvariant(c);
                normalized.Append(upper == 'O' ? '0' : upper == 'I' || upper == 'L' ? '1' : upper);
            }
            if (normalized.Length != 13) { error = "请输入完整房间码：共 13 位字母或数字，可直接粘贴。"; return false; }
            ulong value = 0;
            for (int i = 0; i < 13; i++)
            {
                int digit = Alphabet.IndexOf(normalized[i]);
                if (digit < 0 || (i == 0 && digit > 15)) { error = "房间码含有无效字符，请重新复制。"; return false; }
                value = (value << 5) | (uint)digit;
            }
            var bytes = new byte[8];
            for (int i = 7; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
            if (Checksum(bytes) != bytes[7]) { error = "房间码校验失败，请检查是否输错或重新复制。"; return false; }
            if (bytes[0] != 1) { error = "房间码版本不兼容，请双方使用同一版本游戏。"; return false; }
            var ip = new IPAddress(new byte[] {bytes[1], bytes[2], bytes[3], bytes[4]});
            ushort port = (ushort)((bytes[5] << 8) | bytes[6]);
            if (!Usable(ip) || port == 0) { error = "房间码中的地址或端口无效。"; return false; }
            endpoint = new LanJoinOptions(ip.ToString(), port); return true;
        }
        private static byte Checksum(byte[] bytes)
        {
            byte crc = 0;
            for (int i = 0; i < 7; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++) crc = (byte)((crc & 128) != 0 ? (crc << 1) ^ 7 : crc << 1);
            }
            return crc;
        }
        private static bool Usable(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] > 0 && ip.GetAddressBytes()[0] < 224;

        /// <summary>优先显示具有默认网关的活动网卡；保留回环地址用于同机测试。多网卡时由房主选择。</summary>
        public static string[] LocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .Where(ip => Usable(ip) && !IPAddress.IsLoopback(ip)).Select(ip => ip.ToString())
                    .Concat(new[] { "127.0.0.1" }).Distinct().ToArray();
            }
            catch (NetworkInformationException) { return new[] { "127.0.0.1" }; }
        }
    }
}
