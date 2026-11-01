# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

import numpy as np

from .evaluate_holdout import compare


class HoldoutComparisonTests(unittest.TestCase):
    def test_counts_threshold_and_changes(self):
        labels = np.array([1, 1, 0, 0])
        result = compare(labels, np.array([0.2, 0.0, 0.1, 0.0]), np.array([0.0, 0.1, 0.0, 0.2]))
        self.assertEqual({"true_positive": 1, "false_positive": 1, "false_negative": 1, "true_negative": 1}, result["v37"])
        self.assertEqual(result["v37"], result["candidate"])
        self.assertEqual((1, 1, 1, 1), tuple(result[key] for key in
                         ("positive_gained", "positive_lost", "distractor_gained", "distractor_lost")))

    def test_rejects_invalid_probabilities(self):
        with self.assertRaises(ValueError):
            compare(np.array([1]), np.array([0.2]), np.array([float("nan")]))


if __name__ == "__main__":
    unittest.main()
