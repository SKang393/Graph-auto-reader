# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import numpy as np

from .baseline import summarize


class ControlledBaselineTests(unittest.TestCase):
    def test_counts_fixed_threshold_decisions_and_changes(self):
        labels = np.array([1, 1, 0, 0], dtype=np.uint8)
        base = np.array([0.05, 0.2, 0.1, 0.05], dtype=np.float32)
        candidate = np.array([0.2, 0.05, 0.05, 0.2], dtype=np.float32)
        value = summarize(labels, base, candidate)
        self.assertEqual((1, 1, 1, 1), (value["v27_true_positive"], value["v27_false_positive"],
                                       value["v37_true_positive"], value["v37_false_positive"]))
        self.assertEqual((1, 1, 1, 1), (value["positive_gained"], value["positive_lost"],
                                       value["distractor_gained"], value["distractor_lost"]))

    def test_rejects_invalid_probabilities_and_labels(self):
        with self.assertRaisesRegex(ValueError, "baseline arrays differ"):
            summarize(np.array([0, 1]), np.array([0.2, np.nan]), np.array([0.2, 0.3]))
        with self.assertRaisesRegex(ValueError, "baseline arrays differ"):
            summarize(np.array([0, 2]), np.array([0.2, 0.3]), np.array([0.2, 0.3]))


if __name__ == "__main__":
    unittest.main()
