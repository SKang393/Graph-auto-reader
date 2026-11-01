# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import score_tick_rank_train_probe as score


def group(panel, before, after):
    return {"source_sha256": "source", "panel_sha256": panel, "rows": [
        {"rank": 1, "candidate_identity_sha256": panel + "-a", "target_error": before},
        {"rank": 2, "candidate_identity_sha256": panel + "-b", "target_error": after},
    ]}


def prediction(panel, rank):
    return {"source_sha256": "source", "panel_sha256": panel, "status": "completed",
            "selected_rank": rank, "baseline_identity_sha256": panel + "-a",
            "selected_identity_sha256": panel + ("-a" if rank == 0 else "-b")}


class TickRankTrainScoreTests(unittest.TestCase):
    def test_scores_selected_rank_and_preserves_missing_and_unlabelled_panels(self):
        result = score.summarize(
            [prediction("a", 1), prediction("b", 0), prediction("extra", 0)],
            [group("a", 0.2, 0.1), group("b", 0.3, 0.4), group("missing", 0.2, 0.1)])
        self.assertEqual(2, result["counts"]["scored_panels"])
        self.assertEqual(1, result["counts"]["unscored_labelable_panels"])
        self.assertEqual(1, result["counts"]["unlabelled_or_fallback_panels"])
        self.assertEqual(1, result["counts"]["improved"])
        self.assertAlmostEqual(0.25, result["mean_baseline_normalized_corner_error"])
        self.assertAlmostEqual(0.2, result["mean_selected_normalized_corner_error"])

    def test_rejects_candidate_identity_mismatch(self):
        item = prediction("a", 1)
        item["selected_identity_sha256"] = "wrong"
        with self.assertRaises(ValueError):
            score.summarize([item], [group("a", 0.2, 0.1)])


if __name__ == "__main__":
    unittest.main()
