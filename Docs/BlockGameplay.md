# 积木建造与拆毁

44阶段已应用九文件定向生产修复、精确目标新测试及historical lazy clone；原8 sources／20 effective files组合编译零error／warning。lazy前source9的原四回归通过，原组合T1／T3均失败，各3次budgetDeferred、0served、0attributedPending，保留原结论。后续Tests44DeferredDiagnostic02的T1诊断重跑确证1次deferred changed progress实际发布（deferredProgress1／NoChange0／Published1，phase0→2、proof false→true、mutation0→1、checks538／prefix41）却未计目标服务；不推断其余2次deferred，也不回填原T1／T3路径。root已应用最小partial-progress修复＋仅repair24前缀补齐＋移除临时诊断三文件；此前FinalCandidateComposition44PartialPrefix01为10 sources／20 files，compile44-partial-prefix01完成、error／warning均0、compilationFailed=false，取得时点20源SHA／字节全匹配。修复后原T1／T3均真实Passed，两者served／attributedPending／published均1、unrelatedPublished均0；Full400原执行仍失败（旧公共Tick断言≤4096、实得8160）。现行无命令8192策略早于lazy；仅对齐该Tick合同的单文件测试修改已实际应用，运行时预算不改。此前FinalCandidateComposition44PolicyContract01为11 sources／21 files，compile44-policy-contract01完成、零error／warning，21源取得时点全匹配；该阶段首次idle读取超时保留，第二次于UTC11:40:05.2509507六状态false，runtime MVID保持、当时tests MVID为d2417278-33d0-41c0-9d13-730da6d3ba73。Tests44PolicyContract01合同对齐后Full400已Passed1/1，BlockHistoricalRecoveryTests整组14/14 Passed（failed／skipped／inconclusive均0）；原4096／8160失败保持原归属。原PolicyContract取消class实际1通过／1失败：Clear通过，AuthorityLoss在取消前line120因callbacks=1,0失败，权限取消路径尚未触发，不归类为生产取消缺陷；root现已应用仅测试的取消观察窗口修正，保留原提前完成断言，生产调度／预算不改；此前CancellationObservation为12 source stages／21 files，取消2/2 Passed；原Frame类2通过／1失败、line125的4192 DependentSpend期望True实False，runner32986退出1保持。仅加诊断的stage13单叶真实Failed1/1，frame9→9、checks4000→4000，terrainMs1024.5534→1024.6008已超1000ms、commandPresent=false，runner8002退出1，确证该次有限测试时钟干扰整数额度，并非生产checks额度拒绝；不回判原失败。root仅将UseIntegerIsolationPolicy的1000改为double.MaxValue，真实3ms独立测试、数字oracle及生产代码／预算保留；当前FinalCandidateComposition44FrameIntegerIsolation01为14 source stages／21 files，compile44-frame-integer-isolation01完成、errors／warnings均0，21源时点绑定全匹配；新idle01的HTTP400读取失败保留；本轮指定8叶已实际8/8 Passed（Frame3／Motion2／Fair3），runner57488退出0，两轮原Frame失败保持各自测试身份。root已完成4083文件源冻结（SHA256 `928ce0a4447ef811e2c2b0ec41a040aed87ee46d74d212b2308e61cab7bec310`），14/21组合与freeze审计21/21通过；44正式生产及Addressables构建已成功，4083冻结输入／359包文件完整审计和44项Shader审计通过，仅审核的link.xml／meta二件已归档，19项偏好恢复通过。Final44现已执行结束：240 low-load＋300 sustained共540/540完成，latencyTargetsPassed=true，但sustainedDrained=false、functionalPassed=false；sustainedAllCallbacksFull／cleanupJoined均true。launcher67985退出1，bootstrap37772／harness／Player退出2，正式reader的21项完整性全通过，但startupProbeAccepted=false、唯一失败谓词为sustainedDrained；八张原PNG已逐张看见cyan源列，最终4083源／359包身份零变化通过，均不改判功能失败。剩余1组startup未通过、4场正式性能未跑、6组网络未跑；43包不覆盖44。43冻结阶段的`WeaponSimulationVersion=38`。已补journal-local成功准入归属、取消边界测试及96-cohort夹具，并应用FairPaidExact夹具；最新复编译success=true、error／warning均0、compilationFailed=false，尚未验收。保留42的列启动优先、同帧共享预算及扩散让出机制；新增仅针对真实`BuildMovingOverlap`（reason18）重试的独立重力处理机会，在恢复全部原命令checkpoint后，为仍等待的同一FIFO头推进一次受原预算约束的重力工作。活跃fair owner存在时不介入，reason8（`OwnActivationGenerationChanged`）路径未改。8米顶界、9.81米／秒²重力、0.25秒扩散、墙面首层锚固和守恒规则保留。各版本的Editor、同源包、启动时延、正式性能及网络结果见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)。

