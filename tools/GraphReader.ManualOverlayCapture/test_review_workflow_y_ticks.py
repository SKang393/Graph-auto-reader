# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from review_workflow_y_ticks import source_box


class WorkflowYReviewTests(unittest.TestCase):
    def test_translates_panel_coordinates_without_leaving_panel(self):
        panel = {"provenance": {"encoded_crop_source_pixels": {"x": 100, "y": 200, "right": 150, "bottom": 250}}}
        region = {"bounds": {"left": 3.2, "top": 4.1, "right": 10.7, "bottom": 12.2}}
        self.assertEqual(source_box(panel, region), (103, 204, 111, 213))
        region["bounds"]["right"] = 51
        with self.assertRaises(ValueError):
            source_box(panel, region)


if __name__ == "__main__":
    unittest.main()
