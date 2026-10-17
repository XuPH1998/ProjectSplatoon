# 积木对战调参说明

44阶段已应用九文件定向生产修复、精确目标新测试及historical lazy clone；原8 sources／20 effective files组合编译零error／warning。lazy前source9的原四回归通过，原组合T1／T3均失败，各3次budgetDeferred、0served、0attributedPending，保留原结论。后续Tests44DeferredDiagnostic02的T1诊断重跑确证1次deferred changed progress实际发布（deferredProgress1／NoChange0／Published1，phase0→2、proof false→true、mutation0→1、checks538／prefix41）却未计目标服务；不推断其余2次deferred，也不回填原T1／T3路径。root已应用最小partial-progress修复＋仅repair24前缀补齐＋移除临时诊断三文件；此前FinalCandidateComposition44PartialPrefix01为10 sources／20 files，compile44-partial-prefix01完成、error／warning均0、compilationFailed=false，取得时点20源SHA／字节全匹配。修复后原T1／T3均真实Passed，两者served／attributedPending／published均1、unrelatedPublished均0；Full400原执行仍失败（旧公共Tick断言≤4096、实得8160）。现行无命令8192策略早于lazy；仅对齐该Tick合同的单文件测试修改已实际应用，运行时预算不改。此前FinalCandidateComposition44PolicyContract01为11 sources／21 files，compile44-policy-contract01完成、零error／warning，21源取得时点全匹配；该阶段首次idle读取超时保留，第二次于UTC11:40:05.2509507六状态false，runtime MVID保持、当时tests MVID为d2417278-33d0-41c0-9d13-730da6d3ba73。Tests44PolicyContract01合同对齐后Full400已Passed1/1，BlockHistoricalRecoveryTests整组14/14 Passed（failed／skipped／inconclusive均0）；原4096／8160失败保持原归属。原PolicyContract取消class实际1通过／1失败：Clear通过，AuthorityLoss在取消前line120因callbacks=1,0失败，权限取消路径尚未触发，不归类为生产取消缺陷；root现已应用仅测试的取消观察窗口修正，保留原提前完成断言，生产调度／预算不改；此前CancellationObservation为12 source stages／21 files，取消2/2 Passed；原Frame类2通过／1失败、line125的4192 DependentSpend期望True实False，runner32986退出1保持。仅加诊断的stage13单叶真实Failed1/1，frame9→9、checks4000→4000，terrainMs1024.5534→1024.6008已超1000ms、commandPresent=false，runner8002退出1，确证该次有限测试时钟干扰整数额度，并非生产checks额度拒绝；不回判原失败。root仅将UseIntegerIsolationPolicy的1000改为double.MaxValue，真实3ms独立测试、数字oracle及生产代码／预算保留；当前FinalCandidateComposition44FrameIntegerIsolation01为14 source stages／21 files，compile44-frame-integer-isolation01完成、errors／warnings均0，21源时点绑定全匹配；新idle01的HTTP400读取失败保留；本轮指定8叶已实际8/8 Passed（Frame3／Motion2／Fair3），runner57488退出0，两轮原Frame失败保持各自测试身份。root已完成4083文件源冻结（SHA256 `928ce0a4447ef811e2c2b0ec41a040aed87ee46d74d212b2308e61cab7bec310`），14/21组合与freeze审计21/21通过；44正式生产及Addressables构建已成功，4083冻结输入／359包文件完整审计和44项Shader审计通过，仅审核的link.xml／meta二件已归档，19项偏好恢复通过。Final44现已执行结束：240 low-load＋300 sustained共540/540完成，latencyTargetsPassed=true，但sustainedDrained=false、functionalPassed=false；sustainedAllCallbacksFull／cleanupJoined均true。launcher67985退出1，bootstrap37772／harness／Player退出2，正式reader的21项完整性全通过，但startupProbeAccepted=false、唯一失败谓词为sustainedDrained；八张原PNG已逐张看见cyan源列，最终4083源／359包身份零变化通过，均不改判功能失败。剩余1组startup未通过、4场正式性能未跑、6组网络未跑；43包不覆盖44。43冻结阶段的`WeaponSimulationVersion=38`。已补journal-local成功准入归属、取消边界测试及96-cohort夹具，并应用FairPaidExact夹具；最新复编译success=true、error／warning均0、compilationFailed=false，尚未验收。保留42的列启动优先、同帧共享预算及扩散让出机制；新增仅针对真实`BuildMovingOverlap`（reason18）重试的独立重力处理机会，在恢复全部原命令checkpoint后，为仍等待的同一FIFO头推进一次受原预算约束的重力工作。活跃fair owner存在时不介入，reason8（`OwnActivationGenerationChanged`）路径未改。8米顶界、9.81米／秒²重力、0.25秒扩散、墙面首层锚固和守恒规则保留。各版本的Editor、同源包、启动时延、正式性能及网络结果见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)。

