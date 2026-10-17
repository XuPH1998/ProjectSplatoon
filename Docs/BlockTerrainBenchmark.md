# 积木地形 Player 性能基准

2026-10-17新增 `prototype-v2-45fps`：Player显式传 `-blockAcceptanceProfile prototype-v2-45fps`，工具传 `--acceptance-profile prototype-v2-45fps`。44阶段已应用九文件定向生产修复、精确目标新测试及historical lazy clone；原8 sources／20 effective files组合编译零error／warning。lazy前source9的原四回归通过，原组合T1／T3均失败，各3次budgetDeferred、0served、0attributedPending，保留原结论。后续Tests44DeferredDiagnostic02的T1诊断重跑确证1次deferred changed progress实际发布（deferredProgress1／NoChange0／Published1，phase0→2、proof false→true、mutation0→1、checks538／prefix41）却未计目标服务；不推断其余2次deferred，也不回填原T1／T3路径。root已应用最小partial-progress修复＋仅repair24前缀补齐＋移除临时诊断三文件；此前FinalCandidateComposition44PartialPrefix01为10 sources／20 files，compile44-partial-prefix01完成、error／warning均0、compilationFailed=false，取得时点20源SHA／字节全匹配。修复后原T1／T3均真实Passed，两者served／attributedPending／published均1、unrelatedPublished均0；Full400原执行仍失败（旧公共Tick断言≤4096、实得8160）。现行无命令8192策略早于lazy；仅对齐该Tick合同的单文件测试修改已实际应用，运行时预算不改。此前FinalCandidateComposition44PolicyContract01为11 sources／21 files，compile44-policy-contract01完成、零error／warning，21源取得时点全匹配；该阶段首次idle读取超时保留，第二次于UTC11:40:05.2509507六状态false，runtime MVID保持、当时tests MVID为d2417278-33d0-41c0-9d13-730da6d3ba73。Tests44PolicyContract01合同对齐后Full400已Passed1/1，BlockHistoricalRecoveryTests整组14/14 Passed（failed／skipped／inconclusive均0）；原4096／8160失败保持原归属。原PolicyContract取消class实际1通过／1失败：Clear通过，AuthorityLoss在取消前line120因callbacks=1,0失败，权限取消路径尚未触发，不归类为生产取消缺陷；root现已应用仅测试的取消观察窗口修正，保留原提前完成断言，生产调度／预算不改；此前CancellationObservation为12 source stages／21 files，取消2/2 Passed；原Frame类2通过／1失败、line125的4192 DependentSpend期望True实False，runner32986退出1保持。仅加诊断的stage13单叶真实Failed1/1，frame9→9、checks4000→4000，terrainMs1024.5534→1024.6008已超1000ms、commandPresent=false，runner8002退出1，确证该次有限测试时钟干扰整数额度，并非生产checks额度拒绝；不回判原失败。root仅将UseIntegerIsolationPolicy的1000改为double.MaxValue，真实3ms独立测试、数字oracle及生产代码／预算保留；当前FinalCandidateComposition44FrameIntegerIsolation01为14 source stages／21 files，compile44-frame-integer-isolation01完成、errors／warnings均0，21源时点绑定全匹配；新idle01的HTTP400读取失败保留；本轮指定8叶已实际8/8 Passed（Frame3／Motion2／Fair3），runner57488退出0，两轮原Frame失败保持各自测试身份。root已完成4083文件源冻结（SHA256 `928ce0a4447ef811e2c2b0ec41a040aed87ee46d74d212b2308e61cab7bec310`），14/21组合与freeze审计21/21通过；44正式生产及Addressables构建已成功，4083冻结输入／359包文件完整审计和44项Shader审计通过，仅审核的link.xml／meta二件已归档，19项偏好恢复通过。Final44现已执行结束：240 low-load＋300 sustained共540/540完成，latencyTargetsPassed=true，但sustainedDrained=false、functionalPassed=false；sustainedAllCallbacksFull／cleanupJoined均true。launcher67985退出1，bootstrap37772／harness／Player退出2，正式reader的21项完整性全通过，但startupProbeAccepted=false、唯一失败谓词为sustainedDrained；八张原PNG已逐张看见cyan源列，最终4083源／359包身份零变化通过，均不改判功能失败。剩余1组startup未通过、4场正式性能未跑、6组网络未跑；43包不覆盖44。43冻结阶段采用 `WeaponSimulationVersion=38`，针对reason18新增独立重力处理机会；活跃fair不介入，reason8未改。已补journal-local成功准入归属、取消边界测试及96-cohort夹具，并应用FairPaidExact夹具；最新复编译success=true、error／warning均0、compilationFailed=false，尚未验收。四场780秒、1080p正常主相机、八个独立Player和25万／10万格负载保持；帧时P95≤22.2ms、P99≤33.3ms、普通发布P95≤200ms、八爆全部完成≤2000ms仍是正式门槛。各阶段原始证据与范围见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)。

