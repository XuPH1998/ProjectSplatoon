"""Serial Editor filters; retain raw evidence and issue no commands during a run."""
import argparse
from collections import Counter
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
CLI = Path('C:/Users/wepie/AppData/Local/Unity/bin/unity.exe')
STATUS = ROOT / 'Temp/pipeline_test_status.json'
REQUEST = ROOT / 'Temp/pipeline_test_request.json'
RESULTS = ROOT / 'Reports/BlockTerrain/UnityTests'
DESKTOP_PROBE = ROOT / 'Reports/BlockTerrain/FullAssemblyRegression96/IsolatedDesktopProbe.cs'


def xml_signature(path):
    info = path.stat()
    return info.st_mtime_ns, info.st_size


def build_launch_arguments(test_filter, include_explicit=False, filter_type='testName', timeout_seconds=1200):
    return [str(CLI), 'command', 'run_tests', '--project-path', str(ROOT),
            '--caller', 'plugin', '--skill', 'unity-cli', '--format', 'json', '--',
            '--mode', 'editor', '--filter', test_filter,
            *(['--filter_type', filter_type] if filter_type.lower() != 'testname' else []),
            '--async_tests', 'true', '--timeout', str(timeout_seconds),
            *(['--include_explicit', 'true'] if include_explicit else [])]


def canonical_root_result(value):
    outcomes = {'Passed': 'Passed', 'Failed': 'Failed', 'Failed(Child)': 'Failed',
                'Skipped': 'Skipped', 'Inconclusive': 'Inconclusive'}
    if value not in outcomes:
        raise ValueError('XML root has an unsupported or nonterminal result: ' + str(value))
    return outcomes[value]


def validate_launch(envelope, test_filter, filter_type):
    if not isinstance(envelope, dict) or envelope.get('success') is not True:
        raise ValueError('CLI launch did not succeed')
    response = envelope
    if 'data' in envelope:
        data = envelope['data']
        if not isinstance(data, dict) or data.get('command') != 'run_tests':
            raise ValueError('CLI payload is not the requested run_tests command')
        response = data.get('result')
    if not isinstance(response, dict) or response.get('success') is not True or response.get('result') != 'running':
        raise ValueError('Pipeline did not confirm this single async launch')
    if response.get('StatusPath', response.get('statusPath')) != 'Temp/pipeline_test_status.json' or \
            response.get('Mode', response.get('mode')) != 'EditMode' or \
            response.get('FilterApplied', response.get('filterApplied')) != filter_type.lower() + ': ' + test_filter:
        raise ValueError('Pipeline launch identity/schema does not match this invocation')


def validate_terminal_xml(path, state, started, test_filter, filter_type):
    root = ET.parse(path).getroot()
    cases = root.findall('.//test-case')
    if root.tag != 'test-run' or not root.get('end-time') or not root.get('start-time'):
        raise ValueError('XML root is not a terminal test-run')
    raw_root_result = root.get('result')
    canonical_result = canonical_root_result(raw_root_result)
    if any(case.get('result') not in ('Passed', 'Failed', 'Skipped', 'Inconclusive') or
                not case.get('end-time') for case in cases):
        raise ValueError('XML contains a nonterminal root or case')
    root_start = datetime.datetime.strptime(root.get('start-time'), '%Y-%m-%d %H:%M:%SZ').replace(tzinfo=datetime.timezone.utc).timestamp()
    root_end = datetime.datetime.strptime(root.get('end-time'), '%Y-%m-%d %H:%M:%SZ').replace(tzinfo=datetime.timezone.utc).timestamp()
    # NUnit timestamps have whole-second precision; tolerate only that truncation.
    if root_start < int(started) or root_end < root_start:
        raise ValueError('XML test run predates this invocation or has reversed times')
    counts = {'total': len(cases), 'passed': sum(case.get('result') == 'Passed' for case in cases),
              'failed': sum(case.get('result') == 'Failed' for case in cases),
              'skipped': sum(case.get('result') == 'Skipped' for case in cases),
              'inconclusive': sum(case.get('result') == 'Inconclusive' for case in cases)}
    if any(int(root.get(key, '-1')) != count or state.get('summary', {}).get(key) != count
           for key, count in counts.items()):
        raise ValueError('XML root/leaf counts and fresh Pipeline summary disagree')
    if filter_type == 'assembly':
        assemblies = [suite for suite in root.findall('.//test-suite')
                      if suite.get('type') == 'Assembly' and suite.findall('.//test-case')]
        if not assemblies or any(test_filter.lower() not in suite.get('name', '').removesuffix('.dll').lower()
                                 for suite in assemblies):
            raise ValueError('XML does not belong to the selected assembly')
        if test_filter == 'Splatoon.Tests' and any(suite.get('name') != 'Splatoon.Tests.dll' for suite in assemblies):
            raise ValueError('Full project regression selected an unexpected assembly')
    available = state.get('results')
    if not isinstance(available, list) or len(available) > len(cases):
        raise ValueError('Pipeline results list is invalid')
    status_multiset = Counter((item.get('FullName'), item.get('Status')) for item in available)
    xml_multiset = Counter((case.get('fullname'), case.get('result')) for case in cases)
    if any(not name or not outcome for name, outcome in status_multiset) or status_multiset - xml_multiset:
        raise ValueError('Available Pipeline leaf results do not match XML')
    # Domain reload may leave a nonempty tail list; RootResult summary is authoritative.
    partial = len(available) < len(cases)
    if not partial and status_multiset != xml_multiset:
        raise ValueError('Complete Pipeline result multiset disagrees with XML')
    return cases, {'rootLeafAndStatusCountsAgree': True, 'selectedAssemblyVerified': filter_type == 'assembly',
                   'rootResult': raw_root_result, 'canonicalRootResult': canonical_result,
                   'statusResultCount': len(available), 'statusResultsPartialAfterPossibleDomainReload': partial,
                   'statusResultsMatchAvailableXmlLeaves': True,
                   'executedCases': len(cases), 'discoveryTestcasecountIsNotExecutedCount': True}