42包正式四场已完整采样780.0280202秒，帧时P95/P99均通过45fps档，但仅blast8、scattered100k通过，整体性能失败：steady／combat普通发布P95分别为130583.333／248733.333ms，combat八人持续射击覆盖也未达标。steady40→42存在严重排队／重试回归；42网络未跑，43早期自然单叶已由失败转为通过，未证明墙钟更快；收紧前Core01终态69叶66通过／3失败，旧失败raw详情缺口保留。Reviewed01原14叶计划未闭合，首个Fair叶实测失败（全局完成增量预期8、实得12）；FairPaidExact后Reviewed02最终针对性7/7通过，不代表最终源码完整69叶全通过；七叶末尾idle已确认；43正式构建成功，4081原输入未变、359文件完整包审核及44项Shader审核通过。Final43共540/540完成、时延目标通过，但Quiet排空仍使功能探针失败（退出2）；42→43持续入队→激活P95为83.2745→92.2492ms，发布→激活58.3501→66.7444ms，不证明43更快。43同包正式四场已完整采样780.0180534秒，2/4通过、整体性能失败；steady／combat普通发布P95为167500.000／262983.333ms，peakPending为20174／33447，均只有ordinaryPublishLatency阻断失败。四场帧P95/P99依次为14.864/20.155、17.362/25.495、9.740/12.703、10.307/13.269ms，均符合45fps；blast8的8/8爆破408.6826ms完成，scattered100k通过。performance reader完整性／进程隔离／19项Standalone恢复通过，28个client退出1及两个失败host退出5保留。root实际查看四张热身PNG，中文HUD／terrain可见，不证明图像时序或最终计数。43最终4081源／359包身份零变化通过；startup reader的21项完整性通过，但startupProbeAccepted=false。43六组网络未跑，整个Editor／环境恢复仍须独立收尾。41→42隔离探针的时延改善和源块显示通过不改变Quiet排空失败，历史gameplay40性能2/4、网络0/6通过保持原包归属。

本文说明 2026-10-17 本轮批准方案对应的源码参数和修改入口，供后续平衡调整与包交付使用：全局顶界 8 米、保守扩散间隔 0.25 秒、空白表面弦化，以及三把爆炸武器的显示参数。性能新增 `prototype-v2-45fps`；45fps为验收目标，历史gameplay40正式四场完整实测780.2430886秒，steady与combat未达到门槛、后两场通过，整体性能失败。普通小竖列启动的24次隔离Editor试验已完成，总体P95＝14.94ms、最大208.29ms、23/24次≤100ms；该小样本不是Player或积压场景，也没有配对修改前基线，不能宣称实测提升比例。gameplay38 正式资源与 Development Player 已构建并核验359文件完整包集合／大小／SHA，保持历史阶段归属。gameplay39历史阶段已补同轮接收列保护、WeaponSimulationVersion=35，新规则12/12、摊开集成5/5通过；39正式Addressables与Development Player已构建，原4067冻结输入未变、两个新增生成物已审阅，359文件包集合／大小／SHA独立复算一致，构建专用19项Editor偏好已恢复。39自动显示18/18飞行、28/28爆炸与隔离／Standalone恢复通过，主任务已实际检视39的34组PNG control/sample，双方表现可见，2m泡泡五弹出框及12m副武器较小的限制保留；八独立Player四场实际采样780.3354543秒已完整完成，steady250k与combat250k均因帧P95／P99及普通发布P95三项失败，第三场blast8通过、最后scattered100k完整180.0006秒通过，四场完整但整体性能失败；自然比赛两Player约180秒流程观察12/12门通过、完整包／自然HUD状态／多端积木oracle不由该smoke签收；39六组网络已完整结束，6/6均在穿行后的地形排空等待超时，`requiredProfileMatrixComplete=true`／`caseProfilesMatch=true`而`networkProfilePassed=false`；30个Player＋6个proxy的桌面隔离及Standalone恢复独立成功，30个owned Player全退出，wrapper／native退出4保留原网络失败。上述gameplay38～40的历史构建、功能、联机和性能见[历史2026-10-17验证记录](../Reports/BlockGameplay20261017/Validation.md)；43冻结阶段模拟版本38及当前44生产候选另见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)，不由本文判定；[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)保留历史包与源码归属。此前[高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md)的后移范围保留；旧报告保留各自包和源码归属。

历史 2026-10-16 方块子弹显示修复曾构建为 Windows37，并通过该包内九主武器双阵营18例显示检查，见[验证报告](../Reports/BlockTerrain/CubeProjectileRepair20261016/Validation.md)。该历史结果不覆盖本轮尺寸、特效与玩法改动。飞行弹丸由 [BlockFlightPresentation](../Assets/Splatoon/Runtime/Combat/BlockFlightPresentation.cs)绘制；[BlockProjectileInstanced 材质](../Assets/Splatoon/Runtime/Blocks/Resources/BlockProjectileInstanced.mat)必须保留在 Resources 中并开启实例化，以保留 Player 使用的着色器变体。显示通过也不代表性能和联机门槛通过。

下落积木的[BlockTerrainInstanced材质](../Assets/Splatoon/Runtime/Blocks/Resources/BlockTerrainInstanced.mat)也须保留在Resources中并开启实例化；运行时从模板复制，以保留Player实际使用的地形着色器实例化变体。模板缺失时仍保留原Shader回退。修复前41包缺少该编译变体的证据与修复后实际包／画面检查见本轮记录，弹丸与爆炸显示的历史通过不能替代下落块显示验收。

## 固定玩法约定

积木边长为 **0.10 米**，使用 **16³ 格**稀疏区块，每区块覆盖边长 1.6 米的空间。格尺寸与区块尺寸属于玩法及协议约定。建造沿用地图水平边界，当前全局顶界由源表 `blockWorldTopHeight=8` 控制，即基准地面以上 **8 米**；不是每个高台分别再往上 8 米。顶界与扩散参数参与内容签名，联机各端须一致。

