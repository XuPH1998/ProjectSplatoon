# 正式移植验证记录

日期：2026-09-11。Unity 6000.3.9f1、URP 17.3.0、Windows x64 Development / Mono。图形验证设备为 NVIDIA RTX 3060，D3D12，1280×720。

为保留已打开的编辑器会话，导入、提取、测试与构建在 `Temp/InkValidation` 独立副本执行。验证后的正式资源、渲染器、Layer、包锁文件、Addressables 注册和 Windows 包已回传当前工程；代码与五张配置 JSON 已逐文件确认一致。没有提交 Git，也没有修改参考工程。

## 已完成

| 类别 | 结果与证据 |
| --- | --- |
| 资源正式化 | 参考清单记录 100 个源文件；正式清单记录 141 个资源及 GUID/SHA-256。完整 Jammo 外观、Generic Avatar、方向控制器、跳跃/落地、四组 Rig、嵌套 Splattershot、独立枪口与粒子 Prefab 已落地。核心正式资源依赖检查通过，无 `_Incoming` 依赖。 |
| Excel → Luban → 运行时 | 五张源表重新运行 `gen_luban.bat` 成功；生成内容与已测 Windows 包一致。旧表及过期生成 C#/JSON/meta 已移除。旧 20 项字段去向见 `ConfigMigration.md`。 |
| 编译及构建 | `Logs/ink-build-final.log`：Addressables 和 Windows 构建成功、退出码 0。构建入口为 `PrototypeBuilder.BuildWindows`，不调用资源安装/灰盒重建。 |
| EditMode | `Logs/ink-editmode-final.xml`：38/38 通过。覆盖原有规则/房间码/技能、强类型配置默认值与无效引用、弹道公式、30/60/144 Hz 调用下的最近薄墙扫掠、单次碰撞、枪口处于碰撞体内、寿命/清理、耗墨、归属笔刷、65536 格快照及异常数据。 |
| GPU 表面绘制 | `Logs/ink-gpu-final.log`：13 个表面逐个写入，检查其他面 UV 岛没有串色、清空无残留、RGBA 恢复逐字节相同。释放后登记 RT 字节数为 0。掩体、四侧墙和地面均有实际非透明像素。 |
| 实机图形启动 | `Logs/ink-final-host.log` / `ink-final-client.log`：正式角色、枪械、橙蓝表面墨迹及屏幕空间墨流正常显示，无最终版本渲染异常或纹理释放警告。截图保存在 `Builds/Windows/smoke-final-host.png` 和 `smoke-final-client.png`。 |
| 绘制中加入与增量 | 最终测试达到四个进程（两个图形、两个无图形），最终序号 1046；地面橙 1748、蓝 1283，网格哈希 3701361607。滚动检查点序号 847 后继续发送增量，晚加入客户端恢复到 1046。 |
| 晚加入纹理一致性 | 独立静止场景在序号 333 后加入：地面网格哈希 689732918；地面 RT 哈希 2136211845、东墙 RT 哈希 1468801925，房主与图形客户端完全相同。证据为 `ink-painted-host.log` / `ink-late-client.log` 的 `[SMOKE-RT]`。最终版本再次完成晚加入与退出重进，证据 `ink-final-late.log`。 |
| 四人回合 | `ink-combat-host.log` 与三个 `ink-combat-client*.log`：四人房主模拟、击倒和重生、潜墨/回墨、完整 180 秒结算、5 秒后再次开局。结算橙 2082、蓝 1080，各端哈希 1769535523；新回合归属为零，哈希恢复 3122214213。该测试在最终表现颜色/释放修正前完成，弹道与回合实现相同。 |
| 房主退出 | 最终图形客户端显示“房主已关闭房间，请重新创建或加入房间”，返回主菜单。 |
| 版本与容量 | 在回传的 `Builds/Windows/InkLan.exe` 上验证：旧灰盒版本被拒绝并显示“游戏内容不一致，请双方使用同一构建包”；第五人被拒绝并显示“房间已满（最多 4 人）”。证据 `ink-old-client.log`、`ink-capacity3.log`、`ink-gates-host.log`。 |

