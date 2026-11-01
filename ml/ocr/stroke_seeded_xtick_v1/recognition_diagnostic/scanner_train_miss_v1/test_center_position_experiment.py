# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import center_position_experiment as exp


class CenterPositionExperimentTests(unittest.TestCase):
    def test_component_split_is_deterministic_and_source_disjoint(self):
        components = [[f"g{i}-a", f"g{i}-b"] for i in range(6)]
        counts = {source: 5 for group in components for source in group}
        first = exp.freeze_component_split(components, [s for g in components for s in g], counts, {"old-validation"})
        second = exp.freeze_component_split(components, [s for g in components for s in g], counts, {"old-validation"})
        self.assertEqual(first, second)
        train, validation, groups = first
        self.assertEqual(4, len(groups))
        self.assertFalse(set(train) & set(validation))
        self.assertFalse(set(validation) & {"old-validation"})
        self.assertEqual(set(s for group in groups for s in group), set(validation))

    def test_translation_is_fitted_from_pairs_and_applied_without_truth(self):
        proposals = []
        truths = []
        for index in range(20):
            x = float(index * 20)
            proposals.append({"center": [x, 10.0], "line": [x, 8.0, x, 12.0],
                              "cluster_width_pixels": 2, "dark_pixel_count": 4, "longest_dark_run": 2})
            truths.append({"stroke_center_original_xy": [x + 2.0, 13.0],
                           "stroke_line_original_xyxy": [x + 2.0, 11.0, x + 2.0, 15.0]})
        outputs = [{"source_id": "train", "panels": [{"panel_id": "p", "ink_proposals": proposals}]}]
        calibration = exp.calibrate_translation(outputs, {("train", "p"): truths}, ["train"])
        self.assertEqual((2.0, 3.0), (calibration["dx_px"], calibration["dy_px"]))
        moved = exp.shifted(proposals, calibration["dx_px"], calibration["dy_px"])
        self.assertEqual([2.0, 13.0], moved[0]["center"])
        self.assertEqual([2.0, 11.0, 2.0, 15.0], moved[0]["line"])
        self.assertEqual([0.0, 10.0], proposals[0]["center"])

    def test_score_reports_false_proposals_and_no_visible_panels(self):
        outputs = [{"source_id": "v", "panels": [
            {"panel_id": "positive", "ink_proposals": [
                {"center": [10.0, 10.0], "line": [10, 8, 10, 12]},
                {"center": [80.0, 10.0], "line": [80, 8, 80, 12]}]},
            {"panel_id": "blank", "ink_proposals": [
                {"center": [30.0, 30.0], "line": [30, 28, 30, 32]}]},
        ]}]
        truths = {("v", "positive"): [{"stroke_center_original_xy": [10.0, 10.0],
                    "stroke_line_original_xyxy": [10, 8, 10, 12], "visible": True,
                    "ground_truth_target_unambiguous": True}]}
        result = exp.score(outputs, truths, ["v"], (0.0, 0.0))
        self.assertEqual(1, result["true_matches"])
        self.assertEqual(2, result["false_proposals"])
        self.assertEqual(1, result["proposals_on_no_visible_truth_panels"])


if __name__ == "__main__":
    unittest.main()