贴地图墙面的第一层不论下方是否有支撑都锚固，且排除扩散；向外突出的后续层需要下方支撑。失去支撑的连续竖列共享下落运动，并逐格保留阵营。下落块可被攻击，但不承托角色；不增加自然消失、落块砸伤或天花板附着。出生与换装保护区覆盖的整格竖直列禁止两队生成积木，不额外创建碰撞屋顶。

本轮采用保守的离散扩散：每 0.25 秒调度一轮，源列连续稳定堆叠至少两格才尝试移动非锚固顶格，可把底部锚固格计作稳定支撑。源列顶面须比邻列高出 `blockSpreadMaxStepCells` 以上，默认高差大于一格；每次水平只移动到八方向相邻一格，选择较低可用位置。源、目标有效支撑基底的高差最多为该参数，默认一格，因此不跨平台陡坎向下外抛；斜向移动还须至少一条完整正交路径。每源列每轮最多移出一格；本轮接收过迁移的目标列不再作为来源，即使到达块后来因重力落到另一高度也暂缓移出，下一轮清理保护后恢复。保护仅限制来源，目标仍可继续接收。移动保留原阵营、总格数和计分，不免费增块或产生建造充能；落点受地图边界、静态几何、保护列和玩家占据约束，路径受地图实体和连续支撑路线约束，逐格落位按瞬时提交处理。涉及下落的列等待稳定，锚固格不移动。扩散周期是调度间隔，后台预算和发布排队可使整轮跨帧，不能当作任意堆体整形完成时间的保证。实现入口为 [BlockSpreadRules](../Assets/Splatoon/Runtime/Blocks/BlockSpreadRules.cs)和[BlockTerrain.Spread](../Assets/Splatoon/Runtime/Blocks/BlockTerrain.Spread.cs)。

三分钟结束时按双方存活积木总数判定胜负，相同则平局。已形成的静态块与下落块都计数，重力移动和重新落定不改变分数；飞行弹丸与装饰碎片不计数。普通射击命中地图建造首层，命中己方块沿命中面外侧堆积，命中敌块拆敌后结束本次命中。

实现入口为 [BlockWorld](../Assets/Splatoon/Runtime/Blocks/BlockWorld.cs)、[BlockTerrain](../Assets/Splatoon/Runtime/Blocks/BlockTerrain.cs)和[比赛积木同步与计分](../Assets/Splatoon/Runtime/Prototype/PrototypeMatch.BlockSync.cs)。占据、阵营、显示、Collider 与分数在统一提交边界发布，查询只读取已发布地形。

## 全局参数

修改源文件 [TbGlobal.xlsx](../Config/Luban/source/TbGlobal.xlsx) 的 `Global` 工作表；新增字段还需同步修改 [gameplay.xml](../Config/Luban/source/Defines/gameplay.xml)。运行时经生成的 `GlobalConfig`、`GameplayConfig.Global` 和 [BlockGameplaySettings](../Assets/Splatoon/Config/BlockGameplaySettings.cs)读取。下表对应当前 [tbglobal.json](../Assets/GameResource/Bootstrap/Config/Luban/tbglobal.json) 的值。

| 源表及 JSON 字段 | 当前值 | 调整作用 |
|---|---:|---|
| `blockWorldTopHeight` | 8 | 相对基准地面的全局顶界，米 |
| `blockSpreadInterval` | 0.25 | 保守扩散调度间隔，秒 |
| `blockSpreadMaxStepCells` | 1 | 源与目标支撑基底允许的高度差上限，同时要求邻列顶面高差大于此值；水平固定相邻一格 |
| `blockSpreadMinStackCells` | 2 | 源列连续稳定堆叠的最小格数，可包含底部锚固支撑 |
| `blockBlocksPerMaterial` | 32 | 每点实际支付材料的建造格数 |
| `blockMainAttackMaxBlocks` | 512 | 一次主攻击整组的建造上限 |
| `blockAuxiliaryBudgetFraction` | 0.25 | 沿途、脚下合用的辅助建造预算占比 |
| `blockFriendlyBlastRadiusFraction` | 0.5 | 己方结构爆破半径相对敌方半径的比例 |
| `blockBlocksPerSpecialPoint` | 32 | 每点大招充能对应的实际建造与拆敌格数 |
| `blockMainSpecialPointCap` | 2 | 一次主攻击整组的大招收益上限 |
| `blockSubSpecialPointCap` | 10 | 副武器整次使用或部署的大招收益上限 |
| `blockNetworkBytesPerSecond` | 2097152 | 房主全房间积木传输预算，字节／秒 |

[BlockAttackService](../Assets/Splatoon/Runtime/Blocks/BlockAttackService.cs)按实际扣费创建攻击预算。多弹丸、四颗泡泡、反弹、沿途、脚下和持续部署继承同一次额度，不能按派生事件重新获得产出。辅助建造同时扣除总预算和辅助预算，其整组上限为 `floor(BuildLimit × 0.25)`；当前单次辅助接触另限最多 8 格。未实际建成的预留额度由结果回调返还。

充能按碰撞发布后的实际 `Added + RemovedEnemy` 计算，并受本次攻击累计上限限制。拆己方、重力移动、救援和大招自身的地形变化不充能。改变耗材、射速或连发方式也会影响单位时间建筑产出和大招收益，需一起评估。

## 下落速度与激活等待

