"""Single rendered Windows Player, fixed foam smoke input, real frame timing (no extra render camera)."""
import argparse
import json
import socket
import subprocess
from pathlib import Path

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--exe',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--visible',action='store_true',help='Explicitly show the interactive Player window; hidden windows may produce no rendered frames.')
args=parser.parse_args()
exe=args.exe.resolve(); output=args.output.resolve();output.mkdir(parents=True,exist_ok=True)
with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sock:
    sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
command=[str(exe),'-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-force-d3d11',
         '-lanSmokeHost','-lanPort',str(port),'-inkSmokeCase','foam','-inkLabel','foam-render',
         '-frameProbe','40','-frameProbePlayers','1','-frameProbeWarmup','5','-frameProbeQuit',
         '-frameProbeOutput',str(output/'frame.json'),'-logFile',str(output/'player.log')]
(output/'command.json').write_text(json.dumps(command,indent=2),encoding='utf-8')
info=subprocess.STARTUPINFO();info.dwFlags|=subprocess.STARTF_USESHOWWINDOW;info.wShowWindow=1 if args.visible else 0
result=subprocess.run(command,cwd=exe.parent,timeout=150,startupinfo=info)
report=json.loads((output/'frame.json').read_text(encoding='utf-8-sig'))
print(json.dumps({'exit':result.returncode,'complete':report['complete'],'renderedFrames':report['renderedFrames'],'gpuTimingAvailable':report['gpuTimingAvailable'],
                  'metrics':report['metrics']},ensure_ascii=False))
raise SystemExit(result.returncode if result.returncode else (0 if report['complete'] and report['errors']==0 and report['renderedFrames']>0 else 1))
