"""Integrity checks for the completed geometry-offset diagnostic report."""

import json
import os
import sys
import unittest
from pathlib import Path


REPORT = Path(os.environ["STROKE_OFFSET_REPORT"]) if os.environ.get("STROKE_OFFSET_REPORT") else None


class OffsetSensitivityReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if REPORT is None:
            raise RuntimeError("Set STROKE_OFFSET_REPORT to the completed report.json path")
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_pinned_inference_only_inputs_and_baseline_parity(self):
        r = self.report
        self.assertEqual(r["schema"], "graphreader.stroke-seeded-xtick-v1-p2-v5-geometry-offset-sensitivity.v1")
        self.assertTrue(all(r["original_baseline_parity_to_prior_report"].values()))
        self.assertEqual(r["sample_counts"]["positive_rows"], 165)
        self.assertEqual(r["sample_counts"]["background_rows"], 158)
        self.assertTrue(r["sample_counts"]["scored_rows_are_not_deduplicated"])
        self.assertFalse(r["training_performed"])
        self.assertFalse(r["dev_sealed_private_inputs_read"])
        self.assertFalse(r["production_behavior_changed"])

    def test_all_offsets_and_roles_account_for_every_crop(self):
        results = self.report["offset_results"]
        self.assertEqual(set(results), {"offset0", "offset-1", "offset+1", "offset-2", "offset+2"})
        expected_counts = {
            "ground_truth_positive_boxes": 165,
            "predicted_positive_boxes": 165,
            "forced_background_top_one_boxes": 158,
        }
        for value in results.values():
            for role, expected in expected_counts.items():
                record = value["roles"][role]
                self.assertEqual(record["crop_count"], expected)
                outcome = record["offset_result"]
                self.assertEqual(sum(outcome[k] for k in ("exact", "wrong", "empty_without_alternative", "failures")), expected)

    def test_mask_validation_and_counterfactual_provenance(self):
        r = self.report
        p = r["perturbation"]
        self.assertTrue(p["each_variant_starts_from_original_image"])
        self.assertTrue(p["no_glyph_mask_or_text_used_to_choose_erasure"])
        self.assertEqual(p["width_pixels"], 1)
        self.assertIn("counterfactual", p["interpretation"])
        intersections = r["actual_glyph_mask_intersections"]
        self.assertEqual(set(intersections), {"offset0", "offset-1", "offset+1", "offset-2", "offset+2"})
        for row in intersections.values():
            self.assertEqual(row["positive_cases"], 165)
            self.assertEqual(row["cases_with_actual_glyph_pixel_overlap"], 0)
            self.assertEqual(row["actual_glyph_pixels_erased"], 0)
            self.assertEqual(row["total_nonzero_actual_glyph_mask_pixels"], 90889)
            self.assertLessEqual(row["crop_mask_target_box_max_edge_delta_pixels"], 1)
        self.assertEqual(r["provider_counts"], {"Cpu": 142})


if __name__ == "__main__":
    unittest.main()
