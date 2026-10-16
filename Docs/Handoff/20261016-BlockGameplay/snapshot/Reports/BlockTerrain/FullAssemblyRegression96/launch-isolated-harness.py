"""Prepare-only by default; run one existing acceptance harness on a named desktop."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import json
import msvcrt
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys
import threading
import traceback
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
ROOT = Path('D:/XPHUNITY/ProjectSplatoon').resolve()
HARNESS = {kind: ROOT / 'Tools/BlockTerrain' / (kind + '_acceptance.py') for kind in ('network', 'performance')}
spec = importlib.util.spec_from_file_location('desktop_probe_support', HERE / 'desktop-inheritance-probe.py')
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
USER, KERNEL = native.USER, native.KERNEL
USER.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
USER.OpenInputDesktop.restype = wintypes.HANDLE
CALLBACK = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
USER.EnumDesktopWindows.argtypes = [wintypes.HANDLE, CALLBACK, wintypes.LPARAM]
USER.EnumDesktopWindows.restype = wintypes.BOOL
USER.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER.GetWindowThreadProcessId.restype = wintypes.DWORD
KERNEL.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
KERNEL.OpenProcess.restype = wintypes.HANDLE
KERNEL.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
KERNEL.QueryFullProcessImageNameW.restype = wintypes.BOOL
KERNEL.SetStdHandle.argtypes = [wintypes.DWORD, wintypes.HANDLE]
KERNEL.SetStdHandle.restype = wintypes.BOOL
KERNEL.GetStdHandle.argtypes = [wintypes.DWORD]
KERNEL.GetStdHandle.restype = wintypes.HANDLE

def utc():
    return datetime.now(timezone.utc).isoformat()

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def desktop_name(handle):
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    needed = wintypes.DWORD()
    USER.GetUserObjectInformationW(handle, 2, None, 0, ctypes.byref(needed))
    if not needed.value:
        raise ctypes.WinError(ctypes.get_last_error())
    name = ctypes.create_unicode_buffer(needed.value // ctypes.sizeof(ctypes.c_wchar) + 1)
    if not USER.GetUserObjectInformationW(handle, 2, name, ctypes.sizeof(name), ctypes.byref(needed)):
        raise ctypes.WinError(ctypes.get_last_error())
    return name.value

def input_desktop_name():
    handle = USER.OpenInputDesktop(0, False, 0x0041)
    try:
        return desktop_name(handle)
    finally:
        if handle:
            USER.CloseDesktop(handle)

def validate_desktop_name(name):
    if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', name):
        raise ValueError('Invalid desktopName')
    if name.casefold() in ('default', 'winlogon'):
        raise ValueError('Reserved user/system desktops are forbidden for test launches')

def require_inactive_desktop(expected):
    validate_desktop_name(expected)
    active = input_desktop_name()  # Any native read error propagates; never fall back.
    if expected.casefold() == active.casefold():
        raise RuntimeError('Requested desktop is the active input desktop; refusing launch')
    return active

def argument_value(arguments, key):
    if arguments.count(key) != 1:
        raise ValueError('Exactly one separate ' + key + ' argument is required')
    index = arguments.index(key)
    if index + 1 == len(arguments):
        raise ValueError('Missing ' + key + ' value')
    return arguments[index + 1]

def request(path, require_player=False):
    data = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    if data.get('kind') not in HARNESS:
        raise ValueError('Only the existing network/performance acceptance harnesses are supported')
    validate_desktop_name(data.get('desktopName'))
    arguments = data.get('harnessArguments')
    if not isinstance(arguments, list) or not all(isinstance(part, str) and '\0' not in part for part in arguments):
        raise ValueError('harnessArguments must be an array of literal argument strings')
    report = Path(data['reportRoot']).resolve()
    report.relative_to(ROOT / 'Reports')
    if Path(argument_value(arguments, '--output')).resolve() != report:
        raise ValueError('The original harness --output must identify reportRoot')
    player = Path(argument_value(arguments, '--player')).resolve(strict=require_player)
    player.relative_to(ROOT / 'Builds')
    if player.suffix.lower() != '.exe':
        raise ValueError('The package Player must be an executable under this project Builds directory')
    harness = HARNESS[data['kind']]
    return {'kind': data['kind'], 'desktopName': data['desktopName'], 'reportRoot': str(report),
            'harnessPath': str(harness), 'harnessArguments': arguments,
            'playerPath': str(player), 'harnessSha256': digest(harness)}

def player_image(process_id):
    process = KERNEL.OpenProcess(0x1000, False, process_id)  # QUERY_LIMITED_INFORMATION only.
    if not process:
        return None, ctypes.get_last_error()
    try:
        capacity = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(capacity.value)
        if not KERNEL.QueryFullProcessImageNameW(process, 0, buffer, ctypes.byref(capacity)):
            return None, ctypes.get_last_error()
        return os.path.normcase(buffer.value), None
    finally:
        KERNEL.CloseHandle(process)

def desktop_windows(handle):
    windows = []
    @CALLBACK
    def collect(window, parameter):
        owner = wintypes.DWORD()
        thread = USER.GetWindowThreadProcessId(window, ctypes.byref(owner))
        windows.append((owner.value, thread))
        return True
    if not USER.EnumDesktopWindows(handle, collect, 0):
        raise ctypes.WinError(ctypes.get_last_error())
    return windows

def audit_player_windows(expected, player):
    isolated = active = None
    try:
        isolated = USER.OpenDesktopW(expected, 0, False, 0x0041)
        isolated_name = desktop_name(isolated)
        active = USER.OpenInputDesktop(0, False, 0x0041)
        active_name = desktop_name(active)
        rows, unknown = [], []
        wanted = os.path.normcase(player)
        for name, handle in ((isolated_name, isolated), (active_name, active)):
            windows = desktop_windows(handle)
            for process_id in sorted({pid for pid, tid in windows}):
                image, error = player_image(process_id)
                if error is not None:
                    unknown.append({'pid': process_id, 'imageReadError': error})
                elif image == wanted:
                    owned = [(pid, tid) for pid, tid in windows if pid == process_id]
                    rows.append({'pid': process_id, 'desktopName': name, 'windows': len(owned),
                                 'windowThreads': len({tid for pid, tid in owned}), 'onCurrentInputDesktop': name == active_name})
        return {'observedUtc': utc(), 'source': 'EnumDesktopWindows + exact Player executable via QueryFullProcessImageNameW',
                'expectedDesktopName': expected, 'currentInputDesktopName': active_name,
                'differentFromInputDesktop': expected.casefold() != active_name.casefold(), 'playerWindows': rows,
                'unclassifiedWindowOwners': unknown}
    finally:
        if active:
            USER.CloseDesktop(active)
        if isolated:
            USER.CloseDesktop(isolated)

class WindowAudit:
    def __init__(self, data, metadata):
        self.data, self.metadata = data, metadata
        self.stop = threading.Event()
        self.samples = self.player_samples = self.errors = self.unknown = 0
        self.on_input = False
        self.fatal_error = None
        self.thread = threading.Thread(target=self.run, name='ReadonlyPlayerDesktopAudit', daemon=True)
    def run(self):
        try:
            with (self.metadata / 'player-window-desktops.ndjson').open('w', encoding='utf-8', buffering=1) as stream:
                while True:
                    try:
                        row = audit_player_windows(self.data['desktopName'], self.data['playerPath'])
                        self.samples += 1
                        self.player_samples += bool(row['playerWindows'])
                        self.unknown += bool(row['unclassifiedWindowOwners'])
                        self.on_input |= any(window['onCurrentInputDesktop'] for window in row['playerWindows'])
                    except Exception as error:
                        self.errors += 1
                        row = {'observedUtc': utc(), 'readError': str(error), 'winerror': getattr(error, 'winerror', None)}
                    stream.write(json.dumps(row, ensure_ascii=False) + '\n')
                    if self.stop.wait(2):
                        break
        except Exception as error:
            self.errors += 1
            self.fatal_error = {'type': type(error).__name__, 'message': str(error)}
            traceback.print_exc(file=sys.stderr)
    def finish(self):
        self.stop.set()
        self.thread.join(timeout=10)
        return {'samplePeriodSeconds': 2, 'samples': self.samples, 'samplesWithPlayerWindows': self.player_samples,
                'readErrors': self.errors, 'samplesWithUnclassifiedWindowOwners': self.unknown,
                'observedPlayerWindowOnInputDesktop': self.on_input, 'auditThreadFinished': not self.thread.is_alive(),
                'fatalAuditError': self.fatal_error,
                'scope': 'sampled desktop/window ownership only; no rendering, focus, frame-time, or gameplay acceptance'}

def bootstrap(frozen):
    data = json.loads(frozen.read_text(encoding='utf-8'))
    validate_desktop_name(data.get('desktopName'))
    metadata = Path(data['reportRoot']) / '_desktop-launch'
    record = {'startedUtc': utc(), 'pid': os.getpid(), 'harnessExitCode': None, 'harnessArguments': data['harnessArguments']}
    previous_streams, previous_argv = (sys.stdin, sys.stdout, sys.stderr), sys.argv
    previous_path = sys.path[:]
    std_ids = (0xFFFFFFF6, 0xFFFFFFF5, 0xFFFFFFF4)  # STD_INPUT/OUTPUT/ERROR_HANDLE.
    previous_handles = [KERNEL.GetStdHandle(number) for number in std_ids]
    audit = None
    exit_code = 1
    with open(os.devnull, 'r') as stdin, (metadata / 'harness.stdout.log').open('w', encoding='utf-8', buffering=1) as stdout, \
            (metadata / 'harness.stderr.log').open('w', encoding='utf-8', buffering=1) as stderr:
        try:
            sys.stdin, sys.stdout, sys.stderr = stdin, stdout, stderr
            for number, stream in zip(std_ids, (stdin, stdout, stderr)):
                if not KERNEL.SetStdHandle(number, msvcrt.get_osfhandle(stream.fileno())):
                    raise ctypes.WinError(ctypes.get_last_error())
            observation = native.own_desktop()
            current_input = require_inactive_desktop(data['desktopName'])
            record.update(ownDesktop=observation, inputDesktopName=current_input,
                          differentFromInputDesktop=observation['desktopName'].casefold() != current_input.casefold())
            if observation['desktopName'].casefold() != data['desktopName'].casefold() or observation['desktopName'].casefold() == current_input.casefold():
                raise RuntimeError('Bootstrap is not on the requested inactive desktop; no harness was invoked')
            validated = request(frozen, require_player=True)
            if any(data.get(key) != value for key, value in validated.items()):
                raise RuntimeError('The frozen request identity changed; no harness was invoked')
            if frozen != (metadata / 'request.json').resolve():
                raise RuntimeError('Bootstrap request must be the frozen metadata request; no harness was invoked')
            harness = HARNESS[data['kind']]
            if str(harness) != data['harnessPath'] or digest(harness) != data['harnessSha256']:
                raise RuntimeError('The frozen existing harness identity changed; no harness was invoked')
            if not Path(data['playerPath']).is_file():
                raise RuntimeError('The requested built Player is missing; no harness was invoked')
            record['playerExeSha256'] = digest(data['playerPath'])
            save(metadata / 'bootstrap.json', record)
            audit = WindowAudit(data, metadata)
            audit.thread.start()
            sys.argv = [str(harness), *data['harnessArguments']]
            # Match direct script execution so performance_acceptance can import its
            # existing sibling network_acceptance module without copying either source.
            sys.path.insert(0, str(harness.parent))
            try:
                runpy.run_path(str(harness), run_name='__main__')
                exit_code = 0
            except SystemExit as result:
                exit_code = result.code if isinstance(result.code, int) else 0 if result.code is None else 1
                if result.code is not None and not isinstance(result.code, int):
                    print(result.code, file=sys.stderr)
            record['harnessExitCode'] = exit_code
        except BaseException as error:
            record['bootstrapError'] = {'type': type(error).__name__, 'message': str(error)}
            traceback.print_exc(file=sys.stderr)
            exit_code = 1
        finally:
            if audit is not None:
                record['windowAudit'] = audit.finish()
            record.update(finishedUtc=utc(), bootstrapExitCode=exit_code)
            save(metadata / 'bootstrap.json', record)
            sys.argv = previous_argv
            sys.path[:] = previous_path
            sys.stdin, sys.stdout, sys.stderr = previous_streams
            for number, handle in zip(std_ids, previous_handles):
                KERNEL.SetStdHandle(number, handle)
    return exit_code

def launch(data):
    validate_desktop_name(data.get('desktopName'))
    report = Path(data['reportRoot'])
    if report.exists():
        raise ValueError('A fresh reportRoot is required; preserve the existing result directory')
    pythonw = Path(sys.executable).with_name('pythonw.exe')
    if not pythonw.is_file():
        raise RuntimeError('Local pythonw.exe is required to keep the bootstrap console off the user desktop')
    desktop = USER.OpenDesktopW(data['desktopName'], 0, False, 0x00CF)
    if not desktop:
        raise ctypes.WinError(ctypes.get_last_error())
    process = native.PROCESS_INFORMATION()
    metadata = report / '_desktop-launch'
    receipt = {'startedUtc': utc(), 'launcherPid': os.getpid(), 'bootstrapPid': None,
               'desktopName': data['desktopName'], 'reportRoot': str(report), 'nativeExitCode': None}
    try:
        metadata.mkdir(parents=True)
        frozen = metadata / 'request.json'
        save(frozen, data)
        startup = native.STARTUPINFOW()
        startup.cb, startup.lpDesktop = ctypes.sizeof(startup), 'winsta0\\' + data['desktopName']
        argv = [str(pythonw), '-B', str(Path(__file__).resolve()), '--bootstrap', str(frozen)]
        command = ctypes.create_unicode_buffer(subprocess.list2cmdline(argv))
        KERNEL.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p, ctypes.c_void_p,
                                         wintypes.BOOL, wintypes.DWORD, ctypes.c_void_p, wintypes.LPCWSTR,
                                         ctypes.POINTER(native.STARTUPINFOW), ctypes.POINTER(native.PROCESS_INFORMATION)]
        KERNEL.CreateProcessW.restype = wintypes.BOOL
        KERNEL.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        KERNEL.WaitForSingleObject.restype = wintypes.DWORD
        KERNEL.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        KERNEL.GetExitCodeProcess.restype = wintypes.BOOL
        receipt['inputDesktopNameBeforeLaunch'] = require_inactive_desktop(data['desktopName'])
        if not KERNEL.CreateProcessW(str(pythonw), command, None, None, False, 0, None, str(ROOT),
                                     ctypes.byref(startup), ctypes.byref(process)):
            raise ctypes.WinError(ctypes.get_last_error())
        receipt['bootstrapPid'] = process.dwProcessId
        receipt['bootstrapArgv'] = argv
        save(metadata / 'native-launch.json', receipt)
        print(json.dumps(receipt, ensure_ascii=False), flush=True)
        # Keep the desktop handle alive. Bounded individual waits permit normal tool polling.
        while True:
            waited = KERNEL.WaitForSingleObject(process.hProcess, 1000)
            if waited == 0:
                break
            if waited != 258:
                raise ctypes.WinError(ctypes.get_last_error())
        code = wintypes.DWORD()
        if not KERNEL.GetExitCodeProcess(process.hProcess, ctypes.byref(code)):
            raise ctypes.WinError(ctypes.get_last_error())
        receipt.update(finishedUtc=utc(), nativeExitCode=code.value)
        save(metadata / 'native-launch.json', receipt)
        print(json.dumps(receipt, ensure_ascii=False), flush=True)
        return code.value
    except BaseException as error:
        receipt.update(observationError={'type': type(error).__name__, 'message': str(error)}, finishedUtc=utc())
        if metadata.is_dir():
            save(metadata / 'native-launch.json', receipt)
        raise
    finally:
        if process.hThread:
            KERNEL.CloseHandle(process.hThread)
        if process.hProcess:
            KERNEL.CloseHandle(process.hProcess)
        USER.CloseDesktop(desktop)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument('--request', type=Path, help='Structured request JSON; preparation only unless --execute is supplied')
    selection.add_argument('--bootstrap', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--execute', action='store_true')
    options = parser.parse_args()
    if options.bootstrap:
        if options.execute:
            parser.error('Bootstrap does not accept --execute')
        raise SystemExit(bootstrap(options.bootstrap.resolve()))
    data = request(options.request, require_player=options.execute)
    if not options.execute:
        print(json.dumps({'preparedOnly': True, **data}, ensure_ascii=False, indent=2))
    else:
        raise SystemExit(launch(data))
