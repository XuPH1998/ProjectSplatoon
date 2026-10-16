"""Reports-only raw PlayerPrefs preservation; importing/preparing never opens HKCU."""
import base64
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
COMPANY, PRODUCT = 'DefaultCompany', '\u55b7\u58a8\u5bf9\u6218'
SUBKEY = 'Software\\' + COMPANY + '\\' + PRODUCT
KEYS = ['Splatoon.PlayerName', *[key + str(hero) for hero in range(1, 10)
                               for key in ('Loadout.Sub.', 'Loadout.Special.')]]
CONFIG = {'schema': 1, 'companyName': COMPANY, 'productName': PRODUCT,
          'registrySubKey': SUBKEY, 'logicalKeys': KEYS, 'registryViews': [64, 32]}


def physical_name(name):
    # Same mapping as the reviewed Editor guard; enumerate exact names, never guess hashes.
    return next((key for key in KEYS if name.casefold() == key.casefold() or
                 re.fullmatch(re.escape(key) + r'_h[0-9]+', name, re.IGNORECASE)), None)


def save_new(path, data):
    path = Path(path)
    with path.open('x', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())


def validate_request(path, data, require_player=False):
    original = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    if original.get('standalonePreferences') != CONFIG:
        raise ValueError('The reviewed narrow Standalone preference identity is required')
    settings = (ROOT / 'ProjectSettings/ProjectSettings.asset').read_text(encoding='utf-8-sig')
    names = {}
    for field in ('companyName', 'productName'):
        match = re.search(r'^\s*' + field + r':\s*(.+)$', settings, re.MULTILINE)
        if not match:
            raise ValueError('ProjectSettings preference identity is unavailable')
        value = match.group(1).strip()
        names[field] = json.loads(value) if value.startswith('"') else value
    if names != {'companyName': COMPANY, 'productName': PRODUCT}:
        raise ValueError('Current project Company/Product differs from the reviewed preference branch')
    player = Path(data['playerPath'])
    app_info = player.with_name(player.stem + '_Data') / 'app.info'
    identity = {'playerAppInfoPath': str(app_info)}
    if require_player:
        raw = app_info.read_bytes()
        lines = raw.decode('utf-8-sig').splitlines()
        if lines != [COMPANY, PRODUCT]:
            raise ValueError('Actual built Player app.info Company/Product is missing or mismatched')
        identity['playerAppInfoSha256'] = hashlib.sha256(raw).hexdigest()
    return {**data, 'standalonePreferences': CONFIG, **identity}


