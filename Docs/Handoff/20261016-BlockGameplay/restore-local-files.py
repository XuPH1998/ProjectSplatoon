"""Verify the handoff snapshot; --apply restores missing Reports files only.

Never launches a process, opens the registry, or overwrites an existing file.
"""
import argparse
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', help='Create missing files; refuse every conflict')
    args = parser.parse_args()
    manifest = json.loads((HERE / 'snapshot-manifest.json').read_text(encoding='utf-8-sig'))
    missing, conflicts = [], []
    for row in manifest['files']:
        source = (HERE / row['includedPath']).resolve()
        target = (ROOT / row['originalPath']).resolve()
        if not source.is_relative_to(HERE / 'snapshot') or not target.is_relative_to(ROOT / 'Reports'):
            raise ValueError('Manifest path escaped its allowed folder')
        if source.stat().st_size != row['bytes'] or digest(source) != row['sha256']:
            raise ValueError('Snapshot bytes changed: ' + row['includedPath'])
        if target.exists():
            if not target.is_file() or digest(target) != row['sha256']:
                conflicts.append(row['originalPath'])
        else:
            missing.append((source, target))
    print(json.dumps(dict(filesVerified=len(manifest['files']), missing=len(missing), conflicts=conflicts,
                          apply=args.apply, startsUnityOrPlayers=False), ensure_ascii=False, indent=2))
    if conflicts:
        raise SystemExit('Preserve conflicting local files; no files were copied.')
    if args.apply:
        for source, target in missing:
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open('xb') as output:
                output.write(source.read_bytes())
        print('Restored ' + str(len(missing)) + ' missing report/support files.')


if __name__ == '__main__':
    main()
