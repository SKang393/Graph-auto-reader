# SPDX-License-Identifier: Apache-2.0
"""In-memory synthetic fixtures only; no model, GPU transfer or training."""
from hashlib import sha256
import json
import struct
import unittest

import torch

from .mixed_source import FamilyRows, MixedSourceBatchAdapter
from .objective import SyntheticLabels
from .schedule import ALGORITHM, FixedSchedule, population_sha256


class _FixtureArchive:
    def __init__(self):
        self._rows = [struct.pack("<3267f", *([value] * 3267)) for value in (0.25, 0.75)]
        self.row_identities = b"".join(sha256(b"synthetic-positive" + bytes([i])).digest() for i in range(2))
        self.count = 2

    def read_patch(self, index):
        return self._rows[index]


def _inputs():
    contexts = torch.arange(2 * 3 * 65 * 65, dtype=torch.float32).reshape(2, 3, 65, 65) / (2 * 3 * 65 * 65)
    labels = torch.tensor([0.0, 1.0], dtype=torch.float32)
    bound = SyntheticLabels(b"\x00\x01", sha256(b"\x00\x01").hexdigest())
    return contexts, labels, bound


def _setup():
    contexts, labels, bound = _inputs()
    family = FamilyRows(contexts, labels, bound)
    archive = _FixtureArchive()
    config = {"recipe": {"seed": 71, "epochs": 2, "synthetic_batch_size": 3, "real_positive_batch_size": 1},
              "optimizer_steps_expected": 6, "synthetic_feature_report_sha256": "a" * 64,
              "real_positive_binding_sha256": "b" * 64,
              "schedule": {"algorithm": ALGORITHM, "torch_version": str(torch.__version__),
                  "synthetic_population_sha256": population_sha256("synthetic", family.row_identities),
                  "real_positive_population_sha256": population_sha256("real-positive", archive.row_identities),
                  "synthetic_population_count": family.count, "real_positive_population_count": archive.count,
                  "synthetic_source_binding_sha256": "a" * 64, "real_source_binding_sha256": "b" * 64}}
    raw = json.dumps(config, sort_keys=True).encode()
    schedule = FixedSchedule.from_bound_config(raw, sha256(raw).hexdigest(),
                  synthetic_ids=family.row_identities, real_positive_ids=archive.row_identities)
    adapter = MixedSourceBatchAdapter._synthetic_fixture(family, archive, schedule)
    return contexts, family, archive, adapter


class MixedSourceTests(unittest.TestCase):
    def test_four_reflections_labels_and_complete_schedule(self):
        contexts, family, _, adapter = _setup()
        seen = {1: [], 2: []}
        for _ in range(6):
            batch = adapter._prepare_cpu()
            seen[batch.plan.epoch].extend(batch.plan.synthetic_indices)
            for item, index in enumerate(batch.plan.synthetic_indices):
                view, row = divmod(index, 2)
                expected = contexts[row, :, 16:49, 16:49]
                dims = ((), (-1,), (-2,), (-2, -1))[view]
                if dims:
                    expected = expected.flip(dims)
                self.assertTrue(torch.equal(batch.synthetic[item], expected))
                self.assertEqual(batch.labels.values[item], row)
            self.assertEqual(tuple(batch.real_positive.shape), (1, 3, 33, 33))
            adapter.commit(batch.plan)
        self.assertEqual(sorted(seen[1]), list(range(family.count)))
        self.assertEqual(sorted(seen[2]), list(range(family.count)))
        with self.assertRaises(StopIteration):
            adapter._prepare_cpu()

    def test_byte_comparison_and_invalid_inputs(self):
        contexts, labels, _ = _inputs()
        wrong = SyntheticLabels(b"\x01\x00", sha256(b"\x01\x00").hexdigest())
        with self.assertRaisesRegex(ValueError, "byte-for-byte"):
            FamilyRows(contexts, labels, wrong)
        contexts[0, 0, 0, 0] = float("nan")
        correct = SyntheticLabels(b"\x00\x01", sha256(b"\x00\x01").hexdigest())
        with self.assertRaises(ValueError):
            FamilyRows(contexts, labels, correct)

    def test_changed_pixels_or_wrong_population_poison_run(self):
        _, family, _, adapter = _setup()
        family._patches.add_(0.01)
        with self.assertRaisesRegex(ValueError, "identities"):
            adapter._prepare_cpu()
        with self.assertRaisesRegex(RuntimeError, "not been committed"):
            adapter._prepare_cpu()

    def test_no_repeat_or_fixture_gpu_transfer(self):
        _, _, _, adapter = _setup()
        with self.assertRaisesRegex(RuntimeError, "fixtures"):
            adapter.next_gpu()
        batch = adapter._prepare_cpu()
        with self.assertRaisesRegex(RuntimeError, "not been committed"):
            adapter._prepare_cpu()
        adapter.commit(batch.plan)
        with self.assertRaisesRegex(RuntimeError, "current completed"):
            adapter.commit(batch.plan)


if __name__ == "__main__":
    unittest.main()