class RawRegistry:
    """Only nineteen logical preference values; no whole-branch export/overwrite."""
    def __init__(self):
        import winreg
        self.reg = winreg
        self.api = ctypes.WinDLL('advapi32', use_last_error=True)
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.RegEnumValueW.argtypes = [wintypes.HKEY, wintypes.DWORD, wintypes.LPWSTR,
                                          ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
                                          ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
                                          ctypes.POINTER(wintypes.DWORD)]
        self.api.RegEnumValueW.restype = wintypes.LONG
        self.api.RegSetValueExW.argtypes = [wintypes.HKEY, wintypes.LPCWSTR, wintypes.DWORD,
                                           wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
        self.api.RegSetValueExW.restype = wintypes.LONG

    def sid(self):
        token = wintypes.HANDLE()
        self.kernel.GetCurrentProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
        self.api.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                                wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        self.api.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
        self.kernel.LocalFree.argtypes = [ctypes.c_void_p]
        if not self.api.OpenProcessToken(self.kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
            raise ctypes.WinError(ctypes.get_last_error())
        allocated = ctypes.c_void_p()
        try:
            length = wintypes.DWORD()
            self.api.GetTokenInformation(token, 1, None, 0, ctypes.byref(length))
            if ctypes.get_last_error() != 122 or length.value < ctypes.sizeof(ctypes.c_void_p):
                raise RuntimeError('Cannot size the effective process SID')
            buffer = ctypes.create_string_buffer(length.value)
            if not self.api.GetTokenInformation(token, 1, buffer, length, ctypes.byref(length)):
                raise ctypes.WinError(ctypes.get_last_error())
            sid = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_void_p))[0]
            if not self.api.ConvertSidToStringSidW(sid, ctypes.byref(allocated)):
                raise ctypes.WinError(ctypes.get_last_error())
            return ctypes.wstring_at(allocated)
        finally:
            if allocated.value:
                self.kernel.LocalFree(allocated)
            self.kernel.CloseHandle(token)

    def open(self, view, write=False):
        access = self.reg.KEY_READ | (self.reg.KEY_SET_VALUE if write else 0)
        access |= self.reg.KEY_WOW64_64KEY if view == 64 else self.reg.KEY_WOW64_32KEY
        try:
            return self.reg.OpenKey(self.reg.HKEY_CURRENT_USER, SUBKEY, 0, access)
        except FileNotFoundError:
            return None

    def capture_view(self, view):
        handle = self.open(view)
        if handle is None:
            return {'view': view, 'branchExisted': False, 'values': []}
        rows = []
        with handle:
            index = 0
            while True:
                name_size, data_size = 16384, 4096
                while True:
                    name, raw = ctypes.create_unicode_buffer(name_size), ctypes.create_string_buffer(data_size)
                    n, count, kind = wintypes.DWORD(name_size), wintypes.DWORD(data_size), wintypes.DWORD()
                    error = self.api.RegEnumValueW(int(handle), index, name, ctypes.byref(n), None,
                                                  ctypes.byref(kind), raw, ctypes.byref(count))
                    if error != 234:
                        break
                    name_size, data_size = max(name_size * 2, n.value + 1), max(data_size * 2, count.value)
                if error == 259:
                    break
                if error:
                    raise ctypes.WinError(error)
                logical = physical_name(name.value)
                if logical:
                    rows.append({'name': name.value, 'logicalKey': logical, 'type': kind.value,
                                 'rawBase64': base64.b64encode(raw.raw[:count.value]).decode('ascii')})
                index += 1
        return {'view': view, 'branchExisted': True,
                'values': sorted(rows, key=lambda row: row['name'].casefold())}

    def capture(self):
        return [self.capture_view(view) for view in CONFIG['registryViews']]

    def restore(self, baseline):
        for before in baseline:
            view = before['view']
            after = self.capture_view(view)
            if not before['values'] and not after['values']:
                continue
            handle = self.open(view, write=True)
            if handle is None:
                access = self.reg.KEY_SET_VALUE | (self.reg.KEY_WOW64_64KEY if view == 64 else self.reg.KEY_WOW64_32KEY)
                handle = self.reg.CreateKeyEx(self.reg.HKEY_CURRENT_USER, SUBKEY, 0, access)
            with handle:
                wanted = {row['name'].casefold() for row in before['values']}
                for row in after['values']:
                    if row['name'].casefold() not in wanted:
                        self.reg.DeleteValue(handle, row['name'])
                for row in before['values']:
                    raw = base64.b64decode(row['rawBase64'], validate=True)
                    buffer = ctypes.create_string_buffer(raw)
                    error = self.api.RegSetValueExW(int(handle), row['name'], 0, row['type'], buffer, len(raw))
                    if error:
                        raise ctypes.WinError(error)
        # Only remove an originally absent leaf if it is now empty. Other values/subkeys stay untouched.
        for before in baseline:
            if before['branchExisted']:
                continue
            handle = self.open(before['view'])
            if handle is not None:
                with handle:
                    children, values, _ = self.reg.QueryInfoKey(handle)
                if children == values == 0:
                    access = self.reg.KEY_WOW64_64KEY if before['view'] == 64 else self.reg.KEY_WOW64_32KEY
                    self.reg.DeleteKeyEx(self.reg.HKEY_CURRENT_USER, SUBKEY, access, 0)


def candidate_processes(core, player):
    """Read-only inventory; reject same-product Players, including old packages, never stop them."""
    class Entry(ctypes.Structure):
        _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('pid', wintypes.DWORD),
                    ('heap', ctypes.c_size_t), ('module', wintypes.DWORD), ('threads', wintypes.DWORD),
                    ('parent', wintypes.DWORD), ('priority', wintypes.LONG), ('flags', wintypes.DWORD),
                    ('exe', wintypes.WCHAR * 260)]
    kernel = core.KERNEL
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.Process32FirstW.restype = kernel.Process32NextW.restype = wintypes.BOOL
    handle = kernel.CreateToolhelp32Snapshot(2, 0)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    found, entry = [], Entry()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        more = kernel.Process32FirstW(handle, ctypes.byref(entry))
        while more:
            if entry.exe.casefold() == Path(player).name.casefold():
                image, error = core.player_image(entry.pid)
                if error:
                    raise RuntimeError('Cannot classify a live same-name Player; refusing preference capture')
                if image == os.path.normcase(player):
                    found.append(entry.pid)
                else:
                    executable = Path(image)
                    app_info = executable.with_name(executable.stem + '_Data') / 'app.info'
                    try:
                        names = app_info.read_text(encoding='utf-8-sig').splitlines()
                    except OSError:
                        if executable.is_relative_to(ROOT / 'Builds'):
                            raise RuntimeError('Cannot classify a live project Player preference identity')
                    else:
                        if names == [COMPANY, PRODUCT]:
                            found.append(entry.pid)
            more = kernel.Process32NextW(handle, ctypes.byref(entry))
        if ctypes.get_last_error() != 18:
            raise ctypes.WinError(ctypes.get_last_error())
        return found
    finally:
        kernel.CloseHandle(handle)


