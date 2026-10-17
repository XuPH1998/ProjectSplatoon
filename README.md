# 积木建造与拆毁 · ProjectSplatoon

使用 Unity **6000.3.9f1** 的局域网第三人称积木建造与拆毁对战，最多八人、每队四人，粉蓝两队，三分钟一局。射击生成边长 **0.10 米**的积木，持续堆出路线与掩体，沿稳定己方积木及空白地图地面、墙壁弦化移动，并拆毁敌方建筑；时间结束时按双方存活积木数量判定胜负。空白表面可爬墙，使用普通弦化速度和材料恢复；己方积木才提供加速与快速回能。

## 快速开始

1. 将项目添加到 Unity Hub，使用上述版本打开，等待资源导入和依赖解析。
2. 打开 `Assets/Scenes/Main/Boot.unity`，进入运行模式。
3. 房主选择局域网地址并创建房间，按 Esc 后点击 **复制房间码**。伙伴在主菜单粘贴房间码加入。
4. 执行 **喷墨对战/构建/Windows 正式资源版本**，先构建本地 Addressables，再输出 `Builds/Windows/InkLan.exe`。

完整玩法见 [积木建造与拆毁规则](Docs/BlockGameplay.md)，操作、房间码及双机验证步骤见 [中文联机说明](Docs/LAN-Prototype.md)。当前候选包与验收进展见 [首版状态](Reports/BlockTerrain/FirstPlayableStatus20261013.md)；[早期涂色版本联机验证](Reports/Archive/2026-09-14/Docs/LAN-Validation.md) 仅为历史记录。

## 工程入口

| 目录 | 中文说明 |
| --- | --- |
| `Assets/Splatoon/` | 项目代码，运行时和编辑器程序集分离 |
| `Assets/GameResource/` | Addressables 运行时资源 |
| `Assets/Scenes/Main/Boot.unity` | 启动场景，唯一构建场景入口 |
| `Config/Luban/` | 源工作簿、结构定义、数据生成脚本 |
| `Tools/ExcelMerge/` | Excel 合并辅助工具 |
| `ProjectRules/` | 工程规范 |

配置只保留本原型所需参数，已清除 Vanguard 遗留表。修改源表后运行 `cmd /c Config\Luban\gen_luban.bat`，禁止手改生成 C#/JSON。

本项目界面、编辑器菜单和配置说明采用中文。API、稳定参数键、资源地址和第三方包名称保留英文，中文字段说明见 [配置指南](Config/Luban/README.md)。Unity 自带设置与第三方插件界面沿用其自身语言设置；本项目实际使用的参数见 [工程配置说明](Docs/Project-Configuration.md)。

早期涂色版本的移植分析、资源与弹道链路见 [历史实现说明](Reports/Archive/2026-09-14/Docs/InkMigration/Implementation.md)，对应证据见 [历史移植验证](Reports/Archive/2026-09-14/Docs/InkMigration/Validation.md)。

## 固定立体训练场

默认地图为 `Assets/GameResource/Gameplay/maps/TrainingGround.unity`，X 宽 32 米、Z 长 64 米，积木顶界当前为基准地面以上 **8 米**，可在全局源表调整。可通过 **喷墨对战/地图/打开立体训练场** 直接编辑，游戏仍从 Boot 启动。固定地面、高台、坡道和桥面提供地图几何，积木在其上生成、堆积或下落；比赛统计存活积木数量，桥上桥下保留各自真实空间与碰撞。出生和换装保护区的完整竖直列禁止生成积木。进入房间加载固定 Scene，退出卸载。编辑地图几何或出生点后执行 **校验并烘焙当前地图**，再重新构建资源。见 [地图说明与历史验证](Docs/TrainingGround.md)。

