"""Synthetic adversarial oracle checks only. These are never live-Player acceptance evidence."""
import copy
import struct
import unittest

from network_acceptance import CHECKPOINTS, checkpoint_evidence, drain_ready, unity_first_cell, validate_fall_fixture


def sample_reports(players=2):
    reports = {"host" if owner == 0 else f"client-{owner}":
               dict(initialSyncComplete=True, drainInputObserved=True, drainInputOwner=owner,
                    drainInputLife=2, drainInputSequence=60) for owner in range(players)}
    inputs = [dict(owner=owner, life=2, acknowledgedInput=65) for owner in range(players)]
    state = dict(stage="drain", phase="Playing", round=7, terrainRound=7, revision=10, players=players,
                 pink=128, blue=128, total=256, scorePink=128, scoreBlue=128, chunks=3,
                 fallingRuns=0, fallingCells=0, pending=0, failedTransactions=0, initialSyncComplete=True,
                 chunkHash="a" * 64, fallingHash="b" * 64, snapshotHash="c" * 64,
                 fallingOffsetHash="d" * 64, frozenAt=0, projectilePending=0, subPending=0, specialPending=0,
                 unstableColumns=0, commandsQueued=50, commandsCompleted=50, drainQuietSeconds=1.1,
                 authorityInputs=inputs)
    for report in reports.values():
        report["current"] = copy.deepcopy(state)
        report["states"] = [copy.deepcopy(state)]
    commands, publications = [], []
    for owner in range(players):
        point = dict(x=2 + owner * 4, y=22, z=4)
        first = dict(x=point["x"] * 10, y=220, z=40)
        command = dict(id=f"7:9:{owner}", owner=owner, team=owner % 2 + 1, round=7, baselineRevision=10,
                       requested=32, added=32, callbackCount=1, baselineEmpty=True, removedEnemy=0, removedFriendly=0,
                       completedRevision=11, completedWorldRevision=11, queuedOrdinal=51 + owner,
                       firstCell=first, point=point, lane=dict(x=owner * 4, y=0, z=4), radius=.3,
                       queuedRealtime=10, queuedServerTime=10, completedRealtime=11, completedServerTime=11,
                       queuedHeadAgeMs=0, completedHeadAgeMs=0)
        commands.append(command)
        # Deliberately synthetic legal addresses in the candidate volume; no Unity trajectory is asserted here.
        cells = [dict(runId=1000 + owner * 2 + index // 16,
                      originCell=dict(x=first["x"], y=first["y"] + index, z=first["z"]), owner=command["team"],
                      offset=1, liveCenter=dict(x=(first["x"] + .5) * .1,
                                               y=(first["y"] + index + .5) * .1 - 1, z=(first["z"] + .5) * .1))
                 for index in range(32)]
        publications.append(dict(fixtureId=command["id"], owner=owner, team=command["team"], round=7, revision=12,
                                 serverTime=12, terrainTime=12, realtime=12, headAgeMs=0,
                                 liveCount=32, newlyWitnessedCount=32, pending=0, cells=cells))
    reports["host"]["fallCoverage"] = dict(schema=1, command=9, expectedPlayers=players, errors=0, drainAccepted=True,
        allCommandsCompleted=True, allCellsWitnessed=True, finishRequested=True, round=7, baselineRevision=10,
        finishRequestRevision=12, baselineNextRunId=1000, baselinePink=128, baselineBlue=128, baselineTotal=256,
        requestedRealtime=10.1, finishRequestRealtime=12,
        baselineQueued=50, baselineCompleted=50, activeFixtureFallingCells=32 * players, drainQuietSeconds=1.1,
        drainState=copy.deepcopy(state), drainInputs=copy.deepcopy(inputs), commands=commands, publications=publications)
    return reports