def guarded_bootstrap(core, frozen):
    data = core.request(frozen, require_player=True)
    metadata = Path(data['reportRoot']) / '_desktop-launch'
    original = json.loads(Path(frozen).read_text(encoding='utf-8-sig'))
    if any(original.get(key) != value for key, value in data.items()):
        raise RuntimeError('The frozen request identity changed before preference capture')
    if Path(frozen).resolve() != (metadata / 'request.json').resolve():
        raise RuntimeError('Preference guard requires the original frozen metadata request')
    own = core.native.own_desktop()
    core.require_inactive_desktop(data['desktopName'])
    if own['desktopName'].casefold() != data['desktopName'].casefold():
        raise RuntimeError('Preference guard requires the original inactive bootstrap desktop')
    registry = RawRegistry()
    sid = registry.sid()
    kernel = registry.kernel
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
    mutex = kernel.CreateMutexW(None, False, 'Local\\ProjectSplatoonStandalonePrefs-' + sid)
    if not mutex:
        raise ctypes.WinError(ctypes.get_last_error())
    acquired = False
    saved, baseline, owned = None, None, []
    pending_owned = False
    original_popen = subprocess.Popen
    original_code, exit_code = None, 1
    token = hashlib.sha256((sid + SUBKEY).encode('utf-8')).hexdigest()[:20]
    pending = HERE / 'StandalonePrefsPreparation' / ('pending-' + token + '.json')
    evidence = metadata / 'StandalonePreferences'
    record = {'schema': 1, 'userSid': sid, 'bootstrapPid': os.getpid(), 'scope': CONFIG,
              'originalBootstrapExitCode': None, 'restored': False, 'ownedPlayers': []}
    def tracked_popen(*args, **kwargs):
        command = args[0] if args else kwargs.get('args')
        process = original_popen(*args, **kwargs)
        executable = command[0] if isinstance(command, (list, tuple)) and command else None
        if executable is not None and os.path.normcase(str(Path(executable).resolve())) == os.path.normcase(data['playerPath']):
            owned.append(process)
        return process
    try:
        waited = kernel.WaitForSingleObject(mutex, 0)
        if waited not in (0, 0x80):
            raise RuntimeError('Another Standalone preference guard owns the lane; no harness invoked')
        acquired = True
        if pending.exists():
            raise RuntimeError('An unrestored preference snapshot exists at ' + str(pending) + '; no harness invoked')
        if candidate_processes(core, data['playerPath']):
            raise RuntimeError('A same-product Player already exists; no harness invoked or external process stopped')
        evidence.mkdir(exist_ok=False)
        app_info = Path(data['playerAppInfoPath'])
        if hashlib.sha256(app_info.read_bytes()).hexdigest() != data['playerAppInfoSha256']:
            raise RuntimeError('Actual built Player preference identity changed before capture')
        record.update(playerAppInfoPath=str(app_info), playerAppInfoSha256=data['playerAppInfoSha256'])
        baseline = registry.capture()
        if ({key: value for key, value in baseline[0].items() if key != 'view'} !=
                {key: value for key, value in baseline[1].items() if key != 'view'}):
            raise RuntimeError('Shared HKCU Software 32/64 views disagree; no harness invoked')
        saved = {'schema': 1, 'userSid': sid, 'registrySubKey': SUBKEY, 'logicalKeys': KEYS, 'views': baseline}
        save_new(evidence / 'before.json', saved)
        save_new(pending, {'snapshotPath': str(evidence / 'before.json'), 'bootstrapPid': os.getpid(),
                           'playerPath': data['playerPath'], 'userSid': sid})
        pending_owned = True
        subprocess.Popen = tracked_popen
        original_code = core.bootstrap(frozen)
        exit_code = original_code
    except BaseException as error:
        record['guardError'] = {'type': type(error).__name__, 'message': str(error)}
    finally:
        subprocess.Popen = original_popen
        record['originalBootstrapExitCode'] = original_code
        record['ownedPlayers'] = [{'pid': process.pid, 'exitCode': process.poll()} for process in owned]
        if pending_owned:
            try:
                if any(process.poll() is None for process in owned) or candidate_processes(core, data['playerPath']):
                    raise RuntimeError('A Player is still alive; preferences were not restored ahead of its Save')
                save_new(evidence / 'after-test.json', {'views': registry.capture()})
                registry.restore(baseline)
                after = registry.capture()
                save_new(evidence / 'after-restored.json', {'views': after})
                equal = all(before['values'] == observed['values'] for before, observed in zip(baseline, after))
                if not equal:
                    raise RuntimeError('Restored raw preference names/types/bytes differ from the baseline')
                record.update(restored=True, rawEqual=True,
                              branchExistenceEqual=all(a['branchExisted'] == b['branchExisted'] for a, b in zip(baseline, after)))
                pending.unlink()
            except BaseException as error:
                record['restorationError'] = {'type': type(error).__name__, 'message': str(error)}
                exit_code = original_code if original_code else 1
        if saved is not None and not record['restored']:
            exit_code = original_code if original_code else 1
        record.update(guardExitCode=exit_code, finishedUtc=datetime.now(timezone.utc).isoformat())
        try:
            # Separate receipt: never rewrite the existing bootstrap/harness outcome.
            save_new(evidence / 'verification.json' if evidence.is_dir() else metadata / 'standalone-preferences-refusal.json', record)
        finally:
            if acquired:
                kernel.ReleaseMutex(mutex)
            kernel.CloseHandle(mutex)
    return exit_code
