# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import unittest

from score_workflow_tick_details import summarize


def fixture():
    panels = []
    for number in range(21):
        panels.append({"panel_id": str(number), "status": "failed", "error": "review",
                       "failure_code": "WORKFLOW_RECALIBRATION_REQUIRED", "calibration_validity": "NeedsReview",
                       "centers_before_classification": [], "markers": [], "axis_needs_review": True,
                       "ocr_tick_regions": [], "calibration_reasons": [], "y_axis_line": None})
    return {"rows": [{"panels": panels}]}


class WorkflowTickDetailScoreTests(unittest.TestCase):
    def test_counts_numeric_regions_and_retains_outliers(self):
        before = fixture()
        after = copy.deepcopy(before)
        panel = after["rows"][0]["panels"][0]
        panel["y_axis_line"] = {"start": {"x": 100, "y": 0}, "end": {"x": 100, "y": 100}}
        panel["ocr_tick_regions"] = [{"region_id": name, "role": "YTick", "numeric_value": value,
                                      "literal_numeric": True, "bounds": {"top": 10, "height": 10, "right": right}}
                                     for name, value, right in (("a", 1, 90), ("b", 2, 90), ("c", 3, 50))]
        panel["calibration_reasons"] = ["ocr_tick_sequence_needs_review:YTick:competing_numeric_readings:a"]
        score = summarize(before, after)
        self.assertEqual(score["same_status_and_error"], 21)
        self.assertEqual(score["numeric_y_tick_regions"], 3)
        self.assertEqual(score["numeric_y_ticks_with_competing_readings"], 1)
        self.assertEqual(score["relative_gap_outlier_regions"], 1)

    def test_missing_panel_is_rejected(self):
        before, after = fixture(), fixture()
        after["rows"][0]["panels"].pop()
        with self.assertRaises(ValueError):
            summarize(before, after)


if __name__ == "__main__":
    unittest.main()
