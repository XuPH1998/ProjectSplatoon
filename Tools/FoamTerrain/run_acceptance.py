"""Independent Unity Players; compare terrain only at the same round and revision."""
import argparse
import csv
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "NetworkOptimization"))
from run_network_cases import run_case


def verify(output, result):
    paths = [p for p in output.glob("*.foam.csv")]
    histories = []
    for path in paths:
        with path.open(encoding="utf-8-sig") as stream:
            histories.append({(int(r["round"]), int(r["revision"])): (int(r["foamHash"]), int(r["ownershipHash"]))
                              for r in csv.DictReader(stream) if int(r["revision"]) > 0})
    common = set.intersection(*(set(h) for h in histories)) if histories else set()
    latest = max(common) if common else None
    result["aligned_revision"] = latest
    result["terrain_hash_match"] = bool(latest) and all(h[latest] == histories[0][latest] for h in histories)
    reports=[r for name,r in result["reports"].items() if name!="proxy"]
    result["final_revision_match"] = len(reports)==result["players"] and len({(r.get("foamRound"),r.get("foamRevision"),r.get("foamHash"),r.get("ownershipHash")) for r in reports}) == 1
    result["active_terrain_observed"] = bool(latest) and latest[1] >= 20
    shots = result["reports"].get("host", {}).get("authoritativeShots", [])
    # Reconnecting the same participant creates a new NGO ID after combat has stopped.
    # Require the original participant count to have fired, not every historical ID.
    result["all_players_fired"] = sum(p["shots"] > 0 for p in shots) >= result["players"]
    result["passed"] &= (len(histories) == result["players"] and result["terrain_hash_match"]
                          and result["final_revision_match"] and result["active_terrain_observed"] and result["all_players_fired"])
    (output / "foam-case.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--matrix", choices=("smoke", "full", "eight-late", "reconnect", "reset"), default="smoke")
    parser.add_argument("--graphics",action="store_true",help="Enable a graphics device; batch/hidden windows may still produce no rendered frames. Default is headless network acceptance.")
    args = parser.parse_args()
    cases = [(0, 0)] if args.matrix == "smoke" else [(delay, loss) for delay in (50, 100, 200) for loss in (0, 1, 3)] if args.matrix == "full" else [(100, 1)]
    results = []
    for delay, loss in cases:
        directory = args.output.resolve() / f"rtt-{delay}-loss-{loss}"
        result = run_case(args.exe.resolve(), directory, delay, loss, players=8 if args.matrix == "eight-late" else 2,
                          # Probe lifetime includes Unity/Addressables cold startup. Allow the
                          # host's 45-second startup bound, combat, and a quiet convergence tail.
                          late=args.matrix == "eight-late", seconds=100 if args.matrix in ("eight-late", "reconnect", "reset") else 90,
                          scenario="foam", reconnect=args.matrix == "reconnect",foam_reset=args.matrix=="reset",headless=not args.graphics)
        if args.matrix=="reset":
            result["reset_observed"]="[SMOKE] Returned to room and started next round" in (directory/"host.log").read_text(encoding="utf-8",errors="replace")
            result["passed"] &= result["reset_observed"] and result["reports"].get("host",{}).get("foamRevision",-1)==0
        result = verify(directory, result)
        print(json.dumps({"case": str(directory), "foamPassed": result["passed"], "alignedRevision": result["aligned_revision"]}), flush=True)
        results.append(result)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "foam-matrix.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    sys.exit(0 if all(r["passed"] for r in results) else 1)
