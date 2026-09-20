# 铃芽 SummerCuteness 源文件

`Source` 保存下载目录的原始 FBX、贴图与材质描述，`source-manifest.json` 记录原文件 SHA-256。不要在这里覆盖原始素材。

`Scripts/build_model.py` 使用 Blender 将原 A 姿势转换成 T 姿势，同时变换所有蒙皮顶点、表情形态和法线，然后重建绑定矩阵。模型保持原比例，脚底归零后高度为 1.500024 米。输出为此目录的 `SummerCuteness.blend` 和正式角色目录的 `Models/SummerCuteness.fbx`。

```powershell
& 'D:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe' --background --factory-startup --disable-autoexec --python-exit-code 1 --python ArtSource/Characters/SplooshGirlSummer/Scripts/build_model.py
```

在 Unity 6000.3.9f1 中运行菜单“喷墨对战/角色/重建铃芽 SummerCuteness 模型”。该入口重新建立独立 Humanoid Avatar、材质、持枪挂点、相机和纸片捕获资源。原“安装铃芽与广域标记枪”入口也已使用此模型；该旧入口仍包含武器玩法配置的初始化。

命令行可以在独立的完整项目副本中运行 `-executeMethod Splatoon.Editor.SplooshSummerBuilder.InstallBatch`。需要图形设备，不能加 `-nographics`。原 Shader 不在下载资源中，游戏材质使用项目现有 Toon Shader；依赖原特殊 Shader 的头发阴影覆盖片与害羞覆盖片在中性状态下关闭。

正式角色维持 Hero 8 与 `Character/SplooshGirl`。体型以 `Config/Luban/source/TbHero.xlsx` 为源，修改后运行 `cmd /c Config\Luban\gen_luban.bat`，不要手工编辑生成 JSON。

验收入口包括 `SplooshSummerTests`、`SplooshGirlTests`、`SplooshGirlPlayTests` 与 `Tools/CombatGirls/run_sploosh_acceptance.ps1`。Editor 动作采样和截图位于 `Reports/SplooshGirlSummer`；双进程验收必须使用本次新构建的 Windows Player。
