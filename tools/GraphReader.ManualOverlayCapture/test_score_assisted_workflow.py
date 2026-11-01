# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from score_assisted_workflow import score_points


class AssistedWorkflowScoreTests(unittest.TestCase):
    def test_workflow_points_keep_full_truth_denominator(self):
        panel = {"provenance": {"encoded_crop_source_pixels": {"x": 10, "y": 20, "width": 100, "height": 100},
                                "panel_to_source_matrix": [1, 0, 10, 0, 1, 20, 0, 0, 1]},
                 "workflow_points": [{"x": 5, "y": 5}]}
        score = score_points([{"x": 15, "y": 25}, {"x": 300, "y": 300}], [panel], "workflow_points")
        self.assertEqual(2, score["raw_points"])
        self.assertEqual(1, score["unavailable_points"])
        self.assertEqual(1, score["matched_points"])
        self.assertEqual(0, score_points([{"x": 15, "y": 25}], [], "workflow_points")["matched_points"])


if __name__ == "__main__":
    unittest.main()
