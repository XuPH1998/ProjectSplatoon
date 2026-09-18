# 武器资产、实时散布与单机调试

现行基准为喷3 11.3.0。完整数值、弹道、涂墨与验证状态见 [本轮对齐报告](WeaponAlignment1130/Report.md)；旧迁移验收文件保留为历史记录。

TbHero 只配置英雄标识、角色模型、生命与墨量、资源恢复、基本移动和 `weaponConfigPath`。普通移速及起跳留在表；射击移速、旋转枪蓄力移动及起跳归武器。源表改动仍运行 `cmd /c Config\Luban\gen_luban.bat`，不要修改生成 C#/JSON。

七份武器资产位于 `Assets/GameResource/Weapons/<角色名>/<角色名>WeaponConfig.asset`，角色名依次为 RifleGirl、DualPistolGirl、ShotgunGirl、PistolGirl、RocketLauncherGirl、MachineGunGirl、BubbleGirl。表里填写完整 `Assets/.../*.asset` 路径，Addressables 使用同一完整路径。现有武器模型地址（例如 `Weapon/MachineGun`）在资产中配置。

PistolGirl 的精确速射配置与验收规则见 [窄域标记枪式主武器](WeaponAssets/PistolGirl-Splash.md)。

RocketLauncherGirl（月兔）使用无需蓄力、点击或长按、15 帧定时空爆的 [普通快速爆破枪](WeaponAssets/RocketLauncherGirl-RapidBlaster.md)。发射间隔由 `blasterRepeatSeconds` 控制，射后动作限制由独立模拟截止时间控制。

BubbleGirl（ID 7，沫澜）的四连泡泡、独立资产和验收记录见 [满溢泡泡枪](CombatGirls/BubbleGirl/README.md)。它以组为耗墨和射速单位，使用 `bubbleVolleySeconds` 精确控制组周期；`bubbleIntervalSeconds` 控制颗间间隔。弹跳次数、保速率、重力、累计路程和寿命均由武器资产控制。

迁移保留原表全部 60 个武器字段，包括 15 个旋转枪字段。新增四项：

| 字段 | 默认值 | 含义 |
|---|---|---|
| spreadExpandSeconds | 1 秒 | 从最小扩大至最大散布的时间，0 为立即 |
| spreadRecoverSeconds | 0.5 秒 | 从最大恢复至最小的时间，半进度用一半时间，0 为立即 |
| baseSpreadDegrees | 当前均0° | 地面最小半角 |
| baseJumpSpreadDegrees | 当前均0° | 空中最小半角 |

数值必须有限且非负，并满足 Inspector 下方的武器规则校验。无效修改不会替换调试房上一份有效配置。`landingSpreadRecoverSeconds` 仅用于蓄力火箭筒落地收紧，不叠加到动态武器的 `spreadRecoverSeconds`。

## 中文参数、模式与时间单位

Inspector 显式显示中文参数名、单位、提示和错误信息。发射模式包含全自动、三连发、蓄力松开发射、半自动（支持长按）、旋转枪、泡泡连发、爆破枪；枪口模式使用单枪口、右左交替两个枚举选项，原资产数字映射保持不变。

以下 16 个原参考帧字段已按当前资产数值除以 60，迁入真正的 `double` 秒字段。完整精度参与保存和模拟；可以直接填写小数秒，不需要先计算整数帧。

| 参数 | 秒字段 |
|---|---|
| 人形起手、出墨起手、末发后回墨锁定 | `startSeconds`、`emergeStartSeconds`、`inkRecoverLockSeconds` |
| 组间恢复、半自动点击缓存 | `burstRecoverySeconds`、`semiBufferSeconds` |
| 直行、减速过渡、火箭筒落地散布恢复 | `straightSeconds`、`brakeSeconds`、`landingSpreadRecoverSeconds` |
| 伤害衰减开始、结束 | `damageReduceStartSeconds`、`damageReduceEndSeconds` |
| 满蓄、旋转枪最短有效蓄力、第一圈蓄力 | `chargeSeconds`、`splatlingMinChargeSeconds`、`splatlingFirstChargeSeconds` |
| 旋转枪第一圈、满蓄射击窗口及末弹后恢复 | `splatlingFirstShootSeconds`、`splatlingFullShootSeconds`、`splatlingPostSeconds` |

例如 8 帧对应约 0.13333333333333333 秒，120/150 帧对应 2/2.5 秒，130/260 帧窗口对应约 2.1666666666666665/4.333333333333333 秒。发数、落墨次数继续使用整数；射速继续使用发/秒。旧蓄力枪测试夹具的中间蓄力保留 1/60 秒参考精度，任意小数秒的满蓄端点在达到后钳制为满蓄。旋转枪的最短门槛、两圈端点、包含首末弹的窗口以及慢蓄和退款规则保持原有含义。

