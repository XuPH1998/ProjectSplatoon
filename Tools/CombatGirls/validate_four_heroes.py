"""Read-only source, source-table/generated-data, GUID, whitelist and cleanup audit."""
from pathlib import Path
import hashlib
import json
import math
import re
import subprocess
import openpyxl

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Reports/CombatGirls/FourHeroes'
OUT.mkdir(parents=True,exist_ok=True)
load = lambda p: json.loads(p.read_text('utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
equal = lambda a,b: math.isclose(a,b,rel_tol=1e-6,abs_tol=1e-6) if isinstance(a,(int,float)) and isinstance(b,(int,float)) else a==b
errors=[]
manifest=load(ROOT/'Tools/ValidationData/CombatGirls/FourHeroes/source-assets.json')
for item in manifest['files']:
    src,dst=Path(item['source']),ROOT/item['target']
    if not src.exists() or sha(src)!=item['sourceSha256']: errors.append('Source changed: '+str(src))
    if not dst.exists(): errors.append('Missing imported file: '+str(dst)); continue
    # Unity intentionally rewrites importer metadata. Original meshes/textures remain byte-identical;
    # material and prefab modifications are exactly those recorded by the importer.
    if dst.suffix!='.meta' and sha(dst)!=item['importSha256']:errors.append('Imported content differs: '+item['target'])

heroes=load(ROOT/'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json')
book=openpyxl.load_workbook(ROOT/'Config/Luban/source/TbHero.xlsx',data_only=False)
sheet=book['Hero'];fields=[c.value for c in sheet[1]][1:]
rows=[dict(zip(fields,[c.value for c in row][1:])) for row in sheet.iter_rows(min_row=4) if row[1].value is not None]
assert len(rows)==len(heroes)==5
for row,hero in zip(rows,heroes):
    for field in fields:
        if not equal(row[field],hero[field]):errors.append(f'Source/generated mismatch: {hero["id"]}/{field}')
book.close()
baseline=load(ROOT/'Tools/ValidationData/GameplayUpdate/Hero-Before.json')
numeric_changes={old['id']:{'trailRadiusMin':round(old['trailRadius']*.8,6),'trailRadiusMax':round(old['trailRadius']*1.2,6)} for old in baseline}
measurements={x['id']:x for x in load(ROOT/'Tools/ValidationData/CombatGirls/FourHeroes/paint-measurements.json')}
for hero,old in zip(heroes,baseline):
    changes=numeric_changes.get(hero['id'],{})
    for key,value in changes.items():
        if not equal(hero[key],value):errors.append(f'Approved balance mismatch: {hero["id"]}/{key}')
    if hero['id'] in measurements:
        if not equal(hero['paintRange'],round(measurements[hero['id']]['range'],2)):errors.append('Paint measurement not regenerated')
    for key,value in old.items():
        if key=='trailRadius' or key in changes or hero['id']!=1 and key in {'name','displayName','characterPrefabAddress','weaponPrefabAddress'} or hero['id'] in measurements and key=='paintRange':continue
        if not equal(hero[key],value):errors.append(f'Unapproved change: {hero["id"]}/{key}')

guids={};duplicates=[]
for meta in (ROOT/'Assets').rglob('*.meta'):
    m=re.search(r'(?m)^guid: ([a-f0-9]{32})',meta.read_text('utf-8-sig',errors='ignore'))
    if not m:continue
    guid=m[1]
    if guid in guids:duplicates.append([str(guids[guid]),str(meta)])
    guids[guid]=meta
if duplicates:errors.append('Duplicate GUIDs')
group=(ROOT/'Assets/AddressableAssetsData/AssetGroups/Splatoon Local.asset').read_text('utf-8-sig')
clip_count=0;formal=[]
for pack in manifest['packs']:
    folder=ROOT/'Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack'/pack['pack']
    clips=sorted(p.stem for p in folder.rglob('*.fbx') if 'Animations' in p.parts)
    if clips!=sorted(pack['clips']):errors.append('Clip whitelist mismatch: '+pack['name'])
    clip_count+=len(clips)
    for category,name,file in [('Characters',pack['name'],pack['name']+'Visual'),('Weapons',pack['name'],pack['weapon'])]:
        path=ROOT/f'Assets/GameResource/{category}/{name}/Prefabs/{file}.prefab';formal.append(path)
        address=('Character/'+pack['name']) if category=='Characters' else ('Weapon/'+pack['weapon'])
        if group.count('m_Address: '+address+'\n')!=1:errors.append('Missing/duplicate address: '+address)
        if not path.exists():errors.append('Missing formal prefab: '+str(path))
    controller=ROOT/f'Assets/GameResource/Characters/{pack["name"]}/Animations/{pack["name"]}Combat.controller'
    text=controller.read_text('utf-8-sig')
    for guid in re.findall(r'guid: ([a-f0-9]{32})',text):
        if guid not in guids:errors.append('Unresolved controller GUID: '+guid)

legacy=load(ROOT/'Tools/ValidationData/CombatGirls/legacy-cleanup.json');legacy_guids=set(legacy['guids']);legacy_refs=[]
for path in (ROOT/'Assets').rglob('*'):
    if not path.is_file() or path.suffix not in {'.prefab','.unity','.asset','.controller','.mask','.mat','.meta'}:continue
    text=path.read_text('utf-8-sig',errors='ignore')
    if set(re.findall(r'guid: ([a-f0-9]{32})',text))&legacy_guids:legacy_refs.append(path.relative_to(ROOT).as_posix())
if legacy_refs:errors.append('Retired Jammo GUID still referenced')
for folder in ['Assets/GameResource/Characters/Jammo','Assets/GameResource/Characters/_Incoming']:
    if (ROOT/folder).exists():errors.append('Retired/temp resources remain: '+folder)
report=dict(passed=not errors,sourceFilesChecked=len(manifest['files']),sourceUnchanged=not any(e.startswith('Source') for e in errors),
    newClipFiles=clip_count,heroes=5,fieldsPerHero=len(fields),generatedValuesChecked=len(fields)*5,
    guidCount=len(guids),duplicateGuids=duplicates,formalPrefabs=len(formal),oldResourceReferences=legacy_refs,
    cleanup='Previous ID2-5 shared RifleGirl; no separate retired character files. Keep RifleGirl and all requested appearance variants. Jammo was already removed.',errors=errors)
(OUT/'static-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),'utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
raise SystemExit(0 if report['passed'] else 1)
