# 泡霰 / 爆泡霰弹枪

英雄和弹药 ID 均为 9。角色选择界面左侧向下滚动即可选择“泡霰”。模型、枪模、动作、握枪绑定、头像及纸片资源沿用 ShotgunGirl；球形泡泡沿用 BubbleGirl。

## 默认参数

| 参数 | 默认值 |
| --- | --- |
| 齐射 | 默认三颗同时发射，水平角度 −30°、0°、30°；数量可配置为 1～8 颗 |
| 垂直散布 | 每颗独立随机 ±3°，地面与空中一致 |
| 开火 | 点击或长按，每秒一轮，每轮 16 墨 |
| 起手与回墨 | 普通 0.1 秒，出墨 0.2 秒，回墨锁定 1 秒 |
| 射击移动 | 2.5 米/秒 |
| 泡泡尺寸 | 视觉与逻辑直径均为 1.2 米；站立头部主体实测最大尺寸约 0.208 米 |
| 飞行 | 18 米/秒持续 0.1 秒；0.2 秒线性减速至 0.36 米/秒；低重力 0.05 米/秒²；总寿命 4 秒 |
| 伤害 | 单颗直击 55；半径 2.5 米爆风从 25 线性衰减至零；同颗直击目标不再吃爆风 |
| 涂墨 | 爆炸时将半径 2.8 米球投影到可见的可涂表面，并裁切遮挡范围 |

水平中心弹的名义飞行距离为 4.968 米。实际接触距离取决于枪口高度、瞄准、垂直散布及场景碰撞。枪口贴住障碍物时就地爆破，忽略自身、友军和其他泡泡；不反弹、不穿透、不连锁引爆。

## 调整入口

- `Config/Luban/source/TbHero.xlsx`：角色入口和身体参数；修改后运行 `cmd /c Config\Luban\gen_luban.bat`。
- `Assets/GameResource/Weapons/BubbleShotgunGirl/BubbleShotgunGirlWeaponConfig.asset`：射击、伤害、寿命、球半径和飞行参数。在 Inspector 的“每次有效发射的弹丸数量”（`pelletCount`）中调整齐射数量，默认 3、允许 1～8；每轮耗墨仍由 `shotInk` 控制。水平散布半角默认 30°，多颗时自动等间距覆盖该范围，单颗时水平居中；`floatingPitchSpreadDegrees` 控制每颗独立的垂直随机半角。安装工具只为新建资产设置默认数量，不覆盖已有配置。
- `Assets/GameResource/Weapons/BubbleShotgunGirl/BubbleShotgunGirlAmmoConfig.asset`：爆炸伤害、爆炸涂墨、球体、爆裂特效及声音。
- `Assets/GameResource/Characters/BubbleShotgunGirl/BubbleShotgunGirlPresentation.asset`：独立表现配置。

`FloatingBubble = 6` 使用现有解析式减速弹道。弹丸携带发射时冻结的配置，配置变化和换英雄不改变在途泡泡。恢复消息沿用泡泡状态结构，不回放历史射击动作。水平间距随弹丸数量自动计算，垂直散布由射击种子确定；客户端使用同步后的实际初速度。

## 验证入口

`BubbleShotgunTests` 覆盖配置、齐射、资源消耗、帧率无关模拟、碰撞、遮挡、到期、恢复、对象池和界面布局。`BubbleShotgunPlayTests` 从 Boot 进入真实房主调试房验证实际伤害、纸片形态、地图涂墨、换英雄、表现顺序和截图。

本次运行记录位于 `Reports/BubbleShotgun/`。房主 Play Mode 和序列化恢复测试不等价于独立客户端、真实双机或目标设备性能验收。本次不额外打包。
