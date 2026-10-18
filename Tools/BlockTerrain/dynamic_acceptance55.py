"""Independent Player acceptance for 30 cm surface deposits and free rigidbodies.

Run this through the existing isolated-desktop launcher. It never builds Unity or
switches desktops. Network mode covers the six approved paired impairments.
Natural and blast8 modes use eight actual Players and their own workload oracles;
they do not replace the historical 250k/100k performance workloads.
"""
import argparse
import binascii
import copy
import hashlib
import math
from pathlib import Path
import struct
import sys
import time
import uuid
import zlib

import network_acceptance as legacy

ROOT = Path(__file__).resolve().parents[2]
NETWORK_MATRIX = frozenset((players, rtt, loss) for players in (2, 8)
                           for rtt, loss in ((0, 0), (100, 1), (200, 3)))
CHECKPOINTS = ("late", "destroyed", "frozen", "reconnected", "cleared", "rebuilt")
FIELDS = ("round", "terrainRound", "revision", "dynamicRevision", "surfaces", "dynamic",
          "pink", "blue", "scorePink", "scoreBlue", "surfaceHash", "identityHash")
_PNG_STATS = {}


def numeric(value):
    return type(value) in (int, float) and math.isfinite(value)


def digest(value):
    return isinstance(value, str) and len(value) == 64 and all(character in "0123456789abcdef" for character in value)


def graphics_failures(report):
    if (report.get("graphics") is not True or report.get("graphicsApi") != "Direct3D12"
            or report.get("normalMainCamera") is not True or report.get("width") != 1920
            or report.get("height") != 1080 or type(report.get("renderedFrames")) is not int
            or report["renderedFrames"] <= 0):
        return ["missing rendered Direct3D12 1920x1080 normal main camera evidence"]
    return []


def png_stats(path):
    """Decode actual saved PNG pixels without relying on Player-provided metadata."""
    data = Path(path).read_bytes()
    sha = hashlib.sha256(data).hexdigest()
    if sha in _PNG_STATS:
        return dict(_PNG_STATS[sha])
    if len(data) > 64 * 1024 * 1024 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("invalid PNG signature or size")
    offset, compressed, palette, header, ended = 8, bytearray(), None, None, False
    while offset < len(data):
        if offset + 12 > len(data):
            raise ValueError("truncated PNG chunk")
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data):
            raise ValueError("truncated PNG payload")
        payload = data[offset + 8:end - 4]
        if binascii.crc32(kind + payload) & 0xffffffff != struct.unpack_from(">I", data, end - 4)[0]:
            raise ValueError("PNG chunk CRC mismatch")
        if header is None and kind != b"IHDR":
            raise ValueError("PNG does not start with IHDR")
        if kind == b"IHDR":
            if header is not None or length != 13:
                raise ValueError("invalid PNG header")
            header = struct.unpack(">IIBBBBB", payload)
        elif kind == b"PLTE":
            if length == 0 or length % 3 or length > 768:
                raise ValueError("invalid PNG palette")
            palette = [tuple(payload[index:index + 3]) for index in range(0, length, 3)]
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            if length != 0 or end != len(data):
                raise ValueError("invalid PNG ending")
            ended = True
            break
        offset = end
    if not ended or header is None or not compressed:
        raise ValueError("incomplete PNG")
    width, height, depth, colour, compression, filtering, interlace = header
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(colour)
    if ((width, height) != (1920, 1080) or depth != 8 or channels is None
            or compression or filtering or interlace or (colour == 3 and palette is None)):
        raise ValueError("PNG must be an actual 1920x1080 noninterlaced 8-bit frame")
    stride = width * channels
    expected = height * (stride + 1)
    inflater = zlib.decompressobj()
    decoded = inflater.decompress(compressed, expected + 1)
    if len(decoded) != expected or not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
        raise ValueError("PNG decompressed pixel length mismatch")
    previous = bytearray(stride)
    nonblack, colours = 0, set()
    for y in range(height):
        start = y * (stride + 1)
        filter_type = decoded[start]
        row = bytearray(decoded[start + 1:start + 1 + stride])
        if filter_type not in range(5):
            raise ValueError("invalid PNG row filter")
        if filter_type:
            for index in range(stride):
                left = row[index - channels] if index >= channels else 0
                up = previous[index]
                upper_left = previous[index - channels] if index >= channels else 0
                if filter_type == 1:
                    predictor = left
                elif filter_type == 2:
                    predictor = up
                elif filter_type == 3:
                    predictor = (left + up) // 2
                else:
                    estimate = left + up - upper_left
                    dl, du, dul = abs(estimate - left), abs(estimate - up), abs(estimate - upper_left)
                    predictor = left if dl <= du and dl <= dul else up if du <= dul else upper_left
                row[index] = (row[index] + predictor) & 255
        for index in range(0, stride, channels):
            if colour == 3:
                if row[index] >= len(palette):
                    raise ValueError("PNG palette index out of range")
                red, green, blue = palette[row[index]]
            elif colour in (0, 4):
                red = green = blue = row[index]
            else:
                red, green, blue = row[index:index + 3]
            rgb = (red << 16) | (green << 8) | blue
            nonblack += rgb != 0
            colours.add(rgb)
        previous = row
    result = dict(sha256=sha, width=width, height=height, nonBlackPixels=nonblack, distinctColors=len(colours))
    if len(_PNG_STATS) >= 32:
        _PNG_STATS.clear()
    _PNG_STATS[sha] = result
    return dict(result)


def visual_oracle(reports, players, report_directory=None):
    failures, evidence = [], {}
    for name in legacy.labels(players):
        visual = reports.get(name, {}).get("visual", {})
        errors = []
        if visual.get("complete") is not True or visual.get("valid") is not True:
            errors.append("missing or invalid completed visual probe: " + str(visual.get("error", "")))
        for source in ("collider", "draw"):
            minimum, maximum = visual.get(source + "EdgeMin"), visual.get(source + "EdgeMax")
            if (type(visual.get(source + "Samples")) is not int or visual[source + "Samples"] <= 0
                    or not isinstance(minimum, list) or not isinstance(maximum, list)
                    or len(minimum) != 3 or len(maximum) != 3
                    or any(not numeric(value) or not math.isclose(value, .3, abs_tol=1e-5, rel_tol=0)
                           for value in minimum + maximum)
                    or any(low > high for low, high in zip(minimum, maximum))):
                errors.append(source + ": missing actual world-axis 30 cm geometry measurements")
        if (type(visual.get("drawInstances")) is not int or visual["drawInstances"] <= 0
                or visual.get("shaderSupported") is not True or visual.get("instancingSupported") is not True):
            errors.append("missing actual supported instanced geometry draw evidence")
        visible_surface, visible_dynamic = visual.get("visibleSurface"), visual.get("visibleDynamic")
        if (type(visible_surface) is not int or type(visible_dynamic) is not int
                or visible_surface < 0 or visible_dynamic < 0 or visible_surface + visible_dynamic <= 0
                or (name == "host" and (visible_surface <= 0 or visible_dynamic <= 0))):
            errors.append("normal camera must see blocks; host must see surface and dynamic blocks")
        if name == "host":
            if any(type(visual.get(key)) is not int or visual[key] <= 0
                   for key in ("surfacePublishedCells", "surfaceGridSamples", "surfaceTopSamples")):
                errors.append("missing actual published surface grid and top-face samples")
            if any(not numeric(visual.get(key)) or not 0 <= visual[key] <= 1e-5
                   for key in ("surfaceGridMaxError", "surfaceTopMaxError")):
                errors.append("published surface grid or 30 cm top face differs from the world geometry")
        image = {}
        try:
            image_path = visual.get("screenshotPath")
            if not isinstance(image_path, str) or not Path(image_path).is_absolute() or not Path(image_path).is_file():
                raise ValueError("screenshot must name an existing absolute file")
            if report_directory is not None and Path(image_path).resolve() != (report_directory / f"{name}.dynamic.visual.png").resolve():
                raise ValueError("screenshot is not the current Player's report sibling")
            image = png_stats(image_path)
            if (not digest(visual.get("screenshotSha256")) or image["sha256"] != visual["screenshotSha256"]
                    or visual.get("screenshotWidth") != image["width"] or visual.get("screenshotHeight") != image["height"]
                    or type(visual.get("screenshotNonBlackPixels")) is not int
                    or visual["screenshotNonBlackPixels"] != image["nonBlackPixels"]
                    or type(visual.get("screenshotDistinctColors")) is not int
                    or visual["screenshotDistinctColors"] != image["distinctColors"]):
                errors.append("saved screenshot hash, dimensions or actual pixels disagree with the report")
            if image["nonBlackPixels"] <= 0 or image["distinctColors"] <= 1:
                errors.append("saved screenshot is blank or uniform")
        except (OSError, ValueError, zlib.error, struct.error) as exception:
            errors.append("saved screenshot: " + str(exception))
        failures.extend(name + ": " + error for error in errors)
        evidence[name] = dict(passed=not errors, failures=errors, screenshot=image, measured=visual)
    return dict(passed=not failures, failures=failures, players=evidence,
                scope="actual collider transforms and submitted instanced mesh matrices plus independently decoded normal backbuffer PNG; frustum visibility is supplemented by visual review")


