using System;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Windows 使用系统自带微软雅黑；其他编辑器平台按字体列表回退，不复制系统字体文件。</summary>
    public static class ChineseText
    {
        public static Font CreateFont() => Font.CreateDynamicFontFromOSFont(
            new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "PingFang SC", "Noto Sans CJK SC", "WenQuanYi Micro Hei" }, 22);

        // 底层库的英文异常保留在日志；界面始终给出可操作的中文提示。
        public static string Error(string detail, string fallback = "操作失败，请重试；详细原因见日志。")
        {
            if (string.IsNullOrWhiteSpace(detail)) return fallback;
            if (detail.IndexOf("host shutting down", StringComparison.OrdinalIgnoreCase) >= 0) return "房主已关闭房间，请重新创建或加入房间。";
            foreach (char c in detail) if (c >= '\u4e00' && c <= '\u9fff') return detail;
            return fallback;
        }
    }
}
