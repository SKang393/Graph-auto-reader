# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score_outlying_lane import WARNING
from score_workflow_y_review import summarize


class WorkflowYReviewScoreTests(unittest.TestCase):
    def test_wrong_numeric_and_flagged_nontext_stay_in_separate_denominators(self):
        old = {"rows": [{"panels": [{"panel_id": "p", "axis_needs_review": True,
                                    "calibration_reasons": [],
                                    "ocr_tick_regions": [
                                        {"region_id": "r1", "role": "YTick", "text": "30", "numeric_value": 30},
                                        {"region_id": "r2", "role": "YTick", "text": "2", "numeric_value": 2}]}]}]}
        new = {"rows": [{"panels": [{"panel_id": "p", "error": WARNING + "r2"}]}]}
        manifest = [{"id": "a", "panel_id": "p", "region_id": "r1"},
                    {"id": "b", "panel_id": "p", "region_id": "r2"}]
        labels = [{"id": "a", "classification": "numeric_tick", "expected_text": "20"},
                  {"id": "b", "classification": "other_text", "expected_text": None}]
        score = summarize(old, new, manifest, labels)
        self.assertEqual(score["numeric_tick"]["numeric_wrong"], 1)
        self.assertEqual(score["numeric_tick"]["wrong_without_previous_region_review"], 1)
        self.assertEqual(score["other_text"]["lane_warning"], 1)

    def test_missing_visual_label_is_rejected(self):
        with self.assertRaises(ValueError):
            summarize({"rows": []}, {"rows": []}, [{"id": "a"}], [])


if __name__ == "__main__":
    unittest.main()
