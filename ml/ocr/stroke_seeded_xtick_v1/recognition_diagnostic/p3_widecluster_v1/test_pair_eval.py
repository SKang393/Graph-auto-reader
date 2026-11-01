# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Admission and paired-metric checks for the P3 wide-cluster probe wrapper."""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pair_eval


class FakeEvaluator:
    @staticmethod
    def quarantine_rows(rows, training_hashes, holdout_hashes):
        eligible, quarantined = [], []
        for row in rows:
            reasons = []
            if row["crop_sha256"] in training_hashes:
                reasons.append("train")
            if row["crop_sha256"] in holdout_hashes:
                reasons.append("holdout")
            if reasons:
                quarantined.append({"proposal_id": row["proposal_id"], "reasons": reasons})
            else:
                eligible.append(row)
        return eligible, quarantined


class PairEvaluationTests(unittest.TestCase):
    def test_config_matches_predeclared_recipe(self) -> None:
        path = pair_eval.ROOT / pair_eval.CONFIG_REL
        self.assertEqual(path.read_bytes() and json.loads(path.read_text(encoding="utf-8")),
            pair_eval.expected_config())
        self.assertEqual(pair_eval.THRESHOLD, 0.5)
        self.assertEqual(pair_eval.WIDE_CLUSTER_LIMIT, 1_000_000)

    def test_quarantine_preserves_every_duplicate_row_and_records_both_reasons(self) -> None:
        rows = [
            {"proposal_id": "a", "crop_sha256": "train"},
            {"proposal_id": "b", "crop_sha256": "train"},
            {"proposal_id": "c", "crop_sha256": "holdout"},
            {"proposal_id": "d", "crop_sha256": "both"},
            {"proposal_id": "e", "crop_sha256": "clean"},
        ]
        eligible, quarantined = pair_eval.split_rows(
            FakeEvaluator, rows, {"train", "both"}, {"holdout", "both"})
        self.assertEqual([r["proposal_id"] for r in eligible], ["e"])
        self.assertEqual([r["proposal_id"] for r in quarantined], ["a", "b", "c", "d"])
        self.assertEqual(quarantined[-1]["reasons"], ["train", "holdout"])
        self.assertEqual(len(eligible) + len(quarantined), len(rows))

    def test_paired_metrics_keep_same_visible_truth_denominator_and_count_empty_panels(self) -> None:
        panels = [
            {"visible_truth_count": 2, "needs_review": True,
             "pred": {"usable_detected_x_stroke_count": 2, "matched_count": 1,
                      "unmatched_detected_count": 1}},
            {"visible_truth_count": 0, "needs_review": False,
             "pred": {"usable_detected_x_stroke_count": 1, "matched_count": 0,
                      "unmatched_detected_count": 1}},
        ]
        metrics = pair_eval.panel_metrics(panels, "pred")
        self.assertEqual(metrics["visible_truth_ticks"], 2)
        self.assertEqual(metrics["true_matches"], 1)
        self.assertEqual(metrics["unmatched_proposals"], 2)
        self.assertEqual(metrics["false_proposals_on_no_visible_panels"], 1)
        self.assertEqual(metrics["no_visible_panels_with_proposals"], 1)


if __name__ == "__main__":
    unittest.main()