本轮调度入口在 `BlockPublicationPolicy`、`BlockTerrain.GravityBudget.cs` 与 `BlockTerrain.GravityStartup.cs`。这些是源码常量，修改后需重新编译、重建并实测帧时和端到端时延。43冻结阶段另由[BuildOverlapRepair](../Assets/Splatoon/Runtime/Blocks/BlockTerrain.BuildOverlapRepair.cs)处理reason18：有变更时须等独立重力事务通过原校验并原子提交，无变更时须实际提交已获准的journal进度，确认无ready工作时有限释放；不等待全部块落地。该机会不增加预算、完成原付费命令或跳过碰撞证明，活跃fair和reason8仍沿原路径；这些是候选实现范围，不代表复测通过。

| 参数／入口 | 当前值 | 含义 |
| --- | ---: | --- |
| `BlockWorld.Gravity` | 9.81 m/s² | 实际下落加速度 |
| `GravityChecksWithCommands` | 4096 checks | 有待处理命令时，同一Unity帧全部生产重力入口的合计检查上限 |
| `GravityChecksAlone` | 8192 checks | 无命令时，同一共享账本的合计检查上限 |
| `GravityMillisecondsWithCommands` | 1.0 ms | 有命令时重力阶段时钟预算 |
| `GravityMillisecondsAlone` | 1.5 ms | 无命令时重力阶段时钟预算 |
| `FrameSynchronousMilliseconds` | 3 ms | 原有地形总同步计时预算，由各入口共享 |

阶段自己的剩余预算可能先耗尽，公平性seed／clone保留原较窄窗口。由4096切到8192只增加当帧剩余额度；丢弃草稿、取消命令和清回合均不退还本帧实际消耗，新Unity帧才重置。不可中断的原生调用及新帧为保证进展接纳的首个有界单元可能越过时钟预算，所以预算值不是实测耗时保证。

启动优先与普通访问按实际接纳检查量记债，约2:1分配，并保留普通cursor推进与余量重试。每个扩散有限搜索单元入队前，待启动工作须获得一次真实完成并提交的有界访问；只准备元数据或丢弃草稿不放行。原FIFO及扩散向重力让出规则保留，持续稳定建造可在实际服务后继续摊开；间隔仍0.25秒，积压不集中补跑。

41→42配对测量使用同一冻结的独立Player探针，结果见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)。旧24次Editor数据仅属历史诊断。探针以真实发布观察激活，以真实位移矩阵和主相机绘制事件观察位移绘制边界，后者不是首个可见GPU像素；像素显示另看实际PNG。冻结探针的Budget.maximumChecks仍为历史4096字段，不代表新源码无命令时8192上限；它也不读取新增内置检查／证明分段。内置分段计时对激活前再次断裂的private child／historical recovery不保证完整样本，不据此承诺全路径时序覆盖。


当前实际下落运动使用 `BlockWorld.Gravity=9.81` 米／秒²，新激活的普通竖列从零初速开始，位移按 `s=v0×t+0.5×g×t²` 计算；已在下落的列被拆分时保留已有运动，不重新从零加速。以零初速、未碰撞且不含激活与发布等待为前提，自由落下0.1米约0.143秒、1米约0.452秒、8米约1.277秒；连续竖列共享运动，落地位置还取决于实际支撑。`Gravity` 当前是源码常量，不是 Luban 字段，修改需重新编译与重建包；调度帧预算控制等待，不改变重力常量。

支撑移除后还需扫描、分离并激活下落，这段等待与上述实际运动时间分开。普通无大量积压、单根1～32格竖列的隔离Editor试验已完成24次：激活等待总体P95＝14.94ms、最大208.29ms、23/24次≤100ms；单格组仅6次，其P95为208.29ms。该小样本不保证每个高度或每次均≤100ms，也不覆盖大坍塌、后台积压或Player。没有配对的修改前基线，详见[本轮验证记录](../Reports/BlockGameplay20261017/Validation.md)。普通攻击碰撞发布P95≤200ms、网络RTT和整列落地时长分别属于其他指标，不能互换。

## 九把主武器

英雄表 [TbHero.xlsx](../Config/Luban/source/TbHero.xlsx) 的 `weaponConfigPath` 指向独立 [WeaponConfigAsset](../Assets/Splatoon/Config/WeaponConfigAsset.cs)。实际资产位于 `Assets/GameResource/Weapons/<英雄>/<英雄>WeaponConfig.asset`，Inspector 的“积木建造与拆除”分组包含下表三个字段。

| 主武器 | 资产目录 | `blockBuild` | `blockEnemyRemoval` | `blockRemovalRadius` |
|---|---|---:|---:|---:|
| 专业模型枪 MG | `SplooshGirl` | true | 4 | 0.20 米 |
| 窄域标记枪 | `PistolGirl` | true | 6 | 0.20 米 |
| 斯普拉射击枪 | `RifleGirl` | true | 8 | 0.20 米 |
| 斯普拉旋转枪 | `MachineGunGirl` | true | 每实际发射弹 8 | 0.20 米 |
| 开尔文 525 | `DualPistolGirl` | true | 12 | 0.20 米 |
| 满溢泡泡枪 | `BubbleGirl` | true | 四颗共享 32 | 0.20 米 |
| 爆炸泼桶 | `ShotgunGirl` | false | 0，使用爆破路径 | 普通拆除路径不使用 |
| 快速爆破枪 | `RocketLauncherGirl` | false | 0，使用爆破路径 | 普通拆除路径不使用 |
| 爆泡霰弹枪 | `BubbleShotgunGirl` | false | 0，使用爆破路径 | 普通拆除路径不使用 |

