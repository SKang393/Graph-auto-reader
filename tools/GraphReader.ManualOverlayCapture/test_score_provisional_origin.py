# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from score_provisional_origin import FIRST_PREFIX, NO_ORIGIN, summarize


def report(changed=False):
    panels = []
    for index in range(21):
        reasons = ["other"]
        validity = "NeedsReview"
        if index == 0:
            reasons = [NO_ORIGIN, "other"]
            if not changed:
                reasons.insert(1, FIRST_PREFIX + "2, not session 1.")
                validity = "InvalidSessionOrigin"
        panels.append({"panel_id": str(index), "error": "Automatic calibration is incomplete: " + " | ".join(reasons),
                       "calibration_validity": validity,
                       "status": "failed", "failure_code": "WORKFLOW_RECALIBRATION_REQUIRED",
                       "axis_needs_review": True, "ocr_tick_regions": [], "centers_before_classification": [], "markers": []})
    return {"rows": [{"panels": panels}]}


class ProvisionalOriginScoreTests(unittest.TestCase):
    def test_only_unsupported_reason_is_removed(self):
        result = summarize(report(), report(changed=True))
        self.assertEqual(1, result["unsupported_first_session_claims_removed"])
        self.assertEqual(1, result["invalid_to_review"])
        self.assertEqual(21, result["same_other_reasons"])
        self.assertEqual(21, result["still_recalibration_required"])
        self.assertEqual(0, result["completed_panels"])

    def test_other_removed_reasons_are_visible(self):
        candidate = report(changed=True)
        candidate["rows"][0]["panels"][1]["error"] = "Automatic calibration is incomplete: "
        self.assertEqual(20, summarize(report(), candidate)["same_other_reasons"])

    def test_duplicate_panel_is_rejected(self):
        candidate = report()
        candidate["rows"][0]["panels"][1]["panel_id"] = "0"
        with self.assertRaises(ValueError):
            summarize(report(), candidate)


if __name__ == "__main__":
    unittest.main()
