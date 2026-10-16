"""Correlate immutable host recovery events with client corrections without changing acceptance metrics.

Run with a completed case directory, or omit it to locate the latest complete
acceptance4 case. --self-test uses synthetic events and launches no processes.
Temporal candidates are evidence to inspect, not proof that recovery caused a correction.
"""
import argparse
from collections import Counter, defaultdict
import math
from pathlib import Path

from network_acceptance import percentile, read_json, reconciliation_groups, write_json


ROOT = Path(__file__).resolve().parents[2]
STATE_FIELDS = ("life", "heroRevision", "team", "acknowledgedInput", "simulatedAt", "position", "humanPosition", "velocity",
                "verticalSpeed", "movement", "paperPose", "grounded", "swimming", "compactBody",
                "airHumanOffset", "requiredSupportRevision", "appliedSupportRevision", "supportCell")


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def xyz(value):
    if isinstance(value, dict) and all(finite(value.get(axis)) for axis in ("x", "y", "z")):
        return tuple(value[axis] for axis in ("x", "y", "z"))
    return None


def distance(a, b):
    left, right = xyz(a), xyz(b)
    return math.dist(left, right) if left is not None and right is not None else None


def vector_delta(before, after):
    left, right = xyz(before), xyz(after)
    return {axis: b - a for axis, a, b in zip(("x", "y", "z"), left, right)} if left is not None and right is not None else None


def state_slice(state):
    return {key: state.get(key) for key in STATE_FIELDS}


def ack_key(event):
    state = event.get("authority", {})
    return (event.get("connectionGeneration"), event.get("owner"), event.get("networkObjectId"),
            event.get("terrainRound"), state.get("life"), state.get("acknowledgedInput"))


def active(event):
    value = event.get("metres")
    return (event.get("stage") in ("traverse", "fall") and event.get("terrainRevision", 0) > 0
            and not event.get("lifecycle") and event.get("initialSyncComplete") is True
            and event.get("authority", {}).get("health", 0) > 0 and finite(value) and value >= 0)


def host_evidence(report):
    events = report.get("safetyRecoveryEvents") or []
    reasons = []
    checks = {
        "missing or unsupported safetyRecoveryEventSchema": report.get("safetyRecoveryEventSchema") == 1,
        "full safety recovery events are not included": report.get("safetyRecoveryEventsIncluded") is True,
        "safety recovery producer/consumer evidence is incomplete": report.get("safetyRecoveryEventsComplete") is True,
        "safety recovery consumer errors": report.get("safetyRecoveryConsumerErrors") == 0,
        "safety recovery count does not match retained events": report.get("safetyRecoveryEventCount") == len(events),
        "host report is not complete": report.get("complete") is True,
    }
    reasons.extend(reason for reason, passed in checks.items() if not passed)
    ids = [event.get("eventId") for event in events]
    if not (all(isinstance(value, int) and value > 0 for value in ids)
            and all(a < b for a, b in zip(ids, ids[1:]))):
        reasons.append("safety recovery event IDs are invalid or unordered")
    invalid = []
    for event in events:
        before, after = event.get("before", {}), event.get("after", {})
        identity_valid = all(isinstance(event.get(key), int) for key in ("owner", "terrainRound", "networkObjectId"))
        life_valid = isinstance(before.get("life"), int) and before.get("life") == after.get("life")
        if not identity_valid or not life_valid or not finite(event.get("terrainTime")):
            invalid.append(event.get("eventId"))
    if invalid:
        reasons.append("recovery identity, life or shared terrain time is invalid")
    return {"complete": not reasons, "reasons": reasons, "eventCount": len(events), "invalidEventIds": invalid}


def recovery_reason(event):
    capsule, paper = event.get("capsuleEmbedded"), event.get("paperEmbedded")
    return "capsule_and_paper" if capsule and paper else "capsule_only" if capsule else "paper_only" if paper else "unrecorded"