普通拆除从查询半径内距离命中点最近的敌格开始，每格采用完整存在或删除的状态。提高 `blockEnemyRemoval` 增加每次付费攻击的拆除额度，提高 `blockRemovalRadius` 扩大候选范围；命中结束后不顺带建造，也不伤害后方角色。三把爆炸主武器保持只拆除、不建造。

材料成本仍使用 `shotInk` 等代码字段名；`WeaponSimulation.InkCost` 将实际成本用于预算。资产经 [WeaponConfigService](../Assets/Splatoon/Config/WeaponConfigService.cs)加载并冻结为运行时快照，已发射弹丸保留发射时配置。正式 Player 使用 Addressables 资源，调参后需正式重建资源与 Player。

`BlockGameplayBuilder.ApplyWeaponDefaults()` 会覆盖九把武器的上述三个积木字段，适合恢复批准的初始基线。平衡调整后不要无意再次调用它覆盖新值。

### 三把爆炸武器的弹丸显示尺寸

沿主武器 `ammoConfig` 引用打开 `AmmoConfigAsset`，在 Inspector 的“积木弹整体尺寸（米，零为自动）”调整 `blockProjectileSize`。这是整组飞行积木弹的本地宽／高／长，单位为米，不是单个装饰方块边长。三轴全零使用原自动尺寸；自定义时三轴都须为有限正数，部分零、负数、NaN或无穷值不合法。配置冻结到新弹丸快照，已发射弹丸保留发射时尺寸。

| 主武器 | Ammo 资产 | 本轮批准默认整体尺寸（宽×高×长，米） |
|---|---|---:|
| 快速爆破枪 | `Assets/GameResource/Weapons/RocketLauncherGirl/RocketLauncherGirlAmmoConfig.asset` | 0.30×0.30×0.42 |
| 爆炸泼桶 | `Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlAmmoConfig.asset` | 1.05×1.05×1.05 |
| 爆泡霰弹枪 | `Assets/GameResource/Weapons/BubbleShotgunGirl/BubbleShotgunGirlAmmoConfig.asset` | 1.20×1.20×1.20 |

爆炸泼桶的2×2积木组合按整体尺寸缩放；其他枪和满溢泡泡枪保留各自原自动规则。尺寸只影响表现，保持弹道、碰撞、伤害与结构爆破半径。主武器和爆炸类副武器恢复原爆炸预制体，同时保留积木碎片、阵营染色和池化；不向侦测、毒雾、防护等非爆炸事件添加爆炸。gameplay40同源Player已完成18/18飞行与28/28原爆炸自动显示通过，主任务34组对照人工可见性检视完成；泡泡近机位出屏和12m副武器较小限制保留，完整轮廓与自然战斗可读性未签收，见[40显示读取汇总](../Reports/BlockGameplay20261017/VisualFlight40/visual-final-read-summary.json)。

## 爆破、副武器与大招

结构爆破先确定完整目标，再分批删除。静态地图遮挡爆破，范围内积木不遮挡同一次结构爆破。敌方使用实际结构半径，己方乘全局比例 0.5；该比例作用于半径，不表示删除块数或角色伤害减半。角色伤害另行结算，爆破不会被普通拆敌格数额度截断。

主武器结构半径由 [InkExplosionRules.Radius](../Assets/Splatoon/Runtime/Combat/InkExplosionRules.cs)读取：

```text
AmmoRuntimeConfig.ExplosionRadius × (碰撞爆炸 ? CollisionExplosionRadiusRate : 1)
```

源字段在武器 `ammoConfig` 引用的 Asset 中，为 `explosionRadius` 和 `collisionExplosionRadiusRate`。`collisionExplosionPaintRadius`、`explosionPaintRadiusMin/Max` 属于原表面涂色参数，不决定积木结构爆破半径。

十三种副武器的源表为 [TbSubWeapon.xlsx](../Config/Luban/source/TbSubWeapon.xlsx)，通过 `configPath` 找到 `Assets/GameResource/SubWeapons/<类型>/<类型>.asset`。[SubWeaponService](../Assets/Splatoon/Runtime/Combat/SubWeaponService.cs)通常读取 `Blast.outerRadius`；冰壶、碳酸后续爆炸及鱼雷子滴分别沿各自配置取值。`Common.inkCost` 是整次材料成本，`Common.inkLock` 是恢复锁定。洒墨器等部署物只获得有限材料额度，连爆、分裂和反弹共享原攻击预算。侦测、毒雾、防护保留各自功能，救援不免费建造或拆除积木。

五种大招的源表为 [TbSpecialWeapon.xlsx](../Config/Luban/source/TbSpecialWeapon.xlsx)，配置位于 `Assets/GameResource/SpecialWeapons/<类型>/<类型>.asset`。[SpecialWeaponService](../Assets/Splatoon/Runtime/Combat/SpecialWeaponService.cs)普通爆炸通常使用运行时 `P.outerRadius`，持续区域按对应技能配置处理，不能用统一的展示半径替代结构半径。

## 材料恢复与大招成本

英雄源表保留原 `Ink` 字段名，中文界面含义为材料。当前九名英雄的 `maxInk = 100`、`recoverInk = 10` 点／秒、`swimRecoverInk = 33.333332` 点／秒。快速恢复需要活着、正在弦化，并在稳定己方积木上处于地面或墙面附着；人形站立、空中弦化或仅靠近己方积木不满足资格。

