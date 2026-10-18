"""Compile current sources against cached Unity references; never rewrites Library."""
from pathlib import Path
import subprocess
from datetime import datetime, timezone

root = Path(__file__).resolve().parents[2]
report = root / ('Reports/BlockPhysics20261017/StaticCompile-' + datetime.now(timezone.utc).strftime('%H%M%S'))
report.mkdir(parents=True, exist_ok=True)
editor = Path('C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Data')
for name, source_dir in [('Splatoon.Config', 'Assets/Splatoon/Config'), ('Splatoon', 'Assets/Splatoon/Runtime'), ('Splatoon.Tests', 'Assets/Splatoon/Tests')]:
    candidates = list((root / 'Library/Bee/artifacts').glob('*/' + name + '.rsp'))
    candidates = [p for p in candidates if any('UNITY_EDITOR' in line for line in p.read_text(encoding='utf-8-sig').splitlines() if line.startswith('-define:'))]
    response = max(candidates, key=lambda p: p.stat().st_mtime)
    lines = response.read_text(encoding='utf-8-sig').splitlines()
    lines = [line for line in lines if not line.startswith(('-out:', '-refout:', '-debug:', '-pdb:'))
             and not line.strip('"').endswith('.cs')]
    for dependency in ['Splatoon.Config', 'Splatoon']:
        if (report / (dependency + '.dll')).exists():
            lines = [line.replace(str(response.parent.relative_to(root)).replace('\\', '/') + '/' + dependency + '.ref.dll',
                                  str(report / (dependency + '.dll')).replace('\\', '/')) for line in lines]
    for source in sorted((root / source_dir).rglob('*.cs')):
        if any(list(parent.glob('*.asmdef')) for parent in source.parents
               if parent != root / source_dir and root / source_dir in parent.parents):
            continue
        lines.append('"' + source.relative_to(root).as_posix() + '"')
    lines.append('-out:"' + str(report / (name + '.dll')) + '"')
    target = report / (name + '.rsp')
    target.write_text('\n'.join(lines), encoding='utf-8')
    result = subprocess.run([str(editor / 'NetCoreRuntime/dotnet.exe'), str(editor / 'DotNetSdkRoslyn/csc.dll'), '@' + str(target)],
                            cwd=root, capture_output=True, text=True, encoding='utf-8')
    (report / (name + '.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
    print(name, result.returncode, result.stdout, result.stderr, flush=True)
    if result.returncode:
        raise SystemExit(result.returncode)
