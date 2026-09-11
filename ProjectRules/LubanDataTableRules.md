# Luban 数据规范

- `Config/Luban/source` 中的 Excel 是唯一权威数据源。
- 修改后运行 `cmd /c Config\Luban\gen_luban.bat`。
- 禁止手改 `Assets/Splatoon/Config/Generated` 和 `Assets/GameResource/Bootstrap/Config/Luban` 中的生成文件。
- 集中配置服务初始化完成后，运行时才能访问生成的表缓存。
- 参数稳定键保持兼容；列标题、参数单位与约束提供中文说明。
