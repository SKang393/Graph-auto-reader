# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import unittest

from score_adjacent_ink import WARNING, summarize
from test_score_workflow_tick_details import fixture


class AdjacentInkScoreTests(unittest.TestCase):
    def test_preserves_previous_reasons_and_counts_partial_crop(self):
        old = fixture()
        new = copy.deepcopy(old)
        new["rows"][0]["panels"][0]["error"] = "review | " + WARNING + "r1"
        manifest = [{"id": "a", "panel_id": "0", "region_id": "r1"}]
        labels = [{"id": "a", "classification": "numeric_fragment"}]
        score = summarize(old, new, manifest, labels)
        self.assertEqual(score["workflow"]["same_other_reasons"], 21)
        self.assertEqual(score["visual_categories"]["numeric_fragment"]["adjacent_warning"], 1)
        self.assertEqual(score["visual_categories"]["numeric_fragment"]["new_region_warning"], 1)

    def test_mismatched_review_inventory_is_rejected(self):
        with self.assertRaises(ValueError):
            summarize(fixture(), fixture(), [{"id": "a"}], [])


if __name__ == "__main__":
    unittest.main()
