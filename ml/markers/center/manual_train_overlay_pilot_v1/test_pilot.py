from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from ml.markers.center.manual_train_overlay_pilot_v1.plan import PilotError, build_plan
from ml.markers.center.manual_train_overlay_pilot_v1.render import render_one
from ml.markers.center.manual_train_overlay_pilot_v1 import render_captured as capture_renderer
from ml.markers.center.manual_train_overlay_pilot_v1.render_captured import CaptureRenderError, render_captured


MANIFEST_SHA = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"
LABEL_SCOPE = "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels"


def _hex(value: int) -> str:
    return hashlib.sha256(f"test-{value}".encode("ascii")).hexdigest()


def _manifest() -> dict:
    rows = []
    for index in range(104):
        rows.append({
            "caseId": _hex(index),
            "studyId": _hex(1000 + index),
            "projectSha256": _hex(2000 + index),
            "embeddedImageSha256": _hex(3000 + index),
            "imageWidth": 160,
            "imageHeight": 160,
            "labelingScope": LABEL_SCOPE,
            "curvePointCount": 1,
            "curvePoints": [{"x": 80.0, "y": 80.0}],
        })
    return {
        "schema": "graphreader.manual-train-only-source-capture.v1",
        "status": "completed_train_only_source_capture_unapproved",
        "split": "train",
        "scope": {"development_payload_reads": 0, "sealed_payload_reads": 0},
        "counts": {"train_projects": 104, "train_project_payloads_opened": 104, "development_project_payloads_opened": 0},
        "rows": rows,
    }


