# SPDX-License-Identifier: Apache-2.0
"""Synthetic-only contract tests for the candidate tensor review bridge."""

from __future__ import annotations

import hashlib
import inspect
import json
import struct
import tempfile
import unittest
import zlib
from pathlib import Path
from unittest.mock import patch

from . import bridge as bridge_module
from . import runner as runner_module
from .bridge import (
    ARTIFACT_REPORT_SHA256,
    ASSOCIATION_REPORT_SHA256,
    CORPUS_BUILDER_BINDING_SHA256,
    FULL_CAPTURE_BINDING_SHA256,
    FULL_CAPTURE_COMPLETE_SHA256,
    FULL_CAPTURE_REPORT_SHA256,
    IDENTITY_METADATA_SHA256,
    SOURCE_BINDINGS,
    _canonical,
    _full_capture_candidate_evidence,
    _materialize_source_core,
    materialize_bound_full_capture_panel,
    tensor_f32le,
    validate_train_source_groups,
    verify_source_bindings,
)


ASSOCIATION_SHA = "91a4cfd05f91d155bd95d30877d6feb17f742573d4f4ec0a91f27a450ec59244"
ARTIFACT_SHA = "0db3f20d13b9aa730148111d4e88064532f74caa0c02ea9c337b65eacd4f5c51"
CAPTURE_SHA = FULL_CAPTURE_COMPLETE_SHA256
BINDING_SHA = FULL_CAPTURE_BINDING_SHA256


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def png_chunk(name: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data) & 0xFFFFFFFF)


def tiny_png(width: int, height: int) -> bytes:
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)
    rows = b"".join(b"\x00" + bytes([255]) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", ihdr) +
            png_chunk(b"IDAT", zlib.compress(rows)) + png_chunk(b"IEND", b""))


def identity_matrix() -> dict[str, float]:
    return {"m11": 1, "m12": 0, "m13": 0, "m21": 0, "m22": 1, "m23": 0}


def patch_matrix(center: list[float], radius: float, scale: float, minimum: float) -> list[float]:
    half = max(minimum, radius * scale)
    ratio = 2 * half / 32
    return [ratio, 0.0, center[0] - half + half / 32,
            0.0, ratio, center[1] - half + half / 32, 0.0, 0.0, 1.0]


def evidence(source: dict, candidate_id: str, center: list[float], radius: float,
             view: str, pixel: float) -> bytes:
    scale, minimum = (2.25, 4.0) if view == "local" else (6.75, 12.0)
    key = hashlib.sha256(
        f"{source['source_id']}\0{source['panel_id']}\0{candidate_id}".encode()).hexdigest()
    input_row = {
        "schema": "graphreader.marker-context-crop-evidence.v1",
        "scope": "synthetic_fixture", "caseId": key[:40], "sourceImage": "Original",
        "width": source["width"], "height": source["height"], "channelCount": 1,
        "channelsFirstPixels": [0.5] * (source["width"] * source["height"]),
        "ocrMask": [0.0] * (source["width"] * source["height"]),
        "artifactMask": [0.0] * (source["width"] * source["height"]),
        "originalToFrame": identity_matrix(),
        "markerCenterOriginalPixels": {"x": center[0], "y": center[1]},
        "markerRadiusOriginalPixels": radius, "markerId": candidate_id,
        "options": {"width": 32, "height": 32, "outputChannels": 1,
                    "radiusScale": scale, "minimumHalfExtentFramePixels": minimum,
                    "paddingValue": 0.0, "originalPixelContentBounds": None},
    }
    result = {
        "input": input_row, "outputShapeNchw": [1, 1, 32, 32],
        "productionTensor": [pixel] * 1024,
        "patchToFrameMatrix3x3": patch_matrix(center, radius, scale, minimum),
        "patchToOriginalMatrix3x3": patch_matrix(center, radius, scale, minimum),
    }
    return json.dumps(result, separators=(",", ":"), allow_nan=False).encode()


def evidence_ref(raw: bytes) -> dict:
    return {"raw_bytes": raw, "sha256": digest(raw)}


def visibility_proof(source: dict, glyph_id: str, center: list[float], box: list[float]) -> dict:
    proof = {
        "schema": "graphreader.legend-glyph-visibility-proof.v1",
        "method": "single_glyph_ablation_rgb_diff",
        "scene_sha256": source["scene_sha256"], "annotation_sha256": source["annotation_sha256"],
        "source_reference": "synthetic-legend:0", "glyph_id": glyph_id,
        "center": center, "glyph_box": box, "width": source["width"], "height": source["height"],
        "source_raster_sha256": source["source_raster_sha256"],
        "rendered_rgb_sha256": "1" * 64, "renderer_bundle_sha256": "2" * 64,
        "target_draw_id": "renderer_call:3", "matching_draw_ids": ["renderer_call:3"],
        "changed_pixel_count": 4, "outside_box_changed_pixel_count": 0,
        "ablated_rgb_sha256": "3" * 64,
    }
    proof["proof_sha256"] = digest(_canonical(proof))
    return proof


def fixture(candidates_override=None):
    image = tiny_png(8, 8)
    source_id, panel_id = "synthetic-source-a", "synthetic-panel-a"
    source = {
        "source_id": source_id, "source_group_id": "synthetic-train-group-a",
        "split": "train", "scene_id": "synthetic-scene-a", "scene_sha256": "4" * 64,
        "annotation_sha256": "5" * 64, "source_raster_sha256": digest(image),
        "panel_id": panel_id, "panel_image_sha256": digest(image),
        "panel_result_sha256": "0" * 64, "frame_f32le_sha256": digest(tensor_f32le([0.5] * 64, expected_count=64)),
        "width": 8, "height": 8, "capture_report_sha256": FULL_CAPTURE_REPORT_SHA256,
        "capture_complete_sha256": FULL_CAPTURE_COMPLETE_SHA256,
        "capture_binding_sha256": FULL_CAPTURE_BINDING_SHA256,
        "builder_binding_sha256": CORPUS_BUILDER_BINDING_SHA256,
        "identity_metadata_sha256": IDENTITY_METADATA_SHA256,
        "association_report_sha256": ASSOCIATION_SHA, "artifact_report_sha256": ARTIFACT_SHA,
        "capture_status": "completed",
        "panel_status_counts": {"completed": 1, "failed": 0, "not_processed": 0},
    }
    candidates = candidates_override or [
        {"marker_id": "candidate-positive", "center": {"x": 2.0, "y": 2.0}, "radius": 1.0},
        {"marker_id": "candidate-artifact", "center": {"x": 5.0, "y": 5.0}, "radius": 1.0},
        {"marker_id": "candidate-unmatched", "center": {"x": 7.0, "y": 2.0}, "radius": 1.0},
        {"marker_id": "candidate-overlay", "center": {"x": 7.0, "y": 7.0}, "radius": 1.0},
    ]
    source["candidate_count"] = len(candidates)
    panel = {"source_id": source_id, "panel_id": panel_id, "panel_sha256": digest(image),
             "center_markers": candidates, "symbol_input_ids": [], "symbol_crop_input_ids": []}
    panel_raw = json.dumps(panel, separators=(",", ":")).encode()
    source["panel_result_sha256"] = digest(panel_raw)
    sampled = {}
    for candidate in candidates:
        point = [candidate["center"]["x"], candidate["center"]["y"]]
        sampled[candidate["marker_id"]] = {
            "local": evidence_ref(evidence(source, candidate["marker_id"], point, candidate["radius"], "local", 0.2)),
            "wide": evidence_ref(evidence(source, candidate["marker_id"], point, candidate["radius"], "wide", 0.7)),
        }
    glyph_id = "legend-glyph-a"
    glyph_center = [5.0, 5.0]
    glyph = {
        "kind": "legend_symbol", "source_id": source_id,
        "scene_sha256": source["scene_sha256"], "annotation_sha256": source["annotation_sha256"],
        "glyph_id": glyph_id, "source_reference": "synthetic-legend:0",
        "center": glyph_center, "glyph_box": [4.0, 4.0, 2.0, 2.0],
        "association_status": "matched_artifact_review_only",
        "visibility_evidence": visibility_proof(source, glyph_id, glyph_center, [4.0, 4.0, 2.0, 2.0]),
    }
    glyph["visibility_proof_sha256"] = glyph["visibility_evidence"]["proof_sha256"]
    positive = {"truth_id": "marker-truth-a", "source_id": source_id,
                "annotation_sha256": source["annotation_sha256"],
                "coordinate_space": "original_source_pixels", "center": [2.0, 2.0],
                "bin": "connected_positive"}
    return source, panel_raw, image, sampled, [positive], [glyph], []


