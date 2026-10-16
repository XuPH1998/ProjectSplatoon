"""Opt-in independent Player block replication and prediction acceptance.

The default strict matrix is 2/8 Players x 0/100/200 ms RTT x 0/1/3 percent loss.
The explicit prototype-v1 profile uses 2/8 Players at 0ms/0%, 100ms/1%, and
200ms/3%, and permits P99 <= .60m at 100ms/1% (strict remains <= .35m).
Every case exercises populated late join, falling-state freeze, reconnect, round
clear, and rebuilding. Normal-camera frame reports are diagnostic only; the
benchmark for the selected acceptance profile remains a separate mandatory gate.
This script never builds or starts Unity. Running it explicitly starts Players.
"""
import argparse
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]
CHECKPOINTS = ("late", "frozen", "reconnected", "cleared", "rebuilt")
FINGERPRINT = ("round", "terrainRound", "revision", "pink", "blue", "total",
               "chunks", "fallingRuns", "fallingCells", "chunkHash", "fallingHash", "snapshotHash", "frozenAt", "fallingOffsetHash")
EXPECTED_MATRIX = set(itertools.product((2, 8), (0, 100, 200), (0, 1, 3)))
PROTOTYPE_MATRIX = frozenset((players, rtt, loss) for players in (2, 8)
                             for rtt, loss in ((0, 0), (100, 1), (200, 3)))
P95_LIMIT_METRES = .15
ACCEPTANCE_PROFILES = {
    "strict": {"matrix": frozenset(EXPECTED_MATRIX), "p95": P95_LIMIT_METRES, "p99": .35},
    "prototype-v1": {"matrix": PROTOTYPE_MATRIX, "p95": P95_LIMIT_METRES, "p99": .60},
}


def acceptance_settings(acceptance_profile):
    try:
        return ACCEPTANCE_PROFILES[acceptance_profile]
    except KeyError:
        raise ValueError(f"Unknown network acceptance profile: {acceptance_profile}") from None


def selected_cases(acceptance_profile="strict", players=None, rtt=None, loss=None):
    """Filters select cases from a profile, never invent extra cross-product cases."""
    return {case for case in acceptance_settings(acceptance_profile)["matrix"]
            if (players is None or case[0] in players)
            and (rtt is None or case[1] in rtt)
            and (loss is None or case[2] in loss)}


def matrix_summary(results, selected, acceptance_profile="strict"):
    required = acceptance_settings(acceptance_profile)["matrix"]
    completed = {(result["players"], result["rttMs"], result["lossPercent"]) for result in results}
    profile_complete = selected == required and completed == required and len(results) == len(required)
    strict_complete = selected == EXPECTED_MATRIX and completed == EXPECTED_MATRIX and len(results) == len(EXPECTED_MATRIX)
    profiles_match = bool(results) and all(result["acceptanceProfile"] == acceptance_profile for result in results)
    cases_passed = bool(results) and all(result["networkPassed"] for result in results)
    strict_cases_passed = bool(results) and all(result["strictNetworkPassed"] for result in results)
    return {"scope": "same-machine independent Players; separate same-profile performance acceptance required",
            "acceptanceProfile": acceptance_profile,
            "correctionLimits": {"appliesAt": {"rttMs": 100, "lossPercent": 1},
                                 "p95Metres": acceptance_settings(acceptance_profile)["p95"],
                                 "p99Metres": acceptance_settings(acceptance_profile)["p99"],
                                 "strictP95Metres": ACCEPTANCE_PROFILES["strict"]["p95"],
                                 "strictP99Metres": ACCEPTANCE_PROFILES["strict"]["p99"]},
            "requiredProfileMatrix": [list(case) for case in sorted(required)],
            "selectedMatrix": [list(case) for case in sorted(selected)],
            "completedCaseCount": len(results), "requiredCaseCount": len(required),
            "requiredProfileMatrixComplete": profile_complete, "caseProfilesMatch": profiles_match,
            "requiredMatrixComplete": strict_complete,
            "casesPassed": cases_passed, "strictCasesPassed": strict_cases_passed,
            "networkProfilePassed": profile_complete and profiles_match and cases_passed,
            # This legacy flag continues to mean the complete strict 18-case matrix.
            "networkMatrixPassed": strict_complete and profiles_match and strict_cases_passed,
            "fullAcceptancePassed": False, "cases": results}


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".writing")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    for attempt in range(5):
        try:
            temporary.replace(path)
            return
        except OSError as exception:
            if (os.name != "nt"
                    or getattr(exception, "winerror", None) not in (5, 32, 33)
                    or attempt == 4):
                raise
            time.sleep(.02 * (2 ** attempt))


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1)] if ordered else None


def fingerprint(state):
    if not state or any(key not in state for key in FINGERPRINT):
        return None
    if any(not isinstance(state[key], str) or len(state[key]) != 64 for key in ("chunkHash", "fallingHash", "fallingOffsetHash", "snapshotHash")):
        return None
    return tuple(state[key] for key in FINGERPRINT)


def valid_state(state, count):
    if state.get("phase") == "Finished":
        frozen_at, terrain_time = state.get("frozenAt"), state.get("terrainTime")
        if (not isinstance(frozen_at, (int, float)) or not math.isfinite(frozen_at) or frozen_at < 0
                or not isinstance(terrain_time, (int, float)) or not math.isfinite(terrain_time) or abs(terrain_time - frozen_at) > 1e-9):
            return False
    return (fingerprint(state) is not None and state.get("initialSyncComplete") is True
            and state.get("players") == count and state.get("pending") == 0
            and state.get("failedTransactions") == 0 and state.get("round") == state.get("terrainRound")
            and state.get("total") == state.get("pink", -1) + state.get("blue", -1)
            and state.get("scorePink") == state.get("pink") and state.get("scoreBlue") == state.get("blue"))


def aligned(states, count, stage):
    return (len(states) == count and all(state.get("stage") == stage and valid_state(state, count) for state in states)
            and len({fingerprint(state) for state in states}) == 1)


def labels(players):
    return ["host", *(f"client-{index}" for index in range(1, players))]


def checkpoint_evidence(reports, players, stage):
    """Require a shared exact revision, rather than comparing two missing fields."""
    candidates = []
    for name in labels(players):
        states = reports.get(name, {}).get("states", [])
        candidates.append({fingerprint(state): state for state in states
                           if state.get("stage") == stage and valid_state(state, players)})
    if not candidates or any(not candidate for candidate in candidates):
        return None
    common = set(candidates[0])
    for candidate in candidates[1:]:
        common.intersection_update(candidate)
    if not common:
        return None
    selected = max(common, key=lambda key: (key[0], key[2]))
    return {name: candidate[selected] for name, candidate in zip(labels(players), candidates)}


