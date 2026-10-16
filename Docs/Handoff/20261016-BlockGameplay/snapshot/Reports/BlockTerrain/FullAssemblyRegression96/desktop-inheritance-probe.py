"""Reports-only Python desktop inheritance probe; no UI, Unity, or Player launch."""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
USER = ctypes.WinDLL('user32', use_last_error=True)
KERNEL = ctypes.WinDLL('kernel32', use_last_error=True)
USER.GetThreadDesktop.argtypes = [wintypes.DWORD]
USER.GetThreadDesktop.restype = wintypes.HANDLE
USER.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
USER.GetUserObjectInformationW.restype = wintypes.BOOL
USER.OpenDesktopW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
USER.OpenDesktopW.restype = wintypes.HANDLE
USER.CloseDesktop.argtypes = [wintypes.HANDLE]
USER.CloseDesktop.restype = wintypes.BOOL
KERNEL.GetCurrentThreadId.restype = wintypes.DWORD
KERNEL.CloseHandle.argtypes = [wintypes.HANDLE]
KERNEL.CloseHandle.restype = wintypes.BOOL

def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def own_desktop():
    thread = KERNEL.GetCurrentThreadId()
    handle = USER.GetThreadDesktop(thread)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    needed = wintypes.DWORD()
    USER.GetUserObjectInformationW(handle, 2, None, 0, ctypes.byref(needed))
    if not needed.value:
        raise ctypes.WinError(ctypes.get_last_error())
    name = ctypes.create_unicode_buffer(needed.value // ctypes.sizeof(ctypes.c_wchar) + 1)
    if not USER.GetUserObjectInformationW(handle, 2, name, ctypes.sizeof(name), ctypes.byref(needed)):
        raise ctypes.WinError(ctypes.get_last_error())
    return {'pid': os.getpid(), 'threadId': thread, 'desktopName': name.value,
            'source': 'own GetThreadDesktop(GetCurrentThreadId) + GetUserObjectInformationW(UOI_NAME)'}

def parent(output, desktop_name):
    result = {'observedUtc': datetime.now(timezone.utc).isoformat(), 'parent': own_desktop(),
              'requestedDesktopName': desktop_name, 'cases': []}
    save(output / 'parent.json', result)

    def child_case(mode, inherited_desktop=None):
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 1  # Same SW_SHOWNORMAL as current rendered Player harness.
        if inherited_desktop is not None:
            startup.lpAttributeList = {'handle_list': [int(inherited_desktop)]}
        destination = output / ('child-' + mode + '.json')
        row = {'mode': mode, 'closeFdsArgument': 'omitted: Python default', 'showWindow': 1,
               'explicitInheritableDesktopHandle': inherited_desktop is not None,
               'pythonSubsystem': 'pythonw: no console/UI', 'stdoutAndStderr': 'DEVNULL'}
        child = None
        try:
            # No lpDesktop fake field: Python STARTUPINFO has no such supported field.
            # pythonw is used solely to prevent a probe console from disturbing the user.
            child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--child',
                                      '--output', str(destination)], cwd=HERE,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                     startupinfo=startup)
            row['pid'] = child.pid
            row['nativeExitCode'] = child.wait(timeout=8)
            if destination.exists():
                row['observation'] = json.loads(destination.read_text(encoding='utf-8'))
        except Exception as error:
            row['error'] = {'type': type(error).__name__, 'message': str(error), 'winerror': getattr(error, 'winerror', None)}
            if child is not None and child.poll() is None:
                # Only the tiny probe child we just created, never Unity or a Player.
                child.kill()
                row['nativeExitCode'] = child.wait(timeout=5)
        result['cases'].append(row)
        save(output / 'parent.json', result)
        return row

    baseline = child_case('default-popen')
    child_name = baseline.get('observation', {}).get('desktopName')
    result['fallbackRequired'] = child_name == 'Default' and result['parent']['desktopName'] != 'Default'
    if result['fallbackRequired']:
        # fInherit=True; read/create/enumerate only, without desktop-switch access.
        inherited = USER.OpenDesktopW(desktop_name, 0, True, 0x00CF)
        if not inherited:
            result['fallbackOpenError'] = ctypes.get_last_error()
        else:
            try:
                child_case('inheritable-desktop-handle', inherited)
            finally:
                USER.CloseDesktop(inherited)
    save(output / 'parent.json', result)

