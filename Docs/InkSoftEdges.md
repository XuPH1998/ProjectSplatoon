# 静态墨迹柔润实验

这是基于参考截图的外观实验，不是对任天堂实现方式的复刻声明。按用户后续要求，当前项目的普通对局默认启用 B 版进行观察（`InkAppearance.DefaultLook = Soft`），仍可切回 A 版。两种外观使用同一份覆盖与细节状态；没有修改玩法判定、归属、计分、图集、网络协议或快照版本。原有已生成的对比包与验收数据保留其构建时设置。

## 使用

- Unity 运行期间：`喷墨对战 > 墨迹静态表现 > 柔润实验`，选择当前或柔润效果。菜单只切换当前实例，不保存资源。
- 开发 Player：传入 `-inkSoftCompare`，进入地图后按 F8 在 A（当前正式圆边）和 B（柔润实验）之间切换。
- 性能场景：`-inkStaticScenario static` 或 `paint`，搭配 `-inkStaticLook rounded` / `soft`。性能测试不启用 F8 控件。
- API：`PaintSurface.SetInkLook(InkLook)`、`SetInkBoundaryDebug(InkBoundaryDebug)`、`InvalidateInkBoundary()`。原有 `SetAppearance` / `SetRoundedEdges` 仍控制原外观选项。

## 实现

`InkBoundaryTopology` 从静态网格 UV 与世界坐标重建仿射平面图块，依据真实共享边连接同平面图块与不超过约 45° 的坡面。不同面的 UV 即便相邻，也不会共享距离种子。相邻图块展开到接收图块平面，读取各自覆盖状态，并按有效图块占用率归一化补边采样，防止弱覆盖在 UV 缝处生成假外沿；真实几何端点不视为裸地墨迹边界。

GPU 将覆盖阈值边缘和阵营分界转换为亚纹素种子，经有限半径 Jump Flood 与一步邻域细化写入两通道距离缓存。范围为 ±0.24 m。优先 RG16F，直接存储有符号米值，避免把零点移到 0.5 后损失半浮点精度；备用 RG16 UNorm 使用显式编码。种子暂存使用全浮点坐标。

每笔绘制仅在实际接收墨迹的面和真实接缝邻居上，标记包含肩部影响半径的 32 纹素块，按图块合并脏矩形；无共享边的附近表面不会重建。生成时再加传播所需 halo。一次表面更新先提交所有实验表面的绘制队列，再生成边界。GPU 绘制限制在局部 viewport，共用最大尺寸暂存；静止状态不重建。清空、恢复、邻接变化触发重建，最终实例释放时回收公共资源，实际 RT 分配计入原预算。

外沿采用单调圆顶肩部，避免小墨滴出现空心凸环。法线取轻度过滤后的连续距离梯度；内部继续读原持久高度，进行同阵营、有效图块范围内的五点归一化过滤，再混入 25% 高频残差。中心样本复用，预乘数据直接加权，减少逐像素纹理读取。双色交界使用对称浅凹过渡，在裸地交会处衰减，不制造裸地接触黑线，也不记录先后涂覆顺序。Forward 和 DepthNormals 共用法线函数。

轮廓只在显示采样中平滑，世界位移严格限制为 `min(0.5 个局部覆盖纹素, 1 cm)`。双侧距离净空检查会在窄条、小孔洞和小墨滴附近关闭位移；不跨无效 UV 图块采样。`SoftContourSmoothing=0` 可关闭这项显示修正。厚度仍来自法线，没有网格侧面或视差遮挡。手机保留外沿、双色边界与宏观起伏，降低细法线；反射继续使用项目的环境反射回退，不新增屏幕空间反射。

实验初值：肩宽 0.12 m、表观高度 0.018 m、光滑度 0.76、接触压暗最多 3%、内部起伏 0.002 m、宏观起伏 0.001 m、过滤半径 0.06 m、细法线 0.12。双色高度为外沿 25%，宽度为 50%。9 组截图覆盖宽度 0.08/0.12/0.16 × 高度 0.012/0.018/0.024。

## 验证与复现

独立验证项目应复制当前 Assets、Packages、ProjectSettings 和 Tools，不要与运行中的编辑器共享 Library。

```powershell
./Tools/InkSoftEdges/validate.ps1 -ValidationProject D:/XPHUNITY/ProjectSplatoon-InkSoftValidation-20260923 -ReportDirectory D:/XPHUNITY/ProjectSplatoon/Reports/InkSoftEdges/Final
python Tools/InkSoftEdges/run_player_validation.py --exe Reports/InkSoftEdges/Final/Windows/InkLan.exe --output Reports/InkSoftEdges/Player
python Tools/InkSoftEdges/run_network_validation.py --exe Reports/InkSoftEdges/Final/Windows/InkLan.exe --output Reports/InkSoftEdges/Network
```

图形验证检查非空距离渐变、静止缓存复用、32 种形状脏区/全量误差、恢复后缓存、外观切换数据不变、裸地逐像素一致、共享表面与未切分平面的距离一致、全地图资源释放和显存预算。性能为实际 1080p Player，预热 10 秒、采样 60 秒、串行 A/B、重复三轮，静态与持续涂色分别判定 GPU P95 增量 ≤0.5 ms。视频另行录制，不混入性能样本。

Android Shader 编译不代表手机实机表现或性能。本机多进程联机不代表物理双机局域网验收。截图、测试通过和性能达标都不自动改变正式默认效果；最终观感由用户确认。
