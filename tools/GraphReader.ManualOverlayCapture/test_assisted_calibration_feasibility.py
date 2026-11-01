# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from assisted_calibration_feasibility import derive


class AssistedCalibrationFeasibilityTests(unittest.TestCase):
    def test_rejects_skew_or_ambiguous_panels(self):
        anchors = [
            {"GraphX": 1, "GraphY": 0, "ScreenX": 10, "ScreenY": 90},
            {"GraphX": 1, "GraphY": 100, "ScreenX": 10, "ScreenY": 10},
            {"GraphX": 9, "GraphY": 0, "ScreenX": 90, "ScreenY": 90},
        ]
        panel = {"provenance": {"encoded_crop_source_pixels": {"x": 0, "y": 0, "width": 100, "height": 100}}}
        self.assertEqual("eligible", derive(anchors, [panel]))
        self.assertEqual("ambiguous_or_outside_panel", derive(anchors, [panel, panel]))
        skewed = [dict(item) for item in anchors]
        skewed[1]["ScreenX"] = 15
        self.assertEqual("skew_exceeds_manual_contract", derive(skewed, [panel]))
        reversed_x = [dict(item) for item in anchors]
        reversed_x[2]["ScreenX"] = 5
        self.assertEqual("reversed_axes", derive(reversed_x, [panel]))


if __name__ == "__main__":
    unittest.main()
