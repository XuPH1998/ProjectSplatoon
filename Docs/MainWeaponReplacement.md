# 斯普拉旋转枪与专业模型枪MG

2026-09-21。英雄6「焰橙」使用斯普拉旋转枪，英雄8「铃芽」使用专业模型枪MG。主武器采用喷3 11.3.0、无技能、100生命／100墨量，空间比例 `S=18/24.037`。英雄ID、角色、武器模型、动画、碰撞体、资源GUID和副武器配置保留。

| 参数 | 斯普拉旋转枪 | 专业模型枪MG |
|---|---:|---:|
| 射速 | 15发/秒 | 15发/秒 |
| 伤害 | 未满蓄/满蓄均32，最低16 | 24，最低12 |
| 伤害衰减区间 | 11–19参考帧 | 8–24参考帧 |
| 蓄力 | 最短8帧，一圈18帧，满蓄27帧 | 无 |
| 弹仓 | 一圈11发，满蓄22发 | 100墨连续200发，关闭回墨 |
| 射击窗口 | 一圈42帧，满蓄84帧 | — |
| 耗墨 | 每发15/22，满蓄15 | 每发0.5 |
| 回墨锁定 | 30帧 | 15帧 |
| 地面散布 | 水平4°，垂直1.2° | 12.63° |
| 空中散布 | 8° | 15.54° |
| 普通/潜墨移速 | 4.31335/8.62670 m/s | 4.67280/9.05804 m/s |
| 蓄力/射击移速 | 3.23501/3.86404 m/s | —/3.23501 m/s |
| 直进 | 8帧 | 3帧 |
| 沿途墨预算/相位分段 | 1.6/6 | 1/8 |

轻加释放时立即发首弹，窗口末弹计入弹仓；第一圈实际末弹在释放后40帧，满蓄在84帧。100生命满伤害分别需要4发、5发。

## 来源与维护

