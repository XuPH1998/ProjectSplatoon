"""Compare and lay out reference captures without changing their rendered pixels."""
from pathlib import Path
import argparse
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont

parser = argparse.ArgumentParser()
parser.add_argument('--project', default=str(Path(__file__).resolve().parents[2]))
args = parser.parse_args()
root = Path(args.project) / 'Reports/CombatGirls'
folder = root / 'Screenshots'
font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 20)
montage = Image.new('RGB', (960, 4 * 512), '#20242c')
draw = ImageDraw.Draw(montage)
report = {'conditions': 'Same imported source prefab, pose, camera, white key light, ambient, URP renderer and 960x960 target. Team ring disabled.', 'views': {}}
for row, angle in enumerate(('front', 'side', 'back', 'face')):
    original = Image.open(folder / f'source-{angle}.png').convert('RGB')
    migrated = Image.open(folder / f'target-{angle}.png').convert('RGB')
    diff = np.abs(np.asarray(original).astype(np.int16) - np.asarray(migrated).astype(np.int16))
    report['views'][angle] = {'meanAbsoluteChannelErrorOutOf255': float(diff.mean()), 'maxChannelError': int(diff.max()), 'changedPixels': int(np.any(diff, axis=2).sum())}
    for column, (label, frame) in enumerate((('Source', original), ('RifleGirl', migrated))):
        draw.text((column * 480 + 12, row * 512 + 5), f'{label} / {angle}', font=font, fill='white')
        montage.paste(frame.resize((480, 480), Image.Resampling.LANCZOS), (column * 480, row * 512 + 32))
montage.save(folder / 'source-target-comparison.jpg', quality=95)
(root / 'image-comparison.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
