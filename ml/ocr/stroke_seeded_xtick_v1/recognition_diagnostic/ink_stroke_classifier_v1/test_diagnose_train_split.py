# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Small safety and arithmetic checks for train-only frozen-model diagnosis."""

from __future__ import annotations

import unittest

import numpy as np

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import diagnose_train_split as diagnostic


class _Device:
    def __str__(self) -> str:
        return "cuda:0"

    def __eq__(self, other: object) -> bool:
        return isinstance(other, _Device)


class _Parameter:
    device = _Device()


class _Model:
    @staticmethod
    def parameters():
        return [_Parameter()]


def annotation_for_line(line: list[float]) -> dict:
    return {"proposal_line_original_xyxy": line,
        "crop_transform": {"original_to_crop_pixel": [2, 0, 0, 0, 2, 0],
            "content_xywh": [0, 0, 256, 96]}}


class TrainSplitDiagnosticTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.admitted = diagnostic.admit_train_images()

    def test_admission_reads_only_frozen_train_groups_and_expected_unique_counts(self) -> None:
        self.assertEqual(self.admitted["unique_count"], 446)
        self.assertEqual(self.admitted["class_counts"], {"positive": 81, "negative": 365})
        self.assertEqual(set(self.admitted["source_groups"]), diagnostic.TRAIN_GROUPS)
        self.assertEqual(self.admitted["nontrain_image_hash_overlap"], 0)

    def test_window_uses_only_proposal_geometry_and_clips_to_crop(self) -> None:
        annotation = annotation_for_line([10, 4, 10, 10])
        self.assertNotIn("targets", annotation)
        self.assertEqual(diagnostic.proposal_window(annotation, (256, 96)), (15, 5, 26, 24))
        pixels = np.zeros((96, 256), dtype=np.float32)
        masked, box = diagnostic.mask_to_proposal_window(pixels, annotation)
        left, top, right, bottom = box
        self.assertTrue(np.all(masked[:top] == 1.0))
        self.assertTrue(np.all(masked[top:bottom, :left] == 1.0))
        self.assertTrue(np.all(masked[top:bottom, right:] == 1.0))
        self.assertTrue(np.all(masked[top:bottom, left:right] == 0.0))

    def test_window_clips_proposal_outside_canvas_without_fabricating_pixels(self) -> None:
        annotation = annotation_for_line([4, -1, 4, 2])
        self.assertEqual(diagnostic.proposal_window(annotation, (256, 96)), (3, 0, 14, 8))

    def test_duplicate_image_perturbation_unions_only_its_proposal_windows(self) -> None:
        pixels = np.zeros((96, 256), dtype=np.float32)
        boxes = [(5, 0, 16, 10), (20, 0, 31, 10)]
        masked, area = diagnostic.mask_to_proposal_windows(pixels, boxes)
        self.assertEqual(area, 220)
        self.assertTrue(np.all(masked[0:10, 5:16] == 0.0))
        self.assertTrue(np.all(masked[0:10, 20:31] == 0.0))
        self.assertTrue(np.all(masked[:, 16:20] == 1.0))

    def test_malformed_or_fully_outside_geometry_fails_closed(self) -> None:
        with self.assertRaises(ValueError):
            diagnostic.proposal_window({"proposal_line_original_xyxy": [0, 0]}, (256, 96))
        annotation = annotation_for_line([200, 10, 200, 20])
        with self.assertRaises(ValueError):
            diagnostic.proposal_window(annotation, (256, 96))

    def test_metrics_are_exact_at_frozen_threshold(self) -> None:
        result = diagnostic.binary_metrics([1, 1, 0, 0], [0.8, 0.4, 0.7, 0.2])
        self.assertEqual((result["true_positive"], result["false_positive"],
                          result["false_negative"], result["true_negative"]), (1, 1, 1, 1))
        self.assertEqual(result["precision"], 0.5)
        self.assertEqual(result["recall"], 0.5)

    def test_scores_reject_empty_misaligned_and_nonfinite_inputs(self) -> None:
        for labels, scores in (([], []), ([1], []), ([1], [float("nan")])):
            with self.assertRaises(ValueError):
                diagnostic.binary_metrics(labels, scores)

    def test_inference_binding_rejects_cpu_fallback_and_accepts_rx_identity(self) -> None:
        device = _Device()
        rx = {"device": "cuda:0", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False}
        diagnostic.assert_gpu_inference_binding(rx, _Model(), device)
        with self.assertRaises(diagnostic.DiagnosticError):
            diagnostic.assert_gpu_inference_binding({**rx, "cpu_fallback": True}, _Model(), device)
        with self.assertRaises(diagnostic.DiagnosticError):
            diagnostic.assert_gpu_inference_binding({**rx, "device_name": "CPU"}, _Model(), device)


if __name__ == "__main__":
    unittest.main()
