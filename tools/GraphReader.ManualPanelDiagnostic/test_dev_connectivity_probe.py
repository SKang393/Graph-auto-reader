# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import numpy as np

from dev_connectivity_probe import panel_boxes


class DevConnectivityProbeTests(unittest.TestCase):
    def test_proposals_use_the_selected_axis_and_original_coordinates(self):
        image = np.full((120, 160), 255, dtype=np.uint8)
        image[40:49, 80:85] = 0
        crop = {"X": 0, "Y": 0, "Width": 160, "Height": 120}
        selected = {"Intersection": {"X": 100, "Y": 100}, "TopLeft": {"X": 100, "Y": 10},
                    "BottomRight": {"X": 150, "Y": 100}}
        boxes, axes = panel_boxes(image, crop, selected)
        self.assertEqual([[80, 40, 85, 49]], boxes)
        self.assertEqual(((100, 100), (100, 10), (150, 100)), axes)

    def test_out_of_bounds_panel_is_rejected(self):
        with self.assertRaises(ValueError):
            panel_boxes(np.zeros((10, 10), dtype=np.uint8),
                        {"X": 0, "Y": 0, "Width": 20, "Height": 10}, {})


if __name__ == "__main__":
    unittest.main()
