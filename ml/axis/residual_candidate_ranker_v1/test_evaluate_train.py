# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from ml.axis.residual_candidate_ranker_v1 import evaluate_train, runner


def checkpoint():
    return {"residual_scale": runner.RESIDUAL_SCALE, "state_dict": {
        "feature_scale": list(runner.SCALE), "hidden.weight": [[0.0] * 17 for _ in range(32)],
        "hidden.bias": [0.0] * 32, "output.weight": [[0.0] * 32], "output.bias": [0.0],
    }}


class EvaluationTests(unittest.TestCase):
    def test_zero_residual_selects_native_and_counts_ties(self):
        rows = [{"rank": 1, "features": [0.8] + [0.0] * 16, "target_error": 0.1},
                {"rank": 2, "features": [0.8] + [0.0] * 16, "target_error": 0.2}]
        result = evaluate_train.evaluate([{"rows": rows}], checkpoint())
        self.assertEqual((0, 0, 1, 0), tuple(result[k] for k in
                         ("changed_selection_panels", "improved", "equal", "worsened")))
        self.assertAlmostEqual(0.1, result["candidate_mean_normalized_corner_error"])

    def test_nonzero_residual_changes_ranking_and_correctly_counts_improvement(self):
        model = checkpoint()
        model["state_dict"]["hidden.weight"][0][1] = 1.0
        model["state_dict"]["output.weight"][0][0] = 1.0
        rows = [{"rank": 1, "features": [0.8, 0.0] + [0.0] * 15, "target_error": 0.2},
                {"rank": 2, "features": [0.79, 1.0] + [0.0] * 15, "target_error": 0.1}]
        result = evaluate_train.evaluate([{"rows": rows}], model)
        self.assertEqual((1, 1, 0, 0), tuple(result[k] for k in
                         ("changed_selection_panels", "improved", "equal", "worsened")))

    def test_rejects_wrong_dimensions_and_nonfinite_features(self):
        with self.assertRaises(ValueError):
            evaluate_train.score([[1.0] * 16], checkpoint())
        with self.assertRaises(ValueError):
            evaluate_train.score([[float("nan")] + [0.0] * 16], checkpoint())


if __name__ == "__main__":
    unittest.main()
