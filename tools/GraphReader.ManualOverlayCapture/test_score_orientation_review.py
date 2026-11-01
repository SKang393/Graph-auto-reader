# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import unittest

from score_orientation_review import summarize
from test_score_wide_horizontal_candidate import report


class OrientationReviewScoreTests(unittest.TestCase):
    def test_replacement_warning_is_counted_without_hiding_other_reasons(self):
        baseline = report()
        prior = copy.deepcopy(baseline)
        current = copy.deepcopy(prior)
        baseline["rows"][0]["panels"][0]["error"] = (
            "Automatic calibration is incomplete: ocr_tick_sequence_needs_review:YTick:competing_numeric_readings:a")
        prior["rows"][0]["panels"][0]["error"] = "Automatic calibration is incomplete: other"
        current["rows"][0]["panels"][0]["error"] = (
            "Automatic calibration is incomplete: other | ocr_tick_sequence_needs_review:YTick:orientation_override:a")
        summary = summarize(baseline, prior, current, [{"id": "a", "panel_id": "0", "region_id": "a"}])
        self.assertEqual(1, summary["review_warning_lost_by_prior_candidate"])
        self.assertEqual(1, summary["lost_warning_replaced_by_current_candidate"])
        self.assertEqual(0, summary["current_lost_region_warning"])
        self.assertEqual(21, summary["same_other_failure_reasons"])


if __name__ == "__main__":
    unittest.main()
