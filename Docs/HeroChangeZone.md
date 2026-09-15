# 出生区更换英雄

正式比赛中，存活玩家在本方出生区按 **H** 打开英雄选择界面，再按 H 或 Esc 返回比赛。离开区域、死亡或进入结算时关闭出生区选人界面。热身入口和开发版 DEBUG 入口保留原有规则。

切换在原地生效，生命值、墨量保留并限制在新英雄上限内，不刷新出生保护。武器取消攻击、重置表现以及网络英雄同步沿用原有切换流程。

## 调整区域

打开 `Assets/GameResource/Gameplay/maps/TrainingGround.unity`，在地图根节点的 `06 Hero Change Zones` 下调整：

- `Pink_HeroChangeZone`：Team = 1，中心 `(0, 2, -28)`。
- `Blue_HeroChangeZone`：Team = 2，中心 `(0, 2, 28)`。
- 初始 BoxCollider Size = `(16, 4, 6)`，Is Trigger 必须开启。位置、旋转、缩放、Center 和 Size 都参与实际范围判定；Scene 中显示阵营色线框。
- 每队四个出生点都必须位于本方有效区域内。组件、Collider 或父节点禁用时，该区域不提供换英雄资格。

菜单 **喷墨对战 → 地图 → 增补出生区换英雄触发器** 只补齐缺失阵营，保留已有区域设置，校验出生点覆盖并刷新地图签名。仅修改触发区后也可用此菜单保存并刷新签名；涉及地形时使用原有的“校验并烘焙当前地图”。

## 权限与联机

客户端用 `PresentedState` 驱动提示、打开和确认资格；服务器处理 `SpawnArea` 请求时使用权威状态位置重新查询区域。不依赖 OnTriggerEnter/Exit 缓存，出生、重生和传送可立即判定。敌方区域、区外、死亡、结算、过期回合或生命版本的请求均被拒绝。

Collider 几何与区域的阵营、启用状态参与地图拓扑签名。客户端和 Host 必须使用相同地图内容；无需修改 PlayerSnapshot 数据布局。

## 验证入口

- `HeroChangeZoneTests`：空间范围、阵营、状态、资源保留、触发器碰撞过滤、场景与签名。
- `HeroChangeZonePlayTests`：真实 Addressables/NGO Host，H/Esc、权威拒绝、死亡重生、换队、误射保护。
- `HeroSelectionTests`、`TrainingGroundTests`：原有英雄规则与地图回归。
- `HeroUiSmoke`：比赛出生区 H 开关和选人界面截图。
- 开发版参数 `-heroZoneRole host|client -heroZonePort 18543 -heroZoneOutput <报告路径>`：同机双进程真实传输验证，检查双方出生区换人、远端模型同步、区外及敌方区域拒绝、重生恢复资格。该诊断仅在显式传参时运行，结束后退出诊断进程。

静态检查、Unity Play Mode、同机双进程和两台物理机器的验收应分别记录，不互相替代。

## 2026-09-15 验证结果

- EditMode：50 项通过，0 失败（区域规则、原有英雄选择与地图回归）。报告：`Reports/GameplayUpdate/hero-change-zone-edit-tests.xml`。
- Host Play Mode：真实 Addressables/NGO 房间的输入、服务器拒绝、重生与换队流程通过。报告：`Reports/GameplayUpdate/hero-change-zone-play-tests.xml`。
- UI：1280×720、1920×1080、2560×1080 连续验证通过；超宽屏独立验证亦通过。自动验证脚本使用两点校准鼠标映射，并在输入前保持 Game View 焦点。最终报告：`Reports/GameplayUpdate/hero-change-zone-ui-final-tests.xml`。
- 界面截图：`Reports/HeroSelection/ui-<分辨率>-spawn-area-selection.png` 和 `ui-<分辨率>-spawn-area-hud.png`；已检查出生区文案、H 键提示和布局。双进程 batchmode 截图为黑屏，不作为视觉验收证据。
- 正式入口 `PrototypeBuilder.BuildWindows` 完成 Addressables 和 Windows Development 构建；`Builds/Windows/InkLan.exe` 用于同机 Host/Client 真实传输验证。两端均通过本方换人、资源保留、远端模型同步、区外/敌方出生区拒绝及重生后换人检查，无诊断记录的运行错误。报告：`Reports/HeroChangeZone/host.txt`、`client.txt`。
- 测试版本 `Splatoon.dll` SHA-256：`B3BC49BE3297A16CC37D5DA77604DF787C757D6FF078D0BD503F9C069C671585`。地图拓扑：`A260E4020FDFCD5D02EF9691F8C0E6832996F5A13B0914CA9F754CDB2A7D6A6C`。
- 两台物理机器之间的 LAN 验收未执行。
