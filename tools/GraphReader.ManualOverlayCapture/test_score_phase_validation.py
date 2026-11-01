# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from score_phase_validation import FIELDS, summarize


class PhaseValidationScoreTests(unittest.TestCase):
    def test_reproduces_counts_without_private_details(self):
        summary = {key: 0 for key in FIELDS}
        summary["too_close_pairs"] = 1
        def report(with_summary):
            return {"rows": [{"id": str(i), "image_sha256": str(i), "panels": [{
                "panel_id": str(i), "panel_sha256": str(i), "provenance": {}, "status": "failed",
                "failure_code": "PHASE_INVALID_DIVIDER_OUTPUT", "calibration_validity": "Valid",
                "markers": [], "phase_validation": summary if with_summary else None}]} for i in range(7)]}
        result = summarize(report(False), report(True))
        self.assertEqual(7, result["totals"]["same_status"])
        self.assertEqual(7, result["totals"]["too_close_pairs"])
        self.assertEqual(7, result["failures"]["invalid_divider_too_close_pairs"])


if __name__ == "__main__":
    unittest.main()