def reconciliation_groups(report):
    """Keep the worst observation of every moving ACK, including same-ACK terrain recovery."""
    events = report.get("reconcileEvents", [])
    ids = [event.get("eventId") for event in events]
    complete = (report.get("reconcileEventSchema") == 1 and report.get("reconcileEventsIncluded") is True
                and report.get("reconcileEventsComplete") is True and report.get("reconcileConsumerErrors") == 0
                and report.get("reconcileEventCount") == len(events) and bool(events)
                and all(isinstance(value, int) and value > 0 for value in ids)
                and all(a < b for a, b in zip(ids, ids[1:])))
    groups = {}
    for event in events:
        state = event.get("authority", {})
        distance = event.get("metres")
        if (event.get("stage") not in ("traverse", "fall") or event.get("terrainRevision", 0) <= 0
                or event.get("lifecycle") or not event.get("initialSyncComplete") or state.get("health", 0) <= 0
                or not isinstance(distance, (int, float)) or not math.isfinite(distance) or distance < 0):
            continue
        key = (event.get("connectionGeneration"), event.get("owner"), event.get("networkObjectId"),
               event.get("terrainRound"), state.get("life"), state.get("acknowledgedInput"))
        group = groups.setdefault(key, {"metres": 0, "humanMetres": 0, "moving": False, "friendly": False,
                                        "pending": False, "sameAckRecovery": False, "events": 0})
        group["metres"] = max(group["metres"], distance)
        human = event.get("humanMetres")
        if isinstance(human, (int, float)) and math.isfinite(human) and human >= 0:
            group["humanMetres"] = max(group["humanMetres"], human)
        motion = max(event.get("predictedMotionMetres", 0), event.get("authorityMotionMetres", 0))
        group["moving"] |= event.get("hasPreviousObservation") is True and math.isfinite(motion) and motion >= .001
        group["friendly"] |= event.get("friendlySupport") is True
        group["pending"] |= event.get("terrainPendingBefore") is True or event.get("terrainPendingAfter") is True
        group["sameAckRecovery"] |= event.get("sameAcknowledgement") is True and event.get("origin") == "TerrainResume"
        group["events"] += 1
    # Freeze the original sampling window's movement and friendly qualifications.
    # Late observations may increase an existing ACK's correction, never its eligibility.
    qualified = {key: group for key, group in groups.items() if group["moving"]}
    for event in events:
        if event.get("stage") in ("traverse", "fall"):
            continue  # These events were already counted in the first pass.
        state = event.get("authority", {})
        distance = event.get("metres")
        if (event.get("terrainRevision", 0) <= 0 or event.get("lifecycle")
                or not event.get("initialSyncComplete") or state.get("health", 0) <= 0
                or not isinstance(distance, (int, float)) or not math.isfinite(distance) or distance < 0):
            continue
        key = (event.get("connectionGeneration"), event.get("owner"), event.get("networkObjectId"),
               event.get("terrainRound"), state.get("life"), state.get("acknowledgedInput"))
        group = qualified.get(key)
        if group is None:
            continue
        group["metres"] = max(group["metres"], distance)
        human = event.get("humanMetres")
        if isinstance(human, (int, float)) and math.isfinite(human) and human >= 0:
            group["humanMetres"] = max(group["humanMetres"], human)
        group["pending"] |= event.get("terrainPendingBefore") is True or event.get("terrainPendingAfter") is True
        group["sameAckRecovery"] |= event.get("sameAcknowledgement") is True and event.get("origin") == "TerrainResume"
        group["events"] += 1
    return complete, list(qualified.values())


def finite_number(value):
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return False
    try:
        return math.isfinite(value)
    except (OverflowError, TypeError, ValueError):
        return False


def int_field(value, minimum=0):
    return isinstance(value, int) and not isinstance(value, bool) and value >= minimum


def vector_fields(value, integer=False):
    return isinstance(value, dict) and all(
        (isinstance(value.get(axis), int) and not isinstance(value.get(axis), bool)) if integer
        else finite_number(value.get(axis)) for axis in ("x", "y", "z"))


def unity_float32(value):
    """Round one scalar operation like Unity float; invalid/overflow fails closed."""
    import struct
    if not finite_number(value):
        return None
    try:
        rounded = struct.unpack("<f", struct.pack("<f", value))[0]
    except (OverflowError, TypeError, ValueError, struct.error):
        return None
    return rounded if math.isfinite(rounded) else None


def unity_first_cell(point):
    """Independently reproduce CellAt(point + up * (.10f * .51f))."""
    if not vector_fields(point):
        return None
    cell_size = unity_float32(.10)
    shift = unity_float32(cell_size * unity_float32(.51))
    result = {}
    for axis in ("x", "y", "z"):
        value = unity_float32(point[axis])
        if value is None:
            return None
        component = unity_float32((1 if axis == "y" else 0) * shift)
        shifted = unity_float32(value + component)
        if shifted is None:
            return None
        quotient = unity_float32(shifted / cell_size)
        if quotient is None:
            return None
        cell = math.floor(quotient)
        if not -(1 << 31) <= cell < (1 << 31):
            return None
        result[axis] = cell
    return result


def drain_input_failures(reports, players, authority_inputs):
    failures = []
    if not isinstance(authority_inputs, list) or len(authority_inputs) != players:
        return ["Drain lacks one authoritative input acknowledgement per participant"]
    owners = [row.get("owner") for row in authority_inputs if isinstance(row, dict)]
    if len(owners) != players or any(not int_field(owner) for owner in owners) or len(set(owners)) != players:
        return ["Drain authoritative participant identities are missing or duplicated"]
    indexed = {row["owner"]: row for row in authority_inputs}
    observed = []
    for name in labels(players):
        report = reports.get(name, {})
        owner, life, sequence = (report.get(key) for key in ("drainInputOwner", "drainInputLife", "drainInputSequence"))
        if (report.get("drainInputObserved") is not True or not int_field(owner) or not int_field(life, 1)
                or not int_field(sequence, 1)):
            failures.append(f"{name}: no real post-traversal idle input")
            continue
        observed.append(owner)
        authority = indexed.get(owner, {})
        if (authority.get("life") != life or not int_field(authority.get("acknowledgedInput"))
                or authority["acknowledgedInput"] < sequence):
            failures.append(f"{name}: host has not consumed the post-traversal idle input")
    if len(observed) != players or len(set(observed)) != players or set(observed) != set(owners):
        failures.append("Drain does not cover the same unique participants as the host")
    return failures


