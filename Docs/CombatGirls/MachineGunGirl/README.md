# MachineGunGirl / 消防栓旋转枪

已新增英雄 ID 6，热身阶段按 H 选择「消防栓旋转枪」。原有五名英雄、默认英雄、半自动长按连发和现有弦化规则保留。

按住左键蓄力，松开后持续射击；第一圈 33 发，满蓄 66 发，满蓄整轮保持 40 点近端伤害。Shift 取消蓄力或剩余射击，未发射的预留墨返还。双环准星分别显示两段蓄力，发射期间显示剩余弹量。

正式资源：

| 用途 | 文件 / 地址 |
|---|---|
| 角色 | `Assets/GameResource/Characters/MachineGunGirl/Prefabs/MachineGunGirlVisual.prefab` · `Character/MachineGunGirl` |
| 武器 | `Assets/GameResource/Weapons/MachineGunGirl/Prefabs/MachineGun.prefab` · `Weapon/MachineGun` |
| 控制器 / 遮罩 | `Assets/GameResource/Characters/MachineGunGirl/Animations/` |
| 表现参数 | `Assets/GameResource/Characters/MachineGunGirl/MachineGunGirlPresentation.asset` |
| 弦化拍摄与显示 | `Assets/GameResource/Characters/Shared/Paper/MachineGunGirl/` |
| 原始资源 / 独立共享依赖 | `Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack/CombatGirls_MachineGun/`、`SharedMachineGun/` |

原始资源保留衣装、发色、表情与材质变体。正式装配去掉布料、演示脚本、约束和多余换弹弹匣。Avatar 使用独立 `Humanoid_F`，Hips 映射为 `pelvis`。10 个 FBX 提供 11 个正式片段，FL 为左、BR 为右；射击使用 0–40 帧循环和 41–85 帧结束片段。

脸部保留专用 Toon SDF Shader，通过 `MachineGunFaceShadow` 更新头部朝向；弦化拍摄同样更新该参数。正式材质仅将 `_Is_Filter_LightColor` 设为 1，限制训练场强光导致的过曝；源材质保持原样。枪管不额外旋转。

配置由 `Config/Luban/source/TbHero.xlsx` → Luban → 生成 C#/JSON 驱动。快照协议为 18；蓄力、预留墨、剩余弹数、释放进度和射击时间参与同步。现有发射组标识加轮内弹序号构成每发稳定身份，墨弹保留发射时的英雄与满蓄身份。

复建流程：

```powershell
python Tools/CombatGirls/update_machinegun.py
cmd /c Config\Luban\gen_luban.bat
python Tools/CombatGirls/import_assets.py --hero 6
# Unity 菜单：喷墨对战 / 角色 / 安装 MachineGunGirl
python Tools/CombatGirls/validate_machinegun.py
```

配置脚本在 ID 6 已存在时保留其数值；导入与构建只选择 ID 6。首次导入记录位于 `Tools/ValidationData/MachineGun/source-assets.json`，用于检查源文件哈希。重新建立资源基线时应检查导入报告后更新此记录。

详见 [参数换算与适配边界](Parameters.md)、[验收记录](Acceptance.md)。源目标图片、训练场截图、逐帧墨量和弹道测量均保存在本目录的 `Evidence` 中。未提交 Git。
