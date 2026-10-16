# 积木地形 Player 性能基准

2026-10-15首版交付使用显式 `-blockAcceptanceProfile prototype-v1`；工具传 `--acceptance-profile prototype-v1`。最新范围以 [高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 为准，当前必须运行四场。32米高塔、两万格级联及两场专属最终碰撞和性能整组标记 `user-deferred-not-accepted`；普通重力、守恒、付款、真实碰撞及32米玩法顶界保留。其余仍保持1080p、八人、完整积木规模和帧时分位，稳态180秒、普通发布P95≤200ms、八爆全部完成≤2000ms；偶发尖峰作为诊断风险。省略参数默认保留下文严格档。`--diagnostic-seconds`仍只是诊断，不能作为首版正式通过。

此驱动只在 Editor / Development Build 中、显式传入 `-blockBenchmark <case>` 时创建。正式游戏不加载基准。Editor 结果只能用于诊断，Player 门槛会明确失败。

| Case | 首版 `prototype-v1` | 后续 `strict` | 实际负载 |
| --- | ---: | ---: | --- |
| `steady250k` | 180 秒 | 600 秒 | 至少 25 万存活格，八个真实网络玩家持续射击 |
| `combat250k` | 300 秒 | 300 秒 | 至少 25 万存活格，八个真实网络玩家持续射击 |
| `tower32m` | 用户后移，本次不验收 | 60 秒 | 100 列到达全局 32 米顶界，拆除全部底格，跟踪失支撑和落地 |
| `blast8` | 120 秒 | 120 秒 | 至少 25 万存活格，同一帧排入八次完整半径爆破 |
| `cascade20k` | 用户后移，本次不验收 | 120 秒 | 100 列，每列底格上方 200 格；拆除 100 底格，累计验证 2 万格下落及完成 |
| `scattered100k` | 180 秒 | 180 秒 | 地面棋盘分布的 10 万格，保留真实碰撞和渲染 |

25 万格场景预置 27.5 万格，留出实际交火消耗余量。完整 workload 门槛按整个采样期间的**最低**存活数量判定为至少 25 万格，不能用初始或峰值数量替代。落块仍计入存活数量；坍塌量按新列段中实际格数累计，同一列段只计一次，不能以峰值同时在空中的格数替代。每个场景独立判定；一个场景通过不表示整个项目验收完成。

## 运行

使用当前构建的同源 Windows Development Player，Practice 阶段、八个真实独立 Player（一个 Host、七个客户端）。图形 Host 在非活动隔离桌面上显示普通窗口，正常主相机和后缓冲必须持续 1920×1080；七个 `-batchmode -nographics` 客户端及其它 helper 隐藏运行。驱动没有第二台相机、离屏 RenderTexture 或人为重绘来替代真实画面；不切用户桌面，不在用户前台直接启动这些进程。

核心功能硬门通过、同源候选正式构建并完成包级smoke后，从项目目录使用已准备的隔离请求串行运行四场。实际候选包、源码代次、结果和当前入口统一见 [首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)。下文Windows21 V2的 [完整比赛请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-package-smoke-request.json)、[联机请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-network-request.json)和[D3D12性能请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json)为历史方法示例；执行前必须绑定实际新包和最新四场范围，本节不表示候选包或性能已通过：

```powershell
python Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Candidates/launch-isolated-package21v2.py --request Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json --execute
```

该历史请求的 `harnessArguments` 显式包含 `--acceptance-profile prototype-v1` 与 `--graphics-api d3d12`，使用 `CodexBlockTests20261013a` 隔离桌面，并由入口为测试子进程设置 `SPLATOON_DISABLE_SYSTEM_CURSOR=1`。旧请求和结论保留各自包身份及验收工具哈希。选择D3D12用于获得有效GPU计时，80%计时覆盖率及全部通过门槛保持原样；实际API与覆盖率仍由新包每场报告确认，旧包测量不能替代。省略 `--execute` 仅准备请求，不启动Player；不要向wrapper追加它不接受的profile或graphics-api参数，也不要绕过wrapper在前台直接执行harness。当前首版范围为四场，共780秒正式采样；播种、同步、预热及启动时间另计。若候选包路径改变，先核对请求中的 `--player`、包身份和四场集合再执行，避免执行冻结的旧六场请求。

Harness 只对隔离桌面内的图形 Host 设置 `SW_SHOWNORMAL`，不传 `-batchmode` 或 `-nographics`；无图形客户端保留 `SW_HIDE`。启动记录包含每个进程的窗口模式与完整参数。启动本身不能代表通过，实际主相机渲染、GPU/线程覆盖率和全部性能门槛仍按最终报告判定。Unity 编译、回归、构建和 Player 验收串行调度。

诊断 GPU 计时覆盖率时，可加 harness 参数 `--frame-timing-observation`。它只给 Host 启用有界原始时序观测，另写 `host.performance.json.frame-timing-observation.json`；正式采样、统计及门槛不变，harness 明确把这次运行标为仅诊断，不能计为性能验收通过。完整验收时关闭该参数。

Host 参数示例（由 harness 构造，不用于用户桌面上的直接启动）：

```text
-lanSmokeHost -inkSmokeCase observer -blockAcceptanceProfile prototype-v1 -blockBenchmark steady250k -blockBenchmarkOutput Reports/BlockTerrain/Performance/steady-host.json -blockBenchmarkQuit
```

另外七个独立客户端使用相同 LAN 地址/端口，并传入 `-blockBenchmarkInput combat`；静态、爆破和坍塌场景使用 `-blockBenchmarkInput idle`。单独的 input 参数不创建性能探针，也不生成客户端性能结果。Host 仍渲染全部八个真实角色。这种配置的报告只证明被采样 Host 的本机八人场景性能，不证明客户端设备性能或物理多机 LAN。

若要测某个客户端，也为该进程显式传入相同 `-blockBenchmark <case>` 和独立 output 路径，保留真实图形输出。Host 负责播种和触发结构变更；客户端的 Host 专属动作门槛标为 `unverified`，必须与对应 Host 报告一起验收。

可选参数：

| 参数 | 默认 | 作用 |
| --- | ---: | --- |
| `-blockAcceptanceProfile` | `strict` | 首版必须显式指定 `prototype-v1`；决定时长及发布、爆破和诊断门槛 |
| `-blockBenchmarkDuration` | 所选档上表完整时长 | 低于该档完整时长仅诊断；完整 duration gate 必定失败 |
| `-blockBenchmarkWarmup` | 10 秒 | 真实八人、场景全部发布后预热 |
| `-blockBenchmarkTimeout` | 300 秒 | 等待八人/初始同步/播种完成的超时 |
| `-blockBenchmarkBlastRadius` | 2 米 | 敌方完整半径；己方半径 0.5 倍；低于 2 米不能通过此压力场景 |
| `-blockBenchmarkOutput` | 独立进程 JSON 路径 | 报告路径 |
| `-blockBenchmarkQuit` | 关闭 | 完成后退出；完整通过 exit 0，否则 exit 5 |

驱动先通过地图静态碰撞合作式播种，再用正常后台网格、BakeMesh 和固定模拟边界整体安装 fixture。播种、初始全量同步和预热不进入采样。若地图无法容纳完整负载、角色断线、离开 Practice、分辨率变化或主相机改用 RenderTexture，写出不完整报告。源数据、生成网格和碰撞不会用每格 GameObject / Rigidbody 建造。

## 报告与门槛

每 30 秒写 checkpoint；checkpoint 的 percentile 不计算，`passed=false`。最终报告保留完整样本数量和 P50/P95/P99/最大/均值，不截去长帧。退出或失败也写报告。

- 完整 Player 采样时长、真实八人、整个采样期间的完整 workload。
- 所有实际采样帧均有正常主相机渲染，1080p。GPU、主线程、渲染线程分别报告有效样本覆盖率，每项须至少覆盖 80% 采样帧；GPU 仅接受时间戳不同的正有限样本，线程仅接受有效、运行中的 recorder 两次采样之间新增的正有限样本。Recorder 缓冲满后清理其历史并继续采集，不重复使用旧值充当覆盖率。计时完全缺失为 `unverified`，稀疏采样不能通过。
- 两档均要求帧时间 P95≤15.5 ms，P99≤16.67 ms。首版将偶发长帧作为风险保留，`no33msSpikes` 和 harness 的 33.3 ms 全帧最大值检查不阻断首版；`strict` 仍保留原尖峰硬门。每个尖峰保存已完成帧编号、主/渲染线程、GC、Tick/Publish/Upload/FallingRender 的计时及可用性，并关联同一主相机帧的待处理数、失支撑列数、版本和队列年龄；无对应帧时队列可用性明确为 false。结合这些记录和 Profiler 确认归因，不删去长帧，也不将首版通过写成严格无尖峰通过。
- 真实普通主攻击命中的建造与直接敌块消除发布命令：首版 P95≤200 ms，`strict` P95≤80 ms，以攻击来源显式标记。辅助飞行轨迹、诊断探针、爆破、副武器和大招不进入此门槛；每秒单格补充探针仍进入全部地形命令统计。命令计时从实际命中入队到最后一次成功发布，包含排队、候选准备、后台网格/烘焙和碰撞发布。
- 八次爆破必须在同一帧排入、八次均实际拆格，完整目标集合全部完成：首版≤2000 ms，`strict`≤250 ms。不通过强度上限或范围截断缩短负载。
- 后续重新验收塔/级联时，保留原完整规模：拆除全部100底格、失支撑分离及下落守恒、最终落定与真实碰撞；记录实际落地完成时间，级联累计下落不少于2万格。失支撑列扫描及对应分离事务发布≤500 ms仍为strict硬门。这两场及专属最终核验本次整体后移，不记Passed；普通功能和联机中的永久悬空、未落定或卡死仍须修复。
- 错误和失败事务必须为零。GC 只记录总量、每帧分布和峰值，没有人为新增 GC 通过阈值。

报告同时记录验收档、画质档、vSync、目标帧率、CPU/核心数、内存/显存、图形 API、系统、屏幕和显示分辨率。包和程序集身份由启动 harness 记录。首版报告保留 `prototypeRisks`，严格档和历史失败保持原结论；缺测、诊断短跑、Editor 结果或仅构建成功不能替代当前包的正式通过。

性能报告与同最终源码的核心正确性证据共同验收：八爆的压力完成时间结合完整目标、敌友半径、静态遮挡、逐格守恒、原生碰撞及付费回调回归，不能只凭“八次回调且有删除”单独证明完整几何。普通发布分位统计已完成且实际改变地形的主攻击；同时记录窗口开始/结束的入队及完成计数、剩余命令和队头年龄趋势，避免把采样边界解释成所有请求均已完成。持续射击结束时少量正常排队不直接判失败；真实长期积压仍按原正确性要求检查。

历史六场范围可用 [边界诊断脚本](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/PartialLate86Final36Preparation01/diagnose-performance-boundary21-v2.candidate.py) 的 `--analyze --case-json <六个实际case.json路径>` 汇总数据；当前四场不得虚构后移两场结果来满足旧接口。该诊断保留原通过标记，把期末全部命令欠账保守视作普通慢样本，另给出P95上界的可证性；“不可证”“无数据”及边界pending不新增失败门槛。默认 `--describe` 不读取结果，不启动Player。诊断不能替代当前包正式通过。

网络验收是独立硬门，使用同一包及 `prototype-v1` 隔离网络请求：2/8 Player 各测 0ms/0%、100ms/1%、200ms/3%，共六组；100ms/1% 纠正 P95≤0.15 米、P99≤0.60 米，迟加入、重连、清场重建及积木一致性仍须通过。详见 [玩法与交付说明](BlockGameplay.md)。真实多机和人工操作观感是两项外部验收，按用户要求不计入当前 Goal；同机性能结论仅适用于当前电脑，其它目标设备也需另行验证。
