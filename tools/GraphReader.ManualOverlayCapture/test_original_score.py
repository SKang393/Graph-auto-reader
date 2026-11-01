# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from original_score import anchor_plot_status
from test_score import panel


class OriginalScoreTests(unittest.TestCase):
    def test_anchor_uses_the_single_matching_panel_and_plot_margin(self):
        value = panel(10, 20, 100, 100, [])
        value["stage_file"] = "fixture"
        stages = {"fixture": {"plot": [{"x": 20, "y": 20}, {"x": 80, "y": 20},
                                        {"x": 80, "y": 80}, {"x": 20, "y": 80}]}}
        self.assertEqual("inside_plot_plus_2px", anchor_plot_status([value], stages, 30, 40))
        self.assertEqual("outside_plot_plus_16px", anchor_plot_status([value], stages, 11, 21))
        self.assertEqual("ambiguous_or_missing_panel", anchor_plot_status([value, value], stages, 30, 40))


if __name__ == "__main__":
    unittest.main()
