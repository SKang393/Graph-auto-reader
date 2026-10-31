import json
import unittest
from pathlib import Path


REPORT = Path(__file__).resolve().parents[5] / "artifacts" / "stroke-seeded-xtick-v1-p2-v5-component-rejection-detail-v1" / "report.json"


class ComponentRejectionDetailTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_adapter_rejected_flag_is_exactly_reproduced(self):
        contract = self.report["adapter_encoder_contract"]
        self.assertTrue(contract["production_rejected_flag_verified"])
        self.assertEqual(contract["all_selected_positive_crops"], 165)
        self.assertEqual(contract["adapter_rejected_crops"], 82)
        self.assertEqual(contract["rejected_flag_mismatches"], 0)
        self.assertEqual(
            contract["tall_only"] + contract["glyph_count_only"] + contract["both"],
            contract["adapter_rejected_crops"],
        )

    def test_rejections_are_tall_components_not_glyph_count(self):
        partition = self.report["rejected_reason_partition"]
        self.assertEqual(partition, {"tall_only": 82, "glyph_count_only": 0, "both": 0})
        self.assertEqual(self.report["component_height_ratio_threshold"], 0.75)
        self.assertLessEqual(self.report["adapter_encoder_contract"]["maximum_glyphs"], 8)

    def test_stroke_overlap_buckets_cover_all_rows_without_causal_claim(self):
        strata = self.report["line_overlap_stratification"]
        self.assertEqual(sum(row["crop_count"] for row in strata.values()), 165)
        self.assertEqual(sum(row["adapter_rejected"] for row in strata.values()), 82)
        self.assertEqual(self.report["all_crops"]["annotated_stroke_segment_intersects_target_box"], 0)
        self.assertTrue(self.report["no_training_or_inference"])
        self.assertIn("geometric overlap only", self.report["stroke_overlap_definition"])


if __name__ == "__main__":
    unittest.main()
