# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Non-inference checks for paired top-one metric accounting."""

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import evaluate_paired_probe as paired


class TopOneMetricTests(unittest.TestCase):
    def test_score_gate_matching_false_positives_and_duplicate_rows(self):
        box = (0.1, 0.1, 0.3, 0.3)
        wrong = (0.7, 0.7, 0.9, 0.9)
        predictions = [
            [(0.9, box), (0.8, wrong)],    # top one matches; second candidate ignored
            [(0.9, wrong)],                # positive false detection is FP plus FN
            [(0.2, box)],                  # below both score gates, so FN only
            [(0.7, box)],                  # background false positive
            [(0.2, box)],                  # background below threshold
            [(0.9, box)],                  # exact duplicate row is counted again
        ]
        targets = [box, box, box, None, None, box]
        metrics = paired.top_one_metrics(predictions, targets, (0.25, 0.5), 0.5)
        for threshold in ("0.25", "0.5"):
            self.assertEqual(metrics[threshold]["true_positives"], 2)
            self.assertEqual(metrics[threshold]["false_positives"], 2)
            self.assertEqual(metrics[threshold]["false_negatives"], 2)
            self.assertEqual(metrics[threshold]["background_false_positives"], 1)
            self.assertEqual(metrics[threshold]["precision"], 0.5)
            self.assertEqual(metrics[threshold]["recall"], 0.5)
            self.assertEqual(metrics[threshold]["false_positives_per_background_image"], 0.5)

    def test_mismatched_row_counts_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "row counts differ"):
            paired.top_one_metrics([], [None], (0.25,), 0.5)


if __name__ == "__main__":
    unittest.main()