四人完整回合采用一个图形房主和三个无图形客户端，最终表面测试采用两个图形进程和两个无图形客户端。无图形进程只证明模拟及协议行为，不能证明材质或墙面纹理显示。

## 性能观察与验收边界

图形日志多数 2 秒采样约 120 FPS；涂色常驻 RT 登记 37 MiB。当前 13 个表面的检查点纹理复制最多另需约 16 MiB，即涂色 RT 峰值设计预算约 53 MiB，低于配置的 128 MiB。RenderGraph 的相机临时目标另由 URP 管理。GPU 单表生命周期验证释放后为 0，多次退出重进后常驻登记值恢复为 37 MiB。

这不是严格的 60 FPS 性能验收：尚未采集 Unity Profiler 分配、逐帧 P95/P99、网络积压曲线及长时间多轮资源趋势。测试截图使用显式 URP 渲染和同步 PNG 写盘，早期运行曾产生约 13.6 FPS 的采样低点及一次接收队列提示，不能把截图停顿算作正常玩法稳态，也不能据平滑 FPS 声称没有其他尖峰。

仍需人工/额外设备验收：

- 两台实体机器的有线/无线局域网与防火墙场景；四个图形玩家持续射击的长时间性能。
- 参考工程与本工程同视角录屏对比，逐项核验八方向、斜向、俯仰、跳跃/落地、双手 IK、滑步、潜墨和死亡重生的美术品质。当前已做正式包截图检查，未将参考工程运行画面逐帧对齐。
- 注入丢包/乱序/损坏后的在线块重传，以及结算期间新加入、复杂棱角与 UV 接缝的交互遍历。当前有完整性与非法数据自动测试，但没有进行网络故障注入。

当前绘制面约束针对既有静态凸面竞技场，不包含任意凹面、动态可涂模型或爬墙。Windows 图形房主负责纹理检查点，无图形房主不提供完整历史墙面材质恢复。

## 复现步骤

1. 日常调参：修改 `Config/Luban/source/TbCharacter.xlsx`、`TbWeapon.xlsx`、`TbRoomMode.xlsx`、`TbArena.xlsx` 或 `TbGlobal.xlsx`，运行 `cmd /c Config\Luban\gen_luban.bat`。
2. 正常运行：Unity 打开 `Assets/Scenes/Main/Boot.unity`，Play；或启动 `Builds/Windows/InkLan.exe`，房主创建房间，其他玩家用房间码加入。
3. 正常打包：菜单“喷墨对战/构建/Windows 正式资源版本”；命令行可使用 `-batchmode -nographics -quit -executeMethod Splatoon.Editor.PrototypeBuilder.BuildWindows`。无需再次导入参考项目。
4. GPU 检查：在独立验证工程用 `-batchmode -executeMethod Splatoon.Editor.InkGraphicsValidation.Run`，不要传 `-nographics`。检查会打开正式竞技场但不保存场景，输出 `Logs/InkGraphics/mask-*.png`。
5. 自动联机驱动：Development 包加 `-lanSmokeHost` 或 `-lanSmokeClient`、`-lanPort 7795`；`-inkSmokeCase surfaces` 测试地面/墙面喷涂，`observer` 只观察；不指定则执行完整战斗回合。可用 `-lanLeaveAfter 30 -lanCycles 1` 测试退出重进。图形截图用 `-inkLabel 自定义英文标签` 区分。
6. 只有需要重新提取参考场景时，才运行 `Tools/InkMigration/import_reference.py` 后使用“安装正式喷墨资源”。导入脚本保留已有正式资源适配；安装工具会重建派生 Prefab/动画/绘制 UV，因此不能替代日常构建。

最终可执行文件 SHA-256：`c7e054edf3cd28b374e29139288d6d3cb0077a75f677ebd0dea5ebdd91c53607`。开发日志与构建产物在 Git 忽略目录中，源码和两份资源清单位于工程内。
