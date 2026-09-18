# 七武器涂地校准实施记录

日期：2026-09-17。基准：喷射战士 3 的 11.3.0 参数、无装备技能、空间比例 `18 / 24.037 = 0.7488455298`。本轮是**测量修正、证据审计和一项已核实参数修正**；七武器的原作涂地效果均尚未达到可判定验收条件。

## 实际改动

- 修复直接发射夹具中 `ShotSequence` 和 `FireBurstSequence` 使用相同序号、使哈希项抵消的重复种子问题。保留可复现旧缺陷的回归用例。正式覆盖数据使用真实输入驱动武器状态机，再走正式 `InkProjectileService.Spawn`，包含双枪轮换枪口、泡泡四连组和消防栓蓄力。
- 新增 `referenceFootDepth`，默认 1；泡泡枪为原始 `NearestParam.DrawSizeCollisionPaintParam.PaintDepthScale=1.2`。配置进入中文 Inspector、校验、不可变快照、热更新、内容签名。爆炸泼桶继续使用独立的 `explosherFootDepth=1.4`。
- 同一权威墨迹事件携带纵深，CPU 归属和 GPU 显示共同消费；未另建视觉专用涂地逻辑。
- 武器模拟版本由 11 提升至 12。玩家协议 31、涂墨协议 9 不变，网络结构未变。
- `align_reference_weapons.py` 改为审阅差异导入入口，不再重放旧近似值、改动战斗参数或遗漏爆炸泼桶。`paint_parity.py --apply` 当前只应用上述一项修正，并先核对七份原始文件哈希；重复执行不改变结果。
- 正式构建入口增加指定输出目录的重载，用于绕开其他运行中 Player 的文件锁。日常菜单仍使用原输出目录。

本轮未调整伤害、射速、耗墨、角色移动、攻击模式、弹道、图集或英雄源表。修改前已有的爆炸泼桶与准星工作完整保留。图集名义包围尺寸与归属面积不同，不据此推导“应该统一放大”。

## 冻结、来源与证据边界

冻结位于 `Reports/PaintParity1130/Baseline/`：797 个文件的字节副本和 SHA256、初始 Git 状态、已暂存与未暂存补丁、提交 `6e3f01223cf136ea51aefcddd0397021990d590b`。实际修改前 Editor Host 的房间签名记录于 `room-signature.txt`。旧覆盖基线、图像与归档报告保留。

留档偏差：首次调用已有资产校验脚本时，其默认输出覆盖了 `Reports/WeaponAssets/static-validation.json` 这一共享状态文件；未能恢复它的运行前字节。已新增 `--output`，后续校验写入本次目录。旧覆盖基线没有被该操作改动，此偏差不隐藏为“全部旧报告均未覆盖”。