def valid_state(state, players):
    if not isinstance(state, dict) or not all(key in state for key in FIELDS):
        return False
    integers = ("round", "terrainRound", "revision", "dynamicRevision", "surfaces", "dynamic", "pink", "blue", "scorePink", "scoreBlue")
    return (all(type(state[key]) is int and state[key] >= 0 for key in integers)
            and state.get("ready") is True and state.get("players") == players
            and state.get("failures") == 0 and state.get("pending") == 0
            and state["round"] == state["terrainRound"]
            and state["pink"] + state["blue"] == state["surfaces"] + state["dynamic"]
            and state["scorePink"] == state["pink"] and state["scoreBlue"] == state["blue"]
            and all(digest(state[key]) for key in ("surfaceHash", "identityHash")))


def checkpoint(reports, stage, players):
    keys = FIELDS + (("frozenHash", "frozenAt") if stage in ("frozen", "reconnected") else ())
    candidates = []
    for name in legacy.labels(players):
        by_key = {}
        for state in reports.get(name, {}).get("states", []):
            if state.get("stage") != stage or not valid_state(state, players):
                continue
            if stage in ("frozen", "reconnected") and (state.get("frozen") is not True
                    or state.get("phase") != "Finished" or not numeric(state.get("frozenAt"))
                    or state["frozenAt"] <= 0 or not digest(state.get("frozenHash"))):
                continue
            by_key[tuple(state.get(key) for key in keys)] = state
        candidates.append(by_key)
    if not candidates or any(not candidate for candidate in candidates):
        return None
    shared = set(candidates[0]).intersection(*(set(candidate) for candidate in candidates[1:]))
    if not shared:
        return None
    chosen = max(shared, key=lambda row: (row[0], row[2], row[3]))
    return {name: candidate[chosen] for name, candidate in zip(legacy.labels(players), candidates)}


def current_checkpoint(reports, stage, players, command=None):
    """An active control barrier must never be satisfied by an older saved stage."""
    current = {}
    commands = set()
    for name in legacy.labels(players):
        state = reports.get(name, {}).get("current", {})
        if (not isinstance(state, dict) or state.get("stage") != stage
                or type(state.get("command")) is not int or state["command"] <= 0
                or command is not None and state["command"] != command):
            return None
        commands.add(state["command"])
        current[name] = dict(states=[state])
    if len(commands) != 1:
        return None
    return checkpoint(current, stage, players)


def visual_seed_observation(reports, players):
    """Retain authoritative payments before round-reset counters can erase them."""
    report = reports.get("host", {})
    state = report.get("current", {})
    observation = {key: state.get(key) for key in
                   ("stage", "command", "round", "players", "surfaces", "dynamic", "pink", "blue")}
    observation.update(errors=report.get("errors"), materialRefills=report.get("materialRefills"),
                       actors=[{key: actor.get(key) for key in ("owner", "shots", "spentMaterial")}
                               for actor in report.get("actors", []) if actor.get("round") == state.get("round")])
    return observation


def valid_visual_seed_payment(before, after, players):
    if not isinstance(before, dict) or not isinstance(after, dict):
        return False
    if (before.get("stage") != "late" or after.get("stage") != "seed"
            or type(before.get("command")) is not int or type(after.get("command")) is not int
            or not 0 < before["command"] < after["command"]
            or type(before.get("round")) is not int or before["round"] < 0
            or type(after.get("round")) is not int
            or before["round"] != after.get("round")):
        return False
    owners = []
    for row in (before, after):
        if (row.get("players") != players or row.get("errors") != 0 or row.get("materialRefills") != 0
                or any(type(row.get(key)) is not int or row[key] < 0 for key in ("surfaces", "dynamic", "pink", "blue"))
                or row["pink"] + row["blue"] != row["surfaces"] + row["dynamic"]):
            return False
        actors = row.get("actors")
        if not isinstance(actors, list) or len(actors) != players:
            return False
        by_owner = {}
        for actor in actors:
            if (not isinstance(actor, dict) or type(actor.get("owner")) is not int or actor["owner"] < 0
                    or actor["owner"] in by_owner or type(actor.get("shots")) is not int or actor["shots"] < 0
                    or not numeric(actor.get("spentMaterial")) or actor["spentMaterial"] < 0):
                return False
            by_owner[actor["owner"]] = actor
        owners.append(by_owner)
    if owners[0].keys() != owners[1].keys():
        return False
    newly_paid = 0
    for owner, baseline in owners[0].items():
        paid = owners[1][owner]
        if (paid["shots"] <= 0 or paid["spentMaterial"] <= 0 or paid["shots"] < baseline["shots"]
                or paid["spentMaterial"] < baseline["spentMaterial"]):
            return False
        # Already paid actors may be legitimately muzzle-blocked by their first patch.
        if baseline["shots"] <= 0 or baseline["spentMaterial"] <= 0:
            if paid["shots"] <= baseline["shots"] or paid["spentMaterial"] <= baseline["spentMaterial"]:
                return False
            newly_paid += 1
    # Marginal totals prove both kinds overall and both teams overall. The frozen
    # Player schema has no team-by-kind matrix, so no stronger claim is invented.
    return (newly_paid > 0 and after["surfaces"] > before["surfaces"]
            and after["dynamic"] > before["dynamic"] and after["pink"] > 0 and after["blue"] > 0)


def valid_visual_seed_evidence(evidence, players):
    if not isinstance(evidence, dict) or not valid_visual_seed_payment(evidence.get("before"), evidence.get("after"), players):
        return False
    drained = evidence.get("drained")
    if not isinstance(drained, dict):
        return False
    reports = {name: dict(current=drained.get(name)) for name in legacy.labels(players)}
    aligned = current_checkpoint(reports, "visual-drain", players)
    if aligned is None:
        return False
    host, after = aligned["host"], evidence["after"]
    return (host["command"] > after["command"] and host["round"] == after["round"]
            and all(type(state.get("projectiles")) is int and state["projectiles"] == 0 for state in aligned.values())
            and all(host[key] >= after[key] for key in ("surfaces", "dynamic", "pink", "blue")))


