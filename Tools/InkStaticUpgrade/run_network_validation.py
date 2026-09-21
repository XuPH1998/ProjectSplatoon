"""Reuse the tested early/late/reconnect runner with dense paint enabled on the authoritative host."""
import argparse, importlib.util, subprocess, json
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('--exe',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
p.add_argument('--width',type=int,default=1920);p.add_argument('--height',type=int,default=1080);args=p.parse_args()
spec=importlib.util.spec_from_file_location('ink_edges',Path(__file__).resolve().parents[1]/'InkEdges/run_network_validation.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
original=subprocess.Popen
def launch(command,*a,**kw):
    # Keep normal Player rendering and reject all-zero surfaces after comparison.
    command=[arg for arg in command if arg != '-batchmode']
    command[command.index('-screen-width')+1]=str(args.width)
    command[command.index('-screen-height')+1]=str(args.height)
    command+=['-screen-fullscreen','0']
    if '-lanSmokeHost' in command:command=list(command)+['-inkStaticScenario','static']
    return original(command,*a,**kw)
module.subprocess.Popen=launch
module.run(args.exe.resolve(),args.output.resolve())
path=args.output.resolve()/'result.json'
result=json.loads(path.read_text(encoding='utf-8'))
result['width']=args.width;result['height']=args.height
for stage in ('initial','final'):
    for role,sample in result[stage].items():
        for surface in sample['surfaces']:
            for size,hash_key in (('bytes','hash'),('visualBytes','visualHash')):
                zero_hash=(2166136261*pow(16777619,surface[size],2**32))%2**32
                if surface[hash_key]==zero_hash:
                    result['passed']=False;result['nonEmptyVisualEvidence']=False
                    path.write_text(json.dumps(result,indent=2),encoding='utf-8')
                    raise AssertionError(f"Blank {hash_key} at {stage}/{role}/surface {surface['id']}")
result['nonEmptyVisualEvidence']=True
path.write_text(json.dumps(result,indent=2),encoding='utf-8')
print('PASS: Every compared coverage and visual plane is nonempty.',flush=True)
