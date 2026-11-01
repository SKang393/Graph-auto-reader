# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import sys
import unittest

from propose_dense_tick_windows import ROOT

sys.path.insert(0, str(ROOT / "tools/GraphReader.ManualPanelDiagnostic"))
from test_score_dev_ocr import reading  # noqa: E402
from score_workflow_y_reread import summarize  # noqa: E402


class WorkflowYRereadScoreTests(unittest.TestCase):
    def test_counts_recovery_regression_and_exclusion(self):
        report = {"rows": [{"panels": [{"panel_id": "p", "ocr_tick_regions": [
            {"region_id": "r1", "text": "80", "numeric_value": 80},
            {"region_id": "r2", "text": "20", "numeric_value": 20},
            {"region_id": "r3", "text": "1", "numeric_value": 1}]}]}]}
        manifest = [{"id": name, "panel_id": "p", "region_id": region}
                    for name, region in (("a", "r1"), ("b", "r2"), ("c", "r3"), ("d", "r1"))]
        labels = [{"id": "a", "classification": "numeric_tick", "expected_text": "90"},
                  {"id": "b", "classification": "numeric_tick", "expected_text": "20"},
                  {"id": "c", "classification": "other_text", "expected_text": None},
                  {"id": "d", "classification": "numeric_tick", "expected_text": "90"}]
        padded = [{"id": "a", "server": reading("90", [("90", 90, .9)])},
                  {"id": "b", "server": reading("30", [("30", 30, .9)])},
                  {"id": "c", "server": reading("1", [("1", 1, .9)])}]
        tight = [{"id": "a", "server": reading("80", [("80", 80, .9)])},
                 {"id": "b", "server": reading("20", [("20", 20, .9)])},
                 {"id": "c", "server": reading("x", [("x", None, .9)])}]
        score = summarize(report, manifest, labels, padded, tight)
        self.assertEqual(score["numeric_tick"]["excluded"], 1)
        self.assertEqual(score["numeric_tick"]["padded_recovers_workflow_error"], 1)
        self.assertEqual(score["numeric_tick"]["padded_regresses_workflow_correct"], 1)
        self.assertEqual(score["other_text"]["padded_numeric_primary"], 1)
        self.assertEqual(score["other_text"]["tight_numeric_primary"], 0)

    def test_mismatched_prediction_ids_are_rejected(self):
        with self.assertRaises(ValueError):
            summarize({"rows": []}, [], [], [{"id": "a", "server": {}}], [])


if __name__ == "__main__":
    unittest.main()
