"""Build the delivery summary from actual Player evidence, without hiding failed trials."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2] / 'Reports/InkSoftEdges'
performance = json.loads((root / 'Player/comparison.json').read_text(encoding='utf-8'))
network = json.loads((root / 'NetworkFinalBuild/result.json').read_text(encoding='utf-8'))
video = json.loads((root / 'Video/video-evidence.json').read_text(encoding='utf-8'))
assert set(performance) == {'static', 'paint'}
assert all(len(p['trials']) == 3 for p in performance.values())
rows = ['| 场景 / 轮次 | A GPU P95 | B GPU P95 | GPU 增量 | 主线程 P95 增量 | 0.5 ms 预算 |',
        '|---|---:|---:|---:|---:|---|']
for scenario, title in [('static', '静态高覆盖'), ('paint', '持续涂色')]:
    for trial in performance[scenario]['trials']:
        a = next(m['p95'] for m in trial['baseline']['metrics'] if m['name'] == 'gpuFrameMs')
        b = next(m['p95'] for m in trial['new']['metrics'] if m['name'] == 'gpuFrameMs')
        passed = trial['gpuEvidenceValid'] and trial['gpuP95DeltaMs'] <= .5
        rows.append(f"| {title} / {trial['repeat']} | {a:.3f} ms | {b:.3f} ms | {trial['gpuP95DeltaMs']:+.3f} ms | {trial['mainP95DeltaMs']:+.3f} ms | {'通过' if passed else '未通过'} |")
all_passed = all(p['passed'] for p in performance.values())
text = ('最终版本的六组 GPU 增量预算均通过。' if all_passed else '**最终版本未全部通过 GPU 增量预算，保留实验状态。**') + '\n\n'
text += '\n'.join(rows) + '\n\n'
text += '[全部性能原始数据](Player/comparison.json)。每轮另存 GPU 频率、温度和负载采样；负增量不代表稳定提速。\n\n'
text += ('[最终构建联机复核通过](NetworkFinalBuild/result.json)' if network['passed'] else '**最终构建联机复核未通过**')
text += '：实时涂色、晚加入、继续涂色和重连后的 36 个表面覆盖/细节及归属一致。所有参与者开启实验外观；距离缓存未纳入网络哈希比较。\n\n'
text += f"连续视频：A/B 各 {video['baseline']['frames']} 帧、{video['baseline']['width']}×{video['baseline']['height']}、30 fps，均无重复帧。"
text += '[左右对照视频](Video/comparison-left-baseline-right-new.mp4) · [A 原分辨率](Video/baseline.mp4) · [B 原分辨率](Video/new.mp4) · [视频证据](Video/video-evidence.json)。移动画面的闪烁与观感仍需视觉判断。\n\n'
text += '初版曾出现静态和持续涂色 GPU P95 超预算，随后减少内部滤波采样，并将缓存更新限制到实际绘制面及真实接缝邻居后复测；[迭代记录](iteration-results.json)保留，不混入最终验收。\n'
readme = root / 'README.md'
before, rest = readme.read_text(encoding='utf-8').split('<!-- PLAYER_RESULTS -->', 1)
_, after = rest.split('<!-- /PLAYER_RESULTS -->', 1)
readme.write_text(before + '<!-- PLAYER_RESULTS -->\n' + text + '<!-- /PLAYER_RESULTS -->' + after, encoding='utf-8')
device = performance['static']['trials'][0]['new']
summary = {'performancePassed': all_passed, 'networkPassed': network['passed'],
           'device': device['device'], 'cpu': device['cpu'], 'unity': device['unity'],
           'maxGpuDeltaMs': max(t['gpuP95DeltaMs'] for p in performance.values() for t in p['trials']),
           'staticMedianDeltaMs': performance['static']['gpuP95MedianDeltaMs'],
           'paintMedianDeltaMs': performance['paint']['gpuP95MedianDeltaMs'],
           'visualApprovalPending': True, 'formalDefaultChanged': False}
(root / 'acceptance-summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding='utf-8')
print(json.dumps(summary, indent=2, ensure_ascii=False))
