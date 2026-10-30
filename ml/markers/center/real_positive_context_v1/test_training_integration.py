# SPDX-License-Identifier: Apache-2.0
"""Synthetic wiring tests: CPU fixture tensors never enter a real optimizer."""
from __future__ import annotations

from contextlib import ExitStack
from hashlib import sha256
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import torch

from ml.markers.training_budget import TrainingAuthorization
from . import runner_foundation as runner
from . import checkpoint_writer
from .model import RealPositiveCenterNet
from .test_mixed_source import _setup
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


class _Model:
    def __init__(self, events):
        self.events = events

    def train(self):
        return self

    def logits_and_geometry(self, _batch):
        self.events.append("forward")
        return object(), None

    def parameters(self):
        return []

    def buffers(self):
        return []


class _Loss:
    device = torch.device("cuda:0")

    def backward(self):
        pass

    def detach(self):
        return self

    def cpu(self):
        return self

    def __float__(self):
        return 0.5


class _Optimizer:
    """No PyTorch optimizer or gradient calculation is constructed."""
    def __init__(self, events, corrupt=None):
        self.events, self.corrupt = events, corrupt

    def zero_grad(self, **_kwargs):
        if self.corrupt:
            self.corrupt()

    def step(self):
        self.events.append("optimizer_step")


class TrainingIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp")
        self.directory = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.authorization = TrainingAuthorization(directory=self.directory,
            opened_path=self.directory / "opened.json", binding={"synthetic_test": True})
        _, _, _, self.source = _setup()
        self.events = []
        self.model = _Model(self.events)
        self.optimizer = _Optimizer(self.events)
        self.config = {"real_positive_binding_sha256": "1" * 64, "optimizer_steps_expected": 6}
        self.journal = runner._RunJournal.claim(self.authorization, 6)
        self.addCleanup(self.journal.close)
        self.source.verify_model = lambda *_args: True
        self.source.verify_input_binding = lambda *_args: True

    def _contexts(self, *, fail_sync=False):
        stack = ExitStack()
        actual_begin, actual_complete = self.journal.begin_step, self.journal.commit_step
        actual_prepare, actual_verify, actual_commit = self.source._prepare_cpu, self.source.verify_batch, self.source.commit
        actual_isfinite = torch.isfinite

        def begin(*args):
            result = actual_begin(*args)
            self.events.append("journal_intent")
            return result

        def reserve():
            self.events.append("source_reserve")
            self.current_batch = actual_prepare()
            return self.current_batch

        def verify(batch):
            self.events.append("verify_batch")
            return actual_verify(batch)

        def synchronize(_device):
            self.events.append("synchronize")
            if fail_sync:
                raise RuntimeError("synthetic synchronization failure")

        def completed(step):
            result = actual_complete(step)
            self.events.append("journal_complete")
            return result

        def commit(plan):
            self.events.append("schedule_commit")
            return actual_commit(plan)

        def finite(value):
            return torch.tensor(True) if isinstance(value, _Loss) else actual_isfinite(value)

        for obj, name, kwargs in (
            (runner, "_require_bound_source", {"return_value": self.config}),
            (runner, "_require_reviewed_schedule", {}),
            (runner, "require_gpu_model", {}),
            (runner, "_require_optimizer", {}),
            (runner, "mixed_confidence_loss", {"return_value": _Loss()}),
            (torch, "isfinite", {"side_effect": finite}),
            (torch.cuda, "synchronize", {"side_effect": synchronize}),
            (self.journal, "begin_step", {"side_effect": begin}),
            (self.journal, "commit_step", {"side_effect": completed}),
            (self.source, "next_gpu", {"side_effect": reserve}),
            (self.source, "verify_batch", {"side_effect": verify}),
            (self.source, "commit", {"side_effect": commit}),
        ):
            stack.enter_context(patch.object(obj, name, **kwargs))
        return stack

    def _run_one(self):
        return runner._train_one_verified_batch(self.model, self.optimizer, self.source,
                                               self.authorization, self.journal)

    def test_journal_verify_step_synchronize_and_schedule_order(self):
        with self._contexts():
            self._run_one()
        self.assertEqual(self.events, ["journal_intent", "source_reserve", "verify_batch", "forward", "forward",
                                       "verify_batch", "optimizer_step", "synchronize", "journal_complete",
                                       "schedule_commit"])
        self.assertEqual(self.journal.completed, 1)
        self.assertEqual(self.source._schedule.completed_steps, 1)

    def test_changed_batch_is_rejected_before_mock_step(self):
        def corrupt():
            self.current_batch.synthetic[0, 0, 0, 0] += 0.125
        self.optimizer.corrupt = corrupt
        with self._contexts():
            with self.assertRaises((RuntimeError, ValueError)):
                self._run_one()
        self.assertNotIn("optimizer_step", self.events)
        self.assertNotIn("journal_complete", self.events)
        self.assertNotIn("schedule_commit", self.events)
        self.assertEqual(self.journal.pending, 1)

    def test_interrupted_synchronization_cannot_reclaim_or_resume(self):
        with self._contexts(fail_sync=True):
            with self.assertRaisesRegex(RuntimeError, "synthetic synchronization"):
                self._run_one()
        self.assertEqual(self.journal.pending, 1)
        self.assertEqual(self.source._schedule.completed_steps, 0)
        self.assertNotIn("journal_complete", self.events)
        with self.assertRaises(FileExistsError):
            runner._RunJournal.claim(self.authorization, 6)


