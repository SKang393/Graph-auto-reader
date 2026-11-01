# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from reviewed_axis_score import score_rows


def alternative(text, confidence, numeric, value=None):
    return {"text": text, "confidence": confidence, "numeric": numeric, "value": value}


def prediction(identifier, text, alternatives, failure=None):
    return {"id": identifier, "server": {"text": text, "alternatives": alternatives, "failure": failure}}


class ReviewedAxisScoreTests(unittest.TestCase):
    def test_competing_numeric_evidence_and_nonnumeric_readings_are_separate(self):
        truth = [{"id": "a", "expected": "1"}, {"id": "b", "expected": "2"},
                 {"id": "c", "expected": "64"}, {"id": "d", "expected": "4"}]
        rows = [prediction("a", "1", [alternative("1", .9, True, 1), alternative("7", .01, True, 7)]),
                prediction("b", "3", [alternative("3", .9, True, 3), alternative("2", .5, True, 2)]),
                prediction("c", "B4", [alternative("B4", .9, False), alternative("84", .1, True, 84)]),
                prediction("d", None, [], "failed")]
        result = score_rows(truth, rows)
        self.assertEqual({"labels": 4, "exact": 1, "wrong": 2, "empty": 1,
                          "numeric_primary": 2, "non_numeric_primary": 1, "recognition_failures": 1}, result["counts"])
        self.assertEqual({"correct_flagged": 1, "correct_unflagged": 0, "wrong_flagged": 1, "wrong_unflagged": 0},
                         result["competing_numeric_confidence_thresholds"]["0"])
        self.assertEqual({"correct_flagged": 0, "correct_unflagged": 1, "wrong_flagged": 1, "wrong_unflagged": 0},
                         result["competing_numeric_confidence_thresholds"]["0.05"])

    def test_missing_or_duplicate_predictions_cannot_shrink_the_denominator(self):
        truth = [{"id": "a", "expected": "1"}]
        row = prediction("a", "1", [alternative("1", 1, True, 1)])
        with self.assertRaises(ValueError):
            score_rows(truth, [])
        with self.assertRaises(ValueError):
            score_rows(truth, [row, row])


if __name__ == "__main__":
    unittest.main()
