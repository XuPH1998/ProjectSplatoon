# 工程目录规范

- 项目代码位于 `Assets/Splatoon/`，运行时与编辑器程序集分离。
- Addressables 运行时资源位于 `Assets/GameResource/`，构建设置仅包含 `Assets/Scenes/Main/Boot.unity`。
- `Art/_Incoming` 是临时接入区，预制体和场景不能引用其中资源。
- `Config/Luban/source` 是工作簿与结构定义的唯一数据源；生成的 C#/JSON 只能通过生成器重建。
- 第三方包位于 `Assets/ThirdParty` 或 `Assets/Plugins`，保留原厂目录结构。
- 新增程序集定义必须单向依赖，不得出现循环引用。

## 报告与验证数据

- 仅在任务确有需要时生成报告，简短修改结果优先直接回复。
- 统一报告根目录为项目下的 `Reports/`（本工作区为 `D:\XPHUNITY\ProjectSplatoon\Reports\`），按主题建子目录；实现报告、审计、验证结果、任务日志、截图和测量数据均放在同一主题目录。
- 历史报告归档到 `Reports/Archive/YYYY-MM-DD/`，保留原相对目录层级，禁止覆盖同名历史文件。归档时核验文件数量、内容和引用。
- 禁止把新报告散放在项目根目录、`Assets/`、`Docs/` 或工具目录。现有及新增的生成入口与消费者都必须遵循统一报告路径。
- `Docs/` 保留长期使用说明；可重复测试依赖的固定输入放在 `Tools/ValidationData/`，不得依赖临时报告目录或覆盖历史基准。
- Unity 自动日志保留在 Unity 指定位置，构建产物遵循测试与打包规则。原本忽略的任务日志与本地输出在归档后仍应忽略，不因迁移加入版本控制。
