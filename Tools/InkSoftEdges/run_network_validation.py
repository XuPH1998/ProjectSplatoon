"""Enable the experimental derived cache on every early/late/reconnected peer."""
import argparse
import importlib.util
import subprocess
import json
import sys
from pathlib import Path
sys.dont_write_bytecode = True

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--exe',type=Path,required=True)
p.add_argument('--output',type=Path,required=True)
a=p.parse_args()
spec=importlib.util.spec_from_file_location('network',Path(__file__).resolve().parents[1]/'InkEdges/run_network_validation.py')
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
original=subprocess.Popen
def launch(command,*args,**kwargs):
    command=[x for x in command if x!='-batchmode']
    command+=['-inkSoftCompare','-screen-fullscreen','0']
    # Dense initial paint plus reconnect/snapshot leaves too little time in the
    # older runner's 80s early-client lifetime for a second 36-surface readback.
    command[command.index('-networkProbe')+1]='145' if '-lanSmokeHost' in command else '105' if '-lanCycles' in command else '110'
    if '-lanCycles' in command:command[command.index('-lanLeaveAfter')+1]='55'
    if '-lanSmokeHost' in command:command+=['-inkStaticScenario','static']
    return original(command,*args,**kwargs)
module.subprocess.Popen=launch
module.run(a.exe.resolve(),a.output.resolve())
path=a.output.resolve()/'result.json'
result=json.loads(path.read_text(encoding='utf-8'))
for stage in ('initial','final'):
    for role,sample in result[stage].items():
        assert '[INK-COMPARE] Soft' in (a.output/ (role+'.log')).read_text(encoding='utf-8',errors='replace')
        for surface in sample['surfaces']:
            for size,key in (('bytes','hash'),('visualBytes','visualHash')):
                blank=(2166136261*pow(16777619,surface[size],2**32))%2**32
                assert surface[key]!=blank, f'Blank {stage}/{role}/{surface["id"]}/{key}'
result['experimentalLookEnabledOnAllPeers']=True
result['nonemptyPersistentPlanes']=True
result['derivedDistanceHashesCompared']=False
path.write_text(json.dumps(result,indent=2),encoding='utf-8')
