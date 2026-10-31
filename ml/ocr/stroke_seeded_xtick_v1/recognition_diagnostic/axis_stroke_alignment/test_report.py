# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import json
import os
from pathlib import Path
import unittest


REPORT = Path(os.environ["AXIS_STROKE_ALIGNMENT_REPORT"]) if os.environ.get("AXIS_STROKE_ALIGNMENT_REPORT") else None


class AxisStrokeAlignmentReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if REPORT is None:
            raise RuntimeError("Set AXIS_STROKE_ALIGNMENT_REPORT to a completed report")
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_hash_gated_scope_and_model_free_path(self):
        r = self.report
        self.assertEqual(r["schema"], "graphreader.stroke-seeded-xtick-v1-axis-stroke-alignment.v1")
        self.assertEqual(len(r["probe_manifest_sha256"]), 64)
        self.assertEqual(r["sample_counts"]["source_groups"], 16)
        self.assertEqual(r["sample_counts"]["source_panels"], 24)
        self.assertEqual(r["sample_counts"]["source_tick_rows_regenerated"], 422)
        self.assertEqual(r["sample_counts"]["visible_source_strokes"], 181)
        self.assertEqual(r["sample_counts"]["invisible_source_strokes"], 241)
        self.assertTrue(r["sample_counts"]["all_axis_inputs_hash_validated_before_inference"])
        self.assertFalse(r["sample_counts"]["panel_raster_files_written"])
        self.assertFalse(r["production_components"]["model_inference"])
        self.assertFalse(r["production_components"]["ocr_models_loaded"])
        self.assertFalse(r["training_performed"])
        self.assertFalse(r["dev_sealed_private_inputs_read"])

    def test_detector_and_ground_truth_denominators_reconcile(self):
        matching = self.report["matching"]
        self.assertEqual(matching["panels_succeeded"], 24)
        self.assertEqual(matching["panels_failed"], 0)
        self.assertEqual(matching["truth_count"], 181)
        self.assertEqual(matching["usable_detected_x_stroke_count"], 37)
        self.assertEqual(matching["matched_count"] + matching["unmatched_truth_count"], matching["truth_count"])
        self.assertEqual(matching["matched_count"] + matching["unmatched_detected_count"],
                         matching["usable_detected_x_stroke_count"])
        self.assertEqual(sum(self.report["matched_cases_by_kind"].values()), matching["matched_count"])
        sources = self.report["source_group_results"]
        self.assertEqual(len(sources), 16)
        self.assertEqual(sum(row["panel_count"] for row in sources), 24)
        self.assertEqual(sum(row["truth_count"] for row in sources), 181)

    def test_glyph_overlap_uses_verified_masks_and_post_detection_analysis(self):
        glyph = self.report["glyph_overlap"]
        self.assertEqual(glyph["matched_positive_pairs_with_verified_crop_masks"], 23)
        self.assertEqual(glyph["detected_lines_intersecting_glyph_mask"], 2)
        self.assertEqual(glyph["one_pixel_detected_centerline_mask_intersection_pixels"], 3)
        self.assertEqual(glyph["mask_index_sha256"], "db101ed7657f5f2d4e2522df46736b94a672e3c4afbbaf33de5871cb23def2d4")
        self.assertIn("after detection", glyph["interpretation"])


if __name__ == "__main__":
    unittest.main()
