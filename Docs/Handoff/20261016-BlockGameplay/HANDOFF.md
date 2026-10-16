# ProjectSplatoon 积木玩法：跨电脑 Codex 交接

交接日期：2026-10-16（新加坡时间）。仓库：`https://github.com/XuPH1998/ProjectSplatoon.git`，分支：`dev_积木玩法`。**本文件所在的 Git 提交就是本次交接版本**；用 `git log -1` 获取提交号，不以旧聊天里的包号判断源码版本。

用户最后指示：**上传目前改动到 Git，生成交接文档，然后关闭目标模式、停止开发，换另一台电脑的 Codex 接力。** 本次只收尾、归档、提交和推送，不再修性能或继续测试。旧会话 Goal 按用户要求暂停，不能把仍未通过的项目记为完成。新电脑上的 Goal 不会从旧会话自动迁移；用户要求接力实施后，按本文范围创建新 Goal。

## 1. 新电脑先从这里开始

1. 拉取 `dev_积木玩法` 分支的最新提交，阅读本文、[玩法说明](../../BlockGameplay.md)、[性能说明](../../BlockTerrainBenchmark.md)和[调参说明](../../BlockTuningGuide.md)。保护新电脑上已有的未提交工作，避免直接覆盖已有 checkout。
2. 使用 Unity **6000.3.9f1**，URP **17.3.0**；安装 Windows Build Support。`Packages/manifest.json` 和 lock 文件已随本次源码提交，Pipeline 为 `com.unity.pipeline 0.8.0-exp.1`。恢复工具需要 Python **3.11 或更新版本**，原机为 3.13.5；Windows/PowerShell。先让项目正常导入和编译，不能把原机缓存 DLL 当成新机编译结果。
3. 交接的关键报告和本地工具在 [snapshot](snapshot) 中，路径按原仓库结构保留；[snapshot-manifest.json](snapshot-manifest.json) 记录原路径、拷贝路径、字节数和 SHA256。可以运行 `python Docs/Handoff/20261016-BlockGameplay/restore-local-files.py` 进行只读核对；加 `--apply` 才复制缺失文件到本机 `Reports`，冲突文件会拒绝覆盖。
4. `Builds`、`Library`、`Temp`、`UserSettings` 和新测试输出按现有 `.gitignore` 排除。**仅 Git 拉取不会获得 Windows37 可执行包或完整历史 Reports。** 新机应从交接源码正式构建；需要复核原包时，从旧电脑另拷贝完整 `Builds/Blocks-20261016-block37` 文件夹，并按随附清单核对全部 359 文件。
5. 原机的 PID、CLI 端口连接、注册表 SID、MVID、隔离桌面句柄、绝对路径和冻结请求都不能直接作为新机的实时状态。原工具有 `D:/XPHUNITY/ProjectSplatoon` 等固定路径；若新路径不同，创建新版本、重新绑定请求和哈希，保留原证据。禁止直接运行指向已有报告目录的旧请求。

新 clone 示例（自行选择目标目录）：

```powershell
git clone --branch "dev_积木玩法" https://github.com/XuPH1998/ProjectSplatoon.git ProjectSplatoon
```

## 2. 已确定的最终玩法，继续接力时不得改变

核心循环：射击建造、堆出路线和掩体、沿己方积木弦化移动、拆毁对方建筑，三分钟后按存活积木总量结算。

