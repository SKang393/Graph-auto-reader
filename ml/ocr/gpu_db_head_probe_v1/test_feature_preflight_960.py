# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight_960 as subject
from ml.ocr.gpu_db_head_probe_v1.test_feature_preflight import fixture


def parity_fixture():
    rows = [{"source_image_sha256": str(index) * 64, "byte_identical": True,
             "different_bytes": 0, "different_float32_bits": 0,
             "candidate_tensor_sha256": "a" * 64, "production_tensor_sha256": "a" * 64}
            for index in range(4)]
    bound = {"files": {"one": "synthetic"}}
    report = {"schema": "graphreader.synthetic-tensor-parity-960-comparison.v1",
              "capture_sha256": subject.CAPTURE_SHA, "run_identity_sha256": subject.IDENTITY_SHA,
              "run_identity_files": bound["files"], "all_tensors_byte_identical": True,
              "full_production_workflow_replayed": False, "training_input_approved": False,
              "optimizer_steps": 0, "holdout_raster_or_label_reads": 0, "private_reads": 0,
              "sealed_reads": 0, "cases": rows}
    return report, bound


class ProductionPreflightTests(unittest.TestCase):
    def test_960_mapping_and_marker_artifact_ignore(self):
        png, annotation = fixture()
        annotation["coordinate_space"] = "original_pixels"
        tensor, built, mask, identities = subject.prepare(png, annotation)
        self.assertEqual(tensor.shape, (1, 3, 1024, 1024))
        self.assertEqual(built.regions[0].tensor_box, (128., 128., 256., 256.))
        self.assertEqual(identities, [("tick1", "x_tick")])
        self.assertEqual(mask[0, 672, 672], 0)
        self.assertTrue((mask[built.target == 1] == 1).all())

    def test_missing_or_mismatched_parity_fails_closed(self):
        report, bound = parity_fixture()
        self.assertEqual(len(subject.validate_parity(report, bound)), 4)
        report["cases"][0]["different_bytes"] = 1
        with self.assertRaisesRegex(ValueError, "parity case"):
            subject.validate_parity(report, bound)
        report, bound = parity_fixture()
        report["run_identity_sha256"] = "wrong"
        with self.assertRaisesRegex(ValueError, "parity scope"):
            subject.validate_parity(report, bound)
        report, bound = parity_fixture()
        report["cases"][1]["source_image_sha256"] = report["cases"][0]["source_image_sha256"]
        with self.assertRaisesRegex(ValueError, "parity case"):
            subject.validate_parity(report, bound)

    def test_unsupported_annotation_is_rejected(self):
        png, annotation = fixture()
        annotation["coordinate_space"] = "original_pixels"
        annotation["panels"][0]["texts"][0]["partial"] = True
        with self.assertRaisesRegex(ValueError, "visibility"):
            subject.prepare(png, annotation)
