# 墨迹圆边增强

在 2026-09-21 静态湿墨版本上增加外缘圆肩、独立肩部光滑度和轻微接地压暗。正式资源默认开启。厚度通过法线和光照表现，没有真实网格侧面。

## 切换和参数

Unity 菜单：`喷墨对战 > 墨迹静态表现 > 圆边对照 > 今天版（2026-09-21） / 圆边增强版`。

两种外观共用当前覆盖和细节纹理，切换不会清空墨迹。今天版保留原有 0.018 高度、0.055 宽度和原法线计算。增强参数独立存放在 `InkAppearanceProfile`：高度 0.030、宽度 0.080、坡度上限 0.9、肩部光滑度 0.78、接地压暗上限 0.08。内部起伏继续使用 0.002。

`PaintSurface.SetRoundedEdges(bool)` 只切换该表面的显示；菜单同时保存配置及正式材质。开发 Player 可用 `-inkStaticLook today` 或 `-inkStaticLook rounded` 配合原有 `-inkStaticScenario` 进行对照。

## 着色行为

- 中心加八邻域，以 1-2-1 权重拟合连续覆盖平面，求取局部边缘方向与近似距离。法线过滤中的世界噪声使用中心调制值；实际可见轮廓始终使用原始、未过滤的覆盖场。
- 使用连续一阶导数的截面：较短的外坡上升至圆肩，再以较长的内坡回落。基于局部覆盖支持量降低细小墨滴/窄条高度，基于像素世界尺寸淡出远处细节。
- 采样限制在当前 UV 图集最小四纹素空隙以内，拒绝岛外及纹理外样本；加权拟合在缺少邻域时自然转为单侧估计。无需更改图集、补边或持久纹理。
- 同色合并后没有逐笔叠加的凸边；内部孔洞使用相同外缘算法。异色交界继续使用原来的浅槽。
- Forward 与 DepthNormals 调用同一法线函数；接地压暗只作用于可见墨迹内部，最大 8%。原有反射、灯光和内部细节保留。

覆盖写入、细节累计、归属、计分、原图集和 Luban 数据未变；不新增 RT、绘制通道或网络数据，协议及快照版本保持不变。

## 复现验收

在包含当前源码和资源的独立 Unity 项目副本执行：

```powershell
./Tools/InkRoundedEdges/validate.ps1 -ValidationProject D:/XPHUNITY/ProjectSplatoon-InkRoundedValidation-20260921 -ReportDirectory D:/XPHUNITY/ProjectSplatoon/Reports/InkRoundedEdges/Final
```

该脚本验证 GPU 数据/外观切换、裸地一致性、真实 Host 跨缝射击和恢复，生成固定机位截图，构建 Windows 验证 Player，并编译 Android GLES3/Vulkan Shader。不会生成细节图集或重烘焙探针。

使用 `Tools/InkStaticUpgrade/run_player_validation.py`，以今天的验证包作为 `--baseline`，新包作为 `--new`，传 `--new-look rounded --gpu-budget-ms 0.5`，串行测量静态高覆盖率和持续涂色，并录制各 240 帧主相机连续画面。性能录制与视频录制分开。`encode_video.py` 可生成原始视频及左右对照。

本机结果和证据保存在 `Reports/InkRoundedEdges/README.md`。GPU 时间不代表目标手机性能，Shader 编译不等于 APK 或实体设备验收。
