# 积木建造与拆毁 · ProjectSplatoon

使用 Unity **6000.3.9f1** 的局域网第三人称积木建造与拆毁对战，最多八人、每队四人，粉蓝两队，三分钟一局。射击生成边长 **0.10 米**的积木，持续堆出路线与掩体，沿稳定己方积木弦化移动，并拆毁敌方建筑；时间结束时按双方存活积木数量判定胜负。

## 快速开始

1. 将项目添加到 Unity Hub，使用上述版本打开，等待资源导入和依赖解析。
2. 打开 `Assets/Scenes/Main/Boot.unity`，进入运行模式。
3. 房主选择局域网地址并创建房间，按 Esc 后点击 **复制房间码**。伙伴在主菜单粘贴房间码加入。
4. 执行 **喷墨对战/构建/Windows 正式资源版本**，先构建本地 Addressables，再输出 `Builds/Windows/InkLan.exe`。

完整玩法见 [积木建造与拆毁规则](Docs/BlockGameplay.md)，操作、房间码及双机验证步骤见 [中文联机说明](Docs/LAN-Prototype.md)。当前候选包与验收进展见 [首版状态](Reports/BlockTerrain/FirstPlayableStatus20261013.md)；[早期涂色版本联机验证](Reports/Archive/2026-09-14/Docs/LAN-Validation.md) 仅为历史记录。

## 工程入口

| 目录 | 中文说明 |
| --- | --- |
| `Assets/Splatoon/` | 项目代码，运行时和编辑器程序集分离 |
| `Assets/GameResource/` | Addressables 运行时资源 |
| `Assets/Scenes/Main/Boot.unity` | 启动场景，唯一构建场景入口 |
| `Config/Luban/` | 源工作簿、结构定义、数据生成脚本 |
| `Tools/ExcelMerge/` | Excel 合并辅助工具 |
| `ProjectRules/` | 工程规范 |

配置只保留本原型所需参数，已清除 Vanguard 遗留表。修改源表后运行 `cmd /c Config\Luban\gen_luban.bat`，禁止手改生成 C#/JSON。

本项目界面、编辑器菜单和配置说明采用中文。API、稳定参数键、资源地址和第三方包名称保留英文，中文字段说明见 [配置指南](Config/Luban/README.md)。Unity 自带设置与第三方插件界面沿用其自身语言设置；本项目实际使用的参数见 [工程配置说明](Docs/Project-Configuration.md)。

早期涂色版本的移植分析、资源与弹道链路见 [历史实现说明](Reports/Archive/2026-09-14/Docs/InkMigration/Implementation.md)，对应证据见 [历史移植验证](Reports/Archive/2026-09-14/Docs/InkMigration/Validation.md)。

## 固定立体训练场

默认地图为 `Assets/GameResource/Gameplay/maps/TrainingGround.unity`，X 宽 32 米、Z 长 64 米，积木顶界为基准地面以上 32 米。可通过 **喷墨对战/地图/打开立体训练场** 直接编辑，游戏仍从 Boot 启动。固定地面、高台、坡道和桥面提供地图几何，积木在其上生成、堆积或下落；比赛统计存活积木数量，桥上桥下保留各自真实空间与碰撞。出生和换装保护区的完整竖直列禁止生成积木。进入房间加载固定 Scene，退出卸载。编辑地图几何或出生点后执行 **校验并烘焙当前地图**，再重新构建资源。见 [地图说明与历史验证](Docs/TrainingGround.md)。

早期涂色版本的粉蓝配色、渐变墨量、按米材质与版本 4 同步说明见 [历史墨水效果修复](Reports/Archive/2026-09-14/Docs/InkLook/Implementation.md)。

默认角色使用 CombatGirls RifleGirl 与步枪，接入持枪四向移动、原地转身、循环射击和死亡重生；当前主武器目录共九把，建造与拆除规则见积木玩法说明。早期角色迁移的预览入口、源目标对照图、3C/联机说明与分项验收见 [历史 RifleGirl 移植交付](Reports/Archive/2026-09-14/Docs/CombatGirls/Implementation.md)。

报告统一存放在 `Reports/`，历史记录见 [报告索引](Reports/README.md)；新增报告遵循 [目录规范](ProjectRules/FolderStructureStandard.md)。
