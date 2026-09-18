"""Read-only pre/post scope audit for the Explosher migration."""
from pathlib import Path
import hashlib, json, zipfile
import openpyxl
from validate_weapon_assets import asset_values

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'Tools/ValidationData/Explosher'
base = DATA / 'Baseline'
errors = []
def check(ok, message):
    if not ok: errors.append(message)

manifest = json.loads((base / 'manifest.json').read_text('utf8'))
unchanged = []
for path, digest in manifest.items():
    if '/Weapons/' in path and '/ShotgunGirl/' not in path:
        check(hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == digest, 'Unrelated asset changed: ' + path)
        unchanged.append(path)
before = openpyxl.load_workbook(base / 'TbHero.xlsx')
after = openpyxl.load_workbook(ROOT / 'Config/Luban/source/TbHero.xlsx')
changed = []
for arow, brow in zip(before.active, after.active):
    for a, b in zip(arow, brow):
        check(a._style == b._style, 'Cell style changed: ' + a.coordinate)
        if a.value != b.value: changed.append(a.coordinate)
check(set(changed) == {'J6', 'K6', 'AG6'}, 'Unexpected source cells: ' + str(changed))
with zipfile.ZipFile(base / 'TbHero.xlsx') as a, zipfile.ZipFile(ROOT / 'Config/Luban/source/TbHero.xlsx') as b:
    check(set(a.namelist()) == set(b.namelist()), 'Workbook ZIP structure changed')
    for name in a.namelist():
        if name != 'xl/worksheets/sheet1.xml': check(a.read(name) == b.read(name), 'Unrelated workbook part changed: ' + name)
old = json.loads((base / 'tbhero.json').read_text('utf-8-sig'))
new = json.loads((ROOT / 'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json').read_text('utf-8-sig'))
hero_changes = [(a['id'], key) for a,b in zip(old,new) for key in a if a[key] != b[key]]
check(set(hero_changes) == {(3,'weaponTypeName'),(3,'moveSpeed'),(3,'swimSpeed')}, 'Unexpected generated hero changes')
source = json.loads((DATA / 'Source.json').read_text('utf8'))
check(hashlib.sha256((DATA/'WeaponSlosherWashtub.1130.json').read_bytes()).hexdigest() == source['sha256'], 'Pinned reference changed')
actual = asset_values(ROOT/'Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlWeaponConfig.asset')
for key,value in json.loads((DATA/'Tuning.json').read_text('utf8'))['weapons'].items():
    check(actual[key] == value, 'Weapon target mismatch: ' + key)
report = dict(passed=not errors, unchangedOtherHeroAssets=len(unchanged), changedCells=changed, generatedHeroChanges=hero_changes, pinnedSource=source, errors=errors)
output=ROOT/'Reports/Explosher';output.mkdir(parents=True,exist_ok=True)
(output/'scope-audit.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps(report,ensure_ascii=False,indent=2))
raise SystemExit(1 if errors else 0)
