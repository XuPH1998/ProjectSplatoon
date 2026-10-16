using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Splatoon.Config;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        bool _combatFontsPrewarmed;

        // Normal loading work: preserve the existing OS Font and actual cached styles.
        // A late joining player's new name or a later DPI/cache change can still be cold.
        async UniTask PrewarmCombatTextAsync(string phase, CancellationToken token)
        {
            _combatFontsPrewarmed = false;
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log($"[GUI字体预热] phase={phase} status=headless-skip ready={Ready} busy={Busy} inRoom={InRoom}");
                return;
            }

            // Styles() reads GUI.skin and must remain in the existing OnGUI path.
            // Fixtures or unusual graphics setups must never wait indefinitely for it.
            var numbers = CultureInfo.CurrentCulture.NumberFormat;
            string numeric = "0123456789 :/% -…" + numbers.NumberGroupSeparator +
                numbers.NumberDecimalSeparator + numbers.PercentGroupSeparator + numbers.PercentDecimalSeparator +
                numbers.PercentSymbol + numbers.NegativeSign;
            const string controls = "房主房间往返 ms Tab Esc 战绩菜单 左键 Shift 弦化 蓄力射击 E Q 副武器大招 可使用使用中暂不可用 已就绪按启动";
            const string recovery = "材料预留取消不足可慢速蓄力松开射击恢复锁定己方积木上快速空中弦化普通 · ";
            const string combatHud =
                "蓄力 材料限制，松开发射 枪口被遮挡 W/S 上下　A/D 横移　空格跳离　松开 Shift 脱墙 " +
                "无限热身 · H 选择配装 · Esc 调试房菜单 热身 · H 选择配装 · 双方有真人后由房主开始 出生区 · H 更换配装 已被击杀 · 秒后重生 " +
                "蓄力： 次爆炸 · 松开投出 瞄准中 · 松开 E 使用 · Shift 取消 准备投出 · 仍可调整方向 倒地中 · 不可使用 等待重生 " +
                "材料恢复等待 s 可以恢复材料 再使用 场上 投掷参考 · 索敌后落点会改变 毒雾：减速／持续消耗材料 ◆ 标记 " +
                "等待落地 准备冲刺 制动蓄爆 爆炸后恢复 冲刺中 · 左键提前制动 持握中 · 左键瞄准投出 剩余 次 · " +
                "建造／拆敌充能 倒地中 · 能量保留 死亡 · 能量已减半 充能锁定 能量不足 空间不足，无法启动 动作恢复中 按住左键瞄准，松开投出 " +
                "靠近队友，等待解救 剩余等待时间 秒 W A S D 缓慢移动 · 队友靠近按 F 解救 解救队友 彻底击杀 " +
                "连射 · 剩余 发 满蓄 · 松开连射 慢速蓄力 · 松开连射 蓄力 · 松开连射 按住蓄力 · 松开连射 击倒 " +
                "游戏开始！ 搭建路线与掩体，拆毁敌方积木。结束时积木更多的队伍获胜 " +
                "操作说明 空格 跳跃 左键按住蓄力／松开连射 左键点击单发／长按连续 左键蓄力／松开发射 左键按住射击 " +
                "Shift 弦化／取消投掷 E 按住瞄准，松开使用副武器 Q 启动大招 F 解救队友／彻底击杀 H 热身或出生区更换配装 Tab 按住查看战绩 Esc 关闭界面／房间菜单 回车 开始比赛（房主）";
            // Room-menu labels use this same font while the match continues.
            const string matchMenu = "平局 继续游戏 添加敌方BOT 清除BOT 本局战绩 退出房间";
            // Concatenate actual local/configured text; never split surrogate pairs.
            var heroNames = new StringBuilder(numeric);
            var weaponNames = new StringBuilder(numeric + "生命副武器大招");
            var loadoutNames = new StringBuilder(numeric + "粉队蓝队热身已结束" + controls + recovery);
            foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                heroNames.Append(' ').Append(hero.DisplayName);
                weaponNames.Append(' ').Append(hero.WeaponTypeName);
            }
            foreach (int id in SubWeaponConfigService.Current.Ids)
                loadoutNames.Append(' ').Append(SubWeaponConfigService.Current.GetById(id).Common.displayName);
            foreach (int id in SpecialWeaponConfigService.Current.Ids)
                loadoutNames.Append(' ').Append(SpecialWeaponConfigService.Current.Get(id).Name);
            loadoutNames.Append(' ').Append(combatHud).Append(' ').Append(matchMenu);
            foreach (SubWeaponFailure failure in Enum.GetValues(typeof(SubWeaponFailure)))
                loadoutNames.Append(' ').Append(SubWeaponSimulation.FailureText(failure));
            string username = (SavedUsername ?? "") + " 玩家 测试 BOT  ·  你 …" + numeric;

            int yields = 0, calls = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 0; attempt <= 4; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (_small != null && _chineseFont != null)
                {
                    UiTextStyles();
                    NameStyles();
                    if (MeasureCombatFont(_uiCenter, loadoutNames.ToString(), ref calls)) break;
                }
                if (attempt == 4)
                {
                    Debug.LogWarning($"[GUI字体预热] phase={phase} status=unavailable yields={yields} calls={calls} ready={Ready} busy={Busy} inRoom={InRoom}");
                    return;
                }
                await UniTask.Yield(PlayerLoopTiming.Update, token);
                yields++;
            }

            bool valid = true; // Static HUD/loadout text is warm; request remaining configured/local text below.
            valid &= MeasureCombatFont(_uiText, weaponNames.ToString() + combatHud, ref calls);
            valid &= MeasureCombatFont(_uiMuted, numeric + recovery, ref calls);
            valid &= MeasureCombatFont(_uiStrong, heroNames.ToString() + combatHud, ref calls);
            valid &= MeasureCombatFont(_uiClock, numeric + "热身已结束", ref calls);
            valid &= MeasureCombatFont(_uiScore, numeric, ref calls);
            valid &= MeasureCombatFont(_uiRingValue, numeric + "使用中", ref calls);
            valid &= MeasureCombatFont(_uiWrap, numeric + recovery + controls + combatHud, ref calls);
            valid &= MeasureCombatFont(_heroAction, "可使用", ref calls);
            valid &= MeasureCombatFont(_nameTag, username, ref calls);
            valid &= MeasureCombatFont(_scoreName, username, ref calls);
            _combatFontsPrewarmed = valid;
            timer.Stop();
            Debug.Log($"[GUI字体预热] phase={phase} status={(_combatFontsPrewarmed ? "complete" : "invalid-dimensions")} font={_chineseFont.name} calls={calls} yields={yields} elapsedMs={timer.Elapsed.TotalMilliseconds:F3} frame={Time.frameCount} screen={Screen.width}x{Screen.height} ready={Ready} busy={Busy} inRoom={InRoom} scope=finite-hud");
        }

        bool MeasureCombatFont(GUIStyle style, string text, ref int calls)
        {
            if (style == null || style.font != _chineseFont || string.IsNullOrEmpty(text)) return false;
            calls++;
            Vector2 size = style.CalcSize(new GUIContent(text));
            return size.x > 0 && size.y > 0 && !float.IsNaN(size.x) && !float.IsNaN(size.y) &&
                !float.IsInfinity(size.x) && !float.IsInfinity(size.y);
        }
    }
}
