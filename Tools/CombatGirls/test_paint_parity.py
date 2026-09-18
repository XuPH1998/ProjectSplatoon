"""Meaningful import boundary tests: no historical weapon/workbook replay."""
from pathlib import Path
import shutil
import tempfile
import unittest
import paint_parity as parity

class PaintImportTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.original=parity.ROOT
        for name in parity.NAMES:
            relative=Path(f'Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset')
            dest=self.root/relative;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(parity.ROOT/relative,dest)
        for directory in ('WeaponAlignment','Explosher'):
            src=parity.ROOT/'Tools/ValidationData'/directory;dst=self.root/'Tools/ValidationData'/directory
            dst.mkdir(parents=True,exist_ok=True)
            for p in src.glob('*.json'):shutil.copy2(p,dst/p.name)
        parity.ROOT=self.root
    def tearDown(self):
        parity.ROOT=self.original;self.temp.cleanup()
    def test_only_verified_foot_depth_changes_and_repeat_is_idempotent(self):
        paths=list((self.root/'Assets').rglob('*WeaponConfig.asset'))
        bubble=next(p for p in paths if p.parent.name=='BubbleGirl')
        bubble.write_text(bubble.read_text().replace('  referenceFootDepth: 1.2','  referenceFootDepth: 1'),encoding='utf-8')
        before={p:parity.values(p) for p in paths}
        parity.apply();first={p:p.read_bytes() for p in paths};parity.apply()
        self.assertEqual(first,{p:p.read_bytes() for p in paths})
        for p in paths:
            after=parity.values(p);expected=before[p].copy()
            if p.parent.name=='BubbleGirl':expected['referenceFootDepth']='1.2'
            self.assertEqual(expected,after)
        self.assertFalse((self.root/'Config').exists())
    def test_unverified_source_is_rejected_before_any_asset_write(self):
        paths=list((self.root/'Assets').rglob('*WeaponConfig.asset'));before={p:p.read_bytes() for p in paths}
        p=parity.source(6);p.write_bytes(p.read_bytes()+b' ')
        with self.assertRaisesRegex(ValueError,'hash mismatch'):parity.apply()
        self.assertEqual(before,{p:p.read_bytes() for p in paths})

if __name__=='__main__':unittest.main()
