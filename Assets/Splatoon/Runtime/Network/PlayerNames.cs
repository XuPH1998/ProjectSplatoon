using System;
using System.Text;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Networking
{
    public static class PlayerNames
    {
        public const int MaxCharacters = 16;
        public const string PreferenceKey = "Splatoon.PlayerName";

        public static bool TryNormalize(string input, out string name, out string error)
        {
            name = ""; error = "用户名须为 1～16 个字符，不能包含换行或控制字符。";
            if (input == null) return false;
            int count = 0;
            // Check before trimming: even leading/trailing newlines are invalid input.
            foreach (char c in input)
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029') return false;
            string value = input.Trim();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
                }
                else if (char.IsLowSurrogate(c)) return false;
                if (++count > MaxCharacters) return false;
            }
            if (count == 0) return false;
            name = value; error = ""; return true;
        }

        public static string Load()
        {
            if (TryNormalize(PlayerPrefs.GetString(PreferenceKey, ""), out var name, out _)) return name;
            name = "玩家" + UnityEngine.Random.Range(0, 10000).ToString("D4");
            Save(name); return name;
        }

        public static bool Save(string input)
        {
            if (!TryNormalize(input, out var name, out _)) return false;
            PlayerPrefs.SetString(PreferenceKey, name); PlayerPrefs.Save(); return true;
        }
    }

    // Bounded admission metadata, independent of high-frequency movement snapshots.
    public static class PlayerConnectionPayload
    {
        const int SignatureBytes = 32;
        const int HeaderBytes = 5 + SignatureBytes; // protocol uint, signature, UTF-8 byte count
        static readonly UTF8Encoding Utf8 = new(false, true);

        public static byte[] Encode(byte[] signature, string input)
        {
            if (signature == null || signature.Length != SignatureBytes) throw new ArgumentException("Invalid content signature.");
            if (!PlayerNames.TryNormalize(input, out var name, out var error)) throw new ArgumentException(error);
            byte[] text = Utf8.GetBytes(name), data = new byte[HeaderBytes + text.Length];
            uint version = PlayerSnapshot.ProtocolVersion;
            for (int i = 0; i < 4; i++) data[i] = (byte)(version >> (8 * i));
            Buffer.BlockCopy(signature, 0, data, 4, SignatureBytes);
            data[HeaderBytes - 1] = (byte)text.Length;
            Buffer.BlockCopy(text, 0, data, HeaderBytes, text.Length);
            return data;
        }

        public static bool TryDecode(byte[] data, byte[] expectedSignature, out string name, out string error)
        {
            name = "";
            error = $"协议或游戏内容不一致（玩家协议 {PlayerSnapshot.ProtocolVersion}），请使用相同版本、地图、配置和角色资源。";
            if (data == null || data.Length < HeaderBytes || data.Length > HeaderBytes + 64 ||
                expectedSignature == null || expectedSignature.Length != SignatureBytes) return false;
            uint version = (uint)(data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24);
            if (version != PlayerSnapshot.ProtocolVersion) return false;
            for (int i = 0; i < SignatureBytes; i++) if (data[4 + i] != expectedSignature[i]) return false;
            error = "用户名数据无效，请返回大厅重新设置。";
            if (data[HeaderBytes - 1] != data.Length - HeaderBytes) return false;
            try { return PlayerNames.TryNormalize(Utf8.GetString(data, HeaderBytes, data.Length - HeaderBytes), out name, out error); }
            catch (DecoderFallbackException) { return false; }
        }
    }
}
