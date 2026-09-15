"""Restore the MachineGun hero registration using the split configuration contract.

Existing hero values and tuned weapon assets are preserved. Weapon defaults come from
our pinned pre-migration source snapshot, never from deleted TbHero weapon columns.
Run Config\\Luban\\gen_luban.bat after a source workbook change, then the Unity
CombatGirls builder to register Addressables. No weapon fields are added to Luban.
"""
from pathlib import Path
from copy import copy
import json
import re
import uuid
import openpyxl
from migrate_weapon_seconds import TIME_FIELDS, seconds_values

ROOT = Path(__file__).resolve().parents[2]
CONFIG = 'Assets/GameResource/Weapons/MachineGunGirl/MachineGunGirlWeaponConfig.asset'

def main():
    baseline = json.loads((ROOT/'Tools/ValidationData/WeaponAssets/Migration-Baseline.json').read_text('utf-8'))
    original = seconds_values(next(h for h in baseline if h['id'] == 6))
    path = ROOT/'Config/Luban/source/TbHero.xlsx'
    book = openpyxl.load_workbook(path); sheet = book['Hero']
    columns = {c.value:c.column for c in sheet[1] if c.value and not c.value.startswith('##')}
    if 'weaponConfigPath' not in columns:
        raise RuntimeError('Expected migrated TbHero with weaponConfigPath. Use the matching historical checkout for the old installer.')
    fields = re.findall(r'public (string|int|float|double|WeaponFireMode|WeaponMuzzleMode) (\w+)\s*[;=]', (ROOT/'Assets/Splatoon/Config/WeaponConfigAsset.cs').read_text('utf-8-sig'))
    assert len(fields) == 64, 'Review installer for the changed weapon schema'
    assert not (set(columns) & {name for _,name in fields}), 'Weapon columns must not be present in TbHero'
    row = next((r for r in range(4,sheet.max_row+1) if sheet.cell(r,columns['id']).value == 6), None)
    if row is None:
        row = sheet.max_row + 1
        for key,col in columns.items():
            sheet.cell(row,col)._style = copy(sheet.cell(4,col)._style)
            sheet.cell(row,col).value = CONFIG if key == 'weaponConfigPath' else original[key]
        book.save(path)
        print('Hero 6 restored; run the formal Luban generator.')
    else:
        assert sheet.cell(row,columns['weaponConfigPath']).value == CONFIG, 'Preserve and review the customized hero path'
        print('Hero 6 already registered; workbook unchanged.')
    book.close()
    target = ROOT/CONFIG
    if not target.exists():
        script_meta = (ROOT/'Assets/Splatoon/Config/WeaponConfigAsset.cs.meta').read_text('utf-8')
        guid = re.search(r'^guid: (\w+)', script_meta, re.M)[1]
        lines = ['%YAML 1.1','%TAG !u! tag:unity3d.com,2011:','--- !u!114 &11400000','MonoBehaviour:',
            '  m_ObjectHideFlags: 0','  m_CorrespondingSourceObject: {fileID: 0}', '  m_PrefabInstance: {fileID: 0}',
            '  m_PrefabAsset: {fileID: 0}', '  m_GameObject: {fileID: 0}', '  m_Enabled: 1', '  m_EditorHideFlags: 0',
            f'  m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}', '  m_Name: MachineGunGirlWeaponConfig', '  m_EditorClassIdentifier:']
        defaults = dict(spreadExpandSeconds=1,spreadRecoverSeconds=.5,baseSpreadDegrees=0,baseJumpSpreadDegrees=0)
        for kind,key in fields:
            value = original.get(key, defaults.get(key))
            assert value is not None,key
            lines.append('  '+key+': '+json.dumps(value,ensure_ascii=False))
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text('\n'.join(lines)+'\n','utf-8')
        meta = Path(str(target)+'.meta')
        if not meta.exists(): meta.write_text('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\n','utf-8')
        print('Missing weapon asset restored from pinned defaults. Register Addressables in Unity.')
    else:
        print('Existing weapon asset preserved, including Inspector tuning.')
    packs_path = ROOT/'Tools/CombatGirls/hero-packs.json'
    packs = json.loads(packs_path.read_text())
    if not any(p['id']==6 for p in packs):
        packs.append(dict(id=6,pack='CombatGirls_MachineGun',name='MachineGunGirl',weapon='MachineGun',avatar='Humanoid_F',prefab='Prefab/MachineGun_Girl.prefab',scene='MachineGun_Girl_Scene.unity',clips=['MG_AimWalk_F','MG_AimWalk_B','MG_AimWalk_FL','MG_AimWalk_BR','MG_AimTurn_L90','MG_AimTurn_R90','MG_AimIdle','MG_Shoot','MG_Die_F','MG_Die_B']))
        packs_path.write_text(json.dumps(packs,indent=2)+'\n','utf-8')

if __name__ == '__main__': main()
