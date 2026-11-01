# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from residual_axis_train_probe import axis_error


class AnchorLineDistanceTests(unittest.TestCase):
    def test_horizontal_line_measures_both_anchors_in_panel_units(self):
        line = {"Start": {"X": 0, "Y": 10}, "End": {"X": 20, "Y": 10}}
        self.assertAlmostEqual(0.1, axis_error(line, [[1, 8], [8, 12]], 20))

    def test_sloped_line_accepts_points_on_its_infinite_extension(self):
        line = {"Start": {"X": 0, "Y": 0}, "End": {"X": 2, "Y": 2}}
        self.assertEqual(0.0, axis_error(line, [[3, 3], [-1, -1]], 10))

    def test_rejects_degenerate_and_nonfinite_inputs(self):
        line = {"Start": {"X": 0, "Y": 0}, "End": {"X": 0, "Y": 0}}
        with self.assertRaises(ValueError):
            axis_error(line, [[0, 0], [1, 1]], 10)
        line["End"]["X"] = 1
        with self.assertRaises(ValueError):
            axis_error(line, [[0, 0], [1, float("nan")]], 10)


if __name__ == "__main__":
    unittest.main()
