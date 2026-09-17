"""One-time, repeatable migration. Read each authored profile; never overwrite migrated ammo."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
FIELDS = ('BurstInterval', 'ParticlesPerBurst', 'VisualLifetime', 'MuzzleInterval',
          'MuzzleBurstCount', 'MaxRibbonGap', 'SatelliteSpread')


def migrate():
    metas = {re.search(r'^guid: (\w+)', p.read_text(encoding='utf-8-sig'), re.M)[1]: p.with_suffix('')
             for p in (ROOT / 'Assets/GameResource').rglob('*InkFlightProfile.asset.meta')}
    for path in (ROOT / 'Assets/GameResource/Weapons').rglob('*AmmoConfig.asset'):
        text = path.read_text(encoding='utf-8-sig')
        if re.search(r'^  flightMigrationVersion: [1-9]', text, re.M):
            print(f'Already migrated: {path.name}')
            continue
        guid = re.search(r'^  flightProfile: .*guid: (\w+)', text, re.M)[1]
        profile = metas[guid].read_text(encoding='utf-8-sig')
        values = {field: re.search(rf'^  {field}: (.+)$', profile, re.M)[1] for field in FIELDS}
        migrated = '  flightMigrationVersion: 1\n' + ''.join(
            f'  {field[0].lower() + field[1:]}: {value}\n' for field, value in values.items())
        text = re.sub(r'^  flightProfile:.*\n', lambda _: migrated, text, flags=re.M)
        path.write_text(text, encoding='utf-8')
        print(f'Migrated: {path.name}: {values}')


if __name__ == '__main__':
    migrate()
