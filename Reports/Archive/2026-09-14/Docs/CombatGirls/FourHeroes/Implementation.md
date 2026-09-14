# CombatGirls 四英雄迁移

当前 5 个英雄 ID 保持不变。ID 1 继续使用 RifleGirl；ID 2–5 分别使用双枪、霰弹枪、手枪和火箭筒角色。换装界面、Reload、蹲姿、MagicaCloth 和双枪翻滚不在本次范围。

| ID | 角色地址 | 武器地址 | 发射机制 |
|---|---|---|---|
| 1 | Character/RifleGirl | Weapon/RifleGirlRifle | 原全自动步枪 |
| 2 | Character/DualPistolGirl | Weapon/DualPistols | 每次点击一颗，成功发射后右→左交替 |
| 3 | Character/ShotgunGirl | Weapon/Shotgun | 每次点击同时发射 8 颗，整组扣墨一次 |
| 4 | Character/PistolGirl | Weapon/Pistol | 每次点击一颗精确墨弹 |
| 5 | Character/RocketLauncherGirl | Weapon/RocketLauncher | 原蓄力松开发射，无爆炸 |

## 正式资源与外观

正式资产位于 `Assets/GameResource/Characters/<角色>/` 与 `Assets/GameResource/Weapons/<角色>/`。每位角色拥有预制体、控制器、遮罩、表现配置和正式材质副本；原网格、贴图及服装、表情、材质变体保存在 `Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack/`。

四份独立清单定义在 `Tools/CombatGirls/hero-packs.json`。导入器只导入 41 个获准动画文件，构建器根据源场景的部件、材质和表情覆盖确定初始外观。霰弹枪使用 Magica cloth2 场景外观；正式角色清理布料及演示脚本、物理组件，保留正常骨架和蒙皮。

双枪使用 Humanoid_FeDual_pistol；霰弹枪的 Humanoid_FePistol 源导入设置未生成实际 Avatar 子资源，因此利用其已有完整 Humanoid 映射生成本地 Avatar。手枪和火箭筒使用 Humanoid_F。所有正式 Avatar 均通过 Unity `isHuman` / `isValid` 验证。

源工程的部分共享资产与上一轮 RifleGirl 迁移时不同，却仍使用同一 GUID。新增角色使用 `SharedFourHeroes` 中带独立 GUID 的副本，保留当前 RifleGirl 的共享依赖。源场景材质覆盖也解析到相应副本。导入记录见 `source-assets.json`。

Unity Toon Shader 版本继续为 `0.14.1-preview`。正式 Toon 材质只调整 `_Is_Filter_LightColor=1`，其他原材质参数复制保留；URP Lit 眼镜等不做替换。逐材质记录见 `asset-validation.txt`。同条件对照图来自源模型与初始部件的重新采样，关闭演示脚本及布料；训练场图使用实际灯光、URP 和喷墨 Renderer。

## 动作与握持

双枪 11 个动画，其他三位各 10 个。L/R 分别对应左右横移；火箭筒 FL 固定在 `(-1,0)`、BR 固定在 `(1,0)`。斜向由四向混合得到。构建时采样躯干相对双脚的末帧位移，确认三组 Die1 均向前、Die2 均向后；火箭筒 F/B 映射也经过采样确认。

待机和移动循环；射击、转身、死亡不循环，射击保留 0–29 帧。单枪覆盖上身，双枪分别覆盖左右手臂与手指。单枪射击播放时间为发射间隔的 90%，双枪为同一只手间隔的 90%。动画事件只处理握持表现。

保持实际速度驱动、普通速度 5 m/s、45° 原地转身阈值和最高 540°/s 移动转身。每位角色独立提取转身曲线、步幅倍率、相机高度与枪口位置。双枪从原武器网格提取装配，分别挂到左右手；逻辑与视觉枪口均独立，上身瞄准同时作用于两臂，关闭左手支撑约束。单枪保留对应支撑握点。

死亡时停火并清除射击层，潜墨死亡恢复可见性，保持死亡末帧直到原有 3 秒重生。重生和换英雄重置双枪下一手为右手、射击时间线、挂点和 IK；普通停火和潜墨保留双枪顺序。跳跃沿用原物理与空中持枪姿态。

## 首版参数

