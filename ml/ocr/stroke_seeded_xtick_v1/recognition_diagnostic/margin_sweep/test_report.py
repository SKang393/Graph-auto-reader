import json
import unittest
from pathlib import Path


REPORT = Path(__file__).resolve().parents[5] / "artifacts" / "stroke-seeded-xtick-v1-p2-v5-vertical-margin-sweep-v1" / "report.json"


class MarginSweepReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_margin_025_reproduces_existing_recognition_baseline(self):
        self.assertEqual(
            self.report["margin_025_parity_to_existing_recognition_report"],
            {"ground_truth_positive_boxes": True, "predicted_positive_boxes": True},
        )
        baseline = self.report["margins"]["0.25"]
        self.assertEqual(
            [baseline["ground_truth_positive_boxes"][key] for key in ("exact", "wrong", "empty_without_alternative")],
            [32, 32, 101],
        )
        self.assertEqual(
            [baseline["predicted_positive_boxes"][key] for key in ("exact", "wrong", "empty_without_alternative")],
            [23, 30, 112],
        )

    def test_each_margin_accounts_for_all_positive_and_background_crops(self):
        self.assertEqual(set(self.report["margins"]), {"0.25", "0.50", "0.75"})
        for result in self.report["margins"].values():
            for role in ("ground_truth_positive_boxes", "predicted_positive_boxes", "forced_background_top_one_boxes"):
                metrics = result[role]
                self.assertEqual(metrics["accounted"], metrics["crop_count"])
                self.assertEqual(metrics["explicit_failures"], {})
                self.assertGreaterEqual(metrics["structural_tall_component_reject"], 0)
            self.assertEqual(result["ground_truth_positive_boxes"]["crop_count"], 165)
            self.assertEqual(result["predicted_positive_boxes"]["crop_count"], 165)
            self.assertEqual(result["forced_background_top_one_boxes"]["crop_count"], 158)

    def test_background_rows_are_scored_without_dedup_and_false_text_is_separate(self):
        identities = self.report["background_image_identity"]
        self.assertEqual(identities["background_rows_scored"], 158)
        self.assertEqual(identities["repeated_image_rows"], 158 - identities["unique_image_hashes"])
        self.assertEqual(identities["conflicting_expected_text_by_identical_hash"], 0)
        for result in self.report["margins"].values():
            background = result["forced_background_top_one_boxes"]
            self.assertEqual(background["returned_text_false_outputs"], 0)
            self.assertEqual(background["detector_score_at_least_025"], 0)
            self.assertEqual(background["empty_without_alternative"], 158)
        self.assertFalse(self.report["production_defaults_changed"])
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["dev_sealed_private_inputs_read"])


if __name__ == "__main__":
    unittest.main()
