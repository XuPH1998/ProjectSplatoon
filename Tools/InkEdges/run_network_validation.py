"""Three independent players: live paint, late join, continuation, reconnect, exact settled hashes."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import time


def run(exe, output):
    output.mkdir(parents=True, exist_ok=True)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    trigger = output / 'continue-paint'
    if trigger.exists():
        raise RuntimeError('Use a fresh output directory for each run.')
    processes = {}

    def start(name, host=False, reconnect=False, duration=110):
        args = [str(exe), '-batchmode', '-force-d3d11', '-screen-width', '640', '-screen-height', '360',
                '-lanSmokeHost' if host else '-lanSmokeClient', '-lanAddress', '127.0.0.1', '-lanPort', str(port),
                '-inkSmokeCase', 'observer', '-inkLabel', name, '-inkEdgeProbe', '-inkEdgeProbeOutput', str(output / f'{name}-states.json'),
                '-inkEdgeContinueFile', str(trigger), '-networkProbe', str(duration),
                '-networkProbeOutput', str(output / f'{name}-timings.json'), '-logFile', str(output / f'{name}.log')]
        if reconnect:
            args += ['-lanLeaveAfter', '45', '-lanCycles', '1']
        processes[name] = subprocess.Popen(args, cwd=exe.parent, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0,
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def log(name):
        path = output / f'{name}.log'
        return path.read_text(encoding='utf-8', errors='replace') if path.exists() else ''

    def samples(name):
        try:
            return json.loads((output / f'{name}-states.json').read_text(encoding='utf-8-sig'))['samples']
        except (OSError, json.JSONDecodeError):
            return []

    def wait(predicate, label, seconds=45):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                print(label, flush=True)
                return
            for name, proc in processes.items():
                if proc.poll() is not None and proc.returncode != 0:
                    raise RuntimeError(f'{name} exited {proc.returncode} while waiting for {label}')
            time.sleep(.25)
        raise RuntimeError('Timeout: ' + label)

    def equal_at(sequence):
        group = {}
        for name in processes:
            matching = [s for s in samples(name) if s['sequence'] == sequence]
            if not matching:
                return None
            group[name] = matching[-1]
        reference = group['host']
        for name, sample in group.items():
            if sample['ownershipHash'] != reference['ownershipHash'] or sample['surfaces'] != reference['surfaces']:
                raise AssertionError(f'Ownership or GPU state diverged at sequence {sequence}: {name}')
        return group

    try:
        start('host', host=True, duration=120)
        wait(lambda: '[SMOKE] Connected' in log('host'), 'Host connected')
        start('early', reconnect=True, duration=80)
        wait(lambda: bool(samples('host')) and bool(samples('early')), 'Initial live paint settled')
        first_sequence = samples('host')[-1]['sequence']
        start('late', duration=100)
        wait(lambda: equal_at(first_sequence), 'Late join hashes match')
        initial = equal_at(first_sequence)
        trigger.write_text('continue', encoding='utf-8')
        wait(lambda: samples('host')[-1]['sequence'] > first_sequence, 'Additional paint settled')
        final_sequence = samples('host')[-1]['sequence']
        wait(lambda: equal_at(final_sequence), 'Continuation hashes match')
        wait(lambda: '[SMOKE] Reconnected after cleanup' in log('early'), 'Early client reconnected', seconds=65)
        # A reconnect produces another settled capture at the same sequence after a new match instance.
        wait(lambda: len([s for s in samples('early') if s['sequence'] == final_sequence]) >= 2,
             'Reconnected state captured', seconds=45)
        final = equal_at(final_sequence)
        wait(lambda: all(p.poll() is not None for p in processes.values()), 'All players exited', seconds=100)
        codes = {name: p.returncode for name, p in processes.items()}
        timings = {name: json.loads((output / f'{name}-timings.json').read_text(encoding='utf-8-sig')) for name in processes}
        passed = all(v == 0 for v in codes.values()) and all(t['errors'] == 0 for t in timings.values()) and bool(final)
        result = {'passed': passed, 'fixture': 'Deterministic host paint through the normal authoritative Paint API; observer clients.',
                  'firstSequence': first_sequence, 'finalSequence': final_sequence, 'initial': initial, 'final': final,
                  'exitCodes': codes, 'timings': timings, 'reconnected': True}
        (output / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        if not passed:
            raise AssertionError('Process failure or Unity errors; inspect result.json')
        print('PASS: 36 surface hashes and ownership match across live paint, late join, continuation and reconnect.', flush=True)
    finally:
        for proc in processes.values():
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=15)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    run(args.exe.resolve(), args.output.resolve())
