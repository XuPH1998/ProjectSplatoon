# 大招 11.3.0 参数核对表

原始数据锁定：`7280ff9cde8bb1c5dcef46c700c326471584d2e6`。原始 JSON 与机器可读 reference-map.json 位于 Tools/ValidationData/SpecialWeapons1130。S = 18 / 24.037；时间基准 60Hz。伤害除以 10 转为 100 HP 体系。

**状态：已展开文件中显式覆盖与 Low 档装备零加成值；未获得引擎原生默认值，不能声称完成所有继承展开。** 下表对应自动化 `EveryMappedOverrideMatchesRawSource`；参数相等并不证明整个运行机制相同。

|大招 ID|原始字段|项目字段|原值|换算|项目值|
|---|---|---|---:|---|---:|
|1|DamageParam/DirectHitDamage|directDamage|2200|damage/10|220|
|1|BlastParam/DistanceDamage/0/Damage|innerDamage|530|damage/10|53|
|1|BlastParam/DistanceDamage/1/Damage|outerDamage|350|damage/10|35|
|1|BlastParam/DistanceDamage/0/Distance|innerRadius|2.5|distance*S|1.8721138|
|1|BlastParam/DistanceDamage/1/Distance|outerRadius|4.0|distance*S|2.9953821|
|1|BlastParam/PaintRadius|paintRadius|3.2|distance*S|2.3963057|
|1|spl__WeaponSpUltraShotParam/SpecialDurationFrame/Low|duration|330.0|frames/60|5.5|
|1|spl__WeaponSpUltraShotParam/StartDelayFrame|startup|5|frames/60|0.083333333|
|1|spl__WeaponSpUltraShotParam/ShotDelayFrame|shotDelay|15|frames/60|0.25|
|1|spl__WeaponSpUltraShotParam/RepeatFrame|repeat|55|frames/60|0.91666667|
|1|spl__WeaponSpUltraShotParam/MoveSpeed|moveSpeed|0.07|speed*60*S|3.1451512|
|1|MoveParam/SpawnSpeed|projectileSpeed|1.125|speed*60*S|50.547073|
|1|MoveParam/GoStraightToBrakeStateFrame|straightSeconds|16|frames/60|0.26666667|
|1|MoveParam/BrakeToFreeStateFrame|brakeSeconds|10|frames/60|0.16666667|
|1|MoveParam/BrakeAirResist|brakeDrag|0.09|identity|0.09|
|1|MoveParam/FreeAirResist|freeDrag|0.01985|identity|0.01985|
|1|MoveParam/BrakeGravity|brakeGravity|0.09|accel*3600*S|242.62595|
|1|MoveParam/FreeGravity|freeGravity|0.0190565|accel*3600*S|51.373349|
|1|UltraShotMoveParam/OrbitalRadiusEnd|orbitalRadius|1.0|distance*S|0.74884553|
|1|UltraShotMoveParam/OrbitalRadiusTransitionFrame|orbitalTime|10|frames/60|0.16666667|
|1|CollisionParam/InitRadiusForField|fieldRadiusStart|0.01|distance*S|0.0074884553|
|1|CollisionParam/EndRadiusForField|fieldRadiusEnd|0.3|distance*S|0.22465366|
|1|CollisionParam/ChangeFrameForField|fieldRadiusTime|20|frames/60|0.33333333|
|1|CollisionParam/InitRadiusForPlayer|playerRadiusStart|0.01|distance*S|0.0074884553|
|1|CollisionParam/EndRadiusForPlayer|playerRadiusEnd|0.75|distance*S|0.56163415|
|1|CollisionParam/ChangeFrameForPlayer|playerRadiusTime|10|frames/60|0.16666667|
|2|WeaponParam/SpecialTotalFrame|duration|360|frames/60|6|
|2|BlastParam/RadiusSpreadFrame|expandSeconds|50|frames/60|0.83333333|
|2|BlastParam/DamageHeightUp|damageHeight|20.0|distance*S|14.976911|
|2|BlastParam/DamageRadiusEnd|innerRadius|7.7|distance*S|5.7661106|
|2|BlastParam/PaintRadiusEnd|paintRadius|7.0|distance*S|5.2419187|
|2|BlastParam/PaintSpanFrame|paintInterval|2|frames/60|0.033333333|
|2|MoveParam/SpawnSpeedZSpecUp/Low|throwSpeed|2.0|speed*60*S|89.861464|
|2|MoveParam/SpawnSpeedY|throwLift|0.276|speed*60*S|12.400882|
|3|spl__BulletSpShockSonarParam/GeneratorParam/MaxHP|health|4800|damage/10|480|
|3|spl__BulletSpShockSonarParam/GeneratorParam/HitDamage|directDamage|300|damage/10|30|
|3|spl__BulletSpShockSonarParam/WaveParam/Damage|innerDamage|450|damage/10|45|
|3|spl__BulletSpShockSonarParam/GeneratorParam/WaveEmitFrameArray/0|waveFirst|90|frames/60|1.5|
|3|spl__BulletSpShockSonarParam/WaveParam/MaxFrame/Low|waveLifetime|160.0|frames/60|2.6666667|
|3|spl__BulletSpShockSonarParam/WaveParam/MaxRadius/Low|waveRadius|20.0|distance*S|14.976911|
|3|spl__BulletSpShockSonarParam/WaveParam/DamageYMax|waveHeightUp|2.8|distance*S|2.0967675|
|3|spl__BulletSpShockSonarParam/GeneratorParam/MoveParam/FlyGravity|throwGravity|0.009|accel*3600*S|24.262595|
|3|spl__BulletSpShockSonarParam/GeneratorParam/MoveParam/SpawnSpeedZSpecUp/Low|throwSpeed|0.3|speed*60*S|13.47922|
|3|spl__BulletSpShockSonarParam/GeneratorParam/MoveParam/SpawnSpeedY|throwLift|0.07|speed*60*S|3.1451512|
|4|CloudParam/RainyFrame/Low|effectDuration|480.0|frames/60|8|
|4|CloudParam/DamageRadius|innerRadius|10.0|distance*S|7.4884553|
|4|MoveParam/SpawnSpeedZSpecUp/Low|throwSpeed|1.12|speed*60*S|50.32242|
|4|MoveParam/SpawnSpeedY|throwLift|0.24|speed*60*S|10.783376|
|5|WeaponParam/PreMoveFrame|rideStartup|38|frames/60|0.63333333|
|5|WeaponParam/MoveFrame|rideDuration|54|frames/60|0.9|
|5|WeaponParam/MoveCancelableFrame|rideBrakeAllowed|15|frames/60|0.25|
|5|WeaponParam/MoveSpeed|rideSpeed|0.405|speed*60*S|18.196946|
|5|WeaponParam/MoveSpeed_Aerial|rideAirSpeed|0.22|speed*60*S|9.884761|
|5|WeaponParam/MoveAcc|rideAcceleration|0.031|accel*3600*S|83.571161|
|5|WeaponParam/MoveBrk|rideBrake|0.031|accel*3600*S|83.571161|
|5|WeaponParam/PreBurstFrame|rideBurstDelay|38|frames/60|0.63333333|
|5|WeaponParam/NoDamageStartFrame_PreMove|rideInvincibleStart|25|frames/60|0.41666667|
|5|WeaponParam/NoDamageFrame_AfterBurst|rideInvincibleAfter|28|frames/60|0.46666667|
|5|WeaponParam/UnrelaxFrame|rideRecovery|28|frames/60|0.46666667|
|5|BulletParam/DamageValue|directDamage|2200|damage/10|220|
|5|BulletBlastParam/DistanceDamage/0/Damage|innerDamage|2200|damage/10|220|
|5|BulletBlastParam/DistanceDamage/1/Damage|outerDamage|700|damage/10|70|
|5|BulletBlastParam/DistanceDamage/0/Distance|innerRadius|9.0|distance*S|6.7396098|
|5|BulletBlastParam/DistanceDamage/1/Distance|outerRadius|14.9|distance*S|11.157798|
|5|BulletBlastParam/SubSpecialSpecUpList/1/Value/Low|paintRadius|7.51|distance*S|5.6238299|
|4|CloudParam/RainNum|rainDrops|72|identity|72|
|4|RainParam/MoveParam/FreeGravity|rainGravity|0.02|accel*3600*S|53.916878|
|4|RainParam/MoveParam/FreeAirResist|rainDrag|0.07|identity|0.07|
|4|RainParam/CollisionParam/EndRadiusForField|rainFieldRadius|0.25|distance*S|0.18721138|
|1|spl__WeaponSpUltraShotParam/MoveSpeedInCharge|firingMoveSpeed|0.04|speed*60*S|1.7972293|
|1|MoveParam/GoStraightStateEndMaxSpeed|brakeMaxSpeed|1|speed*60*S|44.930732|
|5|BulletParam/CollisionRadiusForPlayer|ridePlayerRadius|0.8|distance*S|0.59907642|
|5|WeaponParam/Radius_DetectPlayer|rideDetectRadius|1|distance*S|0.74884553|
|5|WeaponParam/OffsetLocal_DetectPlayer/Y|rideDetectOffsetY|0.8|distance*S|0.59907642|
|5|WeaponParam/OffsetLocal_DetectPlayer/Z|rideDetectOffsetZ|1|distance*S|0.74884553|
|5|WeaponParam/GravityKf|rideGravityMultiplier|1.2|identity|1.2|
|5|WeaponParam/RutPaintRadius|rideRutRadius|1.2|distance*S|0.89861464|

