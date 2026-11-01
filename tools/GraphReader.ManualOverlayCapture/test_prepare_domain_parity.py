# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import hashlib
import unittest

import numpy as np

from patches import proposal_patch
from prepare_domain_parity import selected_patches


class PrepareDomainParityTests(unittest.TestCase):
    def test_only_emitted_controlled_patches_are_hashed(self):
        planes = np.zeros((3, 40, 40), dtype=np.float32)
        planes[0] = 1
        planes[0, 20, 20] = 0
        self.assertEqual([], selected_patches(planes, 20, 20, (0, 0, 40, 40), set()))
        selected = selected_patches(planes, 20, 20, (0, 0, 40, 40), {(20, 20)})
        self.assertEqual(1, len(selected))
        self.assertEqual(hashlib.sha256(proposal_patch(planes, 20, 20).tobytes()).hexdigest(),
                         selected[0]["python_patch_sha256"])

    def test_rejects_emitted_position_without_ink(self):
        planes = np.zeros((3, 40, 40), dtype=np.float32)
        planes[0] = 1
        with self.assertRaisesRegex(ValueError, "lacks mirrored ink support"):
            selected_patches(planes, 20, 20, (0, 0, 40, 40), {(20, 20)})


if __name__ == "__main__":
    unittest.main()
