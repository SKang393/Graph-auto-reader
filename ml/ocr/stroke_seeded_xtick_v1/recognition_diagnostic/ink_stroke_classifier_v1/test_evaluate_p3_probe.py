# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused admission/quarantine and GPU runtime tests for P3 probe scoring."""

from __future__ import annotations

import unittest

import evaluate_p3_probe as evaluator


class P3ProbeEvaluationTests(unittest.TestCase):
    def test_train_hash_dedup_conflict_is_rejected(self):
        rows = [
            {"source_group_id": "train-a", "source_id": "train-a", "split": "train", "case_id": "a", "image_sha256": "1" * 64},
            {"source_group_id": "train-b", "source_id": "train-b", "split": "train", "case_id": "b", "image_sha256": "1" * 64},
        ]
        states = {"a": "unique_visible_tick", "b": "unique_visible_tick"}
        labels, groups, count = evaluator.semantic_train_hashes(rows, {"train-a", "train-b"},
            lambda row: {"matching_state": states[row["case_id"]]})
        self.assertEqual(labels, {"1" * 64: 1})
        self.assertEqual(groups, {"1" * 64: {"train-a", "train-b"}})
        self.assertEqual(count, 2)
        states["b"] = "no_ground_truth_tick_nearby"
        with self.assertRaises(evaluator.ProbeEvaluationError):
            evaluator.semantic_train_hashes(rows, {"train-a", "train-b"},
                lambda row: {"matching_state": states[row["case_id"]]})

    def test_train_and_holdout_crop_hashes_are_quarantined(self):
        rows = [
            {"proposal_id": "train", "crop_sha256": "a" * 64},
            {"proposal_id": "holdout", "crop_sha256": "b" * 64},
            {"proposal_id": "clean", "crop_sha256": "c" * 64},
        ]
        eligible, quarantined = evaluator.quarantine_rows(rows, {"a" * 64}, {"b" * 64})
        self.assertEqual([r["proposal_id"] for r in eligible], ["clean"])
        self.assertEqual({r["proposal_id"] for r in quarantined}, {"train", "holdout"})
        self.assertEqual(quarantined[0]["reasons"], ["exact_P3_training_crop_hash_overlap"])

    def test_rx_runtime_binding_rejects_cpu_or_fallback(self):
        evaluator._assert_runtime({"device": "cuda:0", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False})
        for runtime in (
            {"device": "cpu", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False},
            {"device": "cuda:0", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": True},
            {"device": "cuda:0", "device_name": "Other GPU", "cpu_fallback": False},
        ):
            with self.assertRaises(evaluator.ProbeEvaluationError):
                evaluator._assert_runtime(runtime)


if __name__ == "__main__":
    unittest.main()