def empty_drain_state(state, players):
    return (isinstance(state, dict) and state.get("stage") == "drain" and state.get("phase") == "Playing"
            and valid_state(state, players) and state.get("fallingCells") == 0
            and all(state.get(key) == 0 for key in ("projectilePending", "subPending", "specialPending", "unstableColumns"))
            and int_field(state.get("commandsQueued")) and state.get("commandsQueued") == state.get("commandsCompleted")
            and finite_number(state.get("drainQuietSeconds")) and state["drainQuietSeconds"] >= 1)


def drain_ready(reports, players):
    host = reports.get("host", {}).get("current", {})
    return (aligned([reports.get(name, {}).get("current", {}) for name in labels(players)], players, "drain")
            and empty_drain_state(host, players)
            and not drain_input_failures(reports, players, host.get("authorityInputs")))


def validate_fall_fixture(reports, players):
    """Independent cell evidence; Added and old fallingCells alone never satisfy the new fixture."""
    coverage = reports.get("host", {}).get("fallCoverage")
    if not isinstance(coverage, dict) or coverage.get("schema") != 1:
        return None, ["Missing the finite current-round 22m published falling fixture evidence"]
    failures = []
    required_ints = ("round", "baselineRevision", "finishRequestRevision", "baselineNextRunId", "baselinePink",
                     "baselineBlue", "baselineTotal", "baselineQueued", "baselineCompleted", "activeFixtureFallingCells", "command")
    if any(not int_field(coverage.get(key)) for key in required_ints):
        return None, ["Falling fixture integer fields are missing or invalid"]
    if any(not finite_number(coverage.get(key)) or coverage[key] < 0
           for key in ("requestedRealtime", "finishRequestRealtime")):
        return None, ["Falling fixture request/finish realtime is missing or invalid"]
    if coverage["finishRequestRealtime"] < coverage["requestedRealtime"]:
        failures.append("Finite fixture finish request precedes its request observation")
    if (coverage.get("errors") != 0 or coverage.get("expectedPlayers") != players
            or not all(coverage.get(key) is True for key in ("drainAccepted", "allCommandsCompleted", "allCellsWitnessed", "finishRequested"))
            or not finite_number(coverage.get("drainQuietSeconds")) or coverage["drainQuietSeconds"] < 1
            or coverage["activeFixtureFallingCells"] <= 0
            or coverage["finishRequestRevision"] <= coverage["baselineRevision"]):
        failures.append("Finite falling fixture lacks the clean drain, full receipts, witnessed live cells or canonical finish request")
    drain = coverage.get("drainState", {})
    if (not empty_drain_state(drain, players) or drain.get("round") != coverage["round"]
            or drain.get("revision") != coverage["baselineRevision"]
            or drain.get("pink") != coverage["baselinePink"] or drain.get("blue") != coverage["baselineBlue"]
            or drain.get("total") != coverage["baselineTotal"]
            or drain.get("commandsQueued") != coverage["baselineQueued"]
            or drain.get("commandsCompleted") != coverage["baselineCompleted"]):
        failures.append("Recorded falling baseline was not the intact settled traversal state")
    shared_drain = checkpoint_evidence(reports, players, "drain")
    if shared_drain is None or fingerprint(shared_drain["host"]) != fingerprint(drain):
        failures.append("Finite falling baseline lacks a common exact settled terrain checkpoint on all peers")
    drain_inputs = coverage.get("drainInputs")
    failures.extend(drain_input_failures(reports, players, drain_inputs))
    # Validate before using identities as set/dict keys. Malformed evidence must
    # produce a normal failed case, never interrupt the remaining matrix.
    if (not isinstance(drain_inputs, list) or len(drain_inputs) != players
            or any(not isinstance(row, dict) or not int_field(row.get("owner")) for row in drain_inputs)):
        return None, failures
    commands, publications = coverage.get("commands"), coverage.get("publications")
    if (not isinstance(commands, list) or len(commands) != players or not all(isinstance(row, dict) for row in commands)
            or not isinstance(publications, list) or not all(isinstance(row, dict) for row in publications)):
        return None, failures + ["Falling fixture lacks exactly one command per Player and the publication list"]
    ids = [row.get("id") for row in commands]
    owners = [row.get("owner") for row in commands]
    if (any(not isinstance(item, str) or not item for item in ids) or len(set(ids)) != players
            or any(not int_field(owner) for owner in owners) or len(set(owners)) != players):
        return None, failures + ["Falling fixture command/participant identities are missing or duplicated"]
    drain_owners = {row["owner"] for row in drain_inputs}
    if set(owners) != drain_owners:
        failures.append("Finite fixture participants differ from the fully acknowledged drain")
    indexed = dict(zip(ids, commands))
    witnessed, publication_keys = {item: set() for item in ids}, set()
    active_at_finish, pink_added, blue_added = 0, 0, 0
    summaries = []
    ordinals = []
    for row in commands:
        item = row["id"]
        if (row.get("team") not in (1, 2) or row.get("round") != coverage["round"]
                or row.get("baselineRevision") != coverage["baselineRevision"] or row.get("requested") != 32
                or row.get("added") != 32 or row.get("callbackCount") != 1 or row.get("baselineEmpty") is not True
                or row.get("removedEnemy") != 0 or row.get("removedFriendly") != 0
                or not int_field(row.get("completedRevision")) or row["completedRevision"] <= coverage["baselineRevision"]
                or not int_field(row.get("completedWorldRevision")) or row["completedWorldRevision"] < row["completedRevision"]
                or row["completedRevision"] > coverage["finishRequestRevision"]
                or row["completedWorldRevision"] > coverage["finishRequestRevision"]
                or not int_field(row.get("queuedOrdinal"), 1) or not vector_fields(row.get("firstCell"), True)
                or not vector_fields(row.get("point")) or not vector_fields(row.get("lane"))
                or not finite_number(row.get("radius")) or abs(row["radius"] - .3) > 1e-6):
            failures.append(f"{item}: finite command geometry or exact full published callback is invalid")
            continue
        expected_first = unity_first_cell(row["point"])
        if expected_first is None or row["firstCell"] != expected_first:
            failures.append(f"{item}: firstCell does not match the independent Unity float32 build point")
        if any(abs(row["point"][axis] - row["lane"][axis] - value) > 1e-5
               for axis, value in (("x", 2), ("y", 22), ("z", 0))):
            failures.append(f"{item}: requested site is not 22m above its seeded lane")
        for key in ("queuedRealtime", "queuedServerTime", "completedRealtime", "completedServerTime", "queuedHeadAgeMs", "completedHeadAgeMs"):
            if not finite_number(row.get(key)) or row[key] < 0:
                failures.append(f"{item}: missing command timing/head age {key}")
        if (finite_number(row.get("queuedRealtime")) and finite_number(row.get("completedRealtime"))
                and row["completedRealtime"] < row["queuedRealtime"]):
            failures.append(f"{item}: command completed before its enqueue")
        if (finite_number(row.get("completedRealtime"))
                and row["completedRealtime"] > coverage["finishRequestRealtime"]):
            failures.append(f"{item}: command callback completed after the canonical finish request")
        ordinals.append(row["queuedOrdinal"])
        if row["team"] == 1:
            pink_added += row["added"]
        else:
            blue_added += row["added"]
    if sorted(ordinals) != list(range(coverage["baselineQueued"] + 1, coverage["baselineQueued"] + players + 1)):
        failures.append("Finite fixture enqueue ordinals include an omitted, repeated or extra command")
    for index, first_command in enumerate(commands):
        for second_command in commands[index + 1:]:
            if (vector_fields(first_command.get("firstCell"), True) and vector_fields(second_command.get("firstCell"), True)
                    and finite_number(first_command.get("radius")) and finite_number(second_command.get("radius"))):
                dx = first_command["firstCell"]["x"] - second_command["firstCell"]["x"]
                dz = first_command["firstCell"]["z"] - second_command["firstCell"]["z"]
                if math.sqrt(dx * dx + dz * dz) * .1 <= first_command["radius"] + second_command["radius"]:
                    failures.append("Finite fixture footprints overlap; command provenance is ambiguous")
    for row in publications:
        item = row.get("fixtureId")
        command = indexed.get(item)
        cells = row.get("cells")
        if (command is None or row.get("owner") != command.get("owner") or row.get("team") != command.get("team")
                or row.get("round") != coverage["round"] or not int_field(row.get("revision"))
                or row["revision"] <= coverage["baselineRevision"]
                or row["revision"] > coverage["finishRequestRevision"]
                or not isinstance(cells, list) or not cells or row.get("liveCount") != len(cells)
                or not vector_fields(command.get("firstCell"), True) or not finite_number(command.get("radius"))):
            failures.append("Published fall row lacks the matching fixture/owner/round/revision or real cells")
            continue
        key = (item, row["revision"])
        if key in publication_keys:
            failures.append(f"{item}: duplicate fixture publication revision")
        publication_keys.add(key)
        origins, new_count = set(), 0
        for cell in cells:
            if (not isinstance(cell, dict) or not int_field(cell.get("runId"), coverage["baselineNextRunId"])
                    or cell.get("owner") != command["team"] or not vector_fields(cell.get("originCell"), True)
                    or not vector_fields(cell.get("liveCenter")) or not finite_number(cell.get("offset"))):
                failures.append(f"{item}: cell lacks a real new run, correct owner, origin or live position")
                continue
            origin, first = cell["originCell"], command["firstCell"]
            dx, dz = origin["x"] - first["x"], origin["z"] - first["z"]
            if (not first["y"] <= origin["y"] < first["y"] + 32
                    or (dx * dx + dz * dz) * .1 * .1 > command["radius"] ** 2 + .00001):
                failures.append(f"{item}: observed falling cell lies outside its actual build candidate volume")
                continue
            if any(abs(cell["liveCenter"][axis] - ((origin[axis] + .5) * .1 - (cell["offset"] if axis == "y" else 0))) > 1e-4
                   for axis in ("x", "y", "z")):
                failures.append(f"{item}: live falling position does not match the actual trajectory offset")
            address = (origin["x"], origin["y"], origin["z"])
            if address in origins:
                failures.append(f"{item}: publication counts the same original cell twice")
            origins.add(address)
            if address not in witnessed[item]:
                new_count += 1
                witnessed[item].add(address)
        if row.get("newlyWitnessedCount") != new_count:
            failures.append(f"{item}: reported new-cell count differs from independent unique geometry")
        if row["revision"] == coverage["finishRequestRevision"]:
            active_at_finish += len(origins)
        for field in ("serverTime", "terrainTime", "realtime", "headAgeMs"):
            if not finite_number(row.get(field)) or row[field] < 0:
                failures.append(f"{item}: publication is missing {field}")
    for item, command in indexed.items():
        if len(witnessed[item]) != 32:
            failures.append(f"{item}: expected 32 actual newly published falling cells; witnessed {len(witnessed[item])}")
        summaries.append({"id": item, "owner": command.get("owner"), "team": command.get("team"),
                          "point": command.get("point"), "added": command.get("added"),
                          "callbackCount": command.get("callbackCount"), "completedRevision": command.get("completedRevision"),
                          "uniquePublishedFallingCells": len(witnessed[item])})
    if active_at_finish <= 0 or active_at_finish != coverage["activeFixtureFallingCells"]:
        failures.append("Canonical finish request lacks the independently counted current published fixture falling state")
    summary = {"source": "host.blocks.json:fallCoverage", "passed": not failures, "round": coverage["round"],
               "baselineRevision": coverage["baselineRevision"], "finishRequestRevision": coverage["finishRequestRevision"],
               "baselineTotal": coverage["baselineTotal"], "expectedPink": coverage["baselinePink"] + pink_added,
               "expectedBlue": coverage["baselineBlue"] + blue_added,
               "activeAtFinishRequest": active_at_finish, "publicationSamples": len(publications), "commands": summaries}
    return summary, failures