| 规则 | 已批准内容 |
|---|---|
| 格子和地图 | 0.10 米立方体；16³ 稀疏区块，每区块 1.6 米；沿用地图水平边界，距基准地面 32 米顶界，无局部限高 |
| 支撑和重力 | 墙面首层锚固；突出层需要下方支撑；失支撑后列段下落，不做液体扩散；保留阵营；落块可被攻击但不承托角色 |
| 计分 | 已形成的稳定格和下落格都计存活数量；弹丸和装饰碎片不计；下落、坍塌和重新落定不改变数量；实际生成、删除才改变数量；三分钟同数平局 |
| 保护 | 出生和换装区禁止生成，跳过玩家占据空间；落定冲突按安全位置／最近安全点／本队出生点恢复；不加自然消失、砸伤或天花板附着 |
| 普通建造 | 命中地图铺首层；命中己方格沿外法线堆积；命中敌格只拆，本次攻击结束，不能顺带穿透打人或免费建造 |
| 付费预算 | 每材料 32 格；主攻击整组最多 512 格；沿途和脚下合计最多预算 25%；多弹丸、反弹、分裂、派生共享一次付费额度；部署物使用有限额度；救援不改地形 |
| 普通拆除 | MG 4、窄域 6、射击枪 8、旋转枪每实际弹 8、开尔文 12、满溢泡泡四颗整组共享 32；0.20 米邻域从近到远拆敌格；每格完整存在或删除 |
| 爆破 | 爆炸泼桶、快速爆破枪、爆泡霰弹枪只拆；主／副／大招走统一接口；己方半径为敌方 50%；静态地图挡爆破，积木不挡同次结构爆破；先确定完整目标再分批删除；玩家伤害另算 |
| 移动 | 地面／墙面弦化只允许稳定己方格；连续邻域拟合坡面，不跨空洞；移动、纸片姿态、受击体一致；失支撑下一模拟退出附着，保留空中形态和翻越 |
| 资源 | 原墨量为材料；己方格快恢复；移除敌墨持续掉血；实际建造和拆敌每 32 格充 1 点，主攻击整组最多 2，副武器整次最多 10；拆己、重力、大招自身变化不充能 |
| 联机和原子性 | 房主裁决；格数据、网格、Collider、分数在模拟边界同时发布；查询只读已发布版本；分块增量／版本／哈希／分片重传／完整快照；迟加入、重连、回合清空；协议与内容签名同步更新 |
| 首版覆盖 | 全部 9 主武器、13 副武器、5 大招、中文 HUD、Windows 包；保留 CharacterController；材料和武器沿原配置链路；Luban 修改源工作簿后正式生成 |

详细验收以用户后续批准的两个文件为准：[首版标准](snapshot/Reports/BlockTerrain/GoalScopeFirstPlayable20261012.md)、[高塔与级联整组后移](snapshot/Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md)。旧 Goal 工具目标文字没有被重写，其中原严格门槛已被这两次明确批准覆盖。

## 3. 当前已完成到哪里

积木核心、付费建造和拆毁、重力、原子地形发布、武器接入、弦化、材料／充能／计分、中文 HUD、联机同步及验收工具已有实现，均随源码交接。**有实现不代表全部最终验收通过。** 后续仍应按影响范围验证修改。

用户最新优先项“所有枪械子弹看不到，子弹用方块”已单独修复，形成 **Windows37**：

- `BlockFlightPresentation.cs` 从 Resources 读取序列化的实例化材质，克隆双方材质，沿原权威弹道显示立方体。
- `InkPresentation.cs` 在旧主相机为空、禁用或失活后自动重取 `Camera.main`。
- 新增 `Assets/Splatoon/Runtime/Blocks/Resources/BlockProjectileInstanced.mat`，保留正式打包中的实例化着色器变体。正式构建实测顶点、片元各保留两个变体。
- `BlockProjectileVisualSmoke.cs` 仅显式 `-blockProjectileVisualOutput` 时启用；使用真实武器配置、合成 `InkShot`、普通 `Spawn`、自动 `LateUpdate` 和正常主相机背缓冲，默认游戏不启用。
- 未修改本轮射速、耗材、伤害、弹道、建造／拆毁规则和武器数值。

本轮实际证明：700 源码及 metadata 条目绑定、Unity 编译无错误；fresh203 实际 **10/10** 针对性测试；Editor **18/18** 武器／阵营 GPU 案例；独立 Windows37 Player **18/18** 显示案例、禁用旧相机后自动切换通过。实际图和源包逐项核验，偏好已恢复。详见 [Windows37 原验证报告](snapshot/Reports/BlockTerrain/CubeProjectileRepair20261016/Validation.md)、[Root 收据](snapshot/Reports/BlockTerrain/CubeProjectileRepair20261016/root-acceptance.json)、[显示结果](snapshot/Reports/BlockTerrain/CubeProjectileRepair20261016/PlayerVisual01/Pixels/summary.json)、[接触图](snapshot/Reports/BlockTerrain/CubeProjectileRepair20261016/PlayerVisual01/cube-projectile-contact-sheet.png)。

显示探针只证明显示，**没有证明付费开枪、权威命中、RPC、全部预期实例数量或完整战斗验收**。合成显示案例不能替代这些门槛。

