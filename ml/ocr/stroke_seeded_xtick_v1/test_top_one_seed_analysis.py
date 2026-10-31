# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Top-one-per-seed metrics and ambiguous-label review handling."""

import unittest

from ml.ocr.stroke_seeded_xtick_v1 import top_one_seed_analysis as analysis
from ml.ocr.stroke_seeded_xtick_v1 import train_gpu


class TopOneSeedAnalysisTests(unittest.TestCase):
    def test_analysis_config_pins_frozen_p2_and_consumed_probe(self):
        config, _ = analysis._load_config()
        self.assertEqual(config["checkpoint_sha256"], "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351")
        self.assertEqual(config["probe_evaluation_report_sha256"], "97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340")
        self.assertFalse(config["optimizer_execution_authorized"])

    def test_top_one_reduces_redundant_positive_crop_false_positives(self):
        truth = (0.1, 0.1, 0.3, 0.3)
        false_a = (0.55, 0.1, 0.75, 0.3)
        false_b = (0.1, 0.6, 0.3, 0.8)
        predictions = [[(0.95, truth), (0.8, false_a), (0.7, false_b)]]
        targets = [truth]
        nms = train_gpu.image_detection_metrics(predictions, targets, [0.25], 0.5, 0.5, 100)
        top_one = analysis.top_one_metrics(predictions, targets, [0.25], 0.5)
        baseline = nms["score_threshold_metrics"]["0.25"]
        reduced = top_one["score_threshold_metrics"]["0.25"]
        self.assertEqual((baseline["true_positives"], baseline["false_positives"]), (1, 2))
        self.assertEqual((reduced["true_positives"], reduced["false_positives"]), (1, 0))
        self.assertEqual(reduced["selected_detections"], 1)
        self.assertEqual(top_one["detections_per_seed_maximum"], 1)

    def test_background_false_detection_and_score_cutoff(self):
        other = (0.55, 0.1, 0.75, 0.3)
        metrics = analysis.top_one_metrics(
            [[(0.9, other)], [(0.2, other)]], [None, None], [0.25], 0.5)
        result = metrics["score_threshold_metrics"]["0.25"]
        self.assertEqual(result["true_positives"], 0)
        self.assertEqual(result["false_positives"], 1)
        self.assertEqual(result["false_negatives"], 0)
        self.assertEqual(result["false_positives_per_background_image"], 0.5)

    def test_multi_label_or_excluded_seed_is_review_not_background(self):
        self.assertEqual(analysis.seed_crop_disposition("positive", True, 2, "12", []), "needs-review")
        self.assertEqual(analysis.seed_crop_disposition("excluded", False, 2, "12", ["multiple_xtick_targets_in_crop"]), "needs-review")
        self.assertEqual(analysis.seed_crop_disposition("positive", True, 1, "12", []), "score")
        self.assertEqual(analysis.seed_crop_disposition("background_only", True, 0, None, []), "score")


if __name__ == "__main__":
    unittest.main()
