# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score_dev_ocr import competing_numeric, summarize


def reading(primary, alternatives):
    return {"text": primary, "confidence": 0.9, "failure": None,
            "alternatives": [{"text": text, "value": value, "numeric": value is not None, "confidence": confidence}
                             for text, value, confidence in alternatives]}


class DevelopmentOcrScoreTests(unittest.TestCase):
    def test_score_preserves_nontext_and_compares_wrong_and_correct_primaries(self):
        labels = [{"id": "a", "category": "numeric_text", "expected_text": "1"},
                  {"id": "b", "category": "numeric_text", "expected_text": "2"},
                  {"id": "c", "category": "other_text", "expected_text": None}]
        predictions = [{"id": "a", "server": reading("1", [("1", 1, .9), ("7", 7, .0001)])},
                       {"id": "b", "server": reading("3", [("3", 3, .9), ("2", 2, .002)])},
                       {"id": "c", "server": reading("1", [("1", 1, .9)])}]
        result = summarize(labels, predictions)
        self.assertEqual((result["inventory"], result["transcribed"], result["exact"]), (3, 2, 1))
        self.assertEqual(result["unscored_numeric_primaries"], {"other_text": 1})
        self.assertEqual(result["competing_numeric_by_cutoff"]["0.0"]["wrong_with_competing_numeric"], 1)
        self.assertNotIn("wrong_with_competing_numeric", result["competing_numeric_by_cutoff"]["0.01"])

    def test_invalid_confidence_and_duplicate_inventory_are_rejected(self):
        result = reading("1", [("1", 1, .9), ("2", 2, 1.1), ("3", 3, float("nan"))])
        self.assertFalse(competing_numeric(result, 0))
        labels = [{"id": "a", "category": "numeric_text", "expected_text": "1"}] * 2
        with self.assertRaises(ValueError):
            summarize(labels, [{"id": "a", "server": result}] * 2)


if __name__ == "__main__":
    unittest.main()
