# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused pure-metric checks for the paired scanner-proposal evaluator."""

from __future__ import annotations

import unittest

import numpy as np

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_p2_filter_v1 import run_ink_p2_filter as filtering
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1.paired_ink_scan_eval_v1 import evaluate_paired_ink as evaluator
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1.paired_ink_scan_eval_v1 import correct_batch_counter as correction


class PairedInkEvalTests(unittest.TestCase):
    def test_fixed_score_threshold_semantics(self) -> None:
        self.assertFalse(filtering.filter_proposal(None, 0.25))
        self.assertFalse(filtering.filter_proposal(0.249, 0.25))
        self.assertTrue(filtering.filter_proposal(0.25, 0.25))
        self.assertFalse(filtering.filter_proposal(0.499, 0.5))
        self.assertTrue(filtering.filter_proposal(0.5, 0.5))

    def test_box_metric_uses_matched_proposal_in_original_frame(self) -> None:
        row = {"p2_score": 0.75, "p2_box_original": [10.0, 20.0, 30.0, 40.0]}
        match = {"matched_pairs": [{"case_id": "source-p0-t0", "detection_index": 0}]}
        panel = {"panel_id": "panel-1", "p2_filtered_scan_025": match,
                 "p2_filtered_scan_025_survivors": [row]}
        metric = evaluator._box_metric_for([panel], [row], "p2", 0.25,
            {("panel-1", "tick-1"): (10.0, 20.0, 30.0, 40.0)},
            {"source-p0-t0": "tick-1"})
        self.assertEqual(metric, {"accepted_proposals": 1,
            "rejected_by_score_or_missing_box": 0,
            "matched_tick_pairs_with_label_box": 1,
            "matched_tick_top_box_mean_iou_to_glyph": 1.0,
            "matched_tick_top_box_iou_ge_0_5": 1})

    def test_missing_box_is_not_accepted_even_with_score(self) -> None:
        self.assertFalse(filtering.filter_proposal(None, 0.5))

    def test_report_panels_drop_numpy_crop_scratch(self) -> None:
        panels = [{"p2_filtered_scan_025_survivors": [{"pixels": np.zeros((96, 256), dtype=np.float32)}],
                   "mixed_filtered_scan_05_survivors": []}]
        evaluator._drop_survivor_scratch(panels)
        self.assertEqual(panels, [{}])

        import json
        self.assertEqual(json.dumps(panels), "[{}]")

    def test_total_model_calls_correct_to_batches_per_model(self) -> None:
        result = correction.batch_accounting(total_model_batch_calls=24,
            row_count=365, batch_size=32, model_count=2)
        self.assertEqual(result, {"batches_per_model": 12,
            "model_count": 2, "total_model_batch_calls": 24})
        with self.assertRaises(ValueError):
            correction.batch_accounting(total_model_batch_calls=23,
                row_count=365, batch_size=32, model_count=2)


if __name__ == "__main__":
    unittest.main()
