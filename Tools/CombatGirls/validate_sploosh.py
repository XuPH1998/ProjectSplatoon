"""Read-only asset/source audit. Run with Python + openpyxl after SplooshGirlBuilder.Install."""
from pathlib import Path
import hashlib
import json
import math
import re
import openpyxl

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / "Reports/SplooshGirl/Baseline"
OUT = ROOT / "Reports/SplooshGirl/static.json"
checks = []


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    checks.append(label)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


source = ROOT / "Tools/ValidationData/SplooshGirl"
evidence = read(source / "Source.json")
check(sha(source / "WeaponShooterShort.1130.json") == evidence["sha256"], "pinned 11.3.0 source SHA-256")
rows = read(ROOT / "Assets/GameResource/Bootstrap/Config/Luban/tbhero.json")
book = openpyxl.load_workbook(ROOT / "Config/Luban/source/TbHero.xlsx")
sheet = book["Hero"]
fields = [sheet.cell(1, c).value for c in range(1, sheet.max_column + 1)]
check(any(row["id"] == 8 for row in rows), "Sploosh id 8 is present")
for i, row in enumerate(rows, 4):
    for col, field in enumerate(fields, 1):
        if field not in row:
            continue
        expected = sheet.cell(i, col).value
        actual = row[field]
        check(math.isclose(expected, actual, rel_tol=1e-6, abs_tol=1e-6) if isinstance(expected, (int, float)) else expected == actual,
              f"source -> Luban hero {row['id']}/{field}")
shape = ("standingHeight", "bodyRadius", "compactHeight", "controllerStepOffset", "controllerSkinWidth")
for row in rows:
    expected = (1.5, .28, .625, .25, .025) if row["id"] == 8 else (1.8, .35, .7, .3, .03)
    check(all(math.isclose(row[key], value, abs_tol=1e-6) for key, value in zip(shape, expected)), f"hero {row['id']} body dimensions")
sploosh = next(row for row in rows if row["id"] == 8)
check(sploosh["name"] == "SplooshGirl" and sploosh["displayName"] == "铃芽", "hero identity")
weapon = ROOT / sploosh["weaponConfigPath"]
values = dict(re.findall(r"^  (\w+): (.+)$", weapon.read_text(encoding="utf-8-sig"), re.M))
for key, expected in {"fireRate": 12, "shotInk": .8, "damage": 38, "damageMin": 19, "spreadDegrees": 11.66,
                      "jumpSpreadDegrees": 17.49, "shooterDetails": 1, "shooterSplitNum": 5,
                      "inkRecoverLockSeconds": .25, "shootMoveSpeed": .08 * 60 * 18 / 24.037}.items():
    check(math.isclose(float(values[key]), expected, rel_tol=1e-6, abs_tol=1e-6), f"weapon {key}")
for resource in ("Characters/SplooshGirl/Prefabs/SplooshGirlVisual.prefab", "Weapons/SplooshGirl/Prefabs/SplooshGun.prefab",
                 "Weapons/SplooshGirl/SplooshGirlAmmoConfig.asset", "UI/HeroPortraits/SplooshGirlPortrait.png",
                 "Characters/Shared/Paper/SplooshGirl/Paper.asset", "Characters/Shared/Paper/SplooshGirl/PaperCapture.prefab"):
    path = ROOT / "Assets/GameResource" / resource
    check(path.is_file() and Path(str(path) + ".meta").is_file(), resource + " and stable meta")
group = (ROOT / "Assets/AddressableAssetsData/AssetGroups/Splatoon Local.asset").read_text(encoding="utf-8-sig")
for address in ("Character/SplooshGirl", "Weapon/SplooshGun", "Portrait/SplooshGirl", sploosh["weaponConfigPath"]):
    check(address in group, "Addressables " + address)

preserved = []
if (BASE / "manifest.json").exists():
    baseline = read(BASE / "manifest.json")
    for path, expected in baseline.items():
        if path.replace("\\", "/").startswith("Assets/GameResource/Weapons/"):
            check(sha(ROOT / path) == expected, "preserved original weapon " + path)
            preserved.append(path)
    old = openpyxl.load_workbook(BASE / "Config/Luban/source/TbHero.xlsx")["Hero"]
    for row in old:
        for cell in row:
            new = sheet[cell.coordinate]
            check(new.value == cell.value and new._style == cell._style, "preserved workbook " + cell.coordinate)
    original_rows = read(BASE / "Assets/GameResource/Bootstrap/Config/Luban/tbhero.json")
    check(all(all(rows[i][k] == v for k, v in row.items()) for i, row in enumerate(original_rows)), "old seven generated records unchanged")
    for path in ("Assets/Splatoon/Runtime/Prototype/PrototypePlayer.cs", "Assets/Splatoon/Runtime/Painting/PaintSurface.cs"):
        if path.replace("/", "\\") in baseline:
            check(sha(ROOT / path) == baseline[path.replace("/", "\\")], "wire format unchanged " + path)
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps({"passed": True, "checks": len(checks), "details": checks, "oldWeaponFilesPreserved": len(preserved),
                           "baselineAvailable": (BASE / "manifest.json").exists(), "referenceSha256": evidence["sha256"]}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"PASS: {len(checks)} static checks; {len(preserved)} existing weapon files byte-identical; {OUT}")
