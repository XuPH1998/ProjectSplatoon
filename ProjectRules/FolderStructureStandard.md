# 工程目录规范

- 项目代码位于 `Assets/Splatoon/`，运行时与编辑器程序集分离。
- Addressables 运行时资源位于 `Assets/GameResource/`，构建设置仅包含 `Assets/Scenes/Main/Boot.unity`。
- `Art/_Incoming` 是临时接入区，预制体和场景不能引用其中资源。
- `Config/Luban/source` 是工作簿与结构定义的唯一数据源；生成的 C#/JSON 只能通过生成器重建。
- 第三方包位于 `Assets/ThirdParty` 或 `Assets/Plugins`，保留原厂目录结构。
- 新增程序集定义必须单向依赖，不得出现循环引用。
