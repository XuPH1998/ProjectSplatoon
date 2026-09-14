# 喷墨对战 Luban 配置

五把主武器的参考修正与保留机制见 [11.3.0 实施记录](../../Reports/Archive/2026-09-14/Docs/WeaponAudit/Weapon-Reference-Implementation.md)。当前仅启用已确认的独立标量；水平涂地范围仍为待标定目标。`Tools/Luban/create-gameplay-tables.mjs <新目录>` 从现有源表复制完整模板，不再生成过时的单武器默认表，也不允许覆盖权威源表。

源数据按职责拆成四张强类型表。第 1 行为字段名，第 2 行为类型，第 3 行为中文说明及单位，第 4 行开始为数据。默认记录 ID 均为 1；字段名和资源地址保持英文稳定标识。

| 表 | 职责 |
| --- | --- |
| TbHero | 每行完整英雄配置：角色/武器模型、生命、移动恢复、发射、弹道、伤害、涂色和蓄力；当前 5 个英雄、73 个字段 |
| TbRoomMode | 默认英雄/场地、人数、时长、重生及计分规则 |
| TbMap | 场景地址、尺寸、归属网格、布局版本 |
| TbGlobal | 唯一全局记录 ID=1，默认模式、网络及子步频率、超时、同步与内存预算、墨水轮廓阈值及按米噪声参数 |

修改工作簿后运行 `cmd /c Config\Luban\gen_luban.bat`。运行时通过 Addressables 的 `Luban` 标签加载 JSON，再通过 `GameplayConfig` 访问四张表。字段定义位于 `source/Defines/gameplay.xml`。构建入口为 **喷墨对战/构建/Windows 正式资源版本**。全部生成表参与联机内容签名。

`TbHero` 配置地址为 `tbhero`。模式用 `heroId` 选择默认英雄；玩家、发射与在途墨弹保存 `HeroId`，通过 `GameplayConfig.GetHero(id)` 读取同一行参数。`characterGravity` 与 `projectileGravity` 分别表示角色/墨弹重力；`shootMoveSpeed` 是当前英雄实际射击移动速度。各英雄使用独立角色和武器模型地址，每行可分别配置；资源必须满足 `InkCharacterView` 与 `HeroWeaponBindings` 的绑定契约。合表说明及验证见 [英雄迁移记录](../../Reports/Archive/2026-09-14/Docs/HeroMigration/Hero-Migration.md)。

沿途落墨使用 `trailRadiusMin` / `trailRadiusMax`，单位为米；两端必须为有限正数且最小值不大于最大值，相等时固定半径。房主在每次实际落墨（含枪口首次落墨）时独立均匀采样，并同步实际半径。

生成的 C#/JSON 禁止手工编辑。当前立体训练场 X 宽 32 米、Z 长 64 米（Width/Length），各可行走表面归属网格默认 0.125 米，最多 4 人。修改场地布局时必须同步正式场景、SurfaceId 和场地布局版本。人物重力 22 与墨弹重力 9.8 分别配置，不能混用。`effectiveRange` 控制伤害有效射程；`paintRange` 当前用于校验和展示，实际墨迹距离由弹道和碰撞决定。模式以 `mapId` 引用 `TbMap`，配置加载地址为 `tbmap`。

## luban.conf 字段说明

此文件使用标准 JSON，不添加不受支持的注释字段。中文含义如下：

| 字段 / 值 | 中文说明 |
| --- | --- |
| `groups.names: c` / `default: true` | 客户端数据分组，默认生成 |
| `schemaFiles.fileName: Defines` / `type: ""` | 从 Defines 目录加载默认格式的结构定义 |
| `dataDir: .` | 数据源为当前 source 目录 |
| `targets.name: client` | 生成器客户端目标名 |
| `manager: Tables` | 生成的总表管理类名 |
| `groups: [c]` | 该目标包含客户端分组 |
| `topModule: cfg` | 生成代码的命名空间 |

`gen_luban.bat` 是 Windows 生成入口，`gen_luban.sh` 用于类 Unix 环境；`excel-merge.bat/.ps1` 为源工作簿合并工具入口。代码标识和文件路径保持英文，避免破坏引用。