七份原始 JSON 固定在 [Leanny/splat3 提交 7280ff9c](https://github.com/Leanny/splat3/tree/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon)，在线下载结果全部与本地固定 SHA256 相同。逐字段原始值、旧映射、当前值、换算和缺口见 `Reports/PaintParity1130/evidence.json`、`sources-online.json`，审阅修正位于 `Tools/ValidationData/PaintParity1130/VerifiedOverrides.json`。

泡泡枪明确值见 [WeaponSlosherBathtub 原始参数](https://github.com/Leanny/splat3/blob/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon/WeaponSlosherBathtub.game__GameParameterTable.json)。单位：长度乘空间比例；帧除以 60；每帧速度乘 `60 × 空间比例`；纵深比例为无量纲，直接采用 1.2。

社区 [主武器参数说明](https://wikiwiki.jp/splatoon3mix/検証/パラメータ情報/メイン) 仅辅助解释术语，不视为固定版本算法源码。XarrotD 的参数说明页直接访问受限；搜索摘要不承担实现依据。没有找到同时能确认版本、武器、技能、输入条件、尺度及时间精度的原作采样集，合格原作样本数为 0。未使用普通录像或本项目截图替代原作验收。

旧映射中的 `source-explicit` 只表示某个数值可追溯，不表示对应算法已核实。未出现的原作字段不会解释为零；项目默认值和历史近似值均不能升级为原作默认值证据。

## 七武器状态与待核实机制

| 武器／项目 ID | 参数已核实 | 项目行为已验证 | 原作效果已对照 | 本轮处理与主要缺口 |
|---|---|---|---|---|
| 斯普拉射击枪／1 | 部分，明确字段可追溯 | 本次矩阵已验证 | 否，待样本 | 保持配置；`SplitNum=8`、小数墨滴预算、最近处补墨及前三发调度语义未完整核实 |
| 窄域标记枪／4 | 部分 | 本次矩阵已验证 | 否，待样本 | 保持配置；零角散布不意味着固定落墨位置，沿途墨周期和最近处规则待核实 |
| 开尔文525／2 | 部分，仅普通射击 | 本次矩阵已验证 | 否，待样本 | 保持配置；左右枪口重叠、每枪脚下墨与墨滴分配待核实，不引入翻滚后参数 |
| 消防栓／6 | 部分 | 本次矩阵已验证 | 否，待样本 | 保持配置；最短／第一圈／满蓄已测，阶段分布和脚下调度待原作证据 |
| 普通快速爆破枪／5 | 部分 | 本次矩阵已验证 | 否，待样本 | 保持配置；沿途、实体碰撞与定时空爆已有分支，原作墨区及遮挡边界待对照 |
| 满溢泡泡枪／7 | 部分；脚下纵深 1.2 已核实 | 本次矩阵及热更新已验证 | 否，待样本 | 脚下纵深修正；首颗／后续颗、首跳／后跳和 after-paint 调度未强行推导 |
| 爆炸泼桶／3 | 部分 | 本次矩阵已验证 | 否，待样本 | 保留现有改造；落差纵深、子墨滴轨迹、墙滴分阶段等机制仍待证据 |

“项目行为已验证”仅指下列内部测试矩阵，不包括每个缺失原作机制，也不包括物理双机与目标设备验收。

## 测量条件与交付

具体面积、墨迹成本与真实潜游数据见 [测量摘要](Measurements.md)。

修改前后各 3,300 份样本，共 6,600 份；每组固定 30 个种子，110 组／阶段。主矩阵各 3,270 份，加爆炸泼桶横移速度继承补充组各 30 份。该补充基线在修改后追加，但爆炸泼桶配置和算法与冻结基线相同，前后哈希完全相同。

场景包括平地、上仰／下俯 30°、高台落差、斜坡、近／中／远墙、薄墙遮挡、前三动作、射击 1 秒／3 秒、匀速横移、匀速扫射、整箱墨；消防栓另测最短蓄力、第一圈，以及已蓄力横移／扫射。普通横移以射击移速连续移动枪口；爆炸泼桶另有设置真实 `PlanarVelocity` 的继承组。

泡泡以完整四连组计动作。消防栓单动作与前三动作以完整蓄力弹仓计；1／3 秒窗口从开始蓄力计时，因此满蓄计划的 1 秒窗口没有发射是预期结果。

- `statistics.json/csv`：面积、宽度、纵深、前向边界、连通距离、中心线最大空白、首墨时间、墨迹数、有效载荷字节、实际耗墨、慢充补墨、测量耗时的均值、总体标准差、最小／最大值。JSON 同时给出最差样本的路径和种子。
- `Before/wN/场景/seed-XX.json` 与 `After/...`：完整单样本指标。每组 seed 0 有轨迹 CSV、实际涂墨事件 CSV 和归属 PNG。
- `coverage-flat.png`、`coverage-three.png`、`coverage-three-seconds.png`：七武器前后图，等物理比例展示，粉色为归属网格，灰色为空白；**不是原作对照图**。
- `comparison.json`：六把未改武器的全部归属哈希相同；泡泡枪 450 个配对样本的归属哈希发生变化。所有 3,300 对的弹丸数、实际耗墨、资源生成、墨迹数量、序列化字节均不变。
- `atlas.csv`：32 款印章在半径 1、硬度 0.55、强度 1、实际阈值／噪声下的有效面积和尺寸。
- `Swim/wN.txt`：真实 CharacterController 消费测量地面墨迹后的潜游行程、友墨步数和回放差异。

归属网格为 0.125 米。`floorArea` 只计水平基底，`ownedArea` 包括本夹具登记的所有表面；斜坡登记为独立表面。四邻接连通以出生点 1.5 米范围内的墨格为起点，并非角色完整通行判定；潜游测试单独补充。首墨时间是内部 60 Hz 模拟提交墨迹的时刻，不能当作亚帧精确接触时刻。

1／3 秒数据同时保存“窗口结束时已落地面积”和“窗口内发射物最终全部落地并集”。固定动作场景可补给资源以完成动作。消防栓缺墨慢充会生成预留墨，其整仓资源组标记 `resource-cycle`，不能拿它当固定 100 墨效率；其余整箱组为 `finite-tank`。

32 款图集有效面积均值约 1.075 平方米，范围 0.718～1.691；宽度 1.328～1.703 米，纵深 1.156～1.633 米。这些值相对于本项目名义包围尺寸，**不代表原作面积缩减比例**，因此本轮不调整图集或统一放大半径。

## 回归与运行验证

| 层级 | 本轮结果 | 证据 |
|---|---|---|
| 七资产／源表边界 | 通过，923 项历史／审阅值比较，英雄表与生成表相符 | `static-validation.json`、`scope-diff.json` |
| 导入器 | 2 个测试通过；只改已审阅字段、重复执行等价、源哈希损坏阻止写入 | `Tools/CombatGirls/test_paint_parity.py` |
| Editor 选定回归 | 115 个不同用例最终通过；最初 114 通过、1 个夹具错误，修正后专项重跑通过 | `regression.xml`、`playmode-retry.xml`、`postbuild-domain-reload.xml`、`test-summary.json` |
| 全矩阵完整性 | 前后各 3,300 份，220 个完整种子组 | `compile-refresh.xml`、`statistics.json`、`comparison.json` |
| 真正 Play Mode | 调试 Host 热更新成功；在途泡泡仍为 1.2，新弹为 1.5；实际审批回调拒绝冻结旧内容签名 | `playmode.txt` |
| 正式构建 | 成功，Unity 6000.3.9f1，Windows Development Player，正式 Addressables 流程 | `build-result.txt`、`Builds/PaintParity1130/InkLan.exe` |
| 独立 Host＋Client | 通过，同机两进程，全部七武器均有实际发射 | `Network/run2/`、`network-comparison.json` |
| 物理双机／目标设备 | 未执行 | 待实机 |
| 原作效果 | 七把均无法判定 | 合格原作样本为 0 |

联网最终归属哈希 **1154333684**，涂墨序列 **5077**，两端一致。Client 应用两次快照（迟加入和主动补同步）；双方死亡后重生保留英雄7，回合切换清空成功，报告未捕获运行错误。最终内容签名两端均为 `668e0610094309415cb00729f51563c678d781fd7f4c8a79ac7cce8f0e1f516f`，不同于冻结旧签名 `5935cabf9eb42ba5e2007690ea0fd94b42cc4e26919a90a1131e9c67f687125f`。

按项目 ID 1～7，Host 发射数为 `129,45,8,78,12,54,48`，Client 为 `87,45,8,77,12,55,48`。首次窗口与迟加入、网络时序不同，因此不要求两端各自发射数相同；验收比较的是权威涂墨序列和全场归属。Host 本次累计序列化 LivePaint 523,634 字节、SnapshotJournal 5,673 字节、SnapshotData 43,753 字节；这是类型计数器统计，不是 UDP 抓包线速，也不与失败首轮做吞吐优劣比较。

最终代码／配置内容清单为 `current-manifest.json`。构建会临时加入 Unity 管线预加载资产，本轮已恢复该自动变更，不将它纳入玩法修改。独立验收进程已正常退出。

采样最初超过 Unity 默认 180 秒超时，但全部输出已完成；原失败 XML 保留。采样超时现设为 900 秒，并通过独立完整性用例复核每阶段 3,300 个文件、每组 30 个不同种子。热更新夹具最初在进入 Play Mode 的域重载后丢失闭包状态，已隔离到进入后的场景协程；原失败和成功重跑记录均保留。

首次独立联网运行已经达成归属一致、补同步、重生和清空，但自动输入换枪后未留松开阶段，导致部分武器从未发射，整项正确标记失败。后续仅修正 CLI 测试输入，每阶段先松开一秒，再开始射击／蓄力；没有绕过游戏的换枪后松开门槛。首次失败日志保留于 `Network/`，修正后运行另存 `Network/run2/`。

CPU/GPU 一致性包含定向纵深、旋转表面和遮挡裁剪；敌墨覆盖、网络快照、切换和死亡使用正式消费者与既有针对性回归。30／60／144 Hz 一致性比较弹丸数、耗墨、归属哈希、首墨提交时间及墨迹字节，不证明实际渲染帧率。

同步测量耗时包含测试地面创建、物理／归属模拟等开销，seed 0 另有事件采集开销。统计保留该成本变化用于诊断；当前机器有其他 Editor／Player 负载，不把它解释为设备帧时或性能收益。

## 复现与后续验收

1. `python Tools/CombatGirls/paint_parity.py --verify-online` 审计来源；`--apply` 只应用已核实修正。运行 `python Tools/CombatGirls/validate_weapon_assets.py --output Reports/PaintParity1130` 和 `python Tools/CombatGirls/test_paint_parity.py`。
2. Unity 编辑器空闲且场景已保存时，在 `Temp/PaintParity1130/tests` 写两行：独立结果标签，以及以分号隔开的完整测试名称。定向类为 `PaintParityTests`、`PaintParityBehaviorTests`、`WeaponAlignmentCoverageTests`；Play Mode 用例为 `PaintParityPlayTests.PaintReloadFreezesInFlightShotsAndRejectsOldContent`。完整性复核为 `PaintParityTests.ValidateCompletedCaptures`。
3. `CaptureBefore`／`CaptureAfter` 是显式用例，已完成目录不可覆写。新一轮需先使用新根目录，并重新冻结配置／代码／内容签名；当前代码不能回头直接制造修改前数据。
4. 绘图 Python 需 matplotlib、Pillow 和 NumPy：`python Tools/CombatGirls/paint_parity.py --report`。本机验证使用 Codex 随附 Python 环境。
5. `Temp/PaintParity1130/build` 触发正式资源校验、Addressables 和 Windows Development Player 构建，输出 `Builds/PaintParity1130/InkLan.exe`。独立进程命令使用 `-paintParityRole host` 或 `client`、相同 `-paintParityPort`、各自的 `-paintParityOutput` 和 `-logFile` 路径。客户端延迟加入，然后主动请求真实补同步。

仍需合格原作样本逐武器验收：面积／宽度／纵深平均误差不超过 10%；近零值按空间采样误差判断；具备时间精度的首墨和关键事件不超过 1 原作帧；连通距离误差不超过原作值 10% 与两个项目网格单元（0.25 米）中的较大者。样本误差超容差时保持无法判定。未知的 `SplitNum`、小数预算、最近补墨、落差与墙滴算法继续保留现有行为。

物理双机、目标设备性能，以及七把武器的“原作效果已对照”均保持待验收。
