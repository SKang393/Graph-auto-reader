# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

import numpy as np

from .audit import context, marker_masks


class CoverageTests(unittest.TestCase):
    def test_coverage_distinguishes_small_and_large_original_crops(self):
        mask = np.zeros((80, 80), dtype=bool)
        mask[35:46, 39:60] = True
        self.assertEqual("partial", context(mask, 30, 40, 16))
        self.assertEqual("whole", context(mask, 30, 40, 32))
        self.assertEqual("none", context(mask, 0, 0, 16))

    def test_empty_reference_fails_closed(self):
        with self.assertRaises(ValueError):
            context(np.zeros((4, 4), dtype=bool), 1, 1, 1)

    def test_primitive_masks_are_distinct(self):
        scene = {"metadata": {"centers": [(4, 4), (12, 12)], "diameters": [1, 4]},
                 "tensor": np.zeros((3, 20, 20))}

        def mark(draw, center, radius, index, seed):
            draw.ellipse((center[0]-radius, center[1]-radius,
                          center[0]+radius, center[1]+radius), fill=0)

        masks = marker_masks(scene, 0, mark)
        self.assertEqual(2, len(masks))
        self.assertEqual(1, int(masks[0].sum()))
        self.assertFalse(bool(masks[1][4, 4]))
        self.assertTrue(bool(masks[1][12, 12]))


if __name__ == "__main__":
    unittest.main()