历史功能证据应保留原归属：一次完整库集中执行为 1783 项（1746 通过、37 失败）；37 项后来用关联类／叶复测和明确复用收敛，没有第二次完整库 1783 全绿。此前 19 类 184 项及复用记录属于旧图；Windows36 关联 22 项也含历史复用。不要把这些数字全部合并成“Windows37 全量实测通过”。Windows36 自然三分钟比赛及中文 HUD 有历史通过，尚未自动转记 Windows37 同包通过。

## 4. 最新包的精确身份

原机目录：`D:\XPHUNITY\ProjectSplatoon\Builds\Blocks-20261016-block37\InkLan.exe`。必须拷贝整个 block37 目录。正式 Addressables + Windows Development Player 于 **2026-10-16 20:35:01** 构建完成；359 文件，322,179,584 字节。

| 身份 | 原 Windows37 值 |
|---|---|
| Player Runtime MVID | `ccd65caf-bf48-42b1-9543-3a12417940df` |
| `InkLan_Data/Managed/Splatoon.dll` SHA256 | `69353fdffa928efe25245be9d1e600baa9e521a5db3354a4b545df83f6de6c3c` |
| EXE SHA256 | `c7e054edf3cd28b374e29139288d6d3cb0077a75f677ebd0dea5ebdd91c53607` |
| 完整包清单文件 SHA256 | `28418b58f78afbafc95493fe92058631997cd7b9b73d05816910ecd87b910e56` |
| 2944 构建输入清单 SHA256 | `7bc438c80cce00ca7886cb4462d6a95f30bde28cedc4bcf49955232ea9e7cfda` |

EXE 启动壳与 Windows36 相同，不能只查 EXE 判断包是否含修复，应查 Runtime DLL 和完整清单。新机重新构建的 MVID 可以不同，必须建立新机本次的来源绑定，不能照抄这些旧值。

交接前最后一次核对：原机 2944 构建输入和 359 包文件仍全部匹配，零差异，见 [最后核验](snapshot/Reports/BlockTerrain/Handoff20261016/source-package-last-check.json)。本次 Git 提交包括后续报告和交接材料；没有在该包构建后继续修改运行时源码。

## 5. 剩余事项：四类，均未完成

### A. 四场正式性能与积压修复

| 场景 | 必须保留 | Windows37 当前状态 |
|---|---|---|
| `steady250k` | 八个独立 Player、至少25万存活格、180秒 | 已实测，明确失败 |
| `combat250k` | 八人持续交火、至少25万存活格、300秒 | 本轮未执行 |
| `blast8` | 八次同时爆破、完整目标／敌友半径、120秒 | 本轮未执行 |
| `scattered100k` | 十万零散格、八人、180秒 | 本轮未执行 |

首版门槛：正常主相机真实 1080p，帧时 P95 ≤15.5ms／P99 ≤16.67ms，普通命中至碰撞发布 P95 ≤200ms，八爆直接拆除全部完成 ≤2000ms。积木规模、尺寸、完整攻击范围、守恒、碰撞和付款正确性保持。偶发长帧只列原型风险，但帧时分位数失败仍阻断。

Windows37 唯一完整正式性能结果：180.1076607秒，7404帧，八人持续射击覆盖1.0，积木288350至334926格，RTX3060／i7-11700K／32GB／D3D12。帧时 **P95 74.6107ms、P99 399.9851ms**，普通主攻击发布 **P95 7033.3333ms**；全部未达标。GPU P95仅2.8887ms，主线程P95 74.5923ms；0错误、0失败事务；682个>33ms样本保留。单次地形事务创建至提交P95 71.5519ms不含前方排队，不能代替攻击总响应。

原 Tool stop-on-failure 后停止，不能把未运行三场算通过。原报告、正常主相机截图和退出记录见 [正式性能观察](snapshot/Reports/BlockTerrain/Windows37AcceptancePreparation01/PerformanceRun01/RootObservation20261016.md)。旧 Windows36 性能也失败，而且有用户手动 Player 重叠，不能代替干净验收。

下一步原计划是定位主线程长帧和指令积压。源码中 `BlockTerrain.RaycastFalling` 每次遍历全部下落段，在计算运动偏移前没有水平候选筛选；这是**尚未证实的候选耗时点，未实施任何优化**，不能当成已经定位的根因。使用现有 CPU 诊断入口取得可用调用栈后决定修复，避免重复跑同一份未修改的正式失败负载。

### B. 100ms／1% 弱网移动纠正