恢复还受射击、副武器阶段、毒雾和恢复锁定时间限制，见 [ResourceSimulation](../Assets/Splatoon/Runtime/Combat/PlayerSimulation.cs)。敌方积木不触发旧敌墨持续掉血；历史 `enemyInkDamageRate` 等字段不是积木接触伤害的调参入口。

稳定己方积木和稳定空白地图地面、墙壁都允许弦化，空白墙也可爬。空白表面使用英雄表 `neutralSwimSpeed=3` 米／秒，材料保持普通 `recoverInk=10` 点／秒；既不获得己方加速，也不获得33.333332点／秒快速恢复。快速恢复需要当前稳定己方接触，跨缝或空中保留的移动来源不能单独取得资格。敌方积木和下落块不提供附着。连续支撑的局部坡面同时驱动移动、纸片姿态与受击体，真实碰撞仍按积木形状，拟合不能跨越空洞。支撑被拆除或进入下落后，下一次移动模拟退出附着；空中形态与翻越流程保留。

当前 `PaperTraversalSimulationVersion=11`，`BlockTerrain.IsBlockGeometry` 用已登记的原地图 Collider 身份区分共享 Arena 根节点下的地图和未发布积木几何，普通接触与球形足迹均使用该判定；未知积木几何不能回退为裸地图。真实角色完整弦化场景的最新 Editor 单叶与地图分类修复后 LiveGeometry 12/12 已通过；首层锚固的窄墙端补强已应用，gameplay38冻结前指定关联复测为锚固 27/27、重力启动 8/8、摊开集成 4/4 及 7 个 Traversal 单叶全部通过，最终静态与编译空闲／MVID 回执已保存。这些是分阶段 Editor 结果，尚不是最终源码同一次全量重跑或 Player 验收。墙脚水平展开不安全时仍采用安全空中转换；持续背向撞地图外边界时可重复触发落地拒绝及保守空中恢复，这是尚未修复的有限已知行为。相关失败、修复、有效通过及输入／等待边界见[本轮验证记录](../Reports/BlockGameplay20261017/Validation.md)。

正式 gameplay38 构建保持原4067个冻结输入哈希不变，另生成两个已审阅的 Addressables link.xml／meta 文件；二者已按SHA备份移至Build38/GeneratedBuildOutputs，并有移除回执，历史构建时的源码集合实际新增事实保留。38首轮像素通过而窗口隔离审计失败（退出6）；38b修正隔离审计后，18/18飞行叶和27/28原爆炸叶通过，但SuctionBomb队伍2零像素，整体仍失败（退出2），Standalone偏好恢复独立成功。主任务已检视38联系表，确认两队及原prefab可见，同时记录泡泡2m机位出框、副武器12m机位较小；固定fixture不等于自然战斗观感验收。39已通过同步Reports构建守卫完成正式构建，4067文件manifest SHA为`a10d5ef84d8c3856e3095d9cd58ce7ad2eb683317d7513a602542b62938a3ff1`；原输入未变，两个同哈希生成物已审阅，359文件包核对与构建19项偏好恢复成功。39自动显示18/18飞行、28/28爆炸及隔离／Standalone恢复通过；人工34组PNG已检视并保留泡泡出框限制，自然流程观察12/12门通过、完整包／自然HUD状态／多端积木oracle保留原范围，39六组网络已完整结束，6/6均在穿行后的地形排空等待超时，`requiredProfileMatrixComplete=true`／`caseProfilesMatch=true`而`networkProfilePassed=false`；30个Player＋6个proxy的桌面隔离及Standalone恢复独立成功，30个owned Player全退出，wrapper／native退出4保留原网络失败，四场正式性能前两场steady250k与combat250k均失败，第三场blast8通过、最后scattered100k完整180.0006秒通过，四场完整但整体性能失败。显示检查使用配置驱动的合成表现事件，不覆盖付费开火、权威碰撞、伤害、RPC或完整比赛；详情及阶段证据见验证记录。

充能收益和启动成本分别调参。大招表的 `requiredPoints` 决定默认启动成本，[TbLoadoutCost.xlsx](../Config/Luban/source/TbLoadoutCost.xlsx)允许按英雄／副武器／大招的更具体规则覆盖，见 [PlayerLoadout.RequiredPoints](../Assets/Splatoon/Runtime/Combat/PlayerLoadout.cs)。同优先级重叠规则会报错。

## 修改与复测流程

1. 记录参数原值、新值及预期影响，只修改真实来源并保留其他已有工作。
2. 全局、英雄、比赛和配装数值修改 Luban 源工作簿；新增字段同步修改定义。攻击细节修改主武器、副武器或大招对应配置 Asset。
3. 工作簿修改后在项目根目录执行 `cmd /c Config\Luban\gen_luban.bat`。核对生成返回码，以及源表、`Assets/Splatoon/Config/Generated` 和 `Assets/GameResource/Bootstrap/Config/Luban` 的一致性，不能手改生成 C# 或 JSON。
4. 在对应 Editor 中导入和编译，复测变更关联功能、原未决普通用例及核心硬门。影响范围可靠时复用未受影响基线；全局生命周期或共享运行时改动应扩大范围。静态编译不替代 Unity、PhysX、Player 或联机验证。
5. 正式重建 Addressables 与 Windows Development Player，并记录源码、程序集、资源及包身份。武器和技能配置参与内容签名，新参数包不能借用旧包结果。
6. 测试、构建和独立 Player 验收串行运行。使用现有隔离桌面入口和禁系统鼠标捕获参数，不激活窗口、不切换用户输入桌面；完成一轮且恢复偏好后再进入下一轮。

