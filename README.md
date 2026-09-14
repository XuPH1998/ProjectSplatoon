# 喷墨对战 · ProjectSplatoon

使用 Unity **6000.3.9f1** 的局域网第三人称下落墨弹涂地对战，最多八人、每队四人，粉蓝两队，三分钟一局。

## 快速开始

1. 将项目添加到 Unity Hub，使用上述版本打开，等待资源导入和依赖解析。
2. 打开 `Assets/Scenes/Main/Boot.unity`，进入运行模式。
3. 房主选择局域网地址并创建房间，按 Esc 后点击 **复制房间码**。伙伴在主菜单粘贴房间码加入。
4. 执行 **喷墨对战/构建/Windows 正式资源版本**，先构建本地 Addressables，再输出 `Builds/Windows/InkLan.exe`。

完整操作、房间码及双机验证步骤见 [中文联机说明](Docs/LAN-Prototype.md)，验证结果见 [验证记录](Reports/Archive/2026-09-14/Docs/LAN-Validation.md)。

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

正式移植分析、资源与弹道链路见 [实现说明](Reports/Archive/2026-09-14/Docs/InkMigration/Implementation.md)，验收证据见 [移植验证](Reports/Archive/2026-09-14/Docs/InkMigration/Validation.md)。

## 固定立体训练场

默认地图为 `Assets/GameResource/Gameplay/maps/TrainingGround.unity`，X 宽 32 米、Z 长 64 米。可通过 **喷墨对战/地图/打开立体训练场** 直接编辑，游戏仍从 Boot 启动。地面、高台、坡道、桥面独立涂地与面积计分；进入房间加载固定 Scene，退出卸载。编辑地图几何或出生点后执行 **校验并烘焙当前地图**，再重新构建资源。见 [地图说明与验证](Docs/TrainingGround.md)。

粉蓝配色、渐变墨量、按米材质与版本 4 同步的修复说明及本轮验收见 [墨水效果修复](Reports/Archive/2026-09-14/Docs/InkLook/Implementation.md)。

默认角色已迁入 CombatGirls RifleGirl 与步枪，接入持枪四向移动、原地转身、循环射击和死亡重生；旧 Jammo 模型及动作已清理。预览入口、源目标对照图、3C/联机说明与分项验收见 [RifleGirl 移植交付](Reports/Archive/2026-09-14/Docs/CombatGirls/Implementation.md)。

报告统一存放在 `Reports/`，历史记录见 [报告索引](Reports/README.md)；新增报告遵循 [目录规范](ProjectRules/FolderStructureStandard.md)。
