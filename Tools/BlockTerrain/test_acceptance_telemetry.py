import unittest
from network_acceptance import reconciliation_groups


class ReconciliationEvidenceTests(unittest.TestCase):
    def event(self, event_id, ack, distance, **changes):
        event = dict(eventId=event_id, stage="traverse", terrainRevision=3, lifecycle=False,
                     initialSyncComplete=True, connectionGeneration=1, owner=1, networkObjectId=10,
                     terrainRound=1, authority=dict(life=2, acknowledgedInput=ack, health=100),
                     metres=distance, humanMetres=distance, predictedMotionMetres=.02,
                     authorityMotionMetres=.02, hasPreviousObservation=True, friendlySupport=True)
        event.update(changes)
        return event

    def report(self, *events):
        return dict(reconcileEventSchema=1, reconcileEventsIncluded=True, reconcileEventsComplete=True,
                    reconcileConsumerErrors=0, reconcileEventCount=len(events), reconcileEvents=list(events))

    def test_waiting_and_same_ack_resume_cannot_hide_the_larger_correction(self):
        report = self.report(self.event(1, 7, .6, terrainPendingAfter=True),
                             self.event(2, 7, .1, sameAcknowledgement=True, origin="TerrainResume", terrainPendingBefore=True))
        complete, groups = reconciliation_groups(report)
        self.assertTrue(complete)
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0]["metres"], .6)
        self.assertTrue(groups[0]["pending"])
        self.assertTrue(groups[0]["sameAckRecovery"])

    def test_reconnection_and_round_reuse_are_distinct_moving_acks(self):
        complete, groups = reconciliation_groups(self.report(self.event(1, 7, .1),
            self.event(2, 7, .2, connectionGeneration=2), self.event(3, 7, .3, terrainRound=2)))
        self.assertTrue(complete)
        self.assertEqual(len(groups), 3)

    def test_missing_or_duplicated_events_and_consumer_errors_fail_completeness(self):
        for changes in (dict(reconcileEventCount=3), dict(reconcileConsumerErrors=1), dict(reconcileEventsIncluded=False)):
            report = self.report(self.event(1, 7, .1)); report.update(changes)
            self.assertFalse(reconciliation_groups(report)[0])
        self.assertFalse(reconciliation_groups(self.report(self.event(1, 7, .1), self.event(1, 8, .2)))[0])

    def test_lifecycle_teleports_and_stationary_events_are_not_movement_evidence(self):
        complete, groups = reconciliation_groups(self.report(self.event(1, 7, 10, lifecycle=True),
            self.event(2, 8, 0, predictedMotionMetres=0, authorityMotionMetres=0)))
        self.assertTrue(complete)
        self.assertEqual(groups, [])


if __name__ == "__main__":
    unittest.main()
