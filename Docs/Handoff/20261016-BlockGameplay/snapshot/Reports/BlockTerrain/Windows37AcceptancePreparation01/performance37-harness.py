"""Bind Windows37 to the existing four approved full performance workloads.

Calls the unmodified Tools reader. Its original six-case aggregate remains intact;
the separate four-case result follows the user's tower/cascade scope deferral.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import traceback

sys.dont_write_bytecode = True
ROOT = Path('D:/XPHUNITY/ProjectSplatoon')
HERE = Path(__file__).resolve().parent
PLAYER = ROOT / 'Builds/Blocks-20261016-block37/InkLan.exe'
OUTPUT = HERE / 'PerformanceRun01'
CASES = ['steady250k', 'combat250k', 'blast8', 'scattered100k']
DURATIONS = [180, 300, 120, 180]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def save_new(path, data):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--player', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--execution-inputs', type=Path, required=True)
    parser.add_argument('--execution-sha', required=True)
    args = parser.parse_args()
    require(args.player.resolve() == PLAYER and args.output.resolve() == OUTPUT,
            'Use the fixed new-package and fresh lane paths')
    require(args.execution_inputs.resolve() == HERE / 'execution-inputs.json'
            and digest(args.execution_inputs) == args.execution_sha, 'Frozen execution inputs changed')
    inputs = read(args.execution_inputs)
    for row in inputs['pins']:
        path = ROOT / row['path']
        require(digest(path) == row['sha256'], 'Pinned input changed: ' + row['path'])
    repair = ROOT / 'Reports/BlockTerrain/CubeProjectileRepair20261016'
    for row in read(repair / 'BuildRevision04/package-inventory.json'):
        path = ROOT / row['path']
        require(path.stat().st_size == row['bytes'] and digest(path) == row['sha256'],
                'Built package changed: ' + row['path'])
    for row in read(repair / 'build-source-inputs.json'):
        require(digest(ROOT / row['path']) == row['sha256'], 'Built source changed: ' + row['path'])
    require(read(repair / 'root-acceptance.json')['projectileDisplayGatePassed'] is True,
            'Actual packaged projectile gate required')
    tool = ROOT / 'Tools/BlockTerrain/performance_acceptance.py'
    sys.path.insert(0, str(tool.parent))
    spec = importlib.util.spec_from_file_location('windows37_original_performance', tool)
    original = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(original)
    require([original.profile_durations('prototype-v1')[case] for case in CASES] == DURATIONS,
            'Approved full workload durations changed')
    original.require_background_player_desktop()
    argv = [str(tool), '--player', str(PLAYER), '--output', str(OUTPUT),
            '--cases', *CASES, '--acceptance-profile', 'prototype-v1', '--graphics-api', 'd3d12',
            '--capture-main-camera', '--stop-on-failure']
    record = dict(startedUtc=datetime.now(timezone.utc).isoformat(),
                  player=str(PLAYER), runtimeMvid='ccd65caf-bf48-42b1-9543-3a12417940df',
                  runtimeDllSha256=digest(PLAYER.parent / 'InkLan_Data/Managed/Splatoon.dll'),
                  executionInputSha256=args.execution_sha, originalToolSha256=digest(tool),
                  originalArguments=argv, requiredCases=CASES, requiredSeconds=DURATIONS,
                  graphicalHost=1, independentHeadlessInputClients=7,
                  originalOraclesAndCleanupUnchanged=True, diagnosticsShortened=False,
                  cpuProfilerEnabled=False, frameTimingDiagnosticEnabled=False,
                  deferredCases=['tower32m', 'cascade20k'], goalAcceptancePassed=False)
    save_new(OUTPUT / 'execution-start.json', record)
    previous_argv = sys.argv
    code = 1
    try:
        sys.argv = argv
        code = original.main()
    except BaseException as error:
        record['error'] = dict(type=type(error).__name__, message=str(error))
        traceback.print_exc()
    finally:
        sys.argv = previous_argv
        summary = OUTPUT / 'prototype-v1/summary.json'
        results = read(summary).get('cases', []) if summary.is_file() else []
        record.update(finishedUtc=datetime.now(timezone.utc).isoformat(), originalExitCode=code,
                      completedCaseCount=len(results), actualCases=[r['scenario'] for r in results],
                      approvedFourCasePerformancePassed=code == 0 and len(results) == len(CASES)
                          and {r['scenario'] for r in results} == set(CASES)
                          and all(r['passed'] is True for r in results),
                      originalSummaryPreserved=True)
        save_new(OUTPUT / 'execution-terminal.json', record)
    print(json.dumps({key: record[key] for key in ('originalExitCode', 'completedCaseCount',
          'approvedFourCasePerformancePassed', 'goalAcceptancePassed')}), flush=True)
    return code


if __name__ == '__main__':
    raise SystemExit(main())
