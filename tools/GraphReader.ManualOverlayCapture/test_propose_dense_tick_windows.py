# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from propose_dense_tick_windows import clustered_positions, measured_pitch, windows


class DenseTickWindowTests(unittest.TestCase):
    def test_duplicate_strokes_collapse_without_chaining_across_a_tick(self):
        self.assertEqual([11.5, 19], clustered_positions([10, 13, 19]))

    def test_pitch_uses_repeated_measured_gaps_with_missing_strokes(self):
        self.assertAlmostEqual(20, measured_pitch([0, 20, 40, 80, 100, 160]))

    def test_insufficient_repeated_spacing_emits_no_windows(self):
        self.assertIsNone(measured_pitch([10, 20, 55]))
        self.assertEqual([], windows([10, 20, 55], {"left": 0, "top": 20, "right": 90, "bottom": 30}))

    def test_windows_stay_in_lane_and_reject_clipped_edges(self):
        result = windows([10, 30, 50, 70, 90], {"left": 20, "top": 100, "right": 80, "bottom": 110})
        self.assertEqual([[21, 100, 39, 110], [41, 100, 59, 110], [61, 100, 79, 110]], result)
        self.assertTrue(all(left[2] < right[0] for left, right in zip(result, result[1:])))


if __name__ == "__main__":
    unittest.main()