调度预算控制每帧工作，不限制最终积木容量或爆破目标。降低每帧预算可能减少当帧开销，也会延长发布、爆破和重力队列等待；应同时测帧时与端到端时延。一次原生碰撞提交不能在调用中途切断，代码预算不代表实测耗时保证。

## 当前首版验收范围

| 项目 | 必需条件 |
|---|---|
| 普通功能与比赛 | 实际建造／拆除／下落、守恒、扣费与返还、原子碰撞发布、弦化与恢复；自然三分钟比赛、结算、热身及回合清空 |
| 四场性能 | `steady250k` 180 秒、`combat250k` 300 秒、`blast8` 120 秒、`scattered100k` 180 秒，共 780 秒正式采样；原积木规模保持 |
| 渲染与帧时 | 当前电脑，房主正常主相机真实 1920×1080，七个独立无图形客户端参与；`prototype-v2-45fps` 帧时 P95≤22.2 ms、P99≤33.3 ms，为待测目标 |
| 普通下落激活 | 无积压1～32格的24次隔离Editor试验，总体P95＝14.94ms、最大208.29ms、23/24次≤100ms；不含实际落地时间，非Player验收，无配对修改前基线 |
| 普通碰撞发布 | 真实主攻击命中至碰撞发布 P95≤200 ms |
| 八次同时爆破 | 完整目标与敌友半径保持，全部直接拆除≤2000 ms |
| 六组联机 | 2／8 个独立 Player，各测 0 ms／0%、100 ms／1%、200 ms／3%；包括迟加入、重连、普通下落、清空重建及逐项一致性 |
| 弱网移动纠正 | 100 ms／1%，P95≤0.15 米、P99≤0.60 米；充分真实移动与己方积木支撑样本，不能用视觉诊断代替纠正统计 |
| 包与说明 | 同源正式资源及 Windows 包、中文 HUD、来源检查、真实验证报告、已知风险和调参说明 |

`tower32m` 与 `cascade20k` 两场及其专属最终碰撞、压力与性能验收整体为 **user-deferred-not-accepted**，本次不阻挡 Goal，不能记为 Passed。普通功能和联机中的重力、支撑、守恒、碰撞、恢复仍必须通过，当前玩法顶界为8米。真实多机与人工观感两项外部验收不计 Goal。

当前性能运行显式使用 `prototype-v2-45fps`：Player传 `-blockAcceptanceProfile prototype-v2-45fps`，harness传 `--acceptance-profile prototype-v2-45fps`。偶发长帧保留完整原样本并列为风险，新档的P99≤33.3ms仍须通过。历史 `prototype-v1` 保留15.5/16.67ms帧时、200ms发布和2000ms八爆；原 `strict` 的15.5/16.67ms、十八组网络、稳态600秒、普通发布80ms、爆破250ms及尖峰硬门保留各自语义。网络仍单独使用 `prototype-v1` 六组档，不能把新的性能档名称传给网络工具。不得通过扩大格尺寸、降低规模、丢格或截断爆破范围取得通过；单次短跑和诊断入口也不能替代正式采样。

联机完成需同时核对 `networkProfilePassed`、`requiredProfileMatrixComplete` 与 `caseProfilesMatch`；性能需核对 `performancePassed`、`requiredWorkloadsComplete`、实际profile、范围记录及全部风险。`Tools/BlockTerrain/performance_acceptance.py` 对新 `prototype-v2-45fps` 原生选择并校验四场、780秒，禁止用后移的两场验证新档；省略profile仍默认 `strict`。基础工具中的历史 `prototype-v1` 继续保持六场语义，历史四场结果依赖当时wrapper的范围适配。39冻结源码正式780秒／八独立Player性能已执行首场steady250k完整180.176秒，积木289731～320641块；帧P95=63.4490ms、P99=326.4898ms、普通主攻击发布P95=2200ms，三项阻断失败，574.1417ms尖峰另列诊断。第二场combat250k完整300.149秒，帧P95=58.2051ms、P99=447.1885ms、普通发布P95=7400ms，三项阻断失败，754.9658ms尖峰列诊断。第三场blast8完整120.009秒通过，帧P95=11.4245ms、P99=16.2191ms，8/8爆破316.0667ms，83.4748ms尖峰保留诊断；最后scattered100k完整180.0006秒、100000块保持，P95=9.8588ms、P99=12.2411ms通过，352.5257ms尖峰仍列诊断。四场完整、profile一致，但performancePassed=false；32次启动隔离审计和Standalone恢复通过，外层退出5透传前两场失败；没有配对修改前基线，不宣称改进比例或摊开造成了回归。

历史 Windows36 使用[同包验收 Revision02](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/README.md)中的[四场性能入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-four-case-performance-causal36.candidate.py)、[自然三分钟比赛入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-natural-smoke-causal36.candidate.py)和[六组联机入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-network-causal36.candidate.py)。三者绑定其[实际包身份](../Reports/BlockTerrain/KnockbackFixtureWindows36Preparation01/package-identity.json)和[实际来源](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/actual-binding.json)，不覆盖本轮新源码。新包需准备新的同源请求、身份和运行目录，不能直接重用冻结旧请求。每一轮的实际运行、退出、隔离和偏好恢复均结束并核对后，才执行下一轮。

