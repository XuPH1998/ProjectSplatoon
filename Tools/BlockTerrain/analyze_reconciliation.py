"""Describe raw reconciliation outliers without changing the acceptance sample set."""
import argparse
from collections import Counter
from pathlib import Path

from network_acceptance import percentile, read_json, reconciliation_groups, write_json


def analyze(report):
    complete, groups = reconciliation_groups(report)
    events = report.get("reconcileEvents", [])
    outliers = [event for event in events if event.get("metres", 0) > .35 and not event.get("lifecycle")
                and event.get("stage") in ("traverse", "fall")]
    selected = sorted(outliers, key=lambda event: event.get("metres", 0), reverse=True)
    transitions = Counter()
    for event in outliers:
        before, after = event.get("oldPredicted", {}), event.get("replayed", {})
        transitions[(before.get("movement"), after.get("movement"), before.get("paperPose"), after.get("paperPose"))] += 1
    return {
        "evidenceComplete": complete, "movingAcknowledgements": len(groups),
        "rawP95Metres": percentile([group["metres"] for group in groups], .95),
        "rawP99Metres": percentile([group["metres"] for group in groups], .99),
        "humanDiagnosticP99Metres": percentile([group["humanMetres"] for group in groups], .99),
        "outlierEvents": len(outliers),
        "origins": dict(Counter(event.get("origin") for event in outliers)),
        "transitions": [{"beforeMovement": key[0], "afterMovement": key[1], "beforePose": key[2], "afterPose": key[3], "count": value}
                        for key, value in transitions.most_common()],
        "largestEvents": selected[:50],
        "scope": "Diagnostic event-level context only. Formal percentiles retain maximum raw correction per moving ACK.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = analyze(read_json(args.report))
    output = args.output or args.report.with_suffix(".outliers.json")
    write_json(output, result)
    print({key: value for key, value in result.items() if key not in ("largestEvents", "scope")})
    print(output)


if __name__ == "__main__":
    main()