def motion_oracle(reports, players):
    sent = {(row.get("peer"), row.get("round"), row.get("revision"), row.get("tick"), row.get("sequence")): row.get("hash")
            for row in reports.get("host", {}).get("motion", []) if row.get("sending") is True}
    results = {}
    for name in legacy.labels(players)[1:]:
        received = [row for row in reports.get(name, {}).get("motion", [])
                    if row.get("sending") is False and row.get("stage") == "combat"]
        matched = 0
        errors = []
        seen = set()
        for row in received:
            key = (row.get("peer"), row.get("round"), row.get("revision"), row.get("tick"), row.get("sequence"))
            if any(type(value) is not int or value < 0 for value in key) or key[3] == 0 or key[4] == 0 or key in seen:
                errors.append("invalid or repeated motion receipt identity")
            elif sent.get(key) != row.get("hash") or not digest(row.get("hash")):
                errors.append("receipt does not match its exact server packet")
            elif (type(row.get("rows")) is not int or not 1 <= row["rows"] <= 12
                    or type(row.get("appliedRows")) is not int or not 0 <= row["appliedRows"] <= row["rows"]):
                errors.append("invalid or missing actual body application count")
            elif type(row.get("mismatches")) is not int or row["mismatches"] != 0:
                errors.append("applied pose, owner, velocity or angular velocity differs from the received packet")
            elif row["appliedRows"] > 0:
                matched += 1
            seen.add(key)
        results[name] = {"received": len(received), "matchedAppliedPackets": matched,
                         "errors": errors[:20], "passed": matched >= 20 and not errors}
    return {"clients": results, "passed": len(results) == players - 1 and all(row["passed"] for row in results.values()),
            "scope": "same captured server tick, sequence and payload on both peers; client validates actual Body pose and cached authoritative velocity immediately after application"}


def report_failures(reports, players, session, mode):
    failures = []
    for name in legacy.labels(players):
        report = reports.get(name, {})
        if (report.get("schema") != 1 or report.get("session") != session or report.get("mode") != mode
                or report.get("configuredPlayers") != players or report.get("complete") is not True or report.get("errors") != 0):
            failures.append(f"{name}: missing, incomplete, mismatched or erroneous report")
        if (report.get("weaponSimulation") != 46 or report.get("blockProtocol") != 5
                or report.get("dynamicProtocol") != 1 or not numeric(report.get("cellSize"))
                or not math.isclose(report["cellSize"], .3, abs_tol=1e-7, rel_tol=0)):
            failures.append(f"{name}: not the new signed 30 cm gameplay protocol")
        if (report.get("rangeActivationVersion") != 4 or report.get("activationDistanceMetric") != "xz-horizontal"
                or not numeric(report.get("activationRadius")) or report["activationRadius"] != 2
                or not numeric(report.get("deactivationRadius")) or report["deactivationRadius"] != 4):
            failures.append(f"{name}: missing or mismatched 2/4 metre resting-body range activation policy")
        failures.extend(f"{name}: {failure}" for failure in graphics_failures(report))
    return failures


_ENEMY_PLAN_FIELDS = ("session", "command", "connectionGeneration", "round", "owner", "life", "heroRevision",
                      "team", "targetRound", "targetId", "targetOwner")
_ENEMY_INPUT_FIELDS = ("sequence", "inputTick", "inputLife", "inputHeroRevision", "fireSequence", "releaseSequence")
_ENEMY_KINDS = ("input", "authority-input", "weapon", "spawn", "contact", "direct-dynamic-removal")


def _enemy_integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _enemy_plan_key(sample):
    if not isinstance(sample, dict) or not isinstance(sample.get("plan"), dict):
        return None
    plan = sample["plan"]
    if (not isinstance(plan.get("session"), str) or not plan["session"] or plan.get("status") != "ready"
            or any(not _enemy_integer(plan.get(key), 1) for key in ("command", "connectionGeneration", "round", "life", "targetId"))
            or any(not _enemy_integer(plan.get(key)) for key in ("owner", "heroRevision"))
            or type(plan.get("team")) is not int or plan["team"] not in (1, 2)
            or type(plan.get("targetOwner")) is not int or plan["targetOwner"] not in (1, 2)
            or plan["targetOwner"] == plan["team"] or type(plan.get("targetRound")) is not int
            or plan["targetRound"] != plan["round"]):
        return None
    return tuple(plan[key] for key in _ENEMY_PLAN_FIELDS)


def _enemy_input_key(sample):
    if (any(not _enemy_integer(sample.get(key)) for key in _ENEMY_INPUT_FIELDS)
            or sample["sequence"] == 0):
        return None
    return tuple(sample[key] for key in _ENEMY_INPUT_FIELDS)


def _enemy_emission_key(sample, plan_key):
    if any(not _enemy_integer(sample.get(key), 1) for key in ("shotSequence", "action")):
        return None
    return plan_key, sample["shotSequence"], sample["action"]


def _enemy_projectile_key(sample, plan_key):
    emission = _enemy_emission_key(sample, plan_key)
    if emission is None or not _enemy_integer(sample.get("entity"), 1) or not _enemy_integer(sample.get("pellet")):
        return None
    return plan_key, sample["entity"], sample["pellet"], sample["shotSequence"], sample["action"]


