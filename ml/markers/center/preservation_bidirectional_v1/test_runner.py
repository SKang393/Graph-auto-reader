# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic schedule and journal tests; all optimizer operations are mocks."""
from hashlib import sha256
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import torch

from ..real_positive_context_v1.objective import SyntheticLabels
from ..real_positive_preservation_v1.component_source import VIEWS
from ..real_positive_preservation_v1.schedule import BATCHES_PER_EPOCH, TOTAL_STEPS
from . import runner
from .schedule import COMPONENT_ROWS, FAMILY_ROWS, DirectionalSchedule


class FixedScheduleTests(unittest.TestCase):
    def test_deterministic_complete_per_epoch_per_view_coverage(self):
        one, two = DirectionalSchedule(), DirectionalSchedule()
        component = {view: set() for view in VIEWS}
        family = {view: set() for view in VIEWS}
        for step in range(1, TOTAL_STEPS + 1):
            first, second = one.reserve(step), two.reserve(step)
            self.assertEqual(first, second)
            component[first.view].update(first.component_indices)
            family[first.view].update(first.family_indices)
            one.commit(first)
            two.commit(second)
            if step % BATCHES_PER_EPOCH == 0:
                for view in VIEWS:
                    self.assertEqual(component[view], set(range(COMPONENT_ROWS)))
                    self.assertEqual(family[view], set(range(FAMILY_ROWS)))
                component = {view: set() for view in VIEWS}
                family = {view: set() for view in VIEWS}
        self.assertEqual(one.completed, 2264)
        with self.assertRaises(RuntimeError):
            one.reserve(TOTAL_STEPS + 1)

    def test_skipped_repeated_or_forged_plan_rejected(self):
        schedule = DirectionalSchedule()
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


class AdmissionAndJournalTests(unittest.TestCase):
    def test_old_or_unregistered_candidate_cannot_open_journal_or_load_sources(self):
        with patch.object(runner.prior, "_require_bound_source", side_effect=RuntimeError("unadmitted")), \
             patch.object(runner.SyntheticTrainFeatures, "load") as load, \
             patch.object(runner.prior._RunJournal, "claim") as claim:
            with self.assertRaisesRegex(RuntimeError, "unadmitted"):
                runner.run_training(MagicMock(), MagicMock(), MagicMock(), MagicMock())
        load.assert_not_called()
        claim.assert_not_called()
        config = {"revision": "marker-center-real-positive-preservation-v1", "candidate_id": "P1",
                  "component_retention": runner.preservation.expected_component_config(),
                  "directional_preservation": runner.expected_directional_config(), "optimizer_steps_expected": TOTAL_STEPS}
        with self.assertRaisesRegex(RuntimeError, "not exactly preregistered"):
            runner._validate_admission(config, SimpleNamespace(binding={}))

    def test_missing_source_bundle_and_non_float_weights_fail_closed(self):
        config = {"revision": runner.REVISION, "candidate_id": "P1",
                  "component_retention": runner.preservation.expected_component_config(),
                  "directional_preservation": runner.expected_directional_config(), "optimizer_steps_expected": TOTAL_STEPS}
        with self.assertRaisesRegex(RuntimeError, "immutable source bundle"):
            runner._validate_admission(config, SimpleNamespace(binding={"runner_source_paths": []}))
        config["directional_preservation"]["component_weight"] = 1
        with self.assertRaisesRegex(RuntimeError, "not exactly preregistered"):
            runner._validate_admission(config, SimpleNamespace(binding={}))

    def test_missing_protocol_registration_blocks_even_with_complete_source_list(self):
        config = {"revision": runner.REVISION, "candidate_id": "P1",
                  "component_retention": runner.preservation.expected_component_config(),
                  "directional_preservation": runner.expected_directional_config(), "optimizer_steps_expected": TOTAL_STEPS}
        authorization = SimpleNamespace(binding={"runner_source_paths": [path.as_posix() for path in runner.REQUIRED_SOURCES]})
        fake_ledger = MagicMock()
        fake_ledger.read_bytes.return_value = json.dumps({"revisions": []}).encode()
        with patch.object(runner.prior, "_inside_project", return_value=fake_ledger):
            with self.assertRaisesRegex(RuntimeError, "not uniquely preregistered"):
                runner._validate_admission(config, authorization)

    def test_all_four_views_are_authenticated_before_run(self):
        positive = {view: runner.SyntheticTrainFeatures.load("component_positive", view) for view in VIEWS}
        negative = {view: runner.SyntheticTrainFeatures.load("family_negative", view) for view in VIEWS}
        runner._validate_directional_stores(positive, negative)
        negative["horizontal"].binding["feature_sha256"] = "0" * 64
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            runner._validate_directional_stores(positive, negative)

    def test_journal_refuses_replay_and_incomplete_step(self):
        with tempfile.TemporaryDirectory(prefix="graphreader-journal-") as directory:
            authorization = SimpleNamespace(directory=Path(directory), binding={"synthetic": "fixture"})
            journal = runner.prior._RunJournal.claim(authorization, 2)
            self.assertEqual(journal.begin_step(authorization, 2), 1)
            with self.assertRaises(RuntimeError):
                journal.begin_step(authorization, 2)
            with self.assertRaises(FileExistsError):
                runner.prior._RunJournal.claim(authorization, 2)
            journal.close()