42包正式四场已完整采样780.0280202秒，帧时P95/P99均通过45fps档，但仅blast8、scattered100k通过，整体性能失败：steady／combat普通发布P95分别为130583.333／248733.333ms，combat八人持续射击覆盖也未达标。steady40→42存在严重排队／重试回归；42网络未跑，43早期自然单叶已由失败转为通过，未证明墙钟更快；收紧前Core01终态69叶66通过／3失败，旧失败raw详情缺口保留。Reviewed01原14叶计划未闭合，首个Fair叶实测失败（全局完成增量预期8、实得12）；FairPaidExact后Reviewed02最终针对性7/7通过，不代表最终源码完整69叶全通过；七叶末尾idle已确认；43正式构建成功，4081原输入未变、359文件完整包审核及44项Shader审核通过。Final43共540/540完成、时延目标通过，但Quiet排空仍使功能探针失败（退出2）；42→43持续入队→激活P95为83.2745→92.2492ms，发布→激活58.3501→66.7444ms，不证明43更快。43同包正式四场已完整采样780.0180534秒，2/4通过、整体性能失败；steady／combat普通发布P95为167500.000／262983.333ms，peakPending为20174／33447，均只有ordinaryPublishLatency阻断失败。四场帧P95/P99依次为14.864/20.155、17.362/25.495、9.740/12.703、10.307/13.269ms，均符合45fps；blast8的8/8爆破408.6826ms完成，scattered100k通过。performance reader完整性／进程隔离／19项Standalone恢复通过，28个client退出1及两个失败host退出5保留。root实际查看四张热身PNG，中文HUD／terrain可见，不证明图像时序或最终计数。43最终4081源／359包身份零变化通过；startup reader的21项完整性通过，但startupProbeAccepted=false。43六组网络未跑，整个Editor／环境恢复仍须独立收尾。41→42隔离探针的时延改善和源块显示通过不改变Quiet排空失败，历史gameplay40性能2/4、网络0/6通过保持原包归属。旧24次Editor与41→42独立Player探针均不能代替43正式压力或网络验收，不能将帧时通过写成整体性能通过。

