"""Real main-camera 1080p frames. Run serially to avoid GPU contention between baseline/new players."""
import argparse,json,subprocess,time,os,socket,shutil
from pathlib import Path

def run_player(exe,out,label,scenario,video=False):
    out.mkdir(parents=True,exist_ok=True)
    with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as s:
        s.bind(('127.0.0.1',0));port=s.getsockname()[1]
    args=[str(exe),'-force-d3d11','-screen-width','1920','-screen-height','1080','-screen-fullscreen','0',
          '-lanSmokeHost','-lanPort',str(port),'-inkSmokeCase','observer','-inkLabel',label,
          '-inkStaticScenario',scenario,'-logFile',str(out/(label+'.log'))]
    if video:args+=['-inkStaticOutput',str(out/label)]
    else:args+=['-frameProbe','60','-frameProbeWarmup','10','-frameProbePlayers','1','-frameProbeQuit',
                '-frameProbeOutput',str(out/(label+'.json'))]
    p=subprocess.Popen(args,cwd=exe.parent,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    try:code=p.wait(timeout=360)
    except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=15);raise
    if code:raise RuntimeError(f'{label} exit {code}; see {out/(label+".log")}')
    print(label+' completed',flush=True)
    if video:
        frames=sorted((out/label).glob('[0-9][0-9][0-9][0-9].png'))
        if len(frames)!=240 or not (out/label/'capture.txt').exists():raise RuntimeError('Incomplete consecutive-frame recording '+label)
    if not video:
        path=out/(label+'.json')
        if not path.exists():raise RuntimeError(f'{label} exited without performance results; see {out/(label+".log")}')
        data=json.loads(path.read_text(encoding='utf-8-sig'))
        if not data['complete'] or data['errors'] or data['renderedFrames']==0 or data['width']!=1920 or data['height']!=1080:raise RuntimeError('Invalid rendered frame evidence '+label)
        if scenario=='static' and data['paintStamps']!=0:raise RuntimeError('Static sample included fixture warmup '+label)
        if scenario=='paint' and data['paintStamps']<=0:raise RuntimeError('Continuous-paint sample did not paint '+label)
        return data

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--baseline',type=Path,required=True);p.add_argument('--new',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--video-only',action='store_true')
    p.add_argument('--reuse-baseline',type=Path,help='Existing verified baseline results and recording; baseline executable/fixture must be unchanged.')
    a=p.parse_args()
    out=a.output.resolve();results={}
    out.mkdir(parents=True,exist_ok=True)
    if not a.video_only:
        for scenario in ['static','paint']:
            for label,exe in [('baseline',a.baseline),('new',a.new)]:
                if label=='baseline' and a.reuse_baseline:
                    source=a.reuse_baseline/(label+'-'+scenario+'.json');data=json.loads(source.read_text(encoding='utf-8-sig'))
                    assert data['complete'] and data['errors']==0 and data['renderedFrames']>0
                    shutil.copy2(source,out/source.name);results[label+'-'+scenario]=data
                else:results[label+'-'+scenario]=run_player(exe.resolve(),out,label+'-'+scenario,scenario)
        differences={}
        for scenario in ['static','paint']:
            before=results['baseline-'+scenario];after=results['new-'+scenario]
            m0={m['name']:m for m in before['metrics']};m1={m['name']:m for m in after['metrics']}
            differences[scenario]={'gpuEvidenceValid':before['gpuTimingAvailable'] and after['gpuTimingAvailable'],
              'gpuP95DeltaMs':m1['gpuFrameMs']['p95']-m0['gpuFrameMs']['p95'],'mainP95DeltaMs':m1['mainThreadMs']['p95']-m0['mainThreadMs']['p95'],
              'baseline':before,'new':after,'baselineSource':str(a.reuse_baseline.resolve()) if a.reuse_baseline else str(out)}
        (out/'comparison.json').write_text(json.dumps(differences,indent=2),encoding='utf-8')
    for label,exe in [('baseline-video',a.baseline),('new-video',a.new)]:
        if label=='baseline-video' and a.reuse_baseline:shutil.copytree(a.reuse_baseline/label,out/label)
        else:run_player(exe.resolve(),out,label,'video',True)
