"""Root-only serial CLI bridge; never activates a window or starts an Editor."""
import json
import os
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone

ROOT = Path('D:/XPHUNITY/ProjectSplatoon')
OUT = Path(__file__).resolve().parent
CLI = 'C:/Users/wepie/AppData/Local/Unity/bin/unity.exe'

def main():
    label, command, *extra = sys.argv[1:]
    if not label.replace('-', '').replace('_', '').isalnum():
        raise ValueError('Invalid receipt label')
    stdout = OUT / (label + '.stdout.json')
    stderr = OUT / (label + '.stderr.txt')
    receipt = OUT / (label + '.command.json')
    if any(p.exists() for p in (stdout, stderr, receipt)):
        raise FileExistsError('Preserve prior execution')
    argv = [CLI, 'command', command, '--project-path', str(ROOT), '--caller', 'plugin',
            '--skill', 'unity-cli', '--format', 'json', '--timeout', '120', '--'] + extra
    env = dict(os.environ, SPLATOON_DISABLE_SYSTEM_CURSOR='1')
    record = dict(argv=argv, startedUtc=datetime.now(timezone.utc).isoformat(),
                  creationFlags='CREATE_NO_WINDOW', automaticRetry=False)
    try:
        with stdout.open('xb') as out, stderr.open('xb') as err:
            result = subprocess.run(argv, cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                stdout=out, stderr=err, shell=False, creationflags=subprocess.CREATE_NO_WINDOW, timeout=180)
        record['returncode'] = result.returncode
    finally:
        record['finishedUtc'] = datetime.now(timezone.utc).isoformat()
        receipt.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
    print(stdout.read_text(encoding='utf-8-sig'))
    print(stderr.read_text(encoding='utf-8-sig'))
    sys.exit(record['returncode'])

if __name__ == '__main__':
    main()
