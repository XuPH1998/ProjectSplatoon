# RifleGirl · 2.5 头身 Q 版

以当前项目 `RifleGirlVisual` 实际启用的服装、脸、头发、配饰为基础改造的可编辑蒙皮模型。参考图用于头身与卡通轮廓；角色身份沿用 RifleGirl 的贝雷帽、浅色长发、紫色服装和靴子。

## 使用

1. 在 Unity 打开 `Assets/GameResource/Characters/RifleGirlChibi/Preview/RifleGirlChibiPreview.unity`。
2. 进入 Play Mode，左侧按钮可以切换原有 10 个动作，支持暂停、速度和前/后/斜视角。
3. 独立角色预制体：`Assets/GameResource/Characters/RifleGirlChibi/Prefabs/RifleGirlChibi.prefab`。Animator 已引用原来的 `RifleGirlCombat.controller`。
4. 编辑源文件：本目录下的 `RifleGirlChibi.blend`，贴图已打包。交付 FBX 在 `Assets/GameResource/Characters/RifleGirlChibi/Models/RifleGirlChibi.fbx`。

这是独立美术资源和动作预览场景。正式英雄表、网络玩家预制体和原 RifleGirl 的资源引用不在本次替换范围内。

## 比例和蒙皮

- 不含帽子的身高 1.20 m；头发顶部到下巴 0.48 m，比例为 2.50。帽子使整体包围高度约为 1.319 m。
- 93 个骨骼节点，50 个人形骨骼映射；26,021 个顶点、41,013 个三角形、11 个蒙皮网格。
- 保留原服装组合的 UV、材质、拓扑与蒙皮影响，重塑头部宽度、躯干、长发和四肢，并同步修改骨架静止位置。
- 每顶点最多 4 个骨骼影响，权重归一化；无未绑定顶点。
- FBX 的网格与骨架已烘焙成标准 T 姿势，使用 **Humanoid / Create From This Model** 和这套 Q 版骨架生成的新 Avatar。不要改成复制原高挑模型的 Avatar，否则骨架比例会不一致。
- 服装、头发和配饰继续使用蒙皮；本次未添加布料模拟或新的表情系统。

## 动画与持枪预览

原控制器引用的 10 个片段为 `AimIdle`、`AimWalk_F`、`AimWalk_B`、`AimWalk_FL`、`AimWalk_BR`、`AimTurn_L90`、`AimTurn_R90`、`R_AimIdle_AutoShoot`、`Die1`、`Die2`。其中 FL 对应左移，BR 对应右移。原片段和原控制器未改写。

预览场景挂载原步枪的 0.55 倍视觉副本，使用独立的左臂两段 IK 和手腕坐标补偿检查握持。这个比例通过全部站姿动作的握把可达性采样选定。预览脚本只播放动作，不实现移动、伤害、换弹、网络或英雄切换。正式接入游戏时，应通过角色表现层适配这套骨架的挂点、手腕坐标与尺寸。

`RifleGirlChibiAnimationEvents` 接收原动画的 `SwitchSocket` 事件，避免独立模型预览时缺少事件接收者。游戏表现组件仍可接收同名事件。

项目的编辑器启动脚本对这个明确命名的美术预览场景跳过大厅初始化，便于独立检查动画。

## 证据

- `Validation/blender-validation.json`：生成参数、网格数量、权重检查。
- `Validation/unity-validation.json`：Avatar、导入后的蒙皮、10 个动作每个 13 个采样点、握持偏差。
- `Validation/play-mode.json`：独立 Unity Editor 实际 Play Mode 的预览验证结果。
- `Previews/unity-*.png`：Unity Toon 材质下的静止姿态视图。
- `Previews/animation-*.png`：原动作直接重定向；`grip-*.png`：同一姿态加左手握持校正。
- `Previews/play-*.png`：实际 Play Mode 截图。
- `Previews/source-aim-idle.png`：原模型同一待机动作的对照。

验证状态以相应 JSON 中的 `passed` 和错误列表为准。独立预览验证不等于正式英雄接入、联机或目标设备验收。

本次最终验证：实测 2.4999997 头身；10 个片段共 130 个编辑器采样点通过，10 个片段实际 Play Mode 切换通过，预览左手最大握持偏差小于 0.001 mm。验证副本启动时出现一条 Unity 编辑器 SearchDatabase 的索引异常，完整堆栈保留在 `play-mode.json` 的 `editorIssues`，没有计入模型运行错误；模型运行错误列表为空。

## 重建

在项目根目录执行：

```powershell
.\ArtSource\Characters\RifleGirlChibi\Scripts\rebuild.ps1 -ValidateUnity
```

可选 `-RefreshSource` 重新从当前角色预制体导出实际启用的网格、材质与骨架。验证需要独立的项目副本；脚本默认使用 `D:\XPHUNITY\ProjectSplatoon-RifleGirlChibi-Validation-20260917`，不会关闭正在工作的主项目。

来源与授权沿用项目已有 CombatGirls 资产；本目录是项目内部派生资源。