class MockedStepTests(unittest.TestCase):
    def test_journal_precedes_mock_step_and_both_schedules_commit_after_sync(self):
        events = []
        class Loss:
            device = torch.device("cuda:0")
            def __add__(self, other): return self
            def backward(self): events.append("backward_mock")
            def detach(self): return self
            def cpu(self): return self
            def __float__(self): return 0.5
        raw = b"\x00\x01"
        batch = SimpleNamespace(plan=SimpleNamespace(step=1), synthetic=[0, 1], real_positive=[1],
                                labels=SyntheticLabels(raw, sha256(raw).hexdigest()))
        source = MagicMock()
        source.verify_input_binding.return_value = source.verify_model.return_value = source.verify_batch.return_value = True
        source.next_gpu.side_effect = lambda: (events.append("mixed_reserve") or batch)
        source.commit.side_effect = lambda _: events.append("mixed_commit")
        journal = MagicMock(spec=runner.prior._RunJournal)
        journal.begin_step.side_effect = lambda *_: (events.append("journal_intent") or 1)
        journal.commit_step.side_effect = lambda _: events.append("journal_commit")
        component = SimpleNamespace(view="identity", indices=(1, 2), step=1)
        direction = SimpleNamespace(view="identity", component_indices=(1,), family_indices=(2,), step=1)
        component_schedule, directional_schedule = MagicMock(), MagicMock()
        component_schedule.reserve.side_effect = lambda _: (events.append("component_reserve") or component)
        directional_schedule.reserve.side_effect = lambda _: (events.append("direction_reserve") or direction)
        component_schedule.commit.side_effect = lambda _: events.append("component_commit")
        directional_schedule.commit.side_effect = lambda _: events.append("direction_commit")
        optimizer, model = MagicMock(), MagicMock()
        optimizer.step.side_effect = lambda: events.append("optimizer_mock")
        config = {"real_positive_binding_sha256": "1" * 64}
        with patch.object(runner.prior, "_require_bound_source", return_value=config), \
             patch.object(runner, "_validate_admission"), patch.object(runner, "_validate_directional_stores"), \
             patch.object(runner.prior, "_require_reviewed_schedule"), patch.object(runner.prior, "require_gpu_model"), \
             patch.object(runner.prior, "_require_optimizer"), \
             patch.object(runner, "preservation_loss", return_value=SimpleNamespace(total=Loss())), \
             patch.object(runner, "directional_penalties", return_value=SimpleNamespace(weighted_total=Loss())), \
             patch.object(runner.torch, "isfinite", return_value=True), \
             patch.object(runner.torch.cuda, "synchronize", side_effect=lambda *_: events.append("synchronize_mock")):
            result = runner._train_step(model, optimizer, source, MagicMock(), journal, component_schedule,
                directional_schedule, {"identity": object()}, {"identity": object()}, {"identity": object()})
        self.assertEqual(result, 0.5)
        self.assertEqual(events, ["journal_intent", "mixed_reserve", "component_reserve", "direction_reserve", "backward_mock",
            "optimizer_mock", "synchronize_mock", "journal_commit", "mixed_commit", "component_commit", "direction_commit"])
        self.assertEqual(source.verify_batch.call_count, 2)


if __name__ == "__main__":
    unittest.main()
