# SPDX-License-Identifier: Apache-2.0
"""Shared-plane in-memory verifier for one captured marker panel.

This version keeps the V1 candidate matcher and row builder, but replaces its
per-view JSON envelope parsing and repeated full-plane hashing with one verified
frame/mask snapshot per source and compact in-memory evidence tokens.
"""

from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
from types import ModuleType
from typing import Any


PROTOCOL_PATH = Path(__file__).with_name("protocol.json")
V1_BRIDGE_PATH = Path(r"E:\Graph-auto-reader\tools\MarkerCandidateTensorBridgeV1\bridge.py")


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _strict_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("JSON contains a duplicate field")
        result[key] = value
    return result


class _SharedPlanes:
    __slots__ = ("source_id", "panel_id", "panel_result_sha256", "width", "height",
                 "frame_sha256", "ocr_mask_sha256", "artifact_mask_sha256")

    def __init__(self, *, source: dict[str, Any], frame_sha256: str,
                 ocr_mask_sha256: str, artifact_mask_sha256: str) -> None:
        self.source_id = source["source_id"]
        self.panel_id = source["panel_id"]
        self.panel_result_sha256 = source["panel_result_sha256"]
        self.width = source["width"]
        self.height = source["height"]
        self.frame_sha256 = frame_sha256
        self.ocr_mask_sha256 = ocr_mask_sha256
        self.artifact_mask_sha256 = artifact_mask_sha256


class _FastEvidenceToken(bytes):
    """Compact bytes token with immutable per-source verification context."""

    def __new__(cls, payload: bytes, *, planes: _SharedPlanes, candidate_id: str,
                center: tuple[float, float], radius: float, view: str,
                tensor: bytes, tensor_sha256: str, expected_geometry: tuple[float, ...]):
        instance = super().__new__(cls, payload)
        instance.planes = planes
        instance.candidate_id = candidate_id
        instance.center = center
        instance.radius = radius
        instance.view = view
        instance.tensor = tensor
        instance.tensor_sha256 = tensor_sha256
        instance.expected_geometry = expected_geometry
        return instance


def _finite_unit_plane(bridge: ModuleType, values: Any, expected_count: int, name: str) -> tuple[bytes, str]:
    raw = bridge.tensor_f32le(values, expected_count=expected_count)
    return raw, _sha(raw)


def _validate_local_bounds(bridge: ModuleType, value: Any, candidate: dict[str, Any],
                           width: int, height: int) -> None:
    if value is None:
        return
    keys = {"x", "y", "width", "height", "left", "top", "right", "bottom", "is_valid"}
    if not isinstance(value, dict) or set(value) != keys or value.get("is_valid") is not True:
        raise ValueError("full-capture local content bound has an unknown rectangle schema")
    numeric = ("x", "y", "width", "height", "left", "top", "right", "bottom")
    if any(isinstance(value[key], bool) or not isinstance(value[key], (int, float)) or
           not math.isfinite(value[key]) for key in numeric):
        raise ValueError("full-capture local content bound is non-finite")
    x, y, w, h = (float(value[key]) for key in ("x", "y", "width", "height"))
    if (w <= 0 or h <= 0 or value["left"] != x or value["top"] != y or
            value["right"] != x + w or value["bottom"] != y + h or
            x < 0 or y < 0 or x + w > width or y + h > height or
            not (x <= candidate["center"]["x"] < x + w and
                 y <= candidate["center"]["y"] < y + h)):
        raise ValueError("full-capture local content bound is inconsistent or excludes its marker")


