# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused checks for V4 data separation, glyph targets, sampler, and GPU gate."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
import uuid
from unittest.mock import patch

from ml.ocr.fragmented_xtick_detector_v3.gpu_train import require_rx_6600_xt, stratified_batches
from ml.ocr.fragmented_xtick_detector_v3.probe_materialize import LABELS as PROBE_LABELS
from ml.ocr.fragmented_xtick_detector_v4.admission import V4InputAdmissionError, load_train_inputs, read_bounded
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

    def test_hard_negative_mining_selects_only_highest_background_logits(self) -> None:
        import torch
        from ml.ocr.fragmented_xtick_detector_v4.train import hard_negative_mask
        logits = torch.tensor([[[[9.0, 8.0, 7.0, 6.0, 5.0]]]])
        positives = torch.tensor([[[[True, False, False, False, False]]]])
        mined = hard_negative_mask(logits, positives, top_k=2)
        self.assertEqual([1, 2], torch.nonzero(mined.reshape(-1), as_tuple=False).flatten().tolist())
        self.assertFalse(bool((mined & positives).any()))
        self.assertEqual(2, int(mined.sum()))

    def test_bounded_read_stops_at_limit_plus_one_and_rejects_oversize(self) -> None:
        artifacts = REPO_ROOT / "artifacts"
        with tempfile.TemporaryDirectory(dir=artifacts, prefix="v4-admission-test-") as temp:
            root = Path(temp)
            (root / "blob.bin").write_bytes(b"0123456789")
            real_open = Path.open
            read_sizes: list[int] = []

            class MeteredReader:
                def __init__(self, inner): self.inner = inner
                def __enter__(self): self.inner.__enter__(); return self
                def __exit__(self, *args): return self.inner.__exit__(*args)
                def read(self, size=-1): read_sizes.append(size); return self.inner.read(size)

            def open_spy(path, *args, **kwargs):
                return MeteredReader(real_open(path, *args, **kwargs))

            with patch.object(Path, "open", open_spy):
                with self.assertRaises(V4InputAdmissionError):
                    read_bounded(root, "blob.bin", 4)
            self.assertEqual([5], read_sizes)
            self.assertEqual(b"0123456789", read_bounded(root, "blob.bin", 10))

    def test_bounded_read_rejects_symlink_ancestor(self) -> None:
        artifacts = REPO_ROOT / "artifacts"
        with tempfile.TemporaryDirectory(dir=artifacts, prefix="v4-reparse-test-") as temp:
            root = Path(temp); target = root / "real"; target.mkdir()
            (target / "sample.bin").write_bytes(b"ok")
            alias = root / "alias"
            try:
                os.symlink(target, alias, target_is_directory=True)
            except (OSError, NotImplementedError) as error:
                self.skipTest(f"Windows symlink creation unavailable: {error}")
            with self.assertRaises(V4InputAdmissionError):
                read_bounded(root, "alias/sample.bin", 10)

    def test_admission_hash_binds_parsed_manifest_and_rejects_wrong_pin(self) -> None:
        import torch
        from hashlib import sha256
        manifest_path = REPO_ROOT / "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json"
        expected = sha256(manifest_path.read_bytes()).hexdigest()
        manifest, images, boxes, observed = load_train_inputs(torch, "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json", expected)
        self.assertEqual(expected, observed)
        self.assertEqual((288, 192, 96), (len(images), sum(b is not None for b in boxes), sum(b is None for b in boxes)))
        with self.assertRaises(V4InputAdmissionError):
            load_train_inputs(torch, "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json", "0" * 64)

    def test_failed_admission_writes_zero_step_receipt(self) -> None:
        from ml.ocr.fragmented_xtick_detector_v4.train import run_training
        artifacts = REPO_ROOT / "artifacts"
        output_name = f"fragmented-xtick-v4-p2-admission-failure-test-{uuid.uuid4().hex}"
        output = artifacts / output_name
        with tempfile.TemporaryDirectory(dir=artifacts, prefix="v4-failure-config-") as temp:
            config = json.loads((REPO_ROOT / "ml/ocr/fragmented_xtick_detector_v4/training/p2.json").read_bytes())
            config["planned_output_root"] = f"artifacts/{output_name}"
            config["train_manifest_sha256"] = "0" * 64
            config_path = Path(temp) / "bad-pin.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")
            try:
                with self.assertRaises(V4InputAdmissionError):
                    run_training(config_path)
                receipt = json.loads((output / "failure.json").read_bytes())
                self.assertEqual("failed", receipt["status"])
                self.assertEqual("train-input-admission", receipt["failure_phase"])
                self.assertEqual(0, receipt["optimizer_steps_completed"])
            finally:
                if output.exists():
                    shutil.rmtree(output)

    def test_p2_changes_only_loss_recipe_and_output_identity(self) -> None:
        p1 = json.loads((REPO_ROOT / "ml/ocr/fragmented_xtick_detector_v4/training/p1.json").read_bytes())
        p2 = json.loads((REPO_ROOT / "ml/ocr/fragmented_xtick_detector_v4/training/p2.json").read_bytes())
        for key in ("train_manifest_path", "dataset_family", "positive_count", "background_only_count", "canvas", "epochs", "batch_size", "optimizer", "learning_rate", "model_seed", "shuffle_seed", "allowed_input_roots"):
            self.assertEqual(p1[key], p2[key], key)
        self.assertEqual("artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json", p2["train_manifest_path"])
        self.assertEqual("artifacts/fragmented-xtick-v4-p2-hard-negative-v1", p2["planned_output_root"])
        self.assertEqual(32, p2["hard_negative_top_k_per_image"])


if __name__ == "__main__":
    unittest.main()
