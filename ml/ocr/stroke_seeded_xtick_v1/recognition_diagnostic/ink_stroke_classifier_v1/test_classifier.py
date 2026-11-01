# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Admission, component split, sampler and GPU-gate checks for P1."""

from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import train_classifier as trainer


class ClassifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.dataset = trainer.load_dataset({
            "curation_manifest_sha256": trainer.MANIFEST_SHA256,
            "curator_sha256": trainer.digest((trainer.ROOT / trainer.CURATOR_RELATIVE).read_bytes()),
        })

    def test_pinned_admission_counts_and_component_graph(self) -> None:
        data = self.dataset
        self.assertEqual(data["occurrence_counts"], {
            "unique_visible_tick": 114,
            "no_ground_truth_tick_nearby": 769,
            "ambiguous": 13,
        })
        self.assertEqual(len(data["unique_images"]), 533)
        self.assertEqual(sum(row["label"] for row in data["unique_images"]), 101)
        self.assertEqual(data["component_link_counts"], {
            "active_source_groups": 23,
            "components": 18,
            "hashes_shared_by_multiple_groups": 5,
            "groups_in_multi_group_components": 6,
        })

    def test_same_image_conflicting_labels_fail_closed(self) -> None:
        with self.assertRaises(trainer.StrokeClassifierError):
            trainer._deduplicate_label_map({"hash": {0, 1}})

    def test_deterministic_component_holdout_has_no_source_or_hash_leak(self) -> None:
        config = {"holdout_component_count": 4,
            "holdout_target_positive_unique_images": 20,
            "holdout_positive_tolerance": 2,
            "holdout_min_negative_unique_images": 50,
            "split_seed": 27104151}
        selected = trainer._choose_holdout(self.dataset, config)
        self.assertEqual(selected, trainer._choose_holdout(self.dataset, config))
        split = trainer.validate_split(self.dataset, selected)
        self.assertEqual(split["holdout_counts"]["positive"], 20)
        self.assertGreaterEqual(split["holdout_counts"]["negative"], 50)
        self.assertFalse(set(split["train_groups"]) & set(split["holdout_groups"]))
        self.assertFalse(split["train_image_hashes"] & split["holdout_image_hashes"])

    def test_source_balanced_sampler_covers_rows_then_counts_repeats(self) -> None:
        groups = {"a": [0, 1], "b": [2, 3, 4]}
        first = trainer._source_balanced_order(groups, 8, 7)
        self.assertEqual(first, trainer._source_balanced_order(groups, 8, 7))
        self.assertEqual(set(first[:5]), {0, 1, 2, 3, 4})
        self.assertEqual(len(first) - len(set(first)), 3)

    def test_cpu_device_is_rejected_by_gpu_training_gate(self) -> None:
        import torch
        with self.assertRaises(trainer.StrokeClassifierError):
            trainer._assert_gpu_binding({"device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False},
                object(), [], torch.device("cpu"))

    def test_classifier_metric_confusion_counts(self) -> None:
        metric = trainer._classification_metrics([1, 1, 0, 0], [0.9, 0.4, 0.6, 0.2])
        self.assertEqual((metric["true_positive"], metric["false_positive"],
                          metric["false_negative"], metric["true_negative"]), (1, 1, 1, 1))
        self.assertEqual(metric["precision"], 0.5)
        self.assertEqual(metric["recall"], 0.5)


if __name__ == "__main__":
    unittest.main()