2026-10-17源码采用8米顶界、每0.25秒保守扩散、墙面首层锚固、空白表面弦化，并提供三把爆炸武器的整体弹丸尺寸和原爆炸显示。44阶段已应用九文件定向生产修复、精确目标新测试及historical lazy clone；原8 sources／20 effective files组合编译零error／warning。lazy前source9的原四回归通过，原组合T1／T3均失败，各3次budgetDeferred、0served、0attributedPending，保留原结论。后续Tests44DeferredDiagnostic02的T1诊断重跑确证1次deferred changed progress实际发布（deferredProgress1／NoChange0／Published1，phase0→2、proof false→true、mutation0→1、checks538／prefix41）却未计目标服务；不推断其余2次deferred，也不回填原T1／T3路径。root已应用最小partial-progress修复＋仅repair24前缀补齐＋移除临时诊断三文件；此前FinalCandidateComposition44PartialPrefix01为10 sources／20 files，compile44-partial-prefix01完成、error／warning均0、compilationFailed=false，取得时点20源SHA／字节全匹配。修复后原T1／T3均真实Passed，两者served／attributedPending／published均1、unrelatedPublished均0；Full400原执行仍失败（旧公共Tick断言≤4096、实得8160）。现行无命令8192策略早于lazy；仅对齐该Tick合同的单文件测试修改已实际应用，运行时预算不改。此前FinalCandidateComposition44PolicyContract01为11 sources／21 files，compile44-policy-contract01完成、零error／warning，21源取得时点全匹配；该阶段首次idle读取超时保留，第二次于UTC11:40:05.2509507六状态false，runtime MVID保持、当时tests MVID为d2417278-33d0-41c0-9d13-730da6d3ba73。Tests44PolicyContract01合同对齐后Full400已Passed1/1，BlockHistoricalRecoveryTests整组14/14 Passed（failed／skipped／inconclusive均0）；原4096／8160失败保持原归属。原PolicyContract取消class实际1通过／1失败：Clear通过，AuthorityLoss在取消前line120因callbacks=1,0失败，权限取消路径尚未触发，不归类为生产取消缺陷；root现已应用仅测试的取消观察窗口修正，保留原提前完成断言，生产调度／预算不改；此前CancellationObservation为12 source stages／21 files，取消2/2 Passed；原Frame类2通过／1失败、line125的4192 DependentSpend期望True实False，runner32986退出1保持。仅加诊断的stage13单叶真实Failed1/1，frame9→9、checks4000→4000，terrainMs1024.5534→1024.6008已超1000ms、commandPresent=false，runner8002退出1，确证该次有限测试时钟干扰整数额度，并非生产checks额度拒绝；不回判原失败。root仅将UseIntegerIsolationPolicy的1000改为double.MaxValue，真实3ms独立测试、数字oracle及生产代码／预算保留；当前FinalCandidateComposition44FrameIntegerIsolation01为14 source stages／21 files，compile44-frame-integer-isolation01完成、errors／warnings均0，21源时点绑定全匹配；新idle01的HTTP400读取失败保留；本轮指定8叶已实际8/8 Passed（Frame3／Motion2／Fair3），runner57488退出0，两轮原Frame失败保持各自测试身份。root已完成4083文件源冻结（SHA256 `928ce0a4447ef811e2c2b0ec41a040aed87ee46d74d212b2308e61cab7bec310`），14/21组合与freeze审计21/21通过；44正式生产及Addressables构建已成功，4083冻结输入／359包文件完整审计和44项Shader审计通过，仅审核的link.xml／meta二件已归档，19项偏好恢复通过。Final44现已执行结束：240 low-load＋300 sustained共540/540完成，latencyTargetsPassed=true，但sustainedDrained=false、functionalPassed=false；sustainedAllCallbacksFull／cleanupJoined均true。launcher67985退出1，bootstrap37772／harness／Player退出2，正式reader的21项完整性全通过，但startupProbeAccepted=false、唯一失败谓词为sustainedDrained；八张原PNG已逐张看见cyan源列，最终4083源／359包身份零变化通过，均不改判功能失败。剩余1组startup未通过、4场正式性能未跑、6组网络未跑；43包不覆盖44。43冻结阶段模拟版本38新增reason18重试后的独立重力处理机会，活跃fair不介入、reason8未改，已补journal-local准入归属、取消边界与96-cohort夹具，并应用FairPaidExact夹具，最新复编译成功、error／warning均0、compilationFailed=false。43早期自然复现单叶由失败转为通过，未证明墙钟更快；收紧前Core01终态69叶66通过／3失败，旧raw详情缺口保留，Reviewed01原14叶计划未闭合、首Fair叶失败（全局完成增量预期8、实得12）；FairPaidExact后Reviewed02最终针对性7/7通过，不代表最终完整69叶全通过；七叶末尾idle已确认；43正式构建成功，4081原输入未变、359文件完整包审核及44项Shader审核通过。Final43共540/540完成、时延目标通过，但Quiet排空仍使功能探针失败（退出2）；42→43持续入队→激活P95为83.2745→92.2492ms，发布→激活58.3501→66.7444ms，不证明43更快。43同包正式四场已完整采样780.0180534秒，2/4通过、整体性能失败；steady／combat普通发布P95为167500.000／262983.333ms，peakPending为20174／33447，均只有ordinaryPublishLatency阻断失败。四场帧P95/P99依次为14.864/20.155、17.362/25.495、9.740/12.703、10.307/13.269ms，均符合45fps；blast8的8/8爆破408.6826ms完成，scattered100k通过。performance reader完整性／进程隔离／19项Standalone恢复通过，28个client退出1及两个失败host退出5保留。root实际查看四张热身PNG，中文HUD／terrain可见，不证明图像时序或最终计数。43最终4081源／359包身份零变化通过；startup reader的21项完整性通过，但startupProbeAccepted=false。43六组网络未跑，整个Editor／环境恢复仍须独立收尾。42阶段指定Editor范围58/58通过，同探针持续输入的入队→下落激活P95由约159降至83毫秒，八张下落原图均可见，但固定排空期限后Quiet仍为false、整体探针未通过。42正式四场完整采样780.0280202秒，仅blast8／scattered100k通过，steady／combat因普通发布等门槛失败，整体性能未通过，42网络未跑。参数入口见[调参说明](Docs/BlockTuningGuide.md)，原始证据、steady40→42回归和阶段限制见[下落启动修复记录](Reports/BlockFallStartup20261017/Validation.md)。性能仍按 `prototype-v2-45fps` 原四场780秒及八个独立Player验收，历史失败保留。

早期涂色版本的粉蓝配色、渐变墨量、按米材质与版本 4 同步说明见 [历史墨水效果修复](Reports/Archive/2026-09-14/Docs/InkLook/Implementation.md)。

默认角色使用 CombatGirls RifleGirl 与步枪，接入持枪四向移动、原地转身、循环射击和死亡重生；当前主武器目录共九把，建造与拆除规则见积木玩法说明。早期角色迁移的预览入口、源目标对照图、3C/联机说明与分项验收见 [历史 RifleGirl 移植交付](Reports/Archive/2026-09-14/Docs/CombatGirls/Implementation.md)。

报告统一存放在 `Reports/`，历史记录见 [报告索引](Reports/README.md)；新增报告遵循 [目录规范](ProjectRules/FolderStructureStandard.md)。