def _valid_enemy_execution_chain(witness):
    """Revalidate the preserved six actual observations; a caller's valid flag is insufficient."""
    if not isinstance(witness, dict):
        return False
    rows, indices = witness.get("samples"), witness.get("sampleIndices")
    if (not isinstance(rows, list) or len(rows) != 6 or not isinstance(indices, list) or len(indices) != 6
            or any(not _enemy_integer(index) for index in indices)
            or any(left >= right for left, right in zip(indices, indices[1:]))
            or not _enemy_integer(witness.get("capacity"), 1) or witness["capacity"] > 8192
            or not _enemy_integer(witness.get("overflow")) or witness["overflow"] != 0
            or not _enemy_integer(witness.get("sampleCount"), 6)
            or witness["sampleCount"] > witness["capacity"] or indices[-1] >= witness["sampleCount"]):
        return False
    if any(not isinstance(row, dict) or row.get("kind") != kind for row, kind in zip(rows, _ENEMY_KINDS)):
        return False
    plan_key = _enemy_plan_key(rows[0])
    if plan_key is None or any(_enemy_plan_key(row) != plan_key for row in rows[1:]):
        return False
    plan = rows[0]["plan"]
    if (not isinstance(witness.get("session"), str) or not witness["session"]
            or any(not _enemy_integer(witness.get(key), 1) for key in ("command", "round", "generation"))
            or witness.get("session") != plan["session"] or witness.get("command") != plan["command"]
            or witness.get("round") != plan["round"] or witness.get("generation") != plan["connectionGeneration"]):
        return False
    if any(not numeric(row.get("serverTime")) or row["serverTime"] < 0 for row in rows):
        return False
    if any(left["serverTime"] > right["serverTime"] for left, right in zip(rows, rows[1:])):
        return False
    if any(type(row.get(key)) is not int or row[key] != plan[plan_field]
           for row in rows for key, plan_field in (("round", "round"), ("life", "life"), ("heroRevision", "heroRevision"))):
        return False
    final_input, authority, weapon, spawn, contact, direct = rows
    input_key = _enemy_input_key(final_input)
    if input_key is None or any(_enemy_input_key(row) != input_key for row in (authority, weapon)):
        return False
    if (final_input["inputLife"] != plan["life"] or final_input["inputHeroRevision"] != plan["heroRevision"]
            or any(row.get("fire") is not True or row.get("cancelFire") is not False or row.get("swim") is not False
                   or row.get("alive") is not True or row.get("needsRelease") is not False for row in rows[:3])
            or authority.get("timedOut") is not False or weapon.get("timedOut") is not False
            or not _enemy_integer(authority.get("acknowledged"), 1) or not _enemy_integer(weapon.get("acknowledged"), 1)
            or authority.get("acknowledged") != final_input["sequence"]
            or weapon.get("acknowledged") != final_input["sequence"]
            or not _enemy_integer(authority.get("queuedBefore")) or not _enemy_integer(authority.get("discardedInputs"))
            or authority["discardedInputs"] != 0
            or authority.get("source") != ("oldest queued input" if authority["queuedBefore"] > 0 else "reused last input")):
        return False
    if (weapon.get("standQueried") is not True or weapon.get("canShoot") is not True or weapon.get("emitted") is not True
            or not numeric(weapon.get("materialBefore")) or not numeric(weapon.get("materialAfter"))
            or not 0 <= weapon["materialAfter"] < weapon["materialBefore"]
            or not _enemy_integer(weapon.get("shotSequenceBefore")) or not _enemy_integer(weapon.get("shotSequence"), 1)
            or weapon["shotSequence"] != weapon["shotSequenceBefore"] + 1 or not _enemy_integer(weapon.get("action"), 1)
            or any(not isinstance(weapon.get(key), str) or not weapon[key] for key in ("phaseBefore", "phaseAfter"))):
        return False
    if (any(any(not _enemy_integer(row.get(key), 1) for key in ("sequence", "action", "shotSequence", "entity"))
            or any(not _enemy_integer(row.get(key)) for key in ("inputTick", "pellet")) for row in rows[3:])
            or any(row.get("sequence") != final_input["sequence"] or row.get("inputTick") != final_input["inputTick"]
                   or row.get("action") != weapon["action"] or row.get("shotSequence") != weapon["shotSequence"]
                   for row in rows[3:])
            or any(row.get("entity") != spawn["entity"] or row.get("pellet") != spawn["pellet"] for row in rows[4:])):
        return False
    # Real spread may hit another enemy; the unchanged nearest-first Direct budget may remove neighbours.
    if (type(contact.get("knownContact")) is not bool or contact.get("validContact") is not True
            or contact.get("source") != "block contact" or not _enemy_integer(contact.get("actualTargetId"), 1)
            or type(contact.get("actualTargetOwner")) is not int
            or contact["actualTargetOwner"] not in (1, 2) or contact["actualTargetOwner"] == plan["team"]):
        return False
    ids = direct.get("removedIds")
    return (direct.get("actualIdsAbsent") is True and _enemy_integer(direct.get("removed"), 1)
            and isinstance(ids, list) and len(ids) == direct["removed"]
            and all(_enemy_integer(body, 1) for body in ids) and len(set(ids)) == len(ids)
            and _enemy_integer(direct.get("revision"), 1))


def enemy_execution_witness(report):
    """Find a bounded, ordered paid-input-to-real-deletion chain in this current enemy command."""
    invalid = {"valid": False, "reason": "missing or incomplete actual enemy execution chain"}
    if not isinstance(report, dict):
        return invalid
    trace, current = report.get("enemyExecution"), report.get("current", {})
    if not isinstance(trace, dict) or not isinstance(current, dict) or current.get("stage") != "enemy":
        return invalid
    samples, capacity, overflow = trace.get("samples"), trace.get("capacity"), trace.get("overflow")
    if (not isinstance(samples, list) or not _enemy_integer(capacity, 1) or capacity > 8192
            or len(samples) > capacity or not _enemy_integer(overflow) or overflow != 0):
        return dict(invalid, reason="enemy execution evidence overflow or invalid capacity")
    metadata = {"session": report.get("session"), "command": current.get("command"), "round": current.get("round"),
                "generation": current.get("generation"), "capacity": capacity, "overflow": overflow, "sampleCount": len(samples)}
    if (not isinstance(metadata["session"], str) or not metadata["session"]
            or any(not _enemy_integer(metadata[key], 1) for key in ("command", "round", "generation"))
            or not _enemy_integer(report.get("connectionGenerations"), 1)
            or current["generation"] != report["connectionGenerations"]):
        return dict(invalid, reason="enemy execution connection generation is stale")
    inputs, authorities, weapons, spawns, contacts = {}, {}, {}, {}, {}
    for index, row in enumerate(samples):
        plan_key = _enemy_plan_key(row)
        if plan_key is None:
            continue
        plan = row["plan"]
        if any(plan.get(field) != metadata[key] for field, key in
               (("session", "session"), ("command", "command"), ("round", "round"), ("connectionGeneration", "generation"))):
            continue
        kind = row.get("kind")
        key = (plan_key, _enemy_input_key(row)) if kind in _ENEMY_KINDS[:3] else None
        record = (index, row)
        if kind == "input" and key[1] is not None:
            inputs[key] = [record]
        elif kind == "authority-input" and key in inputs:
            authorities[key] = inputs[key] + [record]
        elif kind == "weapon" and key in authorities:
            emission = _enemy_emission_key(row, plan_key)
            if emission is not None:
                weapons[emission] = authorities[key] + [record]
        elif kind == "spawn":
            emission, projectile = _enemy_emission_key(row, plan_key), _enemy_projectile_key(row, plan_key)
            if emission in weapons and projectile is not None:
                spawns[projectile] = weapons[emission] + [record]
        elif kind == "contact":
            projectile = _enemy_projectile_key(row, plan_key)
            if projectile in spawns:
                contacts[projectile] = spawns[projectile] + [record]
        elif kind == "direct-dynamic-removal":
            projectile = _enemy_projectile_key(row, plan_key)
            if projectile in contacts:
                chain = contacts[projectile] + [record]
                witness = dict(metadata, sampleIndices=[item[0] for item in chain], samples=[item[1] for item in chain])
                if _valid_enemy_execution_chain(witness):
                    return copy.deepcopy(dict(witness, valid=True, reason="matched final input, authority selection, paid Step, real Spawn/contact and actual Direct deletion"))
    return invalid


def enemy_observation(report):
    current = report.get("current", {})
    actors = [actor for actor in report.get("actors", []) if actor.get("round") == current.get("round")]
    return {"stage": current.get("stage"), "round": current.get("round"), "session": report.get("session"),
            "command": current.get("command"), "generation": current.get("generation"),
            "pending": current.get("pending"), "projectiles": current.get("projectiles"),
            "removedBodies": report.get("removedBodies"),
            "paidAttacks": sum(actor.get("shots", 0) for actor in actors),
            "spentMaterial": sum(actor.get("spentMaterial", 0) for actor in actors),
            "executionWitness": enemy_execution_witness(report)}


def valid_enemy_evidence(evidence):
    if not isinstance(evidence, dict):
        return False
    before, after = evidence.get("before", {}), evidence.get("after", {})
    if not isinstance(before, dict) or not isinstance(after, dict):
        return False
    return (before.get("stage") == "drain" and after.get("stage") == "enemy"
            and _enemy_integer(before.get("round"), 1) and _enemy_integer(after.get("round"), 1)
            and before["round"] == after["round"]
            and before.get("pending") == 0 and before.get("projectiles") == 0
            and all(type(row.get(key)) is int and row[key] >= 0
                    for row in (before, after) for key in ("removedBodies", "paidAttacks"))
            and all(numeric(row.get("spentMaterial")) and row["spentMaterial"] >= 0 for row in (before, after))
            and after["removedBodies"] > before["removedBodies"] and after["paidAttacks"] > before["paidAttacks"]
            and after["spentMaterial"] > before["spentMaterial"]
            and isinstance(before.get("session"), str) and bool(before["session"]) and before["session"] == after.get("session")
            and _enemy_integer(before.get("command"), 1) and _enemy_integer(after.get("command"), 1)
            and before["command"] < after["command"]
            and _enemy_integer(before.get("generation"), 1) and _enemy_integer(after.get("generation"), 1)
            and before["generation"] == after["generation"]
            and _valid_enemy_execution_chain(after.get("executionWitness"))
            and all(after["executionWitness"].get(key) == after.get(key) for key in ("session", "command", "round", "generation")))


