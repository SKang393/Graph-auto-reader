import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1.curate_train_proposals import (
    assign_proposals, classify_candidate, scanner_result_error,
)


def tick(x: float, visible: bool = True):
    return {"stroke_center_original_xy": [x, 10.0],
            "stroke_line_original_xyxy": [x, 6.0, x, 14.0], "visible": visible}


def proposal(x: float):
    return {"center": [x, 10.0]}


class TrainProposalCurationTests(unittest.TestCase):
    def test_only_unique_one_to_one_visible_tick_match_is_positive_candidate(self):
        assignment = assign_proposals([proposal(10)], [tick(10)])[0]
        self.assertEqual(assignment["state"], "unique_visible_tick")
        self.assertEqual(assignment["truth_index"], 0)

    def test_overlap_and_duplicate_scan_proposals_are_ambiguous(self):
        overlap = assign_proposals([proposal(12)], [tick(10), tick(14)])[0]
        duplicate = assign_proposals([proposal(10), proposal(10)], [tick(10)])
        self.assertEqual(overlap["reason"], "proposal_near_multiple_ticks")
        self.assertEqual([row["reason"] for row in duplicate],
                         ["multiple_proposals_near_one_tick", "multiple_proposals_near_one_tick"])

    def test_hidden_tick_geometry_cannot_become_a_negative(self):
        assignment = assign_proposals([proposal(10)], [tick(10, visible=False)])[0]
        kind, reasons = classify_candidate(assignment, expected_label=None, intended_region_id=None,
            xtick_glyphs_intersecting=[], non_xtick_text_intersects=False,
            intended_glyph_complete=False)
        self.assertEqual(kind, "excluded")
        self.assertIn("near_nonvisible_tick_geometry", reasons)

    def test_unmatched_crop_with_tick_text_is_excluded_not_negative(self):
        assignment = assign_proposals([proposal(40)], [tick(10)])[0]
        kind, reasons = classify_candidate(assignment, expected_label=None, intended_region_id=None,
            xtick_glyphs_intersecting=[{"region_id": "glyph-1", "text": "1"}],
            non_xtick_text_intersects=False, intended_glyph_complete=False)
        self.assertEqual(kind, "excluded")
        self.assertIn("xtick_text_intersects_unmatched_crop", reasons)

    def test_matched_tick_without_complete_intended_glyph_is_never_negative(self):
        assignment = assign_proposals([proposal(10)], [tick(10)])[0]
        kind, reasons = classify_candidate(assignment, expected_label="1", intended_region_id="glyph-1",
            xtick_glyphs_intersecting=[], non_xtick_text_intersects=False,
            intended_glyph_complete=False)
        self.assertEqual(kind, "excluded")
        self.assertIn("neighboring_or_multiple_xtick_glyphs_intersect_crop", reasons)
        self.assertIn("intended_xtick_glyph_missing_or_clipped", reasons)

    def test_only_fully_text_free_geometrically_unmatched_crop_is_background(self):
        assignment = assign_proposals([proposal(40)], [tick(10)])[0]
        kind, reasons = classify_candidate(assignment, expected_label=None, intended_region_id=None,
            xtick_glyphs_intersecting=[], non_xtick_text_intersects=False,
            intended_glyph_complete=False)
        self.assertEqual(kind, "background_only")
        self.assertEqual(reasons, [])

    def test_adjacent_non_tick_text_blocks_background_label(self):
        assignment = assign_proposals([proposal(40)], [tick(10)])[0]
        kind, reasons = classify_candidate(assignment, expected_label=None, intended_region_id=None,
            xtick_glyphs_intersecting=[], non_xtick_text_intersects=True,
            intended_glyph_complete=False)
        self.assertEqual(kind, "excluded")
        self.assertIn("non_xtick_text_intersects_crop", reasons)

    def test_scanner_source_failure_is_not_an_empty_negative_batch(self):
        error = scanner_result_error({"source_id": "source-1", "error": "no line candidates"},
                                     "source-1", {"panel-1"})
        self.assertEqual(error, "no line candidates")

    def test_missing_or_malformed_scanner_output_is_a_source_failure(self):
        missing = scanner_result_error({"source_id": "source-1", "panels": []},
                                       "source-1", {"panel-1"})
        malformed = scanner_result_error({"source_id": "source-1", "panels": [
            {"panel_id": "panel-1", "ink_proposals": None}]}, "source-1", {"panel-1"})
        self.assertIsNotNone(missing)
        self.assertIsNotNone(malformed)


if __name__ == "__main__":
    unittest.main()
