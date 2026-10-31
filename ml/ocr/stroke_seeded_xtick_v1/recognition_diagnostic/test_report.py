# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused consistency checks for the produced GPU-box/V5-recognition report."""

from __future__ import annotations

import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[4]
REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1/recognition_report_v3.json"


class RecognitionReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_frozen_model_and_consumed_probe_identity(self) -> None:
        self.assertEqual(self.report["recipe_id"], "stroke-seeded-xtick-v1-p2-v5-top-one-recognition-probe-v1")
        self.assertEqual(self.report["detector_checkpoint_sha256"],
                         "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351")
        self.assertEqual(self.report["recognizer_onnx_sha256"],
                         "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84")
        self.assertEqual(self.report["probe_manifest_sha256"],
                         "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba")
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["dev_sealed_private_inputs_read"])
        self.assertFalse(self.report["production_approval"])

    def test_detection_and_recognition_accounting(self) -> None:
        self.assertEqual(set(self.report["score_threshold_metrics"]), {"0.25", "0.5"})
        for metrics in self.report["score_threshold_metrics"].values():
            self.assertEqual(metrics["image_count"], 323)
            self.assertEqual(metrics["positive_count"], 165)
            self.assertEqual(metrics["background_only_count"], 158)
            self.assertEqual(metrics["true_positives"] + metrics["false_negatives"], 165)
            self.assertEqual(metrics["false_positives"], metrics["selected_detections"] - metrics["true_positives"])
            recognition = metrics["recognition_among_matched"]
            self.assertEqual(recognition["exact"] + recognition["wrong"] + recognition["review_or_withheld"],
                             recognition["matched_count"])
            self.assertEqual(recognition["runtime_errors"], 0)

    def test_crop_and_provider_contract(self) -> None:
        self.assertEqual(self.report["recognized_crop_count"], 165)
        self.assertEqual(self.report["recognizer_provider_counts"], {"CPUExecutionProvider": 11})
        crop = self.report["recognizer_crop_contract"]
        self.assertEqual(crop["cropper"], "GraphReader.Ocr.OcrCropBatcher")
        self.assertEqual(crop["coordinate_space"], "original_pixels")
        self.assertEqual(crop["input_canvas"], "128x32 grayscale")
        self.assertEqual(crop["resize"], "preserve-aspect-ratio-pad")


if __name__ == "__main__":
    unittest.main()
