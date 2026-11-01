# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import hashlib
import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

from PIL import Image

from ml.markers.center.manual_train_overlay_corpus_v1 import materialize as materializer
from ml.markers.center.manual_train_overlay_corpus_v1.materialize import MaterializationError, materialize_source_views


ROOT = Path(r"E:\Graph-auto-reader")


class StreamingOverlayTests(unittest.TestCase):
    def test_cli_materializes_a_complete_synthetic_source(self) -> None:
        # Exercise the real CLI and receipt path with synthetic pixels only.
        with tempfile.TemporaryDirectory(prefix="manual-overlay-cli-", dir=ROOT / "artifacts/goal22-runs") as temp:
            root = Path(temp)
            source_file = root / materializer.RENDERER_RELATIVE_PATH
            source_file.parent.mkdir(parents=True)
            shutil.copyfile(Path(materializer.__file__), source_file)
            source_sha = hashlib.sha256(source_file.read_bytes()).hexdigest()
            original_protocol = json.loads((ROOT / "ml/markers/center/manual_train_overlay_corpus_v1/protocol.json").read_bytes())
            protocol = {
                "selection": {"max_total_source_png_bytes": materializer.MAX_SOURCE_EXTRACTION_BYTES},
                "source_files": {materializer.RENDERER_RELATIVE_PATH: source_sha},
                "runtime_assemblies": {"GraphReader.Export": "a" * 64, "GraphReader.MarkerTrainOverlayCorpus": "b" * 64},
                "rendering": original_protocol["rendering"],
            }
            protocol_path = source_file.with_name("protocol.json")
            protocol_path.write_text(json.dumps(protocol), encoding="utf-8")
            protocol_sha = hashlib.sha256(protocol_path.read_bytes()).hexdigest()
            run_root = root / "artifacts/goal22-runs"
            plan_dir = run_root / "marker-train-overlay-corpus-plan-v7"
            capture_dir = run_root / "marker-train-overlay-corpus-extract-v7"
            plan_dir.mkdir(parents=True)
            capture_dir.mkdir()
            image_path = capture_dir / "source-000.png"
            with Image.new("RGBA", (256, 192), "white") as source_image:
                source_image.save(image_path, format="PNG")
            image_bytes = image_path.read_bytes()
            image_sha = hashlib.sha256(image_bytes).hexdigest()
            source = {
                "source_index": 0, "case_id": "case", "study_id": "study", "component_sha256": "c" * 64,
                "project_sha256": "d" * 64, "image_sha256": image_sha, "assigned_partition": "internal_train_build",
                "image_width": 256, "image_height": 192, "anchor_pixel": {"x": 128, "y": 96},
            }
            plan = {
                "schema": materializer.PLAN_SCHEMA, "status": "frozen_train_only_expansion_plan_unmaterialized",
                "protocol_sha256": protocol_sha, "source_rows": [source], "recipes": protocol["rendering"]["recipes"],
                "limitations": ["synthetic test only"],
            }
            plan_path = plan_dir / "plan.json"
            plan_path.write_text(json.dumps(plan), encoding="utf-8")
            plan_sha = hashlib.sha256(plan_path.read_bytes()).hexdigest()
            journal = capture_dir / "source-attempt.jsonl"
            journal.write_text("\n".join(json.dumps(row) for row in [
                {"event_name": "source_started", "source_index": 0},
                {"event_name": "source_completed", "source_index": 0},
                {"event_name": "sources_complete", "source_count": 1},
            ]) + "\n", encoding="utf-8")
            captured = {key: source[key] for key in ("source_index", "case_id", "study_id", "component_sha256", "image_sha256", "project_sha256", "assigned_partition")}
            captured.update(path=image_path.name, extracted_png_sha256=image_sha, byte_length=len(image_bytes))
            extraction = {
                "schema": materializer.EXTRACTION_SCHEMA, "status": "train_only_source_group_pngs_extracted_not_materialized",
                "plan_sha256": plan_sha, "protocol_sha256": protocol_sha, "source_manifest_sha256": materializer.MANIFEST_SHA,
                "development_payload_reads": 0, "sealed_payload_reads": 0, "optimizer_steps": 0, "training_admitted": False,
                "selected_sources": 1, "extracted_sources": 1, "failed_sources": 0, "extracted_image_bytes": len(image_bytes),
                "runtime": {"ExportAssemblySha256": "a" * 64, "ExtractorAssemblySha256": "b" * 64},
                "capture_attempt_sha256": hashlib.sha256(journal.read_bytes()).hexdigest(), "capture_attempt_event_count": 3,
                "sources": [captured],
            }
            receipt_path = capture_dir / "source-receipt.json"
            receipt_path.write_text(json.dumps(extraction), encoding="utf-8")
            receipt_sha = hashlib.sha256(receipt_path.read_bytes()).hexdigest()
            argv = ["materialize", "--expected-plan-sha256", plan_sha, "--expected-extraction-receipt-sha256", receipt_sha,
                    "--expected-materializer-sha256", source_sha, "--output-dir", "artifacts/goal22-runs/rendered"]
            output = io.StringIO()
            with patch.object(materializer, "ROOT", root), patch.object(sys, "argv", argv), redirect_stdout(output):
                self.assertEqual(materializer.main(), 0)
            result = json.loads(output.getvalue())
            self.assertEqual(result["overlay_count"], 10)
            self.assertFalse(result["training_admitted"])
            published = json.loads((run_root / "rendered/receipt.json").read_bytes())
            self.assertEqual(published["label_counts"], {"injected_positive_only": 5, "injected_distractor_only": 5})
            self.assertTrue(all(row["pixel_equivalence_outside_patch"] for row in published["rows"]))

    def test_ten_fixed_recipes_are_deterministic_and_patch_bounded(self) -> None:
        protocol = json.loads((ROOT / "ml/markers/center/manual_train_overlay_corpus_v1/protocol.json").read_bytes())
        recipes = protocol["rendering"]["recipes"]
        source = {
            "source_index": 3,
            "assigned_partition": "internal_train_build",
            "component_sha256": hashlib.sha256(b"component").hexdigest(),
            "image_sha256": hashlib.sha256(b"source").hexdigest(),
            "image_width": 256,
            "image_height": 192,
            "anchor_pixel": {"x": 128, "y": 96},
        }
        with tempfile.TemporaryDirectory(prefix="manual-overlay-corpus-test-", dir=ROOT / "artifacts/goal22-runs") as temp:
            base = Path(temp)
            first_dir, second_dir = base / "first", base / "second"
            first_dir.mkdir()
            second_dir.mkdir()
            background = Image.new("RGBA", (256, 192), (130, 140, 150, 255))
            first = materialize_source_views(background, source, recipes, first_dir,
                protocol["rendering"]["patch_width_px"], protocol["rendering"]["patch_height_px"], tuple(protocol["rendering"]["patch_fill_rgba"]))
            second = materialize_source_views(background, source, recipes, second_dir,
                protocol["rendering"]["patch_width_px"], protocol["rendering"]["patch_height_px"], tuple(protocol["rendering"]["patch_fill_rgba"]))
            self.assertEqual(len(first), 10)
            self.assertEqual([row["recipe_id"] for row in first], [row["recipe_id"] for row in second])
            self.assertEqual([row["output_sha256"] for row in first], [row["output_sha256"] for row in second])
            self.assertEqual(sum(row["label"] == "injected_positive_only" for row in first), 5)
            self.assertEqual(sum(row["label"] == "injected_distractor_only" for row in first), 5)
            self.assertTrue(all(row["pixel_equivalence_outside_patch"] and row["native_content_labeling"] == "unknown_everywhere" for row in first))
            patch = (72, 56, 184, 136)
            for row in first:
                path = first_dir / Path(row["output_path"]).name
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), row["output_sha256"])
                with Image.open(path) as rendered:
                    self.assertEqual(rendered.size, background.size)
                    self.assertEqual(rendered.getpixel((1, 1)), background.getpixel((1, 1)))
                    self.assertEqual(rendered.getpixel((100, 70)), (255, 255, 255, 255))

    def test_rejects_patch_outside_source_bounds(self) -> None:
        with tempfile.TemporaryDirectory(prefix="manual-overlay-corpus-bounds-", dir=ROOT / "artifacts/goal22-runs") as temp:
            source = {"source_index": 0, "assigned_partition": "internal_train_build", "component_sha256": "0" * 64,
                      "image_sha256": "1" * 64, "image_width": 128, "image_height": 128,
                      "anchor_pixel": {"x": 20, "y": 64}}
            recipes = [{"id": "filled_circle_r3", "label": "injected_positive_only", "shape": "filled_circle", "radius_px": 3, "stroke_px": 0}]
            with self.assertRaises(MaterializationError):
                materialize_source_views(Image.new("RGBA", (128, 128), "white"), source, recipes, Path(temp))
            self.assertEqual(list(Path(temp).iterdir()), [])

    def test_overlay_byte_limit_is_checked_before_file_creation(self) -> None:
        with tempfile.TemporaryDirectory(prefix="manual-overlay-corpus-budget-", dir=ROOT / "artifacts/goal22-runs") as temp:
            output = Path(temp)
            source = {"source_index": 0, "assigned_partition": "internal_train_build", "component_sha256": "0" * 64,
                      "image_sha256": "1" * 64, "image_width": 256, "image_height": 192,
                      "anchor_pixel": {"x": 128, "y": 96}}
            recipe = [{"id": "filled_circle_r3", "label": "injected_positive_only", "shape": "filled_circle", "radius_px": 3, "stroke_px": 0}]
            with Image.new("RGBA", (256, 192), "white") as base:
                with self.assertRaises(MaterializationError):
                    materialize_source_views(base, source, recipe, output, remaining_output_bytes=0)
            self.assertEqual(list(output.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
