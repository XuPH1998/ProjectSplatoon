# 积木建造与拆毁

2026-10-15：当前交付采用用户批准的 `prototype-v1` 首版验收，最新范围以 [高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 为准，覆盖此前 [Goal首版范围](../Reports/BlockTerrain/GoalScopeFirstPlayable20261012.md) 的对应要求。32米高塔、两万格级联及两场专属最终碰撞和性能验收整体后移，标记 `user-deferred-not-accepted`；普通重力、守恒、付款、真实碰撞及32米玩法顶界保留。网络六组、稳态180秒、普通发布200ms、八爆2秒、弱网P99 0.60米仍适用。偶发长帧保留为优化风险。下文 `strict` 的十八组和原性能数字用于后续严格验证。

默认玩法使用 0.10 米积木。普通射击命中地图时建造首层，命中己方积木时向命中面外侧堆积，命中敌方积木时拆敌后结束本次命中。爆炸泼桶、快速爆破枪和爆泡霰弹枪只拆除，不建造。

积木占据、阵营和墙面锚固保存在 16³ 格稀疏区块中。边界沿用地图水平范围，顶界是基准地面以上 32 米。贴地图墙面的首层固定，突出层依赖下方支撑。失去支撑的连续竖列一起下落，保留逐格阵营；没有液体扩散、自然消失或落块砸伤。出生和换装保护区对应的完整竖直列，从世界底界延伸至 32 米顶界，两队都不能生成积木。保护列按整格竖列与现有保护盒精确交叠判断，保留相邻空列；换装权限仍使用原有有限高度区域。这只限制生成，不添加碰撞屋顶，也不删除已有积木。

三分钟结束后按双方存活积木数量结算，数量相同为平局。下落块也计分；重力移动和重新落定不改变分数。飞行弹丸与装饰碎片不计分。

首版范围仍覆盖九把主武器、十三种副武器和五种大招。生成跳过玩家占据空间；落定导致玩家嵌入时，先寻找附近安全位置，再尝试最近安全点，最后回本队出生点附近的安全位置。下落块可以被攻击，但不承托角色；支撑被拆除或开始下落后，下一次移动模拟退出附着。坡面拟合不得跨越空洞，保留原空中形态及翻越流程，不增加天花板附着。

## 调参入口

面向后续平衡调整的完整说明见[积木对战调参说明](BlockTuningGuide.md)，其中按当前四场性能和六组联机范围整理了参数来源、共享预算、爆破半径、恢复及复测要求；实际执行状态仍以[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)为准。

全局值来自 `Config/Luban/source/TbGlobal.xlsx` 的 `Global` 工作表；改源表后运行 `cmd /c Config\Luban\gen_luban.bat`。生成的 C# 和 JSON 不能手改。格尺寸、区块尺寸和顶界属于玩法及协议约定，不是这些数值的调参项。

字段定义在 `Config/Luban/source/Defines/gameplay.xml`；正式生成脚本输出到 `Assets/Splatoon/Config/Generated` 与 `Assets/GameResource/Bootstrap/Config/Luban`。全局实际产物为 `GlobalConfig.cs` 和 `tbglobal.json`，运行时由 `BlockGameplaySettings` 读取 `GameplayConfig.Global`。增改字段时同步维护源工作簿和定义，正式生成后核对源值、生成字段及 JSON 一致；只改生成文件不会形成可靠的调参来源。本节说明入口，不表示本轮已重新生成或完成运行验收。

| 源表字段 | 默认值 | 含义 |
|---|---:|---|
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

只有稳定己方积木支持地面或墙面弦化及快速恢复材料。局部连续支撑拟合坡面，驱动移动、纸片姿态和受击体；真实碰撞保持积木形状。敌方积木不造成旧墨迹规则的持续掉血。大招只按实际建造和拆敌结算收益，拆己方、重力和大招自身不充能。

## 验证与构建

以下是当前五类完成条件的索引，完整约定以 [最新Goal范围修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 为准。本表定义验收要求，不代表任何一类已通过。

| 类别 | 首版必须保留的范围 |
|---|---|
| 1. 功能稳定性与比赛 | 核心玩法硬门和变更关联回归通过，完整测试库保留集中执行证据；普通重力、积木守恒、真实碰撞、扣费共享预算、付款返还及原子兑现均正确，并完成自然三分钟比赛与回合流程。特定内部调度见证按已批准范围列为诊断，未决普通失败不能按诊断忽略。 |
| 2. 四场性能 | 当前电脑、1080p正常主相机、八独立Player；steady250k 180秒、combat250k 300秒、blast8 120秒、scattered100k 180秒，原规模保持。帧时P95≤15.5 ms/P99≤16.67 ms，普通发布P95≤200 ms；八爆完整目标、敌友半径和规模保持，全部直接拆除≤2000 ms。 |
| 3. 弱网纠正 | 100 ms/1%场景，35秒及充分真实移动和己方积木支撑回执；位置纠正P95≤0.15米/P99≤0.60米，恢复及一致性仍为硬检查。 |
| 4. 联机矩阵与恢复 | 同机2/8个独立Player × 三组RTT/丢包，共六组；迟加入、普通下落、重连、回合清空重建及位置、阵营、数量、区块哈希、计分一致。其余十二组后移。 |
| 5. Windows包与报告 | 核心硬门通过后构建同源候选，再以同一包完成性能和网络；包可启动、加载正式Addressables、完成比赛，中文HUD、协议/内容签名及来源一致，并附验证报告、已知风险及调参说明。 |

32米高塔和两万格级联的专属压力、最终碰撞及性能整组本次不验收，不记Passed；普通功能和联机中的支撑、下落、守恒、碰撞和恢复仍必须通过。真实多机和人工观感两项外部验收不计Goal。

完整基线和修复后回归保留各自真实 XML、失败与退出码。窄修后复测全部原未决普通用例、受影响回归及核心硬门；只有影响范围可可靠界定时才复用未受影响的基线通过证据。共享运行时或全局生命周期改动影响较广时扩大回归，不能将原失败改写为“完整库通过”。最终源码必须有实际验证，缺测不算通过。

复用当前项目 Editor，测试和构建串行。`BlockGameplayBuilder.ApplyWeaponDefaults()` 可以通过 Editor API 重新应用已批准的九把武器默认值；它会覆盖相应三个积木字段，调参后不要无意调用。`BlockGameplayBuilder.BuildWindowsTo(outputDirectory)` 使用正式 Addressables 内容及 Windows Development Player，开启帧时统计以供验收。输出目录应独立，避免覆盖正在使用的旧包。

实际候选包、源码代次、已执行结果和当前入口统一见 [首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)。每次构建关联真实核心收据、源码及元数据图、已加载程序集身份；构建成功后记录包内程序集和资源身份，并在包验收前后检查一致性。候选构建成功仅表示得到可验证的包，不能单独完成Goal。所有运行保持在隔离桌面，避免切换用户前台桌面。下文Windows21 V2命令保留为历史方法示例，执行前必须绑定实际新包及最新四场范围，旧包结果不转记新源码通过。

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

首版当前串行运行四场：25万格稳态180秒、25万格持续交火300秒、八次同时爆炸120秒、十万格零散结构180秒。高塔和两万格级联整组后移；strict档的稳态仍为600秒。房主正常主相机以1080p真实渲染，七个独立无图形客户端提供输入；报告只证明当前电脑上的房主渲染负载。`--diagnostic-seconds`短跑始终不能通过正式门槛。

首版性能门槛：帧时P95≤15.5 ms、P99≤16.67 ms；普通命中至碰撞发布P95≤200 ms，八次同时爆破直接拆除≤2000 ms。普通功能和联机中的下落仍须守恒并最终落定；后移的高塔和级联专属验收不阻挡本次Goal。偶发长帧与极限队列清空时间保留全部样本，列为首版优化记录。strict档继续保留无地形引起>33.3 ms卡顿、普通发布80 ms、爆破250 ms、清队列500 ms的原门槛。不能通过减少格数、丢格或截断爆破范围通过。全帧尖峰的地形归因需结合地形计时和Profiler。

证据写入 `Reports/BlockTerrain`，Windows 包构建成功、单元测试、网络哈希一致、帧时通过各自是独立结论。当前迁移验收状态见该目录的 `Progress.md`。真实多机联机与人工操作观感是两项外部验收，按用户要求不计入当前 Goal 完成条件，单列待验收；同机结论只适用于当前电脑。保留全部偶发长帧和极限坍塌时间，明确后移十二组网络组合及旧 `strict` 指标未获首版证据覆盖，不将旧失败改判，也不宣称原严格验收已经通过。
