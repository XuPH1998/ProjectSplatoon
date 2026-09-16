"""Verify that the saved map changed only the eight cover resolutions and topology."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


def check(root, output):
    path = 'Assets/GameResource/Gameplay/maps/TrainingGround.unity'
    backup = root / 'Reports/InkEdges/Baseline/TrainingGround.unity'
    old = backup.read_text(encoding='utf-8') if backup.exists() else subprocess.check_output(['git', 'show', 'HEAD:' + path], cwd=root).decode()
    new = (root / path).read_text(encoding='utf-8')
    mutable = r'^  (?:Resolution|ResolutionHeight|BakedTopology): .*$'
    assert re.sub(mutable, '', old, flags=re.M) == re.sub(mutable, '', new, flags=re.M), 'Unrelated scene data changed'

    def surfaces(text):
        result = {}
        for block in re.split(r'(?m)^--- ', text):
            match = re.search(r'^  SurfaceId: (\d+)$', block, re.M)
            if match:
                result[int(match[1])] = tuple(int(re.search(r'^  ' + field + r': (\d+)$', block, re.M)[1]) for field in ('Resolution', 'ResolutionHeight'))
        return result

    before, after = surfaces(old), surfaces(new)
    covers = {27, 28, 29, 30, 33, 34, 35, 36}
    assert before.keys() == after.keys() and len(after) == 36
    for sid, size in after.items():
        assert size == ((512, 640 if sid % 2 else 384) if sid in covers else before[sid]), (sid, size)
    unchanged = ['Assets/Splatoon/Runtime/Painting/InkCoverage.hlsl', 'Assets/Splatoon/Runtime/Painting/InkCoverage.cs',
                 'Assets/Splatoon/Runtime/Painting/InkShapeAtlas.cs', 'Assets/Splatoon/Runtime/Painting/PaintSnapshotCodec.cs',
                 'Assets/Splatoon/Runtime/Prototype/PrototypeMatch.PaintSync.cs', 'Assets/Splatoon/Runtime/Network/GameplayContentSignature.cs',
                 'Assets/Settings/PC_RPAsset.asset', 'Assets/Settings/Mobile_RPAsset.asset']
    for path in unchanged:
        baseline = subprocess.check_output(['git', 'show', 'HEAD:' + path], cwd=root).decode().replace('\r\n', '\n')
        assert baseline == (root / path).read_text(encoding='utf-8'), 'Unexpected change: ' + path
    pixels = sum(w * h for w, h in after.values())
    scratch = sum(w * h * 4 for w, h in set(after.values()))
    peaks = {'R8_UNorm': pixels * 13 + scratch, 'R16': pixels * 14 + scratch}
    assert max(peaks.values()) <= 128 * 1048576
    report = {'passed': True, 'coverIds': sorted(covers), 'surfaceCount': len(after),
              'sceneOtherDataIdentical': True, 'unchangedContracts': unchanged, 'peakBytes': peaks,
              'stateBytes': pixels * 4, 'sceneSha256': hashlib.sha256(new.encode()).hexdigest()}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, default=Path('Reports/InkEdges/static-validation.json'))
    args = parser.parse_args()
    check(args.root.resolve(), args.output.resolve())
