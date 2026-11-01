# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from dense_tick_score import prediction_map, score_rows


class DenseTickScoreTests(unittest.TestCase):
    def test_counts_exact_wrong_empty_and_excludes_partial_from_visible(self):
        truth = [
            {"id": "a", "text": "12", "crop_partially_clipped": False},
            {"id": "b", "text": "13", "crop_partially_clipped": True},
            {"id": "c", "text": "14", "crop_partially_clipped": False},
        ]
        self.assertEqual(
            {"labels": 3, "exact": 1, "wrong": 1, "empty": 1,
             "fully_visible_labels": 2, "fully_visible_exact": 1},
            score_rows(truth, {"a": "12", "b": "18", "c": ""}),
        )

    def test_missing_or_duplicate_id_is_rejected(self):
        row = {"id": "a", "text": "1", "crop_partially_clipped": False}
        with self.assertRaises(ValueError):
            score_rows([row], {})
        with self.assertRaises(ValueError):
            score_rows([row, row], {"a": "1"})
        with self.assertRaises(ValueError):
            prediction_map([{"id": "a", "r": {"Text": "1"}}] * 2, "r", "Text")

    def test_invalid_prediction_type_is_rejected(self):
        with self.assertRaises(ValueError):
            prediction_map([{"id": "a", "r": {"Text": 1}}], "r", "Text")


if __name__ == "__main__":
    unittest.main()
