# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Check the paired recognizer report against the prior pinned baseline."""

import hashlib
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[5]
REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-official-recognizer-comparison-v3/report.json"
BASELINE = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-v5-ground-truth-comparison-v2/report.json"
BASELINE_SHA256 = "8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e"


class OfficialComparisonReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))
        baseline = BASELINE.read_bytes()
        if hashlib.sha256(baseline).hexdigest() != BASELINE_SHA256:
            raise ValueError("prior V5 report hash mismatch")
        cls.baseline = json.loads(baseline)["per_score_threshold"]["0.25"]

    def test_v5_reproduces_prior_positive_crop_results(self):
        for current, prior in (
            ("ground_truth_positive", "ground_truth_boxes"),
            ("predicted_positive", "predicted_boxes"),
            ("predicted_positive_iou_ge_0_5", "predicted_boxes_iou_at_least_0_5"),
        ):
            observed = self.report["summaries"][current]
            expected = self.baseline[prior]
            self.assertEqual(observed["count"], expected["sample_count"])
            for key in ("exact", "wrong"):
                self.assertEqual(observed["numeric"][key], expected[key])
            self.assertEqual(observed["numeric"]["empty"], expected["empty_without_alternative"])
            self.assertEqual(observed["numeric"]["failure"], 0)

    def test_outcomes_and_joint_counts_are_accounted(self):
        for name, subset in self.report["summaries"].items():
            background = name == "forced_background"
            for model in ("numeric", "official"):
                result = subset[model]
                total = sum(result[key] for key in ("exact", "wrong", "empty", "failure", "background_with_text"))
                self.assertEqual(total, subset["count"])
                if not background:
                    self.assertEqual(result["background_with_text"], 0)
            self.assertEqual(subset["both_exact"] + subset["only_numeric_exact"], subset["numeric"]["exact"])
            self.assertEqual(subset["both_exact"] + subset["only_official_exact"], subset["official"]["exact"])
            if not background:
                selection = subset["assumed_tick_selection"]
                self.assertEqual(sum(selection[key] for key in ("exact", "wrong", "empty", "failure")), subset["count"])
                self.assertLessEqual(selection["conflicts_numeric_selected_wrong_official_exact"], selection["numeric_value_conflicts"])
                self.assertLessEqual(selection["conflicts_numeric_selected_exact_official_wrong"], selection["numeric_value_conflicts"])

    def test_report_binds_runner_and_inference_scope(self):
        runner = Path(__file__).with_name("Program.cs").read_bytes()
        self.assertEqual(hashlib.sha256(runner).hexdigest(), self.report["runner_source_sha256"])
        self.assertEqual(self.report["provider"], "CPUExecutionProvider")
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["production_approval"])
        self.assertFalse(self.report["production_behavior_changed"])
        self.assertEqual(self.report["sample_count"], 323)
        self.assertEqual(self.report["compared_crop_count"], 488)


if __name__ == "__main__":
    unittest.main()
