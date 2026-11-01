# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import unittest

from score_outlying_lane import WARNING, summarize
from test_score_workflow_tick_details import fixture


class OutlyingLaneScoreTests(unittest.TestCase):
    def test_new_warning_preserves_old_reasons_and_marks_reviewed_region(self):
        old = fixture()
        new = copy.deepcopy(old)
        new["rows"][0]["panels"][0]["error"] = "Automatic calibration is incomplete: " + WARNING + "r1 | review"
        result = summarize(old, new, [{"panel_id": "0", "region_id": "r1"}])
        self.assertEqual(result["same_other_reasons"], 21)
        self.assertEqual(result["lane_warning_count"], 1)
        self.assertEqual(result["reviewed_fragments_with_lane_warning"], 1)
        self.assertEqual(result["reviewed_fragments_without_prior_region_warning"], 1)

    def test_missing_reviewed_panel_is_rejected(self):
        with self.assertRaises(ValueError):
            summarize(fixture(), fixture(), [{"panel_id": "absent", "region_id": "r1"}])


if __name__ == "__main__":
    unittest.main()
