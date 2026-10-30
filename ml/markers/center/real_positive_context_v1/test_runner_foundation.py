# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only source, device, and optimizer-seam checks."""
from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

import numpy as np
import torch

from ml.markers.training_budget import TrainingAuthorization
from .model import RealPositiveCenterNet
from .objective import SyntheticLabels
from . import runner_foundation as runner
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


def _fake_report() -> dict:
    groups = [{"scope": f"{scope}:{orientation}", "rows": count}
              for scope, count in runner.V37_BASE_ROWS.items() for orientation in runner.V37_TRANSFORMS]
    return {"status": "fixed_reflection_training_features_measured", "feature_width": 768,
            "transforms": list(runner.V37_TRANSFORMS), "original_training_rows": 276618,
            "training_rows": 1106472, "all_original_rows_and_labels_retained": True,
            "model_parameters_and_buffers_unchanged": True, "raw_reconstruction_max_absolute_error": 0,
            "model": {"sha256": runner.V37_ORIGINAL_MODEL_SHA256}, "optimizer_steps": 0,
            "private_reads": 0, "sealed_reads": 0, "development_tensor_reads": 0, "groups": groups}


class InputEvidenceTests(unittest.TestCase):
    def test_report_integrity_and_train_split_are_fail_closed(self):
        report = _fake_report()
        raw = json.dumps(report).encode("utf-8")
        digest = sha256(raw).hexdigest()
        self.assertEqual(runner._v37_report(raw, digest)["training_rows"], 1106472)
        with self.assertRaises(ValueError):
            runner._v37_report(raw, "0" * 64)
        for key, invalid in (("private_reads", 1), ("sealed_reads", 1),
                             ("development_tensor_reads", 1), ("training_rows", 100)):
            damaged = dict(report, **{key: invalid})
            damaged_raw = json.dumps(damaged).encode("utf-8")
            with self.subTest(key=key), self.assertRaises(ValueError):
                runner._v37_report(damaged_raw, sha256(damaged_raw).hexdigest())
        damaged = _fake_report()
        damaged["groups"][0]["scope"] = "family:identity"
        damaged_raw = json.dumps(damaged).encode("utf-8")
        with self.assertRaises(ValueError):
            runner._v37_report(damaged_raw, sha256(damaged_raw).hexdigest())

    def test_synthetic_label_bytes_come_from_report_bound_array(self):
        values = np.zeros(35838, dtype=np.float32)
        values[:3258] = 1
        stream = BytesIO()
        np.save(stream, values, allow_pickle=False)
        array_raw = stream.getvalue()
        report = _fake_report()
        report["groups"][0].update(labels={"path": "fixture/train-labels.npy",
                                           "sha256": sha256(array_raw).hexdigest()},
                                   positive=3258, negative=32580)
        report_raw = json.dumps(report).encode("utf-8")
        refs = [MagicMock(read_bytes=MagicMock(return_value=report_raw)),
                MagicMock(read_bytes=MagicMock(return_value=array_raw))]
        with patch.object(runner, "V37_FEATURE_REPORT_SHA256", sha256(report_raw).hexdigest()), \
             patch.object(runner, "_inside_project", side_effect=refs):
            labels = runner.load_bound_synthetic_labels(Path.cwd(), Path.cwd() / "artifacts", "component")
        self.assertIsInstance(labels, SyntheticLabels)
        self.assertEqual(len(labels.values), 35838)
        self.assertEqual(sum(labels.values), 3258)

        refs[1].read_bytes.return_value = array_raw + b"tamper"
        with patch.object(runner, "V37_FEATURE_REPORT_SHA256", sha256(report_raw).hexdigest()), \
             patch.object(runner, "_inside_project", side_effect=refs):
            with self.assertRaises(ValueError):
                runner.load_bound_synthetic_labels(Path.cwd(), Path.cwd() / "artifacts", "component")

    def test_foreign_path_and_missing_admission_are_rejected(self):
        with self.assertRaises(ValueError):
            runner._inside_project(Path.cwd(), Path(Path.cwd().anchor))
        with self.assertRaisesRegex(RuntimeError, "registered candidate"):
            runner.run_training()

    def test_forged_noncanonical_authorization_is_rejected(self):
        root = Path.cwd()
        forged = TrainingAuthorization(directory=root, opened_path=Path(__file__),
            binding={"task": "marker-center", "revision": "fake", "candidate_id": "P1"},
            repo_root=root, snapshot_path=Path(__file__))
        with self.assertRaisesRegex(RuntimeError, "canonical seal directory"):
            runner._require_bound_source(forged, object())