def direct_after_match(authority, recovery, epsilon):
    """A copied recovery state may retain the preceding motor tick's simulatedAt."""
    after = recovery.get("after", {})
    raw_distance = distance(authority.get("position"), after.get("position"))
    at, rt = authority.get("simulatedAt"), after.get("simulatedAt")
    fields = ("life", "heroRevision", "team", "movement", "paperPose", "grounded", "swimming", "compactBody", "requiredSupportRevision")
    human_distance = distance(authority.get("humanPosition"), after.get("humanPosition"))
    velocity_distance = distance(authority.get("velocity"), after.get("velocity"))
    return (recovery.get("recovered") is True and raw_distance is not None and raw_distance <= epsilon
            and human_distance is not None and human_distance <= epsilon
            and velocity_distance is not None and velocity_distance <= epsilon
            and finite(at) and finite(rt) and abs(at - rt) <= 1e-6
            and authority.get("acknowledgedInput") == after.get("acknowledgedInput")
            and all(authority.get(key) is not None and authority.get(key) == after.get(key) for key in fields)
            and finite(authority.get("airHumanOffset")) and finite(after.get("airHumanOffset"))
            and abs(authority["airHumanOffset"] - after["airHumanOffset"]) <= 1e-6)


def candidate_evidence(event, recovery, previous, window, epsilon):
    authority = event.get("authority", {})
    at = authority.get("simulatedAt")
    rt = event.get("replayed", {}).get("simulatedAt")
    publication = recovery.get("terrainTime")
    if not finite(at) or not finite(publication):
        return None
    direct = direct_after_match(authority, recovery, epsilon)
    horizon = max(at, rt) if finite(rt) else at
    if not direct and not (at - window <= publication <= horizon + 1e-6):
        return None
    previous_authority = previous.get("authority", {}) if previous else {}
    previous_time = previous_authority.get("simulatedAt")
    relations = []
    if direct:
        relations.append("direct_recovery_after_state_match")
    if (finite(previous_time) and previous_time < publication <= at + 1e-6):
        relations.append("publication_between_authority_observations")
    if publication <= at + 1e-6:
        relations.append("recent_publication_before_authority")
    elif publication <= horizon + 1e-6:
        relations.append("publication_inside_replay_horizon_after_authority")
    else:
        relations.append("direct_state_retains_prepublication_simulation_timestamp")
    before, after = recovery.get("before", {}), recovery.get("after", {})
    authority_ack, recovery_ack = authority.get("acknowledgedInput"), after.get("acknowledgedInput")
    ack_delta = authority_ack - recovery_ack if isinstance(authority_ack, int) and isinstance(recovery_ack, int) else None
    host_revision, client_revision = recovery.get("terrainRevision"), event.get("terrainRevision")
    return {
        "hostRecoveryEventId": recovery.get("eventId"), "relations": relations,
        "directAfterStateMatch": direct, "reason": recovery_reason(recovery), "recovered": recovery.get("recovered"),
        "hostConnectionGeneration": recovery.get("connectionGeneration"),
        "hostTerrainRevision": host_revision, "clientTerrainRevision": client_revision,
        "clientWorldBehindRecoveryRevision": client_revision < host_revision if isinstance(client_revision, int) and isinstance(host_revision, int) else None,
        "hostTerrainTime": publication, "hostServerTimeDiagnostic": recovery.get("serverTime"),
        "hostBeforeSimulatedAt": before.get("simulatedAt"), "hostAfterSimulatedAt": after.get("simulatedAt"),
        "secondsAuthorityAfterPublication": at - publication,
        "secondsReplayHorizonAfterPublication": horizon - publication,
        "authorityAckMinusRecoveryAck": ack_delta, "authorityAckAtOrAfterRecovery": ack_delta >= 0 if ack_delta is not None else None,
        "rawRecoveryMetres": recovery.get("rawMetres"), "humanRecoveryMetres": recovery.get("humanMetres"),
        "noRawDisplacement": recovery.get("noRawDisplacement"), "noHumanDisplacement": recovery.get("noHumanDisplacement"),
        "authorityDistanceToRecoveryBefore": distance(authority.get("position"), before.get("position")),
        "authorityDistanceToRecoveryAfter": distance(authority.get("position"), after.get("position")),
        "previousAuthorityDistanceToRecoveryBefore": distance(previous_authority.get("position"), before.get("position")),
        "hostRecoveryBefore": state_slice(before), "hostRecoveryAfter": state_slice(after),
        "hostRecoveryRawDisplacement": vector_delta(before.get("position"), after.get("position")),
        "hostRecoveryHumanDisplacement": vector_delta(before.get("humanPosition"), after.get("humanPosition")),
        "interpretation": "Copied post-recovery state matches authority; causal contribution to the correction still needs the prior prediction."
                          if direct else "Identity and shared-time compatibility only; intervening motor steps, contacts and publications are not recorded here.",
    }


