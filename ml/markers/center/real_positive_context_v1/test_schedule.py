# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic in-memory schedule tests: no dataset, GPU training or optimizer."""
from __future__ import annotations

from dataclasses import replace
from hashlib import sha256
import json
import unittest

import torch

from .schedule import ALGORITHM, BoundPopulation, FixedSchedule, population_sha256


def _ids(prefix: str, count: int) -> bytes:
    return b"".join(sha256(f"{prefix}-{index}".encode("utf-8")).digest() for index in range(count))


def _fixture(synthetic_count: int = 13, real_count: int = 4, *, seed: int = 71,
             batch: int = 5, positive_batch: int = 2, epochs: int = 3):
    synthetic, real = _ids("synthetic", synthetic_count), _ids("positive", real_count)
    config = {"recipe": {"seed": seed, "epochs": epochs, "synthetic_batch_size": batch,
                         "real_positive_batch_size": positive_batch},
              "optimizer_steps_expected": epochs * ((synthetic_count + batch - 1) // batch),
              "synthetic_feature_report_sha256": "a" * 64, "real_positive_binding_sha256": "b" * 64,
              "schedule": {"algorithm": ALGORITHM, "torch_version": str(torch.__version__),
                           "synthetic_population_sha256": population_sha256("synthetic", synthetic),
                           "real_positive_population_sha256": population_sha256("real-positive", real),
                           "synthetic_population_count": synthetic_count,
                           "real_positive_population_count": real_count,
                           "synthetic_source_binding_sha256": "a" * 64,
                           "real_source_binding_sha256": "b" * 64}}
    return config, synthetic, real


def _schedule(config: dict, synthetic: bytes, real: bytes) -> FixedSchedule:
    raw = json.dumps(config, sort_keys=True).encode("utf-8")
    return FixedSchedule.from_bound_config(raw, sha256(raw).hexdigest(),
        synthetic_ids=synthetic, real_positive_ids=real)


def _row(blob: bytes, index: int) -> bytes:
    return blob[index * 32:(index + 1) * 32]


def _execute(config: dict, synthetic: bytes, real: bytes):
    schedule = _schedule(config, synthetic, real)
    plans = []
    while True:
        try:
            plan = schedule.reserve()
        except StopIteration:
            break
        schedule.verify(plan, synthetic_row_ids=[_row(synthetic, index) for index in plan.synthetic_indices],
                        real_positive_row_ids=[_row(real, index) for index in plan.real_positive_indices])
        schedule.commit(plan)
        plans.append(plan)
    return schedule, plans


class ScheduleTests(unittest.TestCase):
    def test_fixed_seed_and_complete_per_epoch_coverage(self):
        config, synthetic, real = _fixture()
        result, first = _execute(config, synthetic, real)
        _, second = _execute(config, synthetic, real)
        self.assertEqual(first, second)
        self.assertEqual(result.total_steps, 9)
        self.assertEqual(result.completed_steps, 9)
        for epoch in range(1, 4):
            steps = [plan for plan in first if plan.epoch == epoch]
            self.assertEqual([plan.batch_in_epoch for plan in steps], [1, 2, 3])
            all_synthetic = [item for plan in steps for item in plan.synthetic_indices]
            all_positive = [item for plan in steps for item in plan.real_positive_indices]
            self.assertEqual(sorted(all_synthetic), list(range(13)))
            self.assertEqual(set(all_positive), set(range(4)))
            self.assertEqual(len(all_positive), 6)
            self.assertLessEqual(max(all_positive.count(index) for index in range(4)), 2)
        other, _, _ = _fixture(seed=72)
        _, changed = _execute(other, synthetic, real)
        self.assertNotEqual(first, changed)

    def test_population_hash_count_uniqueness_and_disjointness(self):
        config, synthetic, real = _fixture()
        with self.assertRaises(ValueError):
            _schedule(config, synthetic[:-1] + b"x", real)
        duplicate = _row(synthetic, 0) * 13
        config["schedule"]["synthetic_population_sha256"] = population_sha256("synthetic", duplicate)
        with self.assertRaisesRegex(ValueError, "duplicate"):
            _schedule(config, duplicate, real)
        with self.assertRaises(ValueError):
            BoundPopulation("synthetic", synthetic, "0" * 64, 13)
        config, synthetic, _ = _fixture()
        overlap = _row(synthetic, 0) + _ids("positive", 3)
        config["schedule"]["real_positive_population_sha256"] = population_sha256("real-positive", overlap)
        with self.assertRaisesRegex(ValueError, "overlap"):
            _schedule(config, synthetic, overlap)

    def test_unbound_or_ambiguous_config_is_rejected(self):
        config, synthetic, real = _fixture()
        raw = json.dumps(config).encode("utf-8")
        with self.assertRaisesRegex(ValueError, "hash"):
            FixedSchedule.from_bound_config(raw, "0" * 64, synthetic_ids=synthetic, real_positive_ids=real)
        duplicate = b'{"schedule":{},"schedule":{}}'
        with self.assertRaises(ValueError):
            FixedSchedule.from_bound_config(duplicate, sha256(duplicate).hexdigest(),
                                             synthetic_ids=synthetic, real_positive_ids=real)
        for mutation in (lambda c: c.update(optimizer_steps_expected=8),
                         lambda c: c["recipe"].update(seed=True),
                         lambda c: c["schedule"].update(torch_version="other"),
                         lambda c: c["schedule"].update(real_source_binding_sha256="c" * 64),
                         lambda c: c["schedule"].update(synthetic_population_count=True)):
            candidate = json.loads(json.dumps(config))
            mutation(candidate)
            with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                _schedule(candidate, synthetic, real)

    def test_insufficient_positive_capacity_is_rejected(self):
        config, synthetic, real = _fixture(real_count=7)
        with self.assertRaisesRegex(ValueError, "cannot cover"):
            _schedule(config, synthetic, real)

    def test_omitted_reordered_or_wrong_positive_rows_poison_run(self):
        for mode in ("omitted", "reordered", "wrong_positive"):
            config, synthetic, real = _fixture()
            schedule = _schedule(config, synthetic, real)
            plan = schedule.reserve()
            s = [_row(synthetic, index) for index in plan.synthetic_indices]
            r = [_row(real, index) for index in plan.real_positive_indices]
            if mode == "omitted":
                s.pop()
            elif mode == "reordered":
                s[0], s[1] = s[1], s[0]
            else:
                r[0] = sha256(b"foreign").digest()
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                schedule.verify(plan, synthetic_row_ids=s, real_positive_row_ids=r)
            with self.assertRaisesRegex(RuntimeError, "cannot continue"):
                schedule.reserve()

    def test_repeat_skip_and_unverified_commit_are_rejected(self):
        config, synthetic, real = _fixture()
        schedule = _schedule(config, synthetic, real)
        plan = schedule.reserve()
        with self.assertRaisesRegex(RuntimeError, "must be verified"):
            schedule.reserve()
        with self.assertRaises(RuntimeError):
            schedule.commit(plan)

        schedule = _schedule(config, synthetic, real)
        plan = schedule.reserve()
        with self.assertRaisesRegex(RuntimeError, "unverified"):
            schedule.commit(plan)

        schedule = _schedule(config, synthetic, real)
        plan = schedule.reserve()
        with self.assertRaisesRegex(RuntimeError, "repeated"):
            schedule.verify(replace(plan), synthetic_row_ids=[], real_positive_row_ids=[])

        schedule = _schedule(config, synthetic, real)
        with self.assertRaisesRegex(RuntimeError, "not reserved"):
            schedule.verify(None, synthetic_row_ids=[], real_positive_row_ids=[])
        with self.assertRaisesRegex(RuntimeError, "cannot continue"):
            schedule.reserve()


if __name__ == "__main__":
    unittest.main()
