# 五把武器墨弹与涂色参数核对

本文保留初次核对时的历史基线。后续修正、完整字段对照和未完成项见 [11.3.0 实施记录](Weapon-Reference-Implementation.md)，不要将本页旧值视为当前运行时配置。

日期：2026-09-13。项目基线：`bca490345e866e6373874b7b439373bd8de52f92` 的五把武器。对照版本固定为 **Splatoon 3 11.3.0**（数据库目录 `1130`），不声称它是游戏最新版本。参考为 Lean 公开提取的 Versus 武器数据，不是任天堂提供的 Unity 配置规范。

## 结论

目前不能把这五把武器的墨弹/涂色参数认定为已对齐原作。已确认的主要问题在机制：普通枪的近中远墨迹统一变成随机圆形；五把枪共用落墨间距和大小；狙击枪的笔刷不随蓄力变化；标称涂地射程未参与运行时涂地计算。`9.8` 重力、`0.55` 硬度和 `1` 强度不能仅按数值判为错误，必须考虑单位和各自算法。

本次只核对，不修改 `TbWeapon.xlsx`、武器生成数据、弹道或涂墨算法。

## 方法、单位与证据边界

- 项目 Excel 与生成 JSON 逐字段相同；报告记录当前实际值。源码字段的米、秒单位来自项目结构定义；参考帧由 `WeaponSimulation.ReferenceRate=60` 换算。
- 外部 JSON 数字按**原始内部单位**保留。当前资料未建立这些提取值到本项目米制的可靠空间标定；不可将 `FreeGravity=0.016` 直接替换为 `0.016 m/s²`。假设某原始加速度单位确为 U/帧²、空间比例为 s 米/U，则换算为 `g × 60² × s`，但该假设及 s 均未验证。
- JSON 是类型化参数覆盖，未出现的字段不等于 0。继承默认值、具体阶段转移和默认阻力仍须追查类型默认值或原作轨迹测量。
- 普通枪和蓄力枪分别使用 `spl__BulletSimpleMoveParam`、`spl__BulletChargerMoveParam` 等不同类型；不能用普通枪模型证明狙击枪正确。
- 未进行原作实机逐帧采样或本次 Unity 武器手感验收；关于原作下落运动的具体算法不由参数名单独推断。

## 当前共同参数与消费路径

| 项目字段 | 五把枪当前值 | 实际消费 | 结论 |
|---|---|---|---|
| gravity | 9.8 m/s² | `InkBallistics.Position`：直飞结束后施加 `0.5*g*t²` 下落 | 需结合单位、初速、减速与弹道实测 |
| straightFrames / brakeFrames / brakeSpeedMultiplier | 4 / 8 / 0.66 | `TravelTime`：直飞后 8 帧线性减速至初速 66%，之后保持 | 不能用与原作局部近似的一个乘数证明阶段相同 |
| lifetime | 1.2 秒 | `InkProjectileService.Simulate` 结束弹道 | 联合判断真实涂地距离 |
| paintRadiusMin / paintRadiusMax | 0.65 / 0.8 m | `Resolve` 使用种子随机取圆形笔刷半径，不依距离或蓄力 | 明确未表达原作距离/蓄力形状变化 |
| paintHardness / paintStrength | 0.55 / 1 | `InkBrush.Coverage` 与 `InkTexturePainter.shader` 的 smoothstep 覆盖 | 在已查武器覆盖数据中无直接一一对应字段 |
| trailSpacing / trailRadius / trailMaxDrop | 0.7 / 0.58 / 2.4 m | `PaintTrail` 向下射线命中立即涂色；无独立墨滴运动 | 当前的简化落墨模型，非已验证原作复刻 |
| paintRange / chargeMinPaintRange | 见逐枪表 | `WeaponDisplay` 展示、`GameplayConfig.Validate` 校验 | 未进入弹道或涂地裁剪；不是当前有效的涂地上限 |

