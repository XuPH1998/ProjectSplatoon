"""Arrange the unmodified Blender/Unity renders into review contact sheets."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT=Path(__file__).resolve().parents[1]
FONT=Path('C:/Windows/Fonts/bahnschrift.ttf')

def font(size):
    return ImageFont.truetype(str(FONT),size) if FONT.exists() else ImageFont.load_default(size=size)

def main():
    sources=ROOT/'Previews'/'Unity'
    compare=Image.new('RGB',(1920,1056),'#162935');d=ImageDraw.Draw(compare)
    for i,(file,title) in enumerate([('original-level-grip-close.png','ORIGINAL SHOTGUN'),('water-level-grip-close.png','AQUA 03 / WATER SHOTGUN')]):
        d.text((32+i*960,25),title,font=font(30),fill='#f1eee1')
        with Image.open(sources/file) as im:compare.paste(im.convert('RGB'),(i*960,96))
    compare.save(ROOT/'Previews/06-unity-comparison.png')
    poses=Image.new('RGB',(1600,1720),'#162935');d=ImageDraw.Draw(poses)
    for i,(file,title) in enumerate([('water-level.png','LEVEL AIM'),('water-down45.png','AIM DOWN 45'),('water-up35.png','AIM UP 35'),('water-shoot.png','SHOT KEY POSE')]):
        x=(i%2)*800;y=(i//2)*860
        d.text((x+28,y+14),title,font=font(26),fill='#f1eee1')
        with Image.open(sources/file) as im:poses.paste(im.convert('RGB').resize((800,800),Image.Resampling.LANCZOS),(x,y+60))
    poses.save(ROOT/'Previews/07-unity-poses.png')
    print('Review sheets written.')

if __name__=='__main__':main()