def valid_blast_batch(batch, frequency):
    return (type(batch.get("explosions")) is int and batch["explosions"] >= 8
            and batch.get("completed") == batch["explosions"] and batch.get("removed", 0) > 0
            and len(set(batch.get("owners", []))) >= 8
            and type(frequency) is int and frequency > 0
            and type(batch.get("firstStarted")) is int and type(batch.get("lastCompleted")) is int
            and 0 < batch["firstStarted"] <= batch["lastCompleted"]
            and numeric(batch.get("groupCompletionMs")) and 0 <= batch["groupCompletionMs"] <= 2000
            and math.isclose(batch["groupCompletionMs"], (batch["lastCompleted"] - batch["firstStarted"]) * 1000 / frequency,
                             abs_tol=1e-6, rel_tol=1e-8))


def network_oracle(reports, players, session, rtt, enemy_evidence=None, report_directory=None, visual_seed_evidence=None):
    failures = report_failures(reports, players, session, "network")
    visual = visual_oracle(reports, players, report_directory)
    failures.extend(visual["failures"])
    witnesses = {}
    for stage in CHECKPOINTS:
        aligned = checkpoint(reports, stage, players)
        if aligned is None:
            failures.append(f"{stage}: no common complete surface/dynamic/score checkpoint")
        else:
            witnesses[stage] = aligned
            host = aligned["host"]
            if stage in ("late", "destroyed", "frozen", "reconnected", "rebuilt") and (host["surfaces"] <= 0 or host["dynamic"] <= 0):
                failures.append(f"{stage}: missing either genuine surface deposits or free blocks")
            if stage == "cleared" and host["surfaces"] + host["dynamic"] != 0:
                failures.append("clear: blocks remain after the round reset")
    reconnect = reports.get("client-1", {})
    if any(reconnect.get(key) is not True for key in ("reconnectRequested", "reconnectDisconnected", "reconnectCleanup", "reconnectSuccess")) or reconnect.get("connectionGenerations", 0) < 2:
        failures.append("reconnect: no clean old-body teardown and new synchronized connection")
    motion = motion_oracle(reports, players)
    if not motion["passed"]:
        failures.append("motion: exact packet and actual client Body application oracle failed")
    corrections = {}
    for name in legacy.labels(players)[1:]:
        values = reports.get(name, {}).get("corrections", [])
        if not isinstance(values, list) or len(values) < 20 or any(not numeric(value) or value < 0 for value in values):
            failures.append(f"{name}: insufficient valid moving reconciliation samples")
            continue
        ordered = sorted(values)
        p95 = ordered[math.ceil(len(ordered) * .95) - 1]
        p99 = ordered[math.ceil(len(ordered) * .99) - 1]
        # The approved new-gameplay plan retains historical correction targets as
        # diagnostics. It requires valid moving samples, without inheriting old debt.
        corrections[name] = {"samples": len(values), "p95": p95, "p99": p99, "maximum": ordered[-1],
                             "legacyTargetsMet": p95 <= .15 and p99 <= .60 if rtt == 100 else None}
    legacy_correction = {"scope": "historical correction targets; diagnostic only under the approved new-gameplay plan",
                         "applicable": rtt == 100, "legacyTargets": {"rttMs": 100, "p95": .15, "p99": .60},
                         "legacyTargetsMet": all(row["legacyTargetsMet"] for row in corrections.values())
                             if rtt == 100 and len(corrections) == players - 1 else None}
    host = reports.get("host", {})
    played_rounds = [actor for actor in host.get("actors", []) if actor.get("round", 0) > 0 and actor.get("shots", 0) > 0]
    if len({actor.get("owner") for actor in played_rounds}) < players or any(actor.get("spentMaterial", 0) <= 0 for actor in played_rounds):
        failures.append("combat: all actual Players must emit real paid weapon attacks")
    if host.get("materialRefills") != 0:
        failures.append("combat: probe must recover material through legal gameplay")
    if not valid_enemy_evidence(enemy_evidence or {}):
        failures.append("enemy: no durable before/after proof of a paid ordinary attack removing a free block")
    seed_before = visual_seed_evidence.get("before", {}) if isinstance(visual_seed_evidence, dict) else {}
    late_host = witnesses.get("late", {}).get("host", {})
    if (not valid_visual_seed_evidence(visual_seed_evidence, players)
            or seed_before.get("round") != late_host.get("round")
            or seed_before.get("command") != late_host.get("command")):
        failures.append("visual seed: no durable proof of paid late-player input, both team/type totals and a drained common current state")
    return {"passed": not failures, "failures": failures, "checkpoints": witnesses,
            "motion": motion, "corrections": corrections, "legacyCorrectionDiagnostic": legacy_correction,
            "ordinaryEnemyEvidence": enemy_evidence or {}, "visual": visual,
            "visualSeedEvidence": visual_seed_evidence or {}}


