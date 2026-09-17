# 沫澜 / 满溢泡泡枪

现行11.3.0对齐：每颗32伤害、每组4颗/8墨、32帧首颗到首颗周期、5帧颗间隔；逐颗初速和碰撞成长，第一颗5个沿途墨滴；行走4.31335／游泳8.62670／射击2.24654米/秒。完整实现与最新验收见 [对齐报告](../../WeaponAlignment1130/Report.md)。下文中的旧参数和旧验收证据不代表本轮版本。

ID 7，BubbleGirl。大厅、热身换英雄及“单机武器调试”均通过英雄表列出；选择面板改为双列四行容纳第七卡片。角色、枪械外观及站立瞄准、移动、转身、射击、死亡动作来自 ShotgunGirl；表现配置、角色预制体、武器和弹药配置独立。没有新增副武器、特殊技能、换弹或浴缸模型。

## 操作与参数

点击提交一组四颗泡泡，松开仍发完；长按每 0.55 秒一组。每颗出膛重新读取逻辑枪口和瞄准，转向可甩出扇面。首颗扣整组 8 墨，死亡、换英雄、回合结束取消剩余颗数，不退已消耗墨量。潜墨等待第四颗；人形射击移动限制覆盖整组周期。

| 参数 | 初版值 |
|---|---|
| 生命、墨量、普通移速 | 100、100、5 m/s |
| 颗数、颗间隔、组周期 | 4、0.05 s、0.55 s |
| 人形／出墨起手、末颗后回墨锁 | 0.10／0.20 s、0.65 s |
| 每颗伤害、每组耗墨、射击移速 | 30、8、2.8 m/s |
| 初速、重力、碰撞半径 | 14 m/s、18 m/s²、0.18 m |
| 地面次数、所有反射总次数 | 3、6；再次碰撞终止 |
| 地面法向／切向保速、墙面保速 | 0.72／0.90、0.90 |
| 寿命、累计路径长度 | 2.4 s、24 m，先到终止 |
| 接触墨迹、沿途墨迹半径 | 0.65～0.85 m、0.20～0.30 m |
| 滴墨间隔、向下探测 | 累计 0.6 m、1 m |

无随机散布、伤害衰减、穿透或爆风。忽略发射者和友方。朝上法线与竖直夹角 ≤45° 算地面；墙面、天花板算其他反射。沿用项目不规则墨迹笔刷。

## 配置和实现

- `Config/Luban/source/TbHero.xlsx` → `gen_luban.bat` → `tbhero.json`；新增 ID 7，其余行保留。
- `Assets/GameResource/Weapons/BubbleGirl/BubbleGirlWeaponConfig.asset`：玩法数值；`BubbleGirlAmmoConfig.asset`：网格、枪口与三类声音。
- `Assets/GameResource/Characters/BubbleGirl/BubbleGirlPresentation.asset`：独立表现；纸片资料位于 `Characters/Shared/Paper/BubbleGirl`。
- `BubbleGirlBuilder.Install` 可补建资源；已有武器／弹药数值不覆盖。`PrototypeBuilder.ConfigureAddressables` 注册角色、枪械、配置和依赖。
- `BubbleVolleySimulation` 使用已有快照中的组编号、剩余发数、下一颗时间，支持预测重放。组内四颗具有独立弹丸 ID／ActionId，ActionId 高位为共享组号。整组一次动作／后坐力，四次出泡反馈。
- `InkBubbleSimulation` 使用固定步长与球形扫掠，碰撞后继续处理当前步剩余时间，累计距离／寿命不重置，单步接触上限 8。伤害和涂墨只在服务器结算。
- `InkBounce` 同步弹丸身份、反射序号、时间、位置、新速度、法线、地面次数和累计路程。终止沿用 `InkImpact`，加入时间以对齐显示。晚加入补发当前分段；重复、乱序段和终止后旧事件被处理。
- 泡泡使用池化网格（主体上限 384、碎泡 96）和简单 URP 队伍色材质，无实时折射；显示时钟缓冲 100 ms，接触后压扁回弹。已有命中水花加四枚短寿命碎泡。音频由本地合成 WAV 经现有播放链路播放。
- 玩家协议 29、武器模拟签名 9（包含同时期其他武器改动）；与旧版本互联须重新构建双方内容。后续版本以源码常量为准。

## 验证与复现

专项测试：`Splatoon.Tests.BubbleWeaponTests`。Host 场景测试：`Splatoon.Tests.BubblePlayTests`（EditMode 测试入口进入实际 Boot Play Mode，带 GPU 截图）。测试与构建在独立目录 `D:/XPHUNITY/ProjectSplatoon_BubbleValidation` 执行，避免打断主编辑器和并行开发。

静态链检查：`python Tools/CombatGirls/validate_weapon_assets.py --hero 7`。

正式 Windows 构建入口：`Splatoon.Editor.PrototypeBuilder.BuildWindows`。Development Player 附带默认不启用的 `BubbleNetworkSmoke`：

```text
InkLan.exe -batchmode -bubbleRole host -bubblePort 18237 -bubbleJoinGate <gate-file> -bubbleOutput <host-result> -logFile <host-log>
InkLan.exe -batchmode -bubbleRole client -bubblePort 18238 -bubbleJoinGate <gate-file> -bubbleOutput <client-result> -logFile <client-log>
```

客户端使用已有 `Tools/CombatGirls/udp_test_proxy.py` 将 18238 转发到 18237，可注入 50 ms 单向延迟、10 ms 抖动和 1% 丢包。gate 文件必须是本次新路径，客户端等到 Host 已有在途泡泡才加入。驱动验证选择、实际射击、反射事件、晚加入恢复、死亡重生保留英雄和回合重置；结束输出载荷流量及帧时间。

验收证据和仍待完成的物理双机／设备检查见 [验收记录](Acceptance.md)。
