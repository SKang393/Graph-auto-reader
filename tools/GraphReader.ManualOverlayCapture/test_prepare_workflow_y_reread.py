# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from prepare_workflow_y_reread import padded_inside_panel


class WorkflowYRereadInputTests(unittest.TestCase):
    def test_requires_full_padding_inside_the_panel(self):
        crop = {"width": 100, "height": 80}
        box = {"left": 1, "top": 1, "right": 99, "bottom": 79, "width": 98, "height": 78}
        self.assertTrue(padded_inside_panel(box, crop))
        box["left"] = 0.9
        self.assertFalse(padded_inside_panel(box, crop))
        box["left"], box["bottom"] = 1, 79.1
        self.assertFalse(padded_inside_panel(box, crop))


if __name__ == "__main__":
    unittest.main()