42包正式四场已完整采样780.0280202秒，帧时P95/P99均通过45fps档，但仅blast8、scattered100k通过，整体性能失败：steady／combat普通发布P95分别为130583.333／248733.333ms，combat八人持续射击覆盖也未达标。steady40→42存在严重排队／重试回归；42网络未跑，43早期自然单叶已由失败转为通过，未证明墙钟更快；收紧前Core01终态69叶66通过／3失败，旧失败raw详情缺口保留。Reviewed01原14叶计划未闭合，首个Fair叶实测失败（全局完成增量预期8、实得12）；FairPaidExact后Reviewed02最终针对性7/7通过，不代表最终源码完整69叶全通过；七叶末尾idle已确认；43正式构建成功，4081原输入未变、359文件完整包审核及44项Shader审核通过。Final43共540/540完成、时延目标通过，但Quiet排空仍使功能探针失败（退出2）；42→43持续入队→激活P95为83.2745→92.2492ms，发布→激活58.3501→66.7444ms，不证明43更快。43同包正式四场已完整采样780.0180534秒，2/4通过、整体性能失败；steady／combat普通发布P95为167500.000／262983.333ms，peakPending为20174／33447，均只有ordinaryPublishLatency阻断失败。四场帧P95/P99依次为14.864/20.155、17.362/25.495、9.740/12.703、10.307/13.269ms，均符合45fps；blast8的8/8爆破408.6826ms完成，scattered100k通过。performance reader完整性／进程隔离／19项Standalone恢复通过，28个client退出1及两个失败host退出5保留。root实际查看四张热身PNG，中文HUD／terrain可见，不证明图像时序或最终计数。43最终4081源／359包身份零变化通过；startup reader的21项完整性通过，但startupProbeAccepted=false。43六组网络未跑，整个Editor／环境恢复仍须独立收尾。41→42隔离探针的时延改善和源块显示通过不改变Quiet排空失败，历史gameplay40性能2/4、网络0/6通过保持原包归属。

2026-10-17：当前源码按本轮批准方案将全局顶界调整为 8 米，加入保守扩散、空白表面弦化与爬墙，以及三把爆炸武器的弹丸尺寸和爆炸显示恢复。性能新增显式 `prototype-v2-45fps` 档，要求帧时 P95≤22.2 ms、P99≤33.3 ms；这是约 45fps 的验收目标；gameplay40同源正式四场现已完整实测780.2430886秒，整体性能未通过。普通无大量积压、单根 1～32 格竖列的隔离 Editor 试验已完成 24 次：支撑移除发布→下落激活的总体 P95＝14.94 ms、最大 208.29 ms，23/24 次≤100 ms。该小样本不是 Player、付费攻击或网络验收，没有配对的修改前基线，不能声明实测提升比例；分高度结果与测量边界见 [历史gameplay38～40记录](../Reports/BlockGameplay20261017/Validation.md)；41→42配对Player测量及43同包复测另见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)。历史 `prototype-v1` 和 `strict` 的语义与结果保留。