class STARTUPINFOW(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('lpReserved', wintypes.LPWSTR), ('lpDesktop', wintypes.LPWSTR),
                ('lpTitle', wintypes.LPWSTR), ('dwX', wintypes.DWORD), ('dwY', wintypes.DWORD),
                ('dwXSize', wintypes.DWORD), ('dwYSize', wintypes.DWORD), ('dwXCountChars', wintypes.DWORD),
                ('dwYCountChars', wintypes.DWORD), ('dwFillAttribute', wintypes.DWORD), ('dwFlags', wintypes.DWORD),
                ('wShowWindow', wintypes.WORD), ('cbReserved2', wintypes.WORD), ('lpReserved2', ctypes.c_void_p),
                ('hStdInput', wintypes.HANDLE), ('hStdOutput', wintypes.HANDLE), ('hStdError', wintypes.HANDLE)]

class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [('hProcess', wintypes.HANDLE), ('hThread', wintypes.HANDLE),
                ('dwProcessId', wintypes.DWORD), ('dwThreadId', wintypes.DWORD)]

def launch(output, desktop_name):
    output = output.resolve()
    pythonw = Path(sys.executable).with_name('pythonw.exe')
    if not pythonw.is_file():
        raise RuntimeError('A no-console pythonw executable is required for this probe')
    output.mkdir()
    desktop = USER.OpenDesktopW(desktop_name, 0, False, 0x00CF)
    if not desktop:
        raise ctypes.WinError(ctypes.get_last_error())
    process = PROCESS_INFORMATION()
    startup = STARTUPINFOW()
    startup.cb = ctypes.sizeof(startup)
    startup.lpDesktop = 'winsta0\\' + desktop_name
    command = ctypes.create_unicode_buffer(subprocess.list2cmdline(
        [str(pythonw), str(Path(__file__).resolve()), '--parent', '--desktop', desktop_name, '--output', str(output)]))
    KERNEL.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p, ctypes.c_void_p,
                                     wintypes.BOOL, wintypes.DWORD, ctypes.c_void_p, wintypes.LPCWSTR,
                                     ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]
    KERNEL.CreateProcessW.restype = wintypes.BOOL
    KERNEL.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    KERNEL.WaitForSingleObject.restype = wintypes.DWORD
    KERNEL.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    KERNEL.GetExitCodeProcess.restype = wintypes.BOOL
    receipt = {'launcher': own_desktop(), 'requestedDesktopName': desktop_name,
               'launchApi': 'CreateProcessW with STARTUPINFOW.lpDesktop', 'pythonSubsystem': 'pythonw: no console/UI'}
    try:
        if not KERNEL.CreateProcessW(str(pythonw), command, None, None, False, 0, None, str(HERE),
                                     ctypes.byref(startup), ctypes.byref(process)):
            raise ctypes.WinError(ctypes.get_last_error())
        receipt['parentPid'] = process.dwProcessId
        save(output / 'native-launch.json', receipt)
        wait = KERNEL.WaitForSingleObject(process.hProcess, 30000)
        receipt['nativeWaitResult'] = wait
        if wait != 0:
            raise RuntimeError('The tiny parent probe did not terminate within 30 seconds')
        exit_code = wintypes.DWORD()
        if not KERNEL.GetExitCodeProcess(process.hProcess, ctypes.byref(exit_code)):
            raise ctypes.WinError(ctypes.get_last_error())
        receipt['nativeExitCode'] = exit_code.value
        if (output / 'parent.json').exists():
            receipt['parentAndChild'] = json.loads((output / 'parent.json').read_text(encoding='utf-8'))
        save(output / 'native-launch.json', receipt)
        print(json.dumps(receipt, ensure_ascii=False, indent=2))
        return exit_code.value
    finally:
        if process.hThread:
            KERNEL.CloseHandle(process.hThread)
        if process.hProcess:
            KERNEL.CloseHandle(process.hProcess)
        USER.CloseDesktop(desktop)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    role = parser.add_mutually_exclusive_group(required=True)
    role.add_argument('--launch', action='store_true')
    role.add_argument('--parent', action='store_true')
    role.add_argument('--child', action='store_true')
    parser.add_argument('--desktop')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.child:
            save(args.output, own_desktop())
        elif args.parent:
            parent(args.output, args.desktop)
        else:
            raise SystemExit(launch(args.output, args.desktop))
    except Exception as error:
        destination = args.output if args.child else args.output / 'probe-error.json'
        save(destination, {'pid': os.getpid(), 'error': str(error), 'winerror': getattr(error, 'winerror', None)})
        raise
