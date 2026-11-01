# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from review_workflow_tick_outliers import outlier_regions


class WorkflowTickOutlierReviewTests(unittest.TestCase):
    def test_selects_only_the_distant_numeric_region(self):
        panel = {"y_axis_line": {"start": {"x": 100, "y": 0}, "end": {"x": 100, "y": 100}},
                 "ocr_tick_regions": [{"region_id": name, "role": "YTick", "numeric_value": value,
                                       "bounds": {"top": 10, "height": 10, "right": right}}
                                      for name, value, right in (("a", 1, 90), ("b", 2, 90), ("c", 3, 50))]}
        self.assertEqual([row["region_id"] for row in outlier_regions(panel)], ["c"])
        panel["ocr_tick_regions"].pop()
        self.assertEqual(outlier_regions(panel), [])


if __name__ == "__main__":
    unittest.main()