此前 [高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 中的 `tower32m`、`cascade20k` 及两场专属最终碰撞和性能验收仍标记 `user-deferred-not-accepted`。普通重力、守恒、付款、真实碰撞仍须验收。网络六组、稳态180秒、普通发布200ms、八爆2秒、弱网P99 0.60米仍适用；旧报告的32米顶界及原帧时要求仅描述其历史版本。

默认玩法使用 0.10 米积木。普通射击命中地图时建造首层，命中己方积木时向命中面外侧堆积，命中敌方积木时拆敌后结束本次命中。爆炸泼桶、快速爆破枪和爆泡霰弹枪只拆除，不建造。

积木占据、阵营和墙面锚固保存在 16³ 格稀疏区块中。边界沿用地图水平范围，顶界当前是基准地面以上 8 米，不是每个平台再往上 8 米。贴地图墙面的第一层不论下方有无支撑都保持锚固，突出层依赖下方支撑。失去支撑的连续竖列一起下落，保留逐格阵营。默认重力为 9.81 米／秒²，新激活普通竖列从零初速开始，已在下落的列被拆分时保留已有运动；激活等待和实际下落时间分别统计，详见调参说明。

下落开始前没有配置性的等待秒数；延迟来自支撑检查、落点证明、后台准备和原子发布排队。新建与拆支撑分别保留首次实际发布的起因时间，连续射击不刷新已经等待的起因；丢弃草稿不污染已提交时序。优先调度减少排队等待，激活仍须通过地图碰撞、落点与发布安全检查。FIFO内已有扩散任务保留顺序，持续稳定建造也不会永久阻断扩散。

垂直堆高后，每 0.25 秒调度一轮保守扩散：源列须连续稳定堆叠至少两格（可以包含底部锚固支撑），且源列与相邻列的顶面高度差大于 `blockSpreadMaxStepCells`，默认大于一格。每次只将一个非锚固顶格移向水平八方向相邻一格的较低位置，逐渐形成底宽顶窄的堆体。源、目标的有效支撑基底高度差不得超过该参数，默认最多一格；平台陡坎不会向外抛块，斜向移动还需至少一条完整的正交绕行路径。每源列每轮最多移出一格；本轮接收过迁移的目标列暂不再作为来源，即使其中到达块随后因拆支撑而下落到另一高度，也要等待下一轮；该列仍可继续接收其他来源。扩散移动保留阵营、数量和计分，不免费生成积木；落点受地图边界、静态几何、保护列和玩家占据约束，路径受地图实体和连续支撑路线约束，逐格落位按瞬时提交处理。有下落列时先等待稳定，墙面首层锚固格不移动。扩散轮会受后台预算与发布排队影响，0.25秒不代表整堆完成整形。没有自然消失、落块砸伤或天花板附着。出生和换装保护区对应的完整竖直列，从世界底界延伸至当前顶界，两队都不能生成积木。保护列按整格竖列与现有保护盒精确交叠判断，保留相邻空列；换装权限仍使用原有有限高度区域。这只限制生成，不添加碰撞屋顶，也不删除已有积木。

三分钟结束后按双方存活积木数量结算，数量相同为平局。下落块也计分；重力移动和重新落定不改变分数。飞行弹丸与装饰碎片不计分。

首版范围仍覆盖九把主武器、十三种副武器和五种大招。生成跳过玩家占据空间；落定导致玩家嵌入时，先寻找附近安全位置，再尝试最近安全点，最后回本队出生点附近的安全位置。下落块可以被攻击，但不承托角色；支撑被拆除或开始下落后，下一次移动模拟退出附着。坡面拟合不得跨越空洞，保留原空中形态及翻越流程，不增加天花板附着。

本轮真实角色完整弦化场景的最新 Editor 单叶已通过，包括自然训练场的入墙、下降后真实落地、重新接墙、跳离和翻越；生产共享地图根节点的裸地／裸墙接触误判已修正，修复后 LiveGeometry 完整回归 12/12 通过。首层锚固的窄墙端补强已应用，gameplay38冻结前指定关联复测为锚固 27/27、重力启动 8/8、摊开集成 4/4 及 7 个 Traversal 单叶全部通过，最终静态与编译空闲／MVID 回执已保存。这些分阶段结果没有替代最终源码同一次全量重跑、Player 或联机验收。墙脚展开水平纸片时，若完整轮廓与墙相交，仍会保守转入安全空中姿态再落地；持续背向移动撞向地图另一侧边界时，仍观察到落地候选反复被拒绝、重新进入安全空中姿态的有限已知行为，未宣称修复。原失败、实际通过及复测范围见[本轮验证记录](../Reports/BlockGameplay20261017/Validation.md)。

gameplay39历史验收阶段：已补同轮接收列保护，`WeaponSimulationVersion=35`，新规则回归 12/12、摊开集成 5/5 通过；集成终态只保存最后一叶，结合匹配运行的进度记录可追溯全部五个去重通过叶。39 的正式 Addressables＋Development Player 构建已成功，4067 个冻结输入保持原哈希，另有两个已审阅的 Addressables 生成物；359 文件包集合／大小／SHA 已独立复算一致，构建专用 19 项 Editor 偏好已恢复。39 自动显示18/18飞行、28/28爆炸及窗口隔离／Standalone恢复通过。主任务已实际检视39的34组control/sample：双方弹道和原爆炸可见，泡泡五弹在2m机位严重出框遮屏、副武器12m机位较小，不能据此签出完整轮廓或自然战斗观感。gameplay38 的构建和其两个生成物备份移出保持原阶段归属；38 首轮像素检查通过但窗口隔离审计失败（退出6），38b 隔离审计通过，18/18飞行叶和27/28爆炸叶通过，但吸盘炸弹队伍2零像素使整体退出2，未获完整视觉签收。主任务已检视38联系表，两队颜色及原爆炸 prefab 可见；泡泡在2m近机位出框、副武器12m机位较小，这些固定机位观察不等于自然战斗观感通过。39自动显示证据已保存；自然比赛两Player约180秒流程观察12/12门通过，完整包／自然HUD状态／多端积木oracle仍按原范围保留，39六组网络已完整结束，6/6均在穿行后的地形排空等待超时，`requiredProfileMatrixComplete=true`／`caseProfilesMatch=true`而`networkProfilePassed=false`；30个Player＋6个proxy的桌面隔离及Standalone恢复独立成功，30个owned Player全退出，wrapper／native退出4保留原网络失败；四场正式性能已完整采样780.3354543秒，首场steady250k完成后因帧P95=63.4490ms、P99=326.4898ms、普通发布P95=2200ms三项失败，第二场combat250k亦因帧P95=58.2051ms、P99=447.1885ms、普通发布P95=7400ms三项失败；第三场blast8完整120.009秒通过、8/8爆破316.0667ms，最后scattered100k完整180.0006秒通过、100000块保持；四场完整与profile匹配成立，整体性能仍失败，实际回执见本轮报告。

## 调参入口

面向后续平衡调整的完整说明见[积木对战调参说明](BlockTuningGuide.md)，其中按当前四场性能和六组联机范围整理了参数来源、共享预算、爆破半径、恢复及复测要求；本轮实际执行状态见[2026-10-17验证记录](../Reports/BlockGameplay20261017/Validation.md)，[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)保留历史版本归属。

全局值来自 `Config/Luban/source/TbGlobal.xlsx` 的 `Global` 工作表；改源表后运行 `cmd /c Config\Luban\gen_luban.bat`。生成的 C# 和 JSON 不能手改。格尺寸和区块尺寸仍是玩法及协议约定；顶界与扩散已纳入源表调参和内容签名，联机各端须使用一致配置。

字段定义在 `Config/Luban/source/Defines/gameplay.xml`；正式生成脚本输出到 `Assets/Splatoon/Config/Generated` 与 `Assets/GameResource/Bootstrap/Config/Luban`。全局实际产物为 `GlobalConfig.cs` 和 `tbglobal.json`，运行时由 `BlockGameplaySettings` 读取 `GameplayConfig.Global`。增改字段时同步维护源工作簿和定义，正式生成后核对源值、生成字段及 JSON 一致；只改生成文件不会形成可靠的调参来源。本节说明入口，不表示本轮已重新生成或完成运行验收。

| 源表字段 | 默认值 | 含义 |
|---|---:|---|
| blockWorldTopHeight | 8 | 相对基准地面的全局积木顶界，米 |
| blockSpreadInterval | 0.25 | 保守扩散调度间隔，秒 |
| blockSpreadMaxStepCells | 1 | 可跨越的支撑基底高度差上限，亦用于邻列顶面高差阈值，格；水平固定相邻一格 |
| blockSpreadMinStackCells | 2 | 源列连续稳定堆叠的最小格数，可包含底部锚固支撑 |
| blockBlocksPerMaterial | 32 | 每点实际支付材料的建造格数 |
| blockMainAttackMaxBlocks | 512 | 主攻击整组建造预算上限 |
| blockAuxiliaryBudgetFraction | 0.25 | 沿途和脚下共享预算比例 |
| blockFriendlyBlastRadiusFraction | 0.5 | 己方爆破半径相对敌方的比例 |
| blockBlocksPerSpecialPoint | 32 | 每点大招收益对应的实际变化格数 |
| blockMainSpecialPointCap | 2 | 主攻击整组大招收益上限 |
| blockSubSpecialPointCap | 10 | 副武器整次部署的大招收益上限 |
| blockNetworkBytesPerSecond | 2097152 | 房主全房间地形传输预算，字节/秒 |

每把武器沿英雄表的 `WeaponConfigPath` 找到 `WeaponConfigAsset`，在 Inspector 的“积木建造与拆除”分组调整 `blockBuild`、`blockEnemyRemoval` 和 `blockRemovalRadius`。主攻击由实际扣费建立预算，多弹丸、反弹和派生事件共享；部署物获得有限预算。普通拆敌半径默认 0.20 米。

英雄源表为 `Config/Luban/source/TbHero.xlsx`；主武器实际资产位于 `Assets/GameResource/Weapons/<英雄>/<英雄>WeaponConfig.asset`。主武器爆炸参数沿该资产的 `ammoConfig` 引用进入 `AmmoConfigAsset`；副武器参数位于 `Assets/GameResource/SubWeapons/<类型>/<类型>.asset`，大招参数位于 `Assets/GameResource/SpecialWeapons/<类型>/<类型>.asset`。这些配置资产保留既有参数链路，资产调参不能用修改 Luban 生成代码替代；需要改表内引用时仍修改源表并正式生成。调参后保持同一来源构建及验收，不能以旧包结果证明新参数通过。

| 武器 | 默认每次付费攻击拆敌格数 |
|---|---:|
| 专业模型枪 MG | 4 |
| 窄域标记枪 | 6 |
| 斯普拉射击枪 | 8 |
| 斯普拉旋转枪 | 每个实际发射弹 8 |
| 开尔文 525 | 12 |
| 满溢泡泡枪 | 四颗共享 32 |

爆破采用攻击配置半径，静态地图遮挡爆破，范围内积木不遮挡同一次结构爆破。先确定完整目标再分批提交，不截断范围。角色伤害独立结算。救援事件不改变积木。

稳定己方积木和稳定空白地图地面、墙壁都支持弦化，空白墙面也可爬墙。空白表面当前使用 `neutralSwimSpeed=3` 米／秒及普通 `recoverInk=10` 点／秒，不提供己方积木加速或 `swimRecoverInk=33.333332` 点／秒快速恢复。快速恢复须有当前稳定己方接触，空中或跨缝保留的移动来源不能单独取得资格；射击、副武器、毒雾和恢复锁定仍生效。敌方积木和下落块不允许附着。局部连续支撑拟合坡面，驱动移动、纸片姿态和受击体；真实碰撞保持积木形状。敌方积木不造成旧墨迹规则的持续掉血。大招只按实际建造和拆敌结算收益，拆己方、重力、扩散和大招自身不充能。

快速爆破枪、爆炸泼桶和爆泡霰弹枪的积木弹整体尺寸在各自 `AmmoConfigAsset.blockProjectileSize` 调整；单位为米、沿弹丸本地宽／高／长设置。爆炸同时播放原爆炸预制体和积木碎片，保持既有弹道、伤害与结构爆破半径。具体尺寸和资源入口见调参说明。

## 验证与构建

以下是当前完成条件的索引，结合本轮批准的 8 米顶界、扩散及 45fps 方案，历史后移范围见 [Goal范围修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md)。本表定义验收要求，不代表任何一类已通过。

| 类别 | 首版必须保留的范围 |
|---|---|
| 1. 功能稳定性与比赛 | 核心玩法硬门和变更关联回归通过，完整测试库保留集中执行证据；普通重力、积木守恒、真实碰撞、扣费共享预算、付款返还及原子兑现均正确，并完成自然三分钟比赛与回合流程。特定内部调度见证按已批准范围列为诊断，未决普通失败不能按诊断忽略。 |
| 2. 四场性能 | 显式 `prototype-v2-45fps`，当前电脑、1080p正常主相机、八独立Player；steady250k 180秒、combat250k 300秒、blast8 120秒、scattered100k 180秒，共780秒正式采样，原规模保持。帧时P95≤22.2 ms/P99≤33.3 ms，普通发布P95≤200 ms；八爆完整目标、敌友半径和规模保持，全部直接拆除≤2000 ms。普通小竖列下落激活P95≤100 ms另作功能时延验收，不能用帧时或发布指标代替。 |
| 3. 弱网纠正 | 100 ms/1%场景，35秒及充分真实移动和己方积木支撑回执；位置纠正P95≤0.15米/P99≤0.60米，恢复及一致性仍为硬检查。 |
| 4. 联机矩阵与恢复 | 同机2/8个独立Player × 三组RTT/丢包，共六组；迟加入、普通下落、重连、回合清空重建及位置、阵营、数量、区块哈希、计分一致。其余十二组后移。 |
| 5. Windows包与报告 | 核心硬门通过后构建同源候选，再以同一包完成性能和网络；包可启动、加载正式Addressables、完成比赛，中文HUD、协议/内容签名及来源一致，并附验证报告、已知风险及调参说明。 |

32米高塔和两万格级联的专属压力、最终碰撞及性能整组本次不验收，不记Passed；普通功能和联机中的支撑、下落、守恒、碰撞和恢复仍必须通过。真实多机和人工观感两项外部验收不计Goal。

完整基线和修复后回归保留各自真实 XML、失败与退出码。窄修后复测全部原未决普通用例、受影响回归及核心硬门；只有影响范围可可靠界定时才复用未受影响的基线通过证据。共享运行时或全局生命周期改动影响较广时扩大回归，不能将原失败改写为“完整库通过”。最终源码必须有实际验证，缺测不算通过。

复用当前项目 Editor，测试和构建串行。`BlockGameplayBuilder.ApplyWeaponDefaults()` 可以通过 Editor API 重新应用已批准的九把武器默认值；它会覆盖相应三个积木字段，调参后不要无意调用。`BlockGameplayBuilder.BuildWindowsTo(outputDirectory)` 使用正式 Addressables 内容及 Windows Development Player，开启帧时统计以供验收。输出目录应独立，避免覆盖正在使用的旧包。

当前下落启动修复的实际包、源码代次及分项结果见[下落启动修复记录](../Reports/BlockFallStartup20261017/Validation.md)，gameplay38～40阶段见 [历史2026-10-17验证记录](../Reports/BlockGameplay20261017/Validation.md)，[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)保留历史版本归属。每次构建关联真实核心收据、源码及元数据图、已加载程序集身份；构建成功后记录包内程序集和资源身份，并在包验收前后检查一致性。候选构建成功仅表示得到可验证的包，不能单独完成Goal。所有运行保持在隔离桌面，避免切换用户前台桌面。下文Windows21 V2命令保留为历史方法示例，执行前必须绑定实际新包及最新四场范围，旧包结果不转记新源码通过。

历史串行包验收示例为 [完整比赛请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-package-smoke-request.json)、[联机请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-network-request.json)和[D3D12性能请求](../Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json)，三份冻结请求均绑定 `CodexBlockTests20261013a` 隔离桌面及测试子进程 `SPLATOON_DISABLE_SYSTEM_CURSOR=1`。入口核对实际包身份及验收工具哈希，禁止后台锁定系统鼠标。这些入口说明不代表新包或包级验收已通过。

完整 NUnit XML 及关联启动、终态和恢复回执按执行目录保存在 `Reports/BlockTerrain`，历史 XML 另有 `UnityTests` 目录。`Tools/BlockTerrain/static-compile.py` 只验证缓存 Unity 引用下的 C# API，不代表 Editor 导入、运行或性能通过。

独立 Player 联机矩阵：

```powershell
python Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Candidates/launch-isolated-package21v2.py --request Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-network-request.json --execute
```

首版请求显式指定 `--acceptance-profile prototype-v1`，覆盖2/8 Player各0ms/0%、100ms/1%、200ms/3%共六组，包含迟加入、下落冻结、重连、清场和重建。每客户端需要充分实际移动回执及己方块支撑样本；100ms/1%时纠正P95≤0.15米、P99≤0.60米。省略profile的工具默认仍是`strict`十八组及P99≤0.35米。普通帧报告只作诊断，不替代同档性能验收。只有新包已构建、Editor回归和构建真正结束时才执行请求。

独立性能场景：

```powershell
python Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Candidates/launch-isolated-package21v2.py --request Reports/BlockTerrain/Windows21/FinalSourceCandidate02/Requests/isolated-performance-d3d12-request.json --execute
```

当前性能 harness 显式传 `--acceptance-profile prototype-v2-45fps`，Player 对应 `-blockAcceptanceProfile prototype-v2-45fps`，自动选择四场：25万格稳态180秒、25万格持续交火300秒、八次同时爆炸120秒、十万格零散结构180秒，共780秒正式采样，播种、同步及预热另计。新档直接校验四场完整性。历史 `prototype-v1` 在基础工具中仍保留六场语义，旧四场范围由相应历史 wrapper 实现；`strict` 仍为六场、稳态600秒。高塔和两万格级联整组后移。房主正常主相机以1080p真实渲染，七个独立无图形客户端提供输入；报告只证明当前电脑上的房主渲染负载。`--diagnostic-seconds`短跑始终不能通过正式门槛。

当前45fps性能门槛：帧时P95≤22.2 ms、P99≤33.3 ms；普通命中至碰撞发布P95≤200 ms，八次同时爆破直接拆除≤2000 ms。普通小竖列另有24次隔离Editor实测：支撑移除发布→下落激活总体P95＝14.94ms、最大208.29ms、23/24次≤100ms；小样本不含整段落地时间，不代表Player或普通攻击发布，没有配对修改前基线，分高度数据见[验证记录](../Reports/BlockGameplay20261017/Validation.md)。普通功能和联机中的下落仍须守恒并最终落定；后移的高塔和级联专属验收不阻挡本次Goal。偶发长帧与极限队列清空时间保留全部样本，列为优化记录；新档P99门槛仍阻断通过。历史prototype-v1保留15.5/16.67 ms帧时要求，strict档另保留无地形引起>33.3 ms卡顿、普通发布80 ms、爆破250 ms、清队列500 ms的原门槛。不能通过减少格数、丢格或截断爆破范围通过。全帧尖峰的地形归因需结合地形计时和Profiler。历史gameplay39包的八独立Player四场正式性能已完成780.3354543秒实测：steady250k首场完整180.176秒、289731～320641块，帧P95=63.4490ms、P99=326.4898ms、普通发布P95=2200ms，三项阻断失败；574.1417ms最大帧另列诊断风险。第二场combat250k完整300.149秒，帧P95=58.2051ms、P99=447.1885ms、普通发布P95=7400ms，三项阻断失败，754.9658ms最大帧另列诊断。第三场blast8完整120.009秒通过，帧P95=11.4245ms、P99=16.2191ms、8/8爆破316.0667ms，83.4748ms最大帧仍列诊断；最后scattered100k完整180.0006秒通过、100000块保持；四场requiredWorkloadsComplete／caseProfilesMatch=true，但performancePassed／fullAcceptancePassed=false。最后场P95=9.8588ms、P99=12.2411ms、最大352.5257ms仍列诊断；32次启动隔离审计和偏好恢复均成功，外层退出5透传前两场失败。没有配对修改前基线，不宣称改进比例或把失败归因于摊开。

历史gameplay40正式性能另有780.2430886秒完整四场：steady三项阻断失败、combat仅普通发布延迟失败、blast8与scatter通过，整体未通过；详见[40性能读取汇总](../Reports/BlockGameplay20261017/Performance45fps40/performance-final-read-summary.json)。上述780.3354543秒及其指标归属39历史包。

证据写入 `Reports/BlockTerrain`，Windows 包构建成功、单元测试、网络哈希一致、帧时通过各自是独立结论。当前迁移验收状态见该目录的 `Progress.md`。真实多机联机与人工操作观感是两项外部验收，按用户要求不计入当前 Goal 完成条件，单列待验收；同机结论只适用于当前电脑。保留全部偶发长帧和极限坍塌时间，明确后移十二组网络组合及旧 `strict` 指标未获首版证据覆盖，不将旧失败改判，也不宣称原严格验收已经通过。

后续gameplay40是独立修复与复验阶段：主任务已定位扩散搜索可能持续抢占重力推进机会的源码路径，将保留39原始失败，先保存修复前失败证据，再做窄公平性修复、修复后回归及新的冻结／正式构建／同包验收。该路径尚不足以单独解释39全部网络或性能失败；40支持文件的静态准备不代表修复已验收、40已构建或通过。本段为39终态记录时的后续计划，不替代后来40实际回执。

进度补记（独立gameplay40阶段）：[39生成物归档回执](../Reports/BlockGameplay20261017/Build39/GeneratedBuildOutputs/removal-receipt.json)保存两项link原件／SHA，原路径已移出；主任务随后确认原4067冻结输入仍匹配，再仅应用新增测试。真正的修复前失败来自`Tests40BeforeFix02`的1个Golden单叶（sawRealMotion=false），初次reload HTTP400只是dispatch失败；[生产应用回执](../Reports/BlockGameplay20261017/Tools/SpreadGravityFairness40/production-applied.json)现已记录两处窄公平性生产候选及WeaponSimulationVersion35→36，运行预算未改。[Golden前后独立汇总](../Reports/BlockGameplay20261017/Golden40/golden-before-after-summary.json)现已确认同一单叶修复前1失败→修复后1通过，实际UTF叶耗时58.476709s→53.0749285s；after wrapper60.2s另列，不当作test耗时。progress001读取超时不是断言失败，progress002当时仍运行且无结果，通过证据来自最终terminal。[三类关联回归汇总](../Reports/BlockGameplay20261017/Tests40Related/associated-regression-summary.json)已记录SpreadIntegration6/6、GravityStartup8/8、SpreadRules12/12通过；Integration终态只留末叶，由同job progress012的5个去重Passed叶补齐6叶证据，不把10条缓存记录当10通过。[额外8叶原终态汇总](../Reports/BlockGameplay20261017/Tests40Scheduling/scheduling-regression-summary.json)现为7通过／1失败；SupportRetryWindow原类2通过／1失败，首异常与整队列Single选择假设一致，后续teardown回调不证明paid绕过scan。fixture仅改为选择唯一非spread命令并保留其余断言，适配后同叶实际1/1通过；[34个不同叶关联闭合汇总](../Reports/BlockGameplay20261017/Tests40Related/related-regression-closure-summary.json)记录26＋8个FullName当前通过，原7／1及原类2／1保持历史，独立Golden和定向复测不增加用例数。生产runtime仍36；[Build40构建与绑定读取汇总](../Reports/BlockGameplay20261017/Build40/build-and-binding-read-summary.json)现已记录正式构建、4067原输入／359包文件审计及19项偏好恢复成功，CLI外层timeout退出1未重dispatch。[Network40最终汇总](../Reports/BlockGameplay20261017/Network40/network-final-read-summary.json)现已记录六组完整结束、profile一致但全部drain超时失败；30Player＋6proxy隔离与退出、Standalone恢复独立成功，native／guard／wrapper4保留原网络失败。首组历史采样持续推进、19共同revision字段一致，仍未排空，不能推广全部case根因。[Visual40读取汇总](../Reports/BlockGameplay20261017/VisualFlight40/visual-final-read-summary.json)另记录18/18飞行、12/12主爆炸、16/16副爆炸自动通过及34组对照人工可见性检视，泡泡近机位出屏和12m副武器较小的限制保留；[Performance40最终读取汇总](../Reports/BlockGameplay20261017/Performance45fps40/performance-final-read-summary.json)记录四场完整采样780.2430886秒，steady三项阻断失败、combat仅普通发布P95失败，blast8／scatter通过但整体性能失败；32Player隔离／退出与Standalone恢复独立成功，native／guard／wrapper5保留性能失败。四张主相机人工检视只证明预热HUD，不作最终计数。[NaturalSmoke40最终读取汇总](../Reports/BlockGameplay20261017/NaturalSmoke40/natural-final-read-summary.json)另记录12/12自然流程观察门通过、两端退出0、180.0615134秒日志观测首局，host最终858／736块、revision397；完整包／HUD／多端积木oracle范围保留，不改网络或性能失败。所有runtime lane已结束，最终环境恢复与源码／包身份收尾已完成，71条owned启动ID及holder退出检查通过。完整Player验收尚未签收，具体框架／MVID边界见[40阶段记录](../Reports/BlockGameplay20261017/Validation.md)。39全部失败保持原阶段结论。

本轮同一探针的独立Player对照：低负载新建／拆支撑八档、每档30次均满足既定时延目标，修复前也已达标。持续300次×32格时，发布→激活P95由125.713降至58.350ms，入队→激活P95由158.962降至83.275ms；两轮各一次，不能推广为全部负载保证。八张新包下落原图均实际可见源块／柱。两轮在固定30秒排空后Quiet均为false，整体功能探针仍失败，不能把时延或显示通过当完整验收。详细数据及当前正式性能／网络状态以[本轮记录](../Reports/BlockFallStartup20261017/Validation.md)为准。