- 原始数据固定于 Leanny/splat3 提交 `7280ff9cde8bb1c5dcef46c700c326471584d2e6`：[WeaponSpinnerQuick](https://raw.githubusercontent.com/Leanny/splat3/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon/WeaponSpinnerQuick.game__GameParameterTable.json)、[WeaponShooterBlaze](https://raw.githubusercontent.com/Leanny/splat3/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon/WeaponShooterBlaze.game__GameParameterTable.json)。源文件、SHA-256、修改前资产、逐字段换算及保留值在 `Tools/ValidationData/MainWeaponReplacement/`。
- 武器数值由 `Tools/WeaponReference/replace_main_weapons.py --apply` 导入；不加参数仅核对。名称和英雄移速位于 `Config/Luban/source/TbHero.xlsx`，执行 `cmd /c Config\Luban\gen_luban.bat` 生成。当前源表仅修改 `Hero!AG9`、`AG11`、`J9`、`K9`，其他OOXML内容与样式保留。
- 当前瞄准/涂墨再导入入口已映射新参考数据。编辑器“安装铃芽与专业模型枪MG”也从同一份完整参数记录重建武器，保留资源引用及数值精度，避免重新安装时恢复旧武器。历史报告、冻结基线及旧Sploosh安装期审计脚本继续描述它们生成时的武器，不能作为当前参数表使用；当前核对入口是 `replace_main_weapons.py`。
- 现有字段和网络快照足够表达本次配置；内容签名包含武器参数及英雄表。校验支持旋转枪32/32伤害、MG独立地空偏置及轻加半径相同的重合近中涂墨节点。

## 项目适配

最短8帧蓄力、预留墨/取消返还、脚下调度及缺省继承值保持项目规则。空中或缺墨共用6倍慢蓄、条件不叠乘；缺墨慢充可补足预留墨，不能把这一路径当成有限墨量总发数实验。初速随机幅度沿用项目的加减区间解释。原始 `ForceSpawnNearestAddNumArray` 留作未解释来源，不冒充已复刻算法。

射程指标由实际三阶段弹道计算。`effectiveRange` 记录中心轨迹到最低伤害时刻的水平距离；界面另计算1.4米标准枪口平地落点，涂墨上界与实占面积分别报告。

## 验证记录

- **源表和资源：通过。** 正式 Luban 生成成功；原始工作簿只改变上述4个单元格，样式及其他ZIP成员保持原样；815个角色/武器/副武器资源及meta的SHA-256核对中，只有两份目标武器配置改变。独立导入核对通过。原始结果：`Reports/MainWeaponReplacement/static-validation.json`。
- **Unity EditMode：219通过、12失败、4显式跳过。** 报告：`Reports/AimBallistics/main-weapon-replacement-edit-v2.xml`。12项失败是7项旧涂墨所有权哈希、1项旧Shotgun物理/涂墨哈希、4项旧非法秒数字段中文提示断言。用Git HEAD的原始测试方法及旧英雄6配置在内存中复现全部12项；11项失败信息完全相同，英雄6恢复旧配置后仍与冻结哈希不符。未修改历史预期以消除失败。复现报告：`Reports/MainWeaponReplacement/baseline-comparison.json`。4项跳过均为显式的测量/基线生成入口。
- **Play Mode：2通过、0失败。** 真实Boot/Addressables/Host场景下，轻加4角色共88发、各耗15墨，蓄力/射击/取消/死亡/复活及五个瞄准角度握持检查通过；MG实际胶囊和纸片命中、24伤害、死亡复活、墙体遮挡、换角色与在途弹丸配置不可变检查通过。报告：`Reports/AimBallistics/main-weapon-replacement-play.xml`。
- **选角画面：已检查1280×720截图。** [焰橙/斯普拉旋转枪](MainWeaponReplacement/selection-mini.png)显示32/32→16、15发/秒、4°及新弹道射程；[铃芽/专业模型枪MG](MainWeaponReplacement/selection-mg.png)显示24→12、15发/秒、12.6°及新弹道射程。完整三分辨率UI扫测在第9张卡片失败：旧自动点击脚本未滚动列表，后续分辨率未完成；不将此报告计为整套UI通过。
- **内容签名：通过实际Host审批回调检查。** Play Mode向生产连接审批逻辑提交不同内容签名，得到拒绝和“游戏内容不一致”；本轮未启动旧版本客户端执行握手。
- **准星/参数补充回归：24通过、0失败。** `Reports/AimBallistics/main-weapon-replacement-harness.xml`。
- **最终重新导入回归：7通过、0失败。** `Reports/AimBallistics/main-weapon-replacement-reimport-v3.xml`；在内存克隆上故意恢复旧伤害/射速/耗墨后，调用编辑器实际使用的参数导入方法，得到与正式MG配置逐字节相同的运行时签名数据，且Ammo/模型地址引用保留。该最终改动仅涉及编辑器导入及测试，不改变已验收的Player战斗代码和配置。

- **Windows构建：通过。** Unity 6000.3.9f1、Development、Mono、Addressables，验证包位于 `Temp/CameraReticle/Player/InkLan.exe`；最终构建完成于2026-09-21 21:04:31（UTC+8）。构建产生的无关ProjectSettings变动已还原。
- **独立主客端：两把武器均通过。** 每把武器启动独立Windows Host/Client，覆盖实际射击与双向命中、潜墨、6/8→1→6/8换角、死亡复活、客户端重新请求快照及回合重置。轻加双方涂墨哈希 `1930832540`、序号627一致；MG双方哈希 `3684422428`、序号2937一致。轻加每位玩家269个、MG每位玩家268个相同时刻的权威快照，其生命、墨量、发射序号、预留墨及剩余弹数逐值完全一致。两次运行共四个进程均无运行时错误，内容签名一致。证据：`Reports/MainWeaponReplacement/Network-Mini-v2/`、`Network-MG-v2/`。
- **联网准星：四个进程均通过。** Direct3D11累计37932个渲染帧检查，FOV、投影及潜墨隐藏错误均为0；停火目标提示、实际命中、枪口遮挡及恢复均通过。脚本用当前武器参考点预测计算预期投影；对射夹具用实际发射方向求解瞄准角，并在两轮之间取消剩余弹仓。这些修改仅作用于显式启动的验收路径。

可携带的汇总在 [Validation.json](MainWeaponReplacement/Validation.json)。完整XML、联机日志及快照逐项记录保留在本地Reports；初轮验证脚本失败报告保留，不作为通过证据。

原作逐帧画面对齐、实体双机局域网及目标设备尚未验收；同机画面/联网和参数来源不能替代这些证明。
