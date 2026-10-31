# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exercise real comparator I/O with synthetic admitted inputs and a bad tensor.

Only source admission and identity loading are replaced by synthetic fixtures;
file hashing, capture identity checks, preprocessing and comparison remain real.
"""
from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

from ml.ocr.gpu_db_head_probe_v1 import tensor_parity, tensor_parity_960, tensor_parity_identity
from ml.ocr.gpu_db_head_probe_v1.test_feature_preflight import fixture


class ComparatorFlowTests(unittest.TestCase):
    def _mismatch(self, setting):
        temp_root = tensor_parity.parity.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as temporary:
            root = Path(temporary)
            png, annotation = fixture()
            tensor512 = tensor_parity_960.candidate_tensor(png, 512)
            tensor960 = tensor_parity_960.candidate_tensor(png, 960)
            target = tensor512 if setting == 512 else tensor960
            changed = target.copy()
            changed.flat[0] += 0.125
            source = {"image": {"sha256": sha256(png).hexdigest()}, "annotation": {"sha256": "b" * 64}}
            saved = {"source_image_sha256": source["image"]["sha256"], "source_annotation_sha256": "b" * 64,
                     "tensor_sha256": sha256(tensor512.tobytes()).hexdigest(), "tensor_shape": list(tensor512.shape)}
            prior = root / "prior.json"
            prior.write_text(json.dumps({"cases": [saved] * 4}), encoding="utf-8")
            prior_sha = sha256(prior.read_bytes()).hexdigest()
            keyset = set(tensor_parity_identity.CAPTURE_FIELDS.values()) | tensor_parity_identity.CAPTURE_EXTRA
            identity = {"files": {key: {"sha256": "a" * 64} for key in keyset}}
            capture = {"schema": "graphreader.synthetic-tensor-parity-capture.v1", "maximum_side_length": 512,
                       "dimension_multiple": 128, "optimizer_steps": 0, "training_input_approved": False,
                       "model_inference": False, "opencv_version": "4.13.0", "run_identity_sha256": "c" * 64,
                       "additional_runtime_sha256": {key: "a" * 64 for key in tensor_parity_identity.CAPTURE_EXTRA}}
            capture.update({field: "a" * 64 for field in tensor_parity_identity.CAPTURE_FIELDS})
            rows = []
            for index in range(4):
                rows.append({"source_image_sha256": source["image"]["sha256"],
                    "tensor_file": f"train-{index}.tensor.f32", "tensor_sha256": sha256(changed.tobytes()).hexdigest(),
                    "bgr_file": f"train-{index}.bgr.u8", "bgr_sha256": sha256(bytes([255]) * (64 * 64 * 3)).hexdigest(),
                    "shape": list(tensor512.shape), "production_960_shape": list(tensor960.shape),
                    "production_960_file": f"train-{index}.production-960.f32",
                    "production_960_sha256": sha256(changed.tobytes()).hexdigest()})
            capture["images"] = rows
            (root / "capture.json").write_text(json.dumps(capture), encoding="utf-8")
            filename = "train-0.tensor.f32" if setting == 512 else "train-0.production-960.f32"
            (root / filename).write_bytes(changed.tobytes())
            if setting == 512:
                (root / "train-0.bgr.u8").write_bytes(bytes([255]) * (64 * 64 * 3))
            output = root / "comparison.json"
            with mock.patch.object(tensor_parity_identity, "load", return_value=identity), \
                 mock.patch.object(tensor_parity.preflight, "_train_inputs", return_value=[(source, png, annotation)] * 4), \
                 mock.patch.object(tensor_parity, "PREFLIGHT", prior), \
                 mock.patch.object(tensor_parity, "PREFLIGHT_SHA", prior_sha):
                with self.assertRaisesRegex(ValueError, f"{setting} tensor bytes differ"):
                    comparator = tensor_parity.run if setting == 512 else tensor_parity_960.run
                    comparator(root, output, root / "identity.json", "c" * 64)
            self.assertFalse(output.exists())

    def test_512_changed_capture_rejected(self):
        self._mismatch(512)

    def test_960_changed_capture_rejected(self):
        self._mismatch(960)