源码：[`InkProjectileService`](../../Assets/Splatoon/Runtime/Combat/InkProjectileService.cs)、[`WeaponSimulation`](../../Assets/Splatoon/Runtime/Combat/WeaponSimulation.cs)、[`PaintSurface / InkBrush`](../../Assets/Splatoon/Runtime/Painting/PaintSurface.cs)、[`InkTexturePainter`](../../Assets/Splatoon/Runtime/Painting/InkTexturePainter.shader)、[`WeaponDisplay`](../../Assets/Splatoon/Runtime/Combat/WeaponDisplay.cs)。

### 笔刷覆盖与沿途落墨的实际含义

归属阈值为 `TbGlobal.paintThreshold=0.5`。对孤立单次盖章，强度 1、硬度 0.55 时阈值位于 smoothstep 中点，有效归属半径约为 `0.775 × 配置半径`：命中为 **0.50375～0.62 m**，沿途为 **0.4495 m**。这仅是当前平面理想模型的单次归属范围；重复叠加、表面法线、遮挡、网格采样和显示着色会影响最终结果，不能当作原作涂色半径。

`trailSpacing` 判断的是“上次落墨位置到当前子步末端的三维距离”，超过阈值后只盖一次章，没有按精确等距插值补点。因此 0.7 m 是触发阈值，不保证每个墨点恰好相隔 0.7 m。发射时还会在枪口后方 0.6 m 尝试近身盖章。`trailMaxDrop=2.4` 是即时向下射线的最大长度，不是下落速度、重力或寿命。

## 逐枪对照

### 1. 标准射击枪 → Splattershot