| 参数 | 双枪 | 霰弹枪 | 手枪 |
|---|---:|---:|---:|
| 最小间隔（60 Hz 帧） | 10 | 24 | 16 |
| 每颗伤害 | 32→16 | 10→4 | 52→26 |
| 每次弹丸数 | 1 | 8 | 1 |
| 每次总耗墨 | 0.7 | 4 | 1.4 |
| 伤害射程（m） | 8.4 | 6.5 | 12 |
| 初速（m/s） | 26 | 22 | 33 |
| 地面／空中散布半角 | 3.5°／8° | 7°／11° | 1.5°／6° |
| 伤害衰减起止帧 | 8–40 | 4–18 | 18–42 |
| 射击移速（m/s） | 4.2 | 3.2 | 3.4 |
| 射后回墨锁定帧 | 15 | 30 | 22 |
| 落点半径（m） | 0.55–0.70 | 0.24–0.32 | 0.40–0.55 |
| 沿途半径（m） | 0.45 | 0.18 | 0.28 |
| 水平涂地参考（m） | 11.75 | 12.88 | 14.88 |

涂地参考来自正式枪口和相机、配置散布、固定种子的一次有效发射，通过真实 `InkProjectileService.Spawn`、Physics 球扫和 0.125 m 归属网格测量最远涂色边界；不同种子、俯仰和地形会改变结果。它不等于有效伤害射程，也不是保证连续覆盖到该距离。原始数值和方法见 `paint-measurements.json`，回填保留两位小数。

霰弹使用等面积八点分布，整组按确定性种子旋转。每颗独立 ID、飞行、衰减和碰撞，同组共享 ActionId。全命中最大 80 伤害，因此满血至少两次齐射。枪口、音效和相机反馈按发射组合并，命中反馈允许后续同组击倒升级。

ID 1 数值保持原样；ID 5 只变更身份和资源引用，保留 60 帧满蓄、160 满蓄伤害、8 墨及 18 m 伤害射程，其他弹道与涂色参数也保留。

## 输入、数据与联机

`SemiAutomatic=3` 保留旧枚举值。按下沿接受一发，快速松开不撤销起手；冷却结束前 6 帧允许缓存一次，最多一发，更早点击直接消费。空墨、不能站立、主动潜墨、死亡、菜单、失焦、超时和换英雄清除请求。无新点击的长按不会持续锁住移速或回墨。

`TbHero.xlsx` 增加 `pelletCount`、`muzzleMode`、`semiBufferFrames`；`shotInk` 表示每次有效发射总耗墨。已通过 Luban 生成 C# 和 JSON，未手改生成文件。旧 ID 2–4 的参考测试使用历史 fixture，当前平衡由新测试和源表验证器检查。

状态协议升级为 9。快照同步下一枪手别、左右最近射击身份和服务器时间；已有 Starting 阶段、起手截止时间与发射序列同步单个待发点击。预测重演和服务器确认共享身份，按生命周期、英雄修订号与 ActionId 去重。在途弹记录发射英雄、蓄力、枪口及弹丸索引，不因换英雄改变结算，也不会给新模型施加旧武器后坐。连接签名包含新配置、双枪装配及相关表现参数。

## 重复构建与检查

```powershell
python Tools/CombatGirls/import_assets.py --heroes
# Unity 菜单：喷墨对战 / 角色 / 安装四位 CombatGirls 英雄
python Tools/CombatGirls/update_four_heroes.py
cmd /c Config\Luban\gen_luban.bat
python Tools/CombatGirls/validate_four_heroes.py
python Tools/HeroMigration/verify.py
```

构建角色不改英雄数值。再次导入后需再次执行构建以恢复正式动画导入设置；仅更新来源记录可使用 `--manifest-only`。调整涂地参数后，先运行 `WeaponReferenceMeasurementTests` 的真实测量，再执行源表更新与 Luban。

`FourHeroesPresentationValidation.Run` 输出对照图及训练场动作验证；`CombatGirlsPerformanceValidation.Run` 对比四个 RifleGirl 与四个各新角色，结果是同条件 Editor 数据，不是发布包 GPU 帧时。验收状态单独记录在 `Acceptance.md`。

## 清理边界

旧 ID 2–5 共用 RifleGirl，没有独立旧角色和动作目录。上一轮 Jammo 已删除，已再次核查其 GUID 无引用。保留 RifleGirl、共享依赖和本次要求保留的外观变体；仅清理本轮生成后无引用的临时材质副本。原始 CombatGirls 工程保持不变。
