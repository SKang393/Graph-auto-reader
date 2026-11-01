# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from score_phase_cluster_repair import WARNING, summarize


class PhaseClusterRepairScoreTests(unittest.TestCase):
    def test_counts_completed_panels_and_warning(self):
        def report(completed):
            return {"rows": [{"id": str(i), "image_sha256": str(i), "panels": [{
                "panel_id": str(i), "panel_sha256": str(i), "provenance": {},
                "status": "completed" if completed else "failed", "failure_code": None,
                "markers": [], "workflow_points": [],
                "workflow_warnings": [WARNING] if completed else [], "phase_validation": {"too_close_pairs": 0}}]}
                for i in range(7)]}
        counts = summarize(report(False), report(True))["counts"]
        self.assertEqual(7, counts["candidate_completed"])
        self.assertEqual(7, counts["previous_failure_now_completed"])
        self.assertEqual(7, counts["wide_cluster_warning_panels"])


if __name__ == "__main__":
    unittest.main()
