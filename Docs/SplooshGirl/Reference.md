# 广域标记枪参数来源与项目假设

## 冻结的参数证据

基准为 Splatoon 3 **11.3.0**、无装备技能。来源是公开提取的参数文件，属于数据挖掘证据；并非任天堂公开的算法规范或实机测量报告。

[固定提交的 WeaponShooterShort 参数](https://raw.githubusercontent.com/Leanny/splat3/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon/WeaponShooterShort.game__GameParameterTable.json)

冻结本地副本：`Tools/ValidationData/SplooshGirl/WeaponShooterShort.1130.json`。

SHA-256：`4766d778180949833438b93fd945131cd76835adfda6a11feb6f7b2b3803c315`。

空间比例 S = 18 / 24.037 = 0.7488455298。长度 × S，帧速度 × 60S，帧加速度 × 3600S，帧数 / 60，原始伤害 / 10，耗墨比例 × 100。这里的「米」是当前项目尺度，不代表原版现实米制。

## 直接读取的字段

| 原始字段（组.字段） | 转换 / 项目消费 |
|---|---|
| WeaponParam.RepeatFrame = 5 | 60 / 5 = 12 发/秒 |
| WeaponParam.InkConsume = .008 | .8 墨/发 |
| WeaponParam.InkRecoverStop = 15 | .25 秒锁回墨 |
| WeaponParam.MoveSpeed = .08 | 3.594458 m/s 射击移速 |
| WeaponParam.PostDelayFrame = 2 | .033333 秒射后潜墨限制；独立于首发起手 |
| DamageParam.ValueMax / ValueMin = 380 / 190 | 38 / 19 伤害 |
| DamageParam.ReduceStartFrame / ReduceEndFrame = 6 / 22 | .1 / .366667 秒 |
| WeaponParam.Stand_DegSwerve / Jump_DegSwerve | 11.66° / 17.49° |
| WeaponParam.Stand_DegBiasMin / Max / Kf / Decrease | .04 / .4 / .02 / .01 每参考帧 |
| WeaponParam.Jump_DegBiasMax / DecreaseStartFrame / EndFrame | .4 / 25 / 70 |
| MoveParam.SpawnSpeed = 2.06 | 92.5573 m/s 初速 |
| MoveParam.GoStraightToBrakeStateFrame = 2 | .033333 秒直进 |
| MoveParam.GoStraightStateEndMaxSpeed = 1.835 | 82.448 m/s 直进末端限速 |
| MoveParam.FreeGravity = .016 | 43.1335 m/s² 自由段重力 |
| CollisionParam.InitRadiusForField / Player = .2 / .335 | .149769 / .250863 m |
| spl__SpawnBulletAdditionMovePlayerParam.ZRate = 2 | 前后移动在人物前向上的速度投影 × 2 |
| PaintParam.WidthHalfNear / Middle / Far | 2.57S / 2.28S / 1.82S |
| PaintParam.DistanceNear / Middle | 2S / 3.75S |
| PaintParam.DepthScaleMin / Max | 1.31 / 2.62 |
| PaintParam.DepthScaleMinBreakFree / MaxBreakFree | 1.12 / 2.62 |
| SplashSpawnParam.SpawnNearestLength / SpawnBetweenLength | 1.15S / 6S |
| SplashSpawnParam.SpawnNum / SplitNum | 1.4 / 5 |
| SplashPaintParam.WidthHalf / WidthHalfNearest | 1.38S 沿途 / 1.932S 脚下 |
| SplashPaintParam.DepthMaxDropHeight / DepthMinDropHeight | 3S / 10S |
| WallDropMoveParam.FallPeriodFirstFrameMin / Max | 20 / 40 帧 |
| WallDropMoveParam.FallPeriodSecondFrame | 10 帧 |
| WallDropMoveParam.FallPeriodLastFrameMin / Max | 15 / 35 帧 |
| WallDropMoveParam.FallPeriodFirstTargetSpeed / SecondTargetSpeed | .06 × 60S |
| WallDropCollisionPaintParam.PaintRadiusShock / Fall / Ground | 1.56S / .65S / .6S |
| WeaponParam.ShotGuideFrame | 按同一弹道计算射程引导值；实际落点由真实枪口高度计算 |

具体字段读取集中在 `SplooshGirlBuilder.BuildConfig`。原始文件省略的继承值不作为“已证实原版值”。

## 明确采用的项目实现

以下选择使模拟可回放、可联机，但没有原版实机样本支持算法等价：

1. 人形起手 2 帧、出墨起手 8 帧。原始 PostDelay 仅用作射后限制，不充当已证实起手值。
2. 缺省减速段 4 帧、每参考帧水平阻力 .36、减速重力 .07、自由段阻力 .02。弹体寿命上限沿用项目 10 秒；通常更早与场景碰撞。
3. 三段宽度在距离节点间线性插值；远节点采用缺省假设 20S。直进阶段在入射角 10°～35°间插值纵深，后续阶段在落差 1.5S～10S 间插值。
4. 第 n 发沿途滴数为 `floor(n×1.4) - floor((n-1)×1.4)`，循环为 **1、1、2、1、2**。相位 `(n-1)%5`，首次落墨距离 `1.15S + phase×6S/5`。第 5、10…发产生脚下墨，中断开火后重置；不使用额外快照字段。预算是生成上限，提前撞墙时未到距离的墨滴不会补生成。
5. 空的额外脚下墨数组不补造条目。脚下墨纵深 1.2；沿途纵深在落差 3S～10S 对应 1.2→1。
6. 沿途滴初速度采用确定性随机：横向 ±.055、向上 0～.015、前向 .01～.02（均按帧速度换算）；重力和寿命沿用项目墨滴参数。墨滴逐段射线查询实际轨迹，不穿透不可涂几何。
7. 墙墨首 / 末阶段时长由子弹种子和滴序号确定采样；速度按 0→首段速度→中段速度→0 线性过渡并解析积分；以 .125 m 空间间隔涂墨。不足最后一个采样间隔的尾段不另加采样。离墙后从检测到的点开始下落，初速度为 0，重力 .008×3600S；落地使用独立半径、纵深 1。墙面流墨纵深 1.2。
8. 散布继续使用项目已有的有偏径向随机采样；初速继承按人物 yaw 前向的有符号速度投影，忽略侧向 / 竖直分量。弹道修正沿用项目第三人称瞄准系统。
9. 墨迹轮廓沿用项目图集；脚下墨、沿途墨、墙墨、落点均走同一权威 PaintStamp 事件，使归属和显示消费同一事件。
10. 角色步行动画速度按体型缩放和配置移速换算，保留原 10 个动作。测试证明采样有限且握点吻合，不等于人工逐帧艺术验收或无脚滑证明。

## 尚未完成的原版对照

原版合格录制样本数为 **0**。尚未完成固定地图、固定无技能装备、固定站姿 / 跳跃 / 朝向的原版实机录制与面积、连通距离、射程、散布分布对照。因此不能把本项目的测试通过写成“完整复刻原版”或给出误差百分比。
