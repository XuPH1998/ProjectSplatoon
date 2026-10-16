"""Serial Editor filters; retain XML including failures, never emit runtime commands while testing."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CLI = Path('C:/Users/wepie/AppData/Local/Unity/bin/unity.exe')
STATUS = ROOT / 'Temp/pipeline_test_status.json'
RESULTS = ROOT / 'Reports/BlockTerrain/UnityTests'


def run_filter(test_filter, include_explicit=False):
    started = time.time()
    call = subprocess.run([str(CLI), 'command', 'run_tests', '--project-path', str(ROOT),
                           '--caller', 'plugin', '--skill', 'unity-cli', '--format', 'json', '--',
                           '--mode', 'editor', '--filter', test_filter, '--async_tests', 'true', '--timeout', '1200',
                           *(['--include_explicit', 'true'] if include_explicit else [])],
                          cwd=ROOT, capture_output=True, text=True, encoding='utf-8')
    if call.returncode or json.loads(call.stdout).get('success') is not True:
        raise RuntimeError(call.stdout + call.stderr)
    print('RUNNING ' + test_filter, flush=True)
    deadline = time.monotonic() + 1200
    while time.monotonic() < deadline:
        try:
            state = json.loads(STATUS.read_text(encoding='utf-8-sig'))
            if state.get('status') == 'error':
                raise RuntimeError(str(state))
            files = [path for path in RESULTS.glob('*.xml') if path.stat().st_mtime >= started]
            if state.get('status') == 'completed' and STATUS.stat().st_mtime >= started and not files and state.get('summary', {}).get('total') == 0:
                result = {'filter': test_filter, 'xml': None, 'cases': 0, 'passed': 0,
                          'seconds': time.time() - started, 'failures': [{'message': 'Completed without selecting any test case'}]}
                print(json.dumps(result, ensure_ascii=False), flush=True)
                return result
            if state.get('status') == 'completed' and files:
                latest = max(files, key=lambda path: path.stat().st_mtime)
                cases = ET.parse(latest).findall('.//test-case')
                result = {'filter': test_filter, 'xml': str(latest), 'cases': len(cases),
                          'passed': sum(case.get('result') == 'Passed' for case in cases),
                          'seconds': time.time() - started,
                          'failures': [{'name': case.get('fullname'), 'result': case.get('result'),
                                        'message': case.findtext('failure/message')}
                                       for case in cases if case.get('result') != 'Passed']}
                print(json.dumps(result, ensure_ascii=False), flush=True)
                return result
        except (OSError, ValueError, ET.ParseError):
            pass
        time.sleep(1)
    raise RuntimeError('Timed out: ' + test_filter)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', required=True)
    parser.add_argument('--include-explicit', action='store_true')
    parser.add_argument('filters', nargs='+')
    args = parser.parse_args()
    results = []
    try:
        for test_filter in args.filters:
            results.append(run_filter(test_filter, args.include_explicit))
    finally:
        Path(args.report).write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    raise SystemExit(0 if all(row['cases'] and not row['failures'] for row in results) else 3)