def host_state_bracket(report, round_id, simulated_at):
    states = [state for state in report.get("states", []) if state.get("terrainRound") == round_id
              and finite(state.get("terrainTime"))]
    before = [state for state in states if state["terrainTime"] <= simulated_at]
    after = [state for state in states if state["terrainTime"] > simulated_at]
    fields = ("terrainTime", "serverTime", "revision", "safetyRecoveries", "safetyRecoveryFailures", "stage")
    def selected(state):
        return {key: state.get(key) for key in fields} if state else None
    return {"before": selected(max(before, key=lambda state: state["terrainTime"]) if before else None),
            "after": selected(min(after, key=lambda state: state["terrainTime"]) if after else None)}


def analyze(client, host, window=.5, threshold=.35, position_epsilon=.0001):
    complete, formal_groups = reconciliation_groups(client)
    host_status = host_evidence(host)
    session_match = bool(client.get("session")) and client.get("session") == host.get("session")
    diagnostics, previous_by_identity, grouped_events = [], {}, defaultdict(list)
    for event in client.get("reconcileEvents", []):
        if active(event):
            grouped_events[ack_key(event)].append(event)
    moving_keys = {key for key, events in grouped_events.items()
                   if any(event.get("hasPreviousObservation") is True
                          and finite(event.get("predictedMotionMetres")) and finite(event.get("authorityMotionMetres"))
                          and max(event["predictedMotionMetres"], event["authorityMotionMetres"]) >= .001 for event in events)}
    recovery_index = defaultdict(list)
    if session_match:
        for recovery in host.get("safetyRecoveryEvents") or []:
            key = (recovery.get("owner"), recovery.get("networkObjectId"), recovery.get("terrainRound"), recovery.get("before", {}).get("life"))
            recovery_index[key].append(recovery)
    for event in client.get("reconcileEvents", []):
        authority = event.get("authority", {})
        identity = (event.get("connectionGeneration"), event.get("owner"), event.get("networkObjectId"),
                    event.get("terrainRound"), authority.get("life"))
        previous = previous_by_identity.get(identity)
        previous_by_identity[identity] = event
        if not (finite(event.get("metres")) and event["metres"] > threshold
                and not event.get("lifecycle") and event.get("stage") in ("traverse", "fall")):
            continue
        key = (event.get("owner"), event.get("networkObjectId"), event.get("terrainRound"), authority.get("life"))
        candidates = []
        for recovery in recovery_index.get(key, []):
            if recovery.get("after", {}).get("life") != authority.get("life"):
                continue
            candidate = candidate_evidence(event, recovery, previous, window, position_epsilon)
            if candidate:
                candidates.append(candidate)
        candidates.sort(key=lambda item: (not item["directAfterStateMatch"], abs(item["secondsAuthorityAfterPublication"]), item["hostRecoveryEventId"] or -1))
        at = authority.get("simulatedAt")
        round_boundary = (finite(at) and any(state.get("terrainRound") != event.get("terrainRound")
                          and finite(state.get("terrainTime")) and abs(state["terrainTime"] - at) <= window
                          for state in host.get("states", [])))
        evidence_qualified = complete and host_status["complete"] and session_match and not round_boundary
        same_ack_events = grouped_events.get(ack_key(event), [])
        old_time, replay_time = event.get("oldPredicted", {}).get("simulatedAt"), event.get("replayed", {}).get("simulatedAt")
        direct_ids = [item["hostRecoveryEventId"] for item in candidates if item["directAfterStateMatch"]]
        diagnostics.append({
            "clientEventId": event.get("eventId"), "owner": event.get("owner"), "networkObjectId": event.get("networkObjectId"),
            "clientConnectionGeneration": event.get("connectionGeneration"), "terrainRound": event.get("terrainRound"),
            "life": authority.get("life"), "acknowledgedInput": authority.get("acknowledgedInput"),
            "origin": event.get("origin"), "stage": event.get("stage"), "rawMetres": event["metres"], "humanDiagnosticMetres": event.get("humanMetres"),
            "formalMovingAckPopulation": active(event) and ack_key(event) in moving_keys,
            "sameAcknowledgementEventIds": [item.get("eventId") for item in same_ack_events],
            "sameAcknowledgementMaximumRawMetres": max((item["metres"] for item in same_ack_events), default=None),
            "replaySteps": event.get("replaySteps"), "inputHistoryBefore": event.get("inputHistoryBefore"), "inputHistoryAfter": event.get("inputHistoryAfter"),
            "oldPredictionSecondsAheadOfAuthority": old_time - at if finite(old_time) and finite(at) else None,
            "replayHorizonSecondsMinusOldPrediction": replay_time - old_time if finite(replay_time) and finite(old_time) else None,
            "terrainPendingBefore": event.get("terrainPendingBefore"), "terrainPendingAfter": event.get("terrainPendingAfter"),
            "clientTerrainRevision": event.get("terrainRevision"), "clientTerrainTimeDiagnostic": event.get("terrainTime"),
            "previousAuthorityEventId": previous.get("eventId") if previous else None,
            "previousAuthority": state_slice(previous.get("authority", {})) if previous else None,
            "oldPredicted": state_slice(event.get("oldPredicted", {})), "authority": state_slice(authority), "replayed": state_slice(event.get("replayed", {})),
            "rawCorrectionDisplacement": vector_delta(event.get("oldPredicted", {}).get("position"), event.get("replayed", {}).get("position")),
            "authorityDisplacementSincePreviousObservation": vector_delta(previous.get("authority", {}).get("position"), authority.get("position")) if previous else None,
            "hostStateBracketAtAuthorityTime": host_state_bracket(host, event.get("terrainRound"), at) if finite(at) else None,
            "terrainRoundBoundaryNearby": round_boundary, "evidenceQualified": evidence_qualified,
            "directAfterStateMatchIds": direct_ids, "directAfterStateMatchUnique": len(direct_ids) == 1,
            "candidateCount": len(candidates), "recoveryCandidates": candidates,
            "conclusion": ("direct_state_match_available" if evidence_qualified else "direct_state_match_in_incomplete_or_boundary_evidence") if any(item["directAfterStateMatch"] for item in candidates)
                          else "temporal_candidates_only" if candidates else "no_candidate_in_window" if host_status["complete"] and session_match
                          else "recovery_evidence_unavailable_or_incomplete",
        })
    reason_counts = Counter(recovery_reason(event) for event in host.get("safetyRecoveryEvents") or [])
    rounds = sorted({event.get("terrainRound") for event in host.get("safetyRecoveryEvents") or [] if isinstance(event.get("terrainRound"), int)})
    counter_evidence = []
    for round_id in rounds:
        recoveries = [event for event in host.get("safetyRecoveryEvents") or [] if event.get("terrainRound") == round_id]
        states = [state for state in host.get("states", []) if state.get("terrainRound") == round_id]
        successes = sum(event.get("recovered") is True for event in recoveries)
        failures = sum(event.get("recovered") is False for event in recoveries)
        maximum_successes = max((state.get("safetyRecoveries", 0) for state in states), default=None)
        maximum_failures = max((state.get("safetyRecoveryFailures", 0) for state in states), default=None)
        counter_evidence.append({"terrainRound": round_id, "successfulRecoveryEvents": successes, "failedRecoveryEvents": failures,
                                 "maximumHostStateSafetyRecoveries": maximum_successes, "maximumHostStateSafetyRecoveryFailures": maximum_failures,
                                 "countersMatchRetainedEvents": successes == maximum_successes and failures == maximum_failures
                                                               if maximum_successes is not None and maximum_failures is not None else None})
    formal_values = [group["metres"] for group in formal_groups]
    return {
        "schema": 1, "scope": "Diagnostic correlation only. Formal metrics call unchanged network_acceptance.reconciliation_groups; no event or recovery is removed from that source.",
        "sharedTimeBasis": "Host recovery terrainTime against client authority/replayed simulatedAt. Process realtime and client ServerTime are never used to infer ordering.",
        "identityBasis": "Same report session, owner, networkObjectId, terrainRound and life. Process playerObjectId and connectionGeneration are not compared across host/client.",
        "terrainRoundLimit": "Observation terrainRound belongs to the local World; PlayerSnapshot has no native round field. Nearby host round boundaries are flagged and do not qualify direct evidence.",
        "orderingLimit": "Host recovery runs before that tick's player simulation and retains the preceding snapshot ACK/SimulatedAt; its intermediate After state may never be received. Temporal proximity is not proof of cause, and no match does not exclude earlier effects.",
        "candidateWindowBeforeAuthoritySeconds": window, "diagnosticRawThresholdMetres": threshold,
        "directStatePositionToleranceMetres": position_epsilon, "sessionMatch": session_match, "hostRecoveryEvidence": host_status,
        "formalMetrics": {"evidenceComplete": complete, "movingAcknowledgements": len(formal_groups),
                          "friendlySupportAcknowledgements": sum(group["friendly"] for group in formal_groups),
                          "rawP95Metres": percentile(formal_values, .95), "rawP99Metres": percentile(formal_values, .99),
                          "humanDiagnosticP99Metres": percentile([group["humanMetres"] for group in formal_groups], .99)},
        "hostRecoveryReasonCounts": dict(reason_counts), "hostRecoverySuccesses": sum(event.get("recovered") is True for event in host.get("safetyRecoveryEvents") or []),
        "hostRecoveryCounterEvidenceByRound": counter_evidence,
        "diagnosticOutlierEventCount": len(diagnostics), "diagnosticConclusions": dict(Counter(event["conclusion"] for event in diagnostics)),
        "outliers": diagnostics,
    }