def _fast_evidence_map(bridge: ModuleType, *, panel_bytes: bytes,
                       source: dict[str, Any]) -> tuple[dict[str, dict[str, dict[str, Any]]], dict[str, str]]:
    panel = json.loads(panel_bytes, object_pairs_hook=_strict_object)
    expected_panel_keys = {
        "source_id", "case_id", "scene_id", "scene_sha256", "seed", "panel_id",
        "panel_sha256", "provenance", "frame", "center_markers", "ocr_regions",
        "classifier_inputs", "symbol_input_ids", "symbol_crop_input_ids",
        "original_pixel_content_bounds", "sampled_local_and_experimental_wide_views",
    }
    if set(panel) != expected_panel_keys:
        raise ValueError("saved full-capture panel fields differ from the pinned schema")
    if (panel["source_id"] != source["source_id"] or panel["scene_id"] != source["scene_id"] or
            panel["scene_sha256"] != source["scene_sha256"] or panel["panel_id"] != source["panel_id"] or
            panel["panel_sha256"] != source["panel_image_sha256"] or
            _sha(panel_bytes) != source["panel_result_sha256"]):
        raise ValueError("full-capture panel identity/hash differs from source binding")
    provenance = panel["provenance"]
    if not isinstance(provenance, dict) or set(provenance) != {
            "source_sha256", "panel_image_sha256", "source_width", "source_height",
            "requested_crop_source_pixels", "encoded_crop_source_pixels",
            "source_to_panel_matrix", "panel_to_source_matrix", "identity"}:
        raise ValueError("full-capture panel provenance fields differ")
    identity_matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1]
    width, height = source["width"], source["height"]
    if (provenance["identity"] is not True or provenance["source_sha256"] != source["source_raster_sha256"] or
            provenance["panel_image_sha256"] != source["panel_image_sha256"] or
            provenance["source_width"] != width or provenance["source_height"] != height or
            provenance["source_to_panel_matrix"] != identity_matrix or
            provenance["panel_to_source_matrix"] != identity_matrix):
        raise ValueError("full-capture panel source transform is not the bound identity mapping")

    frame = panel["frame"]
    frame_keys = {"width", "height", "channel_count", "channels_first_pixels", "source_image",
                  "original_to_frame", "ocr_mask", "artifact_mask"}
    if not isinstance(frame, dict) or set(frame) != frame_keys:
        raise ValueError("full-capture frame fields differ from the pinned schema")
    frame_map = frame["original_to_frame"]
    identity_frame = {"m11": 1, "m12": 0, "m13": 0, "m21": 0, "m22": 1,
                      "m23": 0, "determinant": 1, "is_invertible": True}
    if (frame["width"] != width or frame["height"] != height or frame["channel_count"] != 1 or
            frame["source_image"] != 0 or not isinstance(frame_map, dict) or frame_map != identity_frame):
        raise ValueError("full-capture frame is not the bound original-pixel identity frame")
    _, frame_sha = _finite_unit_plane(bridge, frame["channels_first_pixels"], width * height,
                                      "source frame")
    if frame_sha != source["frame_f32le_sha256"]:
        raise ValueError("full-capture frame plane differs from source binding")

    mask_sha: dict[str, str] = {}
    for name, key in (("ocr_mask", "ocr_mask_sha256"), ("artifact_mask", "artifact_mask_sha256")):
        mask = frame[name]
        if (not isinstance(mask, dict) or set(mask) != {"width", "height", "values"} or
                mask["width"] != width or mask["height"] != height):
            raise ValueError(f"full-capture {name} dimensions differ")
        _, mask_sha[key] = _finite_unit_plane(bridge, mask["values"], width * height, name)
    planes = _SharedPlanes(source=source, frame_sha256=frame_sha,
        ocr_mask_sha256=mask_sha["ocr_mask_sha256"],
        artifact_mask_sha256=mask_sha["artifact_mask_sha256"])

    bounds = panel["original_pixel_content_bounds"]
    candidates, inputs, views = (panel["center_markers"], panel["classifier_inputs"],
                                 panel["sampled_local_and_experimental_wide_views"])
    if not isinstance(bounds, dict) or not all(isinstance(values, list) for values in (candidates, inputs, views)):
        raise ValueError("full-capture candidate/bounds inventories must be arrays or objects")
    if len(candidates) != len(inputs) or len(candidates) != len(views):
        raise ValueError("full-capture candidate/input/view denominators differ")
    if [row.get("marker_id") for row in candidates if isinstance(row, dict)] != [
            row.get("marker_id") for row in inputs if isinstance(row, dict)]:
        raise ValueError("full-capture classifier-input order differs from detector candidates")
    if set(bounds) - {row.get("marker_id") for row in candidates if isinstance(row, dict)}:
        raise ValueError("full-capture content bounds refer to an unknown candidate")
    symbol_ids = panel["symbol_input_ids"]
    crop_symbol_ids = panel["symbol_crop_input_ids"]
    if (not isinstance(symbol_ids, list) or not isinstance(crop_symbol_ids, list) or
            any(not isinstance(value, str) for value in symbol_ids + crop_symbol_ids)):
        raise ValueError("full-capture symbol-input identity lists are malformed")

    result: dict[str, dict[str, dict[str, Any]]] = {}
    for candidate, classifier_input, view_row in zip(candidates, inputs, views, strict=True):
        if not isinstance(candidate, dict) or not isinstance(classifier_input, dict) or not isinstance(view_row, dict):
            raise ValueError("full-capture candidate/view row is malformed")
        candidate_id = candidate.get("marker_id")
        if (not isinstance(candidate_id, str) or not candidate_id or candidate_id in result or
                view_row.get("marker") != classifier_input or classifier_input.get("marker_id") != candidate_id or
                view_row.get("local_content_bounds") != bounds.get(candidate_id)):
            raise ValueError("full-capture sampler row is not uniquely bound to its classifier input/bounds")
        center_obj = candidate.get("center")
        if (not isinstance(center_obj, dict) or candidate.get("coordinate_space") != "original_pixels" or
                center_obj.get("is_finite") is not True):
            raise ValueError("full-capture candidate geometry is malformed")
        center = bridge._point({"x": center_obj.get("x"), "y": center_obj.get("y")}, width, height,
                               "full-capture candidate center")
        radius = candidate.get("radius")
        if isinstance(radius, bool) or not isinstance(radius, (int, float)) or not math.isfinite(radius) or radius <= 0:
            raise ValueError("full-capture candidate radius is invalid")
        local_bound = view_row.get("local_content_bounds")
        _validate_local_bounds(bridge, local_bound,
            {"center": {"x": center[0], "y": center[1]}}, width, height)

        pair: dict[str, dict[str, Any]] = {}
        for view, scale, minimum, bound in (("local", 2.25, 4.0, local_bound),
                                             ("wide", 6.75, 12.0, None)):
            tensor_bytes = bridge.tensor_f32le(view_row.get(view))
            tensor_sha = bridge._sha(view_row.get(f"{view}_sha256"), f"full-capture {view} sampler SHA")
            if _sha(tensor_bytes) != tensor_sha:
                raise ValueError(f"full-capture {view} tensor bytes differ from their declared hash")
            options = {"width": 32, "height": 32, "radius_scale": scale,
                       "minimum_half_extent_frame_pixels": minimum}
            geometry = tuple(bridge._patch_to_original(center, float(radius), options))
            token_body = bridge._canonical({"schema": "graphreader.marker-fast-evidence-token.v1",
                "source_id": source["source_id"], "panel_id": source["panel_id"],
                "panel_result_sha256": source["panel_result_sha256"], "candidate_id": candidate_id,
                "view": view, "tensor_sha256": tensor_sha, "frame_sha256": frame_sha,
                "ocr_mask_sha256": planes.ocr_mask_sha256,
                "artifact_mask_sha256": planes.artifact_mask_sha256,
                "center": [center[0], center[1]], "radius": float(radius),
                "patch_to_original": list(geometry), "content_bounds": bound})
            token = _FastEvidenceToken(token_body, planes=planes, candidate_id=candidate_id,
                center=center, radius=float(radius), view=view, tensor=tensor_bytes,
                tensor_sha256=tensor_sha, expected_geometry=geometry)
            pair[view] = {"raw_bytes": token, "sha256": _sha(token)}
        result[candidate_id] = pair
    return result, {"source_plane_f32le_sha256": frame_sha, **mask_sha}


