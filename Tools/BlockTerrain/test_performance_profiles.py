import copy
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import performance_acceptance as performance


class PerformanceProfileTests(unittest.TestCase):
    profile = performance.PROTOTYPE_45_PROFILE

    def report(self, case="steady250k"):
        limits = {"frameP95": 22.2, "frameP99": 33.3,
                  "completeDuration": performance.profile_durations(self.profile)[case]}
        if case in ("steady250k", "combat250k"):
            limits["ordinaryPublishLatency"] = 200
        if case == "blast8":
            limits["eightSimultaneousBlasts"] = 2000
        return dict(acceptanceProfile=self.profile, requiredSeconds=limits["completeDuration"],
                    ordinaryPublishP95LimitMs=200, blastCompletionLimitMs=2000,
                    frameP95LimitMs=22.2, frameP99LimitMs=33.3,
                    frameSpikeBlocking=False, supportQueueClearBlocking=False,
                    requiredScenarios=list(performance.PROTOTYPE_45_CASES),
                    deferredScenarios=["tower32m", "cascade20k"], requiredSamplingSeconds=780,
                    gates=[dict(name=name, limit=limit, status="pass", diagnosticOnly=False)
                           for name, limit in limits.items()]
                          + [dict(name="requiredScenario", status="pass", diagnosticOnly=False)])

    def results(self):
        return [dict(scenario=case, acceptanceProfile=self.profile, profileMatched=True, passed=True)
                for case in performance.PROTOTYPE_45_CASES]

    def summary(self, results=None, requested=None, **changes):
        return performance.summarize_results({}, self.results() if results is None else results,
            list(performance.PROTOTYPE_45_CASES) if requested is None else requested,
            self.profile, "d3d12", **changes)

    def test_v2_has_four_full_workloads_and_approved_45fps_limits(self):
        self.assertEqual(performance.profile_cases(self.profile),
                         ("steady250k", "combat250k", "blast8", "scattered100k"))
        self.assertEqual(performance.profile_durations(self.profile),
                         dict(steady250k=180, combat250k=300, blast8=120, scattered100k=180))
        targets = performance.profile_targets(self.profile)
        self.assertEqual(sum(targets["requiredSeconds"].values()), 780)
        self.assertEqual((targets["frameP95Ms"], targets["frameP99Ms"]), (22.2, 33.3))
        self.assertEqual((targets["ordinaryPublishP95Ms"], targets["eightBlastCompletionMs"]), (200, 2000))
        self.assertFalse(targets["frameSpikeBlocking"])
        self.assertFalse(targets["supportQueueClearBlocking"])

    def test_strict_and_v1_keep_historical_six_case_targets(self):
        for profile, seconds, ordinary, blast in (("strict", 600, 80, 250), ("prototype-v1", 180, 200, 2000)):
            with self.subTest(profile=profile):
                self.assertEqual(performance.profile_cases(profile), performance.CASES)
                targets = performance.profile_targets(profile)
                self.assertEqual(targets["requiredSeconds"]["steady250k"], seconds)
                self.assertEqual(targets["requiredSeconds"]["tower32m"], 60)
                self.assertEqual(targets["requiredSeconds"]["cascade20k"], 120)
                self.assertEqual((targets["frameP95Ms"], targets["frameP99Ms"]), (15.5, 16.67))
                self.assertEqual((targets["ordinaryPublishP95Ms"], targets["eightBlastCompletionMs"]), (ordinary, blast))

    def test_new_report_requires_exact_profile_scope_and_gate_thresholds(self):
        for case in performance.PROTOTYPE_45_CASES:
            with self.subTest(case=case):
                self.assertTrue(performance.report_profile_matches(self.report(case), self.profile, case))
        for field, wrong in (("acceptanceProfile", "prototype-v1"), ("frameP95LimitMs", 15.5),
                             ("frameP99LimitMs", 16.67), ("ordinaryPublishP95LimitMs", 80),
                             ("blastCompletionLimitMs", 250), ("requiredSeconds", 10),
                             ("requiredSamplingSeconds", 960), ("requiredScenarios", list(performance.CASES)),
                             ("deferredScenarios", []), ("frameSpikeBlocking", True)):
            with self.subTest(field=field):
                report = self.report()
                report[field] = wrong
                self.assertFalse(performance.report_profile_matches(report, self.profile, "steady250k"))
        for name in ("frameP95", "frameP99", "completeDuration", "ordinaryPublishLatency"):
            report = self.report()
            next(gate for gate in report["gates"] if gate["name"] == name)["limit"] = 1
            self.assertFalse(performance.report_profile_matches(report, self.profile, "steady250k"))

    def test_relabelled_old_report_missing_new_contract_cannot_validate_new_package(self):
        report = self.report()
        for field in ("frameP95LimitMs", "frameP99LimitMs", "requiredScenarios",
                      "deferredScenarios", "requiredSamplingSeconds"):
            older = copy.deepcopy(report)
            older.pop(field)
            self.assertFalse(performance.report_profile_matches(older, self.profile, "steady250k"))
        for gates in ([], None, [None], [dict(name=[])], report["gates"] + [report["gates"][0]]):
            older = dict(report, gates=gates)
            self.assertFalse(performance.report_profile_matches(older, self.profile, "steady250k"))

    def test_deferred_cases_rejected_before_desktop_access_or_process_launch(self):
        with patch.object(performance, "require_background_player_desktop") as desktop:
            for case in ("tower32m", "cascade20k"):
                with self.assertRaisesRegex(ValueError, "deferred"):
                    performance.run_case(None, None, case, acceptance_profile=self.profile)
            desktop.assert_not_called()

    def test_diagnostic_spikes_remain_risks_and_mandatory_failures_still_fail(self):
        spike = dict(name="no33msSpikes", status="fail", diagnosticOnly=True)
        self.assertTrue(performance.gate_satisfied(spike, self.profile))
        self.assertTrue(performance.gate_satisfied(spike, "prototype-v1"))
        self.assertFalse(performance.gate_satisfied(spike, "strict"))
        self.assertFalse(performance.gate_satisfied(dict(name="frameP99", status="fail", diagnosticOnly=True), self.profile))
        report = dict(samples=100, metrics=[dict(name="frameIntervalMs", available=True, samples=100, max=45)])
        gate = performance.approved_frame_spike_gate(report, self.profile)
        self.assertEqual((gate["status"], gate["maximumMs"]), ("fail", 45))
        self.assertTrue(gate["diagnosticOnly"])

    def test_only_complete_matching_four_case_population_passes_summary(self):
        summary = self.summary()
        self.assertTrue(summary["requiredWorkloadsComplete"])
        self.assertTrue(summary["caseProfilesMatch"])
        self.assertTrue(summary["performancePassed"])
        self.assertFalse(summary["strictPerformancePassed"])
        self.assertFalse(summary["fullAcceptancePassed"])
        self.assertEqual(summary["requiredSamplingSeconds"], 780)
        self.assertEqual(summary["deferredScenarios"], ["tower32m", "cascade20k"])
        for results, requested in ((self.results()[:-1], list(performance.PROTOTYPE_45_CASES)),
                                   (self.results()[:-1], list(performance.PROTOTYPE_45_CASES[:-1])),
                                   (self.results()[:-1] + [self.results()[0]], list(performance.PROTOTYPE_45_CASES)),
                                   (self.results(), list(performance.CASES))):
            summary = self.summary(results, requested)
            self.assertFalse(summary["requiredWorkloadsComplete"])
            self.assertFalse(summary["performancePassed"])

    def test_mixed_old_profile_failed_or_diagnostic_runs_never_pass_new_summary(self):
        for field, value in (("acceptanceProfile", "prototype-v1"), ("profileMatched", False), ("passed", False)):
            results = self.results()
            results[0][field] = value
            self.assertFalse(self.summary(results)["performancePassed"])
        for changes in (dict(diagnostic_seconds=10), dict(frame_timing_observation=True)):
            self.assertFalse(self.summary(**changes)["performancePassed"])
        self.assertFalse(self.summary(results=[])["performancePassed"])

    def test_cli_defaults_to_the_selected_profile_complete_cases_without_launching_players(self):
        for profile in ("strict", "prototype-v1", self.profile):
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as directory:
                exe = Path(directory) / "fixture.exe"
                exe.write_bytes(b"inert unit-test package identity")
                invoked = []
                def fake_case(_exe, _directory, case, *_arguments):
                    invoked.append(case)
                    return dict(scenario=case, acceptanceProfile=profile, profileMatched=True, passed=True)
                arguments = ["performance_acceptance.py", "--player", str(exe), "--output", directory,
                             "--acceptance-profile", profile]
                with patch("sys.argv", arguments), patch.object(performance, "run_case", side_effect=fake_case), \
                        patch.object(performance, "write_json") as writer:
                    self.assertEqual(performance.main(), 0)
                    self.assertEqual(invoked, list(performance.profile_cases(profile)))
                    summary = writer.call_args.args[1]
                    self.assertTrue(summary["requiredWorkloadsComplete"])
                    self.assertTrue(summary["performancePassed"])

    def test_cli_rejects_deferred_workloads_before_checking_the_package_or_launching(self):
        arguments = ["performance_acceptance.py", "--player", "missing-fixture.exe", "--acceptance-profile",
                     self.profile, "--cases", "tower32m"]
        with patch("sys.argv", arguments), patch.object(performance, "run_case") as launch, \
                contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as failure:
                performance.main()
            self.assertEqual(failure.exception.code, 2)
            launch.assert_not_called()


if __name__ == "__main__":
    unittest.main()
