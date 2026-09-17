# 双枪普通射击验收记录

2026-09-17，Windows，Unity 6000.3.9f1，当前工作区。

| 检查 | 结果与证据 |
| --- | --- |
| 双枪静态引用/参数审计 | 通过，105 项历史值与本次覆盖值比较；`Reports/WeaponAssets/hero-2-static-validation.json` |
| DualPistolGirlTests | 20 项全部通过；`Reports/DualPistolGirl/dualies-current.xml` 中该测试类 |
| 真实 Boot + Host | 1 项通过；同一份 `dualies-current.xml`、`host-summary.txt` |
| 固定步涂墨 | 每颗最多 2 次沿途落墨；首发及每隔 5 发脚下落墨；20 发合计 64 个笔刷请求；30/60/144 Hz 外部驱动的 ownership 哈希一致 |
| 参数与原始参考 | `Tools/ValidationData/WeaponAssets/DualPistolGirl-GloogaNormal.json` 与 `Tools/ValidationData/DualPistolGirl/source.json` |

最终组合验证在 2026-09-17 05:33:45 UTC 完成，21 项全部通过，0 失败、0 跳过。此前 Host 测试夹具的“仅场景半径”探针曾受服务器玩家的其他命中代理影响，现已在该探针中排除玩家；没有为此改变生产命中规则。最终结果及记录时相关文件哈希见 `Reports/DualPistolGirl/verification-summary.json`。

Host 项使用实际房间、服务器玩家、武器资源和飞行显示，验证左右枪口交替、粒子存在、近距离 36 伤害、9 米及 12 米命中、参考射程不是硬性伤害截断、墙体遮挡以及玩家较大的扫掠半径。它不是独立客户端或物理双机验证，也未完成目标设备性能和人工视觉/操作手感验收。

共享旧基线测试未全部通过：一次 CombatGirlsWeaponTests 回归有 12 项通过、2 项失败，分别是当时爆破枪配置验证和霰弹枪旧伤害期望（80 与当前 128）。Python 秒制迁移脚本的 5 项转换安全测试通过，当前六资产严格基线比较失败；检查发现旋转枪、手枪、爆破枪、霰弹枪存在其他调参差异，双枪覆盖后的差异数为 0。没有把这些共享基线失败计入双枪通过数，也没有恢复其他武器资产来迎合旧基线。

当前编辑器由多个武器任务共用，通用测试入口曾把另一测试运行的结果写进双枪标签。最终证据使用独立的双枪结果入口，并核对 XML 中的实际测试类名称。
