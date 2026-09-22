"""Evidence-gated paint imports and reports. Never replays historical combat tuning.

Default: audit cached 11.3.0 sources. --verify-online checks immutable source hashes.
--apply applies only the reviewed paint corrections. --report aggregates Unity output.
"""
from pathlib import Path
import argparse, csv, hashlib, json, math, re, statistics, urllib.request
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Reports/PaintParity1130'
COMMIT = '7280ff9cde8bb1c5dcef46c700c326471584d2e6'
S = 18 / 24.037
NAMES = ['RifleGirl', 'DualPistolGirl', 'ShotgunGirl', 'PistolGirl', 'RocketLauncherGirl', 'MachineGunGirl', 'BubbleGirl']
REFS = ['WeaponShooterNormal', 'WeaponManeuverGallon', 'WeaponSlosherWashtub', 'WeaponShooterPrecision', 'WeaponBlasterLight', 'WeaponSpinnerQuick', 'WeaponSlosherBathtub']
MECHANISM_GAPS = [
    ['SplitNum=8 phase/reset and SpawnNum=1.5 allocation', 'ForceSpawnNearestAddNumArray first-three-shot behavior', 'Impact near/middle/far interpolation and drop-height depth'],
    ['Normal-fire allocation and per-hand foot schedule', 'Alternating muzzle overlap; rolling parameters excluded'],
    ['Trail drop-height depth and distance interpolation', 'Entity impact paint versus damage offset', 'Wall drip first/last phases; vertical subdrop trajectory'],
    ['Zero angular spread does not specify trail-drop positions', 'SplitNum phase and nearest forced-drop schedule'],
    ['In-flight stamps versus collision and timed-airburst paint', 'Original explosion mask and occlusion boundaries'],
    ['Minimum/first-ring/full-charge drop schedule', 'Foot schedule per magazine and slow-charge resource regeneration'],
    ['First versus subsequent bubble nearest-paint schedule', 'First versus later bounce paint and after-paint', 'Unspecified inherited bounce/trail defaults']
]
UNIT = 'UnitGroupParam.Unit.0.'
SPLASH = UNIT+'SplashAndSplashWallHitSpawnPrm.SplashParam.0.'
EXPLOSHER_PAINT_MAP = {
    'paintRadiusMin':(UNIT+'PaintParam.WidthHalfNear',S), 'paintRadiusMax':(UNIT+'PaintParam.WidthHalfFar',S),
    'paintDistanceMiddle':(UNIT+'PaintParam.DistanceXZNear',S), 'paintDistanceFar':(UNIT+'PaintParam.DistanceXZFar',S),
    'paintDepthMin':(UNIT+'PaintParam.DepthScaleNear',1), 'paintDepthMax':(UNIT+'PaintParam.DepthScaleFar',1),
    'paintDepthBreakMin':(UNIT+'PaintParam.WidthDepthScaleFall',1), 'paintDepthBreakMax':(UNIT+'PaintParam.WidthDepthScaleFall',1),
    'paintBreakHeight':(UNIT+'PaintParam.ScaleStartFallDistance',S),
    'trailSpacing':(SPLASH+'SpawnParam.SpawnBetweenLength',S), 'referenceTrailBudget':(SPLASH+'SpawnParam.SpawnNum',1),
    'trailRadiusMin':(SPLASH+'DrawSizeCollisionPaintParam.PaintWidthHalf',S), 'trailRadiusMax':(SPLASH+'DrawSizeCollisionPaintParam.PaintWidthHalf',S),
    'trailDepthScale':(SPLASH+'DrawSizeCollisionPaintParam.PaintDepthScale',1),
    'referenceFootRadius':('NearestParam.DrawSizeCollisionPaintParam.PaintWidthHalf',S),
    'explosherFootDepth':('NearestParam.DrawSizeCollisionPaintParam.PaintDepthScale',1),
    'wallDropRadius':(UNIT+'WallDropCollisionPaintParam.PaintRadiusFall',S),
    'wallDropGroundRadius':(UNIT+'WallDropCollisionPaintParam.PaintRadiusGround',S),
    'wallDropSpeed':(UNIT+'WallDropMoveParam.FallPeriodSecondTargetSpeed',60*S),
    'collisionExplosionPaintRadius':('BlastParam.BlastParam.CollisionRadiusForPaint',S),
    'explosherPaintNearDistance':('BlastParam.DistanceNear',S), 'explosherPaintFarDistance':('BlastParam.DistanceFar',S),
    'explosherTrailPhaseMax':(SPLASH+'SpawnParam.FirstSplashRateForLengthMax',1)
}