def evaluate_case(directory, players, rtt, loss, session, exit_codes=None, acceptance_profile="strict"):
    settings = acceptance_settings(acceptance_profile)
    reports = {name: read_json(directory / f"{name}.blocks.json") for name in labels(players)}
    reasons, checkpoints, corrections, diagnostics = [], {}, {}, {}
    selected_correction_failures, strict_correction_failures = [], []
    for name, report in reports.items():
        if (report.get("schema") != 1 or report.get("session") != session or not report.get("complete")
                or not report.get("connected") or not report.get("initialSyncComplete") or report.get("errors") != 0):
            reasons.append(f"{name}: missing, incomplete, wrong-session, unsynchronized, or errored probe")
    for stage in CHECKPOINTS:
        evidence = checkpoint_evidence(reports, players, stage)
        checkpoints[stage] = evidence
        if evidence is None:
            reasons.append(f"{stage}: missing exact block totals/chunk/falling/snapshot equality at a common revision")
    fall_coverage, fall_failures = validate_fall_fixture(reports, players)
    reasons.extend(fall_failures)
    late = checkpoints.get("late")
    frozen = checkpoints.get("frozen")
    cleared = checkpoints.get("cleared")
    rebuilt = checkpoints.get("rebuilt")
    if late and late["host"]["total"] < 128:
        reasons.append("Late join did not restore a populated terrain")
    if frozen and (frozen["host"]["fallingCells"] <= 0 or frozen["host"].get("phase") != "Finished"):
        reasons.append("Frozen checkpoint lacks a real falling state and canonical finished round")
    if frozen and fall_coverage and (frozen["host"]["round"] != fall_coverage["round"]
            or frozen["host"]["revision"] < fall_coverage["finishRequestRevision"]
            or frozen["host"]["pink"] != fall_coverage["expectedPink"]
            or frozen["host"]["blue"] != fall_coverage["expectedBlue"]):
        reasons.append("Frozen checkpoint does not conserve the proven finite current-round falling fixture")
    reconnect = reports.get("client-1", {})
    if (not all(reconnect.get(key) is True for key in ("reconnectRequested", "reconnectDisconnected", "reconnectCleanup", "reconnectSuccess"))
            or reconnect.get("connectionGenerations", 0) < 2):
        reasons.append("Reconnect lacks clean teardown, a new match instance, and successful connection")
    reconnected = checkpoints.get("reconnected")
    if reconnected and reconnected["client-1"].get("connectionGeneration", 0) < 2:
        reasons.append("Reconnected snapshot was recorded before the new connection")
    if frozen and reconnected and fingerprint(frozen["host"]) != fingerprint(reconnected["host"]):
        reasons.append("Frozen terrain changed across reconnect")
    if cleared and (cleared["host"]["total"] != 0 or cleared["host"]["fallingCells"] != 0
                    or not frozen or cleared["host"]["round"] <= frozen["host"]["round"]):
        reasons.append("Round clear lacks a new generation with zero stable and falling blocks")
    if rebuilt and (rebuilt["host"]["total"] < 128 or not cleared or rebuilt["host"]["round"] <= cleared["host"]["round"]):
        reasons.append("No populated synchronized rebuild in a later round")
    for name in labels(players)[1:]:
        evidence_complete, groups = reconciliation_groups(reports.get(name, {}))
        values = [group["metres"] for group in groups]
        friendly = sum(group["friendly"] for group in groups)
        gate_applies = rtt == 100 and loss == 1
        p95, p99 = percentile(values, .95), percentile(values, .99)
        selected_gate_passed = (p95 is not None and p99 is not None and p95 <= settings["p95"] and p99 <= settings["p99"])
        strict_settings = ACCEPTANCE_PROFILES["strict"]
        strict_gate_passed = (p95 is not None and p99 is not None and p95 <= strict_settings["p95"] and p99 <= strict_settings["p99"])
        corrections[name] = {"uniqueMovingAckSamples": len(values), "friendlySupportSamples": friendly,
                             "p95Metres": p95, "p99Metres": p99, "approvedGateApplies": gate_applies,
                             "acceptanceProfile": acceptance_profile,
                             "p95LimitMetres": settings["p95"] if gate_applies else None,
                             "p99LimitMetres": settings["p99"] if gate_applies else None,
                             "selectedGatePassed": selected_gate_passed if gate_applies else None,
                             "strictP95LimitMetres": strict_settings["p95"] if gate_applies else None,
                             "strictP99LimitMetres": strict_settings["p99"] if gate_applies else None,
                             "strictGatePassed": strict_gate_passed if gate_applies else None,
                             "source": "complete immutable reconcile events; maximum raw correction per moving ACK",
                             "evidenceComplete": evidence_complete, "pendingAckGroups": sum(group["pending"] for group in groups),
                             "sameAckRecoveryGroups": sum(group["sameAckRecovery"] for group in groups),
                             "humanPositionDiagnosticP99Metres": percentile([group["humanMetres"] for group in groups], .99)}
        if not evidence_complete:
            reasons.append(f"{name}: incomplete immutable reconciliation event evidence")
        if len(values) < 200 or friendly < 100:
            reasons.append(f"{name}: insufficient dynamic movement acknowledgements ({len(values)}/200; friendly {friendly}/100)")
        if gate_applies and not selected_gate_passed:
            p95_limit = format(settings["p95"], ".2f").lstrip("0")
            p99_limit = format(settings["p99"], ".2f").lstrip("0")
            gate_label = "approved" if acceptance_profile == "strict" else acceptance_profile
            reason = f"{name}: {gate_label} 100ms/1% correction gate exceeded (P95 <= {p95_limit}m, P99 <= {p99_limit}m)"
            selected_correction_failures.append(reason); reasons.append(reason)
        if gate_applies and not strict_gate_passed:
            strict_correction_failures.append(f"{name}: approved 100ms/1% correction gate exceeded (P95 <= .15m, P99 <= .35m)")
    for name in labels(players):
        frame = read_json(directory / f"{name}.frames.json")
        metrics = {metric.get("name"): metric for metric in frame.get("metrics", [])}
        interval = metrics.get("frameIntervalMs", {})
        diagnostics[name] = {"diagnosticOnly": True, "normalCameraFrames": frame.get("renderedFrames", 0),
                             "complete": frame.get("complete", False), "width": frame.get("width"), "height": frame.get("height"),
                             "frameP95Ms": interval.get("p95"), "frameP99Ms": interval.get("p99"),
                             "gpuTimingAvailable": frame.get("gpuTimingAvailable", False)}
        if (not frame.get("complete") or frame.get("errors") != 0 or not frame.get("graphics")
                or frame.get("width") != 1920 or frame.get("height") != 1080
                or frame.get("renderedFrames", 0) < 300 or interval.get("samples", 0) < 300):
            reasons.append(f"{name}: insufficient normal 1080p frame diagnostic evidence")
    proxy = read_json(directory / "proxy.json")
    if (proxy.get("rtt_ms") != rtt or proxy.get("loss_percent") != loss
            or proxy.get("up", {}).get("forwarded", 0) == 0 or proxy.get("down", {}).get("forwarded", 0) == 0):
        reasons.append("Missing applied impairment and bidirectional traffic evidence")
    if loss > 0 and sum(proxy.get(direction, {}).get("dropped", 0) for direction in ("up", "down")) == 0:
        reasons.append("Configured packet loss did not actually drop any datagrams")
    if exit_codes is None or any(exit_codes.get(name) != 0 for name in labels(players)) or exit_codes.get("proxy") != 0:
        reasons.append("Missing successful independent process exits")
    strict_reasons = reasons if acceptance_profile == "strict" else (
        [reason for reason in reasons if reason not in selected_correction_failures] + strict_correction_failures)
    return {"players": players, "rttMs": rtt, "lossPercent": loss, "session": session,
            "acceptanceProfile": acceptance_profile,
            "networkPassed": not reasons, "fullAcceptancePassed": False, "failureReasons": reasons,
            "strictNetworkPassed": not strict_reasons, "strictFailureReasons": strict_reasons,
            "checkpoints": checkpoints, "fallFixture": fall_coverage, "corrections": corrections, "frameDiagnostics": diagnostics,
            "performanceAcceptance": "separate benchmark with the same acceptance profile required; network frame reports are diagnostic only",
            "exitCodes": exit_codes, "impairment": proxy, "reports": reports}


