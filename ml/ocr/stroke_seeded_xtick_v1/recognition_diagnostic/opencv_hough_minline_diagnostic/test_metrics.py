# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from run_hough_min5 import summarize_variant


class HoughMinimumLengthMetricsTests(unittest.TestCase):
    def test_precision_recall_false_detections_and_uncertainty(self):
        panel = {
            "source_id": "source-a",
            "truth_count": 2,
            "default_hough12": {"status": "ok", "needs_review": True,
                                 "uncertainty_reasons": ["axis_pair_ambiguous"],
                                 "x_axis_ticks": [{"tick_id": "one-a"}, {"tick_id": "one-b"}, {"tick_id": "one-c"}]},
            "default_metrics": {"truth_count": 2, "usable_detected_x_stroke_count": 3,
                                "matched_count": 1, "unmatched_truth_count": 1,
                                "unmatched_detected_count": 2,
                                "truths_with_multiple_detections_within_tolerance": 0,
                                "matched_pairs": []},
        }
        result = summarize_variant([panel], "default_hough12")
        self.assertEqual(result["usable_detected_count"], 3)
        self.assertEqual(result["matched_count"], 1)
        self.assertEqual(result["false_detection_count"], 2)
        self.assertAlmostEqual(result["precision"], 1 / 3)
        self.assertEqual(result["recall"], 0.5)
        self.assertEqual(result["uncertainty_reason_counts"], {"axis_pair_ambiguous": 1})


if __name__ == "__main__":
    unittest.main()