## 非显式字段和仍需参考录像校准的行为

- JSON 的 `$type` 标识原生类型，不提供 C++ 类默认值；`ShotParam: {}` 不能解释为全零。共同投掷阻力、重力、碰撞球、持握/收招时间、云高与云速、部分运动和击退属于当前项目实现值，不列为原作已验证参数。
- 终极发射：三次射击，间隔55帧。原始 StartDelayFrame=5 是额外延迟；加上实测基础装备动作18帧，共23帧，首弹最早38帧。射击移速0.04与持握0.07分开，射后恢复40帧。三股弹体按实测相隔4帧启动独立60Hz飞行，16帧直线后按原始值将速度限至1.0/帧。直击玩家穿透；同次攻击保留最高伤害，爆风后直击补差，部署物亦去重。击退和空中反冲功能已接入；旋转角速度与原生速度阈值阶段语义仍为标注近似；当前刹车阶段仍为10帧，不将其标成已完成原作阶段语义验证。
- 三重龙卷风：采用社区实测的82帧预警、51帧持续、5帧37.5伤害、20帧起手及14+19帧投掷；原始 JSON 给7.5/帧，实测用于确认采样方式。向下约2条试射线（10原始距离单位）和持握移速约0.1原始单位/帧来自标为10.0.0的测量记录，并非11.3.0原生默认值。7类敌方炸弹接触有效龙卷风后无爆炸消除，检查移动扫掠及到期引信；友方、预警期、结束后及高度外不消除。完整障碍物/对象行为及向下精确边界仍待原作场景对照。
- 声呐：6.5秒投出后锁定、8秒波标记、无惯性投掷、墙面滑落、已发波不随底座销毁。投掷前摇按社区实测为5帧，选择地雷时额外11帧（共16帧），按所选副武器而非英雄决定；保存在声呐配置中，尚未把现有副武器通用前摇配置全面校准为原作。波标记使用命中时的权威模拟时间计时。隔墙/断地、高低台边界、跳跃及准备好的贴墙状态有场景测试；不代表贴墙移动全过程已验证。初始标记使用原始半径曲线和45帧，但原作统一到期时刻与身体范围相交语义仍待修正；地形环外观仍为简化水平环。
- 墨雨：8秒雨期来自原始值；24 DPS、8秒锁定、13帧投掷来自实测资料。72个独立玩法雨滴按原始重力/阻力/场景碰撞球模拟；其初始相位分布与云高仍为项目实现。云速按实测约7条试射场线/8秒换算。重叠敌雨不叠加DPS；友方雨使用潜墨回血速率且不缩短受伤等待，不免疫敌墨伤害。
- 鲨鱼：加速/刹车与无敌边界接入角色固定步长。接触改用0.8×S球扫描实际身体代理，中心偏移为前方1×S、上方0.8×S，冲墙末帧先结算接触再转制动；映射的 Radius_DetectPlayer=1 字段尚未接入独立的探测阶段，其原作用途仍待核实。爆风击退功能已接入；完整检测形状与原生积分解释仍有近似；外圈15个涂墨碎片已接入，弹道默认值与分布仍为标注的近似。
- 涂地点数：当前暂用3原始平方单位/点，源于旧作300平方单位/点与距离10:1归一化推算；**不是11.3.0已确认值**。场景测试校验Host实际格子面积、接缝、覆盖和重涂，不替代原版固定区域实测。
- 所有未列出的共同零值仅表示该类型不使用对应字段，不能当作原生继承默认零。

