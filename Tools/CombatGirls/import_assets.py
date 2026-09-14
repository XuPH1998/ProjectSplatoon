"""Import the approved Combat Girls art subset without modifying the source project."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import uuid

CLIPS = ["R_AimWalk_F", "R_AimWalk_B", "R_AimWalk_FL", "R_AimWalk_BR",
         "R_AimTurn_L90", "R_AimTurn_R90", "R_AimIdle", "R_AimIdle_AutoShoot",
         "R_Die_F", "R_Die_B"]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default="D:/XPHUNITY/CombatGirls/Assets")
    parser.add_argument("--project", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--heroes", action="store_true", help="Import the four approved additional heroes without touching RifleGirl")
    parser.add_argument("--manifest-only", action="store_true", help="Refresh provenance without resetting Unity importer settings")
    args = parser.parse_args()
    source, project = Path(args.source), Path(args.project)
    pack = source / "CombatGirlsCharacterPack"
    target = project / "Assets/ThirdParty/CombatGirls/CombatGirlsCharacterPack"
    index = {}
    for meta in source.rglob("*.meta"):
        match = re.search(r"(?m)^guid: ([a-f0-9]{32})", meta.read_text(encoding="utf-8-sig"))
        if match:
            index[match[1]] = Path(str(meta)[:-5])
    existing = {}
    for meta in (project / "Assets").rglob("*.meta"):
        if target in meta.parents:
            continue
        match = re.search(r"(?m)^guid: ([a-f0-9]{32})", meta.read_text(encoding="utf-8-sig"))
        if match:
            existing[match[1]] = meta
    definitions = json.loads(Path(__file__).with_name('hero-packs.json').read_text()) if args.heroes else [dict(pack='RifleGirl', clips=CLIPS, avatar='Humanoid_F')]
    allowed = {d['pack']: set(d['clips']) for d in definitions}
    def approved(path):
        return 'Animations' not in path.parts or any((pack / p) in path.parents and path.stem in names for p, names in allowed.items())
    selected = set()
    for definition in definitions:
        for path in (pack / definition['pack']).rglob('*'):
            if path.is_file() and path.suffix.lower() not in ('.meta', '.unity', '.controller', '.cs') and approved(path):
                selected.add(path)
        selected.add(pack / ('Humanoid_Bot/Models/' + definition['avatar'] + '.fbx'))
    if not args.heroes:
        selected.add(pack / "Biperworks_Tools/CombatGirls_Weapon_Control/Character_Weapon_Controller.cs")
    # Follow art dependencies only. Demo controllers, scenes and editor repair hooks
    # must not drag unapproved animations or global material conversion into gameplay.
    pending = list(selected)
    while pending:
        path = pending.pop()
        texts = [Path(str(path) + ".meta")]
        if path.suffix.lower() in (".mat", ".prefab", ".asset"):
            texts.append(path)
        for text_path in texts:
            if not text_path.is_file():
                continue
            text = text_path.read_text(encoding="utf-8-sig")
            for guid in re.findall(r"guid: ([a-f0-9]{32})", text):
                dep = index.get(guid)
                if dep is None or not dep.is_file() or dep in selected:
                    continue
                if dep.suffix.lower() in (".controller", ".unity", ".cs"):
                    continue
                if not approved(dep):
                    continue
                selected.add(dep)
                pending.append(dep)
    # A newer source pack can reuse a GUID for changed shared art. Fork that
    # dependency instead of silently altering the already shipped RifleGirl.
    forks = {}
    if args.heroes:
        for path in selected:
            dst = target / path.relative_to(pack)
            if dst.exists() and not any((pack / d['pack']) in path.parents for d in definitions) and dst.read_bytes() != path.read_bytes():
                original_guid = re.search(r'(?m)^guid: ([a-f0-9]{32})', Path(str(path)+'.meta').read_text('utf-8-sig'))[1]
                forks[original_guid] = uuid.uuid5(uuid.NAMESPACE_URL, 'CombatGirls/FourHeroes/' + str(path.relative_to(pack))).hex
    records = []
    for path in sorted(selected):
        if pack not in path.parents:
            raise RuntimeError(f"Unexpected external art dependency: {path}")
        dst = target / path.relative_to(pack)
        meta = Path(str(path) + ".meta")
        guid = re.search(r"(?m)^guid: ([a-f0-9]{32})", meta.read_text(encoding="utf-8-sig"))[1]
        shared = args.heroes and not any((pack / d['pack']) in path.parents for d in definitions)
        if guid in forks:
            dst = target / 'SharedFourHeroes' / path.relative_to(pack)
        if guid in existing:
            raise RuntimeError(f"GUID collision: {path} and {existing[guid]}")
        dst.parent.mkdir(parents=True, exist_ok=True)
        for src_file, dst_file in ((path, dst), (meta, Path(str(dst) + ".meta"))):
            data = src_file.read_bytes()
            original_hash = hashlib.sha256(data).hexdigest()
            changes = []
            if args.heroes and (src_file.suffix == '.meta' or path.suffix in ('.prefab','.mat','.asset')):
                content = data.decode('utf-8-sig')
                for previous, replacement in forks.items(): content = content.replace(previous, replacement)
                if content.encode('utf-8') != data:
                    data = content.encode('utf-8'); changes.append('Isolate changed shared dependencies from RifleGirl')
            if src_file.suffix == ".meta" and path.suffix.lower() == ".fbx":
                text = data.decode("utf-8-sig")
                corrected = text.replace("c0426d61788399a4aa9702989176ee68", "b02f7765ace5dff4a866b767512e4a05")
                if corrected != text:
                    changes.append("Repair missing Humanoid Avatar source")
                    data = corrected.encode("utf-8")
            if path.suffix == ".prefab" and src_file == path:
                text = data.decode("utf-8-sig")
                corrected = re.sub(r"m_Controller: \{fileID: 9100000, guid: [a-f0-9]{32}, type: 2\}",
                                   "m_Controller: {fileID: 0}", text)
                if corrected != text:
                    changes.append("Remove demo animator controller dependency")
                    data = corrected.encode("utf-8")
            if shared and guid not in forks and dst_file.exists():
                data = dst_file.read_bytes(); changes.append('Reuse existing shared dependency without modifying it')
            elif not args.manifest_only and (not dst_file.exists() or dst_file.read_bytes() != data):
                dst_file.write_bytes(data)
            records.append({"source": str(src_file), "target": str(dst_file.relative_to(project)).replace("\\", "/"),
                            "sourceSha256": original_hash, "importSha256": hashlib.sha256(data).hexdigest(), "changes": changes})
    manifest = project / ("Reports/CombatGirls/FourHeroes/source-assets.json" if args.heroes else "Reports/CombatGirls/source-assets.json")
    manifest.parent.mkdir(parents=True, exist_ok=True)
    manifest.write_text(json.dumps({"packs": definitions, "files": records}, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"assets": len(selected), "files": len(records), "clips": sum(len(d['clips']) for d in definitions)}))

if __name__ == "__main__":
    main()