def natural_movement_failures(host, players):
    """Validate authority movement and owner-specific post-force body displacement."""
    natural = host.get("natural", {})
    host_owner, roles = natural.get("hostOwner"), natural.get("roles")
    if (type(host_owner) is not int or host_owner < 0 or not isinstance(roles, list) or len(roles) != players
            or any(not isinstance(actor, dict) for actor in roles)):
        return ["natural: missing authoritative owner movement records"]
    owners = [actor.get("owner") for actor in roles]
    if (any(type(owner) is not int or owner < 0 for owner in owners)
            or len(set(owners)) != players or host_owner not in owners):
        return ["natural: movement must cover every distinct actual owner including host"]
    round_id = host.get("current", {}).get("round")
    actual_owners = {actor.get("owner") for actor in host.get("actors", [])
                     if actor.get("round") == round_id and type(actor.get("owner")) is int}
    if type(round_id) is not int or round_id <= 0 or actual_owners != set(owners):
        return ["natural: movement owners differ from the actual current-round Players"]
    duration = natural.get("elapsedServerSeconds")
    if not numeric(duration) or duration < 0:
        return ["natural: invalid movement observation duration"]

    def vector(value):
        return isinstance(value, dict) and all(numeric(value.get(axis)) for axis in ("x", "y", "z"))

    failures, pushed_owners, pushed_bodies = [], set(), set()
    for actor in roles:
        owner = actor["owner"]
        if (not numeric(actor.get("horizontalMeters")) or actor["horizontalMeters"] <= 1
                or type(actor.get("movementSamples")) is not int or actor["movementSamples"] <= 0):
            failures.append(f"natural: owner {owner} needs more than 1 m of actual horizontal movement")
        visits = actor.get("waypointVisits")
        if (not isinstance(visits, list) or type(actor.get("waypointArrivals")) is not int
                or actor["waypointArrivals"] != len(visits)):
            failures.append(f"natural: owner {owner} is missing waypoint arrival details")
        else:
            previous_life, expected_index = None, 0
            for visit in visits:
                if (not isinstance(visit, dict) or type(visit.get("index")) is not int or visit["index"] < 0
                        or type(visit.get("life")) is not int or visit["life"] < 0
                        or not numeric(visit.get("seconds")) or not 0 <= visit["seconds"] <= duration + .001
                        or not vector(visit.get("position")) or not vector(visit.get("waypoint"))):
                    failures.append(f"natural: owner {owner} has invalid waypoint evidence")
                    break
                if visit["life"] != previous_life:
                    expected_index = 0
                team, slot = actor.get("team"), actor.get("slot")
                if type(team) is not int or team not in (1, 2) or type(slot) is not int or not 0 <= slot < 4:
                    failures.append(f"natural: owner {owner} has invalid route identity")
                    break
                waypoint = visit["waypoint"]
                target_x = -11.5 if slot % 2 == 0 else 11.5
                target_z = (-20, -10, 8, 20, 8, -10)[visit["index"] % 6] * (1 if team == 1 else -1)
                distance = math.hypot(waypoint["x"] - visit["position"]["x"], waypoint["z"] - visit["position"]["z"])
                if (visit["index"] != expected_index or distance > 1.3001
                        or not math.isclose(waypoint["x"], target_x, abs_tol=1e-5)
                        or not math.isclose(waypoint["z"], target_z, abs_tol=1e-5)):
                    failures.append(f"natural: owner {owner} did not reach the recorded authored waypoint")
                    break
                previous_life, expected_index = visit["life"], visit["index"] + 1
        pushes, seen_bodies = actor.get("movedPushes"), set()
        if not isinstance(pushes, list):
            failures.append(f"natural: owner {owner} is missing post-push body details")
            continue
        for push in pushes:
            if (not isinstance(push, dict) or type(push.get("body")) is not int or push["body"] <= 0
                    or push["body"] in seen_bodies or not numeric(push.get("elapsedSeconds"))
                    or not 0 <= push["elapsedSeconds"] <= 2 or not numeric(push.get("seconds"))
                    or not 0 <= push["seconds"] <= duration + .001
                    or not numeric(push.get("horizontalMeters")) or push["horizontalMeters"] < .01
                    or not numeric(push.get("alongForceMeters")) or push["alongForceMeters"] < .01
                    or not all(vector(push.get(key)) for key in ("from", "to", "direction"))):
                failures.append(f"natural: owner {owner} lacks a real body displacement after push")
                continue
            seen_bodies.add(push["body"])
            dx, dz = push["to"]["x"] - push["from"]["x"], push["to"]["z"] - push["from"]["z"]
            direction = push["direction"]
            along = dx * direction["x"] + dz * direction["z"]
            if (abs(direction["y"]) > 1e-5 or not math.isclose(math.hypot(direction["x"], direction["z"]), 1, abs_tol=1e-5)
                    or not math.isclose(math.hypot(dx, dz), push["horizontalMeters"], abs_tol=1e-4, rel_tol=1e-5)
                    or not math.isclose(along, push["alongForceMeters"], abs_tol=1e-4, rel_tol=1e-5)
                    or along < .01 - 1e-5 or type(actor.get("pushEvents")) is not int or actor["pushEvents"] <= 0):
                failures.append(f"natural: owner {owner} post-push displacement disagrees with its positions and force")
                continue
            pushed_owners.add(owner)
            pushed_bodies.add(push["body"])
    recorded = natural.get("movedPushOwners")
    if (not isinstance(recorded, list) or any(type(owner) is not int for owner in recorded)
            or len(recorded) != len(set(recorded)) or set(recorded) != pushed_owners
            or natural.get("playersThatPushed") != len(pushed_owners)
            or natural.get("pushedBodiesMoved") != len(pushed_bodies)):
        failures.append("natural: aggregate push owners/bodies differ from per-owner displacement witnesses")
    if not any(owner != host_owner for owner in pushed_owners):
        failures.append("natural: at least one remote owner must really push a body by 1 cm horizontally")
    return failures


def workload_oracle(reports, players, session, mode, report_directory=None):
    failures = report_failures(reports, players, session, mode)
    visual = visual_oracle(reports, players, report_directory)
    failures.extend(visual["failures"])
    for name in legacy.labels(players):
        report = reports.get(name, {})
        local = report.get("natural", {})
        if local.get("valid") is not True:
            failures.append(f"{name}: workload local and gameplay evidence is invalid")
        if not numeric(local.get("localShots")) or local["localShots"] <= 0 or not numeric(local.get("localSpentMaterial")) or local["localSpentMaterial"] <= 0:
            failures.append(f"{name}: no local real paid attack evidence")
        if report.get("materialRefills") != 0:
            failures.append(f"{name}: workload must recover material through legal gameplay")
    host = reports.get("host", {})
    natural = host.get("natural", {})
    if natural.get("completedNormalRound") is not True or not numeric(natural.get("elapsedServerSeconds")) or natural["elapsedServerSeconds"] < 179.9:
        failures.append(mode + ": not a complete normal configured 180-second round")
    if mode == "natural" and natural.get("frameGatePassed") is not True:
        failures.append("natural: rendered host frame P95/P99 exceeds 22.2/33.3 ms")
    if any(report.get("natural", {}).get("diagnosticCpuCapture") is True for report in reports.values()):
        failures.append(mode + ": native CPU-profile diagnostics cannot count as acceptance")
    if any(report.get("natural", {}).get("diagnosticBodyStateCapture") is True for report in reports.values()):
        failures.append(mode + ": bounded body-state diagnostics cannot count as acceptance")
    if mode == "natural":
        failures.extend(natural_movement_failures(host, players))
        if (natural.get("latencyGatePassed") is not True or natural.get("attachedCollisionSamples", 0) <= 0
                or natural.get("dynamicCollisionSamples", 0) <= 0):
            failures.append("natural: missing actual attached and dynamic collision publication samples")
        for key in ("contactToCollision", "attachedContactToCollision", "dynamicContactToCollision"):
            metric = natural.get(key, {})
            if metric.get("samples", 0) <= 0 or not numeric(metric.get("p95")) or not 0 <= metric["p95"] <= 200:
                failures.append("natural: " + key + " requires measured collision-effective P95 <=200 ms")
    blast = host.get("blast", {})
    if mode == "blast8" and (blast.get("valid") is not True or type(blast.get("maximumSameTick")) is not int
            or blast["maximumSameTick"] < 8 or blast.get("collisionEffective") is not True):
        failures.append("blast8: no measured eight-explosion same-physics-tick group with complete collider removal within 2000 ms")
    if mode == "blast8" and not any(valid_blast_batch(batch, blast.get("stopwatchFrequency")) for batch in blast.get("batches", [])):
        failures.append("blast8: no complete eight-explosion batch with first-start to last-completion wall time <=2000 ms")
    return {"passed": not failures, "failures": failures, "natural": natural, "blast": blast, "visual": visual}


