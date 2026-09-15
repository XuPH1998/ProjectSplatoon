"""Extract only authored flight/muzzle particles; never run the old map installer."""
from pathlib import Path
import hashlib
import json
import re
import uuid

project = Path(__file__).resolve().parents[2]
reference = Path(r'D:\XPHUNITY\Splatoon-Ink')
fixture = project / 'Assets/Splatoon/Tests/VisualReference/InkFlight'
runtime = project / 'Assets/GameResource/Effects/Ink/Prefabs'
report = project / 'Reports/InkFlightReference'
for folder in (fixture, runtime, report):
    folder.mkdir(parents=True, exist_ok=True)

def guid(path):
    meta = Path(str(path) + '.meta')
    if not meta.exists():
        meta.write_text('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\n', encoding='utf-8')
    return re.search(r'^guid: (\w+)', meta.read_text(), re.M)[1]

source = reference / 'Assets/Scenes/MixAndJam.unity'
text = source.read_text(encoding='utf-8')
blocks = {int(re.search(r'&(\d+)', b)[1]): b for b in re.split(r'(?=^--- !u!)', text, flags=re.M) if b.startswith('--- !u!') and re.search(r'&(\d+)', b)}
material = fixture / 'ReferenceParticle.mat'
material.write_bytes((reference / 'Assets/Materials/ParticleMaterial 1.mat').read_bytes())
material_guid = guid(material)
for name, ids in [('Flight', [949289638, 949289639, 949289640, 949289641]), ('Muzzle', [276323216, 276323217, 276323218, 276323219])]:
    content = '%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n' + ''.join(blocks[i] for i in ids)
    content = re.sub(r'  m_Name: (Main Visual Particle|Shoot Effect Particle)', '  m_Name: Reference' + name, content)
    content = re.sub(r'  m_Father: \{fileID: \d+\}', '  m_Father: {fileID: 0}', content)
    content = re.sub(r'  m_Children:.*?(?=^  m_Father:)', '  m_Children: []\n', content, flags=re.M | re.S)
    content = re.sub(r'  m_LocalPosition: .*', '  m_LocalPosition: {x: 0, y: 0, z: 0}', content)
    content = re.sub(r'  m_LocalRotation: .*', '  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}', content)
    # References to the source impact hierarchy must never reach the runtime prefabs.
    content = re.sub(r'  SubModule:.*?(?=^  \w+Module:)', '  SubModule:\n    enabled: 0\n    subEmitters: []\n', content, flags=re.M | re.S)
    baseline = fixture / ('Reference' + name + '.prefab')
    baseline.write_text(content.replace('4bdfd49a249c4834c9d6a7a591023bb2', material_guid), encoding='utf-8')
    guid(baseline)
    target = runtime / ('Ink' + name + '.prefab')
    target.write_text(content.replace('m_Name: Reference' + name, 'm_Name: Ink' + name).replace('  m_Layer: 4', '  m_Layer: 11'), encoding='utf-8')
    guid(target)

graph = fixture / 'ReferenceStepAndClip.shadergraph'
graph.write_bytes((reference / 'Assets/Metaballs/Shaders/StepAndClip.shadergraph').read_bytes())
graph_guid = guid(graph)
mat = fixture / 'ReferenceStepAndClip.mat'
mat.write_text((reference / 'Assets/Metaballs/Shaders/Shader Graphs_StepAndClip.mat').read_text().replace('f68ffba8030502749b8eb867430ed4a1', graph_guid), encoding='utf-8')
guid(mat)
sources = [source, reference / 'Assets/Materials/ParticleMaterial 1.mat', reference / 'Assets/Metaballs/Shaders/StepAndClip.shadergraph', reference / 'Assets/Metaballs/Shaders/KawaseBlur.shader', reference / 'Assets/Metaballs/RendererFeatures/RenderMetaballsScreenSpace.cs']
manifest = {'reference': str(reference), 'assets': [{'path': str(p.relative_to(reference)), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in sources], 'particleFileIds': {'flight': 949289641, 'muzzle': 276323219}, 'extractionAdaptations': ['standalone transform', 'source collision/splash subemitters detached', 'runtime layer 11', 'fixture material uses original source color']}
(fixture / 'SourceManifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
guid(fixture / 'SourceManifest.json')
(report / 'source-assets.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
protected = [project / 'Assets/Splatoon/Runtime/Combat/InkImpactEffect.cs', project / 'Assets/GameResource/Effects/Ink/Prefabs/InkImpact.prefab', project / 'Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab', project / 'Assets/GameResource/Effects/Ink/Shaders/InkImpact.shader', project / 'Assets/GameResource/Effects/Ink/Shaders/StepAndClip.shadergraph']
protected += list((project / 'Assets/GameResource/Effects/Ink/Materials').glob('InkImpact*'))
baseline = report / 'protected-baseline.json'
if not baseline.exists():
    baseline.write_text(json.dumps({str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest() for p in protected}, indent=2), encoding='utf-8')
print('Extracted source flight/muzzle prefabs and reference composite. Impact resources untouched.')
