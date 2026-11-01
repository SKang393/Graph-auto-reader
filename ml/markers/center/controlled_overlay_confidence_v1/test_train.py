# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Non-training checks for fixed selection and scheduling."""
import unittest

import numpy as np

from .train import fixed_schedule, validate_build


class ControlledTrainingTests(unittest.TestCase):
    def fixture(self):
        patches = np.zeros((214, 3, 33, 33), dtype=np.float32)
        for index in range(214):
            patches[index, 0, index // 33, index % 33] = 1
        labels = np.zeros(214, dtype=np.uint8)
        labels[:98] = 1
        return patches, labels, np.arange(214) % 10, np.arange(194, dtype=np.int64)

    def test_unique_build_selection(self):
        patches, labels = validate_build(*self.fixture())
        self.assertEqual((194, 3, 33, 33), patches.shape)
        self.assertEqual(98, int(labels.sum()))

    def test_duplicate_ink_or_indices_are_rejected(self):
        patches, labels, groups, indices = self.fixture()
        patches[1, 0] = patches[0, 0]
        with self.assertRaises(ValueError):
            validate_build(patches, labels, groups, indices)
        patches, labels, groups, indices = self.fixture()
        indices[1] = indices[0]
        with self.assertRaises(ValueError):
            validate_build(patches, labels, groups, indices)

    def test_schedule_is_deterministic_and_covers_each_epoch(self):
        first = list(fixed_schedule(194, 32580))
        self.assertEqual(first, list(fixed_schedule(194, 32580)))
        self.assertEqual(28, len(first))
        for epoch in range(4):
            batches = first[epoch * 7:(epoch + 1) * 7]
            self.assertEqual(list(range(194)), sorted(item for batch, _ in batches for item in batch))
            self.assertTrue(all(len(negatives) == len(set(negatives)) == 256 for _, negatives in batches))
        with self.assertRaises(ValueError):
            list(fixed_schedule(193, 32580))


if __name__ == "__main__":
    unittest.main()
