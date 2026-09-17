"""Draw the original 1024px single-material color atlas (Python + Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
COLORS = {
    'teal': '#129FAC', 'ivory': '#F2EFE1', 'orange': '#FF812F',
    'grip': '#293D4A', 'deep': '#146171', 'light': '#7BDAD6',
    'black': '#172A36', 'white': '#FCFAEF',
}

def font(size, bold=False):
    candidates = [Path('C:/Windows/Fonts/bahnschrift.ttf'),
                  Path('C:/Windows/Fonts/seguisb.ttf' if bold else 'C:/Windows/Fonts/segoe.ttf'),
                  Path('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf')]
    for path in candidates:
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default(size=size)

def main():
    image = Image.new('RGB', (1024,1024), COLORS['ivory'])
    d = ImageDraw.Draw(image)
    for i, color in enumerate(COLORS.values()):
        d.rectangle((i*128,0,(i+1)*128-1,159), fill=color)
    # Main side badge UV rectangle: (32,224)-(992,640).
    d.rounded_rectangle((42,234,982,630), radius=42, fill=COLORS['ivory'])
    d.polygon([(85,288),(127,288),(92,555),(50,555)], fill=COLORS['teal'])
    d.text((152,267), 'AQUA', font=font(140,True), fill=COLORS['deep'])
    d.text((164,437), 'PRESSURE  /  SPORT', font=font(39), fill=COLORS['deep'])
    d.rounded_rectangle((753,295,940,477), radius=25, fill=COLORS['orange'])
    d.text((773,311),'03',font=font(106,True),fill=COLORS['ivory'])
    for i in range(3):
        x=176+i*154
        d.polygon([(x,534),(x+102,534),(x+79,566),(x-23,566)],fill=COLORS['teal'])
    # Small level indicator UV rectangle: (64,704)-(960,960).
    d.rounded_rectangle((64,704,960,960),radius=50,fill=COLORS['deep'])
    for i in range(7):
        x=114+i*91
        d.rounded_rectangle((x,755,x+43,900),radius=15,fill=COLORS['light'] if i<5 else COLORS['teal'])
    d.text((771,778),'H2O',font=font(58,True),fill=COLORS['ivory'])
    out=ROOT/'Export'/'WaterShotgun_Albedo.png'
    out.parent.mkdir(parents=True,exist_ok=True)
    image.save(out)
    print(out)

if __name__=='__main__':
    main()
