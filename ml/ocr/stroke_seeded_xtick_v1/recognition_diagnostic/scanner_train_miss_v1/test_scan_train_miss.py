# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan


class TrainMissTests(unittest.TestCase):
    def test_python_baseline_pixel_scan_finds_tick_and_rejects_blank(self):
        image = Image.new("L", (100, 60), 255)
        draw = ImageDraw.Draw(image)
        draw.line((10, 40, 90, 40), fill=0)
        self.assertEqual([], scan.scan_panel(image, [10, 40, 90, 40], [0, 0, 100, 60], 8))
        draw.line((40, 35, 40, 45), fill=0)
        proposals = scan.scan_panel(image, [10, 40, 90, 40], [0, 0, 100, 60], 8)
        self.assertEqual(1, len(proposals))
        self.assertEqual([40.0, 40.0], proposals[0]["center"])
        self.assertEqual([40.0, 35.0, 40.0, 45.0], proposals[0]["line"])

    def test_components_join_shared_hashes_and_report_semantic_conflicts(self):
        rows = [
            {"source_id": "a", "kind": "positive", "image_sha256": "shared"},
            {"source_id": "b", "kind": "background_only", "image_sha256": "shared"},
            {"source_id": "c", "kind": "positive", "image_sha256": "separate"},
        ]
        components, conflicts = scan.group_components(rows, ["a", "b", "c"])
        self.assertEqual({frozenset({"a", "b"}), frozenset({"c"})}, {frozenset(v) for v in components})
        self.assertEqual([{"image_sha256": "shared", "kinds": ["background_only", "positive"],
                           "source_ids": ["a", "b"], "occurrences": 2}], conflicts)

    def test_full_panel_hash_also_links_source_groups(self):
        rows = [{"source_id": source, "kind": "positive", "image_sha256": source}
                for source in ("a", "b", "c")]
        components, _ = scan.group_components(rows, ["a", "b", "c"],
            {"a": "same-panel", "b": "same-panel", "c": "other-panel"})
        self.assertEqual({frozenset({"a", "b"}), frozenset({"c"})}, {frozenset(v) for v in components})

    def test_expected_baseline_counts_cover_fit_and_validation_sources(self):
        rows = [{"source_id": "fit", "case_id": "f0"},
                {"source_id": "fit", "case_id": "f1"},
                {"source_id": "validation", "case_id": "v0"}]
        counts = scan.proposal_counts(rows)
        self.assertEqual(2, counts["fit"])
        self.assertEqual(1, counts["validation"])

    def test_frozen_split_is_deterministic_and_keeps_hash_component_whole(self):
        rows = [
            {"source_id": "a", "kind": "positive", "image_sha256": "shared"},
            {"source_id": "b", "kind": "background_only", "image_sha256": "shared"},
            {"source_id": "c", "kind": "positive", "image_sha256": "c"},
            {"source_id": "d", "kind": "positive", "image_sha256": "d"},
            {"source_id": "e", "kind": "positive", "image_sha256": "e"},
        ]
        first = scan.freeze_split(rows, ["a", "b", "c", "d", "e"])
        second = scan.freeze_split(rows, ["a", "b", "c", "d", "e"])
        self.assertEqual(first, second)
        self.assertFalse(set(first["fit_source_ids"]) & set(first["validation_source_ids"]))
        for component in first["fit_components"] + first["validation_components"]:
            if "a" in component:
                self.assertIn("b", component)

    def test_validation_selection_rule_is_fixed_without_development_inputs(self):
        self.assertIn("maximize fit F1", scan.FIT_SELECTION_RULE)
        self.assertEqual((8, 12, 16), scan.MAX_WIDTH_CANDIDATES)
        self.assertNotIn("probe", scan.V6_MANIFEST_REL.as_posix())
        source = Path(scan.__file__).read_text(encoding="utf-8")
        self.assertNotIn("stroke-seeded-xtick-v1-p2-probe-corpus-v3", source)


if __name__ == "__main__":
    unittest.main()
