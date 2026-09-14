# 配置字段与迁移

字段类型、中文说明、单位和默认值以五张源 Excel 及 `Config/Luban/source/Defines/gameplay.xml` 为准。`Tools/Luban/create-gameplay-tables.mjs` 是首次建表工具，会重建默认工作簿，不用于日常调参。

| 原 TbPrototype 参数 | 新表字段 | 迁移值或替代规则 |
| --- | --- | --- |
| MatchSeconds | TbRoomMode.matchSeconds | 180 秒 |
| MaxPlayers | TbRoomMode.maxPlayers | 4 |
| MaxHealth | TbCharacter.maxHealth | 100 |
| Damage | TbWeapon.damage | 25 → 3.75；配合 40 发/秒保持 150 伤害/秒 |
| FireRate | TbWeapon.fireRate | 6 → 40 发/秒 |
| MaxInk | TbCharacter.maxInk | 100 |
| ShotInk | TbWeapon.shotInk | 2 → 0.3；保持 12 点/秒 |
| RecoverInk | TbCharacter.recoverInk | 10 点/秒 |
| SwimRecoverInk | TbCharacter.swimRecoverInk | 35 点/秒 |
| RespawnSeconds | TbRoomMode.respawnSeconds | 3 秒 |
| ProtectionSeconds | TbRoomMode.protectionSeconds | 2 秒 |
| MoveSpeed | TbCharacter.moveSpeed | 5 米/秒 |
| SwimSpeed | TbCharacter.swimSpeed | 8 米/秒 |
| EnemyInkMultiplier | TbCharacter.enemyInkMultiplier | 0.55 |
| JumpSpeed | TbCharacter.jumpSpeed | 7 米/秒 |
| Gravity | TbCharacter.gravity | 22 米/秒²；墨弹另用 TbWeapon.gravity=19.62 |
| Range | TbWeapon.speedMin/speedMax/gravity/lifetime | 旧 45 米射线范围退役；20–25 米/秒、0.5 秒寿命的重力运动 |
| PaintRadius | TbWeapon.paintRadiusMin/paintRadiusMax | 固定 1.2 → 随机 0.2–1.5 米 |
| ArenaSize | TbArena.size | 32 米 |
| CellSize | TbArena.cellSize | 0.5 → 0.125 米 |

新增配置包括角色与武器资源地址、模式 ID 引用、散布与球扫掠半径、笔刷硬度/强度、最低人数、友伤和仅地面计分规则、场地布局版本，以及全局默认模式、端口、30 Hz 网络 Tick、120 Hz 弹道子步、超时、快照分块和 RT 预算。

消费链路：工作簿 → `gen_luban.bat` → `Assets/Splatoon/Config/Generated` 与 `Assets/GameResource/Bootstrap/Config/Luban` → Addressables `Luban` 标签 → `LubanConfigService` → `GameplayConfig` → 玩家/弹道/房间/场地/同步服务。全部 JSON 按名称排序后，与协议标识 `ink-lan-v2` 一起计算 SHA-256；场地布局版本位于参与摘要的 TbArena 中。修改布局必须同步提升版本。

`GameplayConfig.Validate` 检查跨表 ID、有限非负数、射速/寿命/网格/笔刷边界、频率整除及同步预算。角色和武器地址由实际加载句柄与网络角色 Prefab 的绑定校验；增加可选美术资源时需要同时建立对应正式 Prefab。动画、IK、材质、粒子、枪口与镜头反馈在正式美术资源中调整。

当前墨水版本 4 已将 paintWorldUvScale / paintShapeNoiseScale 加入 TbGlobal，覆盖参数参与 ink-lan-v4 内容签名。双队累积、精度与恢复格式见 ../InkLook/Implementation.md。以上版本 2 记录保留为历史迁移依据。
