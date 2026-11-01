# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import hashlib
import unittest

import numpy as np

from materialize_controlled_dataset import validate_rows


def row(partition, image, group, label, x):
    patch = np.zeros((3, 33, 33), dtype=np.float32)
    patch[0, 16, 16] = 1
    return {"partition": partition, "image": image, "group": group * 64, "label": label,
            "x": x, "y": 4, "patch": patch, "sha256": hashlib.sha256(patch.tobytes()).hexdigest()}


def rows():
    return [row("internal_train_build", "a", "a", 1, 4), row("internal_train_build", "b", "a", 0, 8),
            row("internal_train_holdout", "c", "b", 1, 4), row("internal_train_holdout", "d", "b", 0, 8)]


class ControlledDatasetTests(unittest.TestCase):
    def test_accepts_source_grouped_controlled_examples(self):
        validate_rows(rows())

    def test_rejects_duplicate_proposals_and_group_leakage(self):
        value = rows()
        with self.assertRaisesRegex(ValueError, "duplicate proposal"):
            validate_rows(value + [value[0]])
        value[2]["group"] = "a" * 64
        with self.assertRaisesRegex(ValueError, "cross partitions"):
            validate_rows(value)

    def test_rejects_unknown_label_and_tensor_hash_change(self):
        value = rows()
        value[0]["label"] = 2
        with self.assertRaisesRegex(ValueError, "invalid controlled label"):
            validate_rows(value)
        value = rows()
        value[0]["patch"][0, 0, 0] = 1
        with self.assertRaisesRegex(ValueError, "tensor or group identity"):
            validate_rows(value)


if __name__ == "__main__":
    unittest.main()
