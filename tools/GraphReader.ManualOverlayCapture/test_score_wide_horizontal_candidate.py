# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import unittest

from score_wide_horizontal_candidate import summarize


def report():
    panels = []
    for i in range(21):
        panels.append({"panel_id": str(i), "failure_code": "WORKFLOW_RECALIBRATION_REQUIRED",
                       "status": "failed", "calibration_validity": "review", "error": "review",
                       "centers_before_classification": [], "markers": [],
                       "ocr_tick_regions": [], "calibration_reasons": []})
    panels[0]["ocr_tick_regions"] = [
        {"region_id": "a", "role": "YTick", "text": "8", "numeric_value": 8},
        {"region_id": "b", "role": "YTick", "text": "9", "numeric_value": 9},
        {"region_id": "c", "role": "YTick", "text": "1", "numeric_value": 1},
    ]
    return {"rows": [{"panels": panels}]}


class ScoreWideHorizontalCandidateTests(unittest.TestCase):
    def test_correction_regression_and_lost_region_are_all_counted(self):
        previous = report()
        current = copy.deepcopy(previous)
        regions = current["rows"][0]["panels"][0]["ocr_tick_regions"]
        regions[0].update(text="10", numeric_value=10)
        regions[1].update(text="8", numeric_value=8)
        regions[2]["role"] = "Other"
        manifest = [{"id": key, "panel_id": "0", "region_id": key} for key in ("a", "b", "c")]
        labels = [{"id": key, "classification": "numeric_tick", "expected_text": value}
                  for key, value in (("a", "10"), ("b", "9"), ("c", "1"))]
        summary = summarize(previous, previous, current, manifest, labels)
        ticks = summary["visual_categories"]["numeric_tick"]
        self.assertEqual((1, 1, 1), (ticks["corrected"], ticks["regressed"], ticks["missing_or_reclassified"]))
        self.assertEqual(1, summary["workflow"]["lost_y_regions"])
        self.assertEqual(21, summary["workflow"]["recalibration_required"])

    def test_duplicate_reviewed_region_fails(self):
        previous = report()
        current = copy.deepcopy(previous)
        regions = current["rows"][0]["panels"][0]["ocr_tick_regions"]
        regions.append(dict(regions[0]))
        with self.assertRaisesRegex(ValueError, "duplicate reviewed region"):
            summarize(previous, previous, current, [{"id": "a", "panel_id": "0", "region_id": "a"}],
                      [{"id": "a", "classification": "numeric_tick", "expected_text": "10"}])


if __name__ == "__main__":
    unittest.main()
