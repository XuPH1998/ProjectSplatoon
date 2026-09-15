# 武器资产、实时散布与单机调试

TbHero 只配置英雄标识、角色模型、生命与墨量、资源恢复、基本移动和 `weaponConfigPath`。普通移速及起跳留在表；射击移速、旋转枪蓄力移动及起跳归武器。源表改动仍运行 `cmd /c Config\Luban\gen_luban.bat`，不要修改生成 C#/JSON。

六份武器资产位于 `Assets/GameResource/Weapons/<角色名>/<角色名>WeaponConfig.asset`，角色名依次为 RifleGirl、DualPistolGirl、ShotgunGirl、PistolGirl、RocketLauncherGirl、MachineGunGirl。表里填写完整 `Assets/.../*.asset` 路径，Addressables 使用同一完整路径。现有武器模型地址（例如 `Weapon/MachineGun`）在资产中配置。

迁移保留原表全部 60 个武器字段，包括 15 个旋转枪字段。新增四项：

| 字段 | 默认值 | 含义 |
|---|---|---|
| spreadExpandSeconds | 1 秒 | 从最小扩大至最大散布的时间，0 为立即 |
| spreadRecoverSeconds | 0.5 秒 | 从最大恢复至最小的时间，半进度用一半时间，0 为立即 |
| baseSpreadDegrees | 霰弹 2°，其他 0° | 地面最小半角 |
| baseJumpSpreadDegrees | 霰弹 4°，其他 0° | 空中最小半角 |

数值必须有限且非负，并满足 Inspector 下方的武器规则校验。无效修改不会替换调试房上一份有效配置。既有 `spreadRecoverFrames` 仅用于蓄力火箭筒落地收紧，不叠加到动态武器。

## 散布和准星

动态武器维护 `[0,1]` 的进度。首发有效射击前不增长；持续有效射击（含合法发射间隔）增加 `dt / spreadExpandSeconds`，停止后减少 `dt / spreadRecoverSeconds`。散布由当前环境的最小、最大端点插值；地空切换保留进度。

旋转枪蓄力不增加散布，松手释放预存弹量后才增长。地面水平/垂直最大值为 3°/2°，空中为 6°/6°。每发锁定两个方向的实时半角，独立进行向中心偏置采样。完全恢复后的首发为 0；末弹使用发射时散布，随后恢复。结束动画、回墨锁定不延长扩散，再次蓄力也不强制清零。零扩散时长的首发直接采用最大值。

霰弹由 2°/4°扩至原有 5°/10°并恢复到基础值。火箭筒保留随蓄力收紧及地空散布，不使用时间扩散。

准星保留中心点，使用有深色描边的四向刻度（720 高度下长 12、宽 3、最小间距 6）。展开范围由散布角度、相机 FOV 与 Game 画面比例计算，没有额外射击弹跳。普通武器显示细圈，旋转枪显示四角范围，表达水平/垂直独立边界。旋转枪双圈蓄力表位于底部中央，显示两段蓄力及剩余弹量；墨条保留预留墨量，枪口遮挡为红色、缺墨为黄色，有预付弹量时不误报缺墨。

## 编辑器调试

1. 打开 `Assets/Scenes/Main/Boot.unity` 并进入 Play Mode，点击大厅右侧“单机武器调试”。
2. 按 H 切换六名英雄；Esc 房间菜单添加、清理 BOT。房间始终热身，绑定 `127.0.0.1`，不广播且拒绝远端加入。
3. Esc 释放鼠标后点击“定位当前武器配置资产”，在 Inspector 调整参数，返回 Game 试枪。左上显示水平/垂直实时角度、进度和热更新结果。
4. 资产按正常 Unity 保存流程持久化。退出房间会清理观察状态、历史快照和资源引用；普通房间始终使用入房快照。

| 调整项目 | 应用边界 |
|---|---|
| 散布端点/扩大/恢复时长 | 下一模拟步，保留当前进度 |
| 伤害/初速/重力/碰撞/涂墨 | 后续弹丸使用新配置，在途弹丸与视觉保留旧快照 |
| 射击/蓄力移动与蓄力起跳 | 下一模拟步，沿用角色移动模拟 |
| 射速/耗墨/模式/蓄力阶段与窗口/起手/冷却/弹丸数/枪口模式 | 取消旧动作、实际预留余额退款一次，松开后重新按下 |
| 武器模型地址 | 加载并验证成功后才取消及切换，失败保留旧配置和模型 |

合法候选作为一份完整不可变配置在模拟步边界替换。调试房是本地 Host；不向联网房广播实时资产修改。普通入房内容签名包含全部武器参数和资产路径，玩家协议为 19，武器模拟版本为 2。散布进度、双轴角度、发射状态与每发散布参与预测和网络校正。

## 复建和检查

- `Tools/CombatGirls/update_machinegun.py` 只补齐缺失的旋转枪注册/资产，保留现有调参值，不会恢复已删除的表字段；缺失资产的默认值取自迁移基线。
- Unity 的 `Splatoon.Editor.PrototypeBuilder.ConfigureAddressables` 注册六个完整资产路径；正式安装/构建入口会调用它。
- `python Tools/CombatGirls/validate_weapon_assets.py` 审核源表、生成字段、原始数值、六份资产及 Addressables。迁移基线位于 `Tools/ValidationData/WeaponAssets/Migration-Baseline.json`。
- Unity 定向测试为 `WeaponAssetTests`、`WeaponDebugPlayTests`、`MachineGunTests`、`MachineGunPlayTests`。最新结论见 [验收记录](WeaponAssets/Acceptance.md)，完整输出在 `Reports/WeaponAssets/`；原 MachineGun 报告保留为历史记录，不能代替当前版本验收。

真实双机、独立客户端及目标设备性能需要各自验收；Editor Host 的通过结果不覆盖这些环境。
