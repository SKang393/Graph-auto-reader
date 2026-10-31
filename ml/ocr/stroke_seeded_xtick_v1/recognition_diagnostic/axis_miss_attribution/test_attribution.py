# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from run_attribution import distance_point_infinite_line, eligibility


class AttributionGateTests(unittest.TestCase):
    def setUp(self):
        self.pair = {
            "x_axis_line": [0, 50, 100, 50],
            "intersection": [0, 50],
            "x_span": 100,
            "y_span": 100,
        }

    def test_segment_distance_is_to_infinite_axis(self):
        self.assertEqual(distance_point_infinite_line(120, 54, self.pair["x_axis_line"]), 4)

    def test_vertical_candidate_passes_tick_geometry(self):
        c = {"candidate_id": "near", "line": [40, 40, 40, 48], "selected_axis_member": False}
        result, detail = eligibility(c, self.pair, (120, 120), set())
        self.assertEqual(result, "passed_x_tick_eligibility")
        self.assertEqual(detail["distance_to_selected_x_axis_px"], 6)

    def test_candidate_outside_selected_axis_band_is_explained(self):
        c = {"candidate_id": "far", "line": [40, 33, 40, 41], "selected_axis_member": False}
        result, _ = eligibility(c, self.pair, (120, 120), set())
        self.assertEqual(result, "too_far_from_selected_x_axis")

    def test_phase_divider_membership_precedes_tick_gates(self):
        c = {"candidate_id": "divider", "line": [40, 40, 40, 48], "selected_axis_member": False}
        result, _ = eligibility(c, self.pair, (120, 120), {"divider"})
        self.assertEqual(result, "excluded_as_phase_divider")


if __name__ == "__main__":
    unittest.main()
