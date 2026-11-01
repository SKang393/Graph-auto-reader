# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.manual_y_axis_candidate_v1.candidate import group_components, iou


class CandidateTests(unittest.TestCase):
    def test_groups_neighboring_digits_without_merging_separate_rows(self):
        boxes = [(10, 10, 14, 20), (16, 10, 21, 20), (10, 30, 15, 40)]
        self.assertEqual(group_components(boxes), [(10, 10, 21, 20), (10, 30, 15, 40)])

    def test_iou_uses_half_open_pixel_boxes(self):
        self.assertEqual(iou([0, 0, 10, 10], [0, 0, 10, 10]), 1)
        self.assertEqual(iou([0, 0, 10, 10], [10, 0, 20, 10]), 0)


if __name__ == "__main__":
    unittest.main()
