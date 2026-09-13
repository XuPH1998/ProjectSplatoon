"""Read-only provenance, workbook, legacy dependency and image comparison audit."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import subprocess
import openpyxl

parser = argparse.ArgumentParser()
parser.add_argument('--project', default=str(Path(__file__).resolve().parents[2]))
parser.add_argument('--validated-project')
args = parser.parse_args()
root = Path(args.project).resolve()
out = root / 'Docs/CombatGirls'
manifest = json.loads((out / 'source-assets.json').read_text(encoding='utf-8'))
report = {'sourceUnchanged': True, 'artUnchanged': True, 'workbookChanges': {}, 'legacyExternalReferences': [], 'errors': []}
for record in manifest['files']:
    original, target = Path(record['source']), root / record['target']
    if hashlib.sha256(original.read_bytes()).hexdigest() != record['sourceSha256']:
        report['sourceUnchanged'] = False
        report['errors'].append('Source changed: ' + str(original))
    if target.suffix.lower() in ('.mat', '.fbx', '.png', '.tga', '.jpg', '.psd'):
        if original.read_bytes() != target.read_bytes():
            report['artUnchanged'] = False
            report['errors'].append('Art changed: ' + str(target))
for name in ('TbCharacter', 'TbWeapon'):
    backup = root / 'Logs/CombatGirls' / (name + '.before.xlsx')
    if not backup.exists():
        continue
    def cells(path):
        book = openpyxl.load_workbook(path)
        return {(sheet.title, c.coordinate): c.value for sheet in book for row in sheet for c in row}
    before, after = cells(backup), cells(root / 'Config/Luban/source' / (name + '.xlsx'))
    changes = [{'sheet': key[0], 'cell': key[1], 'before': before.get(key), 'after': after.get(key)}
               for key in before.keys() | after.keys() if before.get(key) != after.get(key)]
    report['workbookChanges'][name] = changes
    if any(c['cell'] not in ('C4', 'D4') for c in changes):
        report['errors'].append(name + ' changed unrelated cells')

legacy = root / 'Assets/GameResource/Characters/Jammo'
cleanup_file = out / 'legacy-cleanup.json'
if legacy.exists():
    files = list(legacy.rglob('*')) + [Path(str(legacy) + '.meta')]
    records = [{'path': p.relative_to(root).as_posix(), 'bytes': p.stat().st_size,
                'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in files if p.is_file()]
    guids = {re.search(r'(?m)^guid: (\w+)', p.read_text(encoding='utf-8-sig'))[1]
             for p in files if p.is_file() and p.suffix == '.meta'}
    cleanup = {'path': legacy.relative_to(root).as_posix(), 'files': records, 'guids': sorted(guids)}
else:
    cleanup = json.loads(cleanup_file.read_text(encoding='utf-8'))
    guids = set(cleanup['guids'])
for path in (root / 'Assets').rglob('*'):
    if not path.is_file() or legacy in path.parents or path == Path(str(legacy) + '.meta'):
        continue
    if path.suffix not in ('.meta', '.prefab', '.unity', '.asset', '.mat', '.controller', '.mask'):
        continue
    found = set(re.findall(r'guid: ([a-f0-9]{32})', path.read_text(encoding='utf-8-sig', errors='ignore'))) & guids
    if found:
        report['legacyExternalReferences'].append({'path': path.relative_to(root).as_posix(), 'guids': sorted(found)})
report['legacyRemoved'] = not legacy.exists()
report['legacyFileCount'] = len(cleanup['files'])
report['legacyBytes'] = sum(f['bytes'] for f in cleanup['files'])
report['legacyExternalReferences'] and report['errors'].append('Legacy assets still referenced')
cleanup['removed'] = report['legacyRemoved']
cleanup['externalReferences'] = report['legacyExternalReferences']
cleanup_file.write_text(json.dumps(cleanup, ensure_ascii=False, indent=2), encoding='utf-8')
report['clipFiles'] = sorted(p.stem for p in (root / 'Assets/ThirdParty/CombatGirls').rglob('*.fbx') if 'Animations' in p.parts)
if report['clipFiles'] != sorted(manifest['clips']):
    report['errors'].append('Animation whitelist mismatch')
report['passed'] = not report['errors']
if args.validated_project:
    validated = Path(args.validated_project)
    count = 0
    for directory in ('Assets/Splatoon', 'Assets/ThirdParty/CombatGirls', 'Assets/GameResource/Characters/RifleGirl',
                      'Assets/GameResource/Weapons/RifleGirl', 'Assets/AddressableAssetsData', 'Config/Luban/source',
                      'Assets/GameResource/Bootstrap/Config/Luban'):
        for path in (root / directory).rglob('*'):
            if not path.is_file():
                continue
            relative = path.relative_to(root)
            other = validated / relative
            if not other.exists() or path.read_bytes() != other.read_bytes():
                report['errors'].append('Validated copy mismatch: ' + relative.as_posix())
            count += 1
    for relative in ('Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab', 'Packages/manifest.json', 'Packages/packages-lock.json'):
        if (root / relative).read_bytes() != (validated / relative).read_bytes():
            report['errors'].append('Validated copy mismatch: ' + relative)
        count += 1
    report['validatedFilesCompared'] = count
    report['passed'] = not report['errors']
(out / 'static-validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
raise SystemExit(0 if report['passed'] else 1)