def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def values(path):
    return dict(re.findall(r'^  (\w+): ([^\n]+)', path.read_text(encoding='utf-8-sig'), re.M))

def source(i):
    folder = 'Explosher' if i == 2 else 'MainWeaponReplacement' if i == 5 else 'WeaponAlignment'
    return ROOT / f'Tools/ValidationData/{folder}/{REFS[i]}.1130.json'

def url(i):
    return f'https://raw.githubusercontent.com/Leanny/splat3/{COMMIT}/data/parameter/1130/weapon/{REFS[i]}.game__GameParameterTable.json'

def leaves(node, prefix=''):
    if isinstance(node, dict):
        for k,v in node.items():
            yield from leaves(v, prefix+'.'+k if prefix else k)
    elif isinstance(node,list):
        if not node: yield prefix, []
        for i,v in enumerate(node): yield from leaves(v, f'{prefix}.{i}')
    else: yield prefix,node

def corrections():
    data=json.loads(source(6).read_text())['GameParameters']
    return [{'hero':7,'weapon':'BubbleGirl','field':'referenceFootDepth',
             'after':data['NearestParam']['DrawSizeCollisionPaintParam']['PaintDepthScale'],
             'before':1.0,'sourceField':'NearestParam.DrawSizeCollisionPaintParam.PaintDepthScale',
             'originalValue':1.2,'conversionFactor':1,'evidenceLevel':'source-explicit',
             'reference':url(6),'reason':'Footprint depth was hard-coded to 1; import the explicit dimensionless nearest-paint depth.',
             'targetValidated':False}]

def verify_cached_sources():
    existing=json.loads((ROOT/'Tools/ValidationData/WeaponAlignment/Tuning.json').read_text(encoding='utf-8'))['sources']
    explosher=json.loads((ROOT/'Tools/ValidationData/Explosher/Source.json').read_text(encoding='utf-8'))
    mini=json.loads((ROOT/'Tools/ValidationData/MainWeaponReplacement/Tuning.json').read_text(encoding='utf-8'))['weapons']['6']
    for i in range(7):
        expected=explosher if i==2 else mini if i==5 else existing[str(i+1)]
        if hashlib.sha256(source(i).read_bytes()).hexdigest()!=expected['sha256']:
            raise ValueError('Cached source hash mismatch: '+REFS[i])

