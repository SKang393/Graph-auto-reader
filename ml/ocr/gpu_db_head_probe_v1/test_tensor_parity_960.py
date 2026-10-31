# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from io import BytesIO
from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

import numpy as np
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import tensor_parity_960


class ProductionSizeTests(unittest.TestCase):
    def test_production_ceiling_alignment_and_normalization(self):
        buffer = BytesIO()
        Image.new("RGB", (64, 32), "white").save(buffer, format="PNG")
        tensor = tensor_parity_960.candidate_tensor(buffer.getvalue(), 960)
        self.assertEqual(tensor.shape, (1, 3, 512, 1024))
        expected = (np.float32(1) - np.asarray([0.485, 0.456, 0.406], dtype=np.float32)) * (
            np.float32(1) / np.asarray([0.229, 0.224, 0.225], dtype=np.float32))
        np.testing.assert_array_equal(tensor[0, :, 0, 0], expected)

    def test_unregistered_tensor_size_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            tensor_parity_960.candidate_tensor(b"", 1000)

    def test_960_tensor_mismatch_fails_before_report(self):
        tensor = np.zeros((1, 3, 1, 1), dtype="<f4")
        changed = tensor.copy()
        changed[0, 0, 0, 0] = 1.0
        source = {"image": {"sha256": "a" * 64}}
        saved = {"source_image_sha256": "a" * 64, "tensor_sha256": sha256(tensor.tobytes()).hexdigest(),
                 "tensor_shape": list(tensor.shape)}
        capture = {"schema": "graphreader.synthetic-tensor-parity-capture.v1", "maximum_side_length": 512,
                   "dimension_multiple": 128, "optimizer_steps": 0, "training_input_approved": False,
                   "model_inference": False, "opencv_version": "4.13.0", "images": [
                       {"source_image_sha256": "a" * 64, "production_960_file": f"train-{i}.production-960.f32",
                        "production_960_sha256": sha256(changed.tobytes()).hexdigest(),
                        "production_960_shape": list(tensor.shape)} for i in range(4)]}
        temp_root = tensor_parity_960.parity.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            path = Path(tmp)
            (path / "capture.json").write_text(json.dumps(capture))
            def read(_path, digest):
                if digest == tensor_parity_960.tensor_parity.PREFLIGHT_SHA:
                    return json.dumps({"cases": [saved] * 4}).encode()
                self.assertEqual(digest, sha256(changed.tobytes()).hexdigest())
                return changed.tobytes()
            with mock.patch.object(tensor_parity_960.tensor_parity_identity, "load", return_value={"files": {}}), \
                 mock.patch.object(tensor_parity_960.tensor_parity_identity, "check_capture"), \
                 mock.patch.object(tensor_parity_960.parity, "_read_locked", side_effect=read), \
                 mock.patch.object(tensor_parity_960.preflight, "_train_inputs", return_value=[(source, b"", {})] * 4), \
                 mock.patch.object(tensor_parity_960, "candidate_tensor", return_value=tensor):
                output = path / "report.json"
                with self.assertRaisesRegex(ValueError, "960 tensor bytes differ"):
                    tensor_parity_960.run(path, output, path / "identity.json", "identity")
                self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
