"""Build review artifacts from Unity Physics/ownership captures, never synthetic game acceptance."""
from pathlib import Path
import csv, json, re, shutil, xml.etree.ElementTree as ET
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from PIL import Image

ROOT=Path(__file__).resolve().parents[2]
DATA=ROOT/'Tools/ValidationData/WeaponAlignment'
REPORT=ROOT/'Reports/WeaponAlignment'
OUT=ROOT/'Docs/WeaponAlignment1130'
NAMES=['RifleGirl','DualPistolGirl','ShotgunGirl','PistolGirl','RocketLauncherGirl','MachineGunGirl','BubbleGirl']
CN=['斯普拉射击枪','开尔文525（普通射击）','自定义八弹丸霰弹枪','窄域标记枪','普通快速爆破枪','消防栓旋转枪','满溢泡泡枪']

def load(path):return json.loads(path.read_text('utf-8-sig'))
def filename(hero,scenario='flat'):return f'w{hero}-q{60 if hero==6 else 0}-{scenario}-60'
def assets(path):
    result={}
    for key,value in re.findall(r'^  ([a-z]\w*): (.+)$',path.read_text('utf-8-sig'),re.M):
        if key.startswith('m_') or key=='ammoConfig':continue
        try:result[key]=json.loads(value)
        except ValueError:result[key]=value
    return result

