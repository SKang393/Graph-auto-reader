# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from ml.axis.residual_candidate_ranker_v1 import evaluate_dev


class DevelopmentEvaluationTests(unittest.TestCase):
    def test_summary_keeps_improved_equal_worsened_and_fallback_denominators(self):
        result = evaluate_dev.summarize([(0.2, 0.1), (0.1, 0.1), (0.1, 0.3)], 2, 2)
        self.assertEqual((3, 2, 1, 1, 1, 2), tuple(result[key] for key in
                         ("scored_panels", "changed_selection_panels", "improved", "equal", "worsened",
                          "unscored_fallback_panels")))
        self.assertAlmostEqual(0.4 / 3, result["native_mean_normalized_corner_error"])
        self.assertAlmostEqual(0.5 / 3, result["candidate_mean_normalized_corner_error"])

    def test_summary_rejects_missing_or_nonfinite_errors(self):
        for pairs in ([], [(float("nan"), 0.2)], [(0.1, -0.2)]):
            with self.subTest(pairs=pairs), self.assertRaises(ValueError):
                evaluate_dev.summarize(pairs, 0, 0)


if __name__ == "__main__":
    unittest.main()