def run_case(exe, directory, players, rtt, loss, mode="network", timeout=720, combat_seconds=35,
             graphics_api="d3d12", headless_clients=False, cpu_profile=False, job_workers=None,
             body_state_at=None, body_state_source=None, body_state_package=None):
    legacy.require_background_player_desktop()
    directory.mkdir(parents=True, exist_ok=True)
    control = directory / "control.txt"
    session = uuid.uuid4().hex
    upstream, front = legacy.free_port(), legacy.free_port()
    while front == upstream:
        front = legacy.free_port()
    processes, commands, checkpoints = {}, [], []
    command_id = 0
    started = time.monotonic()
    failures, enemy_evidence, visual_seed_evidence = [], {}, {}

    def reports():
        return {name: legacy.read_json(directory / f"{name}.dynamic.json") for name in legacy.labels(players)}

    def stage(name):
        nonlocal command_id
        command_id += 1
        temporary = control.with_suffix(".writing")
        temporary.write_text(f"{command_id}\n{name}\n", encoding="utf-8")
        for attempt in range(5):
            try:
                temporary.replace(control)
                break
            except OSError:
                if attempt == 4:
                    raise
                time.sleep(.02 * 2 ** attempt)
        checkpoints.append({"stage": name, "command": command_id, "seconds": time.monotonic() - started})

    def wait(predicate, message, seconds=75, allow_exits=False):
        end = min(started + timeout, time.monotonic() + seconds)
        while time.monotonic() < end:
            current = reports()
            if predicate(current):
                return
            exited = {name: process.poll() for name, process in processes.items() if name != "proxy" and process.poll() is not None}
            if exited and not allow_exits:
                raise RuntimeError(f"{message}: Players exited early: {exited}")
            legacy.write_json(directory / "progress.json", {"mode": mode, "players": players, "rtt": rtt, "loss": loss,
                              "waitingFor": message, "seconds": time.monotonic() - started,
                              "host": current.get("host", {}).get("current")})
            time.sleep(.2)
        raise RuntimeError(f"Timed out: {message}")

    def visual_ready(data):
        for name in legacy.labels(players):
            visual = data.get(name, {}).get("visual", {})
            if visual.get("complete") is True and visual.get("valid") is not True:
                raise RuntimeError(f"{name}: visual probe failed: {visual.get('error', '')}")
        if not all(data.get(name, {}).get("visual", {}).get("complete") is True for name in legacy.labels(players)):
            return False
        result = visual_oracle(data, players, directory)
        if not result["passed"]:
            raise RuntimeError("visual evidence: " + "; ".join(result["failures"]))
        return True

    def launch(index):
        name = "host" if index == 0 else f"client-{index}"
        command = [exe, "-noSystemCursor", f"-force-{graphics_api}", "-screen-fullscreen", 0,
                   "-screen-width", 1920, "-screen-height", 1080, "-lanSmokeHost" if index == 0 else "-lanSmokeClient",
                   "-lanAddress", "127.0.0.1", "-lanPort", upstream if index == 0 else front,
                   "-inkSmokeCase", "observer", "-inkLabel", name, "-inkCaptureAfter", 999999,
                   "-blockDynamicProbe", "-dynamicProbeOutput", directory / f"{name}.dynamic.json",
                   "-dynamicProbeControl", control, "-dynamicProbeSession", session, "-dynamicProbePlayers", players,
                   "-dynamicProbeMode", mode, "-dynamicProbeTimeout", timeout,
                   "-logFile", directory / f"{name}.log"]
        if index == 1:
            command += ["-dynamicReconnectParticipant"]
        if index == 0 and cpu_profile:
            command += ["-dynamicProbeCpuProfile", directory / "host.cpu.raw"]
        if job_workers is not None:
            command += ["-job-worker-count", job_workers]
        if index == 0 and body_state_at is not None:
            command += ["-dynamicProbeBodyState55", str(body_state_at),
                        "-dynamicProbeBodyStateSource55", body_state_source,
                        "-dynamicProbeBodyStatePackage55", body_state_package]
        if index > 0 and headless_clients:
            command += ["-batchmode", "-nographics"]
        arguments = [str(part) for part in command]
        commands.append({"label": name, "arguments": arguments, "graphics": not (index > 0 and headless_clients)})
        launcher = legacy.hidden_process if index > 0 and headless_clients else legacy.visible_graphics_process
        processes[name] = launcher(arguments, exe.parent)

    try:
        proxy = [sys.executable, ROOT / "Tools/NetworkOptimization/udp_proxy.py", "--listen", front,
                 "--target", upstream, "--rtt", rtt, "--jitter", 0, "--loss", loss,
                 "--seed", 20261017 + players * 1000 + rtt * 10 + loss,
                 "--seconds", timeout + 10, "--output", directory / "proxy.json", "--stop-file", directory / "stop-proxy"]
        processes["proxy"] = legacy.hidden_process(proxy, ROOT)
        launch(0)
        wait(lambda data: data["host"].get("initialSyncComplete") is True, "host ready")
        for index in range(1, players - (1 if mode == "network" else 0)):
            launch(index)
        if mode == "network":
            stage("seed")
            wait(lambda data: data["host"].get("current", {}).get("dynamic", 0) >= 4
                 and data["host"].get("current", {}).get("surfaces", 0) > 0, "real paid pre-join surface and free blocks")
            stage("late"); launch(players - 1)
            wait(lambda data: checkpoint(data, "late", players) is not None, "populated late-join lifecycle and score oracle")
            visual_seed_evidence["before"] = visual_seed_observation(reports(), players)
            stage("seed")
            wait(lambda data: data["host"].get("current", {}).get("command") == command_id
                 and valid_visual_seed_payment(visual_seed_evidence["before"], visual_seed_observation(data, players), players),
                 "late Player emits real paid inputs; both teams and both block types populate the ordinary lanes", 45)
            visual_seed_evidence["after"] = visual_seed_observation(reports(), players)
            stage("visual-drain")
            wait(lambda data: (aligned := current_checkpoint(data, "visual-drain", players, command_id)) is not None
                 and valid_visual_seed_evidence(dict(visual_seed_evidence, drained=aligned), players),
                 "all current Player states agree after paid seed; pending transactions and actual projectiles drain")
            visual_seed_evidence["drained"] = current_checkpoint(reports(), "visual-drain", players, command_id)
            stage("visual-proof")
            wait(visual_ready, "actual collider/draw geometry and normal-backbuffer visual proof on every Player")
        else:
            wait(lambda data: all(report.get("current", {}).get("players") == players and report.get("initialSyncComplete") is True for report in data.values()), "all independent Players ready")
            wait(lambda data: all(report.get("natural", {}).get("equipmentReady") is True for report in data.values()), "all Players choose their legal production equipment")
        if mode == "network":
            stage("round-start")
            wait(lambda data: all(report.get("current", {}).get("phase") == "Playing" for report in data.values()), "real round starts")
        stage("combat")
        if mode == "network":
            wait(lambda data: data["host"].get("sampledSeconds", 0) >= combat_seconds, "paid combat and predicted capsule movement", combat_seconds + 90)
            stage("drain")
            wait(lambda data: data["host"].get("current", {}).get("stage") == "drain"
                 and data["host"].get("current", {}).get("pending") == 0 and data["host"].get("current", {}).get("projectiles") == 0,
                 "stop firing and drain all admitted projectiles")
            enemy_evidence["before"] = enemy_observation(reports()["host"])
            stage("enemy")
            wait(lambda data: valid_enemy_evidence(dict(enemy_evidence, after=enemy_observation(data["host"]))),
                 "real paid ordinary weapon destroys an enemy free block", 45)
            enemy_evidence["after"] = enemy_observation(reports()["host"])
            stage("destroyed")
            wait(lambda data: checkpoint(data, "destroyed", players) is not None, "enemy destruction replicates to every Player")
            stage("finish")
            wait(lambda data: data["host"].get("current", {}).get("phase") == "Finished", "canonical round freeze")
            stage("frozen")
            wait(lambda data: checkpoint(data, "frozen", players) is not None, "exact final frozen rigidbody poses and velocities")
            stage("reconnect")
            wait(lambda data: data["client-1"].get("reconnectDisconnected") is True, "old client leaves and clears bodies")
            wait(lambda data: data["client-1"].get("reconnectSuccess") is True and data["client-1"].get("connectionGenerations", 0) >= 2,
                 "client establishes a new connection")
            stage("reconnected")
            wait(lambda data: checkpoint(data, "reconnected", players) is not None, "reconnected final full dynamic snapshot")
            stage("clear")
            wait(lambda data: data["host"].get("current", {}).get("phase") == "Practice", "return to room clears the round")
            stage("cleared")
            wait(lambda data: (witness := checkpoint(data, "cleared", players)) is not None
                 and witness["host"]["surfaces"] + witness["host"]["dynamic"] == 0, "zero stale terrain and free-body colliders")
            stage("rebuild")
            wait(lambda data: all(report.get("current", {}).get("phase") == "Playing" for report in data.values()), "next round")
            stage("seed-rebuild")
            wait(lambda data: data["host"].get("current", {}).get("dynamic", 0) >= 4, "next-round real paid reconstruction")
            stage("rebuilt")
            wait(lambda data: checkpoint(data, "rebuilt", players) is not None, "next-round bodies and counts synchronized")
        else:
            wait(lambda data: all(report.get("current", {}).get("phase") == "Finished" for report in data.values()), "complete natural configured round on all Players", 240)
            stage("visual-proof")
            wait(visual_ready, "post-round collider/draw geometry and normal-backbuffer visual proof on every Player")
        stage("quit-clients")
        wait(lambda data: all(processes[name].poll() is not None for name in legacy.labels(players)[1:]), "clients persist final evidence and exit", 45, True)
        stage("quit")
        wait(lambda data: processes["host"].poll() is not None, "host persists final evidence and exits", 45, True)
    except Exception as exception:
        failures.append(str(exception))
    finally:
        if any(process.poll() is None for name, process in processes.items() if name != "proxy"):
            try:
                stage("quit")
            except Exception as exception:
                failures.append("graceful quit control: " + str(exception))
        for name, process in processes.items():
            if name == "proxy":
                continue
            try:
                process.wait(timeout=10)
            except Exception:
                if process.poll() is None:
                    process.terminate()
                try:
                    process.wait(timeout=10)
                except Exception:
                    process.kill(); process.wait(timeout=10)
        if "proxy" in processes:
            try:
                (directory / "stop-proxy").write_text("finished", encoding="utf-8")
                processes["proxy"].wait(timeout=10)
            except Exception as exception:
                failures.append("proxy cleanup: " + str(exception))
                if processes["proxy"].poll() is None:
                    processes["proxy"].terminate(); processes["proxy"].wait(timeout=10)
    collected = reports()
    if job_workers is not None:
        for name in legacy.labels(players):
            if collected.get(name, {}).get("jobWorkerCount") != job_workers:
                failures.append(f"{name}: requested Job Worker budget was not read back from the actual Player")
    if mode == "network":
        result = network_oracle(collected, players, session, rtt, enemy_evidence, directory, visual_seed_evidence)
    else:
        result = workload_oracle(collected, players, session, mode, directory)
    exits = {name: process.returncode for name, process in processes.items()}
    if any(exits.get(name) != 0 for name in legacy.labels(players)):
        failures.append("one or more Player processes exited nonzero or never started")
    proxy_report = legacy.read_json(directory / "proxy.json")
    if proxy_report.get("rtt_ms") != rtt or proxy_report.get("loss_percent") != loss or proxy_report.get("clients", 0) < players - 1:
        failures.append("impairment proxy does not prove the requested configuration and actual clients")
    result["failures"].extend(failures); result["passed"] = result["passed"] and not failures
    result.update(schema=1, mode=mode, session=session, players=players, rttMs=rtt, lossPercent=loss,
                  requestedJobWorkers=job_workers,
                  commands=commands, stages=checkpoints, exits=exits, proxy=proxy_report,
                  reports={name: str(directory / f"{name}.dynamic.json") for name in collected},
                  seconds=time.monotonic() - started,
                  scope="same-machine independent Players; new rules only; historical stress workloads and real multi-machine/manual acceptance remain separate")
    legacy.write_json(directory / "case.json", result)
    print(f"{mode}: {players} Players {rtt} ms/{loss}% passed={result['passed']}", flush=True)
    return result