def full_capture_fixture():
    """Build the exact saved full-capture panel JSON shape with synthetic bytes."""
    args = fixture()
    source, compact_panel_raw, image, evidence_map, truths, glyphs, overlays = args
    compact = json.loads(compact_panel_raw)
    markers = []
    views = []
    for marker in compact["center_markers"]:
        actual_marker = {**marker, "coordinate_space": "original_pixels"}
        actual_marker["center"] = {**actual_marker["center"], "is_finite": True}
        markers.append(actual_marker)
        pair = evidence_map[marker["marker_id"]]
        local_doc = json.loads(pair["local"]["raw_bytes"])
        wide_doc = json.loads(pair["wide"]["raw_bytes"])
        local_values, wide_values = local_doc["productionTensor"], wide_doc["productionTensor"]
        views.append({
            "marker": actual_marker, "local_content_bounds": None,
            "local": local_values, "wide": wide_values,
            "local_sha256": digest(tensor_f32le(local_values)),
            "wide_sha256": digest(tensor_f32le(wide_values)),
        })
    width, height = source["width"], source["height"]
    frame_values = [0.5] * (width * height)
    zero_mask = [0.0] * (width * height)
    frame = {
        "width": width, "height": height, "channel_count": 1,
        "channels_first_pixels": frame_values, "source_image": 0,
        "original_to_frame": {"m11": 1, "m12": 0, "m13": 0, "m21": 0, "m22": 1,
                               "m23": 0, "determinant": 1, "is_invertible": True},
        "ocr_mask": {"width": width, "height": height, "values": zero_mask},
        "artifact_mask": {"width": width, "height": height, "values": zero_mask},
    }
    crop = {"x": 0, "y": 0, "width": width, "height": height,
            "right": width, "bottom": height, "area": width * height, "is_valid": True}
    actual_panel = {
        "source_id": source["source_id"], "case_id": "fixture-case",
        "scene_id": source["scene_id"], "scene_sha256": source["scene_sha256"],
        "seed": 123, "panel_id": source["panel_id"], "panel_sha256": source["panel_image_sha256"],
        "provenance": {"source_sha256": source["source_raster_sha256"],
            "panel_image_sha256": source["panel_image_sha256"], "source_width": width, "source_height": height,
            "requested_crop_source_pixels": crop, "encoded_crop_source_pixels": crop,
            "source_to_panel_matrix": [1,0,0,0,1,0,0,0,1],
            "panel_to_source_matrix": [1,0,0,0,1,0,0,0,1], "identity": True},
        "frame": frame, "center_markers": markers, "ocr_regions": [],
        "classifier_inputs": markers, "symbol_input_ids": [], "symbol_crop_input_ids": [],
        "original_pixel_content_bounds": {},
        "sampled_local_and_experimental_wide_views": views,
    }
    panel_bytes = json.dumps(actual_panel, separators=(",", ":"), allow_nan=False).encode()
    source["panel_result_sha256"] = digest(panel_bytes)
    source["candidate_count"] = len(markers)
    source["frame_f32le_sha256"] = digest(tensor_f32le(frame_values, expected_count=width * height))
    return source, panel_bytes, image, truths, glyphs, overlays