def compact_motor_reports(result, directory):
    """After formal evaluation, reference complete opt-in motor lists in their original files."""
    reports = dict(result.get("reports", {}))
    changed = False
    for name, report in reports.items():
        events = report.get("motorBranchEvents")
        if report.get("motorTraceEnabled") is not True or not isinstance(events, list):
            continue
        source = (directory / f"{name}.blocks.json").resolve()
        digest = hashlib.sha256()
        try:
            with source.open("rb") as handle:
                for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                    digest.update(chunk)
            size = source.stat().st_size
        except OSError as exception:
            # Never discard the only retained copy when a source reference cannot be verified.
            copy = dict(report)
            copy["motorBranchEventsCompactionError"] = str(exception)
            reports[name] = copy; changed = True
            continue
        copy = dict(report)
        copy.pop("motorBranchEvents")
        copy["motorBranchEventsReference"] = {
            "sourcePath": str(source), "sourceSha256": digest.hexdigest(), "sourceBytes": size,
            "sourceListField": "motorBranchEvents", "referenceOnly": True,
            "eventSchema": report.get("motorBranchEventSchema"),
            "recordedEventCount": report.get("motorBranchEventCount"), "retainedEventCount": len(events),
            "eventsIncluded": report.get("motorBranchEventsIncluded"),
            "eventsComplete": report.get("motorBranchEventsComplete"),
            "providerErrors": report.get("motorBranchProviderErrors"),
            "consumerErrors": report.get("motorBranchConsumerErrors")}
        reports[name] = copy; changed = True
    if not changed:
        return result
    copy = dict(result); copy["reports"] = reports
    return copy


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def background_player_environment():
    """Never change the user's global environment or permit automated cursor capture."""
    environment = os.environ.copy()
    environment["SPLATOON_DISABLE_SYSTEM_CURSOR"] = "1"
    return environment