def body_state_request_error(mode, at, source, package):
    if at is None:
        return "body-state hashes require --body-state-at-seconds" if source is not None or package is not None else None
    if mode != "natural" or not numeric(at) or at < 0:
        return "body-state diagnostics require natural mode and finite nonnegative elapsed seconds"
    if not digest(source) or not digest(package):
        return "body-state diagnostics require lower-case SHA256 source and package identities"
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=("network", "natural", "blast8"), default="network")
    parser.add_argument("--players", nargs="+", type=int, choices=(2, 8))
    parser.add_argument("--rtt", nargs="+", type=int, choices=(0, 100, 200))
    parser.add_argument("--loss", nargs="+", type=int, choices=(0, 1, 3))
    parser.add_argument("--timeout", type=int, default=720)
    parser.add_argument("--combat-seconds", type=float, default=35)
    parser.add_argument("--graphics-api", choices=("d3d11", "d3d12"), default="d3d12")
    parser.add_argument("--headless-clients", action="store_true")
    parser.add_argument("--cpu-profile", action="store_true", help="Diagnostic host-only eight-second native CPU capture; never performance acceptance")
    parser.add_argument("--job-workers", type=int, choices=range(1, 16), help="Explicit per-Player Unity Job Worker budget for same-machine scheduling; gameplay and graphics workload stay unchanged")
    parser.add_argument("--body-state-at-seconds", type=float, help="Diagnostic host-only bounded body-state window; natural only; never acceptance")
    parser.add_argument("--body-state-source-sha256")
    parser.add_argument("--body-state-package-sha256")
    args = parser.parse_args()
    body_state_error = body_state_request_error(args.mode, args.body_state_at_seconds, args.body_state_source_sha256, args.body_state_package_sha256)
    if body_state_error:
        parser.error(body_state_error)
    if args.cpu_profile and args.mode == "network":
        parser.error("CPU-window diagnostics require natural or blast8 mode")
    exe = args.player.resolve()
    if not exe.is_file():
        parser.error("--player must name the actual built Player")
    cases = sorted(NETWORK_MATRIX) if args.mode == "network" else [(8, 0, 0)]
    cases = [case for case in cases if (not args.players or case[0] in args.players)
             and (not args.rtt or case[1] in args.rtt) and (not args.loss or case[2] in args.loss)]
    if not cases:
        parser.error("filters select no approved cases")
    results = [run_case(exe, args.output.resolve() / f"{args.mode}-{p}p-{r}ms-{l}loss", p, r, l, args.mode,
                        args.timeout, args.combat_seconds, args.graphics_api, args.headless_clients, args.cpu_profile, args.job_workers,
                        args.body_state_at_seconds, args.body_state_source_sha256, args.body_state_package_sha256) for p, r, l in cases]
    complete = set(cases) == (NETWORK_MATRIX if args.mode == "network" else {(8, 0, 0)})
    result = {"schema": 1, "mode": args.mode, "requiredMatrixComplete": complete,
              "passed": complete and all(case["passed"] for case in results), "cases": results,
              "player": str(exe), "playerSha256": hashlib.sha256(exe.read_bytes()).hexdigest(),
              "diagnosticOnly": args.cpu_profile or args.body_state_at_seconds is not None or args.mode == "network" and args.combat_seconds < 35,
              "bodyStateAtSeconds": args.body_state_at_seconds, "bodyStateSourceSha256": args.body_state_source_sha256,
              "bodyStatePackageSha256": args.body_state_package_sha256,
              "scope": "30 cm independent-body gameplay with 2/4 metre resting-body activation; six new network cases or explicit new workload; old package results never inherited"}
    if result["diagnosticOnly"]:
        result["passed"] = False
    legacy.write_json(args.output.resolve() / "summary.json", result)
    return 0 if result["passed"] else 4


if __name__ == "__main__":
    raise SystemExit(main())
