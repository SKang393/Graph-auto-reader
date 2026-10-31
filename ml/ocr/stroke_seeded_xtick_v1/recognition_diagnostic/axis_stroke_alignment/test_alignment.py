# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from run_axis_alignment import endpoint_error, match_panel, rasterize_segment, transformed_panel_bounds


class AxisStrokeAlignmentTests(unittest.TestCase):
    def test_unique_assignment_reports_multiple_and_unmatched_strokes(self):
        truth = [
            {"case_id": "a", "stroke_center_original_xy": [10.0, 20.0],
             "stroke_line_original_xyxy": [10.0, 16.0, 10.0, 24.0], "kind": "positive"},
            {"case_id": "b", "stroke_center_original_xy": [50.0, 20.0],
             "stroke_line_original_xyxy": [50.0, 16.0, 50.0, 24.0], "kind": "background_only"},
        ]
        detections = [
            {"center": [10.5, 20.0], "line": [10.5, 16.0, 10.5, 24.0]},
            {"center": [12.0, 20.0], "line": [12.0, 16.0, 12.0, 24.0]},
        ]
        result = match_panel(truth, detections)
        self.assertEqual(result["matched_count"], 1)
        self.assertEqual(result["unmatched_truth_count"], 1)
        self.assertEqual(result["unmatched_detected_count"], 1)
        self.assertEqual(result["truths_with_multiple_detections_within_tolerance"], 1)
        self.assertAlmostEqual(result["matched_pairs"][0]["center_error_px"], 0.5)

    def test_line_error_is_endpoint_order_invariant(self):
        self.assertEqual(endpoint_error([3.0, 0.0, 3.0, 10.0], [3.0, 10.0, 3.0, 0.0]), 0.0)

    def test_crop_bounds_use_xywh_and_clip_to_canvas(self):
        self.assertEqual(transformed_panel_bounds({"box": [-2.1, 3.0, 12.2, 8.1]}, (10, 10)),
                         [0, 3, 10, 10])

    def test_segment_rasterization_is_deterministic_and_inclusive(self):
        self.assertEqual(rasterize_segment([0, 0, 3, 3]), [(0, 0), (1, 1), (2, 2), (3, 3)])
        self.assertEqual(rasterize_segment([2, 2, 2, 2]), [(2, 2)])


if __name__ == "__main__":
    unittest.main()
