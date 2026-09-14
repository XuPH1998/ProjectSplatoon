"""Copy ink/environment reference art. RifleGirl owns character content now."""
from pathlib import Path
import hashlib, json, re, shutil, subprocess, sys

project = Path(__file__).resolve().parents[2]
reference = Path(sys.argv[1] if len(sys.argv) > 1 else r'D:\XPHUNITY\Splatoon-Ink')
manifest = []
routes = {
    'Assets/Splattershot-Splatoon': 'Assets/GameResource/Weapons/Splattershot/Art',
    'Assets/Textures': 'Assets/GameResource/Environment/Ink/Textures',
    'Assets/Materials/PaintableMaterials': 'Assets/GameResource/Environment/Ink/Materials',
    'Assets/Metaballs/Shaders': 'Assets/GameResource/Effects/Ink/Shaders',
    'Assets/Metaballs/Materials': 'Assets/GameResource/Effects/Ink/Materials',
}

def copy(source, target):
    source, target = reference / source, project / target
    target.parent.mkdir(parents=True, exist_ok=True)
    # Re-running extraction must retain reviewed local URP/UV/code adaptations.
    # Temporary scene inputs are disposable and can always be refreshed.
    retain = target.exists() and '_Incoming' not in target.parts
    if not retain: shutil.copy2(source, target)
    if source.suffix == '.meta': return
    meta = Path(str(source) + '.meta')
    guid = re.search(r'^guid: (\w+)', meta.read_text(), re.M)[1] if meta.exists() else None
    if meta.exists() and not Path(str(target) + '.meta').exists(): shutil.copy2(meta, Path(str(target) + '.meta'))
    manifest.append(dict(source=source.relative_to(reference).as_posix(), target=target.relative_to(project).as_posix(), guid=guid, sha256=hashlib.sha256(source.read_bytes()).hexdigest()))

for src, dst in routes.items():
    for path in sorted((reference / src).rglob('*')):
        if path.is_file() and path.suffix != '.meta': copy(path.relative_to(reference), Path(dst) / path.relative_to(reference / src))
for src, dst in [
    ('Assets/Shaders/Paintable.shadergraph', 'Assets/GameResource/Environment/Ink/Shaders/Paintable.shadergraph'),
    ('Assets/Shaders/ExtendIslands.shader', 'Assets/GameResource/Environment/Ink/Shaders/ExtendIslands.shader'),
    ('Assets/Scenes/MixAndJam.unity', 'Assets/Art/_Incoming/InkReference/MixAndJam.unity'),
    ('Assets/Metaballs/RendererFeatures/RenderMetaballsScreenSpace.cs', 'Assets/Splatoon/Runtime/Rendering/RenderMetaballsScreenSpace.cs'),
    ('Assets/Materials/ParticleMaterial 1.mat', 'Assets/GameResource/Effects/Ink/Materials/InkParticle.mat'),
    ('LICENSE', 'Assets/GameResource/ThirdPartyNotices/MixAndJam-LICENSE.txt')]: copy(src, dst)
out = project / 'Reports/InkMigration'; out.mkdir(parents=True, exist_ok=True)
commit = subprocess.check_output(['git', '-C', str(reference), 'rev-parse', 'HEAD'], text=True).strip()
(out / 'reference-assets.json').write_text(json.dumps(dict(referenceCommit=commit, baseline='current working copy including local URP 17 adaptations', assets=manifest), ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Copied {len(manifest)} reference assets; recorded GUIDs and SHA-256.')
