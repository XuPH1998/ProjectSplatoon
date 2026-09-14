# 验证记录

这是正式资源移植前的灰盒与房间码历史记录。当前墨弹、表面纹理、五表配置及正式模型的验证结果见 [InkMigration/Validation.md](InkMigration/Validation.md)，下文的 TbPrototype 与旧构建结果不代表当前版本。

验证日期：2026-09-11。Unity 6000.3.9f1，Windows x64，NGO 2.7.0 / Unity Transport 2.6.0。

为保留用户已打开的编辑器会话，编译和打包在 `Temp/PrototypeValidation` 验证副本执行。源码来自当前工作区，生成场景、Prefab、Addressables 配置及 Windows 程序回传到当前项目。没有提交 Git。

## 本次更新：房间码与中文界面

本节对应当前交付包。Windows 与 Addressables 构建日志：`Logs/roomcode-build.log`。Luban 从中文源表重新生成，保留原有 20 项参数键和值，字段文档由 XML 中的中文注释自动生成。

| 验证项 | 本次结果与证据 |
| --- | --- |
| 自动测试与编译 | `Logs/roomcode-tests.xml` 和最后的 `roomcode-tests-final.xml`：23 项通过，0 失败。覆盖原有玩法规则、4 组地址端口往返、全部单字符替换、空白与歧义字符、无效地址、多网卡枚举及中文字形 |
| Windows / Addressables | `Logs/roomcode-build.log`：构建成功，退出码 0；程序窗口标题为“喷墨对战” |
| 房间码真实传输 | `Logs/roomcode-host.log` / `roomcode-client.log`：房间码解码得到 127.0.0.1:7791；客户端测试参数故意指定另一端口，仍按码内端口成功加入 |
| 玩法回归 | 房间码连接后，双方涂色状态 hash 一致（例如 1793926380），观察到伤害、击倒、重生及回墨；本次没有重新跑满完整三分钟 |
| 实际中文主菜单 | `Docs/Screenshots/chinese-main-menu.png`：实际 Windows 窗口截图，中文标题、创建/加入、网卡、端口与操作说明均正常显示 |
| 实际复制按钮 | 点击“复制房间码”，读取到与当前房间一致的地址码；按钮显示“已复制”，见 `chinese-room-copied.png`。测试完成后恢复原剪贴板内容 |
| 粘贴、错误提示及重试 | 实际点击“粘贴”和“加入房间”，错误码显示中文校验失败（`chinese-invalid-code.png`）；替换正确码后同一客户端成功加入（`chinese-code-joined.png`，`Logs/chinese-ui-join.log`） |
| 本机局域网网卡地址 | 上述实际菜单连接使用本机 10.20.16.139:7777，验证了本机网卡地址码；仍属于同机双进程，不作为两台电脑的局域网验收 |
| 房主退出 | 房主通过会话退出时，客户端显示“房主已关闭房间，请重新创建或加入房间。”；关闭房主窗口时显示“与房主的连接已断开，请重新加入。”。两种方式均回主菜单，见 `chinese-host-left.png` 及客户端日志 |
| 配置中文化 | 源工作簿所有参数说明、单位、列标题已中文化；已渲染检查并生成代码/JSON。工程配置、稳定英文键和输入动作映射见 `Docs/Project-Configuration.md` |

实际菜单点击与截图覆盖了主菜单、房间菜单、复制反馈、输错提示和成功加入界面；键鼠长时间游玩手感、所有显示分辨率及所有结算文案组合仍需人工体验。真实双机网络与编辑器混合联机仍未执行。

本次交付的 `InkLan_Data/Managed/Splatoon.dll` SHA-256：`E418710CD3302ED581145978BE39205C76B01077F310522C85A92C09D3838F3C`。复制整个 Windows 文件夹或解压完整 ZIP 后运行 `InkLan.exe`。

## 此前灰盒基线验证（本次未全量重跑）

