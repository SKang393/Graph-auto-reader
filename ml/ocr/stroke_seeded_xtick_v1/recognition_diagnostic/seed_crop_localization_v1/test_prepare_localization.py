# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1 import prepare_localization as subject


class LocalizationGeometryTests(unittest.TestCase):
    def test_seed_relative_box_uses_affine_source_mapping(self):
        annotation = {
            "proposal_center_original_xy": [140.0, 208.0],
            "crop_transform": {"original_to_crop_pixel": [2.0, 0.0, -232.0, 0.0, 2.0, -414.0]},
            "targets": [{"target_crop_xyxy": [30.0, 20.0, 42.0, 36.0]}],
        }
        self.assertEqual((-18.0, 18.0, -6.0, 34.0), subject.seed_relative_target(annotation))

    def test_train_fit_union_has_fixed_margin_and_is_not_label_tuned(self):
        boxes = [(-20.0, 15.0, -8.0, 30.0), (-5.0, 21.0, 6.0, 37.0)]
        self.assertEqual((-22.0, 13.0, 8.0, 39.0), subject.derive_roi(boxes, padding=2.0))

    def test_invalid_or_empty_fit_cannot_produce_roi(self):
        with self.assertRaises(subject.AdmissionError):
            subject.derive_roi([])
        with self.assertRaises(subject.AdmissionError):
            subject.derive_roi([(1, 2, 3, 4)], padding=-1)

    def test_train_and_hash_manifest_identities_are_fixed(self):
        self.assertEqual("aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672", subject.MANIFEST_SHA256)
        self.assertEqual(3, len(subject.FIT_HOLDOUT_GROUPS))
        self.assertEqual(2.0, subject.PAD_PX)

    def test_partition_keeps_hash_linked_source_groups_together(self):
        rows = []
        for group, image_hash in (("fit-a", "a"), ("fit-b", "a"), ("hold", "b")):
            record = {"source_group_id": group, "image_sha256": image_hash, "kind": "positive"}
            rows.append((record, {}))
        fit, holdout, fit_hashes, holdout_hashes = subject.partition_hash_linked_groups(rows, frozenset({"hold"}))
        self.assertEqual({"fit-a", "fit-b"}, fit)
        self.assertEqual({"hold"}, holdout)
        self.assertEqual({"a"}, fit_hashes)
        self.assertEqual({"b"}, holdout_hashes)
        self.assertFalse(fit_hashes & holdout_hashes)

    def test_shared_image_with_opposite_semantic_class_fails_closed(self):
        rows = [
            ({"source_group_id": "a", "image_sha256": "same", "kind": "positive"}, {}),
            ({"source_group_id": "b", "image_sha256": "same", "kind": "background_only"}, {}),
        ]
        with self.assertRaisesRegex(subject.AdmissionError, "semantic label conflict"):
            subject.partition_hash_linked_groups(rows, frozenset({"a"}))

    def test_hash_linked_fit_group_is_moved_into_holdout_component(self):
        rows = [
            ({"source_group_id": "fit", "image_sha256": "same", "kind": "positive"}, {}),
            ({"source_group_id": "hold", "image_sha256": "same", "kind": "positive"}, {}),
        ]
        fit, holdout, fit_hashes, holdout_hashes = subject.partition_hash_linked_groups(rows, frozenset({"hold"}))
        self.assertEqual(set(), fit)
        self.assertEqual({"fit", "hold"}, holdout)
        self.assertFalse(fit_hashes)
        self.assertEqual({"same"}, holdout_hashes)

    def test_pinned_train_split_yields_truth_blind_inference_rows(self):
        docs, meta = subject.build_inputs()
        inference = docs["input"]
        truth = {row["case_id"]: row for row in docs["truth"]["rows"]}
        self.assertEqual(39, len(inference["rows"]))
        self.assertEqual(18, meta["fit_source_group_count"])
        self.assertEqual(0, meta["fit_holdout_hash_overlap"])
        self.assertEqual(0, sum(row["roi_clipped"] for row in inference["rows"]))
        self.assertEqual(25, sum(row["kind"] == "positive" for row in truth.values()))
        for row in inference["rows"]:
            self.assertNotIn("kind", row)
            self.assertNotIn("text", row)
            self.assertNotIn("target_crop_xyxy", row)
            expected = truth[row["case_id"]]
            if expected["kind"] == "positive":
                x0, y0, x1, y1 = expected["target_crop_xyxy"]
                left, top, right, bottom = row["localized_roi_xyxy"]
                self.assertTrue(left <= x0 and top <= y0 and x1 <= right and y1 <= bottom)


if __name__ == "__main__":
    unittest.main()
