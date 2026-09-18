"""Deterministic periodic foam micro-height, roughness and pore masks (not gameplay data)."""
from pathlib import Path
import argparse
import numpy as np
from PIL import Image


def bake(size=512):
    rng = np.random.default_rng(20260918)
    y, x = np.mgrid[0:size, 0:size].astype(np.float32) / size
    height = np.zeros((size, size), np.float32)
    pockets = np.zeros_like(height)
    for count, radius, amplitude in ((24, .13, .65), (115, .041, .20)):
        for _ in range(count):
            px, py = rng.random(2)
            dx = np.minimum(abs(x-px), 1-abs(x-px))
            dy = np.minimum(abs(y-py), 1-abs(y-py))
            r = radius * rng.uniform(.55, 1.35)
            q = (dx*dx + dy*dy) / (r*r)
            cap = np.maximum(0, 1-q)**2
            height = np.maximum(height, cap * amplitude)
            pockets = np.maximum(pockets, np.exp(-((np.sqrt(q)-.84)/.10)**2) * .25)
    # Periodic derivatives. RG are signed tangent slopes; B is a sparse pore mask;
    # A is broad height used for gentle roughness/brightness variation.
    dx = (np.roll(height, -1, 1)-np.roll(height, 1, 1)) * size / 32
    dy = (np.roll(height, -1, 0)-np.roll(height, 1, 0)) * size / 32
    rgba = np.stack((np.clip(.5-dx, 0, 1), np.clip(.5-dy, 0, 1), pockets, height), axis=-1)
    return Image.fromarray(np.uint8(np.clip(rgba, 0, 1)*255))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('Assets/GameResource/Environment/TrainingGround/FoamPores.png'))
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    bake().save(args.output)
    print(args.output)