| 项目 | 证据 |
| --- | --- |
| Luban 源表 → 生成 C#/JSON → 运行时 | `gen_luban.bat` 成功；生成 `Tables.TbPrototype`；Windows 启动日志确认配置加载完成 |
| 配置清理 | 删除 19 个 Vanguard 旧工作簿、2 个旧 XML schema、112 个旧生成 C#、35 个旧 JSON；源目录只保留当前原型表、schema 和 luban.conf |
| Unity 编译及 EditMode | `Logs/prototype-tests.xml`：8 项通过，0 失败；其中 6 项是新增玩法规则测试 |
| Windows x64 与 Addressables 构建 | 构建入口将本地 Addressables 内容打包进 Windows 程序，无远程内容依赖 |
| 同机 Host / Client | 通过真实 UDP 传输连接，使用自动化输入帧驱动各自拥有的角色；双方角色位置变化、射击和潜墨状态正常 |
| 涂色与回墨同步 | 稳定状态下双方格数与完整网格 hash 一致；潜墨恢复至 100，持续射击可耗尽墨水 |
| 伤害、击倒、重生 | 房主日志记录敌人生命 100 → 75 → 50 → 25 → 0；客户端观察到 0 后重生至 100 |
| 完整三分钟比赛与再开局 | 已两次运行完整 180 秒比赛；双方进入 Finished，再开第 2 局；涂色清零，初始网格 hash 恢复为 1051775469 |
| 中途加入 / 四人房间 / 满员 | 第 3、4 个进程进入进行中的比赛，完整涂色 hash 与房主一致；第 5 个进程收到 Room is full (4 players) |
| 客户端退出再加入 | `Logs/lan-rejoin.log`：同一进程连续退出重进两轮，均补齐涂色，无重复角色 |
| 房主退出再建房 | `Logs/lan-rehost.log`：同一进程连续关闭和重建房间两轮，每次网格恢复初始状态 |
| 连接超时与主动取消 | `Logs/lan-timeout.log` 和 `Logs/lan-cancel.log` 分别记录明确超时与取消结果 |
| 无效 IPv4 / 端口占用 | `Logs/lan-invalid-ip.log` 拒绝无效地址；`Logs/lan-port-in-use.log` 在相同 UDP 端口被占用时明确报错 |
| 房主退出 → 客户端返回菜单 | 最终交付包的 `Logs/lan-delivery-client.log` 记录 `Returned to menu. Disconnected due to host shutting down.` |
| 最终交付包复测 | `Logs/prototype-build-delivery.log` 构建成功；`Logs/lan-delivery-host.log` 与 `Logs/lan-delivery-client.log` 的稳定网格 hash 均为 3902909047，无游戏代码异常 |
| 运行场景视觉 | 用 URP 显式离屏渲染检查胶囊角色、方块掩体、双色涂地；`Docs/Screenshots/lan-graybox-host.png` 与 `lan-graybox-client.png` 已人工查看。该截图不包含 IMGUI HUD |

运行中发现并修复了打包程序动态创建 NetworkManager 时 NetworkConfig 为空、成功连接后超时计时器仍访问已释放 CancellationTokenSource，以及生成角色前写 NetworkVariable 的警告。修复后重新构建并验证正常对局和退出重进。

主要联机证据：`Logs/lan-host-v2.log`、`Logs/lan-client-v2.log`、`Logs/lan-late-v2.log`、`Logs/lan-fourth-v2.log`、`Logs/lan-full-v2.log`；修复后完整对局为 `Logs/lan-host-v3.log`、`Logs/lan-client-v3.log`。日志及临时验证目录属于本地构建产物，不纳入 Git。

## 仍需人工验收

- **两台实体电脑的真实局域网**：当前只有同机多进程传输证据，尚未验证另一台电脑、路由器隔离和防火墙路径。
- **真实键鼠手感与完整窗口 UI**：自动输入验证不代替鼠标灵敏度、相机避障观感、Esc 焦点切换和实际菜单点击验收。隐藏窗口的普通后缓冲截图为黑色，不据此认定画面正常。
- **编辑器与 Windows 混合双端**：同版本构建包之间已验证，现有用户编辑器未进入 Play Mode 执行混合联机。
- 配置不一致有明确拒绝分支，但尚未完成两份不同配置构建之间的运行时测试。射线遮挡和友伤规则已实现；友伤纯规则已测，复杂掩体交叉射击仍需手工检查。

离屏抓图时出现一次传输接收队列积压提示，随后双方网格仍一致；抓图是仅在显式诊断参数下运行的同步渲染操作，普通游戏不会执行。

验收操作见 `Docs/LAN-Prototype.md`。真实网络测试时请复制整个 `Builds/Windows` 文件夹。
