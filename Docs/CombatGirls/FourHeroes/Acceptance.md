# 验收记录 — 2026-09-13

资源与功能已经接入；下表区分已经取得的证据、最终编译结果和尚未完成的运行验收。没有进行正式 Addressables 构建或发布。

| 检查 | 结果与证据 |
|---|---|
| 来源与引用 | 397 个资源、794 个文件的来源哈希通过；41 个动画文件；8 个正式角色／武器预制体；无重复 GUID、旧 Jammo 引用。见 `static-validation.json`。 |
| Avatar、动画与绑定 | Unity 构建验证通过：四个 Avatar 有效、正式角色无 Missing Script、动作白名单及循环配置正确。见 `asset-validation.txt`。 |
| 配置链 | 5 行 × 72 字段的源工作簿与 Luban JSON 一致；ID 1 和 ID 5 原有数值保持。`validate_four_heroes.py`、`Tools/HeroMigration/verify.py` 均通过。 |
| Edit Mode + 弹道 Play Mode | **93 / 93 通过**。涵盖半自动快点／长按、6 帧缓存、取消、双枪顺序、历史机制、英雄绑定，以及真实 Physics／归属网格测量在 Edit Mode 和 Play Mode 的一致性。原始 XML：`Validation/edit-and-physics-play-results.xml`。 |
| 实际房主玩法 | 已完成真实 Addressables 启动、五英雄切换、双手装配、四位新增角色潜墨死亡与 3 秒重生、重连回房间与资源释放。霰弹 8 个独立碰撞使 100 HP 变为 20 HP，再次推进模拟不重复伤害。见 `Validation/play-validation.txt`。 |
| 实际 UI 操作 | 热身／DEBUG 英雄选择、预览不切换、确认切换、耗墨保留、UI 点击不射击、退出均完成。见 `Validation/ui-validation.txt`。但该合并测试最终因测试工具恢复 Game View 尺寸的索引错误而失败；已修复索引，最终完整复跑未完成，不能将该 XML 标为通过。 |
| 动作与训练场 | 已完成四向／斜向、空中持枪、转身打断、双臂射击层同时回位、俯仰 -65°／+75°、支撑握点、潜墨死亡、前后死亡及重生采样检查。见 `presentation-validation.txt`。 |
| 对照截图 | 16 组源／目标正侧背面及脸部对照，另有 8 张死亡方向图、44 张训练场图，共 84 张。见 [对照浏览页](review.html)。数字差异见 `visual-comparison.json`；部分正式图有新增队伍标记，数据不代表全部姿态像素级一致。 |
| 最终代码编译 | 使用 Unity 6000.3.9f1 自带 Roslyn 和该验证工程生成的响应文件，Config、Runtime、Editor、Tests 四程序集编译均通过；这不替代 Unity IL 后处理和运行验收。见 `Validation/final-compile.json`。 |
| 最终增补验证 | 新增的快照往返、左右枪口独立遮挡测试，以及潜墨恢复不重播旧射击动作的修复，已编译；本轮末尾未取得它们的运行结果。测试工具 Game View 清理修复也待复跑。 |
| 四实例联机 | **未完成**。已经准备真实 NGO/UTP 四实例流程，以及每方向 50±10 ms、1% 丢包的本机 UDP 代理；首次无图形启动在 Animation Rigging 编辑器初始化中原生崩溃，后续正常图形实例停在启动初始化，未形成有效联机验收数据。 |
| 性能 | **未完成**。准备了相同机器、训练场、1280×720、四角色的 RifleGirl 基线及四位新角色比较工具；受后续 Unity 启动异常影响，没有可用的新帧时、Draw Call 或内存对比，不能声称性能通过。 |
| 发布与设备 | 正式 Addressables 构建、发布包、真实双机／四机、延迟丢包下的完整回合、晚加入、远端动画、墙游／翻越组合回归均未完成。 |

验证使用 `Temp/FourHeroesUnity` 的独立工程副本，未关闭用户已有的 Unity 编辑器。四实例尝试使用另外三个 `Temp/FourHeroesNet*` 副本。已停止本轮挂起的验证实例和 UDP 代理；这些 Temp 目录不进入项目资源或 Git。

额外嵌套副本 `Temp/FourHeroesUnity/Temp/FourHeroesUnity` 的删除被自动审批拒绝，工具只返回 `blocked by policy`，因此保留。正式 Assets 中的旧角色引用检查不受影响。

## 实测涂色边界与覆盖

测量走正式相机／枪口、真实 Spawn、碰撞和涂色路径。固定种子、水平瞄准、一次有效发射，网格间距 0.125 m；每颗霰弹计一个落点。原始 87 个场景及两种运行环境的轨迹、涂色和网格结果保存在 `Measurements/`。

| 英雄 | 最远涂色边界 | 配置回填 | 备注 |
|---|---:|---:|---|
| 双枪 | 11.750 m | 11.75 m | 一次右枪发射 |
| 霰弹枪 | 12.875 m | 12.88 m | 一组 8 颗的最远边界 |
| 手枪 | 14.875 m | 14.88 m | 一次精确点射 |

这些值不是有效伤害射程，也不保证从脚下到末端连续覆盖。散布种子、俯仰、场景碰撞都会改变覆盖；霰弹的单颗伤害仍按飞行时间和 6.5 m 伤害范围结算。

## 清理

旧 ID 2–5 原先共用 RifleGirl，无独立旧模型／动作可删除。旧 Jammo 在上一轮已清理，本轮确认其 GUID 无引用；本轮生成材质中未发现无引用副本，详见 `cleanup.json`。保留 RifleGirl、共享依赖和要求保留的全部外观变体。未修改原 CombatGirls 工程，未提交 Git。
