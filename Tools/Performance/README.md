# 四人帧率验证

`FramePerformanceProbe` 仅在 Editor / Development Player 显式传入 `-frameProbe` 时启用。记录正常主相机帧，不执行额外 RenderRequest，不修改输入或武器配置。GPU 数据缺失时 `gpuTimingAvailable=false`，不把 CPU 渲染提交时间当作 GPU 时间。

构建调用 `Splatoon.Editor.FramePerformanceBuild.BuildWindows`，它临时开启 FrameTimingStats，并调用正式 `PrototypeBuilder.BuildWindows` 入口；完成后恢复该设置。建议在独立验证工程执行。

四台电脑各自启动同一个包，然后正常创建/加入房间：

```powershell
.\InkLan.exe -frameProbe 60 -frameProbePlayers 4 -frameProbeWarmup 5 -frameProbeOutput C:\Perf\fire-run1.json
```

全部四人同步完成后预热 5 秒，采样 60 秒；期间人数变化会将结果标记为不完整。分别做静止、持续交火、交火加潜墨，固定地图/英雄/机位/分辨率，每组重复三次，原版和优化版都保留 JSON、包版本、显卡和分辨率。手动采样不会自动退出。CPU/GPU Profiler 中另外查看 `Splatoon.Paint.GPU`、`InkFlightMetaballs`、`RenderMetaballsScreenSpace` 和 `Splatoon.*` 标记，定位各阶段的成本。

辅助同机矩阵：

```powershell
python Tools/Performance/run_local_matrix.py --exe Builds/Windows/InkLan.exe --output Reports/FourPlayerPerformance/LocalMatrix --visible --playing
```

默认覆盖 1/2/4 人 × 静止/交火/交火加潜墨 × 三次 × 60 秒，每个进程独立。上面的 `--visible` 显示 Player 窗口，`--playing` 在两人以上时启动正式对局；单人仍是练习。省略 `--visible` 时隐藏窗口，若没有正常主相机帧，结果标为不完整。使用已有开发输入夹具，持续射击场景会补充墨水；不额外渲染。这个矩阵会争抢同一台电脑的 CPU/GPU，不能替代四台真实设备的验收，也不能据此承诺目标设备 60 FPS。

快速功能验证可传 `--players 4 --scenarios fire --repeats 1 --seconds 60`。`-frameProbeQuit` 仅用于自动测试；房主应使用比客户端更长的 `-frameProbeQuitDelay`，避免采样结束时过早断开房间。

补同步与换回合检查可使用 `--players 4 --scenarios swim --repeats 1 --seconds 190 --playing --late-join-seconds 12`，最后一人在产生墨迹后加入，之后运行到自然结束和下一回合。隐藏窗口仍可提供联机/生命周期证据，但没有主相机帧时不能作为渲染帧率验收。

CPU 与 GPU 结果一致性测试在 `FrameOptimizationTests`，使用修改前冻结的算法作为对照。测试输出包括 CPU 绘墨与飞行更新的原版/优化版耗时、整张地图的 GPU 纹理和 CPU 归属逐字节对照。它们是隔离算法测量，不代表整场 FPS 提升。