def relocated_fixture(reports, point, first):
    """Move one complete synthetic volume, retaining its independently chosen site."""
    coverage = reports["host"]["fallCoverage"]
    command = coverage["commands"][0]
    previous = command["firstCell"]
    delta = {axis: first[axis] - previous[axis] for axis in ("x", "y", "z")}
    command["point"] = point
    command["lane"] = {axis: point[axis] - value for axis, value in (("x", 2), ("y", 22), ("z", 0))}
    command["firstCell"] = first
    for publication in coverage["publications"]:
        if publication["fixtureId"] != command["id"]:
            continue
        for cell in publication["cells"]:
            for axis in ("x", "y", "z"):
                cell["originCell"][axis] += delta[axis]
                cell["liveCenter"][axis] += delta[axis] * .1


class FiniteFallFixtureEvidenceTests(unittest.TestCase):
    def assert_invalid(self, reports, players=2):
        self.assertTrue(validate_fall_fixture(reports, players)[1])

    def test_complete_unique_cell_evidence_and_consumed_idle_inputs(self):
        for players in (2, 8):
            reports = sample_reports(players)
            self.assertTrue(drain_ready(reports, players))
            summary, failures = validate_fall_fixture(reports, players)
            self.assertEqual(failures, [])
            self.assertEqual(summary["activeAtFinishRequest"], 32 * players)
            self.assertTrue(all(row["uniquePublishedFallingCells"] == 32 for row in summary["commands"]))

    def test_legacy_traversal_falling_state_cannot_prove_a_new_fixture(self):
        reports = sample_reports()
        reports["host"].pop("fallCoverage")
        reports["host"]["current"].update(stage="fall", fallingCells=500)
        self.assert_invalid(reports)

    def test_added_callbacks_without_published_falling_cells_are_insufficient(self):
        reports = sample_reports(); reports["host"]["fallCoverage"]["publications"] = []
        self.assert_invalid(reports)

    def test_each_real_fixture_cell_is_required(self):
        reports = sample_reports()
        row = reports["host"]["fallCoverage"]["publications"][0]
        row["cells"].pop(); row["liveCount"] = row["newlyWitnessedCount"] = 31
        reports["host"]["fallCoverage"]["activeFixtureFallingCells"] = 63
        self.assert_invalid(reports)

    def test_old_run_wrong_owner_and_wrong_live_position_cannot_be_counted(self):
        for key, value in (("runId", 999), ("owner", 2), ("liveCenter", dict(x=999, y=999, z=999))):
            reports = sample_reports(); reports["host"]["fallCoverage"]["publications"][0]["cells"][0][key] = value
            self.assert_invalid(reports)

    def test_duplicate_addresses_and_publication_revisions_cannot_inflate_coverage(self):
        reports = sample_reports(); coverage = reports["host"]["fallCoverage"]
        coverage["publications"][0]["cells"][1] = copy.deepcopy(coverage["publications"][0]["cells"][0])
        self.assert_invalid(reports)
        reports = sample_reports(); coverage = reports["host"]["fallCoverage"]
        coverage["publications"].append(copy.deepcopy(coverage["publications"][0]))
        self.assert_invalid(reports)

    def test_drain_requires_all_idle_input_acknowledgements_and_real_quiet_state(self):
        for field, value in (("projectilePending", 1), ("subPending", 1), ("specialPending", 1), ("unstableColumns", 1),
                             ("pending", 1), ("fallingCells", 1), ("commandsCompleted", 49), ("drainQuietSeconds", .5)):
            reports = sample_reports()
            reports["host"]["current"][field] = value
            reports["host"]["fallCoverage"]["drainState"][field] = value
            self.assertFalse(drain_ready(reports, 2)); self.assert_invalid(reports)
        reports = sample_reports()
        reports["host"]["current"]["authorityInputs"][1]["acknowledgedInput"] = 59
        reports["host"]["fallCoverage"]["drainInputs"][1]["acknowledgedInput"] = 59
        self.assertFalse(drain_ready(reports, 2)); self.assert_invalid(reports)

    def test_wrong_site_round_callback_or_enqueue_ordinal_fails(self):
        for field, value in (("round", 6), ("callbackCount", 2), ("queuedOrdinal", 53), ("added", 31),
                             ("requested", 64), ("baselineEmpty", False)):
            reports = sample_reports(); reports["host"]["fallCoverage"]["commands"][0][field] = value
            self.assert_invalid(reports)
        reports = sample_reports(); reports["host"]["fallCoverage"]["commands"][0]["point"]["y"] = 21
        self.assert_invalid(reports)

    def test_the_finish_must_reference_the_actual_current_published_fall(self):
        for field, value in (("finishRequested", False), ("activeFixtureFallingCells", 0), ("finishRequestRevision", 11), ("errors", 1)):
            reports = sample_reports(); reports["host"]["fallCoverage"][field] = value
            self.assert_invalid(reports)

    def test_a_peer_with_a_different_drain_hash_cannot_prove_the_shared_baseline(self):
        reports = sample_reports()
        reports["client-1"]["current"]["chunkHash"] = "f" * 64
        reports["client-1"]["states"][0]["chunkHash"] = "f" * 64
        self.assertFalse(drain_ready(reports, 2)); self.assert_invalid(reports)

    def test_original_late_freeze_reconnect_clear_rebuild_hash_gates_are_still_strict(self):
        for players in (2, 8):
            for stage in CHECKPOINTS:
                reports = sample_reports(players)
                for report in reports.values():
                    report["states"][0].update(stage=stage)
                    if stage in ("frozen", "reconnected"):
                        report["states"][0].update(phase="Finished", frozenAt=5, terrainTime=5,
                                                   fallingRuns=4, fallingCells=64)
                self.assertIsNotNone(checkpoint_evidence(reports, players, stage))
                for field in ("chunkHash", "fallingHash", "fallingOffsetHash", "snapshotHash"):
                    changed = copy.deepcopy(reports)
                    changed[f"client-{players - 1}"]["states"][0][field] = "f" * 64
                    self.assertIsNone(checkpoint_evidence(changed, players, stage))
                changed = copy.deepcopy(reports)
                changed[f"client-{players - 1}"]["states"][0].update(pink=127, blue=129, scorePink=127, scoreBlue=129)
                self.assertIsNone(checkpoint_evidence(changed, players, stage))
                changed = copy.deepcopy(reports)
                changed[f"client-{players - 1}"]["states"][0]["pending"] = 1
                self.assertIsNone(checkpoint_evidence(changed, players, stage))
                if stage in ("frozen", "reconnected"):
                    changed = copy.deepcopy(reports)
                    changed[f"client-{players - 1}"]["states"][0]["terrainTime"] = 5.01
                    self.assertIsNone(checkpoint_evidence(changed, players, stage))

    def test_reported_addresses_and_live_positions_cannot_translate_away_from_point(self):
        for axis in ("x", "y", "z"):
            reports = sample_reports()
            coverage = reports["host"]["fallCoverage"]
            original_point = copy.deepcopy(coverage["commands"][0]["point"])
            coverage["commands"][0]["firstCell"][axis] -= 100
            for cell in coverage["publications"][0]["cells"]:
                cell["originCell"][axis] -= 100
                cell["liveCenter"][axis] -= 10
            self.assertEqual(coverage["commands"][0]["point"], original_point)
            self.assert_invalid(reports)

    def test_unity_float32_cell_boundaries_on_positive_and_negative_axes(self):
        number = lambda bits: struct.unpack("<f", struct.pack("<I", bits))[0]
        # Explicit binary32 neighbours and expected cells; do not derive the
        # expected answer through the oracle under test or Python double floor.
        cases = [
            (dict(x=number(0x3dcccccc), y=number(0x3d48b439), z=number(0xbdcccccc)), dict(x=0, y=0, z=-1)),
            (dict(x=number(0x3dcccccd), y=number(0x3d48b43a), z=number(0xbdcccccd)), dict(x=1, y=1, z=-1)),
            (dict(x=number(0x3dccccce), y=number(0xbd50e561), z=number(0xbdccccce)), dict(x=1, y=-1, z=-2)),
            (dict(x=-0.0, y=number(0xbd50e560), z=0.0), dict(x=0, y=0, z=0)),
            (dict(x=number(0xbdccccce), y=number(0xbe1a9fbe), z=number(0x3dcccccc)), dict(x=-2, y=-1, z=0)),
            (dict(x=number(0xbdcccccc), y=number(0xbe1a9fbf), z=number(0x3dcccccd)), dict(x=-1, y=-2, z=1)),
            # Decimal .3 / .1 in Python double is below 3; Unity's rounded
            # binary32 division is exactly 3 (and -3 for the negative axis).
            (dict(x=.3, y=22.0, z=-.3), dict(x=3, y=220, z=-3)),
        ]
        for point, expected in cases:
            with self.subTest(point=point):
                self.assertEqual(unity_first_cell(point), expected)
                reports = sample_reports()
                relocated_fixture(reports, point, expected)
                self.assertEqual(validate_fall_fixture(reports, 2)[1], [])
                for axis in ("x", "y", "z"):
                    changed = copy.deepcopy(reports)
                    changed["host"]["fallCoverage"]["commands"][0]["firstCell"][axis] += 1
                    self.assert_invalid(changed)

    def test_float32_and_int32_overflow_fail_closed(self):
        for value in (1e39, -1e39, 3.4028234663852886e38, -3.4028234663852886e38,
                      214748364.8, -214748390.4, 10 ** 400, float("inf"), float("nan")):
            for axis in ("x", "y", "z"):
                point = dict(x=2, y=22, z=4); point[axis] = value
                with self.subTest(axis=axis, value=value):
                    self.assertIsNone(unity_first_cell(point))
                    reports = sample_reports()
                    reports["host"]["fallCoverage"]["commands"][0]["point"] = point
                    self.assert_invalid(reports)

    def test_null_or_malformed_drain_inputs_return_failure_without_exception(self):
        for value in (None, {}, "owners", [], [None, None],
                      [dict(owner=[]), dict(owner=1)], [dict(owner={}), dict(owner=1)],
                      [dict(owner=None), dict(owner=1)], [dict(owner=True), dict(owner=1)]):
            reports = sample_reports(); reports["host"]["fallCoverage"]["drainInputs"] = value
            summary, failures = validate_fall_fixture(reports, 2)
            self.assertIsNone(summary)
            self.assertTrue(failures)

    def test_request_and_finish_times_are_required_finite_nonnegative(self):
        for field in ("requestedRealtime", "finishRequestRealtime"):
            reports = sample_reports(); del reports["host"]["fallCoverage"][field]
            self.assert_invalid(reports)
            for value in (None, -1, True, float("inf"), float("nan"), 10 ** 400):
                reports = sample_reports(); reports["host"]["fallCoverage"][field] = value
                self.assert_invalid(reports)
        reports = sample_reports(); reports["host"]["fallCoverage"]["requestedRealtime"] = 12.1
        self.assert_invalid(reports)

    def test_future_callback_revisions_or_time_cannot_follow_finish_request(self):
        for field, value in (("completedRevision", 13), ("completedWorldRevision", 13), ("completedRealtime", 12.001)):
            reports = sample_reports()
            reports["host"]["fallCoverage"]["commands"][0][field] = value
            self.assert_invalid(reports)

    def test_callback_before_enqueue_remains_invalid_but_update_now_can_precede_prepare(self):
        reports = sample_reports()
        coverage = reports["host"]["fallCoverage"]
        self.assertLess(coverage["commands"][0]["queuedRealtime"], coverage["requestedRealtime"])
        self.assertEqual(validate_fall_fixture(reports, 2)[1], [])
        coverage["commands"][0]["completedRealtime"] = 9.999
        self.assert_invalid(reports)


if __name__ == "__main__":
    unittest.main()
