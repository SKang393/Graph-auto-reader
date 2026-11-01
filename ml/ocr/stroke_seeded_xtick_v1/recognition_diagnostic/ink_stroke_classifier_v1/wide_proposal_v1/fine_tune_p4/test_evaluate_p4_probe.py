import importlib.util
from pathlib import Path
import unittest

MODULE_PATH = Path(__file__).with_name("evaluate_p4_probe.py")
SPEC = importlib.util.spec_from_file_location("p4_eval_test_module", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PairedProbeMetricsTests(unittest.TestCase):
    def test_common_subset_preserves_rows_and_requires_both_eligibility(self):
        rows = [{"proposal_id": f"p{i}", "row": i} for i in range(4)]
        selected = MODULE.common_eligible_rows(rows, {"p0", "p1", "p3"}, {"p0", "p2", "p3"})
        self.assertEqual([row["proposal_id"] for row in selected], ["p0", "p3"])

    def test_duplicate_ids_are_rejected(self):
        with self.assertRaisesRegex(MODULE.P4EvaluationError, "duplicate proposal"):
            MODULE.common_eligible_rows([{"proposal_id": "p0"}, {"proposal_id": "p0"}], {"p0"}, {"p0"})

    def test_metrics_keep_full_visible_denominator_and_no_tick_panels(self):
        panels = [
            {"visible_truth_count": 2, "needs_review": False,
             "m": {"usable_detected_x_stroke_count": 2, "matched_count": 1, "unmatched_detected_count": 1}},
            {"visible_truth_count": 0, "needs_review": True,
             "m": {"usable_detected_x_stroke_count": 1, "matched_count": 0, "unmatched_detected_count": 1}},
        ]
        result = MODULE._metrics(panels, "m")
        self.assertEqual(result["visible_truth_ticks"], 2)
        self.assertEqual(result["recall"], 0.5)
        self.assertEqual(result["precision"], 1 / 3)
        self.assertEqual(result["false_proposals_on_no_visible_tick_panels"], 1)
        self.assertEqual(result["axis_uncertain_panels"], 1)


if __name__ == "__main__":
    unittest.main()