def audit(online=False):
    verify_cached_sources()
    OUT.mkdir(parents=True,exist_ok=True)
    remote={}
    if online:
        def fetch(i):
            raw=urllib.request.urlopen(url(i), timeout=40).read()
            if hashlib.sha256(raw).digest()!=hashlib.sha256(source(i).read_bytes()).digest():
                raise ValueError('Pinned source mismatch: '+REFS[i])
            return str(i+1),{'url':url(i),'sha256':hashlib.sha256(raw).hexdigest(),'verified':True}
        with ThreadPoolExecutor(max_workers=4) as pool: remote=dict(pool.map(fetch,range(7)))
        write_json(OUT/'sources-online.json',remote)
    ledger=json.loads((ROOT/'Tools/ValidationData/WeaponAlignment/Tuning.json').read_text(encoding='utf-8'))['fields']
    mini=json.loads((ROOT/'Tools/ValidationData/MainWeaponReplacement/Tuning.json').read_text(encoding='utf-8'))['weapons']['6']
    ledger=[row for row in ledger if row['hero'] != 6] + [dict(hero=6,field=key,**entry) for key,entry in mini['fields'].items()]
    result=[]
    for i,name in enumerate(NAMES):
        asset=ROOT/f'Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset'
        config=values(asset);raw=json.loads(source(i).read_text())['GameParameters']
        paint=[{'path':k,'value':v} for k,v in leaves(raw) if any(x in k.lower() for x in ('paint','splash','walldrop','nearest')) and not k.endswith('$type')]
        mappings=[x for x in ledger if x['hero']==i+1 and any(t in x['field'].lower() for t in ('paint','trail','foot','walldrop','bounce'))]
        if i==2:
            mappings=[{'field':k,'current':v,'source':'Tools/ValidationData/Explosher/Tuning.json'} for k,v in config.items() if any(t in k.lower() for t in ('paint','trail','foot','walldrop'))]
            raw_leaves=dict(leaves(raw))
            for m in mappings:
                mapping=EXPLOSHER_PAINT_MAP.get(m['field'])
                if mapping:
                    path,factor=mapping
                    m.update(sourceField=path,originalValue=raw_leaves[path],conversionFactor=factor,
                             sourceConvertedValue=raw_leaves[path]*factor,evidenceLevel='source-value; phase semantics pending')
                else:
                    m.update(sourceField=None,originalValue=None,conversionFactor=None,evidenceLevel='historical adaptation or unresolved default')
        for m in mappings: m['current']=config.get(m['field'],'<asset default>')
        for m in mappings:
            m['auditStatus']='historical mapping retained; algorithm semantics not independently established'
            m['missingDefaultPolicy']='Absent source fields are unresolved; current project defaults are not original-game evidence.'
        mappings += [dict(c,current=config.get(c['field']),auditStatus='explicit source value verified; original effect pending') for c in corrections() if c['hero']==i+1]
        result.append({'hero':i+1,'weapon':name,'source':url(i),'sha256':hashlib.sha256(source(i).read_bytes()).hexdigest(),
                       'paintSourceFields':paint,'currentMappings':mappings,
                       'parameterStatus':'explicit mapped values audited; missing defaults and mechanism semantics pending',
                       'targetValidated':False,'qualifiedOriginalSamples':0,
                       'unresolved':MECHANISM_GAPS[i]+['Exact cyclic drop phase / fractional allocation and reset rules',
                                     'Foot scheduling and nearest spawn offsets',
                                     'Missing inherited paint defaults; impact phase/angle interpolation',
                                     'Falling droplet and multi-stage wall drip behavior',
                                     'Original paint mask area and calibrated 11.3.0 captures']})
    write_json(OUT/'evidence.json',{'version':'11.3.0','scale':S,
        'units':{'sourceLengthToProjectMetres':S,'sourceFramesToSeconds':1/60,'sourceUnitsPerFrameToMetresPerSecond':60*S,
                 'depthScale':'dimensionless; no spatial conversion','gridCellMetres':.125},
        'defaultPolicy':'Absent, inherited or semantically ambiguous source fields remain unresolved, never inferred as zero.',
        'weapons':result,'verifiedCorrections':corrections()})
    print(f'Audited {len(result)} weapons; qualified original captures: 0; reviewed corrections: {len(corrections())}')

def apply():
    verify_cached_sources()
    for c in corrections():
        p=ROOT/f"Assets/GameResource/Weapons/{c['weapon']}/{c['weapon']}WeaponConfig.asset"
        text=p.read_text(encoding='utf-8');key=c['field'];line=f"  {key}: {c['after']}"
        text,n=re.subn(r'^  '+re.escape(key)+r': [^\n]*',line,text,flags=re.M)
        if not n: text=text.rstrip()+'\n'+line+'\n'
        p.write_text(text,encoding='utf-8')
    write_json(ROOT/'Tools/ValidationData/PaintParity1130/VerifiedOverrides.json',{'version':'11.3.0','scale':S,'fields':corrections()})
    print('Applied only reviewed paint overrides; combat values, hero workbook and Explosher are untouched.')

