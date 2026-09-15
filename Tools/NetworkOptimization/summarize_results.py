"""Collect raw Unity and independent-process evidence without inferring unmeasured results."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def tests(path):
    if not path.exists():
        return None
    root = ET.parse(path).getroot()
    return {key: root.get(key) for key in ("total", "passed", "failed", "duration")} | {
        "failures": [{"name": case.get("fullname"), "message": case.findtext("failure/message")}
                     for case in root.iter("test-case") if case.get("result") == "Failed"]}


def cases(path):
    result = []
    if not path.exists():
        return result
    for item in sorted(path.rglob("case.json")):
        case = json.loads(item.read_text(encoding="utf-8"))
        reports = case["reports"]
        host, proxy = reports.get("host", {}), reports.get("proxy", {})
        clients = [value for name, value in reports.items() if name.startswith("client-")]
        same_sequence = bool(clients) and all(client.get("appliedPaint") == host.get("appliedPaint") for client in clients)
        converged = all(client.get("ownershipHash") == host.get("ownershipHash") for client in clients) if same_sequence else None
        result.append({"path": str(item), "passed": case["passed"], "rttInjectedMs": case["rtt_ms"],
                       "lossPercent": case["loss_percent"], "players": case["players"],
                       "upBytes": proxy.get("up", {}).get("bytes"), "downBytes": proxy.get("down", {}).get("bytes"),
                       "host": host, "clients": clients, "exitCodes": case["exit_codes"],
                       "finalSamplesAtSamePaintSequence": same_sequence, "ownershipHashMatchesAtSameSequence": converged,
                       "lateJoinEvidence": case.get("late_join_evidence")})
    return sorted(result, key=lambda row: (row["players"], row["rttInjectedMs"], row["lossPercent"]))


def main(report):
    baseline = tests(report / "Baseline/focused.xml")
    optimized = tests(report / "Optimized/focused.xml")
    result = {"baselineTests": baseline, "optimizedTests": optimized,
              "scenePlayTests": tests(report / "Optimized/play.xml"),
              "newFailures": sorted({item["name"] for item in optimized["failures"]} -
                                    {item["name"] for item in baseline["failures"]}),
              "baselineNetwork": cases(report / "Baseline/NetworkFixture") + cases(report / "Baseline/PlayingEight"),
              "optimizedNetwork": cases(report / "Optimized/NetworkFixture") + cases(report / "Optimized/PlayingEight"),
              "lifecycleNetwork": cases(report / "Optimized/Reconnect") + cases(report / "Optimized/Combat"),
              "domainReloadTests": tests(report / "Optimized/domain-reload.xml"),
              "baselineSceneTest": tests(report / "Baseline/play-4v4.xml")}
    (report / "results.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    rows = ["|版本|注入 RTT / 丢包|人数|通过|客户端最近可靠 RTT 观测 ms（可陈旧）|输入积压峰值|Host 帧 P95 / P99 ms|上行 / 下行字节|",
            "|---|---|---|---|---|---|---|---|"]
    for version, key in (("基线", "baselineNetwork"), ("优化", "optimizedNetwork")):
        for case in result[key]:
            clients, host = case["clients"], case["host"]
            rtts = ", ".join(f'{client.get("rttMeanMs", 0):.1f}' for client in clients)
            pending = max((client.get("peakPending", 0) for client in clients), default=0)
            rows.append(f'|{version}|{case["rttInjectedMs"]}ms / {case["lossPercent"]}%|{case["players"]}|'
                        f'{case["passed"]}|{rtts}|{pending}|{host.get("frameP95Ms", 0):.2f} / '
                        f'{host.get("frameP99Ms", 0):.2f}|{case["upBytes"]} / {case["downBytes"]}|')
    (report / "measurements.md").write_text("\n".join(rows) + "\n", encoding="utf-8")
    print(json.dumps({"newFailures": result["newFailures"], "baselineCases": len(result["baselineNetwork"]),
                      "optimizedCases": len(result["optimizedNetwork"])}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    main(parser.parse_args().report.resolve())