def main():
    OUT.mkdir(exist_ok=True)
    before=load(DATA/'Baseline/Values.json');after=[]
    heroes=load(ROOT/'Assets/GameResource/Bootstrap/Config/Luban/tbhero.json')
    for row in heroes:after.append(dict(row,**assets(ROOT/row['weaponConfigPath'])))
    tuning=load(DATA/'Tuning.json');known={(r['hero'],r['field']):r for r in tuning['fields']}
    allfields=[]
    for b,a in zip(before,after):
        for key in sorted(set(a)|set(b)):
            evidence=known.get((a['id'],key),{})
            allfields.append(dict(hero=a['id'],weapon=NAMES[a['id']-1],field=key,before=b.get(key),after=a.get(key),originalValue=evidence.get('originalValue'),evidenceLevel=evidence.get('evidenceLevel','retained-project-value'),sourceField=evidence.get('sourceField'),reference=evidence.get('reference')))
    with (OUT/'AllFields.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=allfields[0].keys());writer.writeheader();writer.writerows(allfields)
    shutil.copy2(DATA/'Tuning.json',OUT/'Tuning.json')
    rows=[]
    for folder,label in [('BeforeReplayed','before'),('After/Direct','after-direct'),('After/Actions','after-actions')]:
        for file in sorted((REPORT/folder).glob('*.json')):
            value=load(file);value['captureSet']=label;rows.append(value)
    with (OUT/'Measurements.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(dict.fromkeys(k for r in rows for k in r)));writer.writeheader();writer.writerows(rows)
    plt.rcParams.update({'font.family':'DejaVu Sans','font.size':9,'axes.spines.top':False,'axes.spines.right':False})
    fig,axes=plt.subplots(4,2,figsize=(12,12),constrained_layout=True)
    for hero,ax in enumerate(axes.flat,1):
        if hero>7:ax.axis('off');continue
        for directory,color,label in [('BeforeReplayed','#8993a5','Before'),('After/Direct','#e4318d','After')]:
            trace=np.genfromtxt(REPORT/directory/(filename(hero)+'.trace.csv'),delimiter=',',names=True)
            trace=np.atleast_1d(trace);trace=trace[trace['shot']==trace['shot'].min()]
            ax.plot(trace['z'],trace['y'],color=color,label=label,lw=1.8)
        ax.axhline(0,c='#cad0d7',lw=.6);ax.set(title=NAMES[hero-1],xlabel='Forward distance (m)',ylabel='Height (m)');ax.legend()
    fig.suptitle('Measured trajectories | Same logical muzzle and aim | First projectile, no target player',fontsize=13)
    fig.savefig(OUT/'Trajectories.png',dpi=160);plt.close(fig)
    for scenario,title,suffix in [('flat','One direct Spawn (8 pellets for Shotgun)','Single'),('continuous','20 direct Spawns; fixed aim','Continuous')]:
        fig,axes=plt.subplots(7,2,figsize=(13,13),constrained_layout=True)
        for hero in range(1,8):
            for col,directory in enumerate(['BeforeReplayed','After/Direct']):
                ax=axes[hero-1,col];path=REPORT/directory/(filename(hero,scenario)+'.coverage.png')
                pixels=np.asarray(Image.open(path))
                # Unity EncodeToPNG stores top row first; world z runs from -10 to 70.
                # Transpose into a long horizontal lane: forward on X, lateral on Y.
                ax.imshow(np.transpose(pixels,(1,0,2))[:,::-1],extent=(-10,70,8,-8),origin='upper',interpolation='nearest',aspect='equal')
                ax.set_xlim(-2,26);ax.set_ylim(-4,4);ax.set_ylabel(NAMES[hero-1]);ax.set_xlabel('Forward (m)')
                if hero==1:ax.set_title('Before' if col==0 else 'After')
        fig.suptitle(title+' | Actual 0.125 m ownership grid; pink = owned',fontsize=13)
        fig.savefig(OUT/f'Coverage-{suffix}.png',dpi=150);plt.close(fig)
    fig,axes=plt.subplots(7,1,figsize=(11,12),constrained_layout=True)
    for hero,ax in enumerate(axes,1):
        pixels=np.asarray(Image.open(REPORT/'After/Actions'/(filename(hero,'sweep')+'.coverage.png')))
        ax.imshow(np.transpose(pixels,(1,0,2))[:,::-1],extent=(-10,70,8,-8),origin='upper',interpolation='nearest',aspect='equal')
        ax.set_xlim(-2,26);ax.set_ylim(-7,7);ax.set_ylabel(NAMES[hero-1]);ax.set_xlabel('Forward (m)')
    fig.suptitle('Complete actions with aim sweep | 20 actions; Hydra 2 full magazines',fontsize=13)
    fig.savefig(OUT/'Coverage-Actions.png',dpi=150);plt.close(fig)
    def measurement(h,scenario='flat',kind='after-actions'):
        return next(r for r in rows if r['weapon']==h and r['scenario']==scenario and r['captureSet']==kind and r['charge']==(1 if h==6 else 0))
    params=['| 角色／对标 | 射击节奏 | 伤害 | 耗墨 | 行走／友方墨中游泳 |','|---|---|---|---|---|']
    for h,a in enumerate(after,1):
        cadence=f"{a['fireRate']:.4g} 次/秒" if h!=7 else '32 帧/组，5 帧/颗'
        damage=f"{a['damage']:g}→{a['damageMin']:g}" if h!=6 else '未满蓄32／满蓄40→16'
        ink=f"{a['shotInk']:.4g}" if h!=6 else '满蓄35／66发'
        params.append(f"| {NAMES[h-1]}／{CN[h-1]} | {cadence} | {damage} | {ink} | {a['moveSpeed']:.5f}／{a['swimSpeed']:.5f} m/s |")
    metric=['| 武器 | 单次动作弹丸数 | 面积 m² | 宽度 m | 纵深 m | 连通距离 m | 面积/100墨 m² |','|---|---:|---:|---:|---:|---:|---:|']
    comparison=['| 武器 | 单次直接发射面积：修改前→后 | 20次直接发射面积：修改前→后 |','|---|---:|---:|']
    for h in range(1,8):
        m=measurement(h);metric.append(f"| {CN[h-1]} | {m['emittedProjectiles']} | {m['ownedArea']:.2f} | {m['ownedWidth']:.3f} | {m['ownedDepth']:.3f} | {m['connectedReach']:.3f} | {m['ownedAreaPer100Ink']:.2f} |")
        values=[]
        for scenario in ['flat','continuous']:
            b=measurement(h,scenario,'before');a=measurement(h,scenario,'after-direct');values.append(f"{b['ownedArea']:.2f}→{a['ownedArea']:.2f} m²")
        comparison.append(f"| {CN[h-1]} | {' | '.join(values)} |")
    final=REPORT/'alignment-final.xml';validation='最终回归运行中。'
    if final.exists():
        xml=ET.parse(final).getroot();validation=f"Unity 最终回归：{xml.get('passed')} 通过，{xml.get('failed')} 失败，{xml.get('skipped')} 跳过。"
        shutil.copy2(final,OUT/'Unity-Final.xml')
    for name in ['baseline.xml','workbook-validation.json','alignment-movement-play.xml','alignment-spinner-tank.xml']:
        if (REPORT/name).exists():shutil.copy2(REPORT/name,OUT/name)
    notes=REPORT/'After/Actions/spinner-tank-notes.txt'
    if notes.exists():shutil.copy2(notes,OUT/'Spinner-Tank.txt')
    text='''# 喷3 11.3.0 武器对齐：实现与验收记录

基准：无技能、100生命、100墨量；S=18/24.037。RifleGirl 对标斯普拉射击枪，双枪仅普通射击，霰弹枪保留冻结资产。所有时间字段以秒保存。角色、美术图集、副武器、特殊武器、装备技能不在本轮重做范围。

## 最终配置

'''+ '\n'.join(params)+'''

步枪由15降至10发/秒、耗墨0.85→0.92；手枪伤害衰减起点10→4参考帧；消防栓第一圈1.5→2秒；泡泡30→32伤害、33→32帧组周期、3→5帧颗间隔。消防栓第一圈33发、满蓄66发；爆破碰撞涂墨半径0.97563→1.72234米。整箱墨实发108次步枪、125次手枪、71次双枪、14次爆破、12组泡泡；霰弹枪25次齐射。

[全部字段前后表](AllFields.csv) 包含保留值；[导入依据](Tuning.json) 区分 source-explicit、reference-simulator-default、project-adaptation，保留原始数字、转换值、字段路径和固定提交链接。retained-project-value 表示保留项目值，不能视为已证实原作值。代码默认字段也纳入不可变快照和内容签名。

## 实现

- 五种射击武器统一直进、4参考帧制动、自由飞行；60Hz离散参考积分，帧间线性取样。权威、展示、准星入口共用 `InkBallistics/ReferenceBallistics`。泡泡使用单独的阻力、重力与逐颗初速，并同步颗序和反弹段。
- 连续扫掠区分场景／玩家半径；移除参考武器以展示射程硬截断伤害的路径。界面分别显示直进、伤害衰减区间、标准1.4米枪口的平地落点和几何涂墨上界；上界不是实际占有面积。
- 步枪、消防栓和爆破枪使用逐发偏置、停火恢复、跳跃年龄恢复；消防栓水平／垂直采样分开，蓄力不增散布。双枪保留普通射击规则，手枪保持零散布。
- 命中、沿途、脚下、爆炸、弹跳分别生成。沿途墨滴实际下落碰撞，不生成伤害或独立网络对象。方向和纵深统一作用于CPU归属与GPU图集；爆炸多落点按8方向局部遮挡裁剪，墙面使用分段下落覆盖。
- 发射冻结武器快照，调试房新弹丸采用新值，普通联机房配置固定。玩家协议30、武器模拟10、涂墨协议9，内容不匹配拒绝加入。PaintStamp由53增至102字节（序列化负载，不含传输开销）；重同步按实际序列化长度分块，不能宣称带宽减少。
- 服务按60Hz稳定排序推进，并补算迟到的新弹丸；涂墨待处理数量与战斗弹丸数量分开。随机种子不依赖全局弹丸ID；霰弹图集袋保留每位射手的历史顺序。

## 项目实测

真实 Unity Physics、逻辑枪口、0.125米归属网格；水平平地、固定种子。原始修改前数据在任何修改前冻结；旧覆盖图由冻结参数重放，并逐项校验与冻结哈希一致。直接发射用于前后可比；完整动作包含泡泡四颗和消防栓整弹仓。墙面／高台、上下30°、遮挡、连续、整箱墨、往返横移和摆动瞄准数据见 [测量表](Measurements.csv)。横移为±5米正弦路径，不代表恒定速度扫射的原作样本。

'''+ '\n'.join(comparison)+ '\n\n'+ '\n'.join(metric)+'''

上表“每100墨面积”由该次动作的面积和耗墨归一化；实际整箱墨测试以 `scenario=tank` 单独记录，不能将单次归一化当成连射并集。固定位置会大量重叠。脚下4邻接连通距离可以远小于最远落墨距离，不通过加大墨迹掩盖断路。消防栓单次动作指满蓄66发。

![弹道前后](Trajectories.png)
![单次直接发射覆盖](Coverage-Single.png)
![20次直接发射覆盖](Coverage-Continuous.png)
![完整动作摆动扫射](Coverage-Actions.png)

## 验证边界

'''+validation+'''

已执行：源表15个预期单元格及样式检查、Luban生成、源表/生成/资产一致性；Unity编译、真实Physics测量、独立逐帧积分≤0.1毫米、30/60/144Hz外部调用一致性、完整泡泡组/消防栓连射、七武器移动消费者、CPU/GPU方向性与裁剪一致性、快照/墨迹序列化与重同步、Editor Host 实射/换枪/死亡重生/重置及调试热更新测试。帧率项是模拟调用频率，不是设备渲染性能。

三个显式入口（首次冻结、旧参数渲染、批量测量）在整组回归中跳过；旧参数重放和批量测量已单独执行并通过，首次冻结入口禁止覆盖。消防栓补充整箱测量从100墨开始执行3次蓄力释放，使用现有缺墨慢充补给；详见附带说明。

未执行本次版本的独立客户端进程、物理双机、目标设备及原作11.3.0采样；旧客户端构建不能证明新协议通过。本轮未发布、未构建分发包。Editor Host与单进程序列化测试不能替代独立客户端验收。所有测量 `targetValidated=false`；原作涂墨面积、宽度、整箱墨效率±10%和连通距离阈值均待合格原作样本验收。

## 剩余适配

1. 参考默认制动／自由飞行参数来自固定 sendou 模拟版本，是参考模拟默认值；并非已获取 Nintendo 完整实现。
2. 小数预算用稳定Bernoulli概率，沿途按累计路程和随机首相位生成。脚下墨独立调度；原始SplitNum只作为来源记录，未解释成每N发脚下墨。
3. 随机采样采用项目中心偏置分布；命中纵深按距离/落差插值。子墨滴仅垂直下落，墙滴按定速分段；这些仍是近似。
4. 泡泡回墨锁定按最后一颗后40帧；未确证的起手保留。弹跳次数3/总反弹6、保速率0.72/0.9/0.9、寿命2.4秒、累计路程24米、反弹缩放1保留项目规则。场景/玩家成长4/5帧，扫掠使用步末半径形成保守碰撞包络。友方穿透沿用项目规则。
5. 原作缺省距离、未公开反弹细节、脚下调度及墙面覆盖需结合原作样本继续校准。消防栓缺墨时沿用已有慢速蓄力补给机制；100墨效率不能解释成该机制下的有限总发数。

## 复现

`python Tools/CombatGirls/align_reference_weapons.py --apply` → `cmd /c Config\\Luban\\gen_luban.bat` → `python Tools/CombatGirls/validate_weapon_assets.py`。首次冻结数据不覆盖。

Unity测试入口：`WeaponAlignmentTests`、`WeaponAlignmentCoverageTests`、已有各武器和联机测试；测量入口 `CaptureAfter`、`RenderFrozenBaseline` 为 Explicit，需按名称执行。`CaptureBaseline` 仅供改动前首次冻结，不应在当前代码执行。`WeaponReferenceMeasurementTests.CaptureAllSevenWeaponsAndCompleteActionsWithRealPhysicsAndOwnership` 必须先于对应PlayMode比较执行。

`python Tools/CombatGirls/report_weapon_alignment.py` 从测量文件生成本报告与图表。报告不修改武器配置。

来源：[Leanny 固定11.3.0提交](https://github.com/Leanny/splat3/tree/7280ff9cde8bb1c5dcef46c700c326471584d2e6/data/parameter/1130/weapon)、[sendou 固定模拟版本](https://github.com/sendou-ink/sendou.ink/blob/55889eccc7c18f24569098959ae28f4a7034ce4e/app/features/comp-analyzer/core/weapon-range.ts)。源文件及SHA256随导入记录保存。
'''
    (OUT/'Report.md').write_text(text,'utf-8')
    print(json.dumps({'output':str(OUT),'fields':len(allfields),'measurements':len(rows),'validation':validation},ensure_ascii=False))

if __name__=='__main__':main()
