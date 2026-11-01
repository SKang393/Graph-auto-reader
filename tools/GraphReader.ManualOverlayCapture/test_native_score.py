# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import numpy as np

from native_score import maximum_matches, score_image
from test_score import panel


class NativePointScoreTests(unittest.TestCase):
    def test_one_prediction_cannot_match_two_points(self):
        self.assertEqual(1, maximum_matches([(10, 10), (11, 10)], [(10.5, 10)]))

    def test_matching_finds_non_greedy_assignment(self):
        self.assertEqual(2, maximum_matches([(0, 0), (4, 0)], [(2, 0), (-4, 0)]))

    def test_modified_patch_and_exact_duplicates_are_excluded(self):
        result = score_image([{"x": 10, "y": 10}, {"x": 10, "y": 10}, {"x": 50, "y": 50}],
                             [panel(0, 0, 100, 100, [(10, 10), (50, 50)])], [40, 40, 60, 60])
        self.assertEqual(1, result["exact_duplicate_points"])
        self.assertEqual(1, result["patch_excluded"])
        self.assertEqual(1, result["eligible_points"])
        self.assertEqual(1, result["matched_points"])

    def test_mask_overlap_is_reported_for_matched_and_missed_points(self):
        captured = panel(0, 0, 100, 100, [(10, 10)])
        captured["frame_file"] = "synthetic.bin.gz"
        frame = np.zeros((3, 100, 100), dtype=np.float32)
        frame[1, 10, 10] = 1
        frame[2, 20, 20] = 1
        result = score_image([{"x": 10, "y": 10}, {"x": 20, "y": 20}], [captured], [40, 40, 60, 60],
                             {"synthetic.bin.gz": frame})
        self.assertEqual(1, result["matched_ocr_overlap"])
        self.assertEqual(1, result["missed_artifact_overlap"])
        self.assertEqual(0, result["missed_ocr_overlap"])


if __name__ == "__main__":
    unittest.main()