原始数据：[11.3.0 WeaponShooterNormal](https://leanny.github.io/splat3/data/parameter/1130/weapon/WeaponShooterNormal.game__GameParameterTable.json)；本地快照：[WeaponShooterNormal.1130.json](WeaponShooterNormal.1130.json)。以下字段路径均从 `GameParameters` 开始。

| 项目 | 当前值 / 实现 | 原作原始字段和值 | 判断与建议 |
|---|---|---|---|
| 弹道与重力 | gravity=9.8；初速=31 m/s；直飞=4 帧，减速=8 帧至 0.66 倍 | `MoveParam`: {"$type": "spl__BulletSimpleMoveParam", "FreeGravity": 0.016, "GoStraightStateEndMaxSpeed": 1.493, "GoStraightToBrakeStateFrame": 4, "SpawnSpeed": 2.266} | 需换算/实测；按完整类型和阶段对照，不能只改重力 |
| 命中涂色 | 半径随机 0.65～0.8 m，圆形 | `PaintParam`: {"$type": "spl__BulletShooterPaintParam", "DepthScaleMax": 2.24, "DepthScaleMaxBreakFree": 2.24, "DepthScaleMin": 1.31, "DepthScaleMinBreakFree": 1.12, "DistanceMiddle": 1.1, "WidthHalfFar": 1.71, "WidthHalfMiddle": 1.93, "WidthHalfNear": 1.93} | 明确机制不一致：应区分距离对应宽度和纵向形状，而非随机圆形 |
| 笔刷硬度 / 强度 | 0.55 / 1，参与覆盖和归属 | 在此武器数据中未找到与当前 smoothstep 硬度、强度直接对应的参数 | 无直接对应依据；结合墨迹边缘、实际归属范围标定 |
| 落墨生成 | 0.7 m 触发阈值；额外近身盖章 | `SplashSpawnParam`: {"$type": "spl__BulletSplashShooterSpawnParam", "ForceSpawnNearestAddNumArray": [4], "SpawnBetweenLength": 9.2, "SpawnNearestLength": 1.2, "SpawnNum": 1.5, "SplitNum": 8} | 原作存在独立数量/分布配置，本项目没有一一对应；具体生成解释须结合算法 |
| 落墨形状 | 半径 0.58 m；射线最大下探 2.4 m | `SplashPaintParam`: {"$type": "spl__BulletSplashShooterPaintParam", "DepthMaxDropHeight": 3.0, "DepthMinDropHeight": 10.0, "WidthHalf": 1.472, "WidthHalfNearest": 2.0608} | 当前圆形即时盖章没有表达这些形状/高度或蓄力差异 |
| 标称射程与涂地射程 | effectiveRange=10.4 m；paintRange=13.6 m | 独立原作射程与最终墨迹应通过完整弹道和表面碰撞确定 | effectiveRange 只限制命中玩家时的伤害资格；paintRange 未限制墨迹距离 |

快照 SHA-256：`dfca9f45fd0df3b6afab8f6ec0e47034912f3a6fc411936b9fd7257fe85c4cb9`。

### 2. 轻型速射枪 → Aerospray MG

原始数据：[11.3.0 WeaponShooterBlaze](https://leanny.github.io/splat3/data/parameter/1130/weapon/WeaponShooterBlaze.game__GameParameterTable.json)；本地快照：[WeaponShooterBlaze.1130.json](WeaponShooterBlaze.1130.json)。以下字段路径均从 `GameParameters` 开始。

| 项目 | 当前值 / 实现 | 原作原始字段和值 | 判断与建议 |
|---|---|---|---|
| 弹道与重力 | gravity=9.8；初速=26 m/s；直飞=4 帧，减速=8 帧至 0.66 倍 | `MoveParam`: {"$type": "spl__BulletSimpleMoveParam", "FreeGravity": 0.016, "GoStraightStateEndMaxSpeed": 1.9513, "GoStraightToBrakeStateFrame": 3, "SpawnSpeed": 2.266} | 需换算/实测；按完整类型和阶段对照，不能只改重力 |
| 命中涂色 | 半径随机 0.65～0.8 m，圆形 | `PaintParam`: {"$type": "spl__BulletShooterPaintParam", "DepthScaleMax": 2.5, "DepthScaleMaxBreakFree": 2.5, "DepthScaleMin": 1.25, "DepthScaleMinBreakFree": 1.07, "DistanceFar": 15.0, "DistanceMiddle": 4.2, "DistanceNear": 2.1, "WidthHalfFar": 2.051, "WidthHalfMiddle": 2.145, "WidthHalfNear": 2.31} | 明确机制不一致：应区分距离对应宽度和纵向形状，而非随机圆形 |
| 笔刷硬度 / 强度 | 0.55 / 1，参与覆盖和归属 | 在此武器数据中未找到与当前 smoothstep 硬度、强度直接对应的参数 | 无直接对应依据；结合墨迹边缘、实际归属范围标定 |
| 落墨生成 | 0.7 m 触发阈值；额外近身盖章 | `SplashSpawnParam`: {"$type": "spl__BulletSplashShooterSpawnParam", "ForceSpawnNearestAddNumArray": [4], "SpawnBetweenLength": 12.5, "SpawnNearestLength": 1.3, "SpawnNum": 1.0, "SplitNum": 8} | 原作存在独立数量/分布配置，本项目没有一一对应；具体生成解释须结合算法 |
| 落墨形状 | 半径 0.58 m；射线最大下探 2.4 m | `SplashPaintParam`: {"$type": "spl__BulletSplashShooterPaintParam", "DepthMaxDropHeight": 3.0, "DepthMinDropHeight": 10.0, "WidthHalf": 1.52, "WidthHalfNearest": 1.932} | 当前圆形即时盖章没有表达这些形状/高度或蓄力差异 |
| 标称射程与涂地射程 | effectiveRange=8.4 m；paintRange=11.6 m | 独立原作射程与最终墨迹应通过完整弹道和表面碰撞确定 | effectiveRange 只限制命中玩家时的伤害资格；paintRange 未限制墨迹距离 |

原作此枪 `MoveParam.GoStraightToBrakeStateFrame=3`，当前为 4；这是可直接列出的帧数差异，但仍需核实两种状态边界的完整语义。

快照 SHA-256：`287ffb3b99b4bda1d7ccabf8798a19240a9009f136d851c0e348d106c911b857`。

### 3. 重型射击枪 → .52 Gal

原始数据：[11.3.0 WeaponShooterGravity](https://leanny.github.io/splat3/data/parameter/1130/weapon/WeaponShooterGravity.game__GameParameterTable.json)；本地快照：[WeaponShooterGravity.1130.json](WeaponShooterGravity.1130.json)。以下字段路径均从 `GameParameters` 开始。

| 项目 | 当前值 / 实现 | 原作原始字段和值 | 判断与建议 |
|---|---|---|---|
| 弹道与重力 | gravity=9.8；初速=34 m/s；直飞=4 帧，减速=8 帧至 0.66 倍 | `MoveParam`: {"$type": "spl__BulletSimpleMoveParam", "FreeAirResist": 0.02, "FreeGravity": 0.016, "GoStraightStateEndMaxSpeed": 1.667, "GoStraightToBrakeStateFrame": 3, "SpawnSpeed": 3.06} | 需换算/实测；按完整类型和阶段对照，不能只改重力 |
| 命中涂色 | 半径随机 0.65～0.8 m，圆形 | `PaintParam`: {"$type": "spl__BulletShooterPaintParam", "DepthScaleMax": 2.24, "DepthScaleMaxBreakFree": 2.24, "DepthScaleMin": 1.31, "DepthScaleMinBreakFree": 1.12, "DistanceMiddle": 1.1, "WidthHalfFar": 2.07, "WidthHalfMiddle": 2.11, "WidthHalfNear": 2.11} | 明确机制不一致：应区分距离对应宽度和纵向形状，而非随机圆形 |
| 笔刷硬度 / 强度 | 0.55 / 1，参与覆盖和归属 | 在此武器数据中未找到与当前 smoothstep 硬度、强度直接对应的参数 | 无直接对应依据；结合墨迹边缘、实际归属范围标定 |
| 落墨生成 | 0.7 m 触发阈值；额外近身盖章 | `SplashSpawnParam`: {"$type": "spl__BulletSplashShooterSpawnParam", "ForceSpawnNearestAddNumArray": [2], "SpawnBetweenLength": 5.5, "SpawnNearestLength": 1.3, "SpawnNum": 2.4, "SplitNum": 5} | 原作存在独立数量/分布配置，本项目没有一一对应；具体生成解释须结合算法 |
| 落墨形状 | 半径 0.58 m；射线最大下探 2.4 m | `SplashPaintParam`: {"$type": "spl__BulletSplashShooterPaintParam", "DepthMaxDropHeight": 3.0, "DepthMinDropHeight": 10.0, "WidthHalf": 1.495, "WidthHalfNearest": 2.16775} | 当前圆形即时盖章没有表达这些形状/高度或蓄力差异 |
| 标称射程与涂地射程 | effectiveRange=12 m；paintRange=15.2 m | 独立原作射程与最终墨迹应通过完整弹道和表面碰撞确定 | effectiveRange 只限制命中玩家时的伤害资格；paintRange 未限制墨迹距离 |

原作此枪 `MoveParam.GoStraightToBrakeStateFrame=3`，当前为 4；这是可直接列出的帧数差异，但仍需核实两种状态边界的完整语义。

快照 SHA-256：`c1a42d516bfb5c1763f4285159456589b7a647ee0862004654c36d604889b838`。

### 4. 三连发射击枪 → L-3 Nozzlenose

原始数据：[11.3.0 WeaponShooterTripleQuick](https://leanny.github.io/splat3/data/parameter/1130/weapon/WeaponShooterTripleQuick.game__GameParameterTable.json)；本地快照：[WeaponShooterTripleQuick.1130.json](WeaponShooterTripleQuick.1130.json)。以下字段路径均从 `GameParameters` 开始。

| 项目 | 当前值 / 实现 | 原作原始字段和值 | 判断与建议 |
|---|---|---|---|
| 弹道与重力 | gravity=9.8；初速=33 m/s；直飞=4 帧，减速=8 帧至 0.66 倍 | `MoveParam`: {"$type": "spl__BulletSimpleMoveParam", "GoStraightStateEndMaxSpeed": 1.946, "GoStraightToBrakeStateFrame": 3, "SpawnSpeed": 3.567} | 需换算/实测；按完整类型和阶段对照，不能只改重力 |
| 命中涂色 | 半径随机 0.65～0.8 m，圆形 | `PaintParam`: {"$type": "spl__BulletShooterPaintParam", "DepthScaleMax": 2.24, "DepthScaleMaxBreakFree": 2.24, "DepthScaleMin": 1.31, "DepthScaleMinBreakFree": 1.12, "DistanceMiddle": 1.1, "WidthHalfFar": 1.7765, "WidthHalfMiddle": 1.7765, "WidthHalfNear": 1.7765} | 明确机制不一致：应区分距离对应宽度和纵向形状，而非随机圆形 |
| 笔刷硬度 / 强度 | 0.55 / 1，参与覆盖和归属 | 在此武器数据中未找到与当前 smoothstep 硬度、强度直接对应的参数 | 无直接对应依据；结合墨迹边缘、实际归属范围标定 |
| 落墨生成 | 0.7 m 触发阈值；额外近身盖章 | `SplashSpawnParam`: {"$type": "spl__BulletSplashShooterSpawnParam", "ForceSpawnNearestAddNumArray": [3, 6, 9], "SpawnBetweenLength": 12.0, "SpawnNearestLength": 1.0, "SpawnNum": 1.5, "SplitNum": 12} | 原作存在独立数量/分布配置，本项目没有一一对应；具体生成解释须结合算法 |
| 落墨形状 | 半径 0.58 m；射线最大下探 2.4 m | `SplashPaintParam`: {"$type": "spl__BulletSplashShooterPaintParam", "DepthMaxDropHeight": 3.0, "DepthMinDropHeight": 10.0, "WidthHalf": 1.7825, "WidthHalfNearest": 2.4955} | 当前圆形即时盖章没有表达这些形状/高度或蓄力差异 |
| 标称射程与涂地射程 | effectiveRange=12 m；paintRange=14.8 m | 独立原作射程与最终墨迹应通过完整弹道和表面碰撞确定 | effectiveRange 只限制命中玩家时的伤害资格；paintRange 未限制墨迹距离 |

原作此枪 `MoveParam.GoStraightToBrakeStateFrame=3`，当前为 4；这是可直接列出的帧数差异，但仍需核实两种状态边界的完整语义。

此枪数据未显式给出 `FreeGravity`，不能当作无重力或套用其他枪的 0.016。

快照 SHA-256：`0ce1d682ab866a17d7b4931cbb799faa1a813c350893803a63b0259cfbf47875`。

### 5. 蓄力狙击枪 → Splat Charger

原始数据：[11.3.0 WeaponChargerNormal](https://leanny.github.io/splat3/data/parameter/1130/weapon/WeaponChargerNormal.game__GameParameterTable.json)；本地快照：[WeaponChargerNormal.1130.json](WeaponChargerNormal.1130.json)。以下字段路径均从 `GameParameters` 开始。

| 项目 | 当前值 / 实现 | 原作原始字段和值 | 判断与建议 |
|---|---|---|---|
| 弹道与重力 | gravity=9.8；初速=46 m/s；直飞=4 帧，减速=8 帧至 0.66 倍 | `MoveParam`: {"$type": "spl__BulletChargerMoveParam", "DistanceFullCharge": 24.037, "DistanceMaxCharge": 24.037, "DistanceMinCharge": 9.033, "SpawnSpeedFullCharge": 4.8, "SpawnSpeedMaxCharge": 4.8, "SpawnSpeedMinCharge": 2.4} | 需换算/实测；按完整类型和阶段对照，不能只改重力 |
| 命中涂色 | 半径随机 0.65～0.8 m，圆形 | `PaintParam`: {"$type": "spl__BulletChargerPaintParam", "RadiusFullCharge": 3.263, "RadiusMaxCharge": 2.719, "RadiusMinCharge": 0.906} | 明确机制不一致：应区分未满蓄力与满蓄力半径；当前圆形半径不随蓄力变化 |
| 笔刷硬度 / 强度 | 0.55 / 1，参与覆盖和归属 | 在此武器数据中未找到与当前 smoothstep 硬度、强度直接对应的参数 | 无直接对应依据；结合墨迹边缘、实际归属范围标定 |
| 落墨生成 | 0.7 m 触发阈值；额外近身盖章 | `SplashSpawnParam`: {"$type": "spl__BulletSplashChargerSpawnParam", "OnTopRateFullCharge": 0.34, "OnTopRateMaxCharge": 0.25, "OnTopRateMinCharge": 0.125, "SkipNum": 1} | 原作存在独立数量/分布配置，本项目没有一一对应；具体生成解释须结合算法 |
| 落墨形状 | 半径 0.58 m；射线最大下探 2.4 m | `SplashPaintParam`: {"$type": "spl__BulletSplashChargerPaintParam", "DepthHalfFullCharge": 1.56, "DepthHalfMaxCharge": 1.56, "DepthHalfMinCharge": 2.73, "RadiusSpawnNearest": 1.2, "WidthHalfFullCharge": 1.56, "WidthHalfMaxCharge": 1.56, "WidthHalfMinCharge": 0.78} | 当前圆形即时盖章没有表达这些形状/高度或蓄力差异 |
| 标称射程与涂地射程 | effectiveRange=18 m；paintRange=20.4 m；chargeMinRange=8 m；chargeMinPaintRange=11.2 m | 独立原作射程与最终墨迹应通过完整弹道和表面碰撞确定 | effectiveRange 只限制命中玩家时的伤害资格；paintRange 未限制墨迹距离 |

本项目蓄力会改变初速、伤害、伤害有效射程和散布，但命中/沿途墨迹仍使用相同笔刷。原作 `RadiusFullCharge` 和 `RadiusMaxCharge` 为两个独立字段，不应将后者误标成满蓄力半径，也不能无依据假定线性插值。

快照 SHA-256：`dfe4637def507f933b0bbecbc805331f6357bb69ac165608a6e558406a4b72c1`。

## 建议优先级

1. **先明确涂地射程语义**：目前它是展示目标，不是约束。后续应选择将其接入模型，或明确命名为设计目标；本次不擅自添加截断。
2. **优先区分狙击枪涂色**：按原作最小、未满蓄力最大、满蓄力等端点建立相应命中及沿途墨迹模型。
3. **再区分四把普通枪的落墨分布与形状**：距离宽度、深度、近身补墨、数量/间距、碰墙流墨应作为不同能力核对。原作原始数字未经单位标定不直接写入 Excel。
4. **最后标定重力和笔刷**：固定地图尺度、枪口高度、射击角度；逐帧记录轨迹、飞行时间、落点、单发墨迹及连续射击可游泳覆盖带。分开验证计分归属与视觉边缘。

## 复核清单

- 五把枪分别测试平射/仰射/俯射、近中远命中、地面/墙/坡面/高台、遮挡与高差超过 2.4 m。
- 狙击枪测试点射、半蓄力、接近满蓄力与满蓄力；三连发区分一组内不同发次的落墨分布。
- 对照原作之前固定游戏版本、无相关装备能力影响、同一射击姿态；记录参考内部单位与项目米制的标定方法。
- 若后续实施，必须经过源表 → Luban → 运行时消费者 → 房主权威结果 → 客户端/中途加入复现验证。
