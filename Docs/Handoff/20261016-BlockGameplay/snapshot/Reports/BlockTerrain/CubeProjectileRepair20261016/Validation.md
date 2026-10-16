# Windows37 方块子弹显示修复

2026-10-16，用户反馈 Windows36 的所有枪械射击时没有可见飞行子弹，并要求优先修复、使用方块子弹。本轮修复已经形成独立 Windows 包，并通过包内九把主武器、双方颜色共 18 个显示案例。

可玩包：[Builds/Blocks-20261016-block37/InkLan.exe](../../../Builds/Blocks-20261016-block37/InkLan.exe)。正式 Addressables 和 Development Player 构建完成于 **2026-10-16 20:35:01（新加坡时间）**。独立 Player 显示检查完成于 **20:36:52**。启动时应保留整个 block37 文件夹的配套文件。

## 修复内容

- 方块弹丸使用现有立方体网格和武器弹道；新增开启实例化的 Resources 材质，使正式打包保留实例化着色器变体。构建日志确认顶点、片元各保留两个变体。
- 自动 LateUpdate 在缓存主相机已被禁用或失活后重新选取 Camera.main，避免把后续方块提交给失效相机。
- 已接入的九把主武器继续共用方块显示链路。射速、耗材、武器配置、弹道、伤害、建造与拆毁规则未作本轮修改。

## 实际验证

| 检查 | 实际结果 | 证据 |
|---|---|---|
| Unity 编译与来源 | 700 个源码及 metadata 条目绑定；实际编译无错误 | [compile-binding.json](compile-binding.json) |
| 针对性回归 | BlockPresentationTests 9 项、WeaponPresentationPlayTests 1 项，实际 10/10 Passed；文件和偏好恢复 | [targeted-tests.json](targeted-tests.json) |
| Editor GPU 显示 | 九把武器 × 双方，18 个实际像素案例通过 | [Editor 原图结果](../../InkFlightReference/WeaponPlayMode/20261016-120535-ba80ed33d62746739e906e377d5abb9f/results.txt) |
| 正式构建 | 正式资源及 Windows Player 成功；19 项 API／17 项 raw Editor 偏好恢复 | [build-record.json](BuildRevision04/build-record.json) |
| 包完整性 | 359 文件，322179584 字节；2944 构建输入与完整包清单逐项哈希匹配 | [package-verification.json](BuildRevision04/package-verification.json) |
| 独立 Player 显示 | RTX 3060、D3D12、1920×1080，18/18 实际主相机背缓冲像素案例通过；旧相机存活但禁用，自动切换通过 | [summary.json](PlayerVisual01/Pixels/summary.json) |
| Player 退出与偏好 | Player／harness／bootstrap／launcher 均 exit 0；19 键两注册表视图精确恢复 | [偏好恢复](PlayerVisual01/_desktop-launch/StandalonePreferences/verification.json) |
| 后台隔离观察 | 隔离桌面 CodexBlockTests20261013a，用户输入桌面 Default；9 次采样、8 次含本轮 Player 窗口，未观察到该 Player 窗口位于输入桌面 | [bootstrap.json](PlayerVisual01/_desktop-launch/bootstrap.json) |

包内显示检查使用实际武器配置构造合成 InkShot，经普通 InkPresentation.Spawn、自动 LateUpdate 和当前主相机渲染。每例均比对控制图与发射后的真实画面，正确阵营像素为 156～318139，全部无相反阵营误判。未使用 RenderTexture 或直接 Camera.Render。原始 36 张控制／结果图全部保留；[18 例方块截图汇总](PlayerVisual01/cube-projectile-contact-sheet.png)只对实际像素裁切、放大，便于检查形状。

上述包内探针证明弹丸显示，不替代实际付费射击、权威命中、RPC、全部预期实例数量或完整比赛验收。原有枪械行为保留既有独立证据；不把合成弹检查称为全部战斗或整体 Goal 通过。

Root 最终复核记录：[root-acceptance.json](root-acceptance.json)。其中包含实际 Player Runtime MVID `ccd65caf-bf48-42b1-9543-3a12417940df`、各项证据和原图哈希，以及所有包文件和构建输入在本次运行后仍未变化的核对结果。

## 构建中断记录

首次构建包装器在偏好备份路径检查处停止，尚未调用 builder，见原 [build-failure.json](build-failure.json)。Revision02 确实进入构建，随后原 Editor35536 与启动器退出；没有构建终态或 Player 输出。原日志与备份保留，17 个 raw 偏好在中断后与基线完全一致，未写注册表，见 [中断现场](BuildRevision02/interrupted-build-observation.json)。第一次恢复的 Editor34028 随后也与启动器一起消失，原因未确定，未把它认定为 Unity 崩溃或构建成功。

最终使用独立隐藏启动器恢复同一项目 Editor33144；已核对隔离桌面、无焦点及三程序集身份均与原通过回归的版本一致。Revision04 成功构建并恢复偏好。所有历史失败、中断、Windows36 包和原验收记录保持原样。未操作其他项目 Editor，未进行 WMI、CIM 或服务恢复；没有提交或发布。

## 整体 Goal 的剩余范围

本轮优先的方块弹丸显示门槛已通过。整体 Goal 仍 active，剩余四场性能、100ms／1% 弱网移动纠正、六组独立 Player 联机恢复，以及最终包的完整验收报告。Windows36 自然三分钟比赛和中文 HUD 的历史通过保持其原包归属；Windows36 首场性能失败也保持原结果，不能作为 Windows37 通过。

高塔／两万格级联专项，以及真实多机与人工观感两项外部验收，继续按用户已批准范围排除，不记为 Passed。
