"""Read-only audit of MachineGun source provenance, six-hero table and formal references."""
from pathlib import Path
import hashlib
import json
import math
import re
import subprocess
import openpyxl
from migrate_weapon_seconds import TIME_FIELDS

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Reports/CombatGirls/MachineGunGirl'
REF = ROOT / 'Tools/ValidationData/MachineGun'
load = lambda p: json.loads(p.read_text('utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
equal = lambda a,b: math.isclose(a,b,rel_tol=1e-6,abs_tol=1e-6) if isinstance(a,(int,float)) and isinstance(b,(int,float)) else a==b

def main():
    errors=[];OUT.mkdir(parents=True,exist_ok=True)
    manifest=load(REF/'source-assets.json')
    for item in manifest['files']:
        src,dst=Path(item['source']),ROOT/item['target']
        if not src.exists() or sha(src)!=item['sourceSha256']:errors.append('Source changed: '+str(src))
        if not dst.exists():errors.append('Missing import: '+str(dst))
        elif dst.suffix!='.meta' and sha(dst)!=item['importSha256']:errors.append('Imported art changed: '+str(dst))
    heroes=load(ROOT/'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json')
    # Audit old balance against the two current sources without restoring deleted columns.
    for hero in heroes:
        asset=(ROOT/hero['weaponConfigPath']).read_text('utf-8-sig')
        for key,value in re.findall(r'^  ([a-z]\w*): (.+)$',asset,re.M):
            if key.startswith('m_'):continue
            try:hero[key]=json.loads(value)
            except json.JSONDecodeError:hero[key]=value
    before=load(REF/'Heroes-Before.json')
    for old in before:
        current=next(h for h in heroes if h['id']==old['id'])
        for k,v in old.items():
            if not equal(v / 60.0 if k in TIME_FIELDS else v,current.get(TIME_FIELDS.get(k,k))):errors.append(f'Existing hero changed: {old["id"]}/{k}')
    book=openpyxl.load_workbook(ROOT/'Config/Luban/source/TbHero.xlsx',data_only=False)
    sheet=book['Hero'];columns={c.value:c.column for c in sheet[1] if c.value and not c.value.startswith('##')}
    rows=[{k:sheet.cell(r,c).value for k,c in columns.items()} for r in range(4,sheet.max_row+1) if sheet.cell(r,columns['id']).value]
    if len(rows)!=6 or len(heroes)!=6:errors.append('Expected six heroes')
    for row in rows:
        hero=next(h for h in heroes if h['id']==row['id'])
        for k,v in row.items():
            if not equal(v,hero[k]):errors.append(f'Source/generated mismatch: {row["id"]}/{k}')
    book.close()
    guid_paths={};duplicates=[]
    for meta in (ROOT/'Assets').rglob('*.meta'):
        m=re.search(r'(?m)^guid: ([a-f0-9]{32})',meta.read_text('utf-8-sig',errors='ignore'))
        if not m:continue
        if m[1] in guid_paths:duplicates.append([str(meta),str(guid_paths[m[1]])])
        guid_paths[m[1]]=meta
    if duplicates:errors.append('Duplicate asset GUIDs')
    p=manifest['packs'][0];pack=ROOT/'Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack'/p['pack']
    clips=sorted(f.stem for f in pack.rglob('*.fbx') if 'Animations' in f.parts)
    if clips!=sorted(p['clips']):errors.append('10 FBX whitelist mismatch')
    group=(ROOT/'Assets/AddressableAssetsData/AssetGroups/Splatoon Local.asset').read_text('utf-8-sig')
    for address in ['Character/MachineGunGirl','Weapon/MachineGun']:
        if group.count('m_Address: '+address+'\n')!=1:errors.append('Missing/duplicate address: '+address)
    formal=ROOT/'Assets/GameResource/Characters/MachineGunGirl'
    for path in formal.rglob('*'):
        if path.suffix not in {'.prefab','.asset','.controller','.mask','.mat'}:continue
        for guid in re.findall(r'guid: ([a-f0-9]{32})',path.read_text('utf-8-sig')):
            if guid not in guid_paths and guid not in {'0000000000000000f000000000000000','0000000000000000e000000000000000'}:errors.append('Unresolved formal reference: '+str(path)+' '+guid)
    # Imported MG shared dependencies must never replace the existing four-hero fork.
    changed=subprocess.check_output(['git','diff','--name-only','--','Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack/SharedFourHeroes'],cwd=ROOT).decode().strip()
    if changed:errors.append('Existing shared art modified: '+changed)
    report=dict(passed=not errors,sourceFiles=len(manifest['files']),sourceUnchanged=not any(e.startswith('Source changed') for e in errors),
        importedFbxCount=len(clips),heroes=len(heroes),fieldsPerHero=len(columns),checkedConfigValues=len(columns)*len(heroes),
        originalFivePreserved=not any(e.startswith('Existing') for e in errors),duplicateGuids=duplicates,errors=errors)
    (OUT/'static-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),'utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2));return 0 if report['passed'] else 1

if __name__=='__main__':raise SystemExit(main())
