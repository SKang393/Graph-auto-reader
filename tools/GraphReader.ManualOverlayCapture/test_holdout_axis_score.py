# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from holdout_axis_score import iou, match_labels


class HoldoutAxisScoreTests(unittest.TestCase):
    def test_iou_and_one_to_one_matching_keep_missed_labels(self):
        self.assertEqual(1, iou([0, 0, 10, 10], [0, 0, 10, 10]))
        self.assertEqual(0, iou([0, 0, 10, 10], [20, 20, 30, 30]))
        labels = [{"sample_id": "a", "box_xyxy": [0, 0, 10, 10], "label": "1"},
                  {"sample_id": "a", "box_xyxy": [20, 0, 30, 10], "label": "2"}]
        joins = [{"id": "x", "sample_id": "a", "box": [0, 0, 10, 10]},
                 {"id": "y", "sample_id": "a", "box": [0, 0, 10, 10]}]
        matched, unmatched, missed = match_labels(labels, joins)
        self.assertEqual([{"id": "x", "expected": "1"}], matched)
        self.assertEqual(["y"], unmatched)
        self.assertEqual(1, missed)

    def test_duplicate_proposal_identity_is_rejected(self):
        row = {"id": "x", "sample_id": "a", "box": [0, 0, 10, 10]}
        with self.assertRaises(ValueError):
            match_labels([], [row, row])


if __name__ == "__main__":
    unittest.main()
