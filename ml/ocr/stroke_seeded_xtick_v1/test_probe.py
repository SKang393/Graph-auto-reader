# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Source-disjoint synthetic diagnostic and duplicate-image label checks."""

from collections import Counter
import unittest

from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import probe_evaluate_gpu, probe_materialize


class StrokeSeededProbeTests(unittest.TestCase):
    def test_probe_is_source_disjoint_and_has_verified_glyph_box_maps(self):
        config, _ = probe_evaluate_gpu.load_config()
        probe = probe_evaluate_gpu.load_probe_rows(config)
        self.assertEqual(probe["manifest_sha256"], config["probe_manifest_sha256"])
        self.assertEqual(probe["counts"], {"positive": 165, "background_only": 158, "excluded": 36})
        self.assertEqual(len(probe["rows"]), 323)
        self.assertEqual(sum(probe["duplicate_images_by_kind"].values()), 88)
        self.assertEqual(probe["duplicate_images_by_kind"], {"positive": 37, "background_only": 51})
        self.assertEqual(len({row["image_sha256"] for row in probe["rows"]}), 235)
        self.assertEqual(len(probe["source_group_counts"]), 16)
        duplicates = probe["manifest"]["duplicate_train_image_exclusions"]
        self.assertEqual(len(duplicates), 63)
        self.assertTrue(all("image_sha256" not in row for row in duplicates))
        self.assertTrue(all(row["kind"] == "background_only" for row in duplicates))
        self.assertEqual(probe["verification"]["separation"]["overlap_counts"], {
            "source_ids": 0, "source_identity_hashes": 0, "source_pixel_hashes": 0, "crop_image_hashes": 0,
        })

    def test_generated_source_styles_and_session_range_differ_from_v6_train(self):
        probe = probe_materialize.verify_probe(
            probe_materialize.OUTPUT,
            "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba")
        train_bound = helpers._bound_read(probe_materialize.ROOT, probe_materialize.TRAIN_MANIFEST,
                                          "pinned V6 train manifest", 5_000_000)
        train_manifest = helpers._parse_json(train_bound, "pinned V6 train manifest")
        train_styles = Counter(source["recipe"]["renderer_family"] for source in train_manifest["sources"])
        probe_manifest = helpers._parse_json(
            helpers._bound_read(probe_materialize.ROOT,
                                "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json",
                                "source-disjoint probe manifest", 5_000_000), "source-disjoint probe manifest")
        probe_styles = Counter(source["renderer_family"] for source in probe_manifest["sources"])
        train_sessions = [source["recipe"]["session_count"] for source in train_manifest["sources"]]
        probe_sessions = [source["session_count"] for source in probe_manifest["sources"]]
        train_ids = {source["source_id"] for source in train_manifest["sources"]}
        probe_ids = {source["source_id"] for source in probe_manifest["sources"]}
        train_seeds = {source["recipe"]["seed"] for source in train_manifest["sources"]}
        probe_seeds = {source["seed"] for source in probe_manifest["sources"]}
        rejected_ids = {row["source_id"] for row in probe_manifest["excluded_source_recipes"]}
        rejected_seeds = {row["seed"] for row in probe_manifest["excluded_source_recipes"]}
        self.assertEqual(probe["sources"], 16)
        self.assertFalse(train_ids & probe_ids)
        self.assertFalse(train_seeds & probe_seeds)
        self.assertFalse(train_ids & rejected_ids)
        self.assertFalse(train_seeds & rejected_seeds)
        self.assertNotEqual(train_styles, probe_styles)
        self.assertGreater(max(probe_sessions), max(train_sessions))

    def test_identical_image_hash_requires_exactly_matching_class_and_box(self):
        seen = {}
        box = (0.1, 0.2, 0.3, 0.4)
        self.assertFalse(probe_evaluate_gpu.register_image_target(seen, "a" * 64, "positive", box, "12"))
        self.assertTrue(probe_evaluate_gpu.register_image_target(seen, "a" * 64, "positive", box, "12"))
        with self.assertRaises(probe_evaluate_gpu.ProbeEvaluationError):
            probe_evaluate_gpu.register_image_target(seen, "a" * 64, "positive", box, "14")
        with self.assertRaises(probe_evaluate_gpu.ProbeEvaluationError):
            probe_evaluate_gpu.register_image_target(seen, "a" * 64, "background_only", None, None)


if __name__ == "__main__":
    unittest.main()