def _verify_fast_token(raw: bytes, *, source: dict[str, Any], candidate: dict[str, Any],
                       view: str, expected_frame_sha256: str,
                       expected_ocr_mask_sha256: str | None,
                       expected_artifact_mask_sha256: str | None) -> tuple[bytes, str, dict[str, str]]:
    if not isinstance(raw, _FastEvidenceToken):
        raise ValueError("fast verifier accepts only internally constructed evidence tokens")
    planes = raw.planes
    if raw.view == "local":
        radius_scale, minimum_half = 2.25, 4.0
    elif raw.view == "wide":
        radius_scale, minimum_half = 6.75, 12.0
    else:
        raise ValueError("fast token has an unknown view")
    half = max(minimum_half, raw.radius * radius_scale)
    scale = 2.0 * half / 32.0
    expected_geometry = (scale, 0.0, raw.center[0] - half + half / 32.0,
                         0.0, scale, raw.center[1] - half + half / 32.0,
                         0.0, 0.0, 1.0)
    if (planes.source_id != source["source_id"] or planes.panel_id != source["panel_id"] or
            planes.panel_result_sha256 != source["panel_result_sha256"] or
            planes.width != source["width"] or planes.height != source["height"] or
            planes.frame_sha256 != expected_frame_sha256 or
            raw.candidate_id != candidate["candidate_id"] or raw.view != view or
            raw.center != tuple(candidate["center"]) or raw.radius != candidate["radius"] or
            raw.expected_geometry != expected_geometry or
            _sha(raw.tensor) != raw.tensor_sha256):
        raise ValueError("fast token source/candidate/view identity differs")
    if (expected_ocr_mask_sha256 is not None and
            planes.ocr_mask_sha256 != expected_ocr_mask_sha256):
        raise ValueError("fast token OCR mask snapshot differs")
    if (expected_artifact_mask_sha256 is not None and
            planes.artifact_mask_sha256 != expected_artifact_mask_sha256):
        raise ValueError("fast token artifact mask snapshot differs")
    return raw.tensor, raw.tensor_sha256, {
        "ocr_mask_snapshot_sha256": planes.ocr_mask_sha256,
        "artifact_mask_snapshot_sha256": planes.artifact_mask_sha256,
        "source_plane_f32le_sha256": planes.frame_sha256,
    }


