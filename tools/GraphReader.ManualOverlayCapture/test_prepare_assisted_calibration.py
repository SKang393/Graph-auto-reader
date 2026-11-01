# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from prepare_assisted_calibration import prepare


class AssistedCalibrationBindingTests(unittest.TestCase):
    def test_panel_coordinates_and_ineligible_skew(self):
        sample = {"Anchors": [
            {"GraphX": 1, "GraphY": 0, "ScreenX": 20, "ScreenY": 100},
            {"GraphX": 1, "GraphY": 100, "ScreenX": 20, "ScreenY": 20},
            {"GraphX": 9, "GraphY": 0, "ScreenX": 100, "ScreenY": 100},
        ]}
        panel = {"panel_id": "panel", "panel_sha256": "panel-hash",
                 "provenance": {"encoded_crop_source_pixels": {"x": 10, "y": 10, "width": 100, "height": 100}}}
        captured = {"image_sha256": "source-hash", "panels": [panel]}
        result = prepare(sample, {"source_index": 0}, captured)
        self.assertIsNotNone(result)
        self.assertEqual([10, 90], result["origin"])
        self.assertEqual([10, 10], result["y_anchor"])
        self.assertEqual([90, 90], result["x_anchor"])
        sample["Anchors"][1]["ScreenX"] = 30
        self.assertIsNone(prepare(sample, {"source_index": 0}, captured))


if __name__ == "__main__":
    unittest.main()
