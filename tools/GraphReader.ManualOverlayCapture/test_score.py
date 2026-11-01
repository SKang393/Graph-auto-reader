# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score import assess


def panel(x, y, width, height, markers):
    return {"provenance": {"encoded_crop_source_pixels": {"x": x, "y": y, "width": width, "height": height},
                           "panel_to_source_matrix": [1, 0, x, 0, 1, y, 0, 0, 1]},
            "markers": [{"center": {"x": point[0], "y": point[1]}} for point in markers],
            "axis_needs_review": False}


class OverlayScoreTests(unittest.TestCase):
    def test_transformed_centers_only_count_in_the_controlled_patch(self):
        value = assess([panel(10, 20, 200, 150, [(50, 40), (52, 40), (150, 80)])], [40, 40, 80, 80])
        self.assertEqual("scored", value["status"])
        self.assertEqual(2, value["nearby_centers"])
        self.assertEqual(2, value["centers_in_patch"])

    def test_ambiguous_or_clipped_patch_is_not_scored(self):
        self.assertEqual("ambiguous_or_clipped_patch", assess([panel(0, 0, 50, 50, [])], [40, 40, 80, 80])["status"])
        self.assertEqual("ambiguous_or_clipped_patch", assess([panel(0, 0, 100, 100, []), panel(0, 0, 100, 100, [])], [40, 40, 80, 80])["status"])

    def test_changed_transform_is_rejected(self):
        value = panel(0, 0, 100, 100, [])
        value["provenance"]["panel_to_source_matrix"][2] = 2
        with self.assertRaises(ValueError):
            assess([value], [40, 40, 80, 80])


if __name__ == "__main__":
    unittest.main()
