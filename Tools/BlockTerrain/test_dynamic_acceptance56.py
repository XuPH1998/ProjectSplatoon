"""Pure evidence-oracle tests; never starts Unity, Players or desktop tools."""
import copy
import binascii
import hashlib
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

import dynamic_acceptance56 as acceptance


class DynamicAcceptanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.files = tempfile.TemporaryDirectory(prefix="dynamic-oracle-")
        cls.image = Path(cls.files.name) / "proof.png"
        cls.blank_image = Path(cls.files.name) / "blank.png"
        cls.uniform_image = Path(cls.files.name) / "uniform.png"
        cls.write_png(cls.image, (20, 30, 40), (200, 150, 100))
        cls.write_png(cls.blank_image, (0, 0, 0), (0, 0, 0))
        cls.write_png(cls.uniform_image, (40, 40, 40), (40, 40, 40))

    @classmethod
    def tearDownClass(cls):
        cls.files.cleanup()

    @staticmethod
    def write_png(path, first, second, width=1920, height=1080):
        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data) & 0xffffffff)
        raw = b"".join(b"\x00" + bytes(first if y < height // 2 else second) * width for y in range(height))
        path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                         + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))

    def visual(self, path=None, **changes):
        path = path or self.image
        image = acceptance.png_stats(path)
        visual = dict(complete=True, valid=True, error="", colliderSamples=2,
                      colliderEdgeMin=[.3] * 3, colliderEdgeMax=[.3] * 3,
                      drawSamples=2, drawEdgeMin=[.3] * 3, drawEdgeMax=[.3] * 3,
                      drawInstances=2, shaderSupported=True, instancingSupported=True,
                      surfacePublishedCells=2, surfaceGridSamples=8, surfaceGridMaxError=0.,
                      surfaceTopSamples=2, surfaceTopMaxError=0., visibleSurface=2, visibleDynamic=2,
                      screenshotPath=str(path), screenshotSha256=image["sha256"], screenshotWidth=image["width"],
                      screenshotHeight=image["height"], screenshotNonBlackPixels=image["nonBlackPixels"],
                      screenshotDistinctColors=image["distinctColors"])
        visual.update(changes)
        return visual

    def state(self, stage="late", **changes):
        state = dict(stage=stage, command=2, generation=1, round=1, terrainRound=1, revision=3, dynamicRevision=7,
                     surfaces=2, dynamic=2, pink=2, blue=2, scorePink=2, scoreBlue=2,
                     surfaceHash="1" * 64, identityHash="2" * 64, frozenHash="3" * 64,
                     frozenAt=180., ready=True, players=2, failures=0, pending=0,
                     projectiles=0, frozen=stage in ("frozen", "reconnected"),
                     phase="Finished" if stage in ("frozen", "reconnected") else "Playing")
        state.update(changes)
        return state

    def report(self, **changes):
        report = dict(schema=1, session="test", mode="network", configuredPlayers=2, complete=True,
                      errors=0, weaponSimulation=47, blockProtocol=5, dynamicProtocol=1,
                      cellSize=.30000001192092896, materialRefills=0,
                      rangeActivationVersion=4, activationRadius=2., deactivationRadius=4.,
                      activationDistanceMetric="xz-horizontal",
                      graphics=True, graphicsApi="Direct3D12", normalMainCamera=True,
                      width=1920, height=1080, renderedFrames=1500, visual=self.visual(),
                      motion=[], corrections=[.01] * 20, states=[self.state(stage) for stage in acceptance.CHECKPOINTS],
                      reconnectRequested=True, reconnectDisconnected=True, reconnectCleanup=True,
                      reconnectSuccess=True, connectionGenerations=2,
                      actors=[dict(owner=owner, round=1, shots=30, spentMaterial=15.) for owner in (0, 1)])
        report["states"][-2] = self.state("cleared", surfaces=0, dynamic=0, pink=0, blue=0, scorePink=0, scoreBlue=0)
        report.update(changes)
        return report

    def enemy_report(self):
        plan = dict(session="test", status="ready", command=4, connectionGeneration=1,
                    round=1, owner=0, life=4, heroRevision=0, team=1,
                    targetRound=1, targetId=500, targetOwner=2)

        def row(kind, server_time, **fields):
            return dict(kind=kind, plan=copy.deepcopy(plan), serverTime=server_time,
                        round=1, life=4, heroRevision=0, **fields)

        input_fields = dict(sequence=77, inputTick=700, inputLife=4, inputHeroRevision=0,
                            fireSequence=20, releaseSequence=5, fire=True, cancelFire=False,
                            swim=False, alive=True, needsRelease=False, timedOut=False)
        projectile = dict(sequence=77, inputTick=700, action=123456789, shotSequence=31,
                          entity=101, pellet=0)
        samples = [row("input", 10., **input_fields),
                   row("authority-input", 10.01, **input_fields, acknowledged=77,
                       queuedBefore=1, discardedInputs=0, source="oldest queued input"),
                   row("weapon", 10.01, **input_fields, acknowledged=77, standQueried=True,
                       canShoot=True, emitted=True, materialBefore=50., materialAfter=49.5,
                       shotSequenceBefore=30, shotSequence=31, action=123456789,
                       phaseBefore="Idle", phaseAfter="Firing"),
                   row("spawn", 10.01, **projectile),
                   row("contact", 10.1, **projectile, knownContact=True, validContact=True,
                       source="block contact", actualTargetId=501, actualTargetOwner=2),
                   row("direct-dynamic-removal", 10.1, **projectile, removed=1, removedIds=[502],
                       actualIdsAbsent=True, revision=8)]
        return dict(session="test", connectionGenerations=1, removedBodies=1,
                    current=self.state("enemy", command=4, projectiles=1),
                    actors=[dict(owner=0, round=1, shots=31, spentMaterial=15.5)],
                    enemyExecution=dict(capacity=8192, overflow=0, samples=samples))

    def enemy(self):
        return dict(before=dict(stage="drain", session="test", command=3, generation=1,
                                round=1, pending=0, projectiles=0,
                                removedBodies=0, paidAttacks=30, spentMaterial=15.),
                    after=acceptance.enemy_observation(self.enemy_report()))

    def visual_seed(self, round_id=1):
        before = dict(stage="late", command=2, round=round_id, players=2, surfaces=2, dynamic=2,
                      pink=4, blue=0, createdBodies=2, errors=0, materialRefills=0,
                      actors=[dict(owner=0, shots=5, spentMaterial=4.6),
                              dict(owner=1, shots=0, spentMaterial=0.)])
        after = dict(stage="seed", command=3, round=round_id, players=2, surfaces=3, dynamic=4,
                     pink=4, blue=3, createdBodies=4, errors=0, materialRefills=0,
                     actors=[dict(owner=0, shots=5, spentMaterial=4.6),
                             dict(owner=1, shots=3, spentMaterial=2.7)])
        drained = {name: self.state("visual-drain", command=4, round=round_id, terrainRound=round_id,
                                   surfaces=3, dynamic=4, pink=4, blue=3, scorePink=4, scoreBlue=3)
                   for name in ("host", "client-1")}
        return dict(before=before, after=after, drained=drained)

    def reports(self):
        reports = {name: self.report() for name in ("host", "client-1")}
        for sequence in range(1, 21):
            row = dict(peer=1, round=1, revision=7, tick=sequence + 100, sequence=sequence, stage="combat", hash="a" * 64,
                       rows=2, appliedRows=2, mismatches=0, sending=False)
            reports["client-1"]["motion"].append(row)
            reports["host"]["motion"].append(dict(row, sending=True))
        return reports

    def test_positive_complete_network_evidence_and_float_cell_size(self):
        result = acceptance.network_oracle(self.reports(), 2, "test", 100, self.enemy(),
                                           visual_seed_evidence=self.visual_seed())
        self.assertTrue(result["passed"], result["failures"])
        self.assertEqual(result["ordinaryEnemyEvidence"]["after"]["removedBodies"], 1)
        self.assertEqual(result["visualSeedEvidence"], self.visual_seed())

    def test_visual_seed_keeps_existing_paid_proof_and_requires_new_late_actor_payments(self):
        evidence = self.visual_seed()
        self.assertTrue(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2))
        self.assertTrue(acceptance.valid_visual_seed_evidence(evidence, 2))
        practice = self.visual_seed(round_id=0)
        self.assertTrue(acceptance.valid_visual_seed_payment(practice["before"], practice["after"], 2))
        self.assertTrue(acceptance.valid_visual_seed_evidence(practice, 2))
        for changes in (dict(shots=0), dict(spentMaterial=0.), dict(shots=0, spentMaterial=0.)):
            evidence = self.visual_seed()
            evidence["after"]["actors"][1].update(changes)
            self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2), changes)
        for changes in (dict(shots=4), dict(spentMaterial=4.5)):
            evidence = self.visual_seed()
            evidence["after"]["actors"][0].update(changes)
            self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2), changes)
        # A partly paid baseline still needs both counters to advance during this seed.
        evidence = self.visual_seed()
        evidence["before"]["actors"][1]["shots"] = 3
        self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2))
        evidence = self.visual_seed()
        evidence["before"]["actors"][1]["spentMaterial"] = 2.7
        self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2))

    def test_visual_seed_rejects_wrong_round_owner_set_duplicate_actors_and_old_commands(self):
        for changes in (dict(round=2), dict(round=-1), dict(command=2), dict(command=1),
                        dict(stage="late"), dict(players=1)):
            evidence = self.visual_seed()
            evidence["after"].update(changes)
            self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2), changes)
        for which in ("before", "after"):
            for actors in ([dict(owner=0, shots=5, spentMaterial=4.6)],
                           [dict(owner=0, shots=5, spentMaterial=4.6), dict(owner=0, shots=3, spentMaterial=2.7)],
                           [dict(owner=0, shots=5, spentMaterial=4.6), dict(owner=9, shots=3, spentMaterial=2.7)]):
                evidence = self.visual_seed()
                evidence[which]["actors"] = actors
                self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2), (which, actors))
        evidence = self.visual_seed()
        evidence["before"]["actors"][1].update(shots=1, spentMaterial=1.)
        self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2))

    def test_visual_seed_requires_growth_of_both_types_and_both_team_totals(self):
        for changes in (dict(surfaces=2, dynamic=4, pink=4, blue=2),
                        dict(surfaces=3, dynamic=2, pink=4, blue=1),
                        dict(surfaces=3, dynamic=4, pink=7, blue=0),
                        dict(surfaces=3, dynamic=4, pink=0, blue=7),
                        dict(materialRefills=1), dict(errors=1)):
            evidence = self.visual_seed()
            evidence["after"].update(changes)
            self.assertFalse(acceptance.valid_visual_seed_payment(evidence["before"], evidence["after"], 2), changes)

    def test_visual_seed_observation_copies_only_same_round_authoritative_actor_evidence(self):
        reports = self.reports()
        reports["host"]["current"] = self.state("late", command=2, round=0, terrainRound=0,
                                                   surfaces=2, dynamic=2, pink=4, blue=0, scorePink=4, scoreBlue=0)
        reports["host"]["actors"] = [dict(owner=0, round=0, shots=5, spentMaterial=4.6),
                                       dict(owner=1, round=0, shots=0, spentMaterial=0.),
                                       dict(owner=1, round=9, shots=99, spentMaterial=99.)]
        reports["host"].update(createdBodies=2, errors=0, materialRefills=0)
        observed = acceptance.visual_seed_observation(reports, 2)
        self.assertEqual(observed["round"], 0)
        self.assertEqual(observed["command"], 2)
        self.assertEqual(len(observed["actors"]), 2)
        self.assertEqual({actor["owner"] for actor in observed["actors"]}, {0, 1})
        reports["host"]["actors"][0]["shots"] = 100
        reports["host"]["current"]["surfaces"] = 100
        self.assertEqual(observed["actors"][0]["shots"], 5)
        self.assertEqual(observed["surfaces"], 2)

    def test_current_checkpoint_cannot_be_masked_by_aligned_historical_states(self):
        reports = self.reports()
        for report in reports.values():
            report["current"] = self.state("late", command=2)
        self.assertIsNotNone(acceptance.current_checkpoint(reports, "late", 2, 2))
        for changes in (dict(identityHash="f" * 64), dict(command=1), dict(stage="seed"),
                        dict(pending=1), dict(ready=False)):
            changed = copy.deepcopy(reports)
            changed["client-1"]["current"].update(changes)
            self.assertIsNotNone(acceptance.checkpoint(changed, "late", 2))
            self.assertIsNone(acceptance.current_checkpoint(changed, "late", 2, 2), changes)

    def test_visual_seed_drained_snapshot_is_current_complete_and_newer_than_seed(self):
        for changes in (dict(command=3), dict(stage="seed"), dict(round=2, terrainRound=2),
                        dict(pending=1), dict(projectiles=1), dict(ready=False),
                        dict(surfaces=2, pink=3, scorePink=3)):
            evidence = self.visual_seed()
            for row in evidence["drained"].values():
                row.update(changes)
            self.assertFalse(acceptance.valid_visual_seed_evidence(evidence, 2), changes)
        evidence = self.visual_seed()
        evidence["drained"]["client-1"]["command"] = 5
        self.assertFalse(acceptance.valid_visual_seed_evidence(evidence, 2))
        evidence = self.visual_seed()
        evidence["drained"].pop("client-1")
        self.assertFalse(acceptance.valid_visual_seed_evidence(evidence, 2))

    def test_late_history_survives_later_seed_drain_and_visual_samples(self):
        reports = self.reports()
        original = copy.deepcopy(acceptance.checkpoint(reports, "late", 2))
        for report in reports.values():
            report["states"].extend(self.state(stage, command=command) for command, stage in
                                     ((3, "seed"), (4, "visual-drain"), (5, "visual-proof")))
            report["current"] = report["states"][-1]
        self.assertEqual(acceptance.checkpoint(reports, "late", 2), original)
        self.assertIsNone(acceptance.current_checkpoint(reports, "late", 2, 2))

    def test_network_oracle_requires_durable_visual_seed_witness(self):
        self.assertFalse(acceptance.network_oracle(self.reports(), 2, "test", 0, self.enemy())["passed"])
        evidence = self.visual_seed()
        evidence["after"]["actors"][1]["spentMaterial"] = 0.
        self.assertFalse(acceptance.network_oracle(self.reports(), 2, "test", 0, self.enemy(),
                                                 visual_seed_evidence=evidence)["passed"])

    def test_visual_seed_witness_must_come_from_the_preserved_late_round_and_command(self):
        for evidence in (self.visual_seed(round_id=2), self.visual_seed()):
            if evidence["before"]["round"] == 1:
                evidence["before"]["command"] = 1
            self.assertTrue(acceptance.valid_visual_seed_evidence(evidence, 2))
            self.assertFalse(acceptance.network_oracle(self.reports(), 2, "test", 0, self.enemy(),
                                                     visual_seed_evidence=evidence)["passed"])

    def test_missing_hash_and_nonhex_hash_cannot_form_a_checkpoint(self):
        for value in (None, "", "z" * 64):
            reports = self.reports()
            reports["client-1"]["states"][0]["surfaceHash"] = value
            self.assertIsNone(acceptance.checkpoint(reports, "late", 2))
        state = self.state()
        del state["identityHash"]
        self.assertFalse(acceptance.valid_state(state, 2))

    def test_free_body_identity_and_frozen_pose_must_match_on_both_peers(self):
        for field, stage in (("identityHash", "late"), ("frozenHash", "frozen")):
            reports = self.reports()
            target = next(row for row in reports["client-1"]["states"] if row["stage"] == stage)
            target[field] = "f" * 64
            self.assertIsNone(acceptance.checkpoint(reports, stage, 2))

    def test_incomplete_counts_scores_and_frozen_metadata_are_rejected(self):
        for changes in (dict(ready=False), dict(pending=1), dict(failures=1), dict(players=1),
                        dict(blue=4), dict(scorePink=0), dict(terrainRound=0)):
            self.assertFalse(acceptance.valid_state(self.state(**changes), 2), changes)
        for changes in (dict(frozen=False), dict(phase="Playing"), dict(frozenAt=float("nan")), dict(frozenAt=0)):
            reports = {name: dict(states=[self.state("frozen", **changes)]) for name in ("host", "client-1")}
            self.assertIsNone(acceptance.checkpoint(reports, "frozen", 2), changes)

    def test_motion_requires_exact_sent_packet_and_actual_body_application(self):
        for changes in (dict(hash="b" * 64), dict(mismatches=1), dict(appliedRows=0),
                        dict(appliedRows=13), dict(rows=None), dict(peer=9), dict(hash=None), dict(tick=None), dict(tick=999)):
            reports = self.reports()
            reports["client-1"]["motion"][0].update(changes)
            self.assertFalse(acceptance.motion_oracle(reports, 2)["passed"], changes)

    def test_duplicate_receipts_cannot_supply_the_twenty_packet_minimum(self):
        reports = self.reports()
        reports["client-1"]["motion"] = [copy.deepcopy(reports["client-1"]["motion"][0]) for _ in range(20)]
        self.assertFalse(acceptance.motion_oracle(reports, 2)["passed"])

    def test_ordinary_enemy_proof_survives_clear_and_requires_paid_new_attack(self):
        reports = self.reports()
        reports["host"]["removedBodies"] = 0  # Counter resets in the next round; saved witness must remain valid.
        self.assertTrue(acceptance.network_oracle(reports, 2, "test", 0, self.enemy(),
                                                visual_seed_evidence=self.visual_seed())["passed"])
        for changes in (dict(removedBodies=0), dict(paidAttacks=30), dict(spentMaterial=15.), dict(round=2)):
            enemy = self.enemy()
            enemy["after"].update(changes)
            self.assertFalse(acceptance.valid_enemy_evidence(enemy), changes)
        enemy = self.enemy()
        enemy["before"]["projectiles"] = 1
        self.assertFalse(acceptance.valid_enemy_evidence(enemy))
        self.assertFalse(acceptance.network_oracle(reports, 2, "test", 0,
                                                 visual_seed_evidence=self.visual_seed())["passed"])

    def assert_invalid_enemy_report(self, report, message=None):
        witness = acceptance.enemy_execution_witness(report)
        self.assertFalse(witness["valid"], message)
        evidence = self.enemy()
        evidence["after"] = acceptance.enemy_observation(report)
        self.assertFalse(acceptance.valid_enemy_evidence(evidence), message)

    def test_enemy_execution_preserves_six_actual_rows_and_allows_legal_spread_and_neighbour_removal(self):
        report = self.enemy_report()
        witness = acceptance.enemy_execution_witness(report)
        self.assertTrue(witness["valid"], witness)
        self.assertEqual(witness["sampleIndices"], list(range(6)))
        self.assertEqual(witness["samples"], report["enemyExecution"]["samples"])
        self.assertEqual(witness["samples"][0]["plan"]["targetId"], 500)
        self.assertEqual(witness["samples"][4]["actualTargetId"], 501)
        self.assertEqual(witness["samples"][5]["removedIds"], [502])
        self.assertTrue(acceptance.valid_enemy_evidence(self.enemy()))
        report["enemyExecution"]["samples"][4]["knownContact"] = False
        self.assertTrue(acceptance.enemy_execution_witness(report)["valid"])
        report["enemyExecution"]["samples"][1].update(queuedBefore=0, source="reused last input")
        self.assertTrue(acceptance.enemy_execution_witness(report)["valid"])

    def test_enemy_execution_observation_is_detached_from_later_mutation_and_round_clear(self):
        report = self.enemy_report()
        saved = acceptance.enemy_observation(report)
        report["enemyExecution"]["samples"][5]["removedIds"].clear()
        report["enemyExecution"]["samples"][0]["plan"]["session"] = "changed"
        report["enemyExecution"]["samples"].clear()
        report.update(session="later", connectionGenerations=2, actors=[], removedBodies=0)
        report["current"] = self.state("cleared", command=9, generation=2, round=2)
        self.assertEqual(saved["executionWitness"]["samples"][5]["removedIds"], [502])
        evidence = self.enemy()
        evidence["after"] = saved
        self.assertTrue(acceptance.valid_enemy_evidence(evidence))

    def test_enemy_execution_rejects_missing_reordered_or_forecast_only_stages(self):
        for index in range(6):
            with self.subTest(missing=index):
                report = self.enemy_report()
                report["enemyExecution"]["samples"].pop(index)
                self.assert_invalid_enemy_report(report)
        for index in range(5):
            with self.subTest(reordered=index):
                report = self.enemy_report()
                samples = report["enemyExecution"]["samples"]
                samples[index], samples[index + 1] = samples[index + 1], samples[index]
                self.assert_invalid_enemy_report(report)
        for kind in ("forecast", "planned-contact", "player contact", "direct-static-removal"):
            report = self.enemy_report()
            report["enemyExecution"]["samples"][4]["kind"] = kind
            self.assert_invalid_enemy_report(report, kind)

    def test_enemy_execution_does_not_join_different_plan_or_actual_lifecycle_identities(self):
        changes = dict(session="other", command=5, connectionGeneration=2, round=2, owner=7,
                       life=5, heroRevision=1, team=2, targetRound=2, targetId=999, targetOwner=1,
                       status="forecast")
        for index in range(6):
            for key, value in changes.items():
                with self.subTest(stage=index, plan_field=key):
                    report = self.enemy_report()
                    report["enemyExecution"]["samples"][index]["plan"][key] = value
                    self.assert_invalid_enemy_report(report)
            for key, value in dict(round=2, life=5, heroRevision=1).items():
                with self.subTest(stage=index, actual_field=key):
                    report = self.enemy_report()
                    report["enemyExecution"]["samples"][index][key] = value
                    self.assert_invalid_enemy_report(report)

    def test_enemy_execution_requires_current_session_command_round_and_connection_generation(self):
        for key, value in dict(stage="drain", command=3, round=2, generation=2).items():
            report = self.enemy_report()
            report["current"][key] = value
            self.assert_invalid_enemy_report(report, key)
        for changes in (dict(session="other"), dict(connectionGenerations=2)):
            report = self.enemy_report()
            report.update(changes)
            self.assert_invalid_enemy_report(report, changes)
        for key in ("session", "command", "round", "generation"):
            evidence = self.enemy()
            evidence["after"][key] = "other" if key == "session" else evidence["after"][key] + 1
            self.assertFalse(acceptance.valid_enemy_evidence(evidence), key)
        for which, changes in (("before", dict(command=4)), ("before", dict(generation=2)),
                               ("before", dict(session="other")), ("after", dict(command=3))):
            evidence = self.enemy()
            evidence[which].update(changes)
            self.assertFalse(acceptance.valid_enemy_evidence(evidence), (which, changes))

    def test_enemy_execution_requires_final_input_and_selected_authority_input_alignment(self):
        for index in range(3):
            for key in acceptance._ENEMY_INPUT_FIELDS:
                report = self.enemy_report()
                report["enemyExecution"]["samples"][index][key] += 1
                self.assert_invalid_enemy_report(report, (index, key))
            for key, value in dict(fire=False, cancelFire=True, swim=True, alive=False, needsRelease=True).items():
                report = self.enemy_report()
                report["enemyExecution"]["samples"][index][key] = value
                self.assert_invalid_enemy_report(report, (index, key))
        for index in (1, 2):
            for changes in (dict(timedOut=True), dict(acknowledged=76)):
                report = self.enemy_report()
                report["enemyExecution"]["samples"][index].update(changes)
                self.assert_invalid_enemy_report(report, (index, changes))
        for changes in (dict(queuedBefore=-1), dict(discardedInputs=1), dict(source="old forecast"),
                        dict(queuedBefore=0, source="oldest queued input")):
            report = self.enemy_report()
            report["enemyExecution"]["samples"][1].update(changes)
            self.assert_invalid_enemy_report(report, changes)

    def test_enemy_execution_requires_real_paid_weapon_step_and_resulting_shot(self):
        for changes in (dict(standQueried=False), dict(canShoot=False), dict(emitted=False),
                        dict(materialBefore=49.5), dict(materialAfter=50.), dict(materialAfter=-1.),
                        dict(materialBefore=float("nan")), dict(materialAfter=float("inf")),
                        dict(shotSequenceBefore=31), dict(shotSequence=30), dict(action=0),
                        dict(phaseBefore=""), dict(phaseAfter=None)):
            report = self.enemy_report()
            report["enemyExecution"]["samples"][2].update(changes)
            self.assert_invalid_enemy_report(report, changes)

    def test_enemy_execution_requires_same_emitted_entity_pellet_action_shot_sequence_and_tick(self):
        for index in range(3, 6):
            for key in ("sequence", "inputTick", "action", "shotSequence", "entity", "pellet"):
                report = self.enemy_report()
                report["enemyExecution"]["samples"][index][key] += 1
                self.assert_invalid_enemy_report(report, (index, key))

    def test_enemy_execution_requires_real_valid_enemy_block_contact(self):
        for changes in (dict(knownContact=1), dict(validContact=False), dict(source="player contact"),
                        dict(source="stale block contact"), dict(actualTargetId=0),
                        dict(actualTargetOwner=1), dict(actualTargetOwner=0)):
            report = self.enemy_report()
            report["enemyExecution"]["samples"][4].update(changes)
            self.assert_invalid_enemy_report(report, changes)

    def test_enemy_execution_requires_actual_unique_deleted_ids_and_revision(self):
        for changes in (dict(actualIdsAbsent=False), dict(removed=0), dict(removedIds=[]),
                        dict(removedIds=[502, 503]), dict(removed=2, removedIds=[502, 502]),
                        dict(removedIds=[0]), dict(removedIds=[True]), dict(removedIds=["502"]),
                        dict(removedIds=[{}]), dict(revision=0)):
            report = self.enemy_report()
            report["enemyExecution"]["samples"][5].update(changes)
            self.assert_invalid_enemy_report(report, changes)

    def test_enemy_execution_explicitly_rejects_overflow_or_capacity_violation_even_with_good_chain(self):
        for changes in (dict(overflow=1), dict(overflow=-1), dict(overflow=True),
                        dict(capacity=0), dict(capacity=5), dict(capacity=8193), dict(capacity=True)):
            report = self.enemy_report()
            report["enemyExecution"].update(changes)
            witness = acceptance.enemy_execution_witness(report)
            self.assertFalse(witness["valid"], changes)
            self.assertIn("overflow or invalid capacity", witness["reason"])
        report = self.enemy_report()
        report["enemyExecution"].update(capacity=6)
        report["enemyExecution"]["samples"].append(dict(kind="noise"))
        self.assert_invalid_enemy_report(report)

    def test_enemy_execution_rejects_nonmonotonic_or_invalid_observation_time(self):
        for index in range(6):
            for value in (-1, float("nan"), float("inf"), None, True):
                report = self.enemy_report()
                report["enemyExecution"]["samples"][index]["serverTime"] = value
                self.assert_invalid_enemy_report(report, (index, value))
        report = self.enemy_report()
        report["enemyExecution"]["samples"][4]["serverTime"] = 9.
        self.assert_invalid_enemy_report(report)

    def test_enemy_execution_revalidates_saved_rows_instead_of_trusting_valid_flag(self):
        evidence = self.enemy()
        evidence["after"]["executionWitness"] = dict(valid=True)
        self.assertFalse(acceptance.valid_enemy_evidence(evidence))
        for index in range(6):
            evidence = self.enemy()
            evidence["after"]["executionWitness"]["samples"][index]["kind"] = "forecast"
            self.assertFalse(acceptance.valid_enemy_evidence(evidence), index)
        for changes in (dict(sampleIndices=[0, 1, 2, 3, 4, 4]), dict(sampleIndices=[True, 1, 2, 3, 4, 5]),
                        dict(sampleCount=5), dict(sampleCount=8193), dict(overflow=1),
                        dict(command=True), dict(round=True), dict(generation=True)):
            evidence = self.enemy()
            evidence["after"]["executionWitness"].update(changes)
            self.assertFalse(acceptance.valid_enemy_evidence(evidence), changes)

    def test_enemy_execution_malformed_ids_are_rejected_without_hash_or_type_errors(self):
        for index, keys in ((0, acceptance._ENEMY_INPUT_FIELDS),
                            (1, ("acknowledged", "discardedInputs", "queuedBefore")),
                            (2, ("shotSequenceBefore", "shotSequence", "action", "acknowledged")),
                            (3, ("sequence", "inputTick", "action", "shotSequence", "entity", "pellet")),
                            (4, ("sequence", "inputTick", "action", "shotSequence", "entity", "pellet")),
                            (5, ("sequence", "inputTick", "action", "shotSequence", "entity", "pellet"))):
            for key in keys:
                for value in (True, False, "1", [], {}, None, -1):
                    report = self.enemy_report()
                    report["enemyExecution"]["samples"][index][key] = value
                    self.assert_invalid_enemy_report(report, (index, key, value))
        for key in acceptance._ENEMY_PLAN_FIELDS[1:]:
            for value in (True, [], {}):
                report = self.enemy_report()
                report["enemyExecution"]["samples"][0]["plan"][key] = value
                self.assert_invalid_enemy_report(report, (key, value))
        for value in (None, [], True, "bad"):
            self.assertFalse(acceptance.enemy_execution_witness(value)["valid"])
            self.assertFalse(acceptance.valid_enemy_evidence(value))
        for which in ("before", "after"):
            for value in (None, [], True):
                evidence = self.enemy()
                evidence[which] = value
                self.assertFalse(acceptance.valid_enemy_evidence(evidence))
    def test_protocol_session_incomplete_and_cell_size_drift_fail(self):
        for changes in (dict(session="other"), dict(complete=False), dict(errors=1), dict(cellSize=.25), dict(cellSize=.2),
                        dict(weaponSimulation=40), dict(weaponSimulation=41), dict(blockProtocol=4), dict(dynamicProtocol=0),
                        dict(rangeActivationVersion=None), dict(rangeActivationVersion=0), dict(rangeActivationVersion=1),
                        dict(rangeActivationVersion=2),
                        dict(activationRadius=4), dict(deactivationRadius=2), dict(activationRadius=None),
                        dict(activationDistanceMetric="3d-sphere")):
            reports = self.reports()
            reports["client-1"].update(changes)
            self.assertTrue(acceptance.report_failures(reports, 2, "test", "network"), changes)

    def test_pre_native_sleep_versions_cannot_pass_any_final_oracle(self):
        for changes in (dict(weaponSimulation=41), dict(weaponSimulation=42), dict(weaponSimulation=43), dict(weaponSimulation=44), dict(weaponSimulation=45), dict(rangeActivationVersion=2), dict(rangeActivationVersion=3)):
            with self.subTest(changes=changes):
                reports = self.reports()
                reports["client-1"].update(changes)
                result = acceptance.network_oracle(reports, 2, "test", 0, self.enemy(),
                                                   visual_seed_evidence=self.visual_seed())
                self.assertFalse(result["passed"])
                self.assertTrue(any("gameplay protocol" in failure or "range activation policy" in failure
                                    for failure in result["failures"]), result["failures"])
                for mode in ("natural", "blast8"):
                    reports = self.workload_reports(mode)
                    reports["client-7"].update(changes)
                    result = acceptance.workload_oracle(reports, 8, "test", mode)
                    self.assertFalse(result["passed"], mode)
                    self.assertTrue(any("gameplay protocol" in failure or "range activation policy" in failure
                                        for failure in result["failures"]), (mode, result["failures"]))

    def test_graphics_gate_applies_to_network_and_both_workload_modes(self):
        for changes in (dict(graphicsApi="Direct3D11"), dict(graphics=False), dict(normalMainCamera=False),
                        dict(width=1280), dict(height=720), dict(renderedFrames=0), dict(renderedFrames=None)):
            reports = self.reports()
            reports["client-1"].update(changes)
            self.assertFalse(acceptance.network_oracle(reports, 2, "test", 0, self.enemy(),
                                                     visual_seed_evidence=self.visual_seed())["passed"], changes)
            for mode in ("natural", "blast8"):
                reports = self.workload_reports(mode)
                reports["client-7"].update(changes)
                self.assertFalse(acceptance.workload_oracle(reports, 8, "test", mode)["passed"], (mode, changes))

    def test_visual_requires_actual_collider_and_draw_geometry_on_every_player(self):
        for changes in (dict(drawSamples=0), dict(colliderSamples=0), dict(drawInstances=0),
                        dict(shaderSupported=False), dict(instancingSupported=False),
                        dict(drawEdgeMin=[.25, .2, .2]), dict(colliderEdgeMax=[.2, .2, .4]),
                        dict(drawEdgeMax=[]), dict(drawEdgeMin=[float("nan")] * 3),
                        dict(complete=False), dict(valid=False, error="readback failed")):
            reports = self.reports()
            reports["client-1"]["visual"].update(changes)
            self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"], changes)
        reports = self.reports()
        del reports["host"]["visual"]
        self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"])

    def test_visual_host_requires_both_visible_kinds_and_actual_published_surface_geometry(self):
        for changes in (dict(visibleSurface=0), dict(visibleDynamic=0), dict(surfacePublishedCells=0),
                        dict(surfaceGridSamples=0), dict(surfaceTopSamples=0),
                        dict(surfaceGridMaxError=.01), dict(surfaceTopMaxError=.01)):
            reports = self.reports()
            reports["host"]["visual"].update(changes)
            self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"], changes)
        reports = self.reports()
        reports["client-1"]["visual"].update(visibleSurface=0, visibleDynamic=1)
        self.assertTrue(acceptance.visual_oracle(reports, 2)["passed"])
        reports["client-1"]["visual"]["visibleDynamic"] = 0
        self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"])

    def test_visual_rejects_actual_black_uniform_missing_and_mismatched_screenshots(self):
        for path in (self.blank_image, self.uniform_image):
            reports = self.reports()
            reports["host"]["visual"] = self.visual(path)
            self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"], path)
        for changes in (dict(screenshotPath=str(Path(self.files.name) / "missing.png")),
                        dict(screenshotSha256="a" * 64), dict(screenshotWidth=1280),
                        dict(screenshotHeight=720), dict(screenshotNonBlackPixels=1),
                        dict(screenshotDistinctColors=100), dict(screenshotPath="relative.png")):
            reports = self.reports()
            reports["host"]["visual"].update(changes)
            self.assertFalse(acceptance.visual_oracle(reports, 2)["passed"], changes)
        reports = self.reports()
        self.assertFalse(acceptance.visual_oracle(reports, 2, Path(self.files.name))["passed"])
        for name in reports:
            sibling = Path(self.files.name) / f"{name}.dynamic.visual.png"
            sibling.write_bytes(self.image.read_bytes())
            reports[name]["visual"] = self.visual(sibling)
        self.assertTrue(acceptance.visual_oracle(reports, 2, Path(self.files.name))["passed"])

    def test_png_decoder_rejects_bad_crc_truncated_pixels_and_wrong_dimensions(self):
        corrupt = Path(self.files.name) / "bad-crc.png"
        data = bytearray(self.image.read_bytes()); data[-1] ^= 1
        corrupt.write_bytes(data)
        wrong = Path(self.files.name) / "wrong-dimensions.png"
        self.write_png(wrong, (10, 20, 30), (30, 20, 10), width=1280, height=720)
        truncated = Path(self.files.name) / "truncated.png"
        truncated.write_bytes(self.image.read_bytes()[:-12])
        for path in (corrupt, wrong, truncated):
            with self.assertRaises(ValueError, msg=str(path)):
                acceptance.png_stats(path)

    def test_moving_correction_legacy_targets_are_diagnostic_for_this_plan(self):
        reports = self.reports()
        reports["client-1"]["corrections"] = [.16] * 100
        result = acceptance.network_oracle(reports, 2, "test", 100, self.enemy(), visual_seed_evidence=self.visual_seed())
        self.assertTrue(result["passed"], result["failures"])
        self.assertEqual(result["corrections"]["client-1"]["maximum"], .16)
        self.assertEqual(result["legacyCorrectionDiagnostic"]["legacyTargets"], dict(rttMs=100, p95=.15, p99=.60))
        self.assertIs(result["legacyCorrectionDiagnostic"]["legacyTargetsMet"], False)
        self.assertIs(result["corrections"]["client-1"]["legacyTargetsMet"], False)
        result = acceptance.network_oracle(reports, 2, "test", 200, self.enemy(), visual_seed_evidence=self.visual_seed())
        self.assertTrue(result["passed"])
        self.assertIs(result["legacyCorrectionDiagnostic"]["applicable"], False)
        self.assertIsNone(result["legacyCorrectionDiagnostic"]["legacyTargetsMet"])
        reports["client-1"]["corrections"] = [.01] * 99 + [.61]
        # Nearest-rank P99 remains .01; the recorded maximum retains the outlier.
        result = acceptance.network_oracle(reports, 2, "test", 100, self.enemy(), visual_seed_evidence=self.visual_seed())
        self.assertTrue(result["passed"])
        self.assertEqual(result["corrections"]["client-1"]["p99"], .01)
        self.assertEqual(result["corrections"]["client-1"]["maximum"], .61)
        self.assertIs(result["legacyCorrectionDiagnostic"]["legacyTargetsMet"], True)
        reports["client-1"]["corrections"][-2] = .61
        result = acceptance.network_oracle(reports, 2, "test", 100, self.enemy(), visual_seed_evidence=self.visual_seed())
        self.assertTrue(result["passed"])
        self.assertIs(result["legacyCorrectionDiagnostic"]["legacyTargetsMet"], False)

    def test_moving_corrections_still_require_twenty_valid_nonnegative_samples(self):
        for values in ([], [.01] * 19, [.01] * 19 + [float("nan")], [.01] * 19 + [float("inf")],
                       [.01] * 19 + [-.1], None, "twenty samples"):
            reports = self.reports()
            reports["client-1"]["corrections"] = values
            result = acceptance.network_oracle(reports, 2, "test", 100, self.enemy(), visual_seed_evidence=self.visual_seed())
            self.assertFalse(result["passed"], values)
            self.assertTrue(any("insufficient valid moving reconciliation" in error for error in result["failures"]))
            self.assertIsNone(result["legacyCorrectionDiagnostic"]["legacyTargetsMet"])

    def workload_reports(self, mode="natural"):
        natural = dict(valid=True, localShots=50, localSpentMaterial=20., completedNormalRound=True,
                       elapsedServerSeconds=180., frameGatePassed=True, latencyGatePassed=True,
                       attachedCollisionSamples=10, dynamicCollisionSamples=10,
                       contactToCollision=dict(samples=30, p95=30.), attachedContactToCollision=dict(samples=10, p95=30.),
                       dynamicContactToCollision=dict(samples=10, p95=30.))
        natural.update(hostOwner=0, movedPushOwners=[1], playersThatPushed=1, pushedBodiesMoved=1, roles=[])
        for owner in range(8):
            team, slot = (1 if owner < 4 else 2), owner % 4
            point = dict(x=-11.5 if slot % 2 == 0 else 11.5, y=0., z=-20. if team == 1 else 20.)
            natural["roles"].append(dict(owner=owner, team=team, slot=slot, horizontalMeters=5. + owner,
                movementSamples=90, movementResets=0, waypointArrivals=1,
                waypointVisits=[dict(index=0, life=1, seconds=10., position=point, waypoint=point)],
                pushEvents=1 if owner == 1 else 0, movedPushes=[]))
        natural["roles"][1]["movedPushes"].append(dict(body=5, seconds=10., elapsedSeconds=.5,
            horizontalMeters=.02, alongForceMeters=.02, **{"from": dict(x=0., y=.1, z=0.),
            "to": dict(x=.02, y=.1, z=0.), "direction": dict(x=1., y=0., z=0.)}))
        return {name: self.report(mode=mode, configuredPlayers=8, graphics=True, normalMainCamera=True,
                                  width=1920, height=1080, renderedFrames=8000, current=dict(round=1),
                                  actors=[dict(owner=owner, round=1, shots=50, spentMaterial=20.) for owner in range(8)],
                                  natural=copy.deepcopy(natural), blast=dict(valid=True, maximumSameTick=8, collisionEffective=True,
                                      stopwatchFrequency=1000, batches=[dict(explosions=8, completed=8, removed=20,
                                          owners=list(range(8)), firstStarted=1000, lastCompleted=2000, groupCompletionMs=1000.)]))
                for name in acceptance.legacy.labels(8)}

    def test_natural_requires_full_real_rendered_round_with_local_paid_inputs(self):
        reports = self.workload_reports()
        self.assertTrue(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])
        for target, key, value in (("host", "completedNormalRound", False), ("host", "elapsedServerSeconds", 179.),
                                   ("host", "frameGatePassed", False), ("client-7", "localSpentMaterial", 0.),
                                   ("client-7", "localShots", 0), ("client-7", "valid", False)):
            reports = self.workload_reports()
            reports[target]["natural"][key] = value
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])
        reports = self.workload_reports()
        reports["host"]["graphics"] = False
        self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_collision_publication_latency_must_be_measured_and_within_gate(self):
        for key in ("contactToCollision", "attachedContactToCollision", "dynamicContactToCollision"):
            for changes in (dict(samples=0), dict(p95=201.), dict(p95=float("nan"))):
                reports = self.workload_reports()
                reports["host"]["natural"][key].update(changes)
                self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_cpu_profile_run_cannot_count_as_acceptance_even_with_passing_metrics(self):
        for mode in ("natural", "blast8"):
            reports = self.workload_reports(mode)
            self.assertTrue(acceptance.workload_oracle(reports, 8, "test", mode)["passed"])
            reports["host"]["natural"]["diagnosticCpuCapture"] = True
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", mode)["passed"])

    def test_body_state_diagnostic_cannot_count_as_acceptance(self):
        for mode in ("natural", "blast8"):
            reports = self.workload_reports(mode)
            self.assertTrue(acceptance.workload_oracle(reports, 8, "test", mode)["passed"])
            reports["host"]["natural"]["diagnosticBodyStateCapture"] = True
            result = acceptance.workload_oracle(reports, 8, "test", mode)
            self.assertFalse(result["passed"])
            self.assertTrue(any("body-state diagnostics" in failure for failure in result["failures"]))

    def test_body_state_request_requires_explicit_natural_time_and_bound_identities(self):
        check = acceptance.body_state_request_error
        source, package = "a" * 64, "b" * 64
        self.assertIsNone(check("natural", None, None, None))
        self.assertIsNone(check("natural", 0., source, package))
        self.assertIsNone(check("natural", 120., source, package))
        for mode, at, first, second in (("network", 120., source, package), ("blast8", 120., source, package),
                ("natural", -1., source, package), ("natural", float("nan"), source, package),
                ("natural", float("inf"), source, package), ("natural", None, source, package),
                ("natural", 120., None, package), ("natural", 120., source, "B" * 64),
                ("natural", 120., source[:-1], package)):
            self.assertIsNotNone(check(mode, at, first, second))

    def test_natural_requires_actual_movement_from_all_eight_distinct_owners(self):
        for value in (0., 1., -1., float("nan"), float("inf")):
            reports = self.workload_reports()
            reports["host"]["natural"]["roles"][7]["horizontalMeters"] = value
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"], value)
        for changes in (dict(movementSamples=0), dict(owner=6), dict(owner=99)):
            reports = self.workload_reports()
            reports["host"]["natural"]["roles"][7].update(changes)
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"], changes)
        reports = self.workload_reports()
        reports["host"]["natural"]["roles"].pop()
        self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_natural_host_alone_pushing_and_force_without_displacement_cannot_pass(self):
        reports = self.workload_reports()
        natural = reports["host"]["natural"]
        natural["roles"][0]["movedPushes"] = natural["roles"][1]["movedPushes"]
        natural["roles"][1]["movedPushes"] = []
        natural["roles"][0]["pushEvents"] = 1
        natural["movedPushOwners"] = [0]
        self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])
        reports = self.workload_reports()
        natural = reports["host"]["natural"]
        natural["roles"][1]["movedPushes"] = []
        natural.update(movedPushOwners=[], playersThatPushed=0, pushedBodiesMoved=0)
        self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_natural_push_proof_requires_force_direction_horizontal_positions_and_timeliness(self):
        for changes in (dict(elapsedSeconds=2.001), dict(alongForceMeters=float("nan")),
                        dict(horizontalMeters=float("nan")), dict(body=0), dict(seconds=float("nan")),
                        {"to": dict(x=0., y=.4, z=0.)}, {"direction": dict(x=0., y=1., z=0.)}):
            reports = self.workload_reports()
            reports["host"]["natural"]["roles"][1]["movedPushes"][0].update(changes)
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"], changes)
        for changes in (dict(movedPushOwners=[0]), dict(movedPushOwners=[1, 1]), dict(playersThatPushed=2),
                        dict(pushedBodiesMoved=2), dict(hostOwner=99)):
            reports = self.workload_reports()
            reports["host"]["natural"].update(changes)
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"], changes)

    def test_natural_accepts_one_remote_pusher_and_tracks_shared_body_per_owner(self):
        reports = self.workload_reports()
        self.assertTrue(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])
        natural = reports["host"]["natural"]
        natural["roles"][2]["movedPushes"] = copy.deepcopy(natural["roles"][1]["movedPushes"])
        natural["roles"][2]["pushEvents"] = 1
        natural.update(movedPushOwners=[1, 2], playersThatPushed=2)
        self.assertTrue(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_natural_waypoint_records_describe_actual_authored_arrivals_without_requiring_all_routes(self):
        for changes in (dict(index=1), dict(seconds=float("nan")),
                        dict(position=dict(x=0., y=0., z=0.)), dict(waypoint=dict(x=0., y=0., z=0.))):
            reports = self.workload_reports()
            reports["host"]["natural"]["roles"][7]["waypointVisits"][0].update(changes)
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"], changes)
        reports = self.workload_reports()
        for actor in reports["host"]["natural"]["roles"]:
            actor.update(waypointVisits=[], waypointArrivals=0)
        self.assertTrue(acceptance.workload_oracle(reports, 8, "test", "natural")["passed"])

    def test_blast_evidence_requires_eight_same_tick_and_effective_colliders(self):
        reports = self.workload_reports("blast8")
        self.assertTrue(acceptance.workload_oracle(reports, 8, "test", "blast8")["passed"])
        for key, value in (("valid", False), ("maximumSameTick", 7), ("collisionEffective", False)):
            reports = self.workload_reports("blast8")
            reports["host"]["blast"][key] = value
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "blast8")["passed"])
        for changes in (dict(completed=7), dict(removed=0), dict(groupCompletionMs=2001.), dict(groupCompletionMs=None),
                        dict(owners=[1] * 8), dict(lastCompleted=3001)):
            reports = self.workload_reports("blast8")
            reports["host"]["blast"]["batches"][0].update(changes)
            self.assertFalse(acceptance.workload_oracle(reports, 8, "test", "blast8")["passed"])


if __name__ == "__main__":
    unittest.main()
