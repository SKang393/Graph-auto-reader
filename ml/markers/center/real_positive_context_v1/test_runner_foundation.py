# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only source, device, and optimizer-seam checks."""
from __future__ import annotations

from hashlib import sha256
from types import SimpleNamespace
from io import BytesIO
import json
from pathlib import Path
import tempfile
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
    def setUp(self):
        temporary_root = runner.PROJECT_ROOT / "tools/gpu-training/.state/temp"
        temporary_root.mkdir(parents=True, exist_ok=True)
        self._temporary = tempfile.TemporaryDirectory(prefix="real-positive-accounting-", dir=temporary_root)
        self.directory = Path(self._temporary.name).resolve()
        if not self.directory.is_relative_to(temporary_root.resolve()):
            raise RuntimeError("Temporary test path escaped the project")
        self.authorization = TrainingAuthorization(directory=self.directory,
            opened_path=self.directory / "opened.json", binding={"synthetic_test": True})
        self.addCleanup(self._cleanup_test_directory, temporary_root.resolve())

    def _cleanup_test_directory(self, root):
        if not self.directory.is_relative_to(root):
            raise RuntimeError("Refusing cleanup outside the project test directory")
        self._temporary.cleanup()

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
        source.next_gpu.side_effect = lambda: SimpleNamespace(
            plan=SimpleNamespace(step=source.next_gpu.call_count), synthetic=synthetic,
            labels=labels, real_positive=controls)
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
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 1}
        journal = runner._RunJournal.claim(self.authorization, 1)
        with patch.object(runner, "_require_bound_source", return_value=config) as admission, \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(optimizer, "step") as step:
            value = runner._train_one_verified_batch(model, optimizer, source, self.authorization, journal)
        self.assertTrue(np.isfinite(value))
        self.assertEqual(admission.call_count, 3)
        self.assertEqual(source.verify_batch.call_count, 2)
        self.assertEqual(source.verify_model.call_count, 2)
        self.assertEqual(source.verify_input_binding.call_count, 2)
        step.assert_called_once()
        self.assertEqual(journal.completed, 1)
        with self.assertRaisesRegex(RuntimeError, "outside the single-use"):
            journal.begin_step(self.authorization, 1)

    def test_unverified_center_cannot_reach_optimizer(self):
        model, optimizer, source, _, _, _ = self._fixture()
        source.verify_batch.return_value = False
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 1}
        journal = runner._RunJournal.claim(self.authorization, 1)
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "unverified"):
                runner._train_one_verified_batch(model, optimizer, source, self.authorization, journal)
        step.assert_not_called()
        self.assertEqual(journal.pending, 1)

    def test_cpu_batch_is_rejected_before_mocked_optimizer(self):
        model, optimizer, source, synthetic, labels, controls = self._fixture()
        source.next_gpu.side_effect = None
        source.next_gpu.return_value = SimpleNamespace(
            plan=SimpleNamespace(step=1), synthetic=synthetic.cpu(), labels=labels, real_positive=controls)
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 1}
        journal = runner._RunJournal.claim(self.authorization, 1)
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "CPU training is forbidden"):
                runner._train_one_verified_batch(model, optimizer, source, self.authorization, journal)
        step.assert_not_called()

    def test_interrupted_run_cannot_be_claimed_or_replayed(self):
        unclaimed = runner._RunJournal(self.authorization, 2)
        with self.assertRaisesRegex(RuntimeError, "run intent"):
            unclaimed.begin_step(self.authorization, 2)
        journal = runner._RunJournal.claim(self.authorization, 2)
        self.assertEqual(journal.begin_step(self.authorization, 2), 1)
        with self.assertRaisesRegex(RuntimeError, "outside the single-use"):
            journal.begin_step(self.authorization, 2)
        with self.assertRaises(FileExistsError):
            runner._RunJournal.claim(self.authorization, 2)
        lines = (self.directory / "optimizer-journal.jsonl").read_text().splitlines()
        self.assertEqual([json.loads(line)["event"] for line in lines], ["run_opened", "step_intent"])

    def test_fresh_state_rejects_modified_residual_or_existing_optimizer_state(self):
        model, optimizer, _, _, _, _ = self._fixture()
        runner._require_fresh_state(model, optimizer)
        with torch.no_grad():
            model.residual.bias.fill_(1)
        with self.assertRaisesRegex(RuntimeError, "zero-initialized"):
            runner._require_fresh_state(model, optimizer)

    def test_missing_reviewed_schedule_blocks_before_source_or_optimizer(self):
        source = MagicMock()
        with self.assertRaisesRegex(RuntimeError, "schedule"):
            runner._require_reviewed_schedule({}, source)
        source.assert_not_called()

    def test_individual_batch_also_rejects_missing_schedule(self):
        model, optimizer, source, _, _, _ = self._fixture()
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 1}
        journal = runner._RunJournal.claim(self.authorization, 1)
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "schedule"):
                runner._train_one_verified_batch(model, optimizer, source, self.authorization, journal)
        step.assert_not_called()
        self.assertIsNone(journal.pending)

    def test_complete_run_is_single_use_and_finalizes_without_promotion(self):
        model, optimizer, source, _, _, _ = self._fixture()
        source.begin_fixed_run.return_value = True
        self.authorization.binding.update({"revision": "synthetic-v1", "candidate_id": "P1"})
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 2,
                  "source_checkpoint_path": "artifacts/test-temp/base.pt",
                  "candidate_checkpoint_path": "artifacts/test-temp/candidate.pt"}
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(runner, "write_candidate_checkpoint", return_value=SimpleNamespace(
                 relative_path="artifacts/test-temp/candidate.pt", sha256="2" * 64)), \
             patch.object(runner, "complete_training_candidate") as complete, \
             patch.object(optimizer, "step") as step:
            result = runner.run_training(model, optimizer, source, self.authorization)
            self.assertEqual(step.call_count, 2)
            self.assertEqual(result["completed_steps"], 2)
            self.assertEqual(result["status"], "training_complete_unapproved")
            self.assertFalse(result["production_approval"])
            self.assertEqual(complete.call_args.kwargs["status"], "training_complete_unapproved")
            optimizer.zero_grad(set_to_none=True)
            with self.assertRaises(FileExistsError):
                runner.run_training(model, optimizer, source, self.authorization)
            self.assertEqual(step.call_count, 2)
            source.begin_fixed_run.assert_called_once()

    def test_partial_run_is_finalized_and_cannot_be_replayed(self):
        model, optimizer, source, _, _, _ = self._fixture()
        source.begin_fixed_run.return_value = True
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 2}
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(runner, "complete_training_candidate") as complete, \
             patch.object(optimizer, "step", side_effect=[None, RuntimeError("synthetic failure")]) as step:
            with self.assertRaisesRegex(RuntimeError, "synthetic failure"):
                runner.run_training(model, optimizer, source, self.authorization)
            self.assertEqual(step.call_count, 2)
            outcome = json.loads((self.directory / "optimizer-run-outcome.json").read_text())
            self.assertEqual(outcome["status"], "failed_training_incomplete")
            self.assertEqual(outcome["completed_steps"], 1)
            self.assertEqual(outcome["pending_step"], 2)
            self.assertNotIn("synthetic failure", json.dumps(outcome))
            self.assertEqual(complete.call_args.kwargs["status"], "failed_training_incomplete")
            with self.assertRaises(FileExistsError):
                runner._RunJournal.claim(self.authorization, 2)

    def test_run_blocks_before_training_when_schedule_missing(self):
        model, optimizer, source, _, _, _ = self._fixture()
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 2}
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(optimizer, "step") as step:
            with self.assertRaisesRegex(RuntimeError, "schedule"):
                runner.run_training(model, optimizer, source, self.authorization)
        step.assert_not_called()
        source.begin_fixed_run.assert_not_called()
        self.assertFalse((self.directory / "optimizer-run-intent.json").exists())

    def test_failed_gpu_synchronization_leaves_step_pending(self):
        model, optimizer, source, _, _, _ = self._fixture()
        source.begin_fixed_run.return_value = True
        config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 1}
        with patch.object(runner, "_require_bound_source", return_value=config), \
             patch.object(runner, "_require_reviewed_schedule"), \
             patch.object(runner, "complete_training_candidate"), \
             patch.object(optimizer, "step") as step, \
             patch.object(torch.cuda, "synchronize", side_effect=RuntimeError("synthetic sync failure")):
            with self.assertRaisesRegex(RuntimeError, "synthetic sync failure"):
                runner.run_training(model, optimizer, source, self.authorization)
        step.assert_called_once()
        outcome = json.loads((self.directory / "optimizer-run-outcome.json").read_text())
        self.assertEqual(outcome["completed_steps"], 0)
        self.assertEqual(outcome["pending_step"], 1)


if __name__ == "__main__":
    unittest.main()