保留35秒、充分真实移动ACK和己方稳定积木支撑样本，纠正P95 ≤0.15米、P99 ≤0.60米。客户端预测、地形版本依赖、移动纠正及恢复一致性必须在最终包上实测。现阶段不能宣称通过。

### C. 六组独立 Player 联机与恢复

2人和8人分别跑 `(0ms,0%)`、`(100ms,1%)`、`(200ms,3%)`，共六组。完整检查数量、阵营、位置、区块哈希、下落状态、存活计分、迟加入、重连、回合清空及重建。普通重力必须最终落定且守恒；不能因极限压力后移而忽略正常联机重力排空。现阶段仍未完成最终同包矩阵。

### D. 最终同包交付验收及报告

修复后正式构建同源新包，串行完成相应功能回归、四场性能、弱网／六组网络、自然三分钟比赛和回合流程、正常中文HUD、正式资源加载、协议／内容签名、来源一致性、已知风险及调参说明。当前 Windows37 只是含方块子弹显示修复的已验证候选，不能称整体最终验收通过。

**已后移且不计本次 Goal：**32米高塔和两万格级联的专属压力／最终碰撞／性能整组；真实多机和人工观感两项外部验收。不要重新加回强制门槛，也不要写成通过。32米建造顶界和普通重力功能仍保留。

## 6. 最后一次 CPU 诊断和停机现场

- `Windows37CpuDiagnosis01/CpuRun01`：启动前缺少光标保护环境变量被拒绝，未启动 Player；已保留失败。
- `Windows37CpuDiagnosis02/CpuRun01/prototype-v1/steady250k/0fbfaddc`：原八人／1080p／180秒请求，开启现有 `-blockBenchmarkCpuProfile`。约150秒观察后，Player、启动器及项目Editor全部消失，**没有正常终态、完整180秒报告或CPU sidecar**。退出原因未知，不能认定为Unity原生崩溃，也不能作为验收通过。
- 本地遗留 `host.sampling.cpu.raw` 为4,293,000,736字节，SHA256 `986b2dc9efb9d2716eeba406a0755842647ce6ac50f767e7be90194d2f40bc20`；最后原生flush和窗口完整性未证实。此4.29GB文件不进Git。若另拷贝，可尝试离线读取内部完整帧作诊断，必须标注中断样本范围。
- 诊断中的150秒 checkpoint 与正式性能跑法、落块状态不同，不能混用数字或用开启Profiler的结果替代无Profiler正式验收。原始checkpoint与中断收据已随交接归档。
- 中断后核对原19键两注册表视图，当前偏好与测试前基线**完全一致**；未写注册表。仅将遗留 pending 文件归档移出阻塞位置，见 [偏好恢复收据](snapshot/Reports/BlockTerrain/Windows37CpuDiagnosis02/CpuRun01/preference-recovery.json)。不要盲目恢复旧电脑注册表快照到新机。
- 后来仅恢复 Editor78316 作只读核对。按用户停止要求，确认非Play、非编译、非构建且无脏场景后正常退出，原生exit0，隐藏keeper也已结束；旧机当前没有本项目Player、测试或构建在运行。其他项目Editor19136未操作。没有执行准备好的离线Profiler提取，也没有实施性能补丁。

## 7. 后台运行约束必须保留

用户此前多次被Unity窗口和鼠标锁定打断工作，**不要让测试抢前台**。

- 原机隔离桌面名 `CodexBlockTests20261013a`，用户输入桌面 `Default`。新机需要建立并验证自己的非输入桌面，不能切换输入桌面、聚焦GameView、激活／移动／显示用户窗口或注入系统输入。
- 每个Player都传 `-noSystemCursor`，启动链环境设 `SPLATOON_DISABLE_SYSTEM_CURSOR=1`；缺失时先停止入口，不启动Player。
- 测试、构建、Player验收由一个Root串行调度，复用同项目Editor；不能因为观察超时就重启仍存活进程。核对真实PID／句柄和终态，过期状态文件不证明仍运行。
- 保存和精确恢复测试涉及的文件、19个PlayerPrefs键及两注册表视图；同产品用户手动Player仍活着时不得抢先恢复旧值。
- 禁止WMI／CIM／vmms／winmgmt服务操作。此前用户一次有条件授权未触发，不能迁移为新机通用授权。
- CLI Editor命令明确 `--project-path`、`--caller plugin --skill unity-cli --format json`，使用无控制台启动。`IsolatedDesktopProbe.Read/Verify` 是只读检查；**不要调用 `Run`，它会聚焦GameView。**
- 不修改domain reload设置。其他项目Editor和用户工作不操作。新开发默认不提交／发布，除非用户后续另行要求；本次提交和推送是用户单独授权的交接动作。

