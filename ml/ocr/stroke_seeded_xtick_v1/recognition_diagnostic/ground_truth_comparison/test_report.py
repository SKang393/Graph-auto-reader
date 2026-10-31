# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Checks paired ground-truth/predicted V5 outcome accounting."""

from __future__ import annotations

import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[5]
REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2/report.json"


class GroundTruthComparisonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_frozen_inputs_and_scope(self) -> None:
        self.assertEqual(self.report["detector_checkpoint_sha256"],
                         "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351")
        self.assertEqual(self.report["recognizer_onnx_sha256"],
                         "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84")
        self.assertEqual(self.report["probe_manifest_sha256"],
                         "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba")
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["dev_sealed_private_inputs_read"])
        self.assertFalse(self.report["production_approval"])

    def test_empty_failure_categories_are_exclusive_and_accounted(self) -> None:
        for threshold in self.report["per_score_threshold"].values():
            for variant in ("ground_truth_boxes", "predicted_boxes",
                            "predicted_boxes_iou_at_least_0_5", "predicted_boxes_iou_below_0_5"):
                metrics = threshold[variant]
                self.assertEqual(metrics["sample_count"], metrics["accounted"])
                self.assertEqual(metrics["explicit_failures_by_severity_and_code"], {})
                self.assertEqual(metrics["exact"] + metrics["wrong"] +
                                 metrics["empty_without_alternative"], metrics["sample_count"])

    def test_ground_truth_boxes_retain_many_post_inference_empties(self) -> None:
        primary = self.report["per_score_threshold"]["0.25"]
        self.assertEqual(primary["selected_positive_rows"], 165)
        self.assertEqual(primary["ground_truth_boxes"]["exact"], 32)
        self.assertEqual(primary["ground_truth_boxes"]["wrong"], 32)
        self.assertEqual(primary["ground_truth_boxes"]["empty_without_alternative"], 101)
        self.assertEqual(primary["predicted_boxes_iou_at_least_0_5"]["sample_count"], 133)
        self.assertEqual(primary["predicted_boxes_iou_at_least_0_5"]["empty_without_alternative"], 83)

    def test_production_cropper_provider_and_coordinates(self) -> None:
        self.assertEqual(self.report["recognizer_provider"], "CPUExecutionProvider")
        self.assertEqual(self.report["recognizer_provider_batches"], 21)
        self.assertEqual(self.report["crop_contract"]["cropper"], "GraphReader.Ocr.OcrCropBatcher")
        self.assertEqual(self.report["crop_contract"]["coordinate_space"], "original_pixels")
        self.assertEqual(self.report["crop_contract"]["width"], 128)
        self.assertEqual(self.report["crop_contract"]["height"], 32)


if __name__ == "__main__":
    unittest.main()