历史 `strict` 和 `prototype-v1` 档保留原语义；省略profile仍默认 `strict`。此前 [高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 中的 `tower32m`、`cascade20k` 及两场专属最终碰撞和性能仍为 `user-deferred-not-accepted`，不能参与新档通过判定。普通重力、守恒、付款及真实碰撞仍须通过。`--diagnostic-seconds`仍只是诊断，不能作为正式通过，旧包成绩也不覆盖本轮新源码。

此驱动只在 Editor / Development Build 中、显式传入 `-blockBenchmark <case>` 时创建。正式游戏不加载基准。Editor 结果只能用于诊断，Player 门槛会明确失败。

| Case | 当前 `prototype-v2-45fps` | 历史 `prototype-v1` 基础工具 | `strict` | 实际负载 |
| --- | ---: | ---: | ---: | --- |
| `steady250k` | 180 秒 | 180 秒 | 600 秒 | 至少 25 万存活格，八个真实网络玩家持续射击 |
| `combat250k` | 300 秒 | 300 秒 | 300 秒 | 至少 25 万存活格，八个真实网络玩家持续射击 |
| `tower32m` | 后移，不接受此case | 60 秒 | 60 秒 | 历史32米高塔fixture，当前8米顶界不将它改写为缩小规模通过 |
| `blast8` | 120 秒 | 120 秒 | 120 秒 | 至少 25 万存活格，同一帧排入八次完整半径爆破 |
| `cascade20k` | 后移，不接受此case | 120 秒 | 120 秒 | 历史100列、每列底格上方200格；累计验证2万格下落，不缩小规模冒充通过 |
| `scattered100k` | 180 秒 | 180 秒 | 180 秒 | 地面棋盘分布的 10 万格，保留真实碰撞和渲染 |

新档原生要求四场共780秒；历史 `prototype-v1` 在基础工具中继续是六场共960秒，2026-10-15批准的历史四场范围由相应wrapper处理，不修改其既有工具含义。`strict`六场共1380秒。播种、初始同步、预热和进程启动均不计入这些正式采样时长。

| 验收项 | 当前 `prototype-v2-45fps` | 历史 `prototype-v1` | `strict` |
|---|---:|---:|---:|
| 帧时P95／P99 | 22.2／33.3 ms | 15.5／16.67 ms | 15.5／16.67 ms |
| 普通发布P95 | 200 ms | 200 ms | 80 ms |
| 八爆完整拆除 | 2000 ms | 2000 ms | 250 ms |
| 全帧尖峰与极限支撑队列 | 保留全部样本作诊断，P99门槛仍阻断 | 保留全部样本作诊断 | 保留原尖峰与500ms支撑队列硬门 |

25 万格场景预置 27.5 万格，留出实际交火消耗余量。完整 workload 门槛按整个采样期间的**最低**存活数量判定为至少 25 万格，不能用初始或峰值数量替代。落块仍计入存活数量；坍塌量按新列段中实际格数累计，同一列段只计一次，不能以峰值同时在空中的格数替代。每个场景独立判定；一个场景通过不表示整个项目验收完成。

## 运行

使用当前构建的同源 Windows Development Player，Practice 阶段、八个真实独立 Player（一个 Host、七个客户端）。图形 Host 在非活动隔离桌面上显示普通窗口，正常主相机和后缓冲必须持续 1920×1080；七个 `-batchmode -nographics` 客户端及其它 helper 隐藏运行。驱动没有第二台相机、离屏 RenderTexture 或人为重绘来替代真实画面；不切用户桌面，不在用户前台直接启动这些进程。

核心功能硬门通过、同源候选正式构建并完成包级smoke后，从项目目录使用已准备的隔离请求串行运行四场。实际候选包、源码代次、结果和当前入口统一见 [首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)。下文Windows21 V2的 [完整比赛请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-package-smoke-request.json)、[联机请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-network-request.json)和[D3D12性能请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json)为历史方法示例；执行前必须绑定实际新包和最新四场范围，本节不表示候选包或性能已通过：

```powershell
python Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Candidates/launch-isolated-package21v2.py --request Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json --execute
```

该历史请求的 `harnessArguments` 包含 `--acceptance-profile prototype-v1` 与 `--graphics-api d3d12`，使用 `CodexBlockTests20261013a` 隔离桌面，并为测试子进程设置 `SPLATOON_DISABLE_SYSTEM_CURSOR=1`。旧请求和结论保留各自包身份及验收工具哈希，不能直接用于本轮新包。当前请求应在相应隔离入口内部绑定新包、新工具身份，并将harness参数设为 `--acceptance-profile prototype-v2-45fps --graphics-api d3d12`；新档自动选四场并校验780秒完整采样。不要向历史wrapper追加它不接受的参数，也不要绕过隔离入口在用户前台直接运行harness。

选择D3D12用于获得有效GPU计时，80%计时覆盖率及全部通过门槛保持原样；实际API与覆盖率仍由新包每场报告确认，旧包测量不能替代。上述历史入口省略 `--execute` 只准备请求，不启动Player。执行新请求前核对 `--player`、包身份、工具哈希、profile和四场集合，使用新运行目录，避免覆盖历史结果。

Harness 只对隔离桌面内的图形 Host 设置 `SW_SHOWNOACTIVATE(4)`，保留正常图形窗口且不激活，不传 `-batchmode` 或 `-nographics`；无图形客户端保留 `SW_HIDE`。启动记录包含每个进程的窗口模式与完整参数。启动本身不能代表通过，实际主相机渲染、GPU/线程覆盖率和全部性能门槛仍按最终报告判定。Unity 编译、回归、构建和 Player 验收串行调度。

诊断 GPU 计时覆盖率时，可加 harness 参数 `--frame-timing-observation`。它只给 Host 启用有界原始时序观测，另写 `host.performance.json.frame-timing-observation.json`；正式采样、统计及门槛不变，harness 明确把这次运行标为仅诊断，不能计为性能验收通过。完整验收时关闭该参数。

Host 参数示例（由 harness 构造，不用于用户桌面上的直接启动）：

```text
-lanSmokeHost -inkSmokeCase observer -blockAcceptanceProfile prototype-v2-45fps -blockBenchmark steady250k -blockBenchmarkOutput Reports/BlockTerrain/Performance/steady-host.json -blockBenchmarkQuit
```

另外七个独立客户端使用相同 LAN 地址/端口，并传入 `-blockBenchmarkInput combat`；静态、爆破和坍塌场景使用 `-blockBenchmarkInput idle`。单独的 input 参数不创建性能探针，也不生成客户端性能结果。Host 仍渲染全部八个真实角色。这种配置的报告只证明被采样 Host 的本机八人场景性能，不证明客户端设备性能或物理多机 LAN。

若要测某个客户端，也为该进程显式传入相同 `-blockBenchmark <case>` 和独立 output 路径，保留真实图形输出。Host 负责播种和触发结构变更；客户端的 Host 专属动作门槛标为 `unverified`，必须与对应 Host 报告一起验收。

可选参数：

| 参数 | 默认 | 作用 |
| --- | ---: | --- |
| `-blockAcceptanceProfile` | `strict` | 当前性能显式指定 `prototype-v2-45fps`；历史 `prototype-v1` 保留，决定完整case范围、时长及帧时／发布／爆破门槛 |
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
- 当前 `prototype-v2-45fps` 要求帧时 P95≤22.2 ms、P99≤33.3 ms；历史 `prototype-v1` 和 `strict` 仍为15.5／16.67 ms。两个prototype档均保留偶发长帧为风险，`no33msSpikes` 和harness全帧最大值检查只作诊断，新档P99≤33.3 ms仍是通过硬门；`strict` 继续保留原尖峰硬门。每个尖峰保存已完成帧编号、主/渲染线程、GC、Tick/Publish/Upload/FallingRender 的计时及可用性，并关联同一主相机帧的待处理数、失支撑列数、版本和队列年龄；无对应帧时队列可用性明确为 false。结合这些记录和 Profiler 确认归因，不删去长帧，也不将当前档通过写成严格无尖峰通过。
- 真实普通主攻击命中的建造与直接敌块消除发布命令：两个prototype档均为P95≤200 ms，`strict` P95≤80 ms，以攻击来源显式标记。辅助飞行轨迹、诊断探针、爆破、副武器和大招不进入此门槛；每秒单格补充探针仍进入全部地形命令统计。命令计时从实际命中入队到最后一次成功发布，包含排队、候选准备、后台网格/烘焙和碰撞发布。另行记录普通小竖列从支撑移除到下落激活的P95≤100 ms目标，不能以发布200ms、网络100ms RTT或780秒性能采样自动证明其通过。
- 八次爆破必须在同一帧排入、八次均实际拆格，完整目标集合全部完成：两个prototype档均≤2000 ms，`strict`≤250 ms。不通过强度上限或范围截断缩短负载。
- 后续重新验收塔/级联时，保留原完整规模：拆除全部100底格、失支撑分离及下落守恒、最终落定与真实碰撞；记录实际落地完成时间，级联累计下落不少于2万格。失支撑列扫描及对应分离事务发布≤500 ms仍为strict硬门。这两场及专属最终核验本次整体后移，不记Passed；普通功能和联机中的永久悬空、未落定或卡死仍须修复。
- 错误和失败事务必须为零。GC 只记录总量、每帧分布和峰值，没有人为新增 GC 通过阈值。

报告同时记录验收档、画质档、vSync、目标帧率、CPU/核心数、内存/显存、图形 API、系统、屏幕和显示分辨率。包和程序集身份由启动 harness 记录。首版报告保留 `prototypeRisks`，严格档和历史失败保持原结论；缺测、诊断短跑、Editor 结果或仅构建成功不能替代当前包的正式通过。

性能报告与同最终源码的核心正确性证据共同验收：八爆的压力完成时间结合完整目标、敌友半径、静态遮挡、逐格守恒、原生碰撞及付费回调回归，不能只凭“八次回调且有删除”单独证明完整几何。普通发布分位统计已完成且实际改变地形的主攻击；同时记录窗口开始/结束的入队及完成计数、剩余命令和队头年龄趋势，避免把采样边界解释成所有请求均已完成。持续射击结束时少量正常排队不直接判失败；真实长期积压仍按原正确性要求检查。

历史六场范围可用 [边界诊断脚本](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/PartialLate86Final36Preparation01/diagnose-performance-boundary21-v2.candidate.py) 的 `--analyze --case-json <六个实际case.json路径>` 汇总数据；当前四场不得虚构后移两场结果来满足旧接口。该诊断保留原通过标记，把期末全部命令欠账保守视作普通慢样本，另给出P95上界的可证性；“不可证”“无数据”及边界pending不新增失败门槛。默认 `--describe` 不读取结果，不启动Player。诊断不能替代当前包正式通过。

网络验收是独立硬门，使用同一包及 `prototype-v1` 隔离网络请求：2/8 Player 各测 0ms/0%、100ms/1%、200ms/3%，共六组；100ms/1% 纠正 P95≤0.15 米、P99≤0.60 米，迟加入、重连、清场重建及积木一致性仍须通过。详见 [玩法与交付说明](BlockGameplay.md)。真实多机和人工操作观感是两项外部验收，按用户要求不计入当前 Goal；同机性能结论仅适用于当前电脑，其它目标设备也需另行验证。

下落启动另用opt-in独立Player探针：真实QueueBuild／QueueDestroy、自然帧和原子发布，新建及拆支撑各1、8、16、32格，每档30次；另做10Hz、300次×32格持续输入。激活、位移绘制边界与实际像素分开验收；该探针不覆盖付费武器输入、25万格性能或网络。源码调度预算见[调参说明](BlockTuningGuide.md)，它们不是新增验收门槛；正式性能仍按原四场完整样本及门槛判定。
