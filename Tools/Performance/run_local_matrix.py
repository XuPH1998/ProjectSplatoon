"""Same-machine auxiliary validation; physical-device acceptance is separate."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--players', type=int, nargs='+', default=[1, 2, 4])
    parser.add_argument('--scenarios', nargs='+', choices=['idle', 'fire', 'swim'], default=['idle', 'fire', 'swim'])
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--seconds', type=int, default=60)
    parser.add_argument('--width', type=int, default=1920)
    parser.add_argument('--height', type=int, default=1080)
    parser.add_argument('--base-port', type=int, default=28740)
    parser.add_argument('--visible', action='store_true', help='Show Player windows for normal camera timing')
    parser.add_argument('--playing', action='store_true', help='Start a formal round when at least two players are present')
    parser.add_argument('--late-join-seconds', type=int, default=0, help='Delay the final client to exercise paint checkpoint restore')
    args = parser.parse_args()
    exe = args.exe.resolve(strict=True)
    root = args.output.resolve(); root.mkdir(parents=True, exist_ok=True)
    manifest = {'kind': 'same-machine-only', 'normalCameraOnly': True, 'inkReplenishmentFixture': True, 'cases': []}
    index = 0
    for count in args.players:
        if count not in (1, 2, 4): parser.error('--players accepts 1, 2, or 4')
        for scenario in args.scenarios:
            for repeat in range(args.repeats):
                name = f'{count}p-{scenario}-{repeat + 1}'
                folder = root / name; folder.mkdir(exist_ok=True)
                case = {'name': name, 'processes': [], 'passed': False}
                processes = []
                try:
                    for player in range(count):
                        if player > 0 and player == count - 1 and args.late_join_seconds > 0:
                            time.sleep(args.late_join_seconds)
                        label = 'host' if player == 0 else f'client-{player}'
                        output = folder / f'{label}.json'
                        cmd = [str(exe), '-force-d3d11', '-screen-fullscreen', '0', '-screen-width', str(args.width),
                               '-screen-height', str(args.height), '-lanSmokeHost' if player == 0 else '-lanSmokeClient',
                               '-lanAddress', '127.0.0.1', '-lanPort', str(args.base_port + index), '-inkSmokeCase', 'inkperf',
                               '-frameScenario', scenario, '-inkLabel', label, '-frameProbe', str(args.seconds),
                               '-frameProbePlayers', str(count), '-frameProbeWarmup', '5', '-frameProbeTimeout', str(args.seconds + 120),
                               '-frameProbeOutput', str(output), '-frameProbeQuit', '-frameProbeQuitDelay', '8' if player == 0 else '3',
                               '-logFile', str(folder / f'{label}.log')]
                        if args.playing and count >= 2: cmd.append('-inkPerfRound')
                        info = None
                        if os.name == 'nt' and not args.visible:
                            info = subprocess.STARTUPINFO(); info.dwFlags |= subprocess.STARTF_USESHOWWINDOW; info.wShowWindow = 0
                        p = subprocess.Popen(cmd, cwd=exe.parent, startupinfo=info)
                        processes.append(p); case['processes'].append({'label': label, 'pid': p.pid, 'arguments': cmd})
                        if player == 0: time.sleep(2)
                    deadline = time.monotonic() + args.seconds + 150
                    while any(p.poll() is None for p in processes) and time.monotonic() < deadline: time.sleep(1)
                    case['passed'] = True
                    for entry, p in zip(case['processes'], processes):
                        entry['exitCode'] = p.poll()
                        path = folder / f"{entry['label']}.json"
                        report = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
                        # A hidden Player that never renders is not a graphics performance result.
                        entry['normalFramesVerified'] = report.get('renderedFrames', 0) > 0
                        case['passed'] &= p.poll() == 0 and report.get('complete', False) and report.get('errors') == 0 and entry['normalFramesVerified']
                finally:
                    for p in processes:
                        if p.poll() is None: p.terminate()
                    for p in processes:
                        try: p.wait(timeout=15)
                        except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=15)
                    manifest['cases'].append(case)
                    (root / 'matrix.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
                print(name, 'PASS' if case['passed'] else 'INCOMPLETE', flush=True)
                index += 1
    return 0 if all(c['passed'] for c in manifest['cases']) else 4


if __name__ == '__main__':
    raise SystemExit(main())
