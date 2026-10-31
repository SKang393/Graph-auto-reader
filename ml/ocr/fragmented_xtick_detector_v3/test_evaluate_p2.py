# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Pure geometry and image-level metric tests for P2's train-fit evaluator."""

from __future__ import annotations

import unittest

from ml.ocr.fragmented_xtick_detector_v3.evaluate_p2 import box_iou, decode_prediction_box, summarize_image_metrics


class P2ImageEvaluationTests(unittest.TestCase):
    def test_cell_relative_box_decodes_to_expected_image_coordinates(self) -> None:
        box = decode_prediction_box((0.25, 0.75, 0.2, 0.4), row=2, column=3)
        self.assertAlmostEqual(3.25 / 32 - 0.1, box[0])
        self.assertAlmostEqual(2.75 / 12 - 0.2, box[1])
        self.assertAlmostEqual(3.25 / 32 + 0.1, box[2])
        self.assertAlmostEqual(2.75 / 12 + 0.2, box[3])

    def test_iou_and_thresholded_nms_false_positives_include_negative_images(self) -> None:
        target = (0.1, 0.1, 0.3, 0.3)
        wrong = (0.6, 0.6, 0.8, 0.8)
        images = [
            {"target": target, "target_cell": {"score": 0.9, "box": target}, "candidates": [(0.9, target), (0.8, target), (0.7, wrong)]},
            {"target": None, "target_cell": None, "candidates": [(0.8, wrong), (0.7, wrong)]},
        ]
        self.assertEqual(1.0, box_iou(target, target))
        metrics = summarize_image_metrics(images, thresholds=(0.5, 0.9), nms_iou_threshold=0.5, match_iou_threshold=0.5)
        self.assertEqual(1.0, metrics["ground_truth_assigned_cell"]["mean_box_iou"])
        at_half = metrics["thresholds"]["0.5"]
        self.assertEqual(1.0, at_half["positive_images"]["image_recall"])
        self.assertEqual(1.0, at_half["positive_images"]["false_positives_per_image"])
        self.assertEqual(1.0, at_half["background_only_images"]["false_positives_per_image"])
        self.assertEqual(2.0, at_half["positive_images"]["nms_detections_per_image"])
        at_nine = metrics["thresholds"]["0.9"]
        self.assertEqual(0.0, at_nine["positive_images"]["false_positives_per_image"])
        self.assertEqual(0.0, at_nine["background_only_images"]["false_positives_per_image"])

    def test_evaluation_requires_both_positive_and_negative_examples(self) -> None:
        with self.assertRaises(ValueError):
            summarize_image_metrics([{"target": None, "target_cell": None, "candidates": []}])

    def test_even_positive_count_uses_average_of_middle_iou_values(self) -> None:
        target = (0.1, 0.1, 0.3, 0.3)
        images = [
            {"target": target, "target_cell": {"score": 0.9, "box": target}, "candidates": [(0.9, target)]},
            {"target": target, "target_cell": {"score": 0.9, "box": (0.6, 0.6, 0.8, 0.8)}, "candidates": [(0.9, (0.6, 0.6, 0.8, 0.8))]},
            {"target": None, "target_cell": None, "candidates": []},
        ]
        metrics = summarize_image_metrics(images, thresholds=(0.5,))
        self.assertEqual(0.5, metrics["highest_confidence_iou"]["median"])


if __name__ == "__main__":
    unittest.main()
