# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Input binding, grouped identity, coordinate decode, and NMS tests."""

import unittest

from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import train_gpu


class StrokeSeededTrainingTests(unittest.TestCase):
    def test_recipe_pins_current_runner_and_exact_manifest(self):
        config, _ = train_gpu.load_config()
        self.assertEqual(config["train_manifest_sha256"],
                         "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a")
        self.assertEqual(config["expected_optimizer_steps"], 2760)

    def test_v6_manifest_hashes_classes_and_source_groups(self):
        admitted = train_gpu.load_train_inputs({
            "train_manifest_path": train_gpu.MANIFEST_RELATIVE,
            "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        })
        self.assertEqual(admitted["kind_counts"], {"positive": 235, "background_only": 409, "excluded": 37})
        self.assertEqual(admitted["admitted_count"], 644)
        self.assertEqual(len(admitted["rows"]), len(admitted["pixels"]))
        self.assertEqual(len(admitted["group_counts"]), 24)
        self.assertEqual(sum(admitted["group_counts"].values()), 644)
        self.assertEqual(admitted["manifest_sha256"], "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a")
        self.assertTrue(all(row["source_identity_sha256"] for row in admitted["rows"]))

    def test_cell_relative_encode_decode_round_trip(self):
        expected = (0.1171875, 0.23958333333333334, 0.1953125, 0.4270833333333333)
        assignment = helpers.target_assignments((expected,))[0]
        row, column, encoded = assignment
        actual = helpers.decode_cell_relative(encoded, row, column)
        for left, right in zip(expected, actual):
            self.assertAlmostEqual(left, right, places=9)

    def test_iou_and_nms_metrics_count_unmatched_predictions_as_false_positives(self):
        truth = (0.1, 0.2, 0.3, 0.4)
        duplicate = (0.1, 0.2, 0.3, 0.4)
        other = (0.6, 0.6, 0.8, 0.8)
        self.assertEqual(train_gpu.box_iou(truth, duplicate), 1.0)
        kept = train_gpu.nms([(0.9, truth), (0.8, duplicate), (0.7, other)], 0.5)
        self.assertEqual(len(kept), 2)
        metrics = train_gpu.image_detection_metrics(
            [[(0.9, truth), (0.8, duplicate)], [(0.8, other)]],
            [truth, None], [0.25], nms_threshold=0.5, match_threshold=0.5)
        result = metrics["score_threshold_metrics"]["0.25"]
        self.assertEqual(result["true_positives"], 1)
        self.assertEqual(result["false_positives"], 1)
        self.assertEqual(result["false_negatives"], 0)
        self.assertEqual(result["false_positives_per_image"], 0.5)
        self.assertEqual(result["false_positives_per_background_image"], 1.0)
        self.assertEqual(result["top_confidence_box_recall"], 1.0)


if __name__ == "__main__":
    unittest.main()