def require_background_player_desktop():
    """Fail before launching Players if Windows desktop isolation is unavailable."""
    if os.name != "nt":
        return
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentThreadId.argtypes = []
    kernel32.GetCurrentThreadId.restype = wintypes.DWORD
    user32.GetThreadDesktop.argtypes = [wintypes.DWORD]
    user32.GetThreadDesktop.restype = wintypes.HANDLE
    user32.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    user32.OpenInputDesktop.restype = wintypes.HANDLE
    user32.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                                ctypes.c_void_p, wintypes.DWORD,
                                                ctypes.POINTER(wintypes.DWORD)]
    user32.GetUserObjectInformationW.restype = wintypes.BOOL
    user32.CloseDesktop.argtypes = [wintypes.HANDLE]
    user32.CloseDesktop.restype = wintypes.BOOL

    def name(handle):
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        buffer = ctypes.create_unicode_buffer(256)
        needed = wintypes.DWORD()
        if not user32.GetUserObjectInformationW(handle, 2, buffer, ctypes.sizeof(buffer),
                                                ctypes.byref(needed)):
            raise ctypes.WinError(ctypes.get_last_error())
        if not buffer.value:
            raise RuntimeError("Cannot identify the Windows desktop; refusing Player launch")
        return buffer.value

    # GetThreadDesktop is borrowed; close only the handle opened by this function.
    thread_name = name(user32.GetThreadDesktop(kernel32.GetCurrentThreadId()))
    input_desktop = user32.OpenInputDesktop(0, False, 0x0001)
    try:
        input_name = name(input_desktop)
    finally:
        if input_desktop and not user32.CloseDesktop(input_desktop):
            raise ctypes.WinError(ctypes.get_last_error())
    if thread_name.casefold() in ("default", "winlogon") or thread_name.casefold() == input_name.casefold():
        raise RuntimeError("Background Players must run on a non-input desktop; refusing foreground launch")


def hidden_process(command, cwd):
    options = {"cwd": cwd, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL,
               "env": background_player_environment()}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        options.update(startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW)
    return subprocess.Popen([str(part) for part in command], **options)


def visible_graphics_process(command, cwd):
    """A normal visible Player window is required for backbuffer diagnostics."""
    if any(str(part) in ("-batchmode", "-nographics") for part in command):
        raise ValueError("Visible graphics Players cannot use headless flags")
    require_background_player_desktop()
    options = dict(cwd=cwd, env=background_player_environment())
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 4  # SW_SHOWNOACTIVATE; normal window on the isolated desktop.
        options.update(startupinfo=startup)
    return subprocess.Popen([str(part) for part in command], **options)


