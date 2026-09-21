# 六种武器的弹道与瞄准几何

本轮采用方案 B：以武器参考距离构造枪口发射方向，准星显示同一中心弹道在有限引导时间内的首次接触点或终点。相机射线命中深度不再改变这六种武器的发射方向。

适用 RifleGirl、PistolGirl、SplooshGirl、DualPistolGirl 普通射击、MachineGunGirl、RocketLauncherGirl。ShotgunGirl（爆炸泼桶）、BubbleGirl、BubbleShotgunGirl 保留各自规则；未新增双枪滑步。

## 证据与实现边界

参数来自仓库冻结的 Splatoon 3 **11.3.0** 公开提取数据。逐字段来源、转换、原始文件 SHA-256、项目缺省值见 [Import.json](../../Tools/ValidationData/AimBallistics/Import.json)。这些是参数证据，不是原版算法规范。

下述瞄准几何、角度域随机分布、滴墨计数解释及墙墨积分属于本项目重建。原版合格录制样本仍为 0，未验证算法等价，也不报告复刻误差百分比。空间比例沿用 `S = 18 / 24.037`；项目长度不代表现实米制。

| 武器 | 引导帧数 / 时间 | 前后速度继承倍率 | 沿途预算 / 分相数 | 脚下墨 |
|---|---:|---:|---:|---|
| RifleGirl | 8 / 0.133333 秒 | 2 | 1.5 / 8 | 每发 |
| PistolGirl | 4 / 0.066667 秒 | 2 | 1.5 / 6 | 每发 |
| SplooshGirl | 6 / 0.1 秒 | 2 | 1.4 / 5 | 本轮第 5、10…发 |
| DualPistolGirl | 7 / 0.116667 秒 | 2 | 2 / 5 | 累计第 1、6、11…发 |
| MachineGunGirl | 11 / 0.183333 秒 | 0 | 1 / 8 | 每发 |
| RocketLauncherGirl | 15 / 0.25 秒 | 2 | 11 / 1 | 每发 |

MachineGun 的 ZRate 在冻结 JSON 中缺失，因此保留项目默认 0，并未宣称原版不继承移动。空的 `ForceSpawnNearestAddNumArray` 不补造行为。

## 几何与代表弹

`C` 为经过避障回缩的逻辑相机位置，`D` 为不含镜头震动的逻辑前向，`M` 为对应枪口的逻辑位置。双枪每发使用实际交替枪口。设 `tg = ShotGuideFrame / 60`：

```text
R = Position(0, forward * centerSpeed, weapon, tg).z
T = C + D * (dot(M - C, D) + R)
direction = normalize(T - M)
velocity = direction * sampledSpeed
         + yawForward * dot(planarVelocity, yawForward) * ZRate
```

`R` 使用中性水平中心速度，不含玩家移动或随机散布；因此相机前后回缩不会额外缩短相对于枪口的前向参考距离。实际飞行保留仰俯、重力和有符号前后速度继承，不做重力补偿或飞行中转向。

`WeaponLaunch` 集中构造几何、速度及代表弹。实际发射、预测、选角页平地射程共用该入口；选角页的实际平地落点射程仍可超出准星引导距离。

Hydra 未蓄力时展示满蓄参考，蓄力中展示当前比例；释放后使用该弹仓冻结的蓄力比例。代表弹使用中心速度与零散布，命中预览不承诺每颗随机弹均命中。

## 准星与碰撞

- 主圈：在 `[0, tg]` 内沿真实中心轨迹以 60 Hz 分段扫掠，显示首次世界／玩家接触点；未接触时显示 `P(tg)`。
- 方向点：投影逻辑相机轴。它和受下落、移动继承影响的主圈可以分离。
- 散布角标：将四个包络边界弹道在相同时间的点投影到 HUD，保留最小可见尺寸。
- 六种武器不再叠加旧的第二落点圈。目标提示仅认引导窗口内实际预测到的敌人。
- 引导窗口不缩短弹丸寿命，碰撞半径与伤害阶段仍使用原武器配置。
- 堵枪口使用原 pivot→muzzle 检测；红色遮挡提示另识别中心弹先接触、相机射线未指向同一碰撞体的近竖直表面。