class PilotTests(unittest.TestCase):
    def test_plan_is_deterministic_and_groups_study_and_exact_image(self) -> None:
        source = _manifest()
        source["rows"][0]["embeddedImageSha256"] = source["rows"][1]["embeddedImageSha256"]
        first = build_plan(source, MANIFEST_SHA)
        second = build_plan(source, MANIFEST_SHA)
        self.assertEqual(first, second)
        self.assertEqual(first["study_image_connected_component_count"], 103)
        self.assertEqual(len(first["sources"]), 2)
        self.assertNotEqual(first["sources"][0]["component_sha256"], first["sources"][1]["component_sha256"])
        self.assertTrue(all(row["assigned_partition"].startswith("internal_train_") for row in first["sources"]))
        self.assertIn("not official dev", first["split_claim"])

    def test_plan_rejects_wrong_manifest_identity_and_negative_scope(self) -> None:
        with self.assertRaises(PilotError):
            build_plan(_manifest(), "0" * 64)
        source = _manifest()
        source["rows"][0]["labelingScope"] = "curve point is marker truth"
        with self.assertRaises(PilotError):
            build_plan(source, MANIFEST_SHA)

    def test_overlay_changes_only_patch_and_labels_only_injected_shapes(self) -> None:
        with tempfile.TemporaryDirectory(prefix="marker-overlay-pilot-", dir=r"E:\Graph-auto-reader\artifacts\goal22-runs") as temp:
            temp_path = Path(temp)
            source_path = temp_path / "source.png"
            output_path = temp_path / "overlay.png"
            repeat_path = temp_path / "overlay-repeat.png"
            source = Image.new("RGBA", (128, 128), (120, 130, 140, 255))
            source.save(source_path, format="PNG", optimize=False)
            expected = hashlib.sha256(source_path.read_bytes()).hexdigest()
            row = {
                "image_width": 128,
                "image_height": 128,
                "anchor_pixel": {"x": 64, "y": 64},
                "distractor_pixel": {"x": 86, "y": 76},
            }
            result = render_one(source_path, expected, row, output_path)
            render_one(source_path, expected, row, repeat_path)
            self.assertEqual(output_path.read_bytes(), repeat_path.read_bytes())
            self.assertTrue(result["pixel_equivalence_outside_patch"])
            self.assertEqual(result["source_pixel_change_scope"], "opaque patch only")
            self.assertEqual([item["label"] for item in result["injected_labels"]], ["injected_positive_only", "injected_distractor_only"])
            with Image.open(source_path) as original, Image.open(output_path) as overlay:
                self.assertEqual(original.getpixel((0, 0)), overlay.getpixel((0, 0)))
                self.assertEqual(overlay.getpixel((64, 64)), (0, 0, 0, 255))
                self.assertEqual(overlay.getpixel((70, 64)), (255, 255, 255, 255))
                self.assertEqual(overlay.getpixel((86, 76)), (0, 0, 0, 255))

    def test_overlay_rejects_source_hash_or_dimensions_mismatch(self) -> None:
        with tempfile.TemporaryDirectory(prefix="marker-overlay-pilot-", dir=r"E:\Graph-auto-reader\artifacts\goal22-runs") as temp:
            source_path = Path(temp) / "source.png"
            output_path = Path(temp) / "overlay.png"
            Image.new("RGBA", (128, 128), "white").save(source_path)
            row = {"image_width": 127, "image_height": 128, "anchor_pixel": {"x": 64, "y": 64}, "distractor_pixel": {"x": 86, "y": 76}}
            actual = hashlib.sha256(source_path.read_bytes()).hexdigest()
            with self.assertRaisesRegex(ValueError, "bytes differ"):
                render_one(source_path, "0" * 64, row, output_path)
            with self.assertRaisesRegex(ValueError, "dimensions differ"):
                render_one(source_path, actual, row, output_path)

    def test_receipt_bound_capture_render_and_tamper_rejection(self) -> None:
        root = Path(r"E:\Graph-auto-reader")
        with tempfile.TemporaryDirectory(prefix="marker-overlay-receipt-test-", dir=root / "artifacts/goal22-runs") as temp:
            base = Path(temp)
            source_dir = base / "source"
            source_dir.mkdir()
            sources = []
            captured_rows = []
            for index, (partition, color) in enumerate((("internal_train_build", (100, 120, 140, 255)), ("internal_train_holdout", (140, 120, 100, 255)))):
                image_path = source_dir / f"source-{index}.png"
                Image.new("RGBA", (128, 128), color).save(image_path)
                image_bytes = image_path.read_bytes()
                digest = hashlib.sha256(image_bytes).hexdigest()
                identities = [_hex(4000 + index), _hex(5000 + index), _hex(6000 + index), _hex(7000 + index)]
                source = {
                    "assigned_partition": partition,
                    "component_sha256": identities[0],
                    "case_id": identities[1],
                    "study_id": identities[2],
                    "project_sha256": identities[3],
                    "image_sha256": digest,
                    "image_width": 128,
                    "image_height": 128,
                    "anchor_pixel": {"x": 64, "y": 64},
                    "anchor_source_point": {"x": 64.0, "y": 64.0},
                    "distractor_pixel": {"x": 86, "y": 76},
                    "curve_point_count": 1,
                }
                sources.append(source)
                captured_rows.append({
                    "index": index,
                    "assigned_partition": partition,
                    "case_id": source["case_id"],
                    "study_id": source["study_id"],
                    "project_sha256": source["project_sha256"],
                    "image_sha256": digest,
                    "extracted_png_sha256": digest,
                    "width": 128,
                    "height": 128,
                    "byte_length": len(image_bytes),
                    "path": image_path.name,
                    "curve_point_count": 1,
                })
            protocol = json.loads((root / "ml/markers/center/manual_train_overlay_pilot_v1/protocol.json").read_bytes())
            protocol_sha = hashlib.sha256((root / "ml/markers/center/manual_train_overlay_pilot_v1/protocol.json").read_bytes()).hexdigest()
            plan = {
                "schema": "graphreader.manual-train-marker-overlay-pilot-plan.v1",
                "status": "frozen_plan_not_yet_materialized",
                "protocol_sha256": protocol_sha,
                "source_capture_manifest_sha256": "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57",
                "source_capture_label_scope": LABEL_SCOPE,
                "source_project_count": 104,
                "split_claim": "assigned signature; not official dev",
                "sources": sources,
            }
            plan_path = base / "plan.json"
            plan_path.write_text(json.dumps(plan, sort_keys=True), encoding="utf-8")
            plan_sha = hashlib.sha256(plan_path.read_bytes()).hexdigest()
            attempt_events = [
                {"event_name": "source_started", "source_index": 0},
                {"event_name": "source_completed", "source_index": 0, "extracted_png_sha256": captured_rows[0]["image_sha256"], "byte_length": captured_rows[0]["byte_length"]},
                {"event_name": "source_started", "source_index": 1},
                {"event_name": "source_completed", "source_index": 1, "extracted_png_sha256": captured_rows[1]["image_sha256"], "byte_length": captured_rows[1]["byte_length"]},
                {"event_name": "sources_complete", "source_count": 2},
            ]
            attempt_path = source_dir / "source-attempt.jsonl"
            attempt_path.write_text("".join(json.dumps(event, separators=(",", ":")) + "\n" for event in attempt_events), encoding="utf-8")
            receipt = {
                "schema": "graphreader.manual-train-marker-overlay-pilot-source-extraction.v1",
                "status": "train_only_source_pngs_extracted_overlay_not_yet_rendered",
                "plan_sha256": plan_sha,
                "protocol_sha256": protocol_sha,
                "source_manifest_sha256": "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57",
                "capture_attempt_sha256": hashlib.sha256(attempt_path.read_bytes()).hexdigest(),
                "assignment_sha256": _hex(8000),
                "selected_inventory_sha256": _hex(8001),
                "extracted_sources": 2,
                "capture_attempt_event_count": 5,
                "development_payload_reads": 0,
                "sealed_payload_reads": 0,
                "optimizer_steps": 0,
                "training_admitted": False,
                "runtime": {
                    "ExportAssemblySha256": protocol["runtime_assemblies"]["GraphReader.Export"],
                    "ExtractorAssemblySha256": protocol["runtime_assemblies"]["GraphReader.MarkerTrainOverlayPilot"],
                },
                "sources": captured_rows,
            }
            receipt_path = source_dir / "source-receipt.json"
            receipt_path.write_text(json.dumps(receipt, sort_keys=True), encoding="utf-8")
            receipt_sha = hashlib.sha256(receipt_path.read_bytes()).hexdigest()
            renderer_sha = hashlib.sha256(Path(capture_renderer.__file__).read_bytes()).hexdigest()
            output = base / "rendered"
            result = render_captured(plan_path, plan_sha, receipt_path, receipt_sha, renderer_sha, output)
            self.assertEqual(result["source_count"], 2)
            self.assertEqual(result["optimizer_steps"], 0)
            self.assertFalse(result["training_admitted"])
            saved = json.loads((output / "receipt.json").read_bytes())
            self.assertTrue(all(row["pixel_equivalence_outside_patch"] for row in saved["rows"]))
            self.assertEqual(set(saved["renderer"]), {"python", "pillow"})
            self.assertEqual(saved["source_extraction_receipt_sha256"], receipt_sha)
            with self.assertRaises(CaptureRenderError):
                render_captured(plan_path, plan_sha, receipt_path, "0" * 64, renderer_sha, base / "tampered")
            self.assertFalse((base / "tampered").exists())


if __name__ == "__main__":
    unittest.main()