def bound_inventory_fixture(*, unmatched_glyph=False):
    source, panel_bytes, image, _, _, _ = full_capture_fixture()
    scene = {"scene_id": "synthetic-scene-bound", "canvas": {"width": 8, "height": 8}}
    scene_sha = digest(_canonical(scene))
    panel = json.loads(panel_bytes)
    panel["scene_id"] = scene["scene_id"]
    panel["scene_sha256"] = scene_sha
    panel_bytes = json.dumps(panel, separators=(",", ":"), allow_nan=False).encode()
    source["scene_id"] = scene["scene_id"]
    source["scene_sha256"] = scene_sha
    source["panel_result_sha256"] = digest(panel_bytes)

    glyph_center, glyph_box = ([2.0, 2.0], [1.0, 1.0, 2.0, 2.0]) if unmatched_glyph else ([5.0, 5.0], [4.0, 4.0, 2.0, 2.0])
    annotation = {
        "scene_id": scene["scene_id"], "coordinate_space": "original_pixels",
        "contextual_negative_profile": {"id": "synthetic-contextual-negatives-v1", "split": "train",
                                         "source_scene_id": scene["scene_id"]},
        "canvas": {"width": 8, "height": 8},
        "panels": [{
            "panel_id": "synthetic-panel", "markers": [{"point_id": "point-a", "marker_id": "marker-a",
                "panel_id": "synthetic-panel", "coordinate_space": "original_pixels", "center": [2.0, 2.0]}],
            "edges": [], "legends": [{"legend_id": "legend-a", "visible": True,
                "box": glyph_box, "entries": [{"text": "A", "glyph_center": glyph_center, "glyph_box": glyph_box}]}],
            "texts": [{"text_id": "text-a", "role": "legend_text", "visible": True,
                "text": "A", "rendered_pixel_box": glyph_box}],
        }],
        "hard_negatives": [{"hard_negative_id": "glyph-a", "kind": "legend_symbol",
            "source_reference": "legend-a:0", "panel_id": "synthetic-panel",
            "coordinate_space": "original_pixels", "excluded_from_marker_mask": True,
            "context": {"category": "legends", "legend_id": "legend-a", "text_ids": ["text-a"]},
            "geometry": {"center": glyph_center, "box": glyph_box}}],
    }
    annotation_sha = digest(_canonical(annotation))
    source["annotation_sha256"] = annotation_sha
    case_id, seed = "synthetic-case-bound", 20261031
    source_id = source["source_id"]
    source["source_group_id"] = source_id
    panel_id = source["panel_id"]
    identity = {"source_id": source_id, "case_id": case_id, "scene_id": scene["scene_id"],
        "scene_sha256": scene_sha, "image_sha256": source["source_raster_sha256"], "seed": seed,
        "source_id_status": "predeclared_not_yet_imported"}
    annotation_empty = {"scene_id": "other-scene", "coordinate_space": "original_pixels",
        "contextual_negative_profile": {"id": "synthetic-contextual-negatives-v1", "split": "train",
                                         "source_scene_id": "other-scene"},
        "canvas": {"width": 8, "height": 8}, "panels": [], "hard_negatives": []}
    identity_rows, capture_rows, truth_rows, artifact_rows, events = [], [], [], [], []
    for index in range(64):
        sid = source_id if index == 0 else f"synthetic-source-{index:02d}"
        cid = case_id if index == 0 else f"synthetic-case-{index:02d}"
        scid = scene["scene_id"] if index == 0 else f"other-scene-{index:02d}"
        ssha = scene_sha if index == 0 else digest(_canonical({"scene_id": scid,
            "canvas": {"width": 8, "height": 8}}))
        image_sha = source["source_raster_sha256"] if index == 0 else f"{index:064x}"
        sd = seed if index == 0 else 20262000 + index
        identity_row = identity if index == 0 else {"source_id": sid, "case_id": cid, "scene_id": scid,
            "scene_sha256": ssha, "image_sha256": image_sha, "seed": sd,
            "source_id_status": "predeclared_not_yet_imported"}
        identity_rows.append(identity_row)
        pid = panel_id if index == 0 else f"synthetic-panel-{index:02d}"
        panel_sha = source["panel_result_sha256"] if index == 0 else f"{index+100:064x}"
        capture_rows.append({**{k: identity_row[k] for k in ("source_id", "case_id", "scene_id", "scene_sha256", "seed")},
            "image_sha256": image_sha, "status": "completed", "failure": None, "panel_count": 1,
            "panels": [{"panel_id": pid, "status": "completed", "marker_count": source["candidate_count"] if index == 0 else 0,
                "provenance": {"source_sha256": image_sha, "panel_image_sha256": image_sha}}]})
        events.append({"source_id": sid, "panel_id": pid, "file": f"panel-{index:04d}.json", "sha256": panel_sha})
        if index == 0:
            truth_rows.append({"image_sha256": image_sha, "case_id": cid, "scene": scene,
                "annotation": annotation, "overlay": {"split": "train", "scene_id": scid,
                    "scene_sha256": ssha, "status": "review_only", "materialized": True,
                    "optimization_allowed": False, "center": [7, 7]}})
            glyphs = [{"glyph_id": "glyph-a", "source_reference": "legend-a:0", "center": glyph_center,
                "glyph_box": glyph_box, "visible_changed_pixels": 4, "visibility_proof_sha256": "1"*64,
                "rendered_rgb_sha256": "2"*64, "ablated_rgb_sha256": "3"*64,
                "association_status": "unmatched_review_only" if unmatched_glyph else "matched_artifact_review_only"}]
        else:
            truth_rows.append({"image_sha256": image_sha, "case_id": cid, "scene": {"scene_id": scid,
                    "canvas": {"width": 8, "height": 8}},
                "annotation": annotation_empty | {"scene_id": scid, "contextual_negative_profile": {
                    "id": "synthetic-contextual-negatives-v1", "split": "train", "source_scene_id": scid}},
                "overlay": {"split": "train", "scene_id": scid, "scene_sha256": ssha,
                    "status": "review_only", "materialized": True, "optimization_allowed": False, "center": [1, 1]}})
            glyphs = []
        artifact_rows.append({**{k: identity_row[k] for k in ("source_id", "scene_id", "scene_sha256", "seed")},
            "image_sha256": image_sha, "panel_id": pid, "panel_result_sha256": panel_sha,
            "status": "completed", "legend_glyph_count": len(glyphs), "glyphs": glyphs})
    truth_doc = {"schema": "marker-context-train-truth-v5", "mode": "full", "sources": truth_rows,
                 "training_approved": False}
    truth_bytes = _canonical(truth_doc)
    identity_doc = {"schema": "marker-context-train-identity-v5", "revision": "fixture",
                    "mode": "full", "project_id": "fixture", "sources": identity_rows,
                    "training_approved": False}
    identity_bytes = _canonical(identity_doc)
    complete_doc = {"schema": "marker-context-train-corpus-complete-v5", "mode": "full",
        "builder_binding_sha256": CORPUS_BUILDER_BINDING_SHA256, "source_count": 64,
        "truth_sha256": digest(truth_bytes), "training_approved": False}
    complete_bytes = _canonical(complete_doc)
    capture_doc = {"schema": "graphreader.marker-context-bound-input-full-capture-report.v1",
        "capture_binding_sha256": FULL_CAPTURE_BINDING_SHA256, "source_count": 64,
        "all_sources_succeeded": True, "training_approved": False, "truth_content_read": False,
        "sources": capture_rows, "events": events}
    capture_bytes = _canonical(capture_doc)
    artifact_doc = {"schema": "graphreader.marker-artifact-v5-train-visibility-audit.v1", "split": "train",
        "source_count": 64, "source_accounting_complete": True, "all_sources_succeeded": True,
        "training_approved": False, "sources": artifact_rows}
    artifact_bytes = _canonical(artifact_doc)
    assoc_bytes = _canonical({"fixture": "association"})
    source.update({"capture_complete_sha256": digest(complete_bytes),
        "capture_report_sha256": digest(capture_bytes), "capture_binding_sha256": FULL_CAPTURE_BINDING_SHA256,
        "builder_binding_sha256": CORPUS_BUILDER_BINDING_SHA256,
        "identity_metadata_sha256": digest(identity_bytes), "association_report_sha256": digest(assoc_bytes),
        "artifact_report_sha256": digest(artifact_bytes)})
    return {"source": source, "panel": panel_bytes, "image": image,
        "complete": complete_bytes, "identity": identity_bytes, "capture": capture_bytes,
        "truth": truth_bytes, "artifact": artifact_bytes, "assoc": assoc_bytes,
        "expected_constants": {"FULL_CAPTURE_COMPLETE_SHA256": digest(complete_bytes),
            "IDENTITY_METADATA_SHA256": digest(identity_bytes), "FULL_CAPTURE_REPORT_SHA256": digest(capture_bytes),
            "ARTIFACT_REPORT_SHA256": digest(artifact_bytes), "ASSOCIATION_REPORT_SHA256": digest(assoc_bytes)},
        "unmatched_glyph": unmatched_glyph}


