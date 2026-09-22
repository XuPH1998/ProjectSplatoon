"""Import the six approved weapon profiles from frozen 11.3.0 data; no Luban edits."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
S = 18 / 24.037
WEAPONS = {
    'RifleGirl': ('WeaponAlignment/WeaponShooterNormal.1130.json', 1, 0, 0),
    'PistolGirl': ('WeaponAlignment/WeaponShooterPrecision.1130.json', 1, 0, 0),
    'SplooshGirl': ('MainWeaponReplacement/WeaponShooterBlaze.1130.json', 5, 1, 4),
    'DualPistolGirl': ('WeaponAlignment/WeaponManeuverGallon.1130.json', 5, 0, 0),
    'MachineGunGirl': ('MainWeaponReplacement/WeaponSpinnerQuick.1130.json', 1, 0, 0),
    'RocketLauncherGirl': ('WeaponAlignment/WeaponBlasterLight.1130.json', 1, 0, 0),
}


def main():
    ledger = {'referenceVersion': '11.3.0', 'scale': S, 'originalAlgorithmVerified': False, 'weapons': {}}
    for hero, (relative, foot_every, foot_sequence, foot_phase) in WEAPONS.items():
        source = ROOT / 'Tools/ValidationData' / relative
        data = json.loads(source.read_text(encoding='utf-8-sig'))['GameParameters']
        asset = ROOT / f'Assets/GameResource/Weapons/{hero}/{hero}WeaponConfig.asset'
        text = asset.read_text(encoding='utf-8-sig')
        baseline = ROOT / 'Temp/AimBallistics/Baseline' / asset.name
        baseline.parent.mkdir(parents=True, exist_ok=True)
        if not baseline.exists():
            baseline.write_text(text, encoding='utf-8')
        fields = {}

        def put(field, value, evidence):
            nonlocal text
            fields[field] = {'value': value, 'evidence': evidence}
            line = f'  {field}: {int(value) if isinstance(value, bool) else value}'
            pattern = rf'^  {re.escape(field)}:.*$'
            if re.search(pattern, text, flags=re.M):
                text = re.sub(pattern, lambda _: line, text, flags=re.M)
            else:
                text = text.rstrip() + '\n' + line + '\n'

        def raw(field, group, key, scale=1, default=None):
            present = key in data.get(group, {})
            value = data[group][key] if present else default
            if value is None:
                return
            put(field, value * scale, f'{group}.{key} * {scale}' if present else f'Project default: {key}={default}, scale={scale}')

        for field, value in {'aimMode': 1, 'angularSpread': 1, 'inheritForwardMovement': 1,
                             'detailedPaint': 1, 'shooterDetails': 0, 'referenceTrailRandomPhase': 0,
                             'referenceFootEvery': foot_every, 'footSequence': foot_sequence, 'footPhase': foot_phase}.items():
            put(field, value, 'Approved project reconstruction / independent capability and schedule')
        raw('shotGuideSeconds', 'WeaponParam', 'ShotGuideFrame', 1/60)
        raw('shooterMoveForwardRate', 'spl__SpawnBulletAdditionMovePlayerParam', 'ZRate', default=0)
        raw('referenceTrailBudget', 'SplashSpawnParam', 'SpawnNum')
        raw('shooterSplitNum', 'SplashSpawnParam', 'SplitNum')
        raw('referenceTrailStart', 'SplashSpawnParam', 'SpawnNearestLength', S)
        raw('trailSpacing', 'SplashSpawnParam', 'SpawnBetweenLength', S)
        raw('referenceFootRadius', 'SplashPaintParam', 'WidthHalfNearest', S)
        raw('shooterPaintNearDistance', 'PaintParam', 'DistanceNear', S, 0)
        raw('shooterPaintNearRadius', 'PaintParam', 'WidthHalfNear', S)
        raw('paintDistanceFar', 'PaintParam', 'DistanceFar', S)
        for field, key in [('shooterSplashDepthMin', 'DepthScaleMin'), ('shooterSplashDepthMax', 'DepthScaleMax')]:
            raw(field, 'SplashPaintParam', key, default=1 if key.endswith('Min') else 1.2)
        raw('shooterSplashHeightMin', 'SplashPaintParam', 'DepthMaxDropHeight', S, 3)
        raw('shooterSplashHeightMax', 'SplashPaintParam', 'DepthMinDropHeight', S, 10)
        for field, value in {'shooterPaintAngleMin': 10, 'shooterPaintAngleMax': 35,
                             'shooterFallHeightMin': 1.5*S, 'shooterFallHeightMax': 10*S,
                             'shooterSplashSideSpeed': .055*60*S, 'shooterSplashUpSpeed': .015*60*S,
                             'shooterSplashForwardMin': .01*60*S, 'shooterSplashForwardMax': .02*60*S,
                             'shooterWallGravity': .008*3600*S}.items():
            put(field, value, 'Explicit project approximation, generalized from the documented Sploosh profile')
        for field, key in {'shooterWallFirstMin': 'FallPeriodFirstFrameMin', 'shooterWallFirstMax': 'FallPeriodFirstFrameMax',
                           'shooterWallMiddle': 'FallPeriodSecondFrame', 'shooterWallLastMin': 'FallPeriodLastFrameMin',
                           'shooterWallLastMax': 'FallPeriodLastFrameMax'}.items():
            raw(field, 'WallDropMoveParam', key, 1/60)
        raw('shooterWallFirstSpeed', 'WallDropMoveParam', 'FallPeriodFirstTargetSpeed', 60*S)
        raw('wallDropSpeed', 'WallDropMoveParam', 'FallPeriodSecondTargetSpeed', 60*S)
        raw('shooterWallShockRadius', 'WallDropCollisionPaintParam', 'PaintRadiusShock', S)
        raw('wallDropRadius', 'WallDropCollisionPaintParam', 'PaintRadiusFall', S)
        raw('wallDropGroundRadius', 'WallDropCollisionPaintParam', 'PaintRadiusGround', S)
        if hero == 'PistolGirl':
            put('referenceSpreadEnabled', 1, 'Zero angular envelope; enable shared spread state without changing accuracy')
        if hero == 'RocketLauncherGirl':
            for field, key in {'referenceBiasMin': 'Stand_DegBiasMin', 'referenceBiasMax': 'Stand_DegBiasMax',
                               'referenceBiasPerShot': 'Stand_DegBiasKf'}.items():
                raw(field, 'WeaponParam', key)
        ledger['weapons'][hero] = {'source': str(source.relative_to(ROOT)).replace('\\', '/'),
                                 'sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
                                 'fields': fields,
                                 'uninterpretedForceSpawnNearestAddNumArray': data['SplashSpawnParam'].get('ForceSpawnNearestAddNumArray', [])}
        asset.write_text(text, encoding='utf-8', newline='\n')
    output = ROOT / 'Tools/ValidationData/AimBallistics/Import.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(ledger, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'Imported {len(WEAPONS)} weapon profiles; provenance: {output.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