最后一项仍是项目提示规则：同一个大型 MeshCollider 内的不同遮挡面可能无法区分。真实弹丸碰撞与主圈接触点仍走实际扫掠；此限制只涉及红色提示分类。

## 散布与确定性

普通弹的角度半径采用 `theta = maxAngle * U^(log(bias)/log(0.5))`，方位角均匀分布。`bias=0` 或半角为 0 时保持中心方向，避免以默认偏置替换合法的零值。站立 Rapid 的偏置因此保持原始参数的 0；空中恢复计时不因偏置为 0 反复初始化。

Hydra 的横／纵角度独立做有符号幂分布，速度另用独立随机流。普通弹的速度也独立于散布。散布、速度、逐滴落墨和墙墨时序不互相消耗随机状态；最终发射速度及权威涂墨事件沿用现有同步路径。

## 涂墨

沿途墨按本轮发射序号 `n` 计算：

```text
count = floor(n * budget) - floor((n - 1) * budget)
phase = (n - 1) % splitNum
firstDistance = nearestLength + phase * spacing / splitNum
```

小数预算先按六位小数稳定化，避免 float 存储误差影响整点。提前撞墙时不会补生成尚未走到的沿途墨。脚下墨以独立计数与相位在接受发射时安排，因此立即堵枪口也保留已应触发的脚下墨。

详细涂墨独立于射击动作。五种普通弹使用距离宽度、直进阶段入射角纵深、后续落差纵深；Rapid 跳过普通撞击印章，由爆炸流程涂墨，爆炸墙面接触另排墙墨。

沿途墨滴独立飞行，轨迹被不可涂几何阻挡。墙墨按首／中／末阶段解析积分，以固定 0.125 项目单位距离采样；离墙后独立下落。落墨侧向速度、纵深、部分角度与落差阈值沿用 Sploosh 已记录的项目近似，并在 Import.json 中逐项标明。

## 配置与维护

`WeaponConfigAsset` 新增 `aimMode`、`shotGuideSeconds`、`angularSpread`、`inheritForwardMovement`、`detailedPaint`、`footSequence`、`footPhase`。旧 `shooterDetails` 保留兼容，仅历史配置继续使用。新参数全部进入不可变快照、内容签名及校验。涉及模式或发射计数解释的热更新重启动作，已发射弹丸保持旧配置。

WeaponSimulationVersion 从 18 升至 19；涂墨协议字段未变。Host 保持随机、命中、伤害和涂墨裁决，客户端显示预测不能写玩法结果。副武器的碰撞筛选与伤害入口保留。

六份武器 asset 为这些字段的运行时来源，不需要修改 Luban。重新应用本次覆盖值：

```powershell
python Tools/WeaponReference/rebuild_aim_ballistics.py
```

导入器仅覆盖已记录字段，保留其余武器参数；重复执行结果一致。`SplooshGirlBuilder.BuildConfig` 同步启用独立模式，避免重新安装角色时恢复旧组合开关。其他历史生成器若重建武器，应最后执行上述导入命令。

`Tools/ValidationData/AimBallistics/Baseline` 冻结的是修改前六份 asset；不得用重导入后的配置覆盖它们。修改前基线提交为 `abb3a4acb7a336f5474ed29a78f2fc4e75adc4b5`。

## 验证入口

Unity 编辑器中 `AimBallisticsValidationRunner` 读取 `Temp/AimBallistics/tests.json`，先刷新和编译，再运行指定测试类。脏场景或已有测试运行时不会启动。结果写入本地忽略目录 `Reports/AimBallistics`。

```json
{"label":"core","names":["Splatoon.Tests.AimBallisticsTests","Splatoon.Tests.WeaponImpactPredictionTests"]}
```

另有 `AimBallisticsPaintTests`（受控几何、实际涂墨归属）与 `AimBallisticsPlayTests`（真实编辑器 Host 场景、HUD 截图、发射与热更新）。后者会进入／退出 Play Mode，截图为 1280×720。运行记录、已知历史断言差异与验收边界见 `Reports/AimBallistics/README.md`。

生成前后对比目录页与 CSV：`python Tools/WeaponReference/report_aim_ballistics.py`。Before 是在当前模拟器中回放冻结旧配置，不能冒充旧二进制运行结果；真正改动前测试结果单独保存在 `baseline-tests.xml`。