def validate_isolation_parameters(desktop_name, expected_editor_pid):
    if not isinstance(desktop_name, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', desktop_name) or \
            desktop_name.casefold() in ('default', 'winlogon'):
        raise ValueError('Testing requires a named isolated desktop; Default/Winlogon are forbidden')
    if type(expected_editor_pid) is not int or expected_editor_pid <= 0:
        raise ValueError('Testing requires the positive expected Editor PID')


def validate_editor_observation(loaded, desktop_name, expected_editor_pid, *, allow_callback_cleanup=False):
    validate_isolation_parameters(desktop_name, expected_editor_pid)
    if not isinstance(loaded, dict) or type(loaded.get('pid')) is not int or loaded['pid'] != expected_editor_pid or \
            not isinstance(loaded.get('projectPath'), str) or not loaded['projectPath'] or \
            Path(loaded.get('projectPath', '')).resolve() != ROOT.resolve() or \
            not isinstance(loaded.get('mainThreadDesktopName'), str) or \
            loaded['mainThreadDesktopName'].casefold() != desktop_name.casefold() or \
            not isinstance(loaded.get('inputDesktopName'), str) or not loaded['inputDesktopName'] or \
            loaded['inputDesktopName'].casefold() == desktop_name.casefold() or loaded.get('batchMode') is not False or \
            not isinstance(loaded.get('graphicsDeviceType'), str) or loaded['graphicsDeviceType'] in ('', 'Null') or \
            any(type(loaded.get(key)) is not bool for key in ('frameworkActive', 'collectorActive', 'playing', 'compiling', 'updating', 'building')) or \
            any(loaded[key] for key in ('playing', 'compiling', 'updating', 'building')) or \
            not allow_callback_cleanup and (loaded['frameworkActive'] or loaded['collectorActive']):
        raise RuntimeError('The same idle graphical Editor on the isolated desktop could not be verified; no test submitted')
    return loaded


def capture_terminal_proof(raw_dir, result):
    if result.get('xml') is None:
        return None
    xml = raw_dir / Path(result['xml']).name
    status = (raw_dir / 'terminal-status.json').read_bytes()
    started = json.loads((raw_dir / 'start.argv.json').read_text(encoding='utf-8'))['startedEpoch']
    validate_terminal_xml(xml, json.loads(status.decode('utf-8-sig')), started, result['filter'], result['filterType'])
    return {'xml': xml, 'xmlSha256': hashlib.sha256(xml.read_bytes()).hexdigest(), 'statusBytes': status,
            'startedEpoch': started, 'filter': result['filter'], 'filterType': result['filterType']}


def verify_editor_isolation(desktop_name, expected_editor_pid, raw_dir=None, *, previous_terminal=None):
    validate_isolation_parameters(desktop_name, expected_editor_pid)
    if previous_terminal is not None:
        xml = previous_terminal['xml']
        if hashlib.sha256(xml.read_bytes()).hexdigest() != previous_terminal['xmlSha256']:
            raise RuntimeError('The preceding verified terminal XML changed; no probe or new run submitted')
        validate_terminal_xml(xml, json.loads(previous_terminal['statusBytes'].decode('utf-8-sig')),
            previous_terminal['startedEpoch'], previous_terminal['filter'], previous_terminal['filterType'])
    # Only a verified preceding terminal result permits waiting for callback cleanup.
    # Repeated Read probes are observations; no run_tests, cancellation or UTF reset.
    cleanup_wait = previous_terminal is not None
    limit_seconds = 180 if cleanup_wait else 30
    deadline = time.monotonic() + limit_seconds
    observations = []
    attempts = []
    while True:
        if REQUEST.exists():
            raise RuntimeError('An existing test request is active; no desktop probe or new run was submitted')
        try:
            status_bytes = STATUS.read_bytes()
            offline_status = json.loads(status_bytes.decode('utf-8-sig'))
        except (OSError, ValueError) as error:
            raise RuntimeError('Offline test idle state is unavailable; no desktop probe or new run was submitted') from error
        if not isinstance(offline_status, dict) or offline_status.get('status') not in ('completed', 'error', 'cancelled'):
            raise RuntimeError('Offline test status is not terminal; no desktop probe or new run was submitted')
        if cleanup_wait and status_bytes != previous_terminal['statusBytes']:
            raise RuntimeError('Offline status changed after the preceding verified terminal run; no new run submitted')
        if cleanup_wait and hashlib.sha256(xml.read_bytes()).hexdigest() != previous_terminal['xmlSha256']:
            raise RuntimeError('The preceding verified terminal XML changed; no probe or new run submitted')
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError(f'Completed-run callback cleanup did not become idle within {limit_seconds} seconds; no new run submitted')
        method = 'Read' if cleanup_wait else 'Verify'
        call_seconds = min(60, remaining)
        # Keep inner Pipeline timeout below CLI timeout, both below subprocess bound.
        cli_seconds = max(1, math.ceil(call_seconds - 1))
        pipeline_ms = max(1, int(max(.001, call_seconds - 2) * 1000))
        argv = [str(CLI), 'command', 'run_script', '--project-path', str(ROOT),
                '--caller', 'plugin', '--skill', 'unity-cli', '--format', 'json', '--timeout', str(cli_seconds), '--',
                '--file', str(DESKTOP_PROBE), '--entry', 'Splatoon.ReportGuards.IsolatedDesktopProbe.' + method,
                '--args', json.dumps([] if method == 'Read' else [desktop_name]), '--mode', 'ephemeral',
                '--timeout_ms', str(pipeline_ms)]
        attempt_number = len(attempts) + 1
        attempt_prefix = f'desktop-probe.attempt-{attempt_number:03d}'
        argv_record = {'argv': argv, 'expectedEditorPid': expected_editor_pid,
            'expectedDesktopName': desktop_name, 'attempt': attempt_number,
            'totalObservationLimitSeconds': limit_seconds, 'singleCallLimitSeconds': call_seconds}
        if raw_dir is not None:
            argv_bytes = json.dumps(argv_record, ensure_ascii=False, indent=2).encode('utf-8')
            (raw_dir / (attempt_prefix + '.argv.json')).write_bytes(argv_bytes)
            (raw_dir / 'desktop-probe.argv.json').write_bytes(argv_bytes)
        process_timeout = None
        try:
            call = subprocess.run(argv, cwd=ROOT, capture_output=True, timeout=call_seconds,
                                  creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            stdout, stderr, returncode = call.stdout, call.stderr, call.returncode
        except subprocess.TimeoutExpired as error:
            process_timeout = error
            stdout, stderr, returncode = error.stdout or b'', error.stderr or b'', None
        if raw_dir is not None:
            (raw_dir / (attempt_prefix + '.stdout')).write_bytes(stdout)
            (raw_dir / (attempt_prefix + '.stderr')).write_bytes(stderr)
            (raw_dir / 'desktop-probe.stdout').write_bytes(stdout)
            (raw_dir / 'desktop-probe.stderr').write_bytes(stderr)
        # Check unchanged offline state even after an observational transport timeout.
        if REQUEST.exists() or STATUS.read_bytes() != status_bytes:
            raise RuntimeError('Offline run state changed during the desktop probe; no new run submitted')
        if cleanup_wait and hashlib.sha256(xml.read_bytes()).hexdigest() != previous_terminal['xmlSha256']:
            raise RuntimeError('The preceding verified terminal XML changed; no probe or new run submitted')
        envelope = None
        if process_timeout is None:
            try:
                envelope = json.loads(stdout.decode('utf-8-sig'))
            except (UnicodeDecodeError, ValueError):
                if returncode:
                    raise RuntimeError('Editor desktop probe failed; no test submitted')
                raise RuntimeError('Editor desktop probe returned invalid JSON; no test submitted')
        # Retry only the exact known run_script timeout envelope, never other errors.
        cli_read_timeout = cleanup_wait and isinstance(envelope, dict) and envelope.get('success') is False and \
            envelope.get('command') == 'unity command run_script' and envelope.get('data') is None and \
            isinstance(envelope.get('errors'), list) and bool(envelope['errors']) and \
            all(isinstance(error, dict) and error.get('code') == 'COMMAND_FAILED' and \
                isinstance(error.get('message'), str) and \
                re.fullmatch(r"Pipeline command 'run_script' timed out after [0-9]+ms", error['message'])
                for error in envelope['errors'])
        transient_read_timeout = cleanup_wait and (process_timeout is not None or cli_read_timeout)
        attempt_record = {'attempt': attempt_number, 'method': method, 'returncode': returncode,
            'subprocessObservationTimedOut': process_timeout is not None,
            'knownCliReadTimeout': bool(cli_read_timeout), 'transientReadTimeout': bool(transient_read_timeout),
            'retrySameReadOnly': bool(transient_read_timeout and time.monotonic() < deadline),
            'unityCancellationWasNotRequested': True, 'testWasNotSubmitted': True}
        attempts.append(attempt_record)
        if raw_dir is not None:
            (raw_dir / (attempt_prefix + '.outcome.json')).write_text(json.dumps(attempt_record, indent=2), encoding='utf-8')
            (raw_dir / 'desktop-probe.idle-wait.json').write_text(json.dumps({'limitSeconds': limit_seconds,
                'precedingTerminalXmlVerified': cleanup_wait, 'observations': observations,
                'attempts': attempts}, ensure_ascii=False, indent=2), encoding='utf-8')
        if time.monotonic() >= deadline:
            raise RuntimeError(f'Completed-run callback cleanup exceeded its {limit_seconds} second bound; no new run submitted')
        if transient_read_timeout:
            time.sleep(min(.25, max(0, deadline - time.monotonic())))
            continue
        if process_timeout is not None:
            raise RuntimeError('Editor desktop probe observation timed out; no test submitted') from process_timeout
        if returncode:
            raise RuntimeError('Editor desktop probe failed; no test submitted')
        if not isinstance(envelope, dict) or envelope.get('success') is not True:
            raise RuntimeError('Editor desktop probe was not confirmed; no test submitted')
        response = envelope
        if 'data' in envelope:
            payload = envelope['data']
            if not isinstance(payload, dict) or payload.get('command') != 'run_script':
                raise RuntimeError('Editor desktop probe returned an unexpected CLI payload; no test submitted')
            response = payload.get('result')
        if not isinstance(response, dict) or response.get('success') is not True:
            raise RuntimeError('Editor desktop probe returned a failed Pipeline response; no test submitted')
        loaded = validate_editor_observation(response.get('result'), desktop_name, expected_editor_pid,
                                            allow_callback_cleanup=cleanup_wait)
        # Identity and all busy flags remain strict; both UTF flags must clear.
        if REQUEST.exists() or STATUS.read_bytes() != status_bytes:
            raise RuntimeError('Offline run state changed during the desktop probe; no new run submitted')
        if time.monotonic() >= deadline:
            raise RuntimeError(f'Completed-run callback cleanup exceeded its {limit_seconds} second bound; no new run submitted')
        observations.append(loaded)
        if raw_dir is not None:
            (raw_dir / 'desktop-probe.idle-wait.json').write_text(json.dumps({'limitSeconds': limit_seconds,
                'precedingTerminalXmlVerified': cleanup_wait, 'observations': observations,
                'attempts': attempts}, ensure_ascii=False, indent=2), encoding='utf-8')
        if not loaded['frameworkActive'] and not loaded['collectorActive']:
            return loaded
        time.sleep(min(.25, max(0, deadline - time.monotonic())))


def run_filter(test_filter, include_explicit=False, filter_type='testName', timeout_seconds=1200, raw_dir=None,
               *, desktop_name=None, expected_editor_pid=None, previous_terminal=None):
    validate_isolation_parameters(desktop_name, expected_editor_pid)
    if REQUEST.exists():
        raise RuntimeError('Preserve the existing pending test request; no new run was submitted')
    # Only before submission, after the previous filter is terminal. No commands
    # are sent during the active run's existing offline observation loop.
    verify_editor_isolation(desktop_name, expected_editor_pid, raw_dir, previous_terminal=previous_terminal)
    if REQUEST.exists():
        raise RuntimeError('A test request appeared after the desktop probe; no new run was submitted')
    started = time.time()
    deadline = time.monotonic() + timeout_seconds
    before = {str(path): xml_signature(path) for path in RESULTS.glob('*.xml')}
    rejected = {}
    last_status = None
    argv = build_launch_arguments(test_filter, include_explicit, filter_type, timeout_seconds)
    if raw_dir is not None:
        (raw_dir / 'start.argv.json').write_text(json.dumps({
            'argv': argv, 'defaultIncludeExplicit': False, 'defaultFilterType': 'testName',
            'launchFilterAppliedExpected': filter_type.lower() + ': ' + test_filter,
            'startedEpoch': started}, ensure_ascii=False, indent=2), encoding='utf-8')
    try:
        call = subprocess.run(argv,
                              cwd=ROOT, capture_output=True, timeout=min(60, timeout_seconds),
                              creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    except subprocess.TimeoutExpired as error:
        if raw_dir is not None:
            (raw_dir / 'start.stdout').write_bytes(error.stdout or b'')
            (raw_dir / 'start.stderr').write_bytes(error.stderr or b'')
            (raw_dir / 'start-timeout.json').write_text(json.dumps({
                'launchObservationTimeoutSeconds': min(60, timeout_seconds),
                'unityCancellationWasNotRequested': True}), encoding='utf-8')
        raise RuntimeError('CLI launch observation timed out; Unity may still be running') from error
    if raw_dir is not None:
        (raw_dir / 'start.stdout').write_bytes(call.stdout)
        (raw_dir / 'start.stderr').write_bytes(call.stderr)
    if call.returncode:
        raise RuntimeError('Test launch failed; retain raw stdout/stderr and check pending request before restoring')
    validate_launch(json.loads(call.stdout.decode('utf-8-sig')), test_filter, filter_type)
    print('RUNNING ' + test_filter, flush=True)
    try:
        while time.monotonic() < deadline:
            try:
                last_status = STATUS.read_bytes()
                state = json.loads(last_status.decode('utf-8-sig'))
                fresh = STATUS.stat().st_mtime >= started
                if fresh and state.get('status') == 'error':
                    if raw_dir is not None:
                        (raw_dir / 'terminal-error-status.json').write_bytes(last_status)
                    raise RuntimeError('Fresh Pipeline error; Unity idle state is not inferred')
                if fresh and state.get('status') == 'completed' and not REQUEST.exists():
                    if state.get('summary', {}).get('total') == 0:
                        if raw_dir is not None:
                            (raw_dir / 'terminal-status.json').write_bytes(last_status)
                        return {'filter': test_filter, 'filterType': filter_type, 'includeExplicit': include_explicit,
                                'timeoutSeconds': timeout_seconds, 'xml': None, 'cases': 0, 'passed': 0,
                                'seconds': time.time() - started,
                                'failures': [{'message': 'Completed without selecting any test case'}]}
                    accepted = []
                    for path in RESULTS.glob('*.xml'):
                        if path.stat().st_mtime < started or before.get(str(path)) == xml_signature(path):
                            continue
                        try:
                            cases, association = validate_terminal_xml(path, state, started, test_filter, filter_type)
                            accepted.append((path, cases, association))
                        except (OSError, ValueError, ET.ParseError) as error:
                            rejected[str(path)] = str(error)
                    if len(accepted) > 1:
                        raise RuntimeError('Ambiguous matching terminal XML; preserve all candidates without accepting one')
                    if accepted:
                        latest, cases, association = accepted[0]
                        if raw_dir is not None:
                            (raw_dir / 'terminal-status.json').write_bytes(last_status)
                            (raw_dir / latest.name).write_bytes(latest.read_bytes())
                        result = {'filter': test_filter, 'filterType': filter_type, 'includeExplicit': include_explicit,
                                  'timeoutSeconds': timeout_seconds, 'xml': str(latest), 'cases': len(cases),
                                  'passed': sum(case.get('result') == 'Passed' for case in cases),
                                  'seconds': time.time() - started, 'association': association,
                                  'failures': [{'name': case.get('fullname'), 'result': case.get('result'),
                                                'message': case.findtext('failure/message')}
                                               for case in cases if case.get('result') != 'Passed']}
                        if association['canonicalRootResult'] != 'Passed' and not result['failures']:
                            result['failures'].append({'name': 'test-run', 'result': association['rootResult'],
                                                       'message': 'Terminal root did not pass despite no failed leaf; retain original XML suite evidence'})
                        print(json.dumps(result, ensure_ascii=False), flush=True)
                        return result
            except (OSError, ValueError, ET.ParseError):
                # Missing status during Play domain reload is observation uncertainty, never a re-submit trigger.
                pass
            time.sleep(1)
        raise RuntimeError('Observation timed out: ' + test_filter + '; Unity cancellation was not requested')
    finally:
        if raw_dir is not None:
            (raw_dir / 'rejected-xml.json').write_text(json.dumps(rejected, ensure_ascii=False, indent=2), encoding='utf-8')
            if last_status is not None:
                (raw_dir / 'last-observed-status.json').write_bytes(last_status)
            if REQUEST.exists():
                (raw_dir / 'pending-request.json').write_bytes(REQUEST.read_bytes())


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', required=True)
    parser.add_argument('--include-explicit', action='store_true')
    parser.add_argument('--filter-type', choices=['testName', 'assembly', 'category'], default='testName')
    parser.add_argument('--timeout-seconds', type=int, default=1200)
    parser.add_argument('--desktop-name')
    parser.add_argument('--expected-editor-pid', type=int)
    parser.add_argument('filters', nargs='+')
    args = parser.parse_args()
    try:
        validate_isolation_parameters(args.desktop_name, args.expected_editor_pid)
    except ValueError as error:
        parser.error(str(error))
    if args.timeout_seconds <= 0 or args.timeout_seconds > 2147483647:
        parser.error('--timeout-seconds must be positive and fit the installed CLI int parameter')
    if args.filter_type == 'assembly' and (args.include_explicit or len(args.filters) != 1):
        parser.error('Full assembly regression is one selector with Explicit excluded; reviewed exact names run separately')
    report_path = Path(args.report).resolve()
    raw_root = report_path.with_suffix(report_path.suffix + '.raw')
    if report_path.exists() or raw_root.exists():
        parser.error('Preserve the existing run; choose a fresh report path')
    report_path.parent.mkdir(parents=True, exist_ok=True)
    raw_root.mkdir()
    results = []
    previous_terminal = None
    try:
        for index, test_filter in enumerate(args.filters):
            raw_dir = raw_root / f'{index:03d}'
            raw_dir.mkdir()
            (raw_dir / 'selection.json').write_text(json.dumps({
                'filter': test_filter, 'filterType': args.filter_type, 'includeExplicit': args.include_explicit,
                'timeoutSeconds': args.timeout_seconds}, ensure_ascii=False, indent=2), encoding='utf-8')
            try:
                result = run_filter(test_filter, args.include_explicit, args.filter_type, args.timeout_seconds, raw_dir,
                                    desktop_name=args.desktop_name, expected_editor_pid=args.expected_editor_pid,
                                    previous_terminal=previous_terminal)
                results.append(result)
                previous_terminal = capture_terminal_proof(raw_dir, result)
            except BaseException as error:
                (raw_dir / 'observation-error.json').write_text(json.dumps({'error': repr(error),
                    'automaticRetry': False, 'unityCancellationWasNotRequested': True}, ensure_ascii=False, indent=2), encoding='utf-8')
                raise
    finally:
        report_path.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    raise SystemExit(0 if all(row['cases'] and not row['failures'] for row in results) else 3)
