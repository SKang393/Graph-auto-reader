import json
import unittest
from pathlib import Path


REPORT = Path(__file__).resolve().parents[5] / "artifacts" / "stroke-seeded-xtick-v1-p2-v5-geometry-only-erase-v2" / "report.json"


class GeometryOnlyEraseReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_positive_box_outcomes_reproduce_baseline_then_change_after_erasure(self):
        self.assertEqual(
            self.report["baseline_parity_at_margin_025"],
            {"ground_truth_positive_boxes": True, "predicted_positive_boxes": True},
        )
        gt = self.report["metrics_by_role"]["ground_truth_positive_boxes"]
        pred = self.report["metrics_by_role"]["predicted_positive_boxes"]
        self.assertEqual([gt["baseline"][key] for key in ("exact", "wrong", "empty_without_alternative")], [32, 32, 101])
        self.assertEqual([gt["geometry_erased"][key] for key in ("exact", "wrong", "empty_without_alternative")], [67, 22, 76])
        self.assertEqual([pred["baseline"][key] for key in ("exact", "wrong", "empty_without_alternative")], [23, 30, 112])
        self.assertEqual([pred["geometry_erased"][key] for key in ("exact", "wrong", "empty_without_alternative")], [37, 30, 98])
        self.assertEqual(gt["paired_changes"]["exact_lost"], 0)
        self.assertEqual(pred["paired_changes"]["exact_lost"], 0)

    def test_background_false_text_and_glyph_mask_safety_are_reported(self):
        background = self.report["metrics_by_role"]["forced_background_top_one_boxes"]
        self.assertEqual(background["baseline"]["empty_without_alternative"], 158)
        self.assertEqual(background["geometry_erased"]["empty_without_alternative"], 158)
        self.assertEqual(background["paired_changes"]["newly_returned_text_on_background"], 0)
        safety = self.report["glyph_mask_safety"]
        self.assertTrue(safety["actual_masks_from_pinned_renderer"])
        self.assertEqual(safety["mask_index_records"], 165)
        self.assertEqual(safety["full_panel_mask_hash_mismatches"], 0)
        self.assertEqual(safety["probe_image_hash_mismatches"], 0)
        self.assertEqual(safety["positive_cases_with_any_erased_pixel_overlapping_actual_glyph_mask"], 0)
        self.assertEqual(safety["actual_glyph_mask_pixels_erased_by_centerline"], 0)
        self.assertLessEqual(safety["crop_mask_target_box_max_edge_delta_pixels"], 1.0)
        self.assertTrue(safety["crop_mask_target_box_alignment_verified_within_1px"])

    def test_erasure_is_geometry_only_and_scope_is_diagnostic(self):
        erasure = self.report["geometry_erasure"]
        self.assertEqual(erasure["centerline_width_pixels"], 1)
        self.assertTrue(erasure["no_label_or_glyph_mask_used_to_choose_erased_pixels"])
        self.assertFalse(erasure["source_images_modified_on_disk"])
        self.assertEqual(self.report["sample_counts"]["background_rows"], 158)
        self.assertEqual(self.report["sample_counts"]["detector_selected_background_rows_score_at_least_025"], 0)
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["dev_sealed_private_inputs_read"])
        self.assertFalse(self.report["production_behavior_changed"])


if __name__ == "__main__":
    unittest.main()
