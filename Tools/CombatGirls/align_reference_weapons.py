"""Apply the approved 11.3.0 profile. Weapon assets and TbHero.xlsx are the sources.

Reference JSON is pinned and cached; baseline files are never overwritten. The
project approximation fields below are intentional, not decoded Nintendo defaults.
"""
from pathlib import Path
import argparse, csv, hashlib, json, re, urllib.request

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'Tools/ValidationData/WeaponAlignment'
S = 18 / 24.037
COMMIT = '7280ff9cde8bb1c5dcef46c700c326471584d2e6'
NAMES = ['RifleGirl','DualPistolGirl','ShotgunGirl','PistolGirl','RocketLauncherGirl','MachineGunGirl','BubbleGirl']
REFERENCES = {1:'WeaponShooterNormal',2:'WeaponManeuverGallon',4:'WeaponShooterPrecision',5:'WeaponBlasterLight',6:'WeaponSpinnerHyper',7:'WeaponSlosherBathtub'}

def values(text):
    return {k:v for k,v in re.findall(r'^  (\w+): ([^\n]+)',text,re.M)}

def enrich(ledger, refs):
    """Attach original numeric evidence without pretending an adaptation is source code."""
    bubble_paths = {
        'damage':'Unit.DamageParam.ValueMax','damageMin':'Unit.DamageParam.ValueMin',
        'paintRadiusMin':'Unit.PaintParam.WidthHalfFar','paintRadiusMax':'Unit.PaintParam.WidthHalfNear',
        'paintDistanceMiddle':'Unit.PaintParam.DistanceXZNear','paintDistanceFar':'Unit.PaintParam.DistanceXZFar',
        'paintDepthMin':'Unit.PaintParam.DepthScaleFar','paintDepthMax':'Unit.PaintParam.DepthScaleNear',
        'paintDepthBreakMin':'Unit.PaintParam.DepthScaleFar','paintDepthBreakMax':'Unit.PaintParam.DepthScaleNear',
        'bubbleFirstBouncePaintRadius':'BounceGroupParam.BounceParam.0.PaintRadiusFirstBounce',
        'bubbleLaterBouncePaintRadius':'BounceGroupParam.BounceParam.1.PaintRadiusFirstBounce',
        'bubbleBouncePaintDecrement':'BounceGroupParam.BounceParam.1.AfterOffsetPaintRadiusFirstBnce',
        'bubbleLaterImpactRadius':'Unit.PaintParam.WidthHalfNear',
        'referenceFootRadius':'NearestParam.DrawSizeCollisionPaintParam.PaintWidthHalf',
        'trailRadiusMin':'Unit.SplashParam.0.DrawSizeCollisionPaintParam.PaintWidthHalf',
        'trailRadiusMax':'Unit.SplashParam.0.DrawSizeCollisionPaintParam.PaintWidthHalf',
        'trailSpacing':'Unit.SplashParam.0.SpawnParam.SpawnBetweenLength',
        'referenceTrailStart':'Unit.SplashParam.0.SpawnParam.SpawnBetweenLength',
        'trailDepthScale':'Unit.SplashParam.0.DrawSizeCollisionPaintParam.PaintDepthScale',
        'referenceTrailBudget':'Unit.SplashAndSplashWallHitSpawnPrm.Combination.0.TotalNum',
        'bubbleRadiusDecrement':'Unit.CollisionParam.AfterOffsetEndRadiusForField',
    }
    for row in ledger:
        hero,key,origin=row['hero'],row['field'],row['evidence']
        match=re.search(r'(?:\w+\.)+\w+',origin)
        path=bubble_paths.get(key) if hero==7 else None
        path=path or (match.group(0) if match else None)
        raw=None
        if path and hero in refs:
            current=refs[hero]
            try:
                for part in path.split('.'):
                    if part=='Unit':current=current['UnitGroupParam']['Unit'][1 if ('Later' in key or key=='bubbleRadiusDecrement') else 0]
                    else:current=current[int(part)] if isinstance(current,list) else current[part]
                if isinstance(current,(float,int)):raw=current
            except (KeyError,IndexError,TypeError,ValueError):pass
        if key in ('moveSpeed','swimSpeed'):
            raw=(.104 if hero==4 else .088 if hero==6 else .096) if key=='moveSpeed' else (.2016 if hero==4 else .1728 if hero==6 else .192)
        level='source-explicit' if raw is not None else 'project-adaptation'
        if raw is None and 'sendou default' in origin:
            level='reference-simulator-default'
            raw={'referenceBrakeDrag':.36,'referenceBrakeGravity':.07,'referenceFreeDrag':0,'projectileGravity':.016,'brakeSeconds':4}[key]
        row['originalValue']=raw
        row['sourceField']=path
        row['evidenceLevel']=level
        row['conversionFactor']=row['after']/raw if raw not in (None,0) else None