class CheckpointFinalizationTests(unittest.TestCase):
    def test_verified_checkpoint_precedes_success_outcome_and_finalization(self):
        with tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp") as directory:
            directory = Path(directory)
            authorization = TrainingAuthorization(directory=directory, opened_path=directory / "opened.json",
                binding={"synthetic_test": True, "revision": "fixture-v1", "candidate_id": "P1"})
            model = _Model([])
            optimizer = _Optimizer([])
            _, _, _, source = _setup()
            source.verify_model = lambda *_args: True
            source.begin_fixed_run = lambda *_args: True
            actual_model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig())))
            base, candidate = directory / "base.pt", directory / "candidate.pt"
            torch.save({"state_dict": actual_model.local.state_dict()}, base)
            base_hash = sha256(base.read_bytes()).hexdigest()
            config = {"optimizer_steps_expected": 1, "real_positive_binding_sha256": "1" * 64,
                      "source_checkpoint_path": base.relative_to(runner.PROJECT_ROOT).as_posix(),
                      "candidate_checkpoint_path": candidate.relative_to(runner.PROJECT_ROOT).as_posix()}
            events = []

            def simulated_step(_model, _optimizer, _source, auth, journal):
                step = journal.begin_step(auth, 1)
                journal.commit_step(step)
                events.append("step_completed")
                return 0.5

            def save(_model, *, base_path, destination, revision, candidate_id):
                self.assertEqual(Path(base_path), base)
                self.assertEqual(Path(destination), candidate)
                with patch.object(checkpoint_writer, "_ALLOWED_BASES", (base,)), \
                     patch.object(checkpoint_writer, "V37_CHECKPOINT_SHA256", base_hash):
                    receipt = checkpoint_writer.write_candidate_checkpoint(actual_model, base_path=base,
                        destination=candidate, revision=revision, candidate_id=candidate_id)
                self.assertEqual(receipt.sha256, sha256(candidate.read_bytes()).hexdigest())
                events.append("checkpoint_verified")
                return receipt

            def finalize(_authorization, *, status, report_sha256):
                self.assertEqual(status, "training_complete_unapproved")
                outcome = (directory / "optimizer-run-outcome.json").read_bytes()
                self.assertEqual(report_sha256, sha256(outcome).hexdigest())
                parsed = json.loads(outcome)
                self.assertEqual(parsed["checkpoint_sha256"], sha256(candidate.read_bytes()).hexdigest())
                self.assertEqual(parsed["checkpoint_path"], config["candidate_checkpoint_path"])
                events.append("candidate_finalized")

            with patch.object(runner, "RealPositiveCenterNet", _Model), \
                 patch.object(torch.optim, "AdamW", _Optimizer), \
                 patch.object(runner, "_require_bound_source", return_value=config), \
                 patch.object(runner, "_require_reviewed_schedule"), \
                 patch.object(runner, "_require_training_gpu"), \
                 patch.object(runner, "_require_optimizer"), \
                 patch.object(runner, "_require_fresh_state"), \
                 patch.object(runner, "_train_one_verified_batch", side_effect=simulated_step), \
                 patch.object(runner, "write_candidate_checkpoint", side_effect=save, create=True), \
                 patch.object(runner, "complete_training_candidate", side_effect=finalize):
                result = runner.run_training(model, optimizer, source, authorization)
            self.assertEqual(events, ["step_completed", "checkpoint_verified", "candidate_finalized"])
            self.assertFalse(result["production_approval"])


if __name__ == "__main__":
    unittest.main()
