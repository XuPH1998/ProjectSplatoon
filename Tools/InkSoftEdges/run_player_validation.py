"""Serial same-build A/B runs, three repeats, followed by separate moving-camera captures."""
import argparse
import importlib.util
import json
import statistics
import subprocess
import threading
import time
import sys
from pathlib import Path
sys.dont_write_bytecode = True

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--exe', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--video-only', action='store_true')
p.add_argument('--performance-only', action='store_true')
a = p.parse_args()
spec = importlib.util.spec_from_file_location('static_player', Path(__file__).resolve().parents[1] / 'InkStaticUpgrade/run_player_validation.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
out = a.output.resolve()
out.mkdir(parents=True, exist_ok=True)
results = {}
def measured_run(name, scenario, look):
    stop = threading.Event()
    def environment():
        with (out / (name + '-gpu-environment.jsonl')).open('w', encoding='utf-8') as stream:
            while not stop.is_set():
                try:
                    values = subprocess.check_output(['nvidia-smi', '--query-gpu=clocks.gr,clocks.mem,temperature.gpu,utilization.gpu,power.draw', '--format=csv,noheader,nounits'], text=True, timeout=3, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                    stream.write(json.dumps({'time': time.time(), 'graphicsMHz_memoryMHz_celsius_utilizationWatts': values.strip()}) + '\n')
                    stream.flush()
                except (OSError, subprocess.SubprocessError):
                    return
                stop.wait(2)
    monitor = threading.Thread(target=environment, daemon=True)
    monitor.start()
    try:
        return runner.run_player(a.exe.resolve(), out, name, scenario, look=look)
    finally:
        stop.set()
        monitor.join(timeout=4)
if not a.video_only:
    for scenario in ('static', 'paint'):
        trials = []
        for repeat in range(3):
            pair = {}
            for label, look in (('baseline', 'rounded'), ('new', 'soft')):
                name = f'{label}-{scenario}-{repeat + 1}'
                pair[label] = measured_run(name, scenario, look)
            metrics = {label: {m['name']: m for m in run['metrics']} for label, run in pair.items()}
            trial = {'repeat': repeat + 1, 'baseline': pair['baseline'], 'new': pair['new'],
                     'gpuEvidenceValid': all(run['gpuTimingAvailable'] for run in pair.values()),
                     'gpuP95DeltaMs': metrics['new']['gpuFrameMs']['p95'] - metrics['baseline']['gpuFrameMs']['p95'],
                     'mainP95DeltaMs': metrics['new']['mainThreadMs']['p95'] - metrics['baseline']['mainThreadMs']['p95']}
            trials.append(trial)
            results[scenario] = {'trials': trials, 'gpuBudgetMs': .5,
                'gpuP95MedianDeltaMs': statistics.median(t['gpuP95DeltaMs'] for t in trials),
                'passed': len(trials) == 3 and all(t['gpuEvidenceValid'] and t['gpuP95DeltaMs'] <= .5 for t in trials)}
            (out / 'comparison.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
if not a.performance_only:
    for label, look in (('baseline-video', 'rounded'), ('new-video', 'soft')):
        runner.run_player(a.exe.resolve(), out, label, 'video', video=True, look=look)
if not a.video_only and not all(r['passed'] for r in results.values()):
    raise RuntimeError('GPU budget failed or timings unavailable; inspect comparison.json')
