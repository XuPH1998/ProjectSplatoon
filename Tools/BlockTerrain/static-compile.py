"""Compile current sources against cached Unity references without changing Library.

This checks source APIs; it is not an Editor import, test, or Player verification.
"""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
report = root / 'Reports/BlockTerrain/StaticCompile'
report.mkdir(parents=True, exist_ok=True)
bee = root / 'Library/Bee/artifacts/1900b0aEDbg.dag'
editor = Path('C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Data')
for name, source_dir in [('Splatoon.Config','Assets/Splatoon/Config'), ('Splatoon','Assets/Splatoon/Runtime'), ('Splatoon.Tests','Assets/Splatoon/Tests')]:
    lines = (bee / f'{name}.rsp').read_text(encoding='utf-8-sig').splitlines()
    lines = [line for line in lines if not line.startswith(('-out:', '-refout:', '-debug:', '-pdb:'))]
    for dependency in ['Splatoon.Config','Splatoon']:
        if (report/f'{dependency}.dll').exists():
            lines = [line.replace(f'Library/Bee/artifacts/1900b0aEDbg.dag/{dependency}.ref.dll',str(report/f'{dependency}.dll').replace('\\','/')) for line in lines]
    existing = {line.strip('"').replace('\\','/') for line in lines if line.strip('"').endswith('.cs')}
    for source in sorted((root/source_dir).rglob('*.cs')):
        if any(list(parent.glob('*.asmdef')) for parent in source.parents if parent != root/source_dir and root/source_dir in parent.parents): continue
        relative = source.relative_to(root).as_posix()
        if relative not in existing: lines.append(f'"{relative}"')
    lines.append(f'-out:"{report/name}.dll"')
    response = report/f'{name}.rsp'
    response.write_text('\n'.join(lines),encoding='utf-8')
    result = subprocess.run([str(editor/'NetCoreRuntime/dotnet.exe'),str(editor/'DotNetSdkRoslyn/csc.dll'),f'@{response}'],cwd=root,capture_output=True,text=True,encoding='utf-8')
    (report/f'{name}.log').write_text(result.stdout+result.stderr,encoding='utf-8')
    print(result.stdout, result.stderr)
    if result.returncode: raise SystemExit(result.returncode)
