# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused source-admission and metric checks for the P3 crop gate."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[5]))
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.p3_text_gate_v1 import p3_text_gate as gate


class P3TextGateTests(unittest.TestCase):
    def test_recipe_ids_and_seed_range_are_unique(self) -> None:
        rows = gate.recipes()
        self.assertEqual(len(rows), 24)
        self.assertEqual(len({r["source_id"] for r in rows}), 24)
        self.assertEqual([r["seed"] for r in rows], list(range(27105100, 27105124)))
        self.assertEqual(len({r["source_identity_sha256"] for r in rows}), 24)

    def test_hash_linked_sources_stay_in_one_split(self) -> None:
        rows = []
        for index in range(8):
            source = f"s{index}"
            rows.append({"source_id": source, "source_pixel_sha256": f"panel{index // 2 if index < 2 else index}",
                         "image_sha256": f"crop{index}", "kind": "positive"})
        split = gate.freeze_split(rows, {f"s{i}" for i in range(8)})
        self.assertTrue(set(split["fit_source_ids"]).isdisjoint(split["validation_source_ids"]))
        self.assertIn(["s0", "s1"], [sorted(c) for c in split["fit_components"] + split["validation_components"]])
        self.assertEqual(split["component_count"], 7)

    def test_same_image_with_conflicting_semantics_is_quarantined(self) -> None:
        rows = [
            {"image_sha256": "x", "kind": "positive", "target_crop_xyxy": [1, 2, 8, 9]},
            {"image_sha256": "x", "kind": "background_only", "target_crop_xyxy": None},
            {"image_sha256": "y", "kind": "positive", "target_crop_xyxy": [1, 2, 8, 9]},
            {"image_sha256": "y", "kind": "positive", "target_crop_xyxy": [1, 2, 8, 9]},
        ]
        self.assertEqual(gate.semantic_conflict_hashes(rows), {"x"})

    def test_top_region_gate_counts_localization_failure_as_false_and_miss(self) -> None:
        rows = [
            {"kind": "positive", "target_crop_xyxy": [0, 0, 10, 10],
             "regions": [{"index": 0, "confidence": 0.9, "box": [20, 20, 30, 30]}]},
            {"kind": "positive", "target_crop_xyxy": [0, 0, 10, 10], "regions": []},
            {"kind": "background_only", "target_crop_xyxy": None,
             "regions": [{"index": 0, "confidence": 0.8, "box": [0, 0, 10, 10]}]},
        ]
        result = gate._score(rows, [0, 1, 2], 0.7)
        self.assertEqual(result["true_positive_crops"], 0)
        self.assertEqual(result["false_positive_crops"], 2)
        self.assertEqual(result["false_negative_crops"], 2)

    def test_iou_and_threshold_choice_are_deterministic(self) -> None:
        self.assertEqual(gate.xyxy_iou([0, 0, 10, 10], [0, 0, 10, 10]), 1.0)
        self.assertEqual(gate.xyxy_iou([0, 0, 10, 10], [10, 10, 20, 20]), 0.0)
        rows = [
            {"kind": "positive", "target_crop_xyxy": [0, 0, 10, 10],
             "regions": [{"index": 0, "confidence": 0.8, "box": [0, 0, 10, 10]}]},
            {"kind": "background_only", "target_crop_xyxy": None,
             "regions": [{"index": 0, "confidence": 0.7, "box": [0, 0, 10, 10]}]},
        ]
        threshold, frozen = gate.choose_threshold(rows, [0, 1])
        self.assertEqual(threshold, 0.8)
        self.assertEqual(frozen["selected_fit_metrics"]["true_positive_crops"], 1)


if __name__ == "__main__":
    unittest.main()
