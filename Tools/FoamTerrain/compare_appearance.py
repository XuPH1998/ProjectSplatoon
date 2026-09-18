"""Build side-by-side evidence and animations from actual Unity captures; never synthesize gameplay images."""
import argparse
import csv
import json
from pathlib import Path
from PIL import Image, ImageDraw

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline',type=Path,required=True)
parser.add_argument('--current',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
for scenario in ('single','merge','scatter','enemy','ramp','platform'):
    board=Image.new('RGB',(1280,384),(24,27,32));draw=ImageDraw.Draw(board)
    for x,(folder,label) in enumerate(((args.baseline,'BASELINE'),(args.current,'ROUNDED FOAM'))):
        image=Image.open(folder/f'{scenario}-view-0.png').convert('RGB').resize((640,360))
        board.paste(image,(x*640,24));draw.text((x*640+12,6),label,fill='white')
    board.save(args.output/f'{scenario}-comparison.png')
for scenario in ('single','enemy'):
    frames=[]
    for n in range(24):
        board=Image.new('RGB',(1280,384),(24,27,32));draw=ImageDraw.Draw(board)
        for x,(folder,label) in enumerate(((args.baseline,'BASELINE'),(args.current,'ROUNDED FOAM'))):
            frame=Image.open(folder/f'{scenario}-{n:03}.png').convert('RGB').resize((640,360))
            board.paste(frame,(x*640,24));draw.text((x*640+12,6),label,fill='white')
        frames.append(board)
    frames[0].save(args.output/f'{scenario}-comparison.gif',save_all=True,append_images=frames[1:],duration=250,loop=0)
def metrics(folder):
    with (folder/'metrics.csv').open(encoding='utf-8-sig') as f:return {r['scenario']:r for r in csv.DictReader(f)}
before=metrics(args.baseline);after=metrics(args.current)
(args.output/'comparison.json').write_text(json.dumps({'baseline':before,'current':after,'note':'Editor commit timings only; occupied nodes are not world area. GIF samples five 20-Hz commits per frame; not real-time captured frame pacing.'},indent=2),encoding='utf-8')
print(args.output)
