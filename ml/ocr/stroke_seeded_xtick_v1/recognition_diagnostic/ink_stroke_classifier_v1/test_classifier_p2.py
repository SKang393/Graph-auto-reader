# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused source-uniform sampler, provenance and train-only admission checks."""

from __future__ import annotations

import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import train_classifier_p2 as p2


class SourceUniformP2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.config, cls.config_sha = p2.validate_config()
        cls.data = p2.admission.admit_train_images()

    def test_exact_dependencies_and_frozen_train_only_admission(self) -> None:
        self.assertEqual(self.data["unique_count"], 446)
        self.assertEqual(self.data["class_counts"], {"positive": 81, "negative": 365})
        self.assertEqual(set(self.data["source_groups"]), set(self.config["train_source_groups"]))
        self.assertEqual(self.data["nontrain_image_hash_overlap"], 0)
        self.assertEqual(p2._helper_sha_before, p2.CONFIG["dependencies"]["model_helper_sha256"])
        self.assertEqual(p2._helper_sha_after, p2._helper_sha_before)
        self.assertEqual(p2.Path(p2.helpers.__file__).resolve(), p2.HELPER_PATH.resolve())

    def test_round_robin_draws_are_uniform_despite_unequal_group_sizes(self) -> None:
        grouped = {"large-source": list(range(93))}
        for index in range(18):
            grouped[f"small-{index:02d}"] = [100 + index]
        state = p2.make_group_state(grouped, 27104153)
        _, counts = p2.source_uniform_order(grouped, 368, 0, state)
        self.assertEqual(len(counts), 19)
        self.assertEqual((min(counts.values()), max(counts.values())), (19, 20))
        self.assertLessEqual(counts["large-source"], 20)
        self.assertEqual(sum(counts.values()), 368)

    def test_rotating_example_cursor_covers_large_source_pool_cumulatively(self) -> None:
        grouped = {"large-source": list(range(93)), "small-source": [1000]}
        state = p2.make_group_state(grouped, 7)
        seen = set()
        coverage_after_five = None
        for epoch in range(20):
            draws, _ = p2.source_uniform_order(grouped, 40, epoch, state)
            seen.update(draws)
            if epoch == 4:
                coverage_after_five = set(seen)
        self.assertEqual(coverage_after_five, set(range(93)) | {1000})
        self.assertEqual(seen, set(range(93)) | {1000})

    def test_frozen_schedule_covers_every_train_hash_and_rotates_group_remainders(self) -> None:
        rows = self.data["images"]
        positive = [row for row in rows if row["label"] == 1]
        negative = [row for row in rows if row["label"] == 0]
        pos_map, _ = p2._owner_group_indices(positive, set(self.data["source_groups"]))
        neg_map, _ = p2._owner_group_indices(negative, set(self.data["source_groups"]))
        self.assertEqual((len(pos_map), len(neg_map)), (10, 19))
        self.assertEqual((len(positive), len(negative)), (81, 365))
        pos_state = p2.make_group_state(pos_map, self.config["sampler_seed"])
        neg_state = p2.make_group_state(neg_map, self.config["sampler_seed"] + 1)
        pos_seen, neg_seen = set(), set()
        pos_all, neg_all = set(), set()
        pos_totals, neg_totals = {g: 0 for g in pos_map}, {g: 0 for g in neg_map}
        for epoch in range(self.config["epochs"]):
            po, pc = p2.source_uniform_order(pos_map, 368, epoch, pos_state)
            no, nc = p2.source_uniform_order(neg_map, 368, epoch, neg_state)
            self.assertLessEqual(max(pc.values()) - min(pc.values()), 1)
            self.assertLessEqual(max(nc.values()) - min(nc.values()), 1)
            pos_seen.update(po); neg_seen.update(no)
            pos_all.update(range(len(positive))); neg_all.update(range(len(negative)))
            for group, count in pc.items(): pos_totals[group] += count
            for group, count in nc.items(): neg_totals[group] += count
        self.assertEqual(pos_seen, pos_all)
        self.assertEqual(neg_seen, neg_all)
        self.assertLessEqual(max(pos_totals.values()) - min(pos_totals.values()), 1)
        self.assertLessEqual(max(neg_totals.values()) - min(neg_totals.values()), 1)

    def test_cpu_device_is_rejected_by_gpu_only_training_binding(self) -> None:
        import torch
        with self.assertRaises(p2.p1.StrokeClassifierError):
            p2.p1._assert_gpu_binding({"device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False},
                object(), [], torch.device("cpu"))


if __name__ == "__main__":
    unittest.main()