def self_test():
    def state(t, x=0, ack=10, life=1):
        return {"simulatedAt": t, "position": {"x": x, "y": 1, "z": 0}, "humanPosition": {"x": x, "y": 1, "z": 0},
                "life": life, "heroRevision": 1, "team": 1, "requiredSupportRevision": 0,
                "velocity": {"x": 0, "y": 0, "z": 0}, "acknowledgedInput": ack, "health": 100, "movement": "Air", "paperPose": "None",
                "grounded": False, "swimming": False, "compactBody": False, "airHumanOffset": 0}
    def event(i, t, x=0, replay_time=None, connection=2):
        return {"eventId": i, "owner": 1, "networkObjectId": 3, "terrainRound": 1, "connectionGeneration": connection,
                "stage": "traverse", "terrainRevision": 5, "initialSyncComplete": True, "lifecycle": False,
                "hasPreviousObservation": True, "predictedMotionMetres": .2, "authorityMotionMetres": .2,
                "metres": .8 if i > 1 else .01, "humanMetres": .8 if i > 1 else .01,
                "authority": state(t, x), "replayed": state(replay_time if replay_time is not None else t, x), "oldPredicted": state(t, x + .8)}
    def recovery(i, t, owner=1, life=1, ack=10, simulation_time=None):
        return {"eventId": i, "owner": owner, "networkObjectId": 3, "terrainRound": 1, "terrainRevision": 6,
                "connectionGeneration": 1, "terrainTime": t, "before": state(t if simulation_time is None else simulation_time, 0, ack, life),
                "after": state(t if simulation_time is None else simulation_time, 1, ack, life), "recovered": True,
                "capsuleEmbedded": False, "paperEmbedded": True, "rawMetres": 1, "humanMetres": 1}
    client = {"session": "synthetic", "reconcileEventSchema": 1, "reconcileEventsIncluded": True,
              "reconcileEventsComplete": True, "reconcileConsumerErrors": 0, "reconcileEventCount": 2,
              "reconcileEvents": [event(1, 9.9), event(2, 10, 1, 10.2)]}
    recoveries = [recovery(1, 9.95), recovery(2, 10.1), recovery(3, 10, owner=2),
                  recovery(4, 10, life=2), recovery(5, 10.4), recovery(6, 10), recovery(7, 10.016, simulation_time=10)]
    host = {"session": "synthetic", "complete": True, "safetyRecoveryEventSchema": 1,
            "safetyRecoveryEventsIncluded": True, "safetyRecoveryEventsComplete": True, "safetyRecoveryConsumerErrors": 0,
            "safetyRecoveryEventCount": len(recoveries), "safetyRecoveryEvents": recoveries}
    result = analyze(client, host)
    candidates = {item["hostRecoveryEventId"]: item for item in result["outliers"][0]["recoveryCandidates"]}
    assert set(candidates) == {1, 2, 6, 7}, candidates
    assert candidates[6]["directAfterStateMatch"] and candidates[7]["directAfterStateMatch"]
    assert "publication_between_authority_observations" in candidates[1]["relations"]
    assert "publication_inside_replay_horizon_after_authority" in candidates[2]["relations"]
    assert candidates[2]["authorityAckAtOrAfterRecovery"] and candidates[1]["clientWorldBehindRecoveryRevision"]
    assert result["formalMetrics"]["rawP99Metres"] == .8 and result["formalMetrics"]["movingAcknowledgements"] == 1
    # Client reconnect generation differs from the host by design and is not an identity boundary across processes.
    assert len(candidates) == 4 and result["hostRecoveryEvidence"]["complete"]
    recoveries[0]["networkObjectId"] = 99
    assert 1 not in {item["hostRecoveryEventId"] for item in analyze(client, host)["outliers"][0]["recoveryCandidates"]}
    # Session mismatch never produces candidates, even when clocks and positions match.
    host["session"] = "other"
    assert analyze(client, host)["outliers"][0]["candidateCount"] == 0
    host["session"] = "synthetic"
    host["safetyRecoveryConsumerErrors"] = 1
    assert not analyze(client, host)["hostRecoveryEvidence"]["complete"]
    # Legacy reports retain their formal statistics and declare unavailable recovery evidence.
    legacy = analyze(client, {"session": "synthetic", "complete": True})
    assert legacy["formalMetrics"] == result["formalMetrics"] and not legacy["hostRecoveryEvidence"]["complete"]
    assert legacy["outliers"][0]["conclusion"] == "recovery_evidence_unavailable_or_incomplete"
    host["safetyRecoveryConsumerErrors"] = 0
    host["states"] = [{"terrainRound": 2, "terrainTime": 10.01}]
    boundary = analyze(client, host)["outliers"][0]
    assert boundary["terrainRoundBoundaryNearby"] and not boundary["evidenceQualified"]
    assert boundary["conclusion"] == "direct_state_match_in_incomplete_or_boundary_evidence"
    print("Synthetic correlation checks passed: identities, shared time, same-ACK multiple recovery, reconnect generations, publication ordering, sessions, incomplete evidence and unchanged formal metrics.")


