"""Observe Windows37 CPU samples without changing any workload or oracle.

This run explicitly enables the existing Development Player profiler. Its
results are diagnostic and can never establish performance acceptance.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import traceback

sys.dont_write_bytecode = True
ROOT = Path('D:/XPHUNITY/ProjectSplatoon')
HERE = Path(__file__).resolve().parent
PLAYER = ROOT / 'Builds/Blocks-20261016-block37/InkLan.exe'
OUTPUT = HERE / 'CpuRun01'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def save(path, data):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--player', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--execution-inputs', type=Path, required=True)
    parser.add_argument('--execution-sha', required=True)
    args = parser.parse_args()
    require(args.player.resolve() == PLAYER and args.output.resolve() == OUTPUT, 'Fixed fresh diagnostic lane required')
    require(args.execution_inputs.resolve() == HERE / 'execution-inputs.json'
            and digest(args.execution_inputs) == args.execution_sha, 'Frozen diagnostic inputs changed')
    for pin in read(args.execution_inputs)['pins']:
        require(digest(ROOT / pin['path']) == pin['sha256'], 'Pinned input changed: ' + pin['path'])
    repair = ROOT / 'Reports/BlockTerrain/CubeProjectileRepair20261016'
    inventory = read(repair / 'BuildRevision04/package-inventory.json')
    for row in inventory:
        path = ROOT / row['path']
        require(path.stat().st_size == row['bytes'] and digest(path) == row['sha256'], 'Package changed: ' + row['path'])
    sources = read(repair / 'build-source-inputs.json')
    for row in sources:
        require(digest(ROOT / row['path']) == row['sha256'], 'Source changed: ' + row['path'])
    tool = ROOT / 'Tools/BlockTerrain/performance_acceptance.py'
    sys.path.insert(0, str(tool.parent))
    spec = importlib.util.spec_from_file_location('cpu37_original_performance', tool)
    performance = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(performance)
    performance.require_background_player_desktop()
    require(os.environ.get('SPLATOON_DISABLE_SYSTEM_CURSOR') == '1', 'Cursor suppression required')
    original_host = performance.visible_graphics_process
    record = dict(startedUtc=datetime.now(timezone.utc).isoformat(), diagnosticOnly=True,
                  performanceAcceptance=False, goalAcceptancePassed=False,
                  player=str(PLAYER), runtimeMvid='ccd65caf-bf48-42b1-9543-3a12417940df',
                  runtimeSha256=digest(PLAYER.parent / 'InkLan_Data/Managed/Splatoon.dll'),
                  toolSha256=digest(tool), executionInputsSha256=args.execution_sha,
                  scenario='steady250k', seconds=180, graphicalHost=1, independentInputClients=7,
                  packageFilesVerified=len(inventory), buildInputsVerified=len(sources), hosts=[])
    owned = []

    def profile_host(command, cwd):
        parts = [str(part) for part in command]
        require(not owned and '-lanSmokeHost' in parts and '-batchmode' not in parts
                and '-nographics' not in parts and '-force-d3d12' in parts, 'One original normal D3D12 host required')
        require(not any(flag in parts for flag in ('-blockBenchmarkCpuProfile', '-profiler-enable',
                '-profiler-log-file', '-deepProfiling')), 'No additional profiler configuration')
        report = Path(parts[parts.index('-blockBenchmarkOutput') + 1]).resolve()
        require(report.is_relative_to(OUTPUT), 'Diagnostic output escaped fresh directory')
        raw = report.parent / 'host.sampling.cpu.raw'
        require(not raw.exists() and not Path(str(raw) + '.window.json').exists(), 'Preserve existing raw and sidecar')
        actual = [*command, '-blockBenchmarkCpuProfile', str(raw)]
        process = original_host(actual, cwd)
        owned.append((process, raw))
        record['hosts'].append(dict(pid=process.pid, arguments=[str(p) for p in actual], raw=str(raw)))
        save(OUTPUT / 'host-dispatch.json', record['hosts'][0])
        return process

    previous_argv = sys.argv
    code = 1
    try:
        performance.visible_graphics_process = profile_host
        sys.argv = [str(tool), '--player', str(PLAYER), '--output', str(OUTPUT), '--cases', 'steady250k',
                    '--acceptance-profile', 'prototype-v1', '--diagnostic-seconds', '180',
                    '--graphics-api', 'd3d12', '--capture-main-camera']
        record['originalArguments'] = sys.argv
        save(OUTPUT / 'execution-start.json', record)
        code = performance.main()
        require(len(owned) == 1 and owned[0][0].poll() is not None, 'Host must exit before validating capture')
        raw = owned[0][1]
        sidecar = Path(str(raw) + '.window.json')
        window = read(sidecar)
        benchmark = read(raw.parent / 'host.performance.json')
        required = ('playerOnly', 'enableProfilerCompilation', 'started', 'startReadbackMatched', 'restorationReadbackMatched')
        require(all(window.get(k) is True for k in required), 'Profiler state not confirmed')
        require(window.get('finishReason') == 'sampling_complete'
                and not window.get('startError') and not window.get('metadataError')
                and not window.get('restorationErrors') and window.get('droppedFrames') == 0, 'CPU window incomplete')
        require(benchmark.get('complete') is True and benchmark.get('samples', 0) > 0
                and all(window.get(k) == benchmark['samples'] for k in
                    ('observedSamples', 'metadataCallsReturned', 'retainedFrames', 'finalFormalSamples')), 'Capture counts differ')
        require(window['originalState'] == window['restoredState']
                and window['originalState']['enabled'] is False and window['originalState']['binaryLog'] is False,
                'Original profiler state was not restored')
        record['captureStateValidated'] = True
        record['captureFiles'] = [dict(path=str(p), bytes=p.stat().st_size, sha256=digest(p)) for p in (raw, sidecar)]
        for row in inventory:
            require(digest(ROOT / row['path']) == row['sha256'], 'Package changed during capture')
        record['packageUnchanged'] = True
    except BaseException as error:
        record['error'] = dict(type=type(error).__name__, message=str(error))
        traceback.print_exc()
        code = 1
    finally:
        performance.visible_graphics_process = original_host
        sys.argv = previous_argv
        record.update(finishedUtc=datetime.now(timezone.utc).isoformat(), originalHarnessExitCode=code)
        save(OUTPUT / 'execution-terminal.json', record)
    print(json.dumps({k:record.get(k) for k in ('originalHarnessExitCode','captureStateValidated','error','performanceAcceptance')}), flush=True)
    return code


if __name__ == '__main__':
    raise SystemExit(main())
