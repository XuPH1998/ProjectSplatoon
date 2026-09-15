"""Regression checks for safe repeated migration and preservation of authored data."""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from migrate_weapon_seconds import ROOT, TIME_FIELDS, asset_values, convert


class WeaponSecondsMigrationTests(unittest.TestCase):
    def setUp(self):
        self.values = json.loads((ROOT / "Tools/ValidationData/WeaponSeconds/Migration.json").read_text("utf-8"))["assets"][0]["before"]

    def document(self, values):
        return "MonoBehaviour:\n  m_Name: AuthoredWeapon\n" + "".join(f"  {k}: {json.dumps(v)}\n" for k, v in values.items())

    def test_all_six_actual_assets_preserve_every_value(self):
        audit = json.loads((ROOT / "Tools/ValidationData/WeaponSeconds/Migration.json").read_text("utf-8"))
        for entry in audit["assets"]:
            actual = asset_values((ROOT / entry["path"]).read_text("utf-8-sig"))
            self.assertEqual(actual, entry["after"])
            for key, value in entry["before"].items():
                self.assertEqual(actual[TIME_FIELDS.get(key, key)], value / 60 if key in TIME_FIELDS else value)

    def test_preserves_custom_values_and_non_time_numbers(self):
        self.values.update(startFrames=7, chargeFrames=157, fireMode=4, muzzleMode=1, pelletCount=7, spreadRecoverSeconds=.73456789, damage=123.456)
        result, changed = convert(self.document(self.values))
        after = asset_values(result)
        self.assertTrue(changed)
        self.assertEqual(after["startSeconds"], 7 / 60)
        self.assertEqual(after["chargeSeconds"], 157 / 60)
        for key in ["fireMode", "muzzleMode", "pelletCount", "spreadRecoverSeconds", "damage"]:
            self.assertEqual(after[key], self.values[key])

    def test_second_conversion_is_byte_identical(self):
        text, _ = convert(self.document(self.values))
        again, changed = convert(text)
        self.assertFalse(changed)
        self.assertEqual(again, text)

    def test_mixed_missing_duplicate_and_invalid_values_are_rejected(self):
        for update in ({"startSeconds": .5}, {"startFrames": -1}, {"startFrames": 1.5}, {"fireMode": 99}):
            with self.subTest(update=update), self.assertRaises(ValueError):
                convert(self.document(dict(self.values, **update)))
        partial = dict(self.values); partial.pop("chargeFrames")
        with self.assertRaises(ValueError): convert(self.document(partial))
        with self.assertRaises(ValueError): convert(self.document(self.values) + "  chargeFrames: 60\n")

    def test_later_invalid_asset_prevents_any_earlier_writes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); assets = root / "Assets"; assets.mkdir()
            a = assets / "AWeaponConfig.asset"; b = assets / "BWeaponConfig.asset"
            a.write_text(self.document(self.values), encoding="utf-8")
            b.write_text(self.document(dict(self.values, startSeconds=.5)), encoding="utf-8")
            for path in [a, b]: Path(str(path) + ".meta").write_text("guid: preserved", encoding="utf-8")
            original = a.read_bytes()
            run = subprocess.run([sys.executable, str(ROOT / "Tools/CombatGirls/migrate_weapon_seconds.py"), "--apply", "--root", folder], capture_output=True)
            self.assertNotEqual(run.returncode, 0)
            self.assertEqual(a.read_bytes(), original)
            self.assertFalse((root / "Reports/WeaponSeconds/migration.json").exists())

    def test_cli_audit_survives_repeat_and_metadata_never_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); assets = root / "Assets"; assets.mkdir()
            asset = assets / "AWeaponConfig.asset"; asset.write_text(self.document(self.values), encoding="utf-8")
            meta = Path(str(asset) + ".meta"); meta.write_text("guid: preserved", encoding="utf-8")
            args = [sys.executable, str(ROOT / "Tools/CombatGirls/migrate_weapon_seconds.py"), "--apply", "--root", folder]
            subprocess.run(args, check=True, capture_output=True)
            audit = (root / "Reports/WeaponSeconds/migration.json").read_bytes()
            converted = asset.read_bytes()
            subprocess.run(args, check=True, capture_output=True)
            self.assertEqual(asset.read_bytes(), converted)
            self.assertEqual((root / "Reports/WeaponSeconds/migration.json").read_bytes(), audit)
            self.assertEqual(meta.read_text(), "guid: preserved")


if __name__ == "__main__":
    unittest.main()