class FastVerifier:
    """Reusable, source-pinned fast verifier with an isolated V1 core module."""

    def __init__(self, *, project_root: Path, expected_protocol_sha256: str) -> None:
        self.project_root = Path(project_root).resolve(strict=True)
        self._reject_reparse(self.project_root)
        if self.project_root != Path(r"E:\Graph-auto-reader").resolve(strict=True):
            raise ValueError("fast verifier requires the canonical E project root")
        self._reject_reparse(PROTOCOL_PATH)
        protocol_bytes = PROTOCOL_PATH.read_bytes()
        if _sha(protocol_bytes) != expected_protocol_sha256:
            raise ValueError("fast verifier protocol differs from caller pin")
        protocol = json.loads(protocol_bytes, object_pairs_hook=_strict_object)
        if protocol.get("schema") != "graphreader.marker-candidate-tensor-fast-verifier.v1":
            raise ValueError("fast verifier protocol schema differs")
        if set(protocol) != {"schema", "scope", "v1_bridge_sha256", "source_bindings", "guarantees", "limitations"}:
            raise ValueError("fast verifier protocol fields differ")
        if (protocol.get("scope") != "synthetic_train_review_only_no_training" or
                protocol.get("guarantees") != {
                    "shared_plane_hashes_per_source": 1,
                    "candidate_order_and_tensor_bytes": "unchanged_v1_core_contract",
                    "label_matching": "unchanged_pinned_v1_core",
                    "training_approved": False, "optimizer_steps": 0} or
                protocol.get("limitations") != [
                    "Fast evidence envelope hashes differ from V1 serialized envelope hashes.",
                    "Batch execution is delegated to a separately pinned wrapper around the V1 batch runner."]):
            raise ValueError("fast verifier protocol guarantees/limitations differ")
        if set(protocol.get("source_bindings", {})) != {"fast_verifier.py", "__init__.py", "bridge.py"}:
            raise ValueError("fast verifier protocol source binding set differs")
        source_path = self.project_root / "tools" / "MarkerCandidateTensorBridgeV1" / "bridge.py"
        self._reject_reparse(source_path)
        self._reject_reparse(Path(__file__))
        package_path = Path(__file__).with_name("__init__.py")
        self._reject_reparse(package_path)
        source_bytes = source_path.read_bytes()
        if (_sha(source_bytes) != protocol.get("v1_bridge_sha256") or
                _sha(Path(__file__).read_bytes()) != protocol.get("source_bindings", {}).get("fast_verifier.py") or
                _sha(package_path.read_bytes()) != protocol.get("source_bindings", {}).get("__init__.py") or
                protocol.get("source_bindings", {}).get("bridge.py") != protocol.get("v1_bridge_sha256")):
            raise ValueError("fast verifier or parent bridge source binding differs")
        module = ModuleType("_marker_candidate_bridge_v1_isolated")
        module.__file__ = str(source_path)
        exec(compile(source_bytes, str(source_path), "exec"), module.__dict__)
        self.source_closure = module.verify_source_bindings(self.project_root)
        module._verify_evidence = _verify_fast_token
        self.bridge = module
        self.protocol = protocol
        self.source_closure_sha256 = _sha(json.dumps(
            self.source_closure, sort_keys=True, separators=(",", ":")).encode("utf-8"))
        self.protocol_sha256 = expected_protocol_sha256

    @staticmethod
    def _reject_reparse(path: Path) -> None:
        absolute = path.absolute()
        ancestors = [absolute]
        while ancestors[-1].parent != ancestors[-1]:
            ancestors.append(ancestors[-1].parent)
        for entry in reversed(ancestors):
            stat = entry.lstat()
            attributes = getattr(stat, "st_file_attributes", 0)
            if entry.is_symlink() or bool(attributes & 0x400):
                raise ValueError("fast verifier path contains a reparse point")

    def materialize_source_core(self, *, source: dict[str, Any], panel_bytes: bytes,
                                source_raster_bytes: bytes, panel_raster_bytes: bytes,
                                positive_truths: list[dict[str, Any]],
                                visible_glyphs: list[dict[str, Any]],
                                generic_overlays: list[dict[str, Any]]) -> dict[str, Any]:
        evidence, plane_hashes = _fast_evidence_map(
            self.bridge, panel_bytes=panel_bytes, source=source)
        result = self.bridge._materialize_source_core(
            source=source, panel_result_bytes=panel_bytes,
            source_raster_bytes=source_raster_bytes, panel_raster_bytes=panel_raster_bytes,
            candidate_evidence=evidence, positive_truths=positive_truths,
            visible_glyphs=visible_glyphs, generic_overlays=generic_overlays)
        result["sampler_input_format"] = "full_capture_panel_shared_plane_fast_v1"
        result["shared_plane_hashes"] = plane_hashes
        result["source_closure_sha256"] = self.source_closure_sha256
        result["source_closure"] = self.source_closure
        result["limitations"].append(
            "Per-view evidence uses a compact in-memory token bound to one source-shared frame/mask snapshot; evidence-envelope hashes differ from the V1 serialized envelope.")
        return result

    def materialize_bound_full_capture_panel(self, *, source: dict[str, Any],
                                             panel_capture_bytes: bytes,
                                             source_raster_bytes: bytes,
                                             panel_raster_bytes: bytes,
                                             complete_receipt_bytes: bytes,
                                             identity_metadata_bytes: bytes,
                                             capture_report_bytes: bytes,
                                             truth_sidecar_bytes: bytes,
                                             artifact_report_bytes: bytes,
                                             project_root: Path) -> dict[str, Any]:
        """Use the unchanged V1 bound sidecar derivation, then fast tensor verification."""
        root = Path(project_root).resolve(strict=True)
        if root != self.project_root:
            raise ValueError("fast bound materialization requires the pinned canonical project root")
        classifier = self.bridge._load_bound_classifier(root, self.source_closure)
        positives, glyphs, overlays, inventory = self.bridge._derive_bound_inventories(
            source=source, complete_receipt_bytes=complete_receipt_bytes,
            identity_metadata_bytes=identity_metadata_bytes,
            capture_report_bytes=capture_report_bytes,
            truth_sidecar_bytes=truth_sidecar_bytes,
            artifact_report_bytes=artifact_report_bytes,
            classify_annotations=classifier)
        if source.get("source_group_id") != source.get("source_id"):
            raise ValueError("source_group_id must be derived from the bound source identity")
        evidence, plane_hashes = _fast_evidence_map(
            self.bridge, panel_bytes=panel_capture_bytes, source=source)
        result = self.bridge._materialize_source_core(
            source=source, panel_result_bytes=panel_capture_bytes,
            source_raster_bytes=source_raster_bytes, panel_raster_bytes=panel_raster_bytes,
            candidate_evidence=evidence, positive_truths=positives,
            visible_glyphs=glyphs, generic_overlays=overlays)
        result["source_closure_sha256"] = self.source_closure_sha256
        result["grouping_basis"] = "source_id_only; participant/study independence is unverified"
        result["truth_inventory_binding"] = inventory
        result["sampler_input_format"] = "full_capture_panel_shared_plane_fast_v1"
        result["shared_plane_hashes"] = plane_hashes
        result["source_closure"] = self.source_closure
        result["limitations"].append(
            "Per-view evidence uses a compact in-memory token bound to one source-shared frame/mask snapshot; evidence-envelope hashes differ from the V1 serialized envelope.")
        return result
