# 异步加载规则

- Boot 必须先初始化 Addressables，再读取配置或加载玩法场景。
- 每次加载都支持取消、进度、错误报告和确定性释放。
- 调用方拥有 Addressables 句柄，直到卸载完成。
- 常规运行时代码禁止使用 `Resources.Load`、`AssetDatabase` 或直接读取文件系统。
