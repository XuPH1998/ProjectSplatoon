# 喷墨对战 Luban 配置

源数据按职责拆成五张强类型表。第 1 行为字段名，第 2 行为类型，第 3 行为中文说明及单位，第 4 行开始为数据。默认记录 ID 均为 1；字段名和资源地址保持英文稳定标识。

| 表 | 职责 |
| --- | --- |
| TbCharacter | 角色资源、生命、墨量、移动、跳跃和回墨 |
| TbWeapon | 武器资源、射速、伤害、耗墨、下落弹道和涂色笔刷 |
| TbRoomMode | 默认角色/武器/场地、人数、时长、重生及计分规则 |
| TbArena | 场景地址、尺寸、归属网格、布局版本 |
| TbGlobal | 唯一全局记录 ID=1，默认模式、网络及子步频率、超时、同步与内存预算 |

修改工作簿后运行 `cmd /c Config\Luban\gen_luban.bat`。运行时通过 Addressables 的 `Luban` 标签加载 JSON，再通过 `GameplayConfig` 访问五张表。字段定义位于 `source/Defines/gameplay.xml`。构建入口为 **喷墨对战/构建/Windows 正式资源版本**。全部生成表参与联机内容签名。

生成的 C#/JSON 禁止手工编辑。当前场地 32 米，归属网格默认 0.125 米，最多 4 人。修改场地布局时必须同步正式场景、SurfaceId 和场地布局版本。人物重力 22 与墨弹重力 19.62 分别配置，不能混用。旧 Range 已由初速、重力和寿命替代；旧固定 PaintRadius 改为半径范围。

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
