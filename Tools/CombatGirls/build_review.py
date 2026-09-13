"""Build a local screenshot review page and quantitative comparison; never edits images."""
from pathlib import Path
import json
from PIL import Image
import numpy as np

ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'Docs/CombatGirls/FourHeroes'
packs=json.loads((Path(__file__).with_name('hero-packs.json')).read_text('utf-8'))
rows=[]
for pack in packs:
    for view in ('front','side','back','face'):
        source=OUT/'Screenshots'/f'{pack["name"]}-source-{view}.png'
        target=source.with_name(source.name.replace('-source-','-target-'))
        a=np.asarray(Image.open(source).convert('RGB')).astype(float)
        b=np.asarray(Image.open(target).convert('RGB')).astype(float)
        foreground=np.max(np.abs(a-a[0,0]),axis=2)>8
        delta=np.abs(a-b)[foreground]
        rows.append(dict(hero=pack['name'],view=view,meanAbsoluteRgb=float(delta.mean()),maxRgbDelta=float(delta.max()),pixels=int(foreground.sum())))
(OUT/'visual-comparison.json').write_text(json.dumps({'condition':'800x800, same sampled idle pose and white light. Source-foreground mask excludes background-only team ring pixels; ring/boots edge overlap can remain. Numeric similarity is not full visual acceptance.','views':rows},indent=2),'utf-8')
page='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>CombatGirls · 迁移对照</title>
<style>body{margin:0;background:#121a22;color:#e6edf4;font:16px/1.6 system-ui,"Microsoft YaHei",sans-serif}main{max-width:1400px;margin:auto;padding:30px}h1{font-size:28px;margin:0}p{color:#b8c5d1}nav{display:flex;gap:14px;flex-wrap:wrap;margin:22px 0}select{padding:10px;background:#243441;color:white;border:1px solid #506678;border-radius:8px;font:inherit}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}figure{margin:0;background:#1c2934;border-radius:10px;overflow:hidden}figcaption{padding:12px 16px}img{width:100%;display:block}a{color:#78d9ef}.note{font-size:14px}#train{max-width:960px;margin:20px auto}@media(max-width:700px){.pair{grid-template-columns:1fr}main{padding:16px}}</style>
<main><h1>CombatGirls · 源外观与正式角色</h1><p>四个角色，41 个站姿动作。源场景初始部件统一采样到持枪待机；正式材质保留原始配色和 Toon 参数。</p>
<nav><label>角色 <select id="hero"></select></label><label>对照视角 <select id="angle"><option value="front">正面</option><option value="side">侧面</option><option value="back">背面</option><option value="face">脸部</option></select></label></nav>
<div class="pair"><figure><figcaption>源初始外观 · 统一姿态与白光</figcaption><img id="source" alt="源模型同条件截图"></figure><figure><figcaption>正式角色 · 原材质副本</figcaption><img id="target" alt="正式模型同条件截图"></figure></div>
<p class="note">对照图均为 800 × 800。部分正式图额外显示脚下队伍标记。原工程文件保持不变；演示脚本和布料未运行。</p>
<nav><label>训练场检查 <select id="pose"><option value="front">亮处正面</option><option value="side">亮处侧面</option><option value="back">亮处背面</option><option value="face">脸部近景</option><option value="shot">射击</option><option value="shadow">阴影处</option><option value="aim-up">上仰瞄准</option><option value="aim-down">下俯瞄准</option><option value="death-forward">向前死亡</option><option value="death-backward">向后死亡</option><option value="respawn">重生恢复</option></select></label></nav>
<figure id="train"><figcaption>实际训练场灯光和喷墨 Renderer · 960 × 960</figcaption><img id="training" alt="训练场角色表现"></figure>
<p><a href="Implementation.md">实现说明</a> · <a href="Acceptance.md">验收记录与未完成项</a> · <a href="static-validation.json">静态检查</a> · <a href="visual-comparison.json">像素差异记录</a></p></main>
<script>const packs=PACKS;const hero=document.querySelector('#hero'),angle=document.querySelector('#angle'),pose=document.querySelector('#pose');for(const p of packs){const o=document.createElement('option');o.value=p.name;o.textContent=p.name;hero.append(o)}function update(){document.querySelector('#source').src=`Screenshots/${hero.value}-source-${angle.value}.png`;document.querySelector('#target').src=`Screenshots/${hero.value}-target-${angle.value}.png`;document.querySelector('#training').src=`Screenshots/${hero.value}-training-${pose.value}.png`}for(const e of [hero,angle,pose])e.addEventListener('change',update);update();</script></html>'''
(OUT/'review.html').write_text(page.replace('PACKS',json.dumps(packs)),encoding='utf-8')
print(f'Created review.html and metrics for {len(rows)} paired views; screenshots unchanged.')
