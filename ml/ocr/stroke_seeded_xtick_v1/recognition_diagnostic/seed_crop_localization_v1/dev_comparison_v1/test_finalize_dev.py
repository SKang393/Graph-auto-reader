# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.dev_comparison_v1 import finalize_dev as subject


class DevFinalizationTests(unittest.TestCase):
    def test_fixed_v5_threshold_and_official_grammar_are_distinct(self):
        numeric = {"NumericText": "2.5", "NumericConfidence": 0.649, "OfficialText": "2.5",
                   "OfficialConfidence": 0.0, "FullNumericText": None, "FullNumericConfidence": None,
                   "FullOfficialText": None, "FullOfficialConfidence": None}
        self.assertFalse(subject.recognition_decision(numeric, "numeric", "localized")[0])
        numeric["NumericConfidence"] = 0.65
        self.assertTrue(subject.recognition_decision(numeric, "numeric", "localized")[0])
        self.assertTrue(subject.recognition_decision(numeric, "official", "localized")[0])
        numeric["OfficialText"] = "2,5"
        self.assertFalse(subject.recognition_decision(numeric, "official", "localized")[0])

    def test_recognition_join_uses_exact_proposal_and_crop_hashes(self):
        result = [{"CaseId": "id1", "ImageHash": "h1", "OfficialText": "1"}]
        row = {"proposal_id": "id1", "image_sha256": "h1", "proposal": {"center": [1, 2]},
               "panel_id": "panel1", "source_id": "source1", "p3_score": .8, "p5_score": .7,
               "p3_selected": True, "p5_selected": True}
        joined = subject.enrich_recognition_rows(result, [row])[0]
        self.assertEqual("panel1", joined["PanelId"])
        self.assertEqual({"center": [1, 2]}, joined["Proposal"])
        row["image_sha256"] = "different"
        with self.assertRaisesRegex(subject.DevFinalizationError, "crop hash differs"):
            subject.enrich_recognition_rows(result, [row])

    def test_no_visible_panel_false_proposals_count_toward_denominator_metrics(self):
        rows = [{"ProposalId": "false1", "PanelId": "blank", "SourceId": "s", "Proposal": {"center": [1, 2]},
                 "OfficialText": "−", "NumericText": None}]
        truth = {"blank": []}
        class Matcher:
            @staticmethod
            def match_panel(truths, detections):
                return {"matched_count": 0, "unmatched_detected_count": len(detections), "matched_pairs": []}
        metrics = subject.evaluate_proposals(rows, truth, {}, Matcher)
        self.assertEqual(1, metrics["proposals"])
        self.assertEqual(1, metrics["false_proposals_on_no_visible_panels"])
        self.assertEqual(1, metrics["false_text_on_no_visible_panels"]["nonempty"])
        self.assertEqual(0, metrics["visible_truth_denominator"])


if __name__ == "__main__":
    unittest.main()
