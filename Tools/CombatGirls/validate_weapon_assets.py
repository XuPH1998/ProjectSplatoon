"""Read-only source workbook -> Luban -> weapon assets -> Addressables migration audit."""
from pathlib import Path
import json
import math
import re
import xml.etree.ElementTree as ET
import openpyxl

ROOT = Path(__file__).resolve().parents[2]

def asset_values(path):
    result = {}
    for key, value in re.findall(r'^  ([a-z]\w*): (.+)$', path.read_text('utf-8-sig'), re.M):
        if key.startswith('m_'): continue
        try: result[key] = json.loads(value)
        except json.JSONDecodeError: result[key] = value
    return result

def main():
    errors = []
    def check(value, message):
        if not value: errors.append(message)
    def equal(a, b):
        return math.isclose(a, b, rel_tol=1e-6, abs_tol=1e-6) if isinstance(a, (int, float)) and isinstance(b, (int, float)) else a == b
    book = openpyxl.load_workbook(ROOT/'Config/Luban/source/TbHero.xlsx', data_only=False)
    sheet = book['Hero']
    columns = {c.value: c.column for c in sheet[1] if c.value and not c.value.startswith('##')}
    rows = [{key: sheet.cell(r, col).value for key, col in columns.items()} for r in range(4, sheet.max_row+1) if sheet.cell(r, columns['id']).value]
    book.close()
    generated = json.loads((ROOT/'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json').read_text('utf-8-sig'))
    baseline = json.loads((ROOT/'Tools/ValidationData/WeaponAssets/Migration-Baseline.json').read_text('utf-8'))
    weapon_fields = re.findall(r'public (?:float|int|string) (\w+)\s*[;=]', (ROOT/'Assets/Splatoon/Config/WeaponConfigAsset.cs').read_text('utf-8-sig'))
    schema = ET.parse(ROOT/'Config/Luban/source/Defines/gameplay.xml')
    hero_fields = {node.attrib['name'] for bean in schema.iter('bean') if bean.attrib.get('name') == 'HeroConfig' for node in bean.findall('var')}
    check(hero_fields == set(columns), 'Source columns do not match Luban HeroConfig schema')
    check(not set(weapon_fields) & set(columns), 'Weapon fields remain in TbHero')
    check(len(rows) == len(generated) == len(baseline) == 6, 'Expected exactly six heroes')
    check(len(columns) == 31 and len(weapon_fields) == 64, 'Unexpected split field counts')
    group = (ROOT/'Assets/AddressableAssetsData/AssetGroups/Splatoon Local.asset').read_text('utf-8-sig')
    entries = dict(re.findall(r'  - m_GUID: (\w+)\s+ m_Address: (.+)', group))
    checked = 0
    for row in rows:
        gen = next(h for h in generated if h['id'] == row['id'])
        check(set(gen) == set(columns), f'Generated hero {row["id"]} contains extra/missing fields')
        for key, value in row.items(): check(equal(gen.get(key), value), f'Source/generated: {row["id"]}/{key}')
        path = ROOT / row['weaponConfigPath']
        check(path.exists(), f'Missing asset: {path}')
        if not path.exists(): continue
        values = asset_values(path)
        check(set(values) == set(weapon_fields), f'Asset fields: {path}')
        meta = Path(str(path)+'.meta').read_text('utf-8')
        guid = re.search(r'^guid: (\w+)', meta, re.M)[1]
        check(entries.get(guid) == row['weaponConfigPath'], f'Addressables full path missing: {path}')
        check(list(entries.values()).count(row['weaponConfigPath']) == 1, f'Duplicate address: {path}')
        old = next(h for h in baseline if h['id'] == row['id'])
        combined = dict(gen, **values)
        for key, value in old.items():
            checked += 1
            check(equal(combined.get(key), value), f'Migration changed original value: {row["id"]}/{key}')
        check(values.get('spreadExpandSeconds') == 1 and values.get('spreadRecoverSeconds') == .5, f'New time defaults: {path}')
        check(values.get('baseSpreadDegrees') == (2 if row['id'] == 3 else 0), f'Ground base: {path}')
        check(values.get('baseJumpSpreadDegrees') == (4 if row['id'] == 3 else 0), f'Air base: {path}')
    report = dict(passed=not errors, heroes=len(rows), characterFields=len(columns)-1, weaponPathFields=1,
                  originalWeaponFields=60, newWeaponFields=4, originalValuesCompared=checked, errors=errors)
    output = ROOT/'Reports/WeaponAssets'; output.mkdir(parents=True, exist_ok=True)
    (output/'static-validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', 'utf-8')
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if not errors else 1

if __name__ == '__main__': raise SystemExit(main())