def latest_case(search_root):
    candidates = []
    for path in search_root.rglob("host.blocks.json"):
        if not any(part.startswith("acceptance4") for part in path.parts):
            continue
        report = read_json(path)
        clients = [read_json(client) for client in path.parent.glob("client-*.blocks.json")]
        if (report.get("complete") is True and host_evidence(report)["complete"] and clients
                and all(client.get("complete") is True and reconciliation_groups(client)[0] for client in clients)):
            candidates.append(path)
    if not candidates:
        raise SystemExit("No complete acceptance4 host report with full safety events was found; supply a case directory to inspect older evidence.")
    return max(candidates, key=lambda path: path.stat().st_mtime).parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path)
    parser.add_argument("--search-root", type=Path, default=ROOT / "Reports/BlockTerrain/NetworkDiagnostic")
    parser.add_argument("--client", type=Path, help="A particular client report; default analyzes every client in the case")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--window", type=float, default=.5, help="Diagnostic lookback before authority.simulatedAt, in seconds")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not math.isfinite(args.window) or args.window < 0:
        parser.error("--window must be finite and nonnegative")
    directory = args.directory or latest_case(args.search_root)
    host_path = directory / "host.blocks.json"
    host = read_json(host_path)
    if not host:
        raise SystemExit(f"Cannot read host report: {host_path}")
    clients = [args.client] if args.client else sorted(directory.glob("client-*.blocks.json"))
    if not clients:
        raise SystemExit(f"No client reports were found in {directory}")
    if args.output and len(clients) != 1:
        parser.error("--output requires a single --client when a case contains multiple clients")
    for client_path in clients:
        client = read_json(client_path)
        if not client:
            raise SystemExit(f"Cannot read client report: {client_path}")
        result = analyze(client, host, window=args.window)
        result["hostReport"] = str(host_path.resolve())
        result["clientReport"] = str(client_path.resolve())
        output = args.output or client_path.with_suffix(".safety-reconciliation.json")
        write_json(output, result)
        print({"client": client_path.name, "sessionMatch": result["sessionMatch"], "hostRecoveryEvidence": result["hostRecoveryEvidence"],
               "formalMetrics": result["formalMetrics"], "outlierEvents": result["diagnosticOutlierEventCount"], "conclusions": result["diagnosticConclusions"]})
        print(output.resolve())


if __name__ == "__main__":
    main()
