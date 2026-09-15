"""Idempotent, value-preserving migration of authored weapon times from 60 Hz to seconds.

Default is a read-only preview. --apply writes a before/after audit before replacing
any assets. All assets are checked first; mixed/partial schemas are rejected.
"""
from pathlib import Path
import argparse
import hashlib
import json
import math
import re

ROOT = Path(__file__).resolve().parents[2]
TIME_FIELDS = {
    "startFrames": "startSeconds",
    "emergeStartFrames": "emergeStartSeconds",
    "inkRecoverLockFrames": "inkRecoverLockSeconds",
    "burstRecoveryFrames": "burstRecoverySeconds",
    "straightFrames": "straightSeconds",
    "brakeFrames": "brakeSeconds",
    "spreadRecoverFrames": "landingSpreadRecoverSeconds",
    "damageReduceStartFrames": "damageReduceStartSeconds",
    "damageReduceEndFrames": "damageReduceEndSeconds",
    "chargeFrames": "chargeSeconds",
    "semiBufferFrames": "semiBufferSeconds",
    "splatlingMinChargeFrames": "splatlingMinChargeSeconds",
    "splatlingFirstChargeFrames": "splatlingFirstChargeSeconds",
    "splatlingFirstShootFrames": "splatlingFirstShootSeconds",
    "splatlingFullShootFrames": "splatlingFullShootSeconds",
    "splatlingPostFrames": "splatlingPostSeconds",
}


def asset_values(text):
    result = {}
    for key, raw in re.findall(r"^  ([a-z]\w*): (.*)$", text, re.M):
        if key.startswith("m_"):
            continue
        if key in result:
            raise ValueError(f"重复字段：{key}")
        try:
            result[key] = json.loads(raw)
        except json.JSONDecodeError:
            result[key] = raw
    return result


def seconds_values(values):
    """Convert a historical frame schema without changing unrelated values."""
    result = dict(values)
    for old, new in TIME_FIELDS.items():
        if old not in result or new in result:
            raise ValueError(f"缺失旧字段或混合新旧字段：{old}/{new}")
        value = result.pop(old)
        if type(value) is not int or value < 0:
            raise ValueError(f"参考帧必须为非负整数：{old}")
        result[new] = value / 60.0
    return result


def convert(text):
    values = asset_values(text)
    old = set(TIME_FIELDS) & values.keys()
    new = set(TIME_FIELDS.values()) & values.keys()
    if not old and new == set(TIME_FIELDS.values()):
        for key in new:
            if not isinstance(values[key], (float, int)) or not math.isfinite(values[key]) or values[key] < 0:
                raise ValueError(f"秒数必须为有限非负数：{key}")
        return text, False
    if old != set(TIME_FIELDS) or new:
        raise ValueError("武器资产存在缺失或混合的新旧时间字段，未执行迁移")
    converted = seconds_values(values)
    for name, enum_range in (("fireMode", range(5)), ("muzzleMode", range(2))):
        if type(values.get(name)) is not int or values[name] not in enum_range:
            raise ValueError(f"未知模式：{name}")
    for before, after in TIME_FIELDS.items():
        text, count = re.subn(rf"(^  ){before}: [^\r\n]*", rf"\g<1>{after}: {converted[after]!r}", text, flags=re.M)
        assert count == 1
    assert asset_values(text) == converted
    return text, True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--report", type=Path, default=Path("Reports/WeaponSeconds/migration.json"))
    args = parser.parse_args()
    paths = sorted((args.root / "Assets").rglob("*WeaponConfig.asset"))
    if not paths:
        raise ValueError("没有找到武器资产")
    pending = []
    report = {"referenceHz": 60, "assets": []}
    for path in paths:
        original = path.read_bytes()
        text = original.decode("utf-8-sig")
        updated, changed = convert(text)
        if not changed:
            continue
        meta = Path(str(path) + ".meta").read_bytes()
        report["assets"].append({"path": path.relative_to(args.root).as_posix(),
            "beforeSha256": hashlib.sha256(original).hexdigest(), "metaSha256": hashlib.sha256(meta).hexdigest(),
            "before": asset_values(text), "after": asset_values(updated)})
        pending.append((path, updated.encode("utf-8")))
    if args.apply and pending:
        dest = args.root / args.report
        dest.parent.mkdir(parents=True, exist_ok=True)
        # Preserve the audit of the first migration. A second invocation is a no-op.
        with dest.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write("\n")
        for path, data in pending:
            temp = path.with_suffix(".seconds-migration.tmp")
            temp.write_bytes(data)
            temp.replace(path)
    print(json.dumps({"assets": len(paths), "converted" if args.apply else "wouldConvert": len(pending),
                      "skipped": len(paths) - len(pending)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
