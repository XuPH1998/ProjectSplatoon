"""One full Assembly run; preserve 19 Editor preferences and five exact output files.

Prepared only. Never starts/stops Editor or Player and never retries a test.
If observation ends while Unity may still be testing, retain pending-restoration
evidence. Run --restore-from only after the owner has ended that active run.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
RUNS = HERE / 'Runs'
CLI = Path('C:/Users/wepie/AppData/Local/Unity/bin/unity.exe')
RUNNER = HERE / 'run-editor-filters.py'
GUARD = HERE / 'FullRegressionPreferenceGuard.cs'
REQUEST = ROOT / 'Temp/pipeline_test_request.json'
STATUS = ROOT / 'Temp/pipeline_test_status.json'
TARGETS = {
    'preference.json': ROOT / 'Temp/PlayerNames/preference.json',
    **{name: ROOT / 'Reports/PlayerNames/Visual' / name for name in
       ('lobby.png', 'scoreboard.png', 'remote-name.png', 'swim-name-hidden.png')},
}


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def save_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def copy_targets(directory):
    directory.mkdir()
    rows = {}
    for name, target in TARGETS.items():
        exists = target.exists()
        data = target.read_bytes() if exists else None
        if exists:
            (directory / name).write_bytes(data)
        rows[name] = {'exists': exists, 'sha256': digest(data) if exists else None,
                      'bytes': len(data) if exists else None,
                      'target': str(target.relative_to(ROOT)).replace('\\', '/')}
    save_json(directory / 'manifest.json', rows)
    return rows


def restore_targets(before_directory, baseline):
    # Verify every saved input before mutating any live target.
    for name, target in TARGETS.items():
        row = baseline[name]
        if row['target'] != str(target.relative_to(ROOT)).replace('\\', '/'):
            raise RuntimeError('Unexpected target in file baseline: ' + name)
        if row['exists'] and digest((before_directory / name).read_bytes()) != row['sha256']:
            raise RuntimeError('Preserved original bytes changed: ' + name)
    for name, target in TARGETS.items():
        if baseline[name]['exists']:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((before_directory / name).read_bytes())
        elif target.exists():
            # Delete only this known output, after retaining its new bytes in Reports.
            target.unlink()
    return {name: target.exists() == baseline[name]['exists'] and
            (not target.exists() or digest(target.read_bytes()) == baseline[name]['sha256'])
            for name, target in TARGETS.items()}


def command(log_directory, label, arguments, timeout=60):
    # argv contains actual JSON newlines/characters; no shell interpolation is used.
    try:
        call = subprocess.run(arguments, cwd=ROOT, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        (log_directory / (label + '.stdout')).write_bytes(error.stdout or b'')
        (log_directory / (label + '.stderr')).write_bytes(error.stderr or b'')
        save_json(log_directory / (label + '.command.json'),
                  {'argv': arguments, 'timeoutSeconds': timeout, 'returncode': None,
                   'observationTimedOut': True, 'unityCancellationWasNotRequested': True})
        raise
    (log_directory / (label + '.stdout')).write_bytes(call.stdout)
    (log_directory / (label + '.stderr')).write_bytes(call.stderr)
    save_json(log_directory / (label + '.command.json'), {'argv': arguments, 'returncode': call.returncode})
    if call.returncode:
        raise RuntimeError(label + ' failed; retain its raw command output')
    envelope = json.loads(call.stdout.decode('utf-8-sig'))
    if envelope.get('success') is not True:
        raise RuntimeError(label + ' reported failure; retain its raw command output')
    payload = envelope.get('data', envelope)
    if not isinstance(payload, dict) or payload.get('success', True) is not True:
        raise RuntimeError(label + ' returned an unsuccessful command payload')
    if 'data' in envelope:
        # CLI command envelope: data contains command/parameters/result/target.
        payload = payload.get('result', payload)
    if not isinstance(payload, dict) or payload.get('success', True) is not True:
        raise RuntimeError(label + ' returned a failed inner Pipeline response')
    return payload


def cli_command(name, *arguments):
    return [str(CLI), 'command', name, '--project-path', str(ROOT),
            '--caller', 'plugin', '--skill', 'unity-cli', '--format', 'json', '--', *arguments]


def script(log_directory, label, method, *arguments):
    response = command(log_directory, label, cli_command('run_script', '--file', str(GUARD),
        '--entry', 'Splatoon.ReportGuards.FullRegressionPreferenceGuard.' + method,
        '--args', json.dumps(list(arguments), ensure_ascii=False), '--mode', 'ephemeral',
        '--timeout_ms', '30000'))
    if response.get('success') is not True or not isinstance(response.get('result'), dict):
        raise RuntimeError(label + ' did not return the reviewed RunScriptResponse schema')
    return response['result']


def offline_idle(require_fresh_completed_since=None):
    if REQUEST.exists():
        return False, 'Pipeline request is still present'
    if STATUS.exists():
        try:
            state = json.loads(STATUS.read_text(encoding='utf-8-sig'))
            if require_fresh_completed_since is not None:
                if STATUS.stat().st_mtime < require_fresh_completed_since or state.get('status') != 'completed':
                    return False, 'No fresh completed status for this invocation'
            elif state.get('status') not in ('completed', 'error', 'cancelled'):
                return False, 'Pipeline status is not terminal'
        except (OSError, ValueError):
            return False, 'Pipeline status cannot be verified'
    elif require_fresh_completed_since is not None:
        return False, 'Pipeline completed status is unavailable'
    return True, None


def preflight(log_directory):
    idle, reason = offline_idle()
    if not idle:
        raise RuntimeError(reason)
    # Read-only process inventory. No name-based or path-based process termination.
    ps = r'''
$ErrorActionPreference = 'Stop'
$taskRoot = '%ROOT%'
$taskPid = %PID%
$blocking = @()
foreach ($process in Get-CimInstance Win32_Process -ErrorAction Stop) {
    if ($process.ProcessId -eq $taskPid -or $process.ProcessId -eq $PID) { continue }
    $exe = [string]$process.ExecutablePath
    $line = [string]$process.CommandLine
    $name = [string]$process.Name
    $underBuilds = $exe.StartsWith(($taskRoot + '\Builds\'), [StringComparison]::OrdinalIgnoreCase)
    $knownPlayer = $name -ieq 'InkLan.exe'
    $underProjectPlayer = $exe.StartsWith(($taskRoot + '\'), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath ([IO.Path]::Combine([IO.Path]::GetDirectoryName($exe), ([IO.Path]::GetFileNameWithoutExtension($exe) + '_Data'))))
    $runner = ($name -match '^python.*\.exe$' -and $line -match '(run-editor-filters|run-font14-regression|run-full-regression-preserved)\.py') -or
              ($name -ieq 'unity.exe' -and $line -match '\brun_tests\b')
    if ($underBuilds -or $knownPlayer -or $underProjectPlayer -or $runner) {
        $blocking += [pscustomobject]@{ pid=$process.ProcessId; name=$name; executable=$exe; commandLine=$line;
            player=($underBuilds -or $knownPlayer -or $underProjectPlayer); runner=$runner }
    }
}
[pscustomobject]@{ success=$true; data=@{ noProjectPlayersOrOtherRunner=($blocking.Count -eq 0); blocking=$blocking } } | ConvertTo-Json -Depth 7
'''.replace('%ROOT%', str(ROOT).replace("'", "''")).replace('%PID%', str(os.getpid()))
    processes = command(log_directory, 'process-preflight',
        ['powershell', '-NoProfile', '-NonInteractive', '-Command', ps])
    if processes.get('noProjectPlayersOrOtherRunner') is not True:
        raise RuntimeError('A project Player or another test runner is running; no process was stopped')
    status = command(log_directory, 'editor-preflight', cli_command('editor_status'))
    if status.get('success') is True:
        status = status.get('result')
    if not isinstance(status, dict) or status.get('status') != 'ready' or status.get('compiling') is not False or \
            status.get('domainReloadInProgress') is not False or status.get('playMode') != 'stopped' or \
            Path(status.get('projectPath', '')).resolve() != ROOT.resolve():
        raise RuntimeError('Connected Editor is not verified ready for this project')


def restore(out, manifest, *, recovery=False):
    log_directory = out / ('Recovery-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
                           if recovery else 'Restoration')
    log_directory.mkdir()
    if recovery:
        preflight(log_directory)
    after = copy_targets(log_directory / 'FilesAfter')
    preferences = script(log_directory, 'preferences-restore', 'Restore', str(ROOT),
                         str(out / 'preferences-before.json'), str(log_directory / 'Preferences'))
    if not isinstance(preferences, dict) or preferences.get('restored') is not True:
        raise RuntimeError('Preference guard did not verify exact restoration')
    files = restore_targets(out / 'FilesBefore', manifest['filesBefore'])
    save_json(log_directory / 'file-verification.json', files)
    manifest['filesAfter'] = after
    manifest['filesRestored'] = files
    manifest['preferencesRestored'] = preferences
    manifest['restored'] = all(files.values())
    manifest['pendingRestoration'] = not manifest['restored']
    manifest['restorationEvidence'] = str(log_directory.relative_to(out)).replace('\\', '/')
    if not manifest['restored']:
        raise RuntimeError('Exact file restoration verification failed')


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--attempt', help='A fresh lowercase run name; exactly one Assembly execution')
    group.add_argument('--restore-from', help='Existing pending run directory; restore only, never rerun tests')
    args = parser.parse_args()
    if args.restore_from:
        out = Path(args.restore_from).resolve()
        if out.parent != RUNS.resolve():
            parser.error('--restore-from must identify one direct child of this prepared Runs directory')
        manifest = json.loads((out / 'preservation.json').read_text(encoding='utf-8'))
        if manifest.get('restored'):
            parser.error('This run was already restored; preserve its evidence')
        try:
            restore(out, manifest, recovery=True)
            return 0
        finally:
            manifest['recoveryFinishedUtc'] = utc()
            save_json(out / 'preservation.json', manifest)
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,47}', args.attempt):
        parser.error('--attempt must be a short lowercase slug')
    out = RUNS / args.attempt
    RUNS.mkdir(exist_ok=True)
    if out.exists():
        parser.error('Preserve the existing run; use a fresh attempt')
    out.mkdir()
    manifest = {'startedUtc': utc(), 'project': str(ROOT), 'assembly': 'Splatoon.Tests',
                'filterType': 'assembly', 'includeExplicit': False, 'timeoutSeconds': 7200,
                'restored': False, 'pendingRestoration': False, 'runnerAttempted': False}
    isolated = False
    code = 1
    submitted = None
    try:
        preflight(out)
        manifest['filesBefore'] = copy_targets(out / 'FilesBefore')
        before = script(out, 'preferences-snapshot', 'Snapshot', str(ROOT), str(out / 'preferences-before.json'))
        if not isinstance(before, dict) or before.get('saved') is not True or before.get('preferences') != 19:
            raise RuntimeError('Preference baseline was not verified as the 19 reviewed keys')
        manifest['preferenceSnapshot'] = before
        save_json(out / 'preservation.json', manifest)
        # Isolate only the old fixture backup. Fresh test cleanup cannot consume old user state.
        isolated = True
        if TARGETS['preference.json'].exists():
            TARGETS['preference.json'].unlink()
        submitted = time.time()
        argv = [sys.executable, str(RUNNER), '--report', str(out / 'editor-results.json'),
                '--filter-type', 'assembly', '--timeout-seconds', '7200', 'Splatoon.Tests']
        manifest['runnerAttempted'] = True
        manifest['submittedEpoch'] = submitted
        manifest['runnerArgv'] = argv
        save_json(out / 'preservation.json', manifest)
        with (out / 'runner.stdout').open('xb') as stdout, (out / 'runner.stderr').open('xb') as stderr:
            # One invocation; no Editor/CLI commands inside this interval.
            process = subprocess.Popen(argv, cwd=ROOT, stdout=stdout, stderr=stderr)
            code = process.wait()
        manifest['runnerExitCode'] = code
    except BaseException as error:
        manifest['wrapperError'] = repr(error)
        code = 130 if isinstance(error, KeyboardInterrupt) else 1
    finally:
        for label, path in (('status', STATUS), ('request', REQUEST)):
            try:
                if path.exists():
                    (out / ('pipeline-after-observation.' + label + '.json')).write_bytes(path.read_bytes())
            except OSError as error:
                manifest['pipelineEvidenceReadError-' + label] = repr(error)
        if isolated:
            try:
                idle, reason = offline_idle(submitted if manifest['runnerAttempted'] else None)
                # completed is emitted just before request/collector cleanup; observe only files for up to 30s.
                deadline = time.monotonic() + 30
                while not idle and REQUEST.exists() and time.monotonic() < deadline:
                    time.sleep(0.25)
                    idle, reason = offline_idle(submitted if manifest['runnerAttempted'] else None)
                if idle:
                    restore(out, manifest)
                else:
                    manifest['pendingRestoration'] = True
                    manifest['restorationDeferredReason'] = reason
                    copy_targets(out / 'FilesAfterPending')
                    # Outer observation timeout never proves cancellation. Do not race the active fixture.
                    code = 5
            except BaseException as error:
                manifest['restorationError'] = repr(error)
                manifest['pendingRestoration'] = True
                code = 4
        manifest['finishedUtc'] = utc()
        save_json(out / 'preservation.json', manifest)
        print(json.dumps({'run': str(out), 'runnerExitCode': manifest.get('runnerExitCode'),
              'restored': manifest['restored'], 'pendingRestoration': manifest['pendingRestoration'],
              'wrapperError': manifest.get('wrapperError'), 'restorationError': manifest.get('restorationError')}, ensure_ascii=False))
    return code


if __name__ == '__main__':
    raise SystemExit(main())