def run_case(exe, directory, players, rtt, loss, timeout=420, traversal_seconds=35, motor_trace=False, terrain_dependencies=False, acceptance_profile="strict"):
    acceptance_settings(acceptance_profile)
    require_background_player_desktop()
    directory.mkdir(parents=True, exist_ok=True)
    session = uuid.uuid4().hex
    control = directory / "control.txt"
    upstream, front = free_port(), free_port()
    processes, commands, checkpoints = {}, [], []
    command_id = 0
    control_write_retries = []
    started = time.monotonic()

    def reports(names):
        return [read_json(directory / f"{name}.blocks.json") for name in names]

    def wait(predicate, description, seconds=45, allow_player_exits=False):
        end = min(started + timeout, time.monotonic() + seconds)
        while time.monotonic() < end:
            if predicate():
                return
            failed = {name: process.poll() for name, process in processes.items()
                      if process.poll() is not None and name != "proxy"}
            if failed and not allow_player_exits:
                raise RuntimeError(f"{description}: Player exited early {failed}")
            write_json(directory / "progress.json", {"session": session, "waitingFor": description,
                       "elapsedSeconds": round(time.monotonic() - started, 1)})
            time.sleep(.2)
        raise RuntimeError(f"Timed out: {description}")

    def stage(name):
        nonlocal command_id
        command_id += 1
        temporary = control.with_suffix(".writing")
        temporary.write_text(f"{command_id}\n{name}\n", encoding="utf-8")
        for attempt in range(5):
            try:
                temporary.replace(control)
                break
            except OSError as exception:
                # Match write_json's bounded Windows replacement policy. Keep
                # one complete payload and command id throughout every retry.
                control_write_retries.append({"stage": name, "commandId": command_id,
                    "attempt": attempt + 1, "winerror": getattr(exception, "winerror", None),
                    "error": str(exception), "elapsedSeconds": round(time.monotonic() - started, 2)})
                if (os.name != "nt"
                        or getattr(exception, "winerror", None) not in (5, 32, 33)
                        or attempt == 4):
                    raise
                time.sleep(.02 * (2 ** attempt))
        checkpoints.append({"stage": name, "elapsedSeconds": round(time.monotonic() - started, 2)})

    def start_player(index):
        name = "host" if index == 0 else f"client-{index}"
        command = [exe, "-noSystemCursor", "-force-d3d11", "-screen-fullscreen", "0", "-screen-width", 1920, "-screen-height", 1080,
                   "-lanSmokeHost" if index == 0 else "-lanSmokeClient", "-lanAddress", "127.0.0.1",
                   "-lanPort", upstream if index == 0 else front, "-inkSmokeCase", "observer", "-inkLabel", name,
                   "-blockNetworkProbe", "-blockProbeOutput", directory / f"{name}.blocks.json",
                   "-blockProbeControl", control, "-blockProbeSession", session, "-blockProbePlayers", players,
                   "-blockAcceptanceProfile", acceptance_profile,
                   "-blockProbeTimeout", timeout, "-frameProbe", 30, "-frameProbeWarmup", 5,
                   "-frameProbePlayers", players, "-frameProbeTimeout", timeout,
                   "-frameProbeOutput", directory / f"{name}.frames.json", "-logFile", directory / f"{name}.log"]
        if index == 1:
            command.append("-blockReconnectParticipant")
        if motor_trace:
            command.append("-blockMotorTrace")
        if terrain_dependencies:
            command.append("-blockTerrainDependencies")
        commands.append({"label": name, "windowMode": "normal-visible-graphics", "arguments": [str(part) for part in command]})
        processes[name] = visible_graphics_process(command, exe.parent)

    def synchronized(name, count=players, names=None):
        names = names or labels(count)
        states = [report.get("current", {}) for report in reports(names)]
        return aligned(states, count, name)

    error = None
    cleanup_errors = []
    try:
        proxy_command = [sys.executable, ROOT / "Tools/NetworkOptimization/udp_proxy.py", "--listen", front,
                         "--target", upstream, "--rtt", rtt, "--jitter", 0, "--loss", loss,
                         "--seed", 20261010 + players * 1000 + rtt * 10 + loss, "--seconds", timeout + 10,
                         "--output", directory / "proxy.json", "--stop-file", directory / "stop-proxy"]
        processes["proxy"] = hidden_process(proxy_command, ROOT)
        start_player(0)
        wait(lambda: reports(["host"])[0].get("initialSyncComplete") is True, "host initial sync")
        for index in range(1, players - 1):
            start_player(index)
        early_names = labels(players - 1)
        wait(lambda: all(report.get("initialSyncComplete") and report.get("current", {}).get("players") == players - 1
                         for report in reports(early_names)), "all early participants joined")
        stage("seed")
        wait(lambda: reports(["host"])[0].get("current", {}).get("total", 0) >= 128, "populated pre-join blocks")
        stage("initial")
        wait(lambda: synchronized("initial", players - 1, early_names), "pre-join settled terrain")
        start_player(players - 1)
        stage("late")
        wait(lambda: synchronized("late"), "populated late-join exact hashes")
        stage("round-start")
        wait(lambda: all(report.get("current", {}).get("phase") == "Playing" and report.get("current", {}).get("total") == 0
                         for report in reports(labels(players))), "round starts with cleared terrain")
        stage("seed-round")
        wait(lambda: reports(["host"])[0].get("current", {}).get("total", 0) >= 128 * players, "round traversal platforms")
        stage("traverse")
        wait(lambda: time.monotonic() - started >= checkpoints[-1]["elapsedSeconds"] + traversal_seconds,
             "dynamic terrain traversal sampling", traversal_seconds + 5)
        # Legacy Update polling can miss same-ACK events and is not the formal
        # sample population. Final evaluation below still requires >=200 moving
        # and >=100 friendly ACK groups from the complete immutable event stream.
        stage("drain")
        wait(lambda: drain_ready({name: report for name, report in zip(labels(players), reports(labels(players)))}, players),
             "traversal input acknowledgements and intact queue/projectile/falling drain")
        stage("fall")
        wait(lambda: not validate_fall_fixture({name: report for name, report in zip(labels(players), reports(labels(players)))}, players)[1],
             "all finite 22m commands complete with actual newly published falling cells")
        stage("finish")
        wait(lambda: reports(["host"])[0].get("current", {}).get("phase") == "Finished", "canonical round freeze")
        stage("frozen")
        wait(lambda: synchronized("frozen") and reports(["host"])[0].get("current", {}).get("fallingCells", 0) > 0,
             "frozen falling-state exact hashes")
        stage("reconnect")
        wait(lambda: reports(["client-1"])[0].get("reconnectDisconnected") is True, "client clean leave")
        wait(lambda: reports(["client-1"])[0].get("reconnectSuccess") is True
             and reports(["client-1"])[0].get("connectionGenerations", 0) >= 2, "new client connection")
        stage("reconnected")
        wait(lambda: synchronized("reconnected"), "reconnected frozen terrain exact hashes")
        stage("clear")
        wait(lambda: reports(["host"])[0].get("current", {}).get("phase") == "Practice", "returned to room")
        stage("cleared")
        wait(lambda: synchronized("cleared") and reports(["host"])[0].get("current", {}).get("total") == 0,
             "round clear: zero stable/falling blocks on every peer")
        stage("restart")
        wait(lambda: all(report.get("current", {}).get("phase") == "Playing" for report in reports(labels(players))), "next round")
        stage("seed-rebuild")
        wait(lambda: reports(["host"])[0].get("current", {}).get("total", 0) >= 128 * players, "post-clear rebuild")
        stage("rebuilt")
        wait(lambda: synchronized("rebuilt"), "post-clear exact terrain hashes")
        # Persist each client's actual final synchronized state before the host
        # shuts down the transport; a shared simultaneous quit races disconnect.
        stage("quit-clients")
        wait(lambda: all(processes[name].poll() is not None for name in labels(players)[1:]), "Clients persist synchronized reports and exit", 120 if motor_trace else 15, True)
        stage("quit")
        wait(lambda: processes["host"].poll() is not None, "Host clean exit", 120 if motor_trace else 15, True)
    except Exception as exception:
        error = str(exception)
    finally:
        def cleanup_failure(name, action, exception):
            cleanup_errors.append(f"{name} {action}: {type(exception).__name__}: {exception}")

        def terminate_owned(name, process):
            try:
                if process.poll() is None:
                    process.terminate()
            except Exception as exception:
                cleanup_failure(name, "terminate", exception)

        def wait_or_kill_owned(name, process):
            try:
                process.wait(timeout=10)
                return
            except subprocess.TimeoutExpired:
                pass
            except Exception as exception:
                cleanup_failure(name, "wait", exception)
            try:
                process.kill()
            except Exception as exception:
                cleanup_failure(name, "kill", exception)
            try:
                process.wait(timeout=10)
            except Exception as exception:
                cleanup_failure(name, "wait after kill", exception)

        try:
            if error and any(process.poll() is None for name, process in processes.items() if name != "proxy"):
                # Preserve the primary harness failure and permit observers to
                # finish. A second command/file failure must not skip teardown.
                stage("quit-clients")
                cleanup_deadline = time.monotonic() + 12
                while time.monotonic() < cleanup_deadline and any(
                        process.poll() is None for name, process in processes.items() if name not in ("host", "proxy")):
                    time.sleep(.2)
                stage("quit")
                cleanup_deadline = time.monotonic() + 12
                host = processes.get("host")
                while host is not None and host.poll() is None and time.monotonic() < cleanup_deadline:
                    time.sleep(.2)
        except Exception as exception:
            cleanup_failure("control", "graceful quit", exception)
        finally:
            # These are only this case's Popen handles; never enumerate by name.
            for name, process in processes.items():
                if name != "proxy":
                    terminate_owned(name, process)
            for name, process in processes.items():
                if name != "proxy":
                    wait_or_kill_owned(name, process)
            # Natural proxy exit flushes impairment counters. Failure to write
            # its stop file still falls through to bounded owned-handle cleanup.
            proxy = processes.get("proxy")
            if proxy is not None:
                try:
                    (directory / "stop-proxy").write_text("finished", encoding="utf-8")
                except Exception as exception:
                    cleanup_failure("proxy", "write stop file", exception)
                try:
                    proxy.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    terminate_owned("proxy", proxy)
                    wait_or_kill_owned("proxy", proxy)
                except Exception as exception:
                    cleanup_failure("proxy", "natural wait", exception)
                    terminate_owned("proxy", proxy)
                    wait_or_kill_owned("proxy", proxy)
            codes = {name: process.returncode for name, process in processes.items()}
    result = evaluate_case(directory, players, rtt, loss, session, codes, acceptance_profile)
    result.update(commands=commands, stages=checkpoints, harnessError=error,
                  cleanupErrors=cleanup_errors, controlWriteRetries=control_write_retries,
                  durationSeconds=time.monotonic() - started)
    failures = ([error] if error else []) + ["Owned cleanup: " + item for item in cleanup_errors]
    if failures:
        result["networkPassed"] = False
        result["strictNetworkPassed"] = False
        for failure in failures:
            result["failureReasons"].append(failure)
            # Strict mode shares this list; append each failure exactly once.
            if result["strictFailureReasons"] is not result["failureReasons"]:
                result["strictFailureReasons"].append(failure)
    if motor_trace:
        result = compact_motor_reports(result, directory)
    write_json(directory / "case.json", result)
    print(f"{players} Players RTT {rtt}ms loss {loss}% profile {acceptance_profile}: networkPassed={result['networkPassed']}, strictNetworkPassed={result['strictNetworkPassed']}", flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "Reports/BlockTerrain/Network")
    parser.add_argument("--acceptance-profile", choices=tuple(ACCEPTANCE_PROFILES), default="strict",
                        help="strict: original 18 cases/P99 .35m; prototype-v1: six paired conditions/P99 .60m; both retain P95 .15m")
    parser.add_argument("--players", nargs="+", type=int, choices=(2, 8), help="Filter the selected profile's matrix by Player count")
    parser.add_argument("--rtt", nargs="+", type=int, choices=(0, 100, 200), help="Filter the selected profile's matrix by RTT")
    parser.add_argument("--loss", nargs="+", type=int, choices=(0, 1, 3), help="Filter the selected profile's matrix by packet loss")
    parser.add_argument("--timeout", type=int, default=420)
    parser.add_argument("--traversal-seconds", type=int, default=35)
    parser.add_argument("--motor-trace", action="store_true",
                        help="Opt-in motor branch diagnostics on every Player; adds allocations, so run formal performance acceptance without this flag")
    parser.add_argument("--terrain-dependencies", action="store_true",
                        help="Opt-in bounded replay-gate and transfer/publication sidecar; formal raw gates unchanged; not performance acceptance")
    args = parser.parse_args()
    if args.traversal_seconds < 35 or args.timeout < 180:
        parser.error("At least 35 seconds traversal and 180 seconds case timeout are required; insufficient samples still fail")
    selected = selected_cases(args.acceptance_profile, args.players, args.rtt, args.loss)
    if not selected:
        parser.error("No cases match the selected acceptance profile and filters")
    exe = args.player.resolve(strict=True)
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    results = []
    for players, rtt, loss in sorted(selected):
        case = output / f"p{players}-rtt{rtt}-loss{loss}" / uuid.uuid4().hex[:8]
        results.append(run_case(exe, case, players, rtt, loss, args.timeout, args.traversal_seconds,
                                args.motor_trace, args.terrain_dependencies, args.acceptance_profile))
        summary = matrix_summary(results, selected, args.acceptance_profile)
        write_json(output / "summary.json", summary)
    return 0 if all(result["networkPassed"] for result in results) else 4


if __name__ == "__main__":
    raise SystemExit(main())
