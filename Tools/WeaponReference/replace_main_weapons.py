"""Apply/verify the approved Mini Splatling and Aerospray MG 11.3.0 profiles.

Weapon identities and assets stay in their existing hero slots. The four Luban
source cells are edited separately; run the formal generator after that edit.
Unspecified inherited fields deliberately retain the frozen project defaults.
"""
from pathlib import Path
import argparse
import hashlib
import json
import math
import re
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'Tools/ValidationData/MainWeaponReplacement'
S = 18 / 24.037
COMMIT = '7280ff9cde8bb1c5dcef46c700c326471584d2e6'
PROFILES = {6: ('MachineGunGirl', 'WeaponSpinnerQuick', '斯普拉旋转枪'),
            8: ('SplooshGirl', 'WeaponShooterBlaze', '专业模型枪MG')}


def read_asset(path):
    values = {}
    for key, value in re.findall(r'^  (\w+): ([^\n]+)', path.read_text('utf-8-sig'), re.M):
        try:
            values[key] = json.loads(value)
        except json.JSONDecodeError:
            pass
    return values


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def profile(hero, source, before):
    p = source['GameParameters']
    values = dict(before)
    fields = {}

    def put(field, value, source_field=None, raw=None, factor=None, reason=None):
        values[field] = value
        fields[field] = dict(before=before.get(field), after=value, sourceField=source_field,
                             originalValue=raw, conversionFactor=factor,
                             evidence='source-explicit' if source_field else 'project-adaptation', reason=reason)

    def raw(field, group, key, factor=1):
        if key in p.get(group, {}):
            value = p[group][key]
            put(field, value * factor, group + '.' + key, value, factor)

    repeat = p['WeaponParam']['RepeatFrame']
    put('fireRate', 60 / repeat, 'WeaponParam.RepeatFrame', repeat, None, '60 / reference frames')
    for field, key in {'damage':'ValueMax', 'damageMin':'ValueMin'}.items():
        raw(field, 'DamageParam', key, .1)
    for field, key in {'damageReduceStartSeconds':'ReduceStartFrame', 'damageReduceEndSeconds':'ReduceEndFrame'}.items():
        raw(field, 'DamageParam', key, 1/60)
    for field, key in {'inkRecoverLockSeconds':'InkRecoverStop',
                       'shotGuideSeconds':'ShotGuideFrame', 'referenceJumpStart':'Jump_DegBiasDecreaseStartFrame',
                       'referenceJumpEnd':'Jump_DegBiasEndFrame'}.items():
        raw(field, 'WeaponParam', key, 1/60)
    raw('shootMoveSpeed', 'WeaponParam', 'MoveSpeed', 60*S)
    for field, key in {'spreadDegrees':'Stand_DegSwerve', 'jumpSpreadDegrees':'Jump_DegSwerve',
                       'referenceBiasMin':'Stand_DegBiasMin', 'referenceBiasMax':'Stand_DegBiasMax',
                       'referenceBiasPerShot':'Stand_DegBiasKf', 'referenceJumpBias':'Jump_DegBiasMax',
                       'referencePitchBias':'PitchDegBias'}.items():
        raw(field, 'WeaponParam', key)
    raw('referenceBiasRecovery', 'WeaponParam', 'Stand_DegBiasDecrease', 60)
    raw('straightSeconds', 'MoveParam', 'GoStraightToBrakeStateFrame', 1/60)
    raw('referenceBrakeEndSpeed', 'MoveParam', 'GoStraightStateEndMaxSpeed', 60*S)
    raw('projectileGravity', 'MoveParam', 'FreeGravity', 3600*S)
    raw('collisionRadius', 'CollisionParam', 'EndRadiusForField', S)
    raw('referencePlayerRadius', 'CollisionParam', 'EndRadiusForPlayer', S)
    raw('shooterMoveForwardRate', 'spl__SpawnBulletAdditionMovePlayerParam', 'ZRate')
    for field, key in {'paintRadiusMin':'WidthHalfFar', 'paintRadiusMax':'WidthHalfMiddle',
                       'shooterPaintNearRadius':'WidthHalfNear', 'shooterPaintNearDistance':'DistanceNear',
                       'paintDistanceMiddle':'DistanceMiddle', 'paintDistanceFar':'DistanceFar'}.items():
        raw(field, 'PaintParam', key, S)
    for field, key in {'paintDepthMin':'DepthScaleMin', 'paintDepthMax':'DepthScaleMax',
                       'paintDepthBreakMin':'DepthScaleMinBreakFree', 'paintDepthBreakMax':'DepthScaleMaxBreakFree'}.items():
        raw(field, 'PaintParam', key)
    for field, key in {'trailRadiusMin':'WidthHalf', 'trailRadiusMax':'WidthHalf',
                       'referenceFootRadius':'WidthHalfNearest', 'shooterSplashHeightMin':'DepthMaxDropHeight',
                       'shooterSplashHeightMax':'DepthMinDropHeight'}.items():
        raw(field, 'SplashPaintParam', key, S)
    for field, key in {'trailDepthScale':'DepthScaleMax', 'shooterSplashDepthMin':'DepthScaleMin',
                       'shooterSplashDepthMax':'DepthScaleMax'}.items():
        raw(field, 'SplashPaintParam', key)
    for field, key, factor in [('referenceTrailBudget','SpawnNum',1), ('shooterSplitNum','SplitNum',1),
                               ('referenceTrailStart','SpawnNearestLength',S), ('trailSpacing','SpawnBetweenLength',S)]:
        raw(field, 'SplashSpawnParam', key, factor)
    for field, key in {'wallDropRadius':'PaintRadiusFall', 'wallDropGroundRadius':'PaintRadiusGround',
                       'shooterWallShockRadius':'PaintRadiusShock'}.items():
        raw(field, 'WallDropCollisionPaintParam', key, S)
    for field, key in {'shooterWallFirstMin':'FallPeriodFirstFrameMin', 'shooterWallFirstMax':'FallPeriodFirstFrameMax',
                       'shooterWallMiddle':'FallPeriodSecondFrame', 'shooterWallLastMin':'FallPeriodLastFrameMin',
                       'shooterWallLastMax':'FallPeriodLastFrameMax'}.items():
        raw(field, 'WallDropMoveParam', key, 1/60)
    raw('shooterWallFirstSpeed', 'WallDropMoveParam', 'FallPeriodFirstTargetSpeed', 60*S)
    raw('wallDropSpeed', 'WallDropMoveParam', 'FallPeriodSecondTargetSpeed', 60*S)
    put('wallDropSeconds', values['shooterWallFirstMax'] + values['shooterWallMiddle'] + values['shooterWallLastMax'],
        reason='Derived maximum of the imported wall phases')
    if hero == 6:
        for field, key in {'chargeSeconds':'ChargeFrame_Second', 'splatlingFirstChargeSeconds':'ChargeFrame_First',
                           'splatlingFirstShootSeconds':'MaxShootingFrame_First', 'splatlingFullShootSeconds':'MaxShootingFrame_Second',
                           'splatlingPostSeconds':'PostDelayFrame', 'emergeStartSeconds':'PreDelayFrame_SquidShot'}.items():
            raw(field, 'WeaponParam', key, 1/60)
        raw('damage', 'DamageParam', 'ValueFullChargeMax', .1)
        raw('chargePartialMaxDamage', 'DamageParam', 'ValueMax', .1)
        raw('chargeMinDamage', 'DamageParam', 'ValueMax', .1)
        raw('splatlingSlowChargeMultiplier', 'WeaponParam', 'InkEmptyChargeTimes')
        fields['splatlingSlowChargeMultiplier']['reason'] = 'Approved shared air/empty multiplier; conditions do not stack'
        raw('splatlingChargeMoveSpeed', 'WeaponParam', 'MoveSpeed_Charge', 60*S)
        raw('splatlingChargeJumpSpeed', 'WeaponParam', 'JumpGnd_Charge', 60*S)
        raw('splatlingPitchSpread', 'WeaponParam', 'PitchDegSwerve')
        raw('splatlingPlayerRadius', 'CollisionParam', 'EndRadiusForPlayer', S)
        raw('splatlingFootRadius', 'SplashPaintParam', 'WidthHalfNearest', S)
        raw('chargeMinSpeed', 'MoveParam', 'SpawnSpeed', 60*S)
        raw('splatlingSpeedBias', 'MoveParam', 'SpawnSpeedRandomBias')
        center = p['MoveParam']['SpawnSpeedFirstLastAndSecond']
        jitter = p['MoveParam']['SpawnSpeedRandomRate']
        for field, sign in [('speedMin', -1), ('speedMax', 1)]:
            put(field, (center + sign*jitter)*60*S, reason='Existing project additive +/- random-rate interpretation, center 1.5 and amplitude 0.1')
        rounds = 1 + math.floor(values['splatlingFullShootSeconds']*values['fireRate'] + 1e-5)
        for field in ['shotInk', 'chargeMinInk']:
            put(field, p['WeaponParam']['InkConsume']*100/rounds, 'WeaponParam.InkConsume', p['WeaponParam']['InkConsume'],
                100/rounds, 'Full magazine ink divided by inclusive round count')
        raw('chargeMinSpread', 'WeaponParam', 'Stand_DegSwerve')
        raw('chargeMinJumpSpread', 'WeaponParam', 'Jump_DegSwerve')
        put('splatlingTrailCount', math.ceil(values['referenceTrailBudget']), reason='Legacy cap mirrors imported fractional drop budget')
    else:
        raw('shooterPostSeconds', 'WeaponParam', 'PostDelayFrame', 1/60)
        raw('shotInk', 'WeaponParam', 'InkConsume', 100)
        raw('speedMin', 'MoveParam', 'SpawnSpeed', 60*S)
        raw('speedMax', 'MoveParam', 'SpawnSpeed', 60*S)

    def distance(speed):
        # Independent 60 Hz horizontal integration through the damage-minimum age.
        at = values['damageReduceEndSeconds']
        straight = values['straightSeconds']
        d = speed * min(at, straight)
        if at <= straight:
            return d
        speed = min(speed, values['referenceBrakeEndSpeed'])
        for duration, drag in [(min(at-straight, values['brakeSeconds']), values['referenceBrakeDrag']),
                               (max(0, at-straight-values['brakeSeconds']), values['referenceFreeDrag'])]:
            frames = duration * 60
            n = int(math.floor(frames + 1e-8))
            for _ in range(n):
                speed *= 1-drag
                d += speed/60
            if frames-n > 1e-8:
                speed *= 1-drag
                d += speed*(frames-n)/60
        return d
    put('effectiveRange', distance((values['speedMin']+values['speedMax'])/2),
        reason='Derived horizontal center trajectory at damage-minimum age; UI also computes actual flat-ground range')
    if hero == 6:
        put('chargeMinRange', distance(values['chargeMinSpeed']), reason='Same metric at minimum-charge center speed')
    for field, value in before.items():
        if field not in fields and not field.startswith('m_'):
            fields[field] = dict(before=value, after=value, evidence='retained-project-value',
                                 reason='No approved replacement mapping; preserve existing behavior')
    return fields


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    manifest = dict(version='11.3.0', scale=S, commit=COMMIT, weapons={}, heroChanges={
        '6':dict(weaponTypeName='斯普拉旋转枪', moveSpeed=.096*60*S, swimSpeed=.192*60*S),
        '8':dict(weaponTypeName='专业模型枪MG')})
    for hero, (name, reference, display) in PROFILES.items():
        asset = ROOT / f'Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset'
        cached = DATA / f'{reference}.1130.json'
        baseline = DATA / 'Baseline' / asset.name
        url = f'https://raw.githubusercontent.com/Leanny/splat3/{COMMIT}/data/parameter/1130/weapon/{reference}.game__GameParameterTable.json'
        if args.apply:
            if not cached.exists():
                cached.parent.mkdir(parents=True, exist_ok=True)
                cached.write_bytes(urllib.request.urlopen(url, timeout=40).read())
            if not baseline.exists():
                baseline.parent.mkdir(parents=True, exist_ok=True)
                baseline.write_bytes(asset.read_bytes())
        fields = profile(hero, json.loads(cached.read_text('utf-8')), read_asset(baseline))
        if args.apply:
            text = asset.read_text('utf-8-sig')
            for key, entry in fields.items():
                if entry['evidence'] == 'retained-project-value':
                    continue
                value = entry['after']
                if isinstance(value, float) and value.is_integer(): value = int(value)
                line = f'  {key}: {value}'
                pattern = rf'^  {re.escape(key)}:.*$'
                text = re.sub(pattern, lambda _: line, text, flags=re.M) if re.search(pattern, text, re.M) else text.rstrip()+'\n'+line+'\n'
            asset.write_text(text, encoding='utf-8', newline='\n')
        current = read_asset(asset)
        for key, entry in fields.items():
            expected = entry['after']
            actual = current.get(key)
            assert math.isclose(actual, expected, rel_tol=1e-6, abs_tol=1e-7) if isinstance(expected, (int,float)) else actual == expected, (hero,key,actual,expected)
        manifest['weapons'][str(hero)] = dict(name=display, asset=str(asset.relative_to(ROOT)).replace('\\','/'),
            reference=reference, url=url, sha256=hashlib.sha256(cached.read_bytes()).hexdigest(), fields=fields,
            uninterpretedForceSpawnNearestAddNumArray=json.loads(cached.read_text())['GameParameters']['SplashSpawnParam'].get('ForceSpawnNearestAddNumArray'))
    if args.apply:
        write_json(DATA / 'Tuning.json', manifest)
    else:
        assert json.loads((DATA/'Tuning.json').read_text('utf-8')) == manifest, 'Reference ledger mismatch'
        heroes = json.loads((ROOT/'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json').read_text('utf-8-sig'))
        for hero, fields in manifest['heroChanges'].items():
            row = next(h for h in heroes if h['id'] == int(hero))
            for key, expected in fields.items():
                assert math.isclose(row[key], expected, rel_tol=1e-6) if isinstance(expected, (float,int)) else row[key] == expected, (hero,key)
    print('PASS: Mini Splatling and Aerospray MG assets, frozen references and conversion ledger' + (' applied' if args.apply else ' verified with generated hero table'))


if __name__ == '__main__':
    main()