参考：
- [锁定数据](https://github.com/Leanny/splat3/tree/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon)
- [终极发射测量与碰撞行为](https://wikiwiki.jp/splatoon3mix/ブキ/スペシャルウェポン/ウルトラショット)
- [三重龙卷风实测](https://wikiwiki.jp/splatoon3mix/ブキ/スペシャルウェポン/トリプルトルネード)
- [声呐实测](https://wikiwiki.jp/splatoon3mix/ブキ/スペシャルウェポン/ホップソナー)
- [墨雨实测](https://wikiwiki.jp/splatoon3mix/ブキ/スペシャルウェポン/アメフラシ)
- [旧作面积测试记录](https://wikiwiki.jp/splatoon2ch/未整理・未確認情報)

## 友方墨雨生命恢复：采用逐帧实测估计

九名英雄的普通恢复改为0.21 HP/帧（12.6 HP/秒），潜墨和友方雨改为1.75 HP/帧（105 HP/秒），受伤等待维持60帧。通过TbHero.xlsx源表生成，墨雨继续复用潜墨速率。

来源为[测量者原始记录（2024-03-25）](https://x.com/mukukuzu/status/1772315890761220373)，其明确逐帧值与[系统测量页](https://wikiwiki.jp/splatoon3mix/システム詳細仕様)一致。两者又把潜墨写为100 HP/秒，与1.75×60=105矛盾。本项目优先采用逐帧测量值并做一致单位换算，替换缺乏原作依据的30/60项目默认值；这是一项有出处的接近原作修正，不是11.3.0原生值已确认。

固定提交的 `misc/SplPlayer.game__GameParameterTable.json` 已保存为 `Tools/ValidationData/SpecialWeapons1130/SplPlayer.json`，没有显式生命恢复速率字段。精确11.3.0校准仍需原生默认值或对应版本逐帧恢复测量。后续证据若推翻当前估计，应统一修正普通／潜墨／友方雨，而非仅改变墨雨倍率。

## 鲨鱼外围涂墨（签名28）

新增六条锁定字段映射：SplashAroundParam的Num=15、OffsetY=0.3、PitchMax=30；SubSpecialSpecUpList无技能Low覆盖的SplashAroundVelocityMin=0.6、Max=0.7、PaintRadius=1。速度乘60S、距离乘S，全部纳入reference-map自动核对。

碎片从玩家脚位+0.3S发出，固定60Hz弹道扫掠碰撞真实地形后涂墨，玩家死亡不撤回已发出碎片，换装或回合重置清理。它们只有涂墨，不另加伤害；爆风与角色恢复时序不等待碎片。

未获取原生飞行默认值，当前使用无阻力抛物线，重力按最大速度0.7、最大角30°、高度0.3、中心最远11.5原始单位拟合，再加1原始单位涂墨半径对应社区约2.5条试射线。均匀方位、随机仰角/速度、射线碰撞及3秒飞行上限同为项目近似。来源范围描述标注10.1.0以前，故仍需原作11.3.0校准，不能把拟合值列为原生默认值。

## 鲨鱼空中启动顺序（签名29）

依据社区观察，空中启动先等待真实地面接触，落地后才消耗能量并启动准备计时；落地后的准备38帧、无敌起点25帧继续使用锁定11.3.0原始数值。行为来源段落标注10.1.0以前，不以该段旧时序覆盖锁定参数；空中转向与垂直运动仍需进一步校准。等待状态已纳入现有配装／生命周期快照和倒地中断规则。

## 击退／反冲功能（签名32，协议36）

终极发射爆风使用原始Accel=470、Bias=0.8、Distance=8；龙卷风使用93.333/0.8/8；鲨鱼爆风使用700/0.8/12；声呐接触使用HitKnockback=400。终极发射空中反冲采用ImpactValue=0.04、AirBreakRt=0.8、StickDownRt=5。

原生积分语义不可得，目前将Accel乘距离比例视为项目加速度、注入一次60Hz速度增量，Bias用作逐帧速度保留率，Distance作为累计位移上限。声呐保留率0.8和上限8原始单位为明确的项目默认；反冲按ImpactValue×60×S转换速度，后拉输入乘StickDownRt。这些解释是为补齐功能而采用的近似，不冒充原生还原。按用户要求不进行数值对比回归。

外力通过CharacterController碰撞移动，状态加入完整与增量快照，预测重放共享同一路径。只有有效敌方伤害后的存活目标获得追加位移；击倒仍走既有泡泡进入流程。复活、倒地、换装和回合中断会清理外力。
