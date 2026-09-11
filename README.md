# 喷墨对战 · ProjectSplatoon

使用 Unity **6000.3.9f1** 的局域网第三人称涂地对战原型，最多四人，橙蓝两队，三分钟一局。

## 快速开始

1. 将项目添加到 Unity Hub，使用上述版本打开，等待资源导入和依赖解析。
2. 执行 **喷墨对战/原型/搭建灰盒场景**，打开 `Assets/Scenes/Main/Boot.unity`，进入运行模式。
3. 房主选择局域网地址并创建房间，按 Esc 后点击 **复制房间码**。伙伴在主菜单粘贴房间码加入。
4. 执行 **喷墨对战/原型/构建 Windows 版本**，先构建本地 Addressables，再输出 `Builds/Windows/InkLan.exe`。

完整操作、房间码及双机验证步骤见 [中文联机说明](Docs/LAN-Prototype.md)，验证结果见 [验证记录](Docs/LAN-Validation.md)。

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