隔离启动、原始偏好保护、回归收集器、已结束入口和提取器的小型源文件已选入 snapshot；它们是接力参考，使用前重新绑定本机路径／输出／来源，不要照跑带旧包哈希和旧输出目录的冻结请求。

## 8. 实现入口和下一步顺序

| 内容 | 主要源码入口 |
|---|---|
| 稀疏格、阵营、计数、下落段、wire | `Assets/Splatoon/Runtime/Blocks/BlockWorld.cs`、`BlockContracts.cs` |
| 建造／拆毁、后台mesh与Collider、原子提交 | `BlockTerrain.cs`、`BlockTerrain.Build*.cs`、`BlockTerrain.Gravity*.cs`、`BlockMeshBuilder.cs`、`BlockWorkerQueue.cs` |
| 公平调度及发布预算 | `BlockTerrain.Fair*.cs`、`BlockTerrain.Publication*.cs`；改动时保护付款、FIFO、可见版本和原子性 |
| 攻击预算与收益 | `BlockAttackService.cs`、`InkProjectileService.cs`、副／特殊武器服务 |
| 方块弹丸与材质 | `BlockFlightPresentation.cs`、`InkPresentation.cs`、`Runtime/Blocks/Resources/BlockProjectile*` |
| 移动／弦化／预测 | `PlayerSimulation.cs`、`SwimBody.cs`、`PaperPoseSimulation.cs`、`PrototypePlayer.Prediction.cs` |
| 联机与内容签名 | `PrototypeMatch.BlockSync.cs`、`NetworkContracts.cs`、`GameplayContentSignature.cs`、`NgLanSessionService.cs` |
| 性能／网络观测 | `BlockTerrainBenchmark.cs`、`BlockBenchmarkCpuWindowCapture.cs`、`BlockNetworkProbe*.cs`、`Tools/BlockTerrain/*acceptance.py` |
| 正式构建 | `Splatoon.Editor.BlockGameplayBuilder.BuildWindowsTo(outputDirectory)`，调用正式Addressables链路；新目录避免覆盖旧包 |
| 参数 | `Config/Luban/source/TbGlobal.xlsx`、`TbRoomMode.xlsx`，生成后同步JSON及C#；九把武器原Asset配置链路 |

用户要求接力开发后，建议先完成新机导入／编译与包身份绑定，定位当前性能失败，再按影响范围修复和回归，正式出新包，逐个完成四场性能、纠正／六组网络和同包比赛／HUD。已有针对性通过后不反复全库回归；新改动影响扩大时再扩大验证。不要删除历史失败、把缺测写Passed或用降低规模掩盖失败。

可直接给新电脑Codex的接力指令：

> 先阅读 Docs/Handoff/20261016-BlockGameplay/HANDOFF.md 和两个已批准范围文件，核对当前 dev_积木玩法 提交及新电脑环境。在本机创建 Goal，继续完成其中四类未完成验收；保留0.10米、32米顶界、完整规模、付款／守恒／真实碰撞及联机一致性。优先定位并修复Windows37正式性能跑法的主线程长帧和攻击排队，不把中断CPU样本或旧包结果当最终通过。测试和构建串行，非输入桌面、禁系统鼠标，不打断前台工作；保留历史证据。未经新的要求不提交或发布。只有必需首版门槛全部真实通过才完成Goal。

## 9. 归档范围和证据限制

snapshot包含60份精选、逐字复制的报告和工具，共8,593,001字节，不是完整旧机Reports备份。目录内 `.gitattributes` 关闭snapshot的文本换行转换，保证跨电脑checkout后SHA256仍可核对。原图集、其余历史XML／日志、Windows36/37包、4.29GB CPU raw、Unity缓存和用户设置仍在旧机。清单明确列出哪些文件随Git传递；文档中的其他历史原机路径仅用于定位，不能假设新机存在。

最后停机和Git上传回执保存在旧机 `Reports/BlockTerrain/Handoff20261016`。本次只做交接和上传，无新增玩法／性能实现，无进一步运行回归，无部署或发布。整体玩法验收尚未完成。