def main():
    args=argparse.ArgumentParser();args.add_argument('--apply',action='store_true');args=args.parse_args()
    refs={};sources={}
    for hero,name in REFERENCES.items():
        path=DATA/(name+'.1130.json')
        url=f'https://raw.githubusercontent.com/Leanny/splat3/{COMMIT}/data/parameter/1130/weapon/{name}.game__GameParameterTable.json'
        raw=path.read_bytes() if path.exists() else urllib.request.urlopen(url).read()
        if args.apply: path.write_bytes(raw)
        refs[hero]=json.loads(raw)['GameParameters'];sources[hero]={'url':url,'sha256':hashlib.sha256(raw).hexdigest()}
    ledger=[]
    for hero,name in enumerate(NAMES,1):
        if hero==3: continue
        path=ROOT/f'Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset'
        text=path.read_text(encoding='utf-8-sig');before=values((DATA/'Baseline'/path.name).read_text(encoding='utf-8-sig'))
        ref=refs[hero]; wp=ref['WeaponParam']; updates={}; origins={}
        def setv(key,val,source='project-adaptation'):
            updates[key]=val;origins[key]=source
        def explicit(key,val,source):setv(key,val,source)
        setv('referenceRules',1)
        explicit('shootMoveSpeed',wp['MoveSpeed']*60*S,'WeaponParam.MoveSpeed * 60S')
        setv('referenceBrakeDrag',.36,'sendou default per frame')
        setv('referenceBrakeGravity',.07*3600*S,'sendou default * 3600S')
        setv('referenceFreeDrag',0,'sendou default per frame')
        setv('projectileGravity',.016*3600*S,'MoveParam.FreeGravity / sendou default * 3600S')
        setv('brakeSeconds',4/60,'sendou default / 60')
        setv('paintDropGravity',.016*3600*S)
        setv('paintDropLifetime',5)
        setv('referenceFootEvery',5 if hero==2 else 1)
        if hero != 7:
            mp=ref['MoveParam']; pp=ref['PaintParam']; sp=ref['SplashSpawnParam']; splash=ref['SplashPaintParam']
            explicit('referenceBrakeEndSpeed',mp['GoStraightStateEndMaxSpeed']*60*S,'MoveParam.GoStraightStateEndMaxSpeed * 60S')
            explicit('straightSeconds',mp['GoStraightToBrakeStateFrame']/60,'MoveParam.GoStraightToBrakeStateFrame / 60')
            explicit('referencePlayerRadius',ref['CollisionParam']['EndRadiusForPlayer']*S,'CollisionParam.EndRadiusForPlayer * S')
            explicit('collisionRadius',ref['CollisionParam']['EndRadiusForField']*S,'CollisionParam.EndRadiusForField * S')
            if hero in (1,4):
                setv('motionMode',4)
                for key in ('speedMin','speedMax'):explicit(key,mp['SpawnSpeed']*60*S,'MoveParam.SpawnSpeed * 60S')
            if hero != 5:
                for key,rawkey in [('paintRadiusMin','WidthHalfFar'),('paintRadiusMax','WidthHalfNear')]:explicit(key,pp[rawkey]*S,'PaintParam.'+rawkey+' * S')
                for key,rawkey in [('paintDepthMin','DepthScaleMin'),('paintDepthMax','DepthScaleMax'),('paintDepthBreakMin','DepthScaleMinBreakFree'),('paintDepthBreakMax','DepthScaleMaxBreakFree')]:explicit(key,pp[rawkey],'PaintParam.'+rawkey)
            explicit('paintDistanceMiddle',pp['DistanceMiddle']*S,'PaintParam.DistanceMiddle * S')
            setv('paintDistanceFar',pp.get('DistanceFar',float(before['effectiveRange'])/S)*S,'PaintParam.DistanceFar * S' if 'DistanceFar' in pp else 'project reference range')
            explicit('trailRadiusMin',splash['WidthHalf']*S,'SplashPaintParam.WidthHalf * S');setv('trailRadiusMax',updates['trailRadiusMin'],origins['trailRadiusMin'])
            explicit('referenceTrailBudget',sp['SpawnNum'],'SplashSpawnParam.SpawnNum; fractional count is project Bernoulli approximation')
            explicit('trailSpacing',sp['SpawnBetweenLength']*S,'SplashSpawnParam.SpawnBetweenLength * S')
            explicit('referenceTrailStart',sp['SpawnNearestLength']*S,'SplashSpawnParam.SpawnNearestLength * S')
            setv('referenceTrailRandomPhase',int(hero in (1,4,6)))
            explicit('referenceFootRadius',0 if hero==5 else splash['WidthHalfNearest']*S,'SplashPaintParam.WidthHalfNearest * S; schedule is adaptation')
            setv('trailDepthScale',splash.get('DepthScaleMax',1),'SplashPaintParam.DepthScaleMax' if 'DepthScaleMax' in splash else 'project-adaptation')
            if hero != 5:
                wall=ref['WallDropCollisionPaintParam'];move=ref['WallDropMoveParam']
                explicit('wallDropRadius',wall['PaintRadiusFall']*S,'WallDropCollisionPaintParam.PaintRadiusFall * S')
                explicit('wallDropGroundRadius',wall['PaintRadiusGround']*S,'WallDropCollisionPaintParam.PaintRadiusGround * S')
                explicit('wallDropSpeed',move['FallPeriodFirstTargetSpeed']*60*S,'WallDropMoveParam.FallPeriodFirstTargetSpeed * 60S')
                setv('wallDropSeconds',(30+move['FallPeriodSecondFrame']+25)/60)
        if hero==1:
            setv('fireRate',10,'inherited reference RepeatFrame=6');explicit('shotInk',wp['InkConsume']*100,'WeaponParam.InkConsume * 100')
        if hero==4: explicit('damageReduceStartSeconds',4/60,'DamageParam.ReduceStartFrame / 60')
        if hero==6:
            setv('motionMode',4)
            explicit('splatlingFirstChargeSeconds',2,'WeaponParam.ChargeFrame_First / 60')
            explicit('splatlingChargeMoveSpeed',wp['MoveSpeed_Charge']*60*S,'WeaponParam.MoveSpeed_Charge * 60S')
            explicit('splatlingPitchSpread',wp['PitchDegSwerve'],'WeaponParam.PitchDegSwerve')
            explicit('referencePitchBias',wp['PitchDegBias'],'WeaponParam.PitchDegBias')
        if hero in (1,6,5):
            setv('referenceSpreadEnabled',1)
            for key,rawkey,default in [('referenceBiasMin','Stand_DegBiasMin',.01),('referenceBiasMax','Stand_DegBiasMax',.25),('referenceBiasPerShot','Stand_DegBiasKf',.02),('referenceJumpBias','Jump_DegBiasMax',.4)]:
                # Zero ground cone blaster still needs a valid nonzero sampling bias.
                setv(key,max(.00001,wp.get(rawkey,default)),'WeaponParam.'+rawkey if rawkey in wp and wp[rawkey]>0 else 'project-adaptation')
            setv('referenceBiasRecovery',wp.get('Stand_DegBiasDecrease',.005)*60,'WeaponParam.Stand_DegBiasDecrease * 60' if 'Stand_DegBiasDecrease' in wp else 'project-adaptation')
            for key,rawkey in [('spreadDegrees','Stand_DegSwerve'),('jumpSpreadDegrees','Jump_DegSwerve')]:explicit(key,wp[rawkey],'WeaponParam.'+rawkey)
            for key,rawkey in [('referenceJumpStart','Jump_DegBiasDecreaseStartFrame'),('referenceJumpEnd','Jump_DegBiasEndFrame')]:explicit(key,wp[rawkey]/60,'WeaponParam.'+rawkey+' / 60')
        if hero==5:
            explicit('collisionExplosionPaintRadius',ref['BlasterBurstParam']['SplashDropPaintShotColHitRadius']*S,'BlasterBurstParam.SplashDropPaintShotColHitRadius * S')
        if hero==7:
            first,later=ref['UnitGroupParam']['Unit'];mp=first['MoveParam']
            explicit('bubbleUpwardRate',first['AddSpawnSpeedYRateByXZ'],'Unit.AddSpawnSpeedYRateByXZ')
            explicit('bubbleInitialRadiusRate',first['CollisionParam']['InitRadiusForField']/first['CollisionParam']['EndRadiusForField'],'Unit.CollisionParam.InitRadiusForField / EndRadiusForField')
            explicit('bubbleFieldGrowSeconds',first['CollisionParam']['ChangeFrameForField']/60,'Unit.CollisionParam.ChangeFrameForField / 60')
            explicit('bubblePlayerGrowSeconds',first['CollisionParam']['ChangeFrameForPlayer']/60,'Unit.CollisionParam.ChangeFrameForPlayer / 60')
            for key,val,source in [('damage',32,'DamageParam.ValueMax / 10'),('damageMin',32,'DamageParam.ValueMin / 10'),('bubbleVolleySeconds',32/60,'WeaponParam.RepeatFrame / 60'),('bubbleIntervalSeconds',5/60,'UnitDelayFrame / 60'),('inkRecoverLockSeconds',40/60,'WeaponParam.InkRecoverStop / 60; origin last bubble is adaptation')]:explicit(key,val,source)
            setv('fireRate',60/32,'reciprocal group period')
            for key,obj,rawkey in [('speedMin',first,'SpawnSpeedGround'),('speedMax',first,'SpawnSpeedGround'),('bubbleAirSpeed',first,'SpawnSpeedAir'),('bubbleLaterSpeed',later,'SpawnSpeedGround'),('bubbleLaterAirSpeed',later,'SpawnSpeedAir')]:explicit(key,obj[rawkey]*60*S,'Unit.'+rawkey+' * 60S')
            explicit('bubbleSpeedDecrement',.01*60*S,'-Unit.AfterOffsetSpawnSpeed * 60S')
            for key,rawkey,factor in [('referenceBrakeEndSpeed','GoStraightStateEndMaxSpeed',60*S),('referenceBrakeDrag','BrakeAirResist',1),('referenceBrakeGravity','BrakeGravity',3600*S),('referenceFreeDrag','FreeAirResist',1),('projectileGravity','FreeGravity',3600*S),('straightSeconds','GoStraightToBrakeStateFrame',1/60),('brakeSeconds','BrakeToFreeStateFrame',1/60)]:explicit(key,mp[rawkey]*factor,'Unit.MoveParam.'+rawkey)
            for key,obj,rawkey in [('collisionRadius',first,'EndRadiusForField'),('referencePlayerRadius',first,'EndRadiusForPlayer'),('bubbleLaterFieldRadius',later,'EndRadiusForField'),('bubbleLaterPlayerRadius',later,'EndRadiusForPlayer')]:explicit(key,obj['CollisionParam'][rawkey]*S,'Unit.CollisionParam.'+rawkey+' * S')
            explicit('bubbleRadiusDecrement',.03*S,'-Unit.CollisionParam.AfterOffsetEndRadius * S')
            for key,val in [('paintRadiusMin',1.65),('paintRadiusMax',1.65),('bubbleFirstBouncePaintRadius',1.65),('bubbleLaterBouncePaintRadius',1.32),('bubbleBouncePaintDecrement',.05),('bubbleLaterImpactRadius',1.19),('trailRadiusMin',1.645),('trailRadiusMax',1.645),('trailSpacing',3),('referenceTrailStart',3),('referenceFootRadius',2.2),('paintDistanceMiddle',5),('paintDistanceFar',20)]:explicit(key,val*S,'Unit/Nearest/BounceGroup paint parameter * S')
            for key in ('paintDepthMin','paintDepthMax','paintDepthBreakMin','paintDepthBreakMax'):explicit(key,1.1,'Unit.PaintParam.DepthScale')
            setv('referenceTrailBudget',5,'Unit.SplashParam.SpawnNum; first bubble only')
            explicit('trailDepthScale',1.84,'Unit.SplashParam.PaintDepthScale')
        for key,value in updates.items():
            text,n=re.subn(r'^  '+re.escape(key)+r': [^\n]*',f'  {key}: {value}',text,flags=re.M)
            if not n:text+=f'  {key}: {value}\n'
            ledger.append({'hero':hero,'weapon':name,'field':key,'before':before.get(key,'<new field>'),'after':value,'evidence':origins[key],'reference':sources[hero]['url']})
        if args.apply:path.write_text(text,encoding='utf-8')
    if args.apply:
        # The bundled artifact-tool package is absent on this machine; narrow fallback.
        import openpyxl
        p=ROOT/'Config/Luban/source/TbHero.xlsx';book=openpyxl.load_workbook(p);sheet=book.active
        baseline_book=openpyxl.load_workbook(DATA/'Baseline/TbHero.xlsx');baseline_sheet=baseline_book.active
        header=next(row for row in sheet if any(c.value=='moveSpeed' for c in row));cols={c.value:c.column for c in header}
        for row in sheet.iter_rows(min_row=header[0].row+1):
            hero=row[cols['id']-1].value
            if hero not in range(1,8):continue
            for field,raw in [('moveSpeed',.104 if hero==4 else .088 if hero==6 else .096),('swimSpeed',.2016 if hero==4 else .1728 if hero==6 else .192)]:
                cell=sheet.cell(row[0].row,cols[field]);ledger.append({'hero':hero,'weapon':NAMES[hero-1],'field':field,'before':baseline_sheet.cell(row[0].row,cols[field]).value,'after':raw*60*S,'evidence':'Common.1130 no-gear movement * 60S','reference':'Tools/ValidationData/WeaponAudit/Common.1130.json'});cell.value=raw*60*S
            if hero==1:sheet.cell(row[0].row,cols['weaponTypeName']).value='斯普拉射击枪'
        book.save(p)
        enrich(ledger,refs)
        (DATA/'Tuning.json').write_text(json.dumps({'version':'11.3.0','scale':S,'sources':sources,'fields':ledger,'targetValidated':False},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        with (DATA/'Changes.csv').open('w',encoding='utf-8-sig',newline='') as f:
            writer=csv.DictWriter(f,fieldnames=ledger[0].keys());writer.writeheader();writer.writerows(ledger)
    print(json.dumps({'apply':args.apply,'fields':len(ledger),'scale':S}))

if __name__=='__main__':main()
