# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused checks for row rotation, source balancing, and collision quarantine."""

from collections import Counter
import unittest

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import train_curated_mix as mix


class CuratedMixTests(unittest.TestCase):
    def test_fixed_steps_rotation_coverage_and_image_repeat_accounting(self):
        rows = []
        boxes = []
        hashes = []
        # Match the admitted V6 class counts and unique-hash counts. Repeated V6
        # bytes are retained as distinct original rows and counted by the sampler.
        for i in range(235):
            rows.append({"case_id": f"bp{i}", "source_id": f"v6p{i % 16}"})
            boxes.append((0.1, 0.1, 0.2, 0.2))
            hashes.append(f"base-positive-{i % 193}")
        for i in range(409):
            rows.append({"case_id": f"bn{i}", "source_id": f"v6n{i % 16}"})
            boxes.append(None)
            hashes.append(f"base-negative-{i % 132}")
        additions = []
        for kind, total, groups in (("positive", 77, 13), ("background_only", 71, 16)):
            for i in range(total):
                row = {"case_id": f"c-{kind}-{i}", "source_id": f"cur{i % groups}", "kind": kind}
                additions.append(row)
                rows.append(row)
                boxes.append((0.2, 0.2, 0.3, 0.3) if kind == "positive" else None)
                hashes.append(f"cur-{kind}-{i}")
        data = {"rows": rows, "boxes": boxes, "image_hashes": hashes,
                "base_count": 644, "curated_additions": additions}
        config = {"shuffle_seed": 27103152}
        coverage = set()
        reports = []
        for epoch in range(30):
            batches, stats = mix.sample_epoch(data, epoch, config)
            flat = [index for batch in batches for index in batch]
            self.assertEqual(len(batches), 92)
            self.assertEqual([len(batch) for batch in batches], [7] * 92)
            self.assertEqual(len(flat), 644)
            self.assertEqual(len(set(flat)), 644)
            self.assertEqual(stats["positive_rows"], 235)
            self.assertEqual(stats["background_rows"], 409)
            self.assertEqual(stats["curated_positive_rows"], 24)
            self.assertEqual(stats["curated_background_rows"], 24)
            self.assertGreater(stats["repeated_image_hash_rows"], 0)
            self.assertEqual(stats["unique_image_hashes"] + stats["repeated_image_hash_rows"], 644)
            self.assertLessEqual(max(stats["curated_background_by_source"].values()), 2)
            self.assertLessEqual(max(stats["curated_positive_by_source"].values()), 2)
            coverage.update(flat)
            reports.append(stats)
        self.assertEqual(coverage, set(range(644 + 148)))
        self.assertEqual(min(r["curated_positive_rows"] for r in reports), 24)
        self.assertEqual(min(r["curated_background_rows"] for r in reports), 24)

    def test_same_image_semantic_conflicts_are_quarantined(self):
        mix._reject_semantic_label_conflicts({"abc": {("positive", "1", (1, 2, 3, 4))}}, "fixture")
        with self.assertRaisesRegex(mix.CuratedMixError, "quarantined"):
            mix._reject_semantic_label_conflicts(
                {"abc": {("positive", "1", (1, 2, 3, 4)), ("positive", "2", (1, 2, 3, 4))}},
                "fixture")

    def test_exact_cross_manifest_label_match_can_be_deduplicated(self):
        base = {"img": {("positive", "11", (30.0, 20.0, 44.0, 36.0))}}
        curated = {"img": {("positive", "11", (30.0, 20.0, 44.0, 36.0))},
                   "new": {("background_only", None, None)}}
        mix._reject_semantic_label_conflicts(base, "V6")
        mix._reject_semantic_label_conflicts(curated, "curation")
        for image_hash in set(base) & set(curated):
            self.assertEqual(base[image_hash], curated[image_hash])


if __name__ == "__main__":
    unittest.main()