def report():
    stats=[]
    metrics=('ownedArea','floorArea','ownedWidth','ownedDepth','maxOwnedForward','connectedReach','longestCenterlineGap','firstPaintSeconds','paintStamps','paintPayloadBytes','simulationMilliseconds','inkSpent','inkGenerated','areaAtWindowEnd')
    for stage in ('Before','After'):
        for folder in sorted((OUT/stage).glob('w*/*')):
            files=sorted(folder.glob('seed-*.json'))
            if not files:continue
            samples=[json.loads(f.read_text()) for f in files]
            if len(samples)!=30:raise ValueError('Incomplete seed cohort: '+str(folder))
            item={'stage':stage,'hero':samples[0]['weapon'],'case':folder.name,'n':len(samples),'targetValidated':False,'resourceMode':samples[0]['resourceMode']}
            for key in metrics:
                vals=[s[key] for s in samples if key in s and (key!='firstPaintSeconds' or s[key]>=0)]
                item[key]={'mean':statistics.mean(vals),'sd':statistics.pstdev(vals),'min':min(vals),'max':max(vals)} if vals else None
            item['worstSamples']={}
            for key,mode in (('floorArea',min),('connectedReach',min),('longestCenterlineGap',max),('firstPaintSeconds',max)):
                valid=[(f,s) for f,s in zip(files,samples) if key!='firstPaintSeconds' or s[key]>=0]
                if valid:
                    f,s=mode(valid,key=lambda pair:pair[1][key])
                    item['worstSamples'][key]={'file':str(f.relative_to(OUT)),'seed':s['sampleSeed'],'value':s[key]}
            stats.append(item)
    write_json(OUT/'statistics.json',stats)
    comparison=[]
    for hero in range(1,8):
        changed=0;total=0
        for before in (OUT/'Before'/f'w{hero}').glob('*/seed-*.json'):
            after=OUT/'After'/before.relative_to(OUT/'Before')
            if not after.exists():raise ValueError('Missing paired sample: '+str(after))
            a=json.loads(before.read_text());b=json.loads(after.read_text());total+=1
            changed+=a['gridHash']!=b['gridHash']
            for key in ('emittedProjectiles','inkSpent','inkGenerated','paintStamps','paintPayloadBytes'):
                if a[key]!=b[key]:raise ValueError(f'Unexpected production change {hero}/{before.parent.name}/{key}')
        comparison.append({'hero':hero,'pairedSamples':total,'changedOwnershipHashes':changed,'projectilesInkStampCountAndPayloadUnchanged':True})
    write_json(OUT/'comparison.json',comparison)
    with (OUT/'statistics.csv').open('w',encoding='utf-8-sig',newline='') as f:
        fields=['stage','hero','case','n','resourceMode']+[f'{m}_{suffix}' for m in metrics for suffix in ('mean','sd','min','max')]
        writer=csv.DictWriter(f,fieldnames=fields);writer.writeheader()
        for s in stats:writer.writerow({**{k:s[k] for k in fields[:5]},**{f'{m}_{suffix}':s[m][suffix] if s[m] else '' for m in metrics for suffix in ('mean','sd','min','max')}})
    print('Aggregated',len(stats),'cohorts')
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    import numpy as np
    from PIL import Image
    for case in ('flat','three','three-seconds'):
        fig,axes=plt.subplots(7,2,figsize=(10,22),layout='constrained')
        for i,name in enumerate(NAMES):
            for j,stage in enumerate(('Before','After')):
                folder=OUT/stage/f'w{i+1}'/case
                paths=list(folder.glob('*.coverage.png'))
                if paths:
                    # Plot original measurement raster; no synthesized ground-truth image.
                    raster=np.asarray(Image.open(paths[0]))[::-1].transpose(1,0,2)
                    axes[i,j].imshow(raster,origin='lower',extent=(-10,70,-8,8),aspect='equal',interpolation='nearest')
                    axes[i,j].set_xlim(-2,26)
                axes[i,j].set_title(f'{name} / {stage}',fontsize=9)
                axes[i,j].set_xlabel('forward (project m)');axes[i,j].set_ylabel('lateral (project m)')
        fig.suptitle(f'Project ownership, seed 0: {case}\nBefore/After code change, NOT original Splatoon comparison')
        fig.savefig(OUT/f'coverage-{case}.png',dpi=120);plt.close(fig)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--verify-online',action='store_true');parser.add_argument('--apply',action='store_true');parser.add_argument('--report',action='store_true');args=parser.parse_args()
    if args.apply: apply()
    audit(args.verify_online)
    if args.report: report()
