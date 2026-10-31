# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused checks for V4 data separation, glyph targets, sampler, and GPU gate."""

from __future__ import annotations

import json
from pathlib import Path
import unittest

from ml.ocr.fragmented_xtick_detector_v3.gpu_train import require_rx_6600_xt, stratified_batches
from ml.ocr.fragmented_xtick_detector_v3.probe_materialize import LABELS as PROBE_LABELS
from ml.ocr.fragmented_xtick_detector_v4.materialize import (
    FONT_RELATIVES, FONT_SIZES, LABELS, P4_TRAIN_LABELS, REPO_ROOT, cases, render_case,
)


class V4DataContractTests(unittest.TestCase):
    def test_materialized_shapes_are_mask_exact_diverse_and_split_disjoint(self) -> None:
        rows = cases()
        self.assertEqual((288, 192, 96), (len(rows), sum(r["kind"] == "positive" for r in rows), sum(r["kind"] == "background_only" for r in rows)))
        self.assertTrue(set(LABELS).isdisjoint(P4_TRAIN_LABELS))
        self.assertTrue(set(LABELS).isdisjoint(PROBE_LABELS))
        self.assertEqual(3, len(FONT_RELATIVES)); self.assertEqual(7, len(FONT_SIZES))
        probe_manifest_path = REPO_ROOT / "artifacts/fragmented-xtick-detector-v3-fresh-probe-v1/manifest.json"
        probe = json.loads(probe_manifest_path.read_bytes())
        manifest_path = REPO_ROOT / "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json"
        if manifest_path.exists():
            train = json.loads(manifest_path.read_bytes())
            self.assertFalse({r["case_id"] for r in train["records"]} & {r["case_id"] for r in probe["records"]})
            self.assertFalse({r["image_sha256"] for r in train["records"]} & {r["image_sha256"] for r in probe["records"]})
            self.assertTrue(all(r["split"] == "train" for r in train["records"]))
        for case in rows:
            image, annotation = render_case(case)
            target = annotation["target"]
            if target is None:
                self.assertEqual("background_only", annotation["kind"])
                continue
            from PIL import Image, ImageDraw, ImageFont
            mask = Image.new("L", image.size, 0); draw = ImageDraw.Draw(mask)
            x, y = case["origin"]; font = ImageFont.truetype(str(REPO_ROOT / case["font_relative"]), case["font_size"])
            for i, char in enumerate(case["text"]):
                draw.text((x, y), char, fill=255, font=font); x += max(1, round(float(font.getlength(char))))
                if i == case["gap_after"]: x += case["gap_pixels"]
            self.assertEqual(list(mask.getbbox()), target["bounding_box_xyxy"])
            self.assertEqual(sum(value > 0 for value in mask.tobytes()), target["visible_glyph_pixel_count"])
            self.assertTrue(0 <= target["bounding_box_xyxy"][0] < target["bounding_box_xyxy"][2] <= 256)
            self.assertTrue(0 <= target["bounding_box_xyxy"][1] < target["bounding_box_xyxy"][3] <= 96)

    def test_stratified_batches_consume_all_samples_once_and_mix_classes(self) -> None:
        batches = stratified_batches(tuple(range(192)), tuple(range(192, 288)), 8, 24105022)
        self.assertEqual(36, len(batches))
        self.assertEqual(list(range(288)), sorted(index for batch in batches for index in batch))
        self.assertTrue(all(5 <= sum(index < 192 for index in batch) <= 6 for batch in batches))

    def test_runtime_gate_accepts_only_exact_rx_6600_xt(self) -> None:
        import torch
        runtime = require_rx_6600_xt(torch)
        self.assertIn("RX 6600 XT", runtime["device_name"])
        self.assertFalse(runtime["cpu_fallback"])


if __name__ == "__main__":
    unittest.main()
