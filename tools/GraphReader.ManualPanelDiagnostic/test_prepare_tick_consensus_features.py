# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from prepare_tick_consensus_features import normalized_gap


class TickConsensusFeatureTests(unittest.TestCase):
    def test_gap_uses_the_axis_interpolated_at_crop_center(self):
        self.assertAlmostEqual(normalized_gap([4, 5, 8, 15], [20, 0, 30, 20]), 1.7)

    def test_empty_box_and_horizontal_axis_are_rejected(self):
        with self.assertRaises(ValueError):
            normalized_gap([4, 5, 8, 5], [20, 0, 30, 20])
        with self.assertRaises(ValueError):
            normalized_gap([4, 5, 8, 15], [20, 0, 30, 0])


if __name__ == "__main__":
    unittest.main()
