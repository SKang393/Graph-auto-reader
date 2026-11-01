# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

import numpy as np

from .mask_overlap_diagnostic import mask_overlap


class MaskOverlapDiagnosticTests(unittest.TestCase):
    def test_center_overlap_and_bounds(self):
        masks = np.zeros((2, 3, 4), dtype=np.float32)
        masks[0, 1, 2] = .5
        masks[1, 1, 2] = 1
        self.assertEqual((True, True), mask_overlap(masks, 2, 1))
        self.assertEqual((False, False), mask_overlap(masks, 0, 0))
        with self.assertRaises(ValueError):
            mask_overlap(masks, -1, 0)
        with self.assertRaises(ValueError):
            mask_overlap(np.full((2, 3, 4), np.nan), 0, 0)


if __name__ == "__main__":
    unittest.main()
