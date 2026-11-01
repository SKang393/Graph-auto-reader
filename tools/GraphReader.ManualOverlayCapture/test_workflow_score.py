# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from test_score import panel
from workflow_score import geometry, score_workflow_image


class WorkflowScoreTests(unittest.TestCase):
    def test_failed_workflow_retains_observed_marker_stage(self):
        value = panel(0, 0, 100, 100, [(20, 20)])
        value.update(status="failed", marker_stage_reached=True,
                     centers_before_classification=[{"center": {"x": 10, "y": 10}},
                                                    {"center": {"x": 20, "y": 20}}],
                     classified_plot=[{}, {}], workflow_points=[])
        result = score_workflow_image([{"x": 10, "y": 10}, {"x": 20, "y": 20}], [value])
        self.assertEqual(2, result["unique_recorded_points"])
        self.assertEqual(2, result["center_matches"])
        self.assertEqual(1, result["accepted_matches"])
        self.assertEqual(1, result["marker_stage_panels"])
        self.assertEqual(0, result["completed_panels"])

    def test_failed_import_keeps_unavailable_points_in_denominator(self):
        result = score_workflow_image([{"x": 10, "y": 10}, {"x": 10, "y": 10},
                                       {"x": 20, "y": 20}], [])
        self.assertEqual(2, result["unique_recorded_points"])
        self.assertEqual(2, result["unavailable_points"])
        self.assertEqual(0, result["accepted_matches"])

    def test_center_identity_compares_geometry_not_generated_id(self):
        marker = {"marker_id": "first", "center": {"x": 10, "y": 20}, "radius": 3,
                  "center_confidence": 0.5, "artifact_probability": 0.1}
        self.assertEqual(geometry([marker]), geometry([{**marker, "marker_id": "second"}]))
        self.assertNotEqual(geometry([marker]), geometry([{**marker, "radius": 4}]))


if __name__ == "__main__":
    unittest.main()
