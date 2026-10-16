# 积木对战调参说明

本文说明当前源码的玩法参数和修改入口，供后续平衡调整与包交付使用。实际构建、功能、联机和性能的完成情况见[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)，不由本文判定。当前验收范围以[高塔与级联整组后移修订](../Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md)为准；旧报告保留各自的包和源码归属。

2026-10-16 的方块子弹显示修复已构建为 Windows37，并通过包内九主武器双阵营18例显示检查，见[验证报告](../Reports/BlockTerrain/CubeProjectileRepair20261016/Validation.md)。飞行弹丸由 [BlockFlightPresentation](../Assets/Splatoon/Runtime/Combat/BlockFlightPresentation.cs)绘制；[BlockProjectileInstanced 材质](../Assets/Splatoon/Runtime/Blocks/Resources/BlockProjectileInstanced.mat)必须保留在 Resources 中并开启实例化，以保留 Player 使用的着色器变体。本轮没有调整武器或建造参数；显示通过不代表性能和联机门槛通过。

## 固定玩法约定

积木边长为 **0.10 米**，使用 **16³ 格**稀疏区块，每区块覆盖边长 1.6 米的空间。建造沿用地图水平边界，顶界为基准地面以上 **32 米**，不设局部堆高限制。这些数值是玩法与协议约定，不能作为性能调参项。

贴地图墙面的首层锚固；突出部分需要下方支撑。失去支撑的连续竖列共享下落运动，并逐格保留阵营。下落块可被攻击，但不承托角色；不增加液体扩散、自然消失、落块砸伤或天花板附着。出生与换装保护区覆盖的整格竖直列禁止两队生成积木，不额外创建碰撞屋顶。

三分钟结束时按双方存活积木总数判定胜负，相同则平局。已形成的静态块与下落块都计数，重力移动和重新落定不改变分数；飞行弹丸与装饰碎片不计数。普通射击命中地图建造首层，命中己方块沿命中面外侧堆积，命中敌块拆敌后结束本次命中。

实现入口为 [BlockWorld](../Assets/Splatoon/Runtime/Blocks/BlockWorld.cs)、[BlockTerrain](../Assets/Splatoon/Runtime/Blocks/BlockTerrain.cs)和[比赛积木同步与计分](../Assets/Splatoon/Runtime/Prototype/PrototypeMatch.BlockSync.cs)。占据、阵营、显示、Collider 与分数在统一提交边界发布，查询只读取已发布地形。

## 全局参数

修改源文件 [TbGlobal.xlsx](../Config/Luban/source/TbGlobal.xlsx) 的 `Global` 工作表；新增字段还需同步修改 [gameplay.xml](../Config/Luban/source/Defines/gameplay.xml)。运行时经生成的 `GlobalConfig`、`GameplayConfig.Global` 和 [BlockGameplaySettings](../Assets/Splatoon/Config/BlockGameplaySettings.cs)读取。下表对应当前 [tbglobal.json](../Assets/GameResource/Bootstrap/Config/Luban/tbglobal.json) 的值。

| 源表及 JSON 字段 | 当前值 | 调整作用 |
|---|---:|---|
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

地面与墙面弦化只允许稳定己方积木。连续支撑的局部坡面同时驱动移动、纸片姿态与受击体，真实碰撞仍按积木形状，拟合不能跨越空洞。支撑被拆除或进入下落后，下一次移动模拟退出附着；空中形态与翻越流程保留。

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
| 渲染与帧时 | 当前电脑，房主正常主相机真实 1920×1080，七个独立无图形客户端参与；帧时 P95≤15.5 ms、P99≤16.67 ms |
| 普通碰撞发布 | 真实主攻击命中至碰撞发布 P95≤200 ms |
| 八次同时爆破 | 完整目标与敌友半径保持，全部直接拆除≤2000 ms |
| 六组联机 | 2／8 个独立 Player，各测 0 ms／0%、100 ms／1%、200 ms／3%；包括迟加入、重连、普通下落、清空重建及逐项一致性 |
| 弱网移动纠正 | 100 ms／1%，P95≤0.15 米、P99≤0.60 米；充分真实移动与己方积木支撑样本，不能用视觉诊断代替纠正统计 |
| 包与说明 | 同源正式资源及 Windows 包、中文 HUD、来源检查、真实验证报告、已知风险和调参说明 |

`tower32m` 与 `cascade20k` 两场及其专属最终碰撞、压力与性能验收整体为 **user-deferred-not-accepted**，本次不阻挡 Goal，不能记为 Passed。普通功能和联机中的重力、支撑、守恒、碰撞、恢复以及 32 米玩法顶界仍必须保留。真实多机与人工观感两项外部验收不计 Goal。

首版运行显式使用 `prototype-v1`，偶发长帧保留完整原样本并列为风险。原 `strict` 的十八组网络、稳态 600 秒、普通发布 80 ms、爆破 250 ms 等指标保留各自语义，不将首版通过写成严格验收通过。不得通过扩大格尺寸、降低规模、丢格或截断爆破范围取得通过；单次短跑和诊断入口也不能替代正式采样。

联机完成需同时核对 `networkProfilePassed`、`requiredProfileMatrixComplete` 与 `caseProfilesMatch`；性能需核对当前四场适配入口产出的 `performancePassed`、`requiredWorkloadsComplete`、范围记录及全部风险。基础 `Tools/BlockTerrain/performance_acceptance.py` 仍默认历史六场，不能直接用它的六场完整性字段判定当前四场范围。

当前 Windows36 使用[同包验收 Revision02](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/README.md)中的[四场性能入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-four-case-performance-causal36.candidate.py)、[自然三分钟比赛入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-natural-smoke-causal36.candidate.py)和[六组联机入口](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/launch-network-causal36.candidate.py)。三者均使用同一份[实际包身份](../Reports/BlockTerrain/KnockbackFixtureWindows36Preparation01/package-identity.json)和已经完整来源核验的[实际绑定](../Reports/BlockTerrain/KnockbackFixtureAcceptancePreparation01/Revision02/actual-binding.json)，不得替换包文件后沿用原身份或覆盖既有运行目录。每一轮的实际运行、退出、隔离和偏好恢复均结束并核对后，才执行下一轮。

2026-10-16，Windows36 正式资源与 Player 构建、完整包检查及实际来源绑定已完成；本轮 22 项关联回归的通过覆盖来自原 15 项、fresh200 六项及 fresh201 一项的真实结果及恢复证据。此覆盖不表示完整程序集在最终图重跑。自然比赛、性能、网络、中文 HUD 和最终交付分别依据对应实际终态判定，最新进展见[首版状态](../Reports/BlockTerrain/FirstPlayableStatus20261013.md)。旧包、旧入口及历史失败保持各自归属。各摘要只代表对应验收范围，整个 Goal 还需功能、同源包与交付证据闭合。
