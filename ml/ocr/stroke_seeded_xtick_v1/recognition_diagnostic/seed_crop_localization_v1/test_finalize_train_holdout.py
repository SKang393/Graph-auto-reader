# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1 import finalize_train_holdout as subject


class TrainHoldoutFinalizerTests(unittest.TestCase):
    def fixture(self, completed: bool = True):
        truth = {"rows": [
            {"case_id": "p1", "kind": "positive", "text": "1", "target_crop_xyxy": [1, 1, 4, 4]},
            {"case_id": "p2", "kind": "positive", "text": "1", "target_crop_xyxy": [1, 1, 4, 4]},
            {"case_id": "b1", "kind": "background_only", "text": None, "target_crop_xyxy": None},
        ]}
        rows = []
        for case_id, image_hash, source, text, number, failure in (
            ("p1", "h1", "s1", "1", "1", None),
            ("p2", "h1", "s1", "2", "1", None),
            ("b1", "h2", "s2", "−", None, None),
        ):
            rows.append({"CaseId": case_id, "SourceGroup": source, "ImageHash": image_hash,
                         "NumericText": number, "NumericConfidence": 0.9 if number else None, "NumericFailure": failure,
                         "OfficialText": text, "OfficialConfidence": 0.9, "FullNumericText": None,
                         "FullNumericConfidence": None, "FullNumericFailure": None,
                         "FullOfficialText": None, "FullOfficialConfidence": None, "FullOfficialFailure": None})
        recognition = {"completed": completed, "schema": "graphreader.seed-crop-localization-recognition.v1",
                       "input_sha256": "input", "inference_provider": "CPUExecutionProvider",
                       "distinct_image_hashes": 2, "recognitions": rows}
        input_doc = {"truth_read": False, "rows": [
            {"case_id": "p1", "image_sha256": "h1", "localized_roi_xyxy": [0, 0, 10, 10], "roi_clipped": False},
            {"case_id": "p2", "image_sha256": "h1", "localized_roi_xyxy": [0, 0, 10, 10], "roi_clipped": False},
            {"case_id": "b1", "image_sha256": "h2", "localized_roi_xyxy": [0, 0, 10, 10], "roi_clipped": False},
        ]}
        return recognition, truth, input_doc

    def test_occurrence_metrics_keep_repeated_crop_rows_and_report_text_variation(self):
        recognition, truth, input_doc = self.fixture()
        report = subject.assemble(recognition, truth, input_doc, "input")
        official = report["official_ppocr"]["localized_roi"]
        self.assertEqual(3, report["input_rows"])
        self.assertEqual(2, report["unique_images"])
        self.assertEqual(1, report["positive_unique_images"])
        self.assertEqual(1, official["counts_by_occurrence"]["exact"])
        self.assertEqual(1, official["counts_by_occurrence"]["wrong_nonempty"])
        self.assertEqual(1, official["counts_by_occurrence"]["background_nonempty"])
        self.assertEqual(1, official["repeated_hash_groups_with_output_variation"])

    def test_finalization_requires_completed_reader_pass_before_truth(self):
        recognition, truth, input_doc = self.fixture(completed=False)
        with self.assertRaisesRegex(ValueError, "incomplete"):
            subject.assemble(recognition, truth, input_doc, "input")

    def test_input_case_set_and_hash_binding_are_mandatory(self):
        recognition, truth, input_doc = self.fixture()
        with self.assertRaisesRegex(ValueError, "binding mismatch"):
            subject.assemble(recognition, truth, input_doc, "wrong")
        input_doc["rows"].pop()
        with self.assertRaisesRegex(ValueError, "row counts differ"):
            subject.assemble(recognition, truth, input_doc, "input")


if __name__ == "__main__":
    unittest.main()
