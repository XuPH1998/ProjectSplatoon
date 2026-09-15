"""Assemble existing Unity captures; requires Pillow and an explicit ffmpeg path."""
import argparse
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw, ImageFont

parser = argparse.ArgumentParser()
parser.add_argument("--report", type=Path, required=True)
parser.add_argument("--ffmpeg", type=Path, required=True)
args = parser.parse_args()
root = args.report.resolve()
font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 24)
scenarios = [("continuous", "持续射击"), ("sweep", "移动扫射"), ("short-stop", "点射与停火收尾"), ("muzzle", "枪口喷溅")]
contact = Image.new("RGB", (1920, 4 * 596), (22, 28, 34))
for row, (name, label) in enumerate(scenarios):
    left = root / f"{name}-reference" / "%03d.png"
    right = root / f"{name}-restored" / "%03d.png"
    for slow in (False, True):
        suffix = "slow4x" if slow else "normal"
        # Both source inputs retain their original 1920x1080 pixels side by side.
        graph = "[0:v][1:v]hstack=inputs=2"
        if slow:
            graph += ",setpts=4*PTS"
        subprocess.run([str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-y",
                        "-framerate", "60", "-i", str(left), "-framerate", "60", "-i", str(right),
                        "-filter_complex", graph, "-r", "60", "-c:v", "libx264", "-crf", "18",
                        "-preset", "fast", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                        str(root / f"{name}-comparison-{suffix}.mp4")], check=True)
    index = 6 if name == "muzzle" else 30
    for col, mode in enumerate(("reference", "restored")):
        with Image.open(root / f"{name}-{mode}" / f"{index:03d}.png") as source:
            contact.paste(source.resize((960, 540), Image.Resampling.LANCZOS), (col * 960, row * 596 + 56))
    draw = ImageDraw.Draw(contact)
    draw.text((24, row * 596 + 14), f"{label}  |  参考原色", font=font, fill="white")
    draw.text((984, row * 596 + 14), "当前工程 · 保留粉蓝队色", font=font, fill="white")
contact.save(root / "keyframes-comparison.jpg", quality=94)

# Lightweight animated preview; MP4 and raw PNGs remain the full resolution evidence.
frames = []
for i in range(0, 84, 2):
    canvas = Image.new("RGB", (1280, 394), (22, 28, 34))
    for col, mode in enumerate(("reference", "restored")):
        with Image.open(root / f"continuous-{mode}" / f"{i:03d}.png") as source:
            canvas.paste(source.resize((640, 360), Image.Resampling.LANCZOS), (col * 640, 34))
    draw = ImageDraw.Draw(canvas)
    draw.text((12, 2), "参考原色", font=font, fill="white")
    draw.text((652, 2), "当前工程 · 粉队", font=font, fill="white")
    frames.append(canvas)
frames[0].save(root / "flight-preview.webp", save_all=True, append_images=frames[1:], duration=67, loop=0, quality=85)
print("Wrote 8 videos, keyframe comparison and animated preview.")