## 散布和准星

参考模式下，步枪和消防栓散布边界固定，逐发增大中心采样偏置，停火恢复；跳跃按年龄恢复偏置。消防栓地面水平/垂直边界3°/2°，空中6°/6°；蓄力不增大连射偏置。步枪地空边界4.86°/11.66°，不再保证首发绝对零散布。爆破枪地面0°、空中8°，40～70参考帧恢复跳跃偏置；双枪维持自己的普通射击偏置，手枪保持零散布。

旧时间进度算法保留给历史测试夹具。白凛现为爆炸泼桶，地面和空中散布均为0°；改造前霰弹配置保存在 `Tools/ValidationData/Explosher/Baseline/`。`ReferenceRules`关闭时使用旧弹道；三阶段运动枚举为4，爆炸泼桶运动枚举追加为5、发射模式追加为7，既有数字映射不变。

准星保留中心点，使用有深色描边的四向刻度（720 高度下长 12、宽 3、最小间距 6）。展开范围由散布角度、相机 FOV 与 Game 画面比例计算，没有额外射击弹跳。普通武器显示细圈，旋转枪显示四角范围，表达水平/垂直独立边界。旋转枪双圈蓄力表位于底部中央，显示两段蓄力及剩余弹量；墨条保留预留墨量，枪口遮挡为红色、缺墨为黄色，有预付弹量时不误报缺墨。

七把武器的主准星均投影镜头的逻辑瞄准点，不跟随抛物线落点。预计命中位置使用独立的浅青色空心断环，与实际弹体共用弹道积分和碰撞尺寸：普通墨弹显示首次命中，爆炸泼桶穿人后显示实体爆点，爆破枪显示碰撞或定时爆点，泡泡显示下一颗泡泡的首次碰撞位置（后续仍可反弹）。旋转枪蓄力时按当前蓄力、连射时按本轮释放的蓄力预测，待机显示满蓄力参考位置。带散布或速度随机性的武器使用中心方向和平均初速，随机范围仍由原主准星表达；辅助环不承诺每颗随机弹都命中同一点。

无有效命中/爆炸、落点在画面外或相机后方，以及潜墨或死亡时隐藏辅助环；切换武器后重新计算。命中反馈和枪口遮挡提示仍跟随主准星。落点预测只读，不发射墨弹、不采样权威随机数，也不触发伤害、涂墨或联网消息。

## 编辑器调试

1. 打开 `Assets/Scenes/Main/Boot.unity` 并进入 Play Mode，点击大厅右侧“单机武器调试”。
2. 按 H 切换七名英雄；Esc 房间菜单添加、清理 BOT。房间始终热身，绑定 `127.0.0.1`，不广播且拒绝远端加入。
3. Esc 释放鼠标后点击“定位当前武器配置资产”，在 Inspector 调整参数，返回 Game 试枪。左上显示水平/垂直实时角度、进度和热更新结果。
4. 资产按正常 Unity 保存流程持久化。退出房间会清理观察状态、历史快照和资源引用；普通房间始终使用入房快照。

| 调整项目 | 应用边界 |
|---|---|
| 散布端点/偏置/恢复时长 | 下一模拟步，保留并约束当前偏置或旧模式进度 |
| 伤害及衰减时间/初速/直行及减速时间/重力/碰撞/涂墨 | 后续弹丸使用新配置，在途弹丸与视觉保留旧快照 |
| 射击/蓄力移动与蓄力起跳 | 下一模拟步，沿用角色移动模拟 |
| 参考规则开关/射速/耗墨/模式/蓄力阶段与窗口/起手/冷却/弹丸数/枪口模式 | 取消旧动作、实际预留余额退款一次，松开后重新按下 |
| 武器模型地址 | 加载并验证成功后才取消及切换，失败保留旧配置和模型 |

合法候选作为一份完整不可变配置在模拟步边界替换。调试房是本地 Host；不向联网房广播实时资产修改。普通入房内容签名包含全部武器参数和资产路径。当前玩家协议为31、涂墨协议为9，武器模拟版本为12（后续以 `PlayerSnapshot.ProtocolVersion` 和 `GameplayContentSignature.WeaponSimulationVersion` 为准）。散布进度、双轴角度、发射状态、泡泡组内剩余发数及以双精度秒表示的蓄力状态参与预测和网络校正；泡泡反射段和终止时间同步到客户端。爆炸泼桶的潜墨截止时间与移速限制截止时间分别同步；穿人伤害事件标记为非终止事件，迟加入通过权威在途弹快照恢复弹体，不重播旧枪口和角色动作。

## 白凛：爆炸泼桶

