# CombatGirls RifleGirl 移植交付

更新日期：2026-09-13。Unity 6000.3.9f1，URP 17.3.0，Unity Toon Shader 0.14.1-preview。

## 已接入的资源与入口

默认玩家已换为 RifleGirl 与配套步枪，保留源 `Rifle_Full_Body` 的初始外观。源演示场景对该实例只有名称和根变换覆盖，没有衣装或材质覆盖。其他服装、表情部件、颜色及武器材质变体保存在第三方目录，本轮无换装 UI。

| 用途 | 文件 / 地址 |
| --- | --- |
| 角色预制体 | `Assets/GameResource/Characters/RifleGirl/Prefabs/RifleGirlVisual.prefab` / `Character/RifleGirl` |
| 步枪预制体 | `Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab` / `Weapon/RifleGirlRifle` |
| 表现参数 | `Assets/GameResource/Characters/RifleGirl/RifleGirlPresentation.asset` |
| 控制器和上身遮罩 | `Assets/GameResource/Characters/RifleGirl/Animations/` |
| 独立预览 | `Assets/GameResource/Characters/RifleGirl/Preview/RifleGirlPreview.unity` |
| 源资源副本 | `Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack/` |
| 实战入口 | `Assets/Scenes/Main/Boot.unity` |

编辑器菜单：**喷墨对战 / 角色 / 打开 RifleGirl 预览场景**。资源已生成，无需先运行安装工具。需要重建派生资源时使用 **安装 RifleGirl**。正常游戏从 Boot 进入 Play Mode；角色预览场景用于检查美术资源。

本轮使用 Addressables 的 **Use Asset Database** Play Mode 选项验证。如果本机之前手动选择了 **Use Existing Build**，请在 Addressables Groups 的 Play Mode Script 中改回 Use Asset Database；旧发布内容尚未重建，不包含新的角色地址。

第三方导入共 99 个资源及其 meta。`source-assets.json` 记录原路径、原始 SHA-256 和导入改动；`static-validation.json` 验证源工程未改变，模型、贴图和原材质文件内容一致。所有选中动画统一引用现存 `Humanoid_F.fbx` 的 Humanoid Avatar，修复失效来源 GUID。正式角色中删除缺失布料组件、源演示控制脚本、原碰撞体及约束辅助组件；保留骨架与蒙皮。原始第三方预制体是参考副本，实战只使用 GameResource 下已清理的正式预制体。

## 动作与 3C

| 文件 | 行为 | 5 m/s 播放倍率 |
| --- | --- | --- |
| `R_AimWalk_F` | 前 `(0,1)` | 3.28263 |
| `R_AimWalk_B` | 后 `(0,-1)` | 3.44295 |
| `R_AimWalk_FL` | 左 `(-1,0)`，对应 A | 3.57958 |
| `R_AimWalk_BR` | 右 `(1,0)`，对应 D | 3.57958 |
| `R_AimTurn_L90` / `R_AimTurn_R90` | 原地左 / 右转 90°，各 1 秒 | 原速 |
| `R_AimIdle` | 持枪待机 / 空中基础姿态 | 循环 |
| `R_AimIdle_AutoShoot` | 上身连射；15–44 帧，0.966667 秒 | 循环和 Loop Pose |
| `R_Die_F` / `R_Die_B` | 向前 / 向后倒地，约 1.733 秒 | 单次，保持末帧 |

控制器总计只引用上述 10 个 FBX。没有 FR、BL、Reload、蹲姿或跳跃动画。斜向输入归一化，并混合四个正方向。基础层移动参数来自实际水平速度，撞墙、停步、敌墨减速会反映到混合树中。地面速度为 5 m/s；原潜墨速度、跳跃物理、喷墨耗墨/回墨与涂地规则继续使用 Luban 配置。

身体与瞄准朝向独立：水平速度低于 0.1 m/s 且夹角超过 45° 时启动一次转身。转身进度来自原动画 RootQ 曲线，分别提取到 -89.9999° / 90°。Animator 根运动关闭，转身根旋转不烘焙进身体姿态，外部身体旋转只应用一次。移动、跳跃、潜墨或死亡中断转身；移动时身体最高 540°/s 追随瞄准。镜头和权威弹道共用表现配置中的枢轴、碰撞半径与枪口几何。

