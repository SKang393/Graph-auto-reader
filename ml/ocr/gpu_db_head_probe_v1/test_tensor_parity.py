# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest
import json
from hashlib import sha256
from io import BytesIO
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

import numpy as np
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import tensor_parity as subject
from ml.ocr.gpu_db_head_probe_v1.tensor_parity import compare_tensors


class TensorParityTests(unittest.TestCase):
    def test_byte_identity_and_changed_float(self):
        values = np.array([0.0, 1.0], dtype="<f4")
        self.assertTrue(compare_tensors(values, values.tobytes())["byte_identical"])
        changed = np.array([0.0, 2.0], dtype="<f4")
        result = compare_tensors(values, changed.tobytes())
        self.assertFalse(result["byte_identical"])
        self.assertEqual(result["different_float32_bits"], 1)
        self.assertEqual(result["max_absolute_difference"], 1.0)

    def test_signed_zero_is_detected_despite_equal_values(self):
        positive = np.array([0.0], dtype="<f4")
        negative = np.array([-0.0], dtype="<f4")
        result = compare_tensors(positive, negative.tobytes())
        self.assertFalse(result["byte_identical"])
        self.assertEqual(result["different_float32_bits"], 1)
        self.assertEqual(result["max_absolute_difference"], 0)

    def test_wrong_length_and_nonfinite_values_rejected(self):
        values = np.array([0.0], dtype="<f4")
        with self.assertRaisesRegex(ValueError, "shape"):
            compare_tensors(values, b"")
        with self.assertRaisesRegex(ValueError, "nonfinite"):
            compare_tensors(values, np.array([np.nan], dtype="<f4").tobytes())

    def test_bgr_mismatch_fails_before_report(self):
        buffer = BytesIO()
        Image.new("RGB", (32, 32), "white").save(buffer, format="PNG")
        tensor = np.zeros((1, 3, 1, 1), dtype="<f4")
        source = {"image": {"sha256": "a" * 64}, "annotation": {"sha256": "b" * 64}}
        saved = {"source_image_sha256": "a" * 64, "source_annotation_sha256": "b" * 64,
                 "tensor_sha256": sha256(tensor.tobytes()).hexdigest(), "tensor_shape": list(tensor.shape)}
        capture = {"schema": "graphreader.synthetic-tensor-parity-capture.v1", "maximum_side_length": 512,
                   "dimension_multiple": 128, "optimizer_steps": 0, "training_input_approved": False,
                   "model_inference": False, "opencv_version": "4.13.0", "images": [
                       {"source_image_sha256": "a" * 64, "tensor_file": f"train-{i}.tensor.f32",
                        "bgr_file": f"train-{i}.bgr.u8", "shape": list(tensor.shape),
                        "tensor_sha256": "tensor", "bgr_sha256": "bgr", "production_960_shape": []}
                       for i in range(4)]}
        temp_root = subject.parity.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            path = Path(tmp)
            (path / "capture.json").write_text(json.dumps(capture))
            def read(_path, digest):
                return {subject.PREFLIGHT_SHA: json.dumps({"cases": [saved] * 4}).encode(),
                        "tensor": tensor.tobytes(), "bgr": bytes(32 * 32 * 3)}[digest]
            with mock.patch.object(subject.tensor_parity_identity, "load", return_value={"files": {}}), \
                 mock.patch.object(subject.tensor_parity_identity, "check_capture"), \
                 mock.patch.object(subject.parity, "_read_locked", side_effect=read), \
                 mock.patch.object(subject.preflight, "_train_inputs", return_value=[(source, buffer.getvalue(), {})] * 4), \
                 mock.patch.object(subject.preflight, "_prepare", return_value=(tensor, None, None, {})):
                output = path / "report.json"
                with self.assertRaisesRegex(ValueError, "BGR image bytes differ"):
                    subject.run(path, output, path / "identity.json", "identity")
                self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
