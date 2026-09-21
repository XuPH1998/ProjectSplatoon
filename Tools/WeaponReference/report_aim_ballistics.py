"""Summarize existing Unity measurements without rerunning or changing gameplay data."""
from pathlib import Path
import csv
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Reports/AimBallistics'
HEROES = {1: 'RifleGirl', 4: 'PistolGirl', 8: 'SplooshGirl', 2: 'DualPistolGirl', 6: 'MachineGunGirl', 5: 'RocketLauncherGirl'}
METRICS = ('ownedArea', 'floorArea', 'ownedWidth', 'ownedDepth', 'maxOwnedForward',
           'centerlineContinuous', 'connectedReach', 'longestCenterlineGap', 'inkSpent',
           'emittedProjectiles', 'paintStamps', 'impacts', 'paintPayloadBytes')


def main():
    before = {p.name: json.loads(p.read_text()) for p in (OUT / 'Measurements/Before').glob('*.json')}
    rows = []
    for path in sorted((OUT / 'Measurements/After').glob('seed-*/*.json')):
        after = json.loads(path.read_text())
        old = before[path.name]
        row = {'hero': after['weapon'], 'weapon': HEROES[after['weapon']], 'scenario': after['scenario'],
               'seed': after['sampleSeed'], 'charge': after['charge'], 'driverHz': after['driverHz'],
               'beforeType': 'frozen-config-replay-current-simulator', 'beforeSeed': old['sampleSeed']}
        for field in METRICS:
            row[field + 'Before'] = old[field]
            row[field + 'After'] = after[field]
        row['gridHashBefore'] = old['gridHash']
        row['gridHashAfter'] = after['gridHash']
        rows.append(row)
    if not rows:
        raise SystemExit('Run AimBallisticsPaintTests in Unity first.')
    with (OUT / 'comparison.csv').open('w', newline='', encoding='utf-8-sig') as handle:
        writer = csv.DictWriter(handle, fieldnames=rows[0])
        writer.writeheader()
        writer.writerows(rows)

    intro = ('Before 是在当前模拟器中回放修改前冻结配置；不是旧二进制截图或原版实机。'
             '涂墨比较使用相同 seed 0；CSV 另列 seed 1 的修改后结果。'
             '面积采用 0.125 网格；普通武器 10 次接受动作，Hydra 为 25% 蓄力的一次弹仓。'
             '30/60/144 Hz 指外部模拟驱动频率，不是渲染帧率。')
    page = ['<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>弹道与瞄准对比</title>',
            '<style>body{font:16px/1.6 system-ui;margin:30px;background:#15212c;color:#e8edf4}'
            'h1,h2{color:#8ee2db}a{color:#8ee2db}.pair{display:grid;grid-template-columns:1fr 1fr;gap:16px}'
            'figure{margin:0}img{width:100%;background:#263744}details{margin:14px 0}summary{cursor:pointer}'
            'table{border-collapse:collapse}td,th{padding:8px;border:1px solid #536272}'
            '.coverage img{width:auto;height:480px;max-width:100%;object-fit:contain}figcaption{padding:6px}</style>',
            '<h1>六武器弹道与瞄准对比</h1>', '<p>' + intro + '</p>', '<p><a href="comparison.csv">完整测量 CSV</a></p>']
    md = ['# 弹道与瞄准实施验证', '', intro, '',
          '实现说明：[Design.md](../../Docs/AimBallistics/Design.md)。', '',
          '查看 [截图对比目录](comparison.html) 或 [完整测量 CSV](comparison.csv)。', '',
          '## 执行结果', '', '| 运行 | 通过 | 失败 | 跳过 |', '|---|---:|---:|---:|']
    for name in ('baseline-tests', 'core-final', 'paint', 'playmode', 'regression-final'):
        file = OUT / (name + '.xml')
        if file.exists():
            root = ET.parse(file).getroot()
            md.append(f'| [{name}]({name}.xml) | {root.get("passed")} | {root.get("failed")} | {root.get("skipped")} |')
    md += ['', '这些运行包含重复测试，不应把通过数相加当作独立用例总数。', '',
           '修改前基线实际运行于本轮修改之前：179 通过、7 失败。7 项均为 SplooshGirlTests 的历史涂墨哈希断言。', '',
           '扩展回归中仍有 23 项失败、4 项跳过：上述 7 项；BubbleShotgunTests 的 10 项旧速度／阶段／落点断言；'
           'WeaponAlignmentTests 的 1 项旧爆炸泼桶涂墨哈希；HeroMigrationTests 的 5 项六角色目录、地址字典及移速断言。'
           '后 16 项不在本轮改动前实际运行的基线集合，不能当作已做旧二进制前后验证。'
           '对应特殊武器资产未修改，未改写这些断言掩盖差异。详情见 [回归失败清单](regression-failures.md)。', '',
           '本次需要修正的历史快照加载器已显式关闭新增能力，HeroSelectionTests 的三项历史发射配置检查和'
           'WeaponAssetTests 的旧版散布热更新检查已恢复通过。', '',
           '## 同条件平地测量（seed 0）', '',
           '| 武器 | 总归属面积 前→后 | 最大地面前伸 前→后 | 连通前伸 前→后 | 墨耗 前→后 |',
           '|---|---:|---:|---:|---:|']
    for hero, name in HEROES.items():
        row = next(r for r in rows if r['hero'] == hero and r['scenario'] == 'flat' and r['seed'] == 0)
        def pair(field):
            return f'{row[field + "Before"]:.3f} → {row[field + "After"]:.3f}'
        md.append(f'| {name} | {pair("ownedArea")} | {pair("maxOwnedForward")} | {pair("connectedReach")} | {pair("inkSpent")} |')
        page += ['<h2>' + name + '</h2>', '<div class="pair">']
        for case, caption in [('before', '旧配置回放'), ('standing', '当前配置：站立')]:
            src = f'PlayMode/hero-{hero}-{case}.png'
            page += [f'<figure><a href="{src}"><img src="{src}"></a><figcaption>{caption}</figcaption></figure>']
        page += ['</div><details><summary>俯仰、移动及遮挡截图</summary><div class="pair">']
        for case in ('up30', 'down30', 'forward', 'backward', 'near-wall', 'camera-wall'):
            src = f'PlayMode/hero-{hero}-{case}.png'
            page += [f'<figure><a href="{src}"><img loading="lazy" src="{src}"></a><figcaption>{case}</figcaption></figure>']
        page += ['</div></details><details><summary>平地归属网格（同尺度、粉色为所属格）</summary><div class="pair coverage">']
        for folder, caption in [('Before', '旧配置回放'), ('After/seed-0', '当前配置 seed 0')]:
            src = next((OUT / 'Measurements' / folder).glob(f'w{hero}-*-flat-60.coverage.png')).relative_to(OUT).as_posix()
            page += [f'<figure><a href="{src}"><img loading="lazy" src="{src}"></a><figcaption>{caption}</figcaption></figure>']
        page += ['</div></details>']
    md += ['', '长度与面积均为项目单位。它们用于观察本次变化，不是原版误差；Before/After 随机分布与落墨行为已改变。', '',
           '## 覆盖与边界', '',
           '- 核心：六武器、±60° 仰俯、前后移动、双枪枪口、Hydra 蓄力／释放、有限引导、散布分位数、签名与配置冻结。',
           '- 涂墨：9 类几何场景 × 6 武器 × 2 种子；各自比较 30/60/144 Hz 的归属哈希、印章数、命中数及耗墨。另覆盖离墙下落、不可涂遮挡、堵枪口脚下墨及撞击形状。',
           '- Host：6 武器 × 8 张真实渲染截图；真实服务发射／涂墨、热更新冻结旧弹、不兼容入房载荷拒绝；[观测值](PlayMode/observations.csv)。',
           '- 来源：冻结 11.3.0 参数；几何和概率分布为项目重建。没有原版合格实机对照、独立客户端／物理双机或发布包验收。',
           '- 红色遮挡提示仍以碰撞体身份及墙面法线区分；同一大网格不同面存在提示分类局限，不影响实际扫掠碰撞。',
           '- 导入器幂等检查通过；6 份 Baseline asset 与修改前 HEAD 完全一致。', '',
           'Reports 被 git 忽略；可由本次新增的测试与 report_aim_ballistics.py 重新生成。代码未提交，未执行打包。']
    (OUT / 'comparison.html').write_text('\n'.join(page) + '</html>', encoding='utf-8')
    (OUT / 'README.md').write_text('\n'.join(md) + '\n', encoding='utf-8')
    failures = ['# 扩展回归失败清单', '']
    for case in ET.parse(OUT / 'regression-final.xml').iter('test-case'):
        if case.get('result') == 'Failed':
            failures += ['## ' + case.get('fullname'), '', '```text', case.findtext('failure/message').strip(), '```', '']
    (OUT / 'regression-failures.md').write_text('\n'.join(failures), encoding='utf-8')
    print(f'Generated comparison for {len(rows)} after samples; {OUT / "README.md"}')


if __name__ == '__main__':
    main()