`ShotgunGirlWeaponConfig.asset` 保留原资源身份与枪械外观，采用固定 11.3.0 参数和比例 `18 / 24.037`。射击周期唯一由 `fireRate=60/55` 管理；独立起手为人形16帧/出墨22帧，射后潜墨限制35帧、射击移速限制55帧、回墨锁定70帧，运行时字段均以秒表示。

每次发射一颗穿人墨弹，直击55，同一目标生命周期只结算一次；触及实体表面时固定爆风35，允许与直击叠加到90。寿命到期只回收。每发耗墨11.7，100墨量连续8发。爆风伤害半径约2.149米，爆炸涂墨半径在水平飞行15.726～17.223米之间由3.370米过渡到3.067米。

`Explosher*` 参数覆盖空中初速、移动继承、碰撞体成长、独立动作截止、爆炸墨区插值和脚下墨纵深；支持中文 Inspector、不可变快照、合法性校验和内容签名。其余参考弹道/墨滴字段复用现有配置。原始参数、改造前配置、换算结果位于 `Tools/ValidationData/Explosher/`，复建入口是 `Tools/CombatGirls/install_explosher.py --apply`、Luban 正式生成器和 Unity“重建爆炸泼桶表现”菜单。`--apply` 会重新应用本次固定数值；普通调试无需运行。

`Tools/CombatGirls/validate_explosher.py` 检查仅三格英雄源表修改、源表格式、生成表差异、其他六名英雄配置不变及固定参数哈希。行为和测量测试为 `ExplosherTests`、真实 Host 测试为 `ExplosherPlayTests`；结果、截图和独立进程联网报告统一输出到 `Reports/Explosher/`。物理双机、目标设备和原作逐帧对照分别验收。

## 复建和检查

- 七武器涂地审计和本轮验收见 [11.3.0 涂地验证记录](PaintParity1130/Report.md)。`referenceFootDepth` 是无量纲脚下墨纵深，默认 1，泡泡枪采用原始明确值 1.2；与爆炸泼桶的 `explosherFootDepth=1.4` 分开。它参与中文 Inspector、合法性校验、冻结快照、热更新和内容签名。
- `python Tools/CombatGirls/paint_parity.py --verify-online` 核对固定提交的七份原始参数。`--apply` 只应用已审阅涂地差异，`--report` 汇总独立的 `Reports/PaintParity1130/` 数据（绘图需 matplotlib、Pillow、NumPy）。旧入口 `align_reference_weapons.py --apply` 现在调用同一安全导入器；不再重放历史战斗数值、回写英雄表或把白凛恢复成旧霰弹枪。历史导入记录保留。
- 正式 Windows 菜单仍输出 `Builds/Windows`。验证工具可调用同一流程的 `PrototypeBuilder.BuildWindowsTo(outputDirectory)`，避开正在运行的 Player 锁定旧输出文件。

- `Tools/CombatGirls/update_machinegun.py` 只补齐缺失的旋转枪注册/资产，保留现有调参值，不会恢复已删除的表字段；缺失资产的默认值取自迁移基线。
- `python Tools/CombatGirls/migrate_weapon_seconds.py` 预览旧武器资产的秒制迁移；加 `--apply` 执行。所有资产先校验，混合或缺失新旧时间字段时报错；已迁移资产跳过。首次转换前记录当前值与元数据校验值至 `Reports/WeaponSeconds/migration.json`，重复执行不覆盖该记录。本次迁移证据保存在 `Tools/ValidationData/WeaponSeconds/Migration.json`。禁止将旧字段直接改名而不换算数值。
- `python Tools/CombatGirls/test_weapon_seconds_migration.py` 检查当前数值等效、异常资产阻止写入、重复运行及元数据保持。历史测试夹具通过 `WeaponTimeFixture` 在测试入口换算，原始参考数据保持不变。
- Unity 的 `Splatoon.Editor.PrototypeBuilder.ConfigureAddressables` 注册七个完整资产路径；正式安装/构建入口会调用它。
- `python Tools/CombatGirls/validate_weapon_assets.py` 审核源表、生成字段、原始数值、武器资产及 Addressables。迁移基线位于 `Tools/ValidationData/WeaponAssets/Migration-Baseline.json`。
- Unity 定向测试为 `WeaponSecondsTests`、`WeaponInspectorEditorTests`、`WeaponAssetTests`、`WeaponDebugPlayTests`、`MachineGunTests`、`MachineGunPlayTests`。秒制变更结论见 [秒制迁移验收记录](WeaponAssets/Seconds-Acceptance.md)，完整输出在 `Reports/WeaponSeconds/`；[原资产改造验收](WeaponAssets/Acceptance.md)与 MachineGun 报告保留为历史记录。

真实双机、独立客户端及目标设备性能需要各自验收；Editor Host 的通过结果不覆盖这些环境。
