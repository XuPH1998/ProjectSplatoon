# AQUA 03 · 卡通喷水枪

独立模型资源，基于当前 ShotgunGirl 霰弹枪的尺寸、挂载坐标和握持区域制作。**本次未替换游戏内霰弹枪。**

造型采用青蓝蓄水壳体、米白塑料外壳、橙色注水盖与安全喷嘴、深灰握持部位。小水仓融入枪身上部，保持原枪细长比例；水仓为不透明塑料，没有透明液体与内部机械。

## 打开与使用

- `WaterShotgun.blend`：Blender 5.2.2 LTS 可编辑源文件。`EDITABLE` 集合保留 32 个部件；纹理已打包。`REFERENCE` 为原枪对照，`EXPORT` 为最终单网格及挂点，`STUDIO` 为预览灯光与相机。
- `Export/WaterShotgun.fbx`：最终游戏网格与三个空节点 `Mount`、`LeftGrip`、`Muzzle`。不含角色、原枪参考、灯光、相机、骨骼或动画。
- `Export/WaterShotgun_Albedo.png`：唯一的 1024×1024 配色贴图。
- `Export/WaterShotgun_Toon.mat`：当前项目 `Toon/Toon` 材质；配套 `.meta` 保存模型→材质→贴图的关联。整套导入当前项目后即可解析材质，无需把 Blender 节点材质转换成 Unity 着色器。
- `Previews/`：五个 Blender 视角；`Previews/Unity/` 为独立验证副本中原角色的同机位原枪/喷水枪对比。

日后需要导入时，将 **Export 文件夹内的全部文件连同 `.meta`** 复制到一个新的 Unity 资源目录。本次没有执行此操作。Unity 项目需要已安装 `com.unity.toonshader`；当前验证版本为 `0.14.1-preview`。其他引擎可使用 FBX、配色贴图，并自行配置卡通着色器。

## 尺寸、挂点与导出坐标

建模基准来自 Unity 实际导入的 `Assets/GameResource/Weapons/ShotgunGirl/Prefabs/Shotgun.prefab`，不是 FBX 文件中的原始厘米坐标。数据保存在 `Reference/shotgun-reference.json`。

建模坐标系以原枪口为原点：X 向枪口前方、Y 向左、Z 向上。以下为该坐标系的网格包围尺寸，不是角色世界坐标下随姿势变化的包围盒。

| 项目 | 原枪 | 新模型 | 尺寸偏差 |
|---|---:|---:|---:|
| 长度 | 1004.539 mm | 1004.344 mm | 0.0194% |
| 宽度 | 53.436 mm | 53.350 mm | 0.1613% |
| 高度 | 220.755 mm | 219.907 mm | 0.3839% |

FBX 顶层使用**原 Shotgun 武器根节点的局部坐标系**，因此导出网格看起来不一定朝世界 +Z，这是现有挂载约定。将 FBX 根节点挂在原 `WeaponSocket` 下时，使用局部位置 `(0,0,0)`、旋转 `(0,0,0)`、缩放 `(1,1,1)`，不要再居中或自动调整枢轴。

| 节点 | 原武器局部位置（米） | 原武器局部旋转 Quaternion（x,y,z,w） |
|---|---|---|
| Mount | `(0,0,0)` | `(0,0,0,1)` |
| LeftGrip | `(-0.33203492,0.071830735,0.06840561)` | `(0.6846295,0.37757275,0.21851097,0.5839301)` |
| Muzzle | `(-0.727905,0.02715215,0.23276588)` | `(0.42768666,-0.44149768,-0.5607825,0.55469537)` |

`LeftGrip` 是左手骨骼的目标变换，位置可以在枪体表面之外；不能把它当成枪身中心。右手握持通过武器挂载原点、原角色骨骼和握把几何共同保持。三个节点只提供后续接入所需的定位信息，本资源包不带运行时组件。

轴转换用独立探针验证：本脚本设置下，Blender `(X,Y,Z)` 经 FBX 导入 Unity 后为 `(-X,Z,-Y)`。脚本已经处理转换；不要再次手动旋转模型。Unity 导入后的三处挂点位置/角度对比见 `Validation/unity-validation.json`。

## 材质与性能

- 导出后 3,304 个三角面，较原枪 4,676 个减少约 29.3%；单 Mesh、单材质槽、单配色贴图。
- 源文件保留分部件编辑，游戏文件合并为一个网格；内部为多个封闭部件，不是一个连通的拓扑壳体。
- 使用 UV 配色图集、分段明暗和适度几何倒角。没有透明材质、法线贴图、镜面反射贴图、额外描边网格或新增动画。
- Unity `.meta` 已禁用动画导入及 Read/Write。预览贴图保持无压缩以便对色；目标平台贴图压缩按项目后续打包配置决定。
- 单材质槽不等于只有一个 GPU 绘制通道；现有 Toon 阴影和描边仍由项目着色器控制。
- Blender 的 `WaterShotgun_ToonPreview` 使用 Eevee 的 Shader to RGB，负责源文件预览；Unity 最终材质使用项目现有 Toon 着色器。两种光照环境不追求逐像素一致。

## 复现

纹理脚本使用 Python + Pillow；建模脚本使用 Blender 自带 Python，不需要 Blender 插件。

```powershell
& .\ArtSource\Weapons\WaterShotgun\Scripts\rebuild.ps1
```

这会使用交付时锁定的 `shotgun-reference.json`，重新生成贴图、模型、源文件和 Blender 预览。Blender 路径可用 `-BlenderExecutable` 指定。需要更换原枪基准时，应先在验证副本执行 `WaterShotgunValidation.ExportReference`，重新核对尺寸和握持，再修改造型。

Unity 复核只允许在独立副本中执行。本次副本位于 `D:\XPHUNITY\ProjectSplatoon-WaterShotgun-Validation-20260917`，使用当前工程的 Assets、Packages 和 ProjectSettings 的独立文件副本。

```powershell
& .\ArtSource\Weapons\WaterShotgun\Scripts\rebuild.ps1 -ValidateUnity
```

若使用其他验证副本，传入 `-ValidationProject`。脚本会拒绝将主工程作为验证工程。验证 C# 只复制到验证副本，未安装进当前项目的 `Assets`。

## 验证边界

完成 Blender 网格检查、Unity FBX 再导入检查、真实 GPU 的 Toon 材质渲染和原角色 Animator 姿势采样。检查水平瞄准、向下 45°、向上 35°和射击关键姿势，并对照原枪。

预览中的俯仰标签采用视觉仰角：向上为正。项目运行时 Pitch 的符号相反，因此向下 45°使用 Pitch `+45`，向上 35°使用 Pitch `-35`。

`*-game-camera.png` 使用 ShotgunGirl 配置中的实际 CameraPivot、CameraOffset 和运行时相机定位函数，FOV 为 60°；预览采用方形中心画幅。默认背后视角下枪体大部分被角色遮挡，不能认定该角度已经达到“清楚识别喷水枪完整造型”的要求。原枪有同样的遮挡，本次保持相机与尺寸约束；完整造型请结合侧面与三分之四视角查看。

这次是模型资源制作与隔离的 Editor 姿势/视觉验证，没有执行游戏内替换、完整 Play Mode 对局、双机网络、Player 构建或手机性能测试。面数减少不等于已经测得帧率提升。最终数据与逐项结果见 `Validation/Acceptance.md`。