上身层排除根节点、腿与脚部 IK；射击事件不生成子弹，射速继续由房主武器模拟决定。松手、空墨、潜墨、死亡或回合结束退出射击层，使用 0.1 秒混合。右手使用原 `Hand_R_Socket`，左手约束到原 `Left_Handle`；先修正脊柱/胸部瞄准，再求解左臂握持。保留 `SwitchSocket` 动画事件接收，并显式恢复握枪状态。移除了 Jammo 枪口缩放和枪体大幅平移后坐，保留小幅纯表现相机反馈。

队色与保护状态通过脚下标记显示，不覆盖人物身体材质。潜墨时隐藏人物并显示墨水粒子；死亡立即退出潜墨，停火并停止瞄准/手部约束，显示全身死亡姿态。角色控制器关闭，因此尸体不阻挡其他角色。房主根据入射速度与身体朝向选择倒地方向：背后来弹向前倒，正面、侧向或未知来向默认向后倒。沿用 3 秒重生等待，重置身体朝向、各动画层、握枪与可见性。

本轮表现边界：空中保持持枪姿态；头发与服装保留原骨骼，未接入 MagicaCloth，倒地后不会产生布料垂落或拖曳模拟。瞄准修正保留原相机俯仰范围，上身极端俯仰会明显弯腰。

## 渲染还原

源材质文件全部保留，三阶颜色、阴影阈值/过渡、描边、法线、高光、MatCap、裁切、贴图和渲染队列继续使用原值。正式材质实例位于 `Characters/RifleGirl/Materials`，只把 `_Is_Filter_LightColor` 从 0 改为 1；它将大于 1 的受光 RGB 截断，避免训练场强度 2 的暖色主光把灰发和皮肤提亮成黄白色。没有调整原纹理、配色或地图灯光，也没有修改喷墨 Renderer Feature。

Unity 会规范化实例序列化中的关键字和默认属性。校验工具逐项比较有效 shader keyword、渲染队列、纹理/UV、所有 shader 数值和颜色；唯一允许的数值差异是上述受光过滤开关。源文件中无效的 `_` 关键字不参与着色。

同条件对照使用源预制体副本与正式预制体，在同一 Unity 进程、同一姿态、白色主光强度 1、相同环境光、相机与 960×960 输出下渲染。背面及脸部逐像素一致；正面和侧面各只有 1 个像素不同，均为 921,600 像素输出中的孤立差异。该结果证明受控条件下的移植一致性，不意味着训练场不同灯光下所有像素仍等于源演示场景。

- [四视角源目标对照](Screenshots/source-target-comparison.jpg)
- [训练场脸部近景](Screenshots/training-bright-face.png)
- [训练场阴影位置](Screenshots/training-shadow-front.png)
- [最终 Play Mode 越肩实拍](Screenshots/playmode-trainingground-final.png)
- [对照像素数据](image-comparison.json)

## 配置与联机

`TbCharacter.xlsx`、`TbWeapon.xlsx` 只改了记录 1 的 C4 名称和 D4 地址，并运行 `cmd /c Config\Luban\gen_luban.bat` 生成结果。开始任务时未提交的武器值完整保留：40 发/秒、每发伤害 3、耗墨 0.3、初速 20–32、重力 9.8、寿命 1 秒、碰撞半径 0.025、散布 5°、涂色半径 0.2–2、硬度 0.01、强度 1。

网络玩家预制体、Addressables 角色/武器地址和启动绑定检查同步更新。`PlayerSnapshot.ProtocolVersion = 5`，身体朝向、转身方向/开始角/开始时间、连射开始时间、死亡方向/开始时间均随快照同步。表现按服务器时间恢复转身、连射和死亡进度。协议版本参与连接内容签名，旧新客户端无法通过连接审批；输入协议没有增加 Reload 或蹲下字段。

## 旧资源清理

已删除 `Assets/GameResource/Characters/Jammo` 及目录 meta，共 **91 个资源/meta 文件，131,976,596 字节（约 125.86 MiB）**，包括旧模型、Avatar、动画、控制器、角色预制体和专用材质贴图。删除前确认没有外部 GUID 引用，删除后的扫描再次通过。清单及 SHA-256 见 [legacy-cleanup.json](legacy-cleanup.json)。

旧导入工具不再导入 Jammo，Luban 初始模板地址和编辑器安装入口已改为 RifleGirl。训练场生成工具改用环境目录中的现有 Sky_8 材质。旧 Splattershot 武器美术保留在原目录，已移除其 Addressables 注册且正式玩家不再引用；本次清理范围为旧角色模型和动作。历史移植文档作为记录保留，以本页描述当前角色链路。

## 验收结果与可复查证据