class CandidateTensorBridgeTests(unittest.TestCase):
    def test_named_full_capture_report_and_identity_metadata_join(self):
        project_root = Path(__file__).resolve().parents[2]
        capture_path = (project_root / "artifacts" / "goal22-runs" /
                        "marker-context-bound-input-full-capture-v4" / "report.json")
        identity_path = (project_root / "artifacts" / "goal22-runs" /
                         "marker-context-train-corpus-v5-full" / "identity-metadata.json")
        if not capture_path.is_file() or not identity_path.is_file():
            self.skipTest("optional local synthetic capture identity artifacts are absent")
        capture_bytes, identity_bytes = capture_path.read_bytes(), identity_path.read_bytes()
        self.assertEqual(FULL_CAPTURE_REPORT_SHA256, digest(capture_bytes))
        self.assertEqual(IDENTITY_METADATA_SHA256, digest(identity_bytes))
        capture = json.loads(capture_bytes)
        identity = json.loads(identity_bytes)
        self.assertEqual("graphreader.marker-context-bound-input-full-capture-report.v1", capture["schema"])
        self.assertEqual(64, capture["source_count"])
        self.assertEqual(64, len(capture["sources"]))
        self.assertEqual(64, len(capture["events"]))
        self.assertEqual(64, len(identity["sources"]))
        identity_by_id = {row["source_id"]: row for row in identity["sources"]}
        capture_by_id = {row["source_id"]: row for row in capture["sources"]}
        self.assertEqual(64, len(identity_by_id))
        self.assertEqual(set(identity_by_id), set(capture_by_id))
        self.assertEqual(64, len({(row["case_id"], row["image_sha256"])
                                  for row in identity["sources"]}))
        for source_id, identity_row in identity_by_id.items():
            capture_row = capture_by_id[source_id]
            for key in ("case_id", "scene_id", "scene_sha256", "seed", "image_sha256"):
                self.assertEqual(identity_row[key], capture_row[key], (source_id, key))
        event_pairs = [(row["source_id"], row["panel_id"]) for row in capture["events"]]
        self.assertEqual(64, len(set(event_pairs)))
        declared_pairs = {(source_row["source_id"], panel["panel_id"])
                          for source_row in capture["sources"] for panel in source_row["panels"]}
        self.assertEqual(set(event_pairs), declared_pairs)
        for event in capture["events"]:
            source_row = capture_by_id[event["source_id"]]
            panel_rows = {row["panel_id"]: row for row in source_row["panels"]}
            self.assertIn(event["panel_id"], panel_rows)
            self.assertEqual(64, len(event["sha256"]))
            self.assertEqual("completed", panel_rows[event["panel_id"]]["status"])

    def test_protocol_pins_source_closure_and_disables_training(self):
        protocol = json.loads(Path(__file__).with_name("protocol.json").read_text(encoding="utf-8"))
        self.assertEqual("graphreader.marker-candidate-tensor-bridge.v2", protocol["schema"])
        self.assertEqual(SOURCE_BINDINGS, protocol["source_bindings"])
        self.assertEqual(digest(_canonical(SOURCE_BINDINGS)), protocol["source_closure_sha256"])
        self.assertEqual(0, protocol["labels"]["trainable_rows"])
        self.assertEqual(0, protocol["labels"]["negative_training_rows"])
        self.assertFalse(protocol["labels"]["training_approved"])
        self.assertEqual(FULL_CAPTURE_REPORT_SHA256,
                         protocol["expected_inputs"]["full_capture_report_sha256"])
        self.assertEqual(FULL_CAPTURE_COMPLETE_SHA256,
                         protocol["expected_inputs"]["full_capture_complete_sha256"])
        self.assertEqual(FULL_CAPTURE_BINDING_SHA256,
                         protocol["expected_inputs"]["full_capture_binding_sha256"])
        self.assertIn("truth_inventory", protocol)

    def test_source_pins_match_current_sampler_and_join_sources(self):
        project_root = Path(__file__).resolve().parents[2]
        self.assertEqual(SOURCE_BINDINGS, verify_source_bindings(project_root))

    def test_unique_positive_visible_glyph_and_unmatched_are_review_only(self):
        args = fixture()
        result = _materialize_source_core(source=args[0], panel_result_bytes=args[1],
                                    source_raster_bytes=args[2], panel_raster_bytes=args[2],
                                    candidate_evidence=args[3], positive_truths=args[4],
                                    visible_glyphs=args[5], generic_overlays=args[6])
        rows = {row["candidate_id"]: row for row in result["rows"]}
        self.assertEqual("review_positive_connected", rows["candidate-positive"]["status"])
        self.assertEqual("marker_positive_review_only", rows["candidate-positive"]["review_label"])
        self.assertEqual("review_visible_legend_glyph", rows["candidate-artifact"]["status"])
        self.assertEqual("artifact_candidate_review_only", rows["candidate-artifact"]["review_label"])
        self.assertEqual("unknown_unmatched", rows["candidate-unmatched"]["status"])
        self.assertIsNone(rows["candidate-unmatched"]["review_label"])
        self.assertNotEqual(rows["candidate-positive"]["local_tensor_sha256"],
                            rows["candidate-positive"]["wide_tensor_sha256"])
        self.assertEqual(0, result["trainable_rows"])
        self.assertEqual(0, result["negative_training_rows"])
        self.assertFalse(result["training_approved"])
        self.assertFalse(any(row["training_admitted"] for row in rows.values()))

    def test_actual_full_capture_inline_panel_schema_is_consumed(self):
        source, panel, image, truths, glyphs, overlays = full_capture_fixture()
        evidence_map = _full_capture_candidate_evidence(json.loads(panel), panel, source)
        result = _materialize_source_core(source=source, panel_result_bytes=panel,
            source_raster_bytes=image, panel_raster_bytes=image, candidate_evidence=evidence_map,
            positive_truths=truths, visible_glyphs=glyphs, generic_overlays=overlays)
        self.assertEqual(source["candidate_count"], result["candidate_count"])
        self.assertEqual(0, result["trainable_rows"])
        by_id = {row["candidate_id"]: row for row in result["rows"]}
        self.assertEqual("marker_positive_review_only", by_id["candidate-positive"]["review_label"])
        self.assertEqual("artifact_candidate_review_only", by_id["candidate-artifact"]["review_label"])

    def test_actual_full_capture_rejects_tensor_hash_and_candidate_order_mismatch(self):
        source, panel, image, truths, glyphs, overlays = full_capture_fixture()
        value = json.loads(panel)
        value["sampled_local_and_experimental_wide_views"][0]["local"][0] = 0.9
        changed = json.dumps(value, separators=(",", ":"), allow_nan=False).encode()
        source["panel_result_sha256"] = digest(changed)
        with self.assertRaisesRegex(ValueError, "tensor bytes differ"):
            evidence_map = _full_capture_candidate_evidence(json.loads(changed), changed, source)
            _materialize_source_core(source=source, panel_result_bytes=changed,
                source_raster_bytes=image, panel_raster_bytes=image, candidate_evidence=evidence_map,
                positive_truths=truths, visible_glyphs=glyphs, generic_overlays=overlays)

    def _materialize_bound_fixture(self, bundle):
        with patch.multiple(bridge_module, **bundle["expected_constants"]):
            return materialize_bound_full_capture_panel(
                source=bundle["source"], panel_capture_bytes=bundle["panel"],
                source_raster_bytes=bundle["image"], panel_raster_bytes=bundle["image"],
                complete_receipt_bytes=bundle["complete"], identity_metadata_bytes=bundle["identity"],
                capture_report_bytes=bundle["capture"], truth_sidecar_bytes=bundle["truth"],
                artifact_report_bytes=bundle["artifact"],
                project_root=Path(__file__).resolve().parents[2])

    def test_bound_inventory_derives_exhaustive_labels_and_statuses(self):
        bundle = bound_inventory_fixture()
        self.assertNotIn("case_id", json.loads(bundle["artifact"])["sources"][0])
        result = self._materialize_bound_fixture(bundle)
        self.assertEqual("graphreader.marker-candidate-tensor-bridge.v2", result["schema"])
        self.assertEqual("source_id_only; participant/study independence is unverified", result["grouping_basis"])
        self.assertEqual(1, result["truth_inventory_binding"]["positive_truth_count"])
        self.assertEqual(1, result["truth_inventory_binding"]["visible_glyph_count"])
        self.assertEqual(1, result["truth_inventory_binding"]["overlay_count"])
        self.assertEqual(64, result["truth_inventory_binding"]["source_count"])
        self.assertEqual(64, len(result["truth_inventory_binding"]["full_source_inventory_sha256"]))
        self.assertEqual(64, len(result["source_closure_sha256"]))
        protocol = json.loads(Path(__file__).with_name("protocol.json").read_text(encoding="utf-8"))
        self.assertEqual(protocol["source_closure_sha256"], result["source_closure_sha256"])
        rows = {row["candidate_id"]: row for row in result["rows"]}
        self.assertEqual("marker_positive_review_only", rows["candidate-positive"]["review_label"])
        self.assertEqual("artifact_candidate_review_only", rows["candidate-artifact"]["review_label"])
        self.assertEqual(0, result["trainable_rows"])

    def test_batch_inventory_derivation_returns_all_sources_in_one_pass(self):
        bundle = bound_inventory_fixture()
        def fixture_classifier(annotation, *, expected_split):
            self.assertEqual("train", expected_split)
            markers = annotation.get("panels", [{}])[0].get("markers", []) if annotation.get("panels") else []
            return {"records": [{"id": marker["point_id"], "center": marker["center"],
                                  "bin": "connected_positive"} for marker in markers]}
        with patch.multiple(bridge_module, **bundle["expected_constants"]):
            derived, rows = bridge_module._derive_bound_inventories(
                source=None, complete_receipt_bytes=bundle["complete"],
                identity_metadata_bytes=bundle["identity"], capture_report_bytes=bundle["capture"],
                truth_sidecar_bytes=bundle["truth"], artifact_report_bytes=bundle["artifact"],
                classify_annotations=fixture_classifier)
        self.assertEqual(64, len(derived))
        self.assertEqual(64, len(rows))
        self.assertEqual(sorted(derived), [row["source_id"] for row in rows])
        self.assertEqual(1, len(derived[bundle["source"]["source_id"]]["positive_truths"]))

    def test_bound_inventory_rejects_omitted_truth_or_artifact_record(self):
        bundle = bound_inventory_fixture()
        truth_doc = json.loads(bundle["truth"])
        truth_doc["sources"][0]["annotation"]["panels"][0]["markers"] = []
        forged_truth = _canonical(truth_doc)
        receipt = json.loads(bundle["complete"])
        receipt["truth_sha256"] = digest(forged_truth)
        forged_receipt = _canonical(receipt)
        with patch.multiple(bridge_module, **bundle["expected_constants"]):
            with self.assertRaisesRegex(ValueError, "corpus COMPLETE receipt bytes"):
                materialize_bound_full_capture_panel(
                    source=bundle["source"], panel_capture_bytes=bundle["panel"],
                    source_raster_bytes=bundle["image"], panel_raster_bytes=bundle["image"],
                    complete_receipt_bytes=forged_receipt, identity_metadata_bytes=bundle["identity"],
                    capture_report_bytes=bundle["capture"], truth_sidecar_bytes=forged_truth,
                    artifact_report_bytes=bundle["artifact"], project_root=Path(__file__).resolve().parents[2])
        artifact_doc = json.loads(bundle["artifact"])
        artifact_doc["sources"][0]["glyphs"] = []
        forged_artifact = _canonical(artifact_doc)
        with patch.multiple(bridge_module, **bundle["expected_constants"]):
            with self.assertRaisesRegex(ValueError, "artifact visibility report bytes"):
                materialize_bound_full_capture_panel(
                    source=bundle["source"], panel_capture_bytes=bundle["panel"],
                    source_raster_bytes=bundle["image"], panel_raster_bytes=bundle["image"],
                    complete_receipt_bytes=bundle["complete"], identity_metadata_bytes=bundle["identity"],
                    capture_report_bytes=bundle["capture"], truth_sidecar_bytes=bundle["truth"],
                    artifact_report_bytes=forged_artifact, project_root=Path(__file__).resolve().parents[2])

    def test_bound_positive_near_unmatched_valid_glyph_is_ambiguous(self):
        bundle = bound_inventory_fixture(unmatched_glyph=True)
        result = self._materialize_bound_fixture(bundle)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-positive")
        self.assertEqual("unknown_ambiguous_collision", row["status"])
        self.assertIsNone(row["review_label"])

    def test_bound_capture_panel_uses_event_sha_not_nonexistent_panel_hash_field(self):
        bundle = bound_inventory_fixture()
        capture = json.loads(bundle["capture"])
        self.assertNotIn("panel_result_sha256", capture["sources"][0]["panels"][0])
        self.assertEqual(bundle["source"]["panel_result_sha256"], capture["events"][0]["sha256"])
        result = self._materialize_bound_fixture(bundle)
        self.assertEqual(bundle["source"]["panel_result_sha256"], result["panel_result_sha256"])

    def test_bound_source_group_must_equal_authenticated_source_id(self):
        bundle = bound_inventory_fixture()
        bundle["source"]["source_group_id"] = "caller-invented-participant-group"
        with patch.multiple(bridge_module, **bundle["expected_constants"]):
            with self.assertRaisesRegex(ValueError, "source_group_id must be derived"):
                materialize_bound_full_capture_panel(
                    source=bundle["source"], panel_capture_bytes=bundle["panel"],
                    source_raster_bytes=bundle["image"], panel_raster_bytes=bundle["image"],
                    complete_receipt_bytes=bundle["complete"], identity_metadata_bytes=bundle["identity"],
                    capture_report_bytes=bundle["capture"], truth_sidecar_bytes=bundle["truth"],
                    artifact_report_bytes=bundle["artifact"], project_root=Path(__file__).resolve().parents[2])

    def test_bound_api_has_no_caller_inventory_arguments(self):
        names = set(inspect.signature(materialize_bound_full_capture_panel).parameters)
        self.assertFalse({"positive_truths", "visible_glyphs", "generic_overlays"} & names)

    def test_public_entrypoint_rejects_source_mutation_before_label_classification(self):
        bundle = bound_inventory_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "bound-source.py").write_bytes(b"mutated source bytes")
            with patch.multiple(bridge_module, CANONICAL_PROJECT_ROOT=root,
                                SOURCE_BINDINGS={"bound-source.py": "0" * 64}):
                with patch.object(bridge_module, "_load_bound_classifier",
                                  side_effect=AssertionError("label source must not load")) as load_classifier:
                    with self.assertRaisesRegex(ValueError, "source SHA-256 differs"):
                        materialize_bound_full_capture_panel(
                            source=bundle["source"], panel_capture_bytes=bundle["panel"],
                            source_raster_bytes=bundle["image"], panel_raster_bytes=bundle["image"],
                            complete_receipt_bytes=bundle["complete"], identity_metadata_bytes=bundle["identity"],
                            capture_report_bytes=bundle["capture"], truth_sidecar_bytes=bundle["truth"],
                            artifact_report_bytes=bundle["artifact"], project_root=root)
                    load_classifier.assert_not_called()

    def test_public_entrypoint_refuses_noncanonical_project_root_before_reads(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(ValueError, "canonical E project root"):
                verify_source_bindings(root)

    def test_runner_writes_fresh_review_only_report_and_receipt_from_synthetic_fixture(self):
        bundle = bound_inventory_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run_root = root / "artifacts" / "goal22-runs"
            run_root.mkdir(parents=True)
            input_dir = root / "synthetic-inputs"
            input_dir.mkdir()
            input_map = {
                "panel_capture": bundle["panel"], "source_raster": bundle["image"],
                "panel_raster": bundle["image"], "complete_receipt": bundle["complete"],
                "identity_metadata": bundle["identity"], "capture_report": bundle["capture"],
                "truth_sidecar": bundle["truth"], "artifact_report": bundle["artifact"],
            }
            input_paths = {}
            for key, raw in input_map.items():
                path = input_dir / f"{key}.bin"
                path.write_bytes(raw)
                input_paths[key] = path.relative_to(root).as_posix()
            descriptor = _canonical({"schema": "graphreader.marker-candidate-tensor-bridge-run.v1",
                "source": bundle["source"], "inputs": input_paths})
            label_relative = "ml/synthetic/marker_context_catalog_v1/labels.py"
            label_bytes = (Path(__file__).resolve().parents[2] / label_relative).read_bytes()
            label_path = root.joinpath(*label_relative.split("/"))
            label_path.parent.mkdir(parents=True)
            label_path.write_bytes(label_bytes)
            source_pins = {label_relative: digest(label_bytes)}
            closure_sha = digest(_canonical(source_pins))
            tool_dir = root / "tools" / "MarkerCandidateTensorBridgeV1"
            tool_dir.mkdir(parents=True)
            runner_path = tool_dir / "runner.py"
            bridge_path = tool_dir / "bridge.py"
            package_init = tool_dir / "__init__.py"
            runner_path.write_bytes(Path(runner_module.__file__).read_bytes())
            bridge_path.write_bytes(Path(bridge_module.__file__).read_bytes())
            package_init.write_bytes(Path(bridge_module.__file__).with_name("__init__.py").read_bytes())
            source_bindings = {"runner.py": digest(runner_path.read_bytes()),
                "bridge.py": digest(bridge_path.read_bytes()),
                "__init__.py": digest(package_init.read_bytes())}
            protocol = {"schema": "graphreader.marker-candidate-tensor-bridge-runner.v1",
                "source_bindings": source_bindings, "source_closure_sha256": closure_sha,
                "expected_inputs": {
                    "full_capture_report_sha256": bundle["expected_constants"]["FULL_CAPTURE_REPORT_SHA256"],
                    "identity_metadata_sha256": bundle["expected_constants"]["IDENTITY_METADATA_SHA256"],
                    "full_capture_complete_sha256": bundle["expected_constants"]["FULL_CAPTURE_COMPLETE_SHA256"],
                    "full_capture_binding_sha256": FULL_CAPTURE_BINDING_SHA256,
                    "corpus_builder_binding_sha256": CORPUS_BUILDER_BINDING_SHA256,
                    "association_report_sha256": bundle["expected_constants"]["ASSOCIATION_REPORT_SHA256"],
                    "artifact_report_sha256": bundle["expected_constants"]["ARTIFACT_REPORT_SHA256"]}}
            protocol_bytes = _canonical(protocol)
            protocol_path = root / "runner_protocol.json"
            protocol_path.write_bytes(protocol_bytes)
            with patch.multiple(bridge_module, CANONICAL_PROJECT_ROOT=root,
                                SOURCE_BINDINGS=source_pins, **bundle["expected_constants"]):
                with patch.multiple(runner_module, RUNNER_PROTOCOL=protocol_path, __file__=runner_path):
                    with patch.object(bridge_module, "__file__", bridge_path):
                        receipt = runner_module.run_one_source(descriptor_bytes=descriptor,
                            project_root=root, output_relative="artifacts/goal22-runs/fixture-run",
                            expected_runner_protocol_sha256=digest(protocol_bytes))
                    self.assertEqual("completed_review_only", receipt["status"])
                    self.assertEqual(0, receipt["trainable_rows"])
                    self.assertEqual(0, receipt["negative_training_rows"])
                    output = run_root / "fixture-run"
                    saved_receipt = json.loads((output / "receipt.json").read_bytes())
                    saved_report = json.loads((output / "review-rows.json").read_bytes())
                    self.assertEqual(receipt, saved_receipt)
                    self.assertFalse(saved_report["training_approved"])
                    self.assertEqual(0, saved_report["trainable_rows"])
                    self.assertNotIn("tensor_bytes_by_candidate", saved_report)
                    self.assertEqual(2 * receipt["candidate_count"], receipt["tensor_file_count"])
                    for refs in saved_report["tensor_files"].values():
                        for reference in refs.values():
                            raw = (output / reference["path"]).read_bytes()
                            self.assertEqual(reference["bytes"], len(raw))
                            self.assertEqual(reference["sha256"], digest(raw))
                    with self.assertRaises(FileExistsError):
                        with patch.object(bridge_module, "__file__", bridge_path):
                            runner_module.run_one_source(descriptor_bytes=descriptor,
                                project_root=root, output_relative="artifacts/goal22-runs/fixture-run",
                                expected_runner_protocol_sha256=digest(protocol_bytes))

    def test_full_train_batch_derives_inventory_once_and_accounts_source_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "artifacts" / "goal22-runs").mkdir(parents=True)
            input_dir = root / "fixture-inputs"
            input_dir.mkdir()
            input_payloads = {
                "complete_receipt": b"complete", "identity_metadata": b"identity",
                "capture_report": b"capture", "truth_sidecar": b"truth",
                "artifact_report": b"artifact",
            }
            input_paths = {}
            for key, raw in input_payloads.items():
                path = input_dir / f"{key}.json"
                path.write_bytes(raw)
                input_paths[key] = path.relative_to(root).as_posix()
            image = tiny_png(2, 2)
            image_sha = digest(image)
            raster_root = root / "raster-only"
            raster_root.mkdir()
            (raster_root / f"{image_sha}.png").write_bytes(image)
            panel_root = root / "panels"
            panel_root.mkdir()
            source_ids = ["source-a", "source-b"]
            identity_rows, capture_rows, events = [], [], []
            panel_bytes_by_source = {}
            for index, source_id in enumerate(source_ids):
                panel_id = f"panel-{index}"
                panel = {"source_id": source_id, "panel_id": panel_id,
                    "panel_sha256": image_sha,
                    "provenance": {"source_sha256": image_sha, "panel_image_sha256": image_sha,
                        "source_width": 2, "source_height": 2},
                    "frame": {"width": 2, "height": 2, "channel_count": 1,
                        "channels_first_pixels": [0.0, 0.1, 0.2, 0.3]}}
                panel_bytes = _canonical(panel)
                filename = f"panel-{index:04d}.json"
                (panel_root / filename).write_bytes(panel_bytes)
                panel_bytes_by_source[source_id] = panel_bytes
                identity_rows.append({"source_id": source_id, "scene_id": f"scene-{index}",
                    "scene_sha256": "a" * 64, "image_sha256": image_sha, "case_id": f"case-{index}"})
                capture_rows.append({"source_index": index, "source_id": source_id,
                    "status": "completed", "failure": None, "panel_count": 1,
                    "panels": [{"panel_id": panel_id, "status": "completed", "marker_count": 2}]})
                events.append({"source_id": source_id, "panel_id": panel_id,
                    "file": filename, "sha256": digest(panel_bytes)})
            identity_bytes = _canonical({"sources": identity_rows})
            capture_bytes = _canonical({"sources": capture_rows, "events": events})
            input_payloads["identity_metadata"] = identity_bytes
            input_payloads["capture_report"] = capture_bytes
            for key, raw in input_payloads.items():
                (input_dir / f"{key}.json").write_bytes(raw)

            derived = {source_id: {"annotation_sha256": "b" * 64,
                "positive_truths": [], "visible_glyphs": [], "generic_overlays": []}
                for source_id in source_ids}
            inventory_rows = [{"source_id": source_id} for source_id in source_ids]
            label_source = "ml/synthetic/marker_context_catalog_v1/labels.py"
            label_bytes = b"fixture-label-source"
            label_path = root.joinpath(*label_source.split("/"))
            label_path.parent.mkdir(parents=True)
            label_path.write_bytes(label_bytes)
            source_pins = {label_source: digest(label_bytes)}
            closure_sha = digest(_canonical(source_pins))
            tool_dir = root / "tools" / "MarkerCandidateTensorBridgeV1"
            tool_dir.mkdir(parents=True)
            runner_path, bridge_path = tool_dir / "runner.py", tool_dir / "bridge.py"
            package_init = tool_dir / "__init__.py"
            runner_path.write_bytes(Path(runner_module.__file__).read_bytes())
            bridge_path.write_bytes(Path(bridge_module.__file__).read_bytes())
            package_init.write_bytes(Path(bridge_module.__file__).with_name("__init__.py").read_bytes())
            source_bindings = {"runner.py": digest(runner_path.read_bytes()),
                "bridge.py": digest(bridge_path.read_bytes()),
                "__init__.py": digest(package_init.read_bytes())}
            expected_constants = {
                "FULL_CAPTURE_COMPLETE_SHA256": digest(input_payloads["complete_receipt"]),
                "IDENTITY_METADATA_SHA256": digest(identity_bytes),
                "FULL_CAPTURE_REPORT_SHA256": digest(capture_bytes),
                "ARTIFACT_REPORT_SHA256": digest(input_payloads["artifact_report"]),
                "FULL_CAPTURE_BINDING_SHA256": "c" * 64,
                "CORPUS_BUILDER_BINDING_SHA256": "d" * 64,
                "ASSOCIATION_REPORT_SHA256": "e" * 64,
            }
            protocol = {"schema": "graphreader.marker-candidate-tensor-bridge-runner.v1",
                "source_bindings": source_bindings, "source_closure_sha256": closure_sha,
                "batch_execution": {"source_count": 2, "validation_passes": 1,
                    "per_source_tensor_retention": "one_source_at_a_time"},
                "expected_inputs": {
                    "full_capture_report_sha256": expected_constants["FULL_CAPTURE_REPORT_SHA256"],
                    "identity_metadata_sha256": expected_constants["IDENTITY_METADATA_SHA256"],
                    "full_capture_complete_sha256": expected_constants["FULL_CAPTURE_COMPLETE_SHA256"],
                    "full_capture_binding_sha256": expected_constants["FULL_CAPTURE_BINDING_SHA256"],
                    "corpus_builder_binding_sha256": expected_constants["CORPUS_BUILDER_BINDING_SHA256"],
                    "artifact_report_sha256": expected_constants["ARTIFACT_REPORT_SHA256"],
                    "association_report_sha256": expected_constants["ASSOCIATION_REPORT_SHA256"]}}
            protocol_bytes = _canonical(protocol)
            protocol_path = root / "runner_protocol.json"
            protocol_path.write_bytes(protocol_bytes)
            derive = lambda **_kwargs: (derived, inventory_rows)
            calls = []

            def fake_core(**kwargs):
                source_id = kwargs["source"]["source_id"]
                calls.append(source_id)
                if source_id == "source-b":
                    raise ValueError("synthetic per-source failure")
                local, wide = b"local-tensor", b"wide-tensor"
                candidate_id = f"{source_id}:candidate-0"
                return {"status": "completed_review_only", "source_id": source_id,
                    "source_group_id": source_id, "split": "train",
                    "candidate_count": 1,
                    "rows": [{"candidate_id": candidate_id, "training_admitted": False}],
                    "tensor_bytes_by_candidate": {candidate_id: {
                        "local_bytes": local, "local_sha256": digest(local),
                        "wide_bytes": wide, "wide_sha256": digest(wide)}},
                    "matched_positive_review_rows": 1,
                    "visible_legend_review_rows": 0, "unknown_or_excluded_rows": 0,
                    "trainable_rows": 0, "negative_training_rows": 0,
                    "training_approved": False, "optimizer_steps": 0,
                    "limitations": []}

            batch_paths = {key: input_paths[key] for key in input_paths}
            with patch.multiple(bridge_module, CANONICAL_PROJECT_ROOT=root,
                                SOURCE_BINDINGS=source_pins, **expected_constants):
                with patch.multiple(runner_module, RUNNER_PROTOCOL=protocol_path,
                    BATCH_SOURCE_COUNT=2, BATCH_INPUT_PATHS=batch_paths,
                    BATCH_CAPTURE_PANEL_ROOT="panels", BATCH_RASTER_ROOT="raster-only",
                    __file__=runner_path):
                    with patch.object(bridge_module, "__file__", bridge_path):
                        with patch.object(bridge_module, "verify_source_bindings",
                                          return_value=source_pins):
                            with patch.object(bridge_module, "_load_bound_classifier", return_value=object()):
                                with patch.object(bridge_module, "_derive_bound_inventories", side_effect=derive) as derive_mock:
                                    with patch.object(bridge_module, "_full_capture_candidate_evidence",
                                                      return_value={}):
                                        with patch.object(bridge_module, "_materialize_source_core",
                                                          side_effect=fake_core):
                                            receipt = runner_module.run_train_batch(
                                                descriptor_bytes=_canonical({"schema": "graphreader.marker-candidate-tensor-bridge-batch.v1",
                                                    "source_ids": source_ids}), project_root=root,
                                                output_relative="artifacts/goal22-runs/batch-fixture",
                                                expected_runner_protocol_sha256=digest(protocol_bytes))
                                    derive_mock.assert_called_once()
            self.assertEqual(["source-a", "source-b"], calls)
            self.assertEqual("completed_with_source_failures_review_only", receipt["status"])
            self.assertEqual({"completed": 1, "failed": 1}, receipt["source_status_counts"])
            self.assertEqual(0, receipt["trainable_rows"])
            self.assertEqual(0, receipt["negative_training_rows"])
            output = root / "artifacts" / "goal22-runs" / "batch-fixture"
            report = json.loads((output / "batch-report.json").read_bytes())
            self.assertEqual(2, report["source_count"])
            self.assertEqual(["completed_review_only", "failed"],
                             [row["status"] for row in report["sources"]])
            self.assertEqual("ValueError", report["sources"][1]["failure_code"])
            self.assertEqual(1, report["candidate_count"])
            self.assertEqual(4, report["captured_candidate_count"])
            self.assertEqual(2, report["tensor_file_count"])
            good_dir = output / report["sources"][0]["source_output"]
            self.assertEqual(2, len(list((good_dir / "tensors").glob("*.f32le"))))
            self.assertFalse((output / "sources" / "source-001").exists())
            saved_source_report = json.loads((good_dir / "review-rows.json").read_bytes())
            self.assertEqual(["source-a:candidate-0"],
                             [row["candidate_id"] for row in saved_source_report["rows"]])
            for references in saved_source_report["tensor_files"].values():
                for reference in references.values():
                    payload = (good_dir / reference["path"]).read_bytes()
                    self.assertEqual(reference["bytes"], len(payload))
                    self.assertEqual(reference["sha256"], digest(payload))
            self.assertFalse(report["private_input_supplied"])
            self.assertFalse(report["sealed_input_supplied"])
            self.assertFalse(report["dev_input_supplied"])
            self.assertTrue(any("classifier normalization is not applied or verified" in item
                                for item in report["limitations"]))
            self.assertTrue(any("production model-store factory/runtime parity are not verified" in item
                                for item in receipt["limitations"]))
            with self.assertRaises(FileExistsError):
                with patch.multiple(bridge_module, CANONICAL_PROJECT_ROOT=root,
                                    SOURCE_BINDINGS=source_pins, **expected_constants):
                    with patch.multiple(runner_module, RUNNER_PROTOCOL=protocol_path,
                        BATCH_SOURCE_COUNT=2, BATCH_INPUT_PATHS=batch_paths,
                        BATCH_CAPTURE_PANEL_ROOT="panels", BATCH_RASTER_ROOT="raster-only",
                        __file__=runner_path):
                        with patch.object(bridge_module, "__file__", bridge_path):
                            with patch.object(bridge_module, "verify_source_bindings", return_value=source_pins):
                                with patch.object(bridge_module, "_load_bound_classifier", return_value=object()):
                                    with patch.object(bridge_module, "_derive_bound_inventories", return_value=(derived, inventory_rows)):
                                        runner_module.run_train_batch(
                                            descriptor_bytes=_canonical({"schema": "graphreader.marker-candidate-tensor-bridge-batch.v1",
                                                "source_ids": source_ids}), project_root=root,
                                            output_relative="artifacts/goal22-runs/batch-fixture",
                                            expected_runner_protocol_sha256=digest(protocol_bytes))

    def test_batch_descriptor_rejects_duplicate_or_unsorted_inventory(self):
        for source_ids in (["source-a"] * 2, ["source-b", "source-a"]):
            descriptor = {"schema": "graphreader.marker-candidate-tensor-bridge-batch.v1",
                          "source_ids": source_ids}
            with self.assertRaisesRegex(ValueError, "batch descriptor source inventory differs"):
                runner_module._parse_batch_descriptor(_canonical(descriptor), 2)

    def test_checked_in_runner_protocol_pins_its_executable_bundle_and_inputs(self):
        protocol_bytes = runner_module.RUNNER_PROTOCOL.read_bytes()
        protocol = json.loads(protocol_bytes)
        self.assertEqual("graphreader.marker-candidate-tensor-bridge-runner.v1", protocol["schema"])
        self.assertEqual(digest(Path(runner_module.__file__).read_bytes()),
                         protocol["source_bindings"]["runner.py"])
        self.assertEqual(digest(Path(bridge_module.__file__).read_bytes()),
                         protocol["source_bindings"]["bridge.py"])
        self.assertEqual(digest(Path(bridge_module.__file__).with_name("__init__.py").read_bytes()),
                         protocol["source_bindings"]["__init__.py"])
        self.assertEqual(digest(_canonical(SOURCE_BINDINGS)), protocol["source_closure_sha256"])
        self.assertEqual(FULL_CAPTURE_REPORT_SHA256,
                         protocol["expected_inputs"]["full_capture_report_sha256"])
        self.assertEqual(ARTIFACT_REPORT_SHA256,
                         protocol["expected_inputs"]["artifact_report_sha256"])

    def test_prepared_real_source_eight_path_preflight_does_not_open_input_contents(self):
        project_root = Path(__file__).resolve().parents[2]
        descriptor_path = (project_root / "artifacts" / "goal22-runs" /
            "marker-candidate-tensor-bridge-one-source-prep-v1" / "source-descriptor.json")
        if not descriptor_path.is_file():
            self.skipTest("optional prepared synthetic source descriptor is absent")
        descriptor_bytes = descriptor_path.read_bytes()
        result = runner_module.preflight_descriptor_paths(descriptor_bytes=descriptor_bytes,
                                                           project_root=project_root)
        self.assertEqual("paths_valid_contents_unopened", result["status"])
        self.assertEqual(8, result["input_count"])
        self.assertFalse(result["annotation_content_read"])
        self.assertFalse(result["bridge_executed"])
        self.assertEqual(20820000, result["seed"])
        self.assertTrue(all(row["regular_file"] and row["bytes"] > 0
                            for row in result["inputs"].values()))

    def test_runner_rejects_changed_source_pin_before_output_creation(self):
        bundle = bound_inventory_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "artifacts" / "goal22-runs").mkdir(parents=True)
            protocol_bytes = _canonical({"schema": "graphreader.marker-candidate-tensor-bridge-runner.v1",
                "source_bindings": {"runner.py": "0" * 64}, "source_closure_sha256": "0" * 64,
                "expected_inputs": {}})
            protocol_path = root / "runner_protocol.json"
            protocol_path.write_bytes(protocol_bytes)
            with patch.object(runner_module, "RUNNER_PROTOCOL", protocol_path):
                with patch.multiple(bridge_module, CANONICAL_PROJECT_ROOT=root):
                    with self.assertRaisesRegex(ValueError, "runner protocol source or evidence pins differ"):
                        runner_module.run_one_source(descriptor_bytes=b"{}", project_root=root,
                            output_relative="artifacts/goal22-runs/no-output",
                            expected_runner_protocol_sha256=digest(protocol_bytes))
            self.assertFalse((root / "artifacts" / "goal22-runs" / "no-output").exists())
        source, panel, image, truths, glyphs, overlays = full_capture_fixture()
        value = json.loads(panel)
        value["sampled_local_and_experimental_wide_views"][0], value["sampled_local_and_experimental_wide_views"][1] = \
            value["sampled_local_and_experimental_wide_views"][1], value["sampled_local_and_experimental_wide_views"][0]
        changed = json.dumps(value, separators=(",", ":"), allow_nan=False).encode()
        source["panel_result_sha256"] = digest(changed)
        with self.assertRaisesRegex(ValueError, "not bound to its classifier input"):
            evidence_map = _full_capture_candidate_evidence(json.loads(changed), changed, source)
            _materialize_source_core(source=source, panel_result_bytes=changed,
                source_raster_bytes=image, panel_raster_bytes=image, candidate_evidence=evidence_map,
                positive_truths=truths, visible_glyphs=glyphs, generic_overlays=overlays)

    def test_joint_marker_glyph_collision_stays_unlabeled(self):
        args = fixture()
        glyph = args[5][0]
        glyph["center"] = [2.0, 2.0]
        glyph["glyph_box"] = [1.0, 1.0, 2.0, 2.0]
        glyph["visibility_evidence"] = visibility_proof(args[0], glyph["glyph_id"], glyph["center"], glyph["glyph_box"])
        glyph["visibility_proof_sha256"] = glyph["visibility_evidence"]["proof_sha256"]
        result = _materialize_source_core(source=args[0], panel_result_bytes=args[1],
                                    source_raster_bytes=args[2], panel_raster_bytes=args[2],
                                    candidate_evidence=args[3], positive_truths=args[4],
                                    visible_glyphs=args[5], generic_overlays=args[6])
        positive = next(row for row in result["rows"] if row["candidate_id"] == "candidate-positive")
        self.assertEqual("unknown_ambiguous_collision", positive["status"])
        self.assertIsNone(positive["review_label"])

    def test_positive_near_valid_unmatched_glyph_is_ambiguous(self):
        args = fixture()
        glyph = args[5][0]
        glyph["association_status"] = "unmatched_review_only"
        glyph["center"] = [2.0, 2.0]
        glyph["glyph_box"] = [1.0, 1.0, 2.0, 2.0]
        glyph["visibility_evidence"] = visibility_proof(args[0], glyph["glyph_id"], glyph["center"], glyph["glyph_box"])
        glyph["visibility_proof_sha256"] = glyph["visibility_evidence"]["proof_sha256"]
        result = _materialize_source_core(source=args[0], panel_result_bytes=args[1],
            source_raster_bytes=args[2], panel_raster_bytes=args[2], candidate_evidence=args[3],
            positive_truths=args[4], visible_glyphs=args[5], generic_overlays=args[6])
        positive = next(row for row in result["rows"] if row["candidate_id"] == "candidate-positive")
        self.assertEqual("unknown_ambiguous_collision", positive["status"])
        self.assertIsNone(positive["review_label"])

    def test_duplicate_center_overlay_and_unmatched_glyph_never_become_negative(self):
        source, panel_raw, image, sampled, truths, glyphs, overlays = fixture()
        panel = json.loads(panel_raw)
        panel["center_markers"].append({"marker_id": "candidate-duplicate",
                                        "center": {"x": 2.0, "y": 2.0}, "radius": 1.0})
        source["candidate_count"] = len(panel["center_markers"])
        duplicate = panel["center_markers"][-1]
        sampled[duplicate["marker_id"]] = {
            "local": evidence_ref(evidence(source, duplicate["marker_id"], [2.0, 2.0], 1.0, "local", 0.2)),
            "wide": evidence_ref(evidence(source, duplicate["marker_id"], [2.0, 2.0], 1.0, "wide", 0.7)),
        }
        panel_raw = json.dumps(panel, separators=(",", ":")).encode()
        source["panel_result_sha256"] = digest(panel_raw)
        glyphs[0]["association_status"] = "unmatched_review_only"
        overlays.append({"center": [7.0, 7.0]})
        result = _materialize_source_core(source=source, panel_result_bytes=panel_raw,
                                    source_raster_bytes=image, panel_raster_bytes=image,
                                    candidate_evidence=sampled, positive_truths=truths,
                                    visible_glyphs=glyphs, generic_overlays=overlays)
        statuses = {row["candidate_id"]: row for row in result["rows"]}
        self.assertEqual("unknown_duplicate_candidate_center", statuses["candidate-positive"]["status"])
        self.assertIsNone(statuses["candidate-artifact"]["review_label"])
        self.assertEqual("unknown_unmatched", statuses["candidate-unmatched"]["status"])
        self.assertIsNone(statuses["candidate-unmatched"]["review_label"])
        self.assertEqual("excluded_generic_overlay", statuses["candidate-overlay"]["status"])
        self.assertIsNone(statuses["candidate-overlay"]["review_label"])

    def test_symbol_inputs_are_excluded_not_negative(self):
        source, panel_raw, image, sampled, truths, glyphs, overlays = fixture()
        panel = json.loads(panel_raw)
        panel["symbol_input_ids"] = ["candidate-unmatched"]
        panel_raw = json.dumps(panel, separators=(",", ":")).encode()
        source["panel_result_sha256"] = digest(panel_raw)
        result = _materialize_source_core(source=source, panel_result_bytes=panel_raw,
                                    source_raster_bytes=image, panel_raster_bytes=image,
                                    candidate_evidence=sampled, positive_truths=truths,
                                    visible_glyphs=glyphs, generic_overlays=overlays)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-unmatched")
        self.assertEqual("excluded_symbol_input", row["status"])
        self.assertIsNone(row["review_label"])

    def test_invalid_source_split_raster_frame_and_evidence_fail_closed(self):
        args = fixture()
        args[0]["split"] = "dev"
        with self.assertRaisesRegex(ValueError, "train source"):
            _materialize_source_core(source=args[0], panel_result_bytes=args[1],
                               source_raster_bytes=args[2], panel_raster_bytes=args[2],
                               candidate_evidence=args[3], positive_truths=args[4],
                               visible_glyphs=args[5], generic_overlays=args[6])
        args = fixture()
        raw = json.loads(args[3]["candidate-positive"]["local"]["raw_bytes"])
        raw["input"]["channelsFirstPixels"][0] = 0.7
        changed = json.dumps(raw, separators=(",", ":")).encode()
        args[3]["candidate-positive"]["local"] = evidence_ref(changed)
        with self.assertRaisesRegex(ValueError, "source-plane hash"):
            _materialize_source_core(source=args[0], panel_result_bytes=args[1],
                               source_raster_bytes=args[2], panel_raster_bytes=args[2],
                               candidate_evidence=args[3], positive_truths=args[4],
                               visible_glyphs=args[5], generic_overlays=args[6])

    def test_source_group_split_and_duplicate_ids_rejected(self):
        rows = [{"source_id": "a", "source_group_id": "group", "split": "train"},
                {"source_id": "b", "source_group_id": "group", "split": "train"}]
        self.assertEqual(2, validate_train_source_groups(rows)["train_source_count"])
        with self.assertRaisesRegex(ValueError, "train sources only"):
            validate_train_source_groups(rows + [{"source_id": "c", "source_group_id": "group", "split": "dev"}])
        with self.assertRaisesRegex(ValueError, "duplicated"):
            validate_train_source_groups(rows + [rows[0]])


if __name__ == "__main__":
    unittest.main()
