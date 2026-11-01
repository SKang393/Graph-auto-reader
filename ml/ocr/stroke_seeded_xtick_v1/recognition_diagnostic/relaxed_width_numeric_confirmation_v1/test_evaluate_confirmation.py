# SPDX-License-Identifier: Apache-2.0
from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.relaxed_width_numeric_confirmation_v1 import evaluate_confirmation as subject


class NumericConfirmationTests(unittest.TestCase):
    def test_fixed_numeric_rule_uses_training_threshold_and_numeric_grammar(self):
        self.assertTrue(subject.numeric_confirmation("12.5%", 0.65))
        self.assertTrue(subject.numeric_confirmation("-7", 0.99))
        for text, confidence in (("12x", 0.99), ("12 5", 0.99), ("", 0.99), ("12", 0.649), (None, 0.99), ("12", None)):
            with self.subTest(text=text, confidence=confidence):
                self.assertFalse(subject.numeric_confirmation(text, confidence))

    def test_official_comparator_preserves_v8_zero_minimum_and_graph_number_grammar(self):
        self.assertTrue(subject.official_confirmation("3.25", 0.0))
        self.assertTrue(subject.official_confirmation("3.25", 0.001))
        self.assertFalse(subject.official_confirmation("3,25", 0.99))

    def test_truth_finalization_requires_both_recognizers_to_finish(self):
        self.assertEqual(0.5, subject.THRESHOLD)
        self.assertEqual(0.65, subject.NUMERIC_THRESHOLD)
        self.assertEqual(433, subject.EXPECTED_ROWS)
        # The finalize path reads probe labels only after the recognition receipt exists
        # and cryptographically binds the exact score-row bytes.
        import inspect
        source = inspect.getsource(subject.finalize_after_recognition)
        self.assertLess(source.index("read_project(RECOGNITION_REL"), source.index("annotation_rel ="))
        self.assertIn('"completed") is not True', source)

    def test_recognition_rows_bind_system_text_json_pascal_case(self):
        row = {"ProposalId": "candidate-1", "NumericText": "2.5", "NumericConfirmed": True}
        self.assertEqual("candidate-1", subject.result_field(row, "proposal_id"))
        self.assertEqual("2.5", subject.result_field(row, "numeric_text"))
        self.assertTrue(subject.result_field(row, "numeric_confirmed"))


if __name__ == "__main__":
    unittest.main()
