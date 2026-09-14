# 固定验证输入

这里保存测试和工具依赖的固定输入，不保存测试输出。原历史报告位于 `../../Reports/Archive/2026-09-14/`。

- `HeroMigration/`、`WeaponAudit/` 和 `CombatGirls/` 保留历史基准、字段映射与来源清单，内容按原文件复制并核对。
- `WeaponAudit/Project-Baseline.json` 固定原审计工具从 Git 历史读取的四张表，保留提交来源；运行审计不再依赖本地历史提交是否齐全。
- `GameplayUpdate/Hero-Before.json` 固定本次沿途半径拆分前的实际英雄表，提交来源见同目录 `Baseline.txt`，用于确认其他参数没有被迁移修改。
- 历史单半径字段只在历史测试加载层转换为相等的最小、最大值；不要据此覆盖当前配置，也不要让报告生成器改写这里的固定输入。
- 新验证结果写入项目 `Reports/` 下的主题目录。
