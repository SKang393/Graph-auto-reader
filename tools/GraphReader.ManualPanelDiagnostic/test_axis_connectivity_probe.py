# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import numpy as np

from axis_connectivity_probe import connected_to_axes
from axis_connectivity_score import summarize


class AxisConnectivityTests(unittest.TestCase):
    def test_axis_connected_strokes_and_detached_glyphs_are_distinct(self):
        image = np.full((100, 100), 255, dtype=np.uint8)
        image[10:91, 60] = 0
        image[90, 60:96] = 0
        image[45:48, 52:61] = 0
        image[70:77, 48:52] = 0
        image[82:91, 80:83] = 0
        axes = ((60, 90), (60, 10), (95, 90))
        self.assertEqual((True, False), connected_to_axes(image, [52, 45, 60, 48], axes, 128))
        self.assertEqual((False, False), connected_to_axes(image, [48, 70, 52, 77], axes, 128))
        self.assertEqual((False, True), connected_to_axes(image, [80, 82, 83, 90], axes, 128))

    def test_invalid_bounds_are_rejected(self):
        with self.assertRaises(ValueError):
            connected_to_axes(np.zeros((10, 10), dtype=np.uint8), [-1, 0, 5, 5],
                              ((2, 8), (2, 1), (8, 8)), 128)

    def test_scoring_keeps_unmeasured_cases_in_the_denominator(self):
        features = {"a": {"status": "measured", "dark_vertical": True, "dark_horizontal": False,
                          "faint_vertical": True, "faint_horizontal": True},
                    "b": {"status": "ambiguous_panel"}}
        self.assertEqual({"items": 2, "unmeasured": 1, "dark_vertical": 1, "dark_horizontal": 0,
                          "dark_either": 1, "faint_vertical": 1, "faint_horizontal": 1, "faint_either": 1},
                         summarize(features, {"a", "b"}))
        with self.assertRaises(ValueError):
            summarize(features, {"missing"})


if __name__ == "__main__":
    unittest.main()
