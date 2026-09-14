# 英雄配置合表

本次以 `b43f28f784c4a4723b15599a6b5e933b4723674f` 为数值基线，将角色和武器配置合并为 `TbHero`：5 个英雄，每行 69 个字段。地图和射击算法未调整，上一轮 11.3.0 参考修正保留。

## 配置与字段迁移

`TbHero.xlsx` 的 `Hero` 工作表是运行时参数来源，生成 `HeroConfig` / `TbHero` / `tbhero.json`。表头第 1 行为字段名、第 2 行类型、第 3 行中文含义与单位；数据从第 4 行起，按身份、模型、角色属性、移动恢复、发射、弹道、伤害、涂色和蓄力排列。

| 旧字段 | 英雄字段 | 规则 |
|---|---|---|
| 武器 id/name/displayName | id/name/displayName | 保留 1～5 和现有名称 |
| 角色 visualAddress | characterPrefabAddress | 当前全部为 `Character/RifleGirl` |
| 武器 prefabAddress | weaponPrefabAddress | 当前全部为 `Weapon/RifleGirlRifle` |
| 角色 gravity | characterGravity | 全部为 22，供角色运动使用 |
| 武器 gravity | projectileGravity | 全部为 9.8，供墨弹轨迹使用 |
| 武器 shootMoveSpeed | shootMoveSpeed | 按英雄保留 3.6 / 4 / 3 / 3.4 / 2.4 |
| 角色 shootMoveSpeed | 移除重复字段 | 正式玩家原本使用武器射击移速；不增加第二个覆盖入口 |
| 角色其余基础属性 | 同名字段 | 将当前美少女基础属性复制到每行 |
| 武器其余射击属性 | 同名字段 | 按原 ID 一一迁移，不调整数值 |
| 模式 characterId + weaponId | heroId | 当前默认英雄 1 |

完整的逐字段映射见 [Field-Mapping.json](Field-Mapping.json)，独立的迁移前记录见 [Migration-Baseline.json](Migration-Baseline.json)。配置来源现在为四张表：Hero、RoomMode、Map、Global。Luban 自动生成代码和数据；`tbhero` 继承旧武器 JSON 的 Unity GUID 与 `Luban` 标签，旧角色表注册移除。

`GameplayConfig.DefaultHero` 用于默认选择；`GetHero(id)` 为玩家、发射和在途墨弹解析英雄配置。0 仅保留为尚未初始化状态的默认解析值；玩家选择请求必须指定表中存在的正 ID。

## 模型装配与生命周期

英雄资源在创建/加入房间、启动 NGO 之前全部预加载。按 Addressables 地址去重；当前五个英雄只持有两份资源句柄。加载失败、绑定缺失或取消连接时清理缓存；退出与销毁时释放。

角色预制体须提供 Humanoid Animator、控制器、`InkCharacterView`、`CharacterPresentationProfile`、右手武器挂点、队伍标记与潜墨效果。武器预制体须提供 `HeroWeaponBindings`，明确绑定其内部的枪口和左手握点；运行时不按骨骼名称猜测挂点。

`HeroViewBinder` 在非激活层级完成装配，先移除角色的预览武器，再挂载所选武器、重建 Renderer/握持/枪口绑定后激活。模型资源相同的英雄切换复用实例；模型不同时替换外观，网络玩家根对象及碰撞体保持原有配置。逻辑枪口、相机与转身规则来自所选英雄角色预制体的表现配置，动画枪口只承担视觉反馈。新增不同武器长度的资源时，应提供与组合相符的逻辑表现配置。

原美少女安装工具会创建新的武器绑定组件；现有资源可通过“喷墨对战/内容/安装英雄模型绑定”补齐。该菜单只更新绑定及 Addressables 注册，不构建玩家包。

## 英雄选择与网络

- H 在热身打开英雄选择；DEBUG 在开发环境沿用比赛中选择入口。列表点击只预览，确认后请求房主执行；参数比较区可滚动显示角色基础属性与射击参数。
- 快照、输入和发射上下文使用 `HeroId` / `HeroRevision`。切换清除蓄力与连发，旧修订的输入不能发射新英雄武器。位置、队伍和生命周期保持；在途墨弹继续使用发射时的英雄 ID。
- 热身切换补满新英雄墨量；DEBUG 切换保留资源绝对值，生命、墨量限制在新英雄上限内。重生按所选英雄上限恢复，重新进入房间选择默认英雄。
- 玩家协议为 8，涂色协议仍为 5。内容签名包含全部表数据、全部英雄的逻辑枪口/相机/转身配置及模型挂点变换，并以英雄 ID 排序。房间列表和审批继续拒绝版本或内容不一致的客户端。

## 验证入口

- `python Tools/HeroMigration/verify.py`：核对 345 个迁移值、源表与生成输出、模式映射和旧表清理。
- `python Tools/HeroMigration/verify.py --measurements Logs/WeaponReference/EditMode`：将 87 组测量与迁移前保留的轨迹、墨迹、归属哈希比较；同样支持 `Logs/WeaponReference/PlayMode`。固定记录见 [Measurement-Comparison.csv](Measurement-Comparison.csv)。
- `python Tools/WeaponReference/audit.py --check`：从英雄表投影复核历史武器参考基线与原始文件哈希；历史对照 CSV 增加 `heroField` 指明实际字段。
- “喷墨对战/验证/英雄迁移 EditMode”：配置、独立英雄属性、模型装配/异常恢复、资源限制、射击、移动、碰撞、发现与 87 组测量。
- “喷墨对战/验证/英雄迁移 PlayMode”：真实 Addressables/NGO 房主流程、英雄选择、重生和清理，H/DEBUG/鼠标预览确认的实际界面事件，以及 EditMode/Play Mode 测量一致性。先执行 EditMode，再执行 PlayMode；此入口不会退出编辑器。

本次具体结果及未执行项见 [Validation.md](Validation.md)。以上工程迁移不改变原作复刻完成度，也不把编辑器验证当作真实多机或原作实机验收。
