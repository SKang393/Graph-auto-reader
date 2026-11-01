# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.dev_comparison_v1 import prepare_dev as subject


class DevAdmissionTests(unittest.TestCase):
    def test_crop_seed_mapping_inverts_fixed_v6_rounding_without_truth(self):
        for actual, expected in zip(subject.proposal_seed_in_crop([100.2, 51.3]), (48.4, 4.6)):
            self.assertAlmostEqual(expected, actual)
        for actual, expected in zip(subject.proposal_seed_in_crop([99.9, 50.9]), (47.8, 3.8)):
            self.assertAlmostEqual(expected, actual)

    def test_frozen_selected_rows_are_truth_blind_and_overlap_quarantined(self):
        input_doc, audit = subject.make_inputs()
        self.assertEqual(116, len(input_doc["rows"]))
        self.assertFalse(input_doc["truth_read"])
        self.assertTrue(input_doc["development_probe"])
        self.assertEqual(116, audit["rows"])
        self.assertEqual(0, audit["crop_hash_overlap_localizer_train"])
        self.assertEqual(0, audit["crop_hash_overlap_localizer_holdout"])
        self.assertFalse(audit["truth_read"])
        for row in input_doc["rows"]:
            self.assertTrue(row["p3_selected"] or row["p5_selected"])
            self.assertNotIn("expected_tick_label", row)
            self.assertNotIn("target_crop_xyxy", row)
            self.assertNotIn("kind", row)


if __name__ == "__main__":
    unittest.main()
