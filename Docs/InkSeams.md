# 坡道与桥面接缝涂色

## 原因与修改

本次对应训练场四条坡道上沿与高台/桥面的接合处。两面夹角约 26.565°，原来的 `PrototypeArena.Apply` 只向共面邻居分发落墨，因此墨迹会被截在接缝处。移开掩体后的 336 次普通地面真实弹道采样没有复现最初怀疑的碰撞法线异常；未采用扩大法线容差的修复。

`GroundPaintSeams` 在地图注册时建立可行走表面的公共边关系。仅连接朝上、坡度及相邻面夹角不超过 45°、公共边误差不超过 5 mm 的计分表面；展开后两面必须位于公共边两侧，只有点接触或存在明显间隙的表面不连接。当前处理边与边的连接，对应坡道上沿；坡道下沿落在大片地面的内部，不属于这类连接。

落墨范围与公共边相交时，以公共边为轴把同一笔落墨展开到接收面，旋转位置、法线及投射方向，保留形状种子、尺寸、强度和裁剪数据。CPU 归属与 GPU 纹理接收同一个展开结果；每个表面每笔最多累计一次。原有共面分发继续处理桥面/高台和平地的分块。

网络仍传输一条原始 `PaintStamp`，主机与客户端使用相同拓扑展开；无需增加消息字段。`PaintProtocolVersion` 从 9 升为 10，拒绝混用旧版涂色逻辑。

## 验证入口

- `InkSeamTests.RealShotCrossesRampTop`：真实步枪/手枪弹道，分别从坡道与平台方向命中接缝；检查两侧归属、敌方覆盖、重放一致性及桥下/墙面隔离。修复前 4 项均失败。
- `InkSeamTests.AllRampTopsJoinOnlyTheirPlatforms`：四条坡道的顶部连接。
- `InkSeamTests.RealProjectilesKeepGroundPlaneAtTileEdges`：两个英雄各 168 次普通地块接缝/四块交点的真实弹道回归。
- `InkSeamTests.GroundSeamRequiresSharedEdgeAndDoesNotExpandShortBrush`：短笔刷不跨越远处接缝；存在实际间隙时不连接。
- `InkSeamPlayTests.HostShootsAcrossSeamsAndRestoresPaint`：Boot/TrainingGround 实际主机、正式 `Spawn`、步枪/手枪首笔与扫射截图，全部表面的 Mask/DisplayMask 快照精确恢复。
- 显式运行 `InkSeamTests.BuildNetworkValidationPlayer` 后，可使用 `Tools/InkEdges/run_network_validation.py --exe Temp/InkSeams/Player/InkLan.exe --output Reports/InkSeams/Network` 检查独立进程的实时同步、晚加入、覆盖续涂和重连。开发验证图案已包含坡道接缝。

本次生成的 XML、CSV、截图及联网结果位于 `Reports/InkSeams`。同机独立进程联机与 Play Mode 图像验证不等于实体双机或目标设备性能验收。

## 2026-09-20 实测结果

- 最终选定回归 **33/33 通过**，包含四条坡道顶部、双向真实射击、敌方覆盖、重放、平地接缝、地图隔离及既有 GPU 批量绘制/CPU 归属对照。结果：`Reports/InkSeams/final-regression.xml`。
- 有画面 Host Play Mode 用例通过：步枪、手枪各 12 发正式 `Spawn`，每个英雄产生 24 笔落墨；所有表面的原始与显示纹理快照恢复逐字节一致。已检查 `hero-1-first.png` 与 `hero-4-sweep.png`，接缝处没有露底中断。原始结果保存在 `Reports/BubbleShotgun/ink-seams-host-fixed.xml` 的 `HostShootsAcrossSeamsAndRestoresPaint` 用例中。
- 正式 Windows 构建成功，输出 `Temp/InkSeams/Player/InkLan.exe`。首轮构建遇到 Windows 1224 文件占用；重试构建日志报告 Success，旧测试包装器随后按默认 180 秒超时报错，已将显式构建用例超时设为 900 秒。未把该包装器 XML 计入通过的回归数。
- 同机两个独立 Windows 进程完成首轮涂色同步：序号 **944**，归属哈希 **4010177981**，**36 个表面的 GPU 状态全部一致**。数据：`Reports/InkSeams/Network-20260920/host-states.json` 与 `early-states.json`。
- **晚加入、后续覆盖同步和重连未完成验收**：D3D11 验证主机在现有 `PrototypeSmoke.Update` 的 `AsyncGPUReadback.Request` 处发生原生崩溃；D3D12 重试未在启动时限内建立主机连接。没有将本次首轮一致性或本地快照恢复等同于这些未通过的验收。
- 串行 Edit Mode 测试曾累计未释放的临时涂色纹理；已在本次用例及相关旧地图/GPU 对照用例的清理阶段显式释放，最终 33 项串行运行通过。构建产生的 Addressables 与项目设置变化已恢复。
