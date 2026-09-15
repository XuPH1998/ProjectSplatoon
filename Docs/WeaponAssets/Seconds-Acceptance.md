# 武器配置中文化、枚举与秒制迁移验收

2026-09-15，实施前基线为 `bf8dd19`。验证使用 Unity 6000.3.9f1 和独立工程 `D:/XPHUNITY/ProjectSplatoon-WeaponAssets-Validation-20260915`。用户原有编辑器未被启动、退出或切换 Play Mode。

## 已完成

- 64 个配置字段使用显式中文 Inspector 标签、单位、提示及中文校验信息；发射与枪口模式从资产到运行时均采用 Config 层枚举，保留 0–4 / 0–1 序列化映射。
- 16 个整数参考帧参数及其运行时字段改为 `double` 秒。六份资产按实施时实际值除以 60，已有秒数及所有其他值保持。源表、生成 C#/JSON、资产路径、GUID 与 Addressables 注册均保持。
- 火箭筒中间蓄力维持 1/60 秒量化，支持非整参考帧的满蓄端点。旋转枪按秒推进，保留边界容差、包含首末弹的窗口、33/66 发、慢蓄、满蓄身份和实际余额退款。
- 蓄力快照使用双精度秒，玩家协议 20 → 21、武器模拟版本 2 → 3；配置签名、旧弹丸快照、热更新、武器详情、HUD 和重建/验证脚本同步适配。

迁移前后原始数值及元数据哈希见 [Migration.json](../../Tools/ValidationData/WeaponSeconds/Migration.json)。工具先验证全部资产，再记录审计并替换；重复执行跳过已迁移资产。

## 验证结果

| 检查 | 结果与证据 |
|---|---|
| 当前提交基线 | 371 项，358 通过、13 失败。[XML](Seconds-Evidence/baseline.xml) |
| 最终完整相关回归 | **386 项，374 通过、12 失败**；剩余失败的名称和断言文本均与当前基线一致，覆盖范围内无新增失败。[XML](Seconds-Evidence/regression.xml)、[逐项比较](Seconds-Evidence/baseline-comparison.json) |
| 原有协议断言 | 修正仍要求协议 19 的旧测试；另将协议 20 的纸片碰撞测试适配为 21，保持其实际签名差异断言 |
| 正常编辑器运行 | **42/42 通过**：含真实自定义 Inspector 绘制、序列化属性修改、撤销/重做、保存后重载及本机调试 Host。[XML](Seconds-Evidence/normal-editor.xml) |
| Python 迁移工具 | **6/6 通过**：六资产逐值对比、自定义值保留、重复运行不写入、异常资产阻止所有写入、审计与元数据保持 |
| 配置与 Addressables | 540 个原始值等效，六资产完整路径注册一致。[静态报告](Seconds-Evidence/static-validation.json) |
| 旋转枪复建/来源审计 | 现有资产和源表未被复建工具覆盖；278 个源文件、10 个 FBX、六名英雄及 GUID 检查通过 |

汇总见 [summary.json](Seconds-Evidence/summary.json)。完整日志与中间诊断保留在本机 `Reports/WeaponSeconds/`。

## 行为覆盖

- 旋转枪 8、119/120/121、149/150 帧对应时刻，33/66 发及默认四参考帧射击间隔；空中/缺墨慢蓄不叠乘、末弹恢复、取消退款及满蓄伤害身份。
- 非整参考帧秒值：起手、回墨锁定、火箭筒满蓄、旋转枪两段蓄力和射击窗口、伤害衰减及直行/减速。四种非法双精度值逐一覆盖全部 16 个时间字段；非法枚举不能替换当前有效配置。
- 快照双精度值序列化及重放；实际弹道与涂墨在 30/60/144 Hz 驱动下保持一致。另有三个旧射手驱动测试仍因历史发数预期失败，已列入基线比较，不能据此称整个旧射手套件通过。
- 调试 Host 使用与 Inspector 相同的 `SerializedProperty` 路径，在蓄力/连射中修改射速、耗墨及小数秒窗口，以及切换发射枚举，检查退款一次与松开重按；在途弹丸保留旧配置。普通房使用入房快照，非法配置/模型继续保留旧版本。[调试房记录](Seconds-Evidence/debug-room-results.txt)
- 同一个真实 Host 的四名权威角色共发射 264 发，各消耗 35 墨。[Host 记录](Seconds-Evidence/host-results.txt)。这不是四个独立客户端。

## 画面与验收边界

[Inspector 上部](Seconds-Evidence/inspector-0.png)、[蓄力与枪口](Seconds-Evidence/inspector-650.png)、[旋转枪与散布](Seconds-Evidence/inspector-1350.png)均直接读取本次编辑器窗口的渲染内容，并已检查中文标签、中文模式下拉及秒数显示。[绘制与截图测试](Seconds-Evidence/inspector.xml)通过。截图中的最短蓄力 `0.137123456789` 来自验证保存精度的临时副本，正式资产仍保留 8/60 秒。

[Game 画面](Seconds-Evidence/game-view.png)来自本次正常编辑器渲染，既有准星、双圈蓄力与实时散布 HUD 保持。编辑操作由测试程序驱动，没有将其描述为人工鼠标/键盘操作。

本次未构建或运行独立客户端，未进行真实双机或目标设备验收。旧版本报告只作历史记录，不替代本次 `bf8dd19` 基线和当前运行结果。