历史 2026-10-16，Windows36 正式资源与 Player 构建、完整包检查及实际来源绑定已完成；当时22项关联回归的通过覆盖来自原15项、fresh200六项及fresh201一项的真实结果及恢复证据，不覆盖本轮源码。该历史覆盖也不表示完整程序集在最终图重跑。自然比赛、性能、网络、中文 HUD 和最终交付分别依据对应实际终态判定，最新进展见[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)。旧包、旧入口及历史失败保持各自归属。各摘要只代表对应验收范围，整个 Goal 还需功能、同源包与交付证据闭合。

后续gameplay40是独立修复与复验阶段：主任务已定位扩散搜索可能持续抢占重力推进机会的源码路径，将保留39原始失败，先保存修复前失败证据，再做窄公平性修复、修复后回归及新的冻结／正式构建／同包验收。该路径尚不足以单独解释39全部网络或性能失败；40支持文件的静态准备不代表修复已验收、40已构建或通过。本段为39终态记录时的后续计划，不替代后来40实际回执。

进度补记（独立gameplay40阶段）：[39生成物归档回执](../Reports/BlockGameplay20261017/Build39/GeneratedBuildOutputs/removal-receipt.json)保存两项link原件／SHA，原路径已移出；主任务随后确认原4067冻结输入仍匹配，再仅应用新增测试。真正的修复前失败来自`Tests40BeforeFix02`的1个Golden单叶（sawRealMotion=false），初次reload HTTP400只是dispatch失败；[生产应用回执](../Reports/BlockGameplay20261017/Tools/SpreadGravityFairness40/production-applied.json)现已记录两处窄公平性生产候选及WeaponSimulationVersion35→36，运行预算未改。[Golden前后独立汇总](../Reports/BlockGameplay20261017/Golden40/golden-before-after-summary.json)现已确认同一单叶修复前1失败→修复后1通过，实际UTF叶耗时58.476709s→53.0749285s；after wrapper60.2s另列，不当作test耗时。progress001读取超时不是断言失败，progress002当时仍运行且无结果，通过证据来自最终terminal。[三类关联回归汇总](../Reports/BlockGameplay20261017/Tests40Related/associated-regression-summary.json)已记录SpreadIntegration6/6、GravityStartup8/8、SpreadRules12/12通过；Integration终态只留末叶，由同job progress012的5个去重Passed叶补齐6叶证据，不把10条缓存记录当10通过。[额外8叶原终态汇总](../Reports/BlockGameplay20261017/Tests40Scheduling/scheduling-regression-summary.json)现为7通过／1失败；SupportRetryWindow原类2通过／1失败，首异常与整队列Single选择假设一致，后续teardown回调不证明paid绕过scan。fixture仅改为选择唯一非spread命令并保留其余断言，适配后同叶实际1/1通过；[34个不同叶关联闭合汇总](../Reports/BlockGameplay20261017/Tests40Related/related-regression-closure-summary.json)记录26＋8个FullName当前通过，原7／1及原类2／1保持历史，独立Golden和定向复测不增加用例数。生产runtime仍36；[Build40构建与绑定读取汇总](../Reports/BlockGameplay20261017/Build40/build-and-binding-read-summary.json)现已记录正式构建、4067原输入／359包文件审计及19项偏好恢复成功，CLI外层timeout退出1未重dispatch。[Network40最终汇总](../Reports/BlockGameplay20261017/Network40/network-final-read-summary.json)现已记录六组完整结束、profile一致但全部drain超时失败；30Player＋6proxy隔离与退出、Standalone恢复独立成功，native／guard／wrapper4保留原网络失败。首组历史采样持续推进、19共同revision字段一致，仍未排空，不能推广全部case根因。[Visual40读取汇总](../Reports/BlockGameplay20261017/VisualFlight40/visual-final-read-summary.json)另记录18/18飞行、12/12主爆炸、16/16副爆炸自动通过及34组对照人工可见性检视，泡泡近机位出屏和12m副武器较小的限制保留；[Performance40最终读取汇总](../Reports/BlockGameplay20261017/Performance45fps40/performance-final-read-summary.json)记录四场完整采样780.2430886秒，steady三项阻断失败、combat仅普通发布P95失败，blast8／scatter通过但整体性能失败；32Player隔离／退出与Standalone恢复独立成功，native／guard／wrapper5保留性能失败。四张主相机人工检视只证明预热HUD，不作最终计数。[NaturalSmoke40最终读取汇总](../Reports/BlockGameplay20261017/NaturalSmoke40/natural-final-read-summary.json)另记录12/12自然流程观察门通过、两端退出0、180.0615134秒日志观测首局，host最终858／736块、revision397；完整包／HUD／多端积木oracle范围保留，不改网络或性能失败。所有runtime lane已结束，最终环境恢复与源码／包身份收尾已完成，71条owned启动ID及holder退出检查通过。完整Player验收尚未签收，具体框架／MVID边界见[40阶段记录](../Reports/BlockGameplay20261017/Validation.md)。39全部失败保持原阶段结论。

41→42同一探针的独立Player对照：低负载新建／拆支撑八档、每档30次均满足既定时延目标，修复前也已达标。持续300次×32格时，发布→激活P95由125.713降至58.350ms，入队→激活P95由158.962降至83.275ms；两轮各一次，不能推广为全部负载保证。八张新包下落原图均实际可见源块／柱。两轮在固定30秒排空后Quiet均为false，整体功能探针仍失败，不能把时延或显示通过当完整验收。详细数据及当前正式性能／网络状态以[本轮记录](../Reports/BlockFallStartup20261017/Validation.md)为准。
