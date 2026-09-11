# 工程配置中文说明

运行时界面及项目菜单采用中文。稳定键、资源地址、程序集名及 Unity/第三方序列化字段保留英文，以下为当前工程配置的中文索引。

| 配置入口 / 字段 | 中文含义与当前值 |
| --- | --- |
| `ProjectSettings/ProjectVersion.txt` | 编辑器版本：Unity 6000.3.9f1 |
| `PlayerSettings.productName` | 程序窗口名称：喷墨对战；启动文件仍为 InkLan.exe |
| `runInBackground` | 后台继续运行，支持同机双窗口 |
| `defaultScreenWidth / Height` | 初始窗口尺寸：1280 × 720 |
| `fullScreenMode / resizableWindow` | 窗口模式，允许调整尺寸 |
| `Standalone / Mono2x` | Windows x64 使用 Mono 脚本后端 |
| `BuildOptions.Development` | 开发版，保留日志及显式启用的联机测试驱动 |
| `EditorBuildSettings.scenes` | 唯一启动场景：Boot；玩法场景由 Addressables 加载 |
| `NetworkConfig.TickRate` | 房主模拟频率：30 次/秒 |
| `EnableSceneManagement` | 关闭 NGO 自动场景管理，使用现有异步加载器 |
| `ConnectionApproval` | 启用连接审批，校验配置签名和最多 4 人的容量 |
| `UnityTransport.SetConnectionData` | 房主监听所有网卡 0.0.0.0，默认 UDP 7777 |
| 连接超时 | 20 秒，支持取消与失败重试 |
| `Splatoon Local` 资源组 | 本地内容打包组，使用 LocalBuildPath / LocalLoadPath |
| `Luban` 标签 | 运行时批量加载配置 JSON 的标签 |
| `Prototype/Arena / Player / Match` | 场地、玩家与比赛状态的稳定资源地址 |
| `Packages/manifest.json` | 包依赖清单；保留包的正式名称与版本标识 |
| `*.asmdef` | 程序集依赖清单，标识需与代码引用一致 |

角色 Inspector 中 `Visual` 是外观根节点、`CharacterView` 绑定动画和 IK、`SimulationMuzzle` 是不受表现后坐力影响的逻辑枪口。场地 `Unpaintable` 为不计分地面投影，`SpawnPoints` 为正式出生点；各模型上的 `PaintSurface` 管理稳定表面 ID、是否计分和绘制纹理尺寸。技能定义字段同样添加中文说明，技能事件下拉项显示中文。

## 输入与渲染资产

`InputSystem_Actions.inputactions` 是模板保留的动作资产。`Player`（玩家）下的 `Move / Look / Attack / Interact / Crouch / Jump / Previous / Next / Sprint` 分别为移动、视角、攻击、交互、蹲下、跳跃、上一项、下一项、冲刺；`UI`（界面）下的 `Navigate / Submit / Cancel / Point / Click / RightClick / MiddleClick / ScrollWheel / TrackedDevicePosition / TrackedDeviceOrientation` 分别为导航、确认、取消、指针、单击、右键、中键、滚轮、追踪设备位置与方向。控制方案 `Keyboard&Mouse / Gamepad / Touch / Joystick / XR` 分别为键鼠、手柄、触摸、摇杆、扩展现实设备。当前原型直接读取 Input System 的键鼠设备，操作以游戏中文提示为准，模板动作资产不控制潜墨按键。

`Assets/Settings/PC_RPAsset / PC_Renderer` 是桌面渲染管线与渲染器，`Mobile_RPAsset / Mobile_Renderer` 为移动平台配置。`DefaultVolumeProfile / SampleSceneProfile` 是体积效果配置，`UniversalRenderPipelineGlobalSettings` 是管线全局设置。核心渲染参数：`RenderScale` 为渲染比例，`MSAA` 为多重采样抗锯齿，`SupportHDR` 为高动态范围，`RequireDepthTexture / RequireOpaqueTexture` 为深度/不透明纹理开关，`MainLightShadowsSupported` 为主光源阴影。材质标识 `Stone / Pale / Weapon / Orange / Blue / Paint / Tracer` 分别对应石色、浅灰、枪械、橙队、蓝队、涂色及弹道材质，名称保留以保证重复生成稳定。

## 中文字体与运行环境

Windows 运行时优先使用系统自带 Microsoft YaHei（微软雅黑），其次为雅黑 UI、黑体、宋体；其他编辑器平台按 `ChineseText` 中的字体列表回退。不将系统字体文件拷贝或分发到项目。界面样式显式指定字体，避免默认拉丁字体导致方框。自动测试检查代表性中文字形，特殊精简系统需要安装中文字体。

## 房间码

房间码采用 13 位易读字母数字，显示为 4-4-5 分组。内部编码版本、IPv4、UDP 端口及 CRC-8 校验；支持小写、空白、去除横线的粘贴，兼容 O/0、I/L/1。无须发现广播或互联网服务。同一地址和端口生成相同码；它是地址快捷方式，不是密码，也不保证房主当前在线。

多网卡时优先展示有默认网关的活动地址，房主可点击切换；选择虚拟网卡或不通的地址时，请改为实际有线/无线网卡。127.0.0.1 仅供同机测试。退出房间立即清除显示的房间码。建立房间后切换分享地址只改变编码，不更改监听所有网卡的网络服务。

玩法参数已改为五张强类型 Luban 表，详见 Config/Luban/README.md。墨流使用独立 InkVisual 层与 RenderGraph 深度/模糊合成；默认桌面渲染器采用 Forward 路径。
