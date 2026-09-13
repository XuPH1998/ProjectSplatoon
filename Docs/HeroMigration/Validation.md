# 英雄合表验证记录

验证环境：Unity 6000.3.9f1，Windows，现有编辑器。数值基线为 `b43f28f784c4a4723b15599a6b5e933b4723674f`。本次没有提交 Git 或构建玩家包。

测试数量、执行时间和原始结果文件哈希保存于 [Verification-Results.json](Verification-Results.json)。

## 数据、资源与编译

- 源工作簿和结构定义修改后运行 `cmd /c Config\Luban\gen_luban.bat` 成功；生成 C#/JSON 未手工改写。
- `Tools/HeroMigration/verify.py` 通过：5 个英雄 × 69 字段 = 345 个值与迁移前记录相符；四张源工作簿与生成 JSON 一致。
- 角色重力 22、墨弹重力 9.8 分开映射；射击移速保留原武器逐枪值 3.6 / 4 / 3 / 3.4 / 2.4。
- 五个英雄全部使用 `Character/RifleGirl`、`Weapon/RifleGirlRifle`；真实 Addressables 连接流程只持有两个去重后的模型资源。
- 旧角色/武器工作簿、生成类型、JSON 和运行时配置入口已移除；Addressables 仅保留新 `tbhero` 注册及 `Luban` 标签。
- Unity 编译成功；玩家协议 8、涂色协议 5。不同英雄配置通过全表签名参与兼容性判断，非默认英雄表现参数变化亦改变签名。
- 建表工具复制测试成功，输出的 4 张工作簿及结构定义与源文件逐字节相同。
- `Tools/WeaponReference/audit.py --check` 通过：上一轮 9 个独立标量修正保留，证据不足而保留的参数未动，6 份原始参考文件哈希一致。

## 自动化功能验证

英雄迁移 EditMode：**130 / 130 通过**。结果文件：`Logs/WeaponReference/Hero-EditMode-tests.xml`。

包含五种射击机制、连发/蓄力取消、阶段和生命限制、在途墨弹保留发射英雄、快照字段序列化、英雄切换资源上限、移动与回墨按所选英雄取值、模型复用/替换/挂点/无残留、地址去重、替代地址装配、加载失败和取消后重启、内容签名、发现与涂色/碰撞回归。

区分英雄属性的测试仅在内存构造英雄 2 的生命 80、墨量 60、移速 2、回墨 4；验证运动、恢复、参数显示及真实重生使用该英雄记录。权威源表没有这些测试数值。

真实房主 Play Mode 已验证：实际 Addressables 启动与 NGO 房主、五个英雄共享外观实例、热身补墨、DEBUG 保留生命与墨量、非法 ID 拒绝、所选英雄重生、退出释放资源、重新进入默认英雄 1。`Logs/HeroMigration/play-validation.txt` 保存流程结果。

英雄迁移 PlayMode：**2 / 2 通过**，含真实房主与 UI 流程以及全部 87 组 Play Mode 测量。结果文件：`Logs/WeaponReference/Hero-PlayMode-tests.xml`。UI 使用真实 Input System H/Esc 键和 IMGUI 鼠标事件，验证列表点击仅预览、确认切换、热身补墨、对局 H 禁止、DEBUG 切换、Esc 返回和无误发射；`Logs/HeroMigration/ui-validation.txt` 为 `passed=True`。已查看 1280×720 的热身选择、蓄力英雄预览与美少女持枪截图。

## 87 组测量基线

迁移前完整测量临时保留在 `Temp/HeroMigration/BaselineMeasurements`；永久保存的每场景轨迹、墨迹和归属哈希见 [Measurement-Comparison.csv](Measurement-Comparison.csv)。

- 87 个场景的 JSON 数值结果一致，仅排除实际执行耗时 `simulationMilliseconds`。
- 87 份逐步轨迹 CSV 与 87 份涂色事件 CSV 逐字节一致。
- 87 个归属网格哈希一致。EditMode 与实际 Play Mode 捕获均可通过 `verify.py --measurements <目录>` 比较。
- 场景包括五英雄、蓄力多个端点、近中远墙面、平/仰/俯射、坡面、遮挡、高差及 30/60/144 Hz 驱动。该结论证明本次结构迁移没有改变原有测量结果，不代表通过原作实机射程标定。

## 测试夹具修复

原蓄力后潜墨测试使用地板接缝/出生保护区位置，且传送后的 CharacterController 尚未接地。改为出生点前方的可涂区域，并先建立真实地面接触，再测试释放后立即潜墨与预测重放；未修改运动或射击算法。原回墨断言同步为上一轮已确认的 `100/180` 每帧。

Play Mode 集成协程放在进入模式、完成域重载之后创建，避免 Unity 测试恢复时丢失捕获变量；连接后显式等待本地网络玩家生成。

旧 UI 测试假设固定工具栏高度，当前编辑器停靠布局下点击位置偏移。测试先在大厅背景采样游戏收到的鼠标位置，校准 Game View 偏移，再执行真实按钮点击；恢复测试前的 Game View 分辨率，不退出编辑器。

## 尚未执行的独立验收

- 真实远端客户端的外观、英雄切换与输入确认，中途加入、断线重连，以及真实双机 LAN。当前房主流程和序列化/规则测试不替代这些结果。
- 新模型地址的测试使用满足现有 Humanoid 契约的测试副本；未覆盖不同骨架、英雄专用碰撞体或未提供的新美术资源。
- CPU/GPU 墨水着色、四人持续射击性能与原作实机手感没有重新做设备验收；本次未修改笔刷或射击算法。

本次完成度仅针对英雄配置结构迁移，不改变此前五武器原作复刻报告中的证据缺口。
