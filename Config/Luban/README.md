# 喷墨对战 Luban 配置

`source/TbPrototype.xlsx` 保存当前灰盒玩法的 20 项数值。第三行是中文列标题，`description` 列为每个参数提供中文说明、单位和约束。参数键保留英文以兼容运行时代码，数值可直接编辑。`source/Defines/prototype.xml` 定义按键索引的表及字段，含中文注释。Vanguard 遗留表、注册表及生成产物已清除。

修改工作簿后运行 `cmd /c Config\Luban\gen_luban.bat`。运行时通过 Addressables 的 `Luban` 标签加载 JSON，再访问 `cfg.Tables.TbPrototype`。执行 **喷墨对战/原型/搭建灰盒场景** 注册原型资源，或执行 **喷墨对战/原型/构建 Windows 版本** 同时构建本地内容与程序。

生成的 C#/JSON 禁止手工编辑。固定拓扑参数 `ArenaSize`（场地边长 32 米）、`CellSize`（网格边长 0.5 米）、`MaxPlayers`（最多 4 人）改动时必须同步修改场地构建器及网格实现。

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
