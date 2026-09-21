"""Encode 240 consecutive Player screenshots without interpolating or repeating frames."""
import argparse, hashlib, json, struct, subprocess
from pathlib import Path

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--input',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--ffmpeg',type=Path,required=True)
args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
evidence={}
for label in ('baseline','new'):
    folder=args.input/(label+'-video')
    frames=sorted(folder.glob('[0-9][0-9][0-9][0-9].png'))
    assert [p.name for p in frames]==[f'{i:04d}.png' for i in range(240)],label+' has missing frames'
    dimensions={struct.unpack('>II',p.read_bytes()[16:24]) for p in frames}
    assert dimensions=={(1920,1080)},dimensions
    hashes={hashlib.sha256(p.read_bytes()).hexdigest() for p in frames}
    assert len(hashes)==240,'Camera recording contains duplicate frames'
    output=args.output/(label+'.mp4')
    command=[str(args.ffmpeg),'-y','-loglevel','error','-framerate','30','-i',str(folder/'%04d.png'),
             '-frames:v','240','-c:v','libx264','-threads','2','-preset','fast','-crf','18',
             '-pix_fmt','yuv420p','-movflags','+faststart',str(output)]
    subprocess.run(command,check=True)
    evidence[label]={'frames':240,'uniqueFrames':len(hashes),'width':1920,'height':1080,'fps':30,'seconds':8,
                     'file':output.name,'sha256':hashlib.sha256(output.read_bytes()).hexdigest()}
    print(label+' video encoded',flush=True)
subprocess.run([str(args.ffmpeg),'-y','-loglevel','error','-i',str(args.output/'baseline.mp4'),'-i',str(args.output/'new.mp4'),
                '-filter_complex','[0:v]scale=960:540[a];[1:v]scale=960:540[b];[a][b]hstack=inputs=2[v]',
                '-map','[v]','-c:v','libx264','-threads','2','-preset','fast','-crf','18','-pix_fmt','yuv420p',
                '-movflags','+faststart',str(args.output/'comparison-left-baseline-right-new.mp4')],check=True)
(args.output/'video-evidence.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
print('Comparison encoded: left baseline, right new.',flush=True)