class OptimizerSeamTests(unittest.TestCase):
    def _fixture(self):
        device = torch.device("cuda:0")
        if not torch.version.hip or not torch.cuda.is_available():
            self.skipTest("ROCm RX 6600 XT is required for GPU-only tests")
        properties = torch.cuda.get_device_properties(device)
        if properties.name != "AMD Radeon RX 6600 XT" or properties.gcnArchName.split(":")[0] != "gfx1032":
            self.skipTest("RX 6600 XT is not present at cuda:0")
        model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig()))).to(device)
        optimizer = torch.optim.AdamW(model.residual.parameters(), lr=runner.DRAFT_RECIPE.learning_rate,
                                      weight_decay=runner.DRAFT_RECIPE.weight_decay, capturable=True)
        synthetic = torch.zeros((2, 3, 33, 33), device=device)
        controls = torch.zeros((1, 3, 33, 33), device=device)
        raw = b"\x00\x01"
        labels = SyntheticLabels(raw, sha256(raw).hexdigest())
        source = MagicMock()
        source.next_batch.return_value = (synthetic, labels, controls)
        source.verify_batch.return_value = True
        source.verify_model.return_value = True
        source.verify_input_binding.return_value = True
        return model, optimizer, source, synthetic, labels, controls

    def test_synthetic_probe_has_gpu_gradients_and_no_optimizer(self):
        model, _, _, synthetic, labels, controls = self._fixture()
        before = {name: value.detach().clone() for name, value in model.state_dict().items()}
        value = runner.synthetic_gradient_probe(model, synthetic, labels, controls)
        self.assertTrue(np.isfinite(value))
        self.assertTrue(all(parameter.grad is None for parameter in model.local.parameters()))
        self.assertTrue(all(parameter.grad is not None and parameter.grad.device.type == "cuda"
                            for parameter in model.residual.parameters()))
        self.assertTrue(all(torch.equal(value, before[name]) for name, value in model.state_dict().items()))

    def test_verified_step_checks_binding_and_gpu_before_mocked_step(self):
        model, optimizer, source, _, _, _ = self._fixture()
        with patch.object(runner, "_require_bound_source", return_value={"real_positive_binding_sha256": "1" * 64}) as admission, \
             patch.object(optimizer, "step") as step:
            value = runner.train_one_verified_batch(model, optimizer, source, MagicMock())
        self.assertTrue(np.isfinite(value))
        self.assertEqual(admission.call_count, 3)
        self.assertEqual(source.verify_batch.call_count, 2)
        self.assertEqual(source.verify_model.call_count, 2)
        self.assertEqual(source.verify_input_binding.call_count, 2)
        step.assert_called_once()

    def test_unverified_center_cannot_reach_optimizer(self):
        model, optimizer, source, _, _, _ = self._fixture()
        source.verify_batch.return_value = False
        with patch.object(runner, "_require_bound_source", return_value={"real_positive_binding_sha256": "1" * 64}), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "unverified"):
                runner.train_one_verified_batch(model, optimizer, source, MagicMock())
        step.assert_not_called()

    def test_cpu_batch_is_rejected_before_mocked_optimizer(self):
        model, optimizer, source, synthetic, labels, controls = self._fixture()
        source.next_batch.return_value = (synthetic.cpu(), labels, controls)
        with patch.object(runner, "_require_bound_source", return_value={"real_positive_binding_sha256": "1" * 64}), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "CPU training is forbidden"):
                runner.train_one_verified_batch(model, optimizer, source, MagicMock())
        step.assert_not_called()


if __name__ == "__main__":
    unittest.main()
