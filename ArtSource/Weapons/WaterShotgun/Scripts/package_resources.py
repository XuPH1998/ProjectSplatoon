"""Assemble verified deliverables and a SHA-256 manifest; does not change Unity assets."""
import hashlib,json,re,zipfile
from pathlib import Path
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
PROJECT=ROOT.parents[2]
VALIDATION_COPY=PROJECT.parent/'ProjectSplatoon-WaterShotgun-Validation-20260917'
SHA=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()

def guid(path):
    return re.search(r'^guid: (\w+)',path.read_text(encoding='utf-8-sig'),re.M)[1]

def main():
    geom=json.loads((ROOT/'Validation/blender-geometry.json').read_text())
    unity=json.loads((ROOT/'Validation/unity-validation.json').read_text())
    assert geom['triangles']==unity['triangles']<=4000
    assert geom['nonmanifold_edges']==geom['degenerate_triangles']==unity['degenerateTriangles']==0
    assert unity['meshes']==unity['materialSlots']==1
    assert len(unity['poses'])==4
    for p in unity['poses']:assert p['handDeltaMeters']<=.001
    for m in unity['markers']:assert m['positionErrorMeters']<=.001 and m['rotationErrorDegrees']<=.5
    assert max(geom['size_relative_error'])<=.02
    with Image.open(ROOT/'Export/WaterShotgun_Albedo.png') as im:assert im.size==(1024,1024) and im.mode=='RGB'
    texture_guid=guid(ROOT/'Export/WaterShotgun_Albedo.png.meta')
    material_guid=guid(ROOT/'Export/WaterShotgun_Toon.mat.meta')
    material=(ROOT/'Export/WaterShotgun_Toon.mat').read_text()
    assert set(re.findall(r'guid: (\w+)',material))=={texture_guid,'be891319084e9d147b09d89e80ce60e0'}
    assert material_guid in (ROOT/'Export/WaterShotgun.fbx.meta').read_text()
    assert SHA(ROOT/'Export/WaterShotgun.fbx')==SHA(VALIDATION_COPY/'Assets/WaterShotgunValidation/WaterShotgun.fbx')
    assert SHA(ROOT/'Export/WaterShotgun_Albedo.png')==SHA(VALIDATION_COPY/'Assets/WaterShotgunValidation/WaterShotgun_Albedo.png')
    assert SHA(ROOT/'Export/WaterShotgun_Toon.mat')==SHA(VALIDATION_COPY/'Assets/WaterShotgunValidation/WaterShotgun_Toon.mat')
    preserved=[]
    for rel in [
        'Assets/GameResource/Weapons/ShotgunGirl/Prefabs/Shotgun.prefab',
        'Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlWeaponConfig.asset',
        'Assets/GameResource/Characters/ShotgunGirl/Prefabs/ShotgunGirlVisual.prefab',
        'Assets/GameResource/Characters/ShotgunGirl/ShotgunGirlPresentation.asset',
        'Assets/GameResource/Characters/ShotgunGirl/Materials/Weapon_Shotgun_Metal_9b8a7ab4.mat',
        'Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack/Shotgun_Girl/Models/ShotgunGirl_FullBody.fbx',
        'Assets/Splatoon/Runtime/Combat/HeroWeaponBindings.cs',
        'Assets/Splatoon/Runtime/Combat/InkCharacterView.cs',
        'Assets/AddressableAssetsData/AssetGroups/Splatoon Local.asset']:
        current=SHA(PROJECT/rel);baseline=SHA(VALIDATION_COPY/rel)
        assert current==baseline, f'Source changed since snapshot: {rel}'
        preserved.append({'path':rel,'sha256':current,'equals_validation_snapshot':True})
    (ROOT/'Validation/source-preservation.json').write_text(json.dumps(preserved,indent=2),encoding='utf-8')
    ratio=(1-unity['triangles']/geom['source_triangles'])*100
    size0=[geom['source_design_max_m'][i]-geom['source_design_min_m'][i] for i in range(3)]
    size1=[geom['design_max_m'][i]-geom['design_min_m'][i] for i in range(3)]
    rows='\n'.join(f'| {name} | {size0[i]*1000:.3f} mm | {size1[i]*1000:.3f} mm | {geom["size_relative_error"][i]*100:.4f}% |' for i,name in enumerate(['长度','宽度','高度']))
    poses='\n'.join(f'| {p["pose"]} | {p["baselineLeftHandErrorMeters"]*1000:.6f} mm | {p["newLeftHandErrorMeters"]*1000:.6f} mm | {p["handDeltaMeters"]*1000:.6f} mm |' for p in unity['poses'])
    content=f'''# 喷水枪模型验收记录

日期：2026-09-17。Blender {geom['blender_version']}；Unity {unity['unityVersion']}；{unity['graphicsDevice']}。

## 网格与资源检查：通过

- 最终 FBX 与 Unity 导入网格均为 **{unity['triangles']:,} 个三角面**，比原枪减少 **{ratio:.1f}%**。
- Unity 导入顶点数为 {unity['vertices']:,}；这是包含法线和 UV 拆分后的数值。
- 1 个网格、1 个材质槽、1 张不透明 RGB 1024×1024 贴图。
- Blender 检查：0 个退化三角面，0 条非流形/开放边；32 个封闭建模部件合并为单网格。
- Unity 检查：0 个退化三角面，材质自动重映射至配套 `Toon/Toon`，模型/材质/贴图引用完整。
- 导出资源与独立验证副本实际加载的 FBX、贴图、材质 SHA-256 一致。

| 尺寸 | 原枪 | 新模型 | 偏差 |
|---|---:|---:|---:|
{rows}

枪口前端沿建模 X 轴与原网格前端对齐。枪托后端差约 0.195 mm，为倒角引起的轮廓收缩。所有尺寸偏差低于约定的 2%。

## 挂点与握持检查：通过

Unity FBX 再导入后三个节点 `Mount`、`LeftGrip`、`Muzzle` 的位置误差和 Quaternion 角度误差，在本次 Unity 浮点比较中均为 0，满足 1 mm / 0.5° 阈值。

| 姿势 | 原枪左手到挂点距离 | 新模型左手到挂点距离 | 新旧左手位置差 |
|---|---:|---:|---:|
{poses}

挂点数值检查与视觉检查分别执行：在水平、向下 45°、向上 35°和射击关键姿势的同机位截图中对照双手、扳机区域、枪托与喷嘴，未发现新增明显穿插或悬空。原角色姿势和左手求解保持一致。图像证据位于 `../Previews/Unity/`。

## 视觉检查：模型与握持通过，默认背视辨识度受限

- 已检查 Blender 侧面、正面、俯视、三分之四与后侧视角。
- 已检查 Unity 原角色握持近景、四种姿势，以及使用原配置和运行时定位函数的第三人称相机对比。
- 默认背后视角下，枪体大部分被角色遮挡，不能认定这一角度已达到清楚识别喷水枪完整造型的要求。原枪有同样的遮挡；本次保持现有相机和模型尺寸约束，完整轮廓以侧面和三分之四视角判断。
- 喷嘴环口法线已修正；注水盖、泵握把、塑料色块和水量标识能区分水枪造型。
- 使用当前项目实际 `Toon/Toon` 着色器及真实 GPU 渲染。Blender 与 Unity 的灯光不同，不以逐像素一致为验收标准。

## 工程边界

当前主工程只新增 `ArtSource/Weapons/WaterShotgun/` 交付目录；原霰弹枪、角色、材质、武器参数和运行时脚本未修改。`source-preservation.json` 记录关键源文件与制作前验证副本快照的相同哈希。没有安装或替换游戏内资源。

本次完成的是隔离的 Editor 网格/导入/GPU 材质/Animator 姿势检查。没有执行完整 Play Mode 对局、双机网络、Player 构建或目标手机性能测试；不把三角面减少解释为已测得的帧率提升。
'''
    (ROOT/'Validation/Acceptance.md').write_text(content,encoding='utf-8')
    files=sorted(p for p in ROOT.rglob('*') if p.is_file() and p.suffix.lower() not in {'.log','.zip','.blend1','.pyc'} and p.name!='manifest.json' and '__pycache__' not in p.parts)
    manifest={'asset':'AQUA 03 WaterShotgun','files':[{'path':str(p.relative_to(ROOT)).replace('\\','/'),'bytes':p.stat().st_size,'sha256':SHA(p)} for p in files]}
    (ROOT/'Validation/manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    files.append(ROOT/'Validation/manifest.json')
    target=ROOT/'WaterShotgun-Resources.zip'
    with zipfile.ZipFile(target,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in files:z.write(p,'WaterShotgun/'+str(p.relative_to(ROOT)).replace('\\','/'))
    with zipfile.ZipFile(target) as z:
        assert z.testzip() is None
        for item in manifest['files']:assert hashlib.sha256(z.read('WaterShotgun/'+item['path'])).hexdigest()==item['sha256']
    print(json.dumps({'files':len(files),'archive_bytes':target.stat().st_size,'triangles':unity['triangles'],'vertices':unity['vertices'],'automated_checks':'PASS','visual_limitation':'Original rear camera occludes most of the gun; see Acceptance.md'},indent=2))

if __name__=='__main__':main()
