# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic schedule and mocked runner tests; no optimizer or data access."""
from hashlib import sha256
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import torch

from ..real_positive_context_v1.objective import SyntheticLabels
from . import runner
from .schedule import BATCHES_PER_EPOCH, BATCH_SIZE, ROWS_PER_VIEW, ComponentSchedule


class ComponentScheduleTests(unittest.TestCase):
    def test_each_view_covers_all_rows_and_schedule_is_reproducible(self):
        first, second = ComponentSchedule(), ComponentSchedule()
        visited = {name: set() for name in runner.VIEWS}
        for step in range(1, BATCHES_PER_EPOCH + 1):
            plan = first.reserve(step)
            other = second.reserve(step)
            self.assertEqual(plan, other)
            self.assertEqual(len(set(plan.indices)), BATCH_SIZE)
            visited[plan.view].update(plan.indices)
            first.commit(plan)
            second.commit(other)
        self.assertTrue(all(len(indices) == ROWS_PER_VIEW for indices in visited.values()))
        self.assertEqual(first.completed, BATCHES_PER_EPOCH)

    def test_skipped_repeated_and_forged_steps_fail_closed(self):
        schedule = ComponentSchedule()
        with self.assertRaises(RuntimeError):
            schedule.reserve(2)
        plan = schedule.reserve(1)
        with self.assertRaises(RuntimeError):
            schedule.reserve(1)
        with self.assertRaises(RuntimeError):
            schedule.commit(SimpleNamespace(step=1))
        schedule.commit(plan)
        with self.assertRaises(RuntimeError):
            schedule.commit(plan)

    def test_missing_source_bundle_rejected_before_ledger_read(self):
        config = {"revision": runner.REVISION, "candidate_id": "P1",
                  "component_retention": runner.expected_component_config(),
                  "optimizer_steps_expected": runner.TOTAL_STEPS}
        authorization = SimpleNamespace(binding={"runner_source_paths": []})
        with self.assertRaisesRegex(RuntimeError, "immutable source bundle"):
            runner._validate_admission(config, authorization)

    def test_new_protocol_is_required_in_source_bundle(self):
        protocol = (runner.LOCAL / "protocol.json").as_posix()
        self.assertIn(protocol, {path.as_posix() for path in runner.REQUIRED_SOURCES})


class MockedStepTests(unittest.TestCase):
    def test_unadmitted_run_cannot_load_component_sources_or_claim_journal(self):
        with patch.object(runner.prior, "_require_bound_source", side_effect=RuntimeError("unadmitted")), \
             patch.object(runner.ComponentNegativeStore, "open_authenticated") as load, \
             patch.object(runner.prior._RunJournal, "claim") as claim:
            with self.assertRaisesRegex(RuntimeError, "unadmitted"):
                runner.run_training(MagicMock(), MagicMock(), MagicMock(), MagicMock())
        load.assert_not_called()
        claim.assert_not_called()

    def test_journal_precedes_mock_step_and_commits_follow_synchronization(self):
        events = []
        class Loss:
            device = torch.device("cuda:0")
            def backward(self): events.append("backward_mock")
            def detach(self): return self
            def cpu(self): return self
            def __float__(self): return 0.5
        raw = b"\x00\x01"
        batch = SimpleNamespace(plan=SimpleNamespace(step=1), synthetic=[0, 1],
            real_positive=[1], labels=SyntheticLabels(raw, sha256(raw).hexdigest()))
        source = MagicMock()
        source.verify_input_binding.return_value = source.verify_model.return_value = source.verify_batch.return_value = True
        source.next_gpu.side_effect = lambda: (events.append("mixed_reserve") or batch)
        source.commit.side_effect = lambda _: events.append("mixed_commit")
        journal = MagicMock(spec=runner.prior._RunJournal)
        journal.begin_step.side_effect = lambda *_: (events.append("journal_intent") or 1)
        journal.commit_step.side_effect = lambda _: events.append("journal_commit")
        component = SimpleNamespace(view="identity", indices=(1, 2), step=1)
        schedule = MagicMock()
        schedule.reserve.side_effect = lambda _: (events.append("component_reserve") or component)
        schedule.commit.side_effect = lambda _: events.append("component_commit")
        optimizer, model = MagicMock(), MagicMock()
        optimizer.step.side_effect = lambda: events.append("optimizer_mock")
        config = {"real_positive_binding_sha256": "1" * 64}
        with patch.object(runner.prior, "_require_bound_source", return_value=config), \
             patch.object(runner, "_validate_admission"), \
             patch.object(runner.prior, "_require_reviewed_schedule"), \
             patch.object(runner.prior, "require_gpu_model"), \
             patch.object(runner.prior, "_require_optimizer"), \
             patch.object(runner, "preservation_loss", return_value=SimpleNamespace(total=Loss())), \
             patch.object(runner.torch, "isfinite", return_value=True), \
             patch.object(runner.torch.cuda, "synchronize", side_effect=lambda *_: events.append("synchronize_mock")):
            value = runner._train_step(model, optimizer, source, MagicMock(), journal, schedule, {"identity": object()})
        self.assertEqual(value, 0.5)
        self.assertEqual(events, ["journal_intent", "mixed_reserve", "component_reserve", "backward_mock",
                                  "optimizer_mock", "synchronize_mock", "journal_commit", "mixed_commit", "component_commit"])
        self.assertEqual(source.verify_batch.call_count, 2)


if __name__ == "__main__":
    unittest.main()