| 类别 | 结果 / 边界 |
| --- | --- |
| 静态资源、源文件、工作簿、旧 GUID | 通过；见 `static-validation.json`、`import-validation.txt` |
| EditMode | 67/67 通过；新增朝向、打断、死亡方向、快照往返、四向绑定与遮罩检查；见 `editmode-tests.xml` |
| GPU 姿态验证 | 待机、四向、移动连射、转向瞄准、空中、潜墨退出、双向死亡末帧、重生通过；见 `graphics-validation.txt` |
| 连射接缝 | 300 帧 / 5 次跨循环，手臂和手腕局部旋转最大接缝变化 0.7913°，内部最大正常变化 7.4344° |
| 握持与枪口 | 检查姿态中左手位置误差低于 0.04 m 门限，记录值约 0；枪口角误差最高 0.0626° |
| 同机双 Editor Play Mode | 晚加入、退出重连、双方互看移动/转身/连射/死亡/重生、完整配置 180 秒回合通过；见 `playmode-host.json`、`playmode-client.json` |
| 原玩法回归 | 房主实际控制器地面/桥面/坡道潜墨、回墨、敌墨减速，以及跳跃、涂色计分和退出清理通过；客户端报告未单独触发自身潜墨，因此不将其写成双端全场景通过 |
| 最终材质 Play Mode | 受光修正后再次完成 28 秒实际玩法检查与截图，退出纹理占用为 0；见 `playmode-visual-final.json` |
| 训练场视觉 | 明处正背面/脸部、遮挡阴影、-65° / +75° 俯仰已取图检查；见 `training-graphics-validation.txt` |
| 四角色开销 | 记录 720p、PC 质量、RTX 3060 下四个角色实例的编辑器帧时、渲染提交 CPU 时间和资源占用；见 `four-visual-editor-comparison.json`。批处理 Draw Call 计数无效，不把 0 当作测量结果 |
| 正式内容与设备 | **未执行**正式 Addressables 构建、发布包、真实双机人工验收及正式四人发布性能/Draw Call/GPU 帧时验收 |

四角色比较使用相同训练场、无遮挡镜头、分辨率和质量，两种角色均启用侧移/射击参数。其结果用于观察几何、材质和纹理成本；编辑器帧间隔受编辑器调度影响，渲染提交 CPU 时间不等于 GPU 帧时，不能据此认定最终游戏帧率提高或下降。两次完整联机运行的游戏视口为 640×480，独立实拍输出为 1280×720，也不用于四人性能结论。

测试修订保留现有配置：旧 DPS 150 断言改为当前 40×3；旧固定 0.51 秒的弹丸过期断言改为配置寿命加 0.01 秒。潜墨诊断保留已消费的跳跃序号，避免诊断输入重置序号触发一次意外跳跃。双进程日志曾包含 UnityEditor.Search 启动索引异常，调用栈来自编辑器搜索模块，玩法运行和退出均完成；最终材质 Play Mode 检查未出现该异常。

## 重跑方式

为保护已打开的主工程与源工程，批处理验证使用独立目录 `D:/XPHUNITY/ProjectSplatoon-CombatGirls-Validation` 与 `D:/XPHUNITY/ProjectSplatoon-CombatGirls-ClientValidation`。验证目录保留旧角色副本供性能基线复查；当前主工程的旧角色已删除。不要同时让两个 Unity 编辑器打开同一工程目录。

可用编辑器批处理入口：`CombatGirlsGraphicsValidation.RebuildAndRun`、`CombatGirlsGraphicsValidation.CaptureTrainingAndExit`、`CombatGirlsPlayModeValidation.Run`、`CombatGirlsPerformanceValidation.Run`（均位于 `Splatoon.Editor` 命名空间）。Play Mode 使用 `-cgSmokeRole host` / `client`，可通过 `-cgSmokePort` 指定端口；`-cgSmokeVisualOnly` 只跑最终单机视觉流程。全部 smoke 仅在显式参数下运行，不影响普通游戏。

导入原资源脚本：`Tools/CombatGirls/import_assets.py`。重新导入后必须重新执行安装工具，以恢复循环、根曲线等正式 importer 设置。配置更新脚本：`Tools/CombatGirls/update_config.py`。复查脚本：`validate_migration.py`、`compare_captures.py`。四角色旧基线复跑需在独立验证副本中保留或从 Git 恢复旧资产，不应重新导入当前主工程。

未生成新发布包；仓库已有 `Builds` 输出仍属于之前版本。当前改动未提交或推送。
