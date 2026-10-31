# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Independent, no-training curation of existing synthetic train candidates.

This module never converts unknown or generic-overlay candidates to negatives.
It recomputes marker/glyph associations from bound train annotations and the
separate bound glyph-visibility audit, detects geometric/tensor collisions,
and refuses a family split when renderer families are not disjoint.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from itertools import islice, combinations
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import uuid
from typing import Any

ROOT = Path(r"E:\Graph-auto-reader")
OUTPUT_PATH = ROOT / "artifacts/goal22-runs/marker-label-curation-train-v1"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
TOLERANCE_PX = 2.0
LOCAL_SCALE = 2.25
LOCAL_MIN_HALF = 4.0
WIDE_SCALE = 6.75
WIDE_MIN_HALF = 12.0
CROP_BILINEAR_SUPPORT_MARGIN_PX = 1.0
TENSOR_BYTES = 32 * 32 * 4
SOURCE_PATHS = {
    "curator": "ml/markers/candidate_label_curation_v1/curator.py",
    "package_init": "ml/markers/candidate_label_curation_v1/__init__.py",
    "curation_protocol": "ml/markers/candidate_label_curation_v1/curation_protocol-v1.json",
    "binding_schema": "ml/markers/candidate_label_curation_v1/input-binding.schema.json",
    "annotation_label_classifier": "ml/synthetic/marker_context_catalog_v1/labels.py",
    "train_corpus_plan": "ml/synthetic/marker_context_train_corpus_v5/plan.py",
    "train_corpus_builder": "ml/synthetic/marker_context_train_corpus_v5/builder.py",
    "catalog": "ml/synthetic/marker_context_catalog_v1/catalog.py",
    "overlay_renderer": "ml/synthetic/marker_context_overlay_v1/overlay.py",
    "fast_batch_runner": "tools/MarkerCandidateTensorBridgeFastV1/batch_runner.py",
    "fast_verifier": "tools/MarkerCandidateTensorBridgeFastV1/fast_verifier.py",
    "fast_batch_protocol": "tools/MarkerCandidateTensorBridgeFastV1/batch_protocol.json",
    "fast_protocol": "tools/MarkerCandidateTensorBridgeFastV1/protocol.json",
    "candidate_bridge_runner": "tools/MarkerCandidateTensorBridgeV1/runner.py",
    "candidate_bridge": "tools/MarkerCandidateTensorBridgeV1/bridge.py",
    "candidate_bridge_protocol": "tools/MarkerCandidateTensorBridgeV1/runner_protocol.json",
    "artifact_association": "tools/MarkerArtifactAssociationV1/association.py",
    "artifact_batch_capture": "tools/MarkerArtifactAssociationV1/batch_capture.py",
    "artifact_batch_producer": "tools/MarkerArtifactAssociationV1/batch_producer.py",
    "artifact_association_runner": "tools/MarkerArtifactAssociationV1/run_batch_analysis.py",
}
INPUT_PATHS = {
    "batch_report": "artifacts/goal22-runs/marker-candidate-tensor-bridge-fast-train-batch-v1/batch-report.json",
    "batch_receipt": "artifacts/goal22-runs/marker-candidate-tensor-bridge-fast-train-batch-v1/fast-batch-receipt.json",
    "complete_receipt": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json",
    "identity_metadata": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json",
    "raster_manifest": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/manifest.json",
    "producer_binding": "artifacts/goal22-runs/marker-context-train-corpus-binding-v5.json",
    "capture_report": "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/report.json",
    "truth_sidecar": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/post-capture-truth/truth.json",
    "artifact_report": "artifacts/goal22-runs/marker-artifact-v5-train-full-analysis-v2/report.json",
    "artifact_receipt": "artifacts/goal22-runs/marker-artifact-v5-train-full-analysis-v2/report.json.receipt.json",
}
CANDIDATE_STATUSES = frozenset({
    "review_positive_connected", "review_positive_isolated", "review_visible_legend_glyph",
    "excluded_symbol_input", "excluded_generic_overlay", "unknown_capture_incomplete",
    "unknown_duplicate_candidate_center", "unknown_ambiguous_collision",
    "unknown_unmatched_visible_glyph", "unknown_unmatched",
})
def canonical(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False, allow_nan=False).encode("utf-8")


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _train_corpus_scene_sha256(scene: Any) -> str:
    """Match marker-context-train-corpus-v5 plan.py canonical scene bytes.

    The corpus builder uses json.dumps' default ensure_ascii=True. General
    descriptor/report canonicalization in this module intentionally does not.
    """
    raw = json.dumps(scene, sort_keys=True, separators=(",", ":"),
                     allow_nan=False).encode("utf-8")
    return digest(raw)


def _strict_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _json(raw: bytes, label: str) -> dict[str, Any]:
    value = json.loads(raw, object_pairs_hook=_strict_object,
                       parse_constant=lambda _: (_ for _ in ()).throw(ValueError("non-finite JSON")))
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object")
    return value


def _sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or HEX64.fullmatch(value) is None:
        raise ValueError(f"{label} must be lowercase SHA-256")
    return value


def _number(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{label} must be numeric")
    converted = float(value)
    if not math.isfinite(converted):
        raise ValueError(f"{label} must be finite")
    return converted


def _project_path(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise ValueError("input paths must be normalized project-relative paths")
    rel = Path(relative)
    if rel.is_absolute() or any(part in ("", ".", "..") for part in rel.parts):
        raise ValueError("input path traversal is forbidden")
    candidate = root / rel
    if not candidate.resolve(strict=True).is_relative_to(root.resolve(strict=True)):
        raise ValueError("input path escapes the project")
    _reject_reparse(root.resolve(strict=True), candidate)
    if not candidate.is_file():
        raise ValueError("input is not a regular file")
    return candidate


def _reject_reparse(root: Path, candidate: Path) -> None:
    """Reject symlinks/junctions at every path component on Windows and POSIX."""
    current = root
    try:
        if current.is_symlink():
            raise ValueError("project root is a reparse point")
        for part in candidate.relative_to(root).parts:
            current = current / part
            if current.is_symlink():
                raise ValueError("input path contains a symlink")
            try:
                attributes = current.stat(follow_symlinks=False).st_file_attributes
            except AttributeError:
                attributes = 0
            if attributes & 0x400:
                raise ValueError("input path contains a reparse point")
    except (OSError, ValueError) as error:
        if isinstance(error, ValueError):
            raise
        raise ValueError("cannot safely inspect input path") from error


def _read_bound(root: Path, entry: dict[str, Any], label: str) -> bytes:
    if not isinstance(entry, dict) or set(entry) != {"path", "sha256"}:
        raise ValueError(f"{label} binding fields differ")
    path = _project_path(root, entry["path"])
    with path.open("rb") as stream:
        raw = stream.read()
    if digest(raw) != _sha(entry["sha256"], f"{label} SHA"):
        raise ValueError(f"{label} bytes differ from pinned SHA")
    return raw


def _distance(a: list[float], b: list[float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _point(value: Any, label: str) -> list[float]:
    if not isinstance(value, list) or len(value) != 2:
        raise ValueError(f"{label} must be an XY pair")
    return [_number(value[0], label), _number(value[1], label)]


def _valid_box(value: Any, label: str) -> tuple[float, float, float, float] | None:
    if value is None:
        return None
    if not isinstance(value, list) or len(value) != 4:
        raise ValueError(f"{label} must be a four-edge box")
    left, top, right, bottom = (_number(part, label) for part in value)
    if right <= left or bottom <= top:
        raise ValueError(f"{label} must have positive area")
    return left, top, right, bottom


def _validate_overlay_record(value: Any, *, scene_id: str, scene_sha256: str,
                             scene_panel_ids: set[str], width: int,
                             height: int) -> tuple[list[float], list[list[float]]]:
    if not isinstance(value, dict):
        raise ValueError("synthetic overlay record is required")
    if (isinstance(width, bool) or not isinstance(width, int) or width <= 0 or
        isinstance(height, bool) or not isinstance(height, int) or height <= 0):
        raise ValueError("source raster dimensions are invalid")
    if (value.get("schema") != "marker-context-inplot-overlay-v1" or
        value.get("split") != "train" or value.get("scene_id") != scene_id or
        value.get("scene_sha256") != scene_sha256 or
        not isinstance(value.get("panel_id"), str) or value.get("panel_id") not in scene_panel_ids or
        value.get("status") != "review_only" or value.get("materialized") is not True or
        value.get("optimization_allowed") is not False or value.get("semantic_non_data") is not True):
        raise ValueError("synthetic overlay identity or review-only scope differs")
    if type(value.get("cue_visible")) is not bool:
        raise ValueError("synthetic overlay cue_visible must be a boolean")
    center = _point(value.get("center"), "synthetic overlay center")
    if not (0 <= center[0] < width and 0 <= center[1] < height):
        raise ValueError("synthetic overlay center is outside source raster")
    raw_box = value.get("cue_box")
    box = _valid_box(raw_box, "synthetic overlay cue box")
    if value["cue_visible"]:
        if box is None or value.get("cue_text") != "note":
            raise ValueError("visible synthetic cue requires its declared cue box and text")
        left, top, right, bottom = box
        if left < 0 or top < 0 or right > width or bottom > height:
            raise ValueError("visible synthetic cue box is outside source raster")
    elif box is not None or value.get("cue_text") is not None:
        raise ValueError("invisible synthetic cue must not declare a box or cue text")
    return center, [] if box is None else [[*box]]


def _intersects_half_extent(center: list[float], half_extent: float,
                            box: tuple[float, float, float, float]) -> bool:
    left, top, right, bottom = box
    return (center[0] + half_extent >= left and center[0] - half_extent < right and
            center[1] + half_extent >= top and center[1] - half_extent < bottom)


def derive_family_id(scene: dict[str, Any]) -> str:
    families = scene.get("families")
    if not isinstance(families, dict) or not families:
        raise ValueError("authenticated scene family map is required")
    for key, item in families.items():
        if not isinstance(key, str) or not isinstance(item, dict) or not isinstance(item.get("key"), str):
            raise ValueError("scene family metadata is malformed")
    return digest(canonical(families))


def _marker_truth_rows(classified_records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Preserve classifier ``id`` as the candidate foreign key.

    Its ``source_id`` is the unprefixed annotation marker UUID, while producer
    candidate ``truth_id`` uses the classifier's typed ``marker:<uuid>`` ID.
    """
    return [{"truth_id": record["id"], "center": record["center"],
             "kind": "connected" if record["bin"] == "connected_positive" else "isolated"}
            for record in classified_records
            if record["bin"] in ("connected_positive", "isolated_positive")]


def _mutual_unique_links(candidates: list[dict[str, Any]], targets: list[dict[str, Any]]) -> tuple[list[list[int]], list[list[int]]]:
    links = [[j for j, target in enumerate(targets)
              if _distance(candidate["center"], target["center"]) <= TOLERANCE_PX]
             for candidate in candidates]
    reverse = [[i for i, selected in enumerate(links) if j in selected]
               for j in range(len(targets))]
    return links, reverse


def _tensor_conflict_rows(rows: list[dict[str, Any]]) -> set[int]:
    # Duplicate complete two-view inputs can leak across groups or inflate a
    # class. A shared local patch with different wide context is distinct.
    by_pair: dict[tuple[str, str], list[int]] = defaultdict(list)
    for index, row in enumerate(rows):
        by_pair[(row["local_tensor_sha256"], row["wide_tensor_sha256"])].append(index)
    duplicate_indexes = {index for pair, indexes in by_pair.items() if len(indexes) > 1 for index in indexes}
    return {index for index in duplicate_indexes if rows[index].get("proposed_label") in
            ("plot_marker", "visible_legend_symbol")}


def _apply_cross_source_tensor_conflicts(sources: list[dict[str, Any]]) -> int:
    rows = [row for source in sources for row in source["rows"]]
    conflicting = _tensor_conflict_rows(rows)
    for index in conflicting:
        row = rows[index]
        row["proposed_label"] = None
        row["training_eligible"] = False
        row["collision_reasons"].append("exact_local_and_wide_tensor_pair_is_duplicated_across_candidates")
        row["label_evidence"].append("paired_tensor_duplicate_checked_across_all_candidates")
    return len(conflicting)


def curate_source(source: dict[str, Any]) -> dict[str, Any]:
    """Recompute candidate labels from one authenticated train source record.

    Input is normalized by ``load_current_inputs``. Status strings in the
    candidate rows are treated only as consistency checks, never as label
    authority. The annotation and glyph visibility record are the authorities.
    """
    required = {"source_id", "scene_id", "scene_sha256", "image_sha256", "panel_id",
                "panel_result_sha256", "family_id", "markers", "glyphs", "overlay_center",
                "overlay_boxes", "candidates", "source_split"}
    if set(source) != required:
        raise ValueError("normalized source fields differ")
    source_id = source["source_id"]
    if not isinstance(source_id, str) or not source_id:
        raise ValueError("source identity is required")
    if source["source_split"] != "train":
        raise ValueError("only the fixed train split is admissible")
    for field in ("scene_sha256", "image_sha256", "panel_result_sha256", "family_id"):
        _sha(source[field], field)
    markers = source["markers"]
    glyphs = source["glyphs"]
    candidates = source["candidates"]
    if not all(isinstance(items, list) for items in (markers, glyphs, candidates)):
        raise ValueError("source truth and candidate arrays are required")
    for item in markers:
        item["center"] = _point(item.get("center"), "marker center")
        if not isinstance(item.get("truth_id"), str) or item.get("kind") not in ("connected", "isolated"):
            raise ValueError("marker truth identity or subtype differs")
    if len({item["truth_id"] for item in markers}) != len(markers):
        raise ValueError("marker truth IDs must be unique")
    for item in glyphs:
        item["center"] = _point(item.get("center"), "glyph center")
        if not isinstance(item.get("glyph_id"), str):
            raise ValueError("glyph identity is required")
        item["proof_valid"] = (
            item.get("association_status") == "matched_artifact_review_only" and
            isinstance(item.get("visible_changed_pixels"), int) and
            not isinstance(item.get("visible_changed_pixels"), bool) and item["visible_changed_pixels"] > 0 and
            HEX64.fullmatch(str(item.get("visibility_proof_sha256", ""))) is not None and
            HEX64.fullmatch(str(item.get("rendered_rgb_sha256", ""))) is not None and
            HEX64.fullmatch(str(item.get("ablated_rgb_sha256", ""))) is not None and
            item["rendered_rgb_sha256"] != item["ablated_rgb_sha256"])
    if len({item["glyph_id"] for item in glyphs}) != len(glyphs):
        raise ValueError("annotated glyph IDs must be unique")
    overlay = _point(source["overlay_center"], "overlay center")
    overlay_boxes = [_valid_box(box, "overlay cue box") for box in source["overlay_boxes"]]
    overlay_boxes = [box for box in overlay_boxes if box is not None]

    ids = [row.get("candidate_id") for row in candidates]
    if any(not isinstance(value, str) or not value for value in ids) or len(set(ids)) != len(ids):
        raise ValueError("candidate IDs must be unique within a source")
    for row in candidates:
        row["center"] = _point(row.get("center"), "candidate center")
        radius = _number(row.get("radius"), "candidate radius")
        if radius <= 0:
            raise ValueError("candidate radius must be positive")
        row["wide_half_extent"] = max(WIDE_MIN_HALF, radius * WIDE_SCALE)
        _sha(row.get("local_tensor_sha256"), "local tensor SHA")
        _sha(row.get("wide_tensor_sha256"), "wide tensor SHA")

    marker_links, marker_reverse = _mutual_unique_links(candidates, markers)
    glyph_links, glyph_reverse = _mutual_unique_links(candidates, glyphs)
    overlay_links = [(_distance(row["center"], overlay) <= TOLERANCE_PX) for row in candidates]
    evidence_rows: list[dict[str, Any]] = []
    for index, row in enumerate(candidates):
        row_status = row.get("source_status")
        if row_status not in CANDIDATE_STATUSES:
            raise ValueError("candidate status is outside the fixed review schema")
        marker_matches = marker_links[index]
        glyph_matches = glyph_links[index]
        result = {
            "candidate_id": row["candidate_id"], "source_id": source_id,
            "source_group_id": source["scene_id"], "scene_id": source["scene_id"],
            "scene_sha256": source["scene_sha256"], "image_sha256": source["image_sha256"],
            "panel_id": source["panel_id"], "panel_result_sha256": source["panel_result_sha256"],
            "family_id": source["family_id"], "source_split": source["source_split"],
            "candidate_center": row["center"], "radius": row["radius"],
            "local_tensor_sha256": row["local_tensor_sha256"],
            "wide_tensor_sha256": row["wide_tensor_sha256"],
            "source_review_status": row_status, "proposed_label": None,
            "label_evidence": [], "collision_reasons": [],
            "assigned_partition": None, "training_eligible": False,
        }
        if row_status in ("excluded_generic_overlay", "excluded_symbol_input"):
            result["label_evidence"].append(f"{row_status}_is_never_a_training_label")
            result["collision_reasons"].append(f"{row_status}_is_excluded_by_source_review")
        else:
            if overlay_links[index]:
                result["collision_reasons"].append("candidate_center_within_2px_of_synthetic_overlay")
            collision_half_extent = row["wide_half_extent"] + CROP_BILINEAR_SUPPORT_MARGIN_PX
            if _intersects_half_extent(row["center"], collision_half_extent,
                                       (overlay[0] - 6, overlay[1] - 6, overlay[0] + 7, overlay[1] + 7)):
                result["collision_reasons"].append("wide_crop_intersects_synthetic_overlay_glyph")
            if any(_intersects_half_extent(row["center"], collision_half_extent, box)
                   for box in overlay_boxes):
                result["collision_reasons"].append("wide_crop_intersects_synthetic_overlay_cue")
            if len(marker_matches) > 1:
                result["collision_reasons"].append("candidate_within_2px_of_multiple_markers")
            if len(glyph_matches) > 1:
                result["collision_reasons"].append("candidate_within_2px_of_multiple_legend_glyphs")
            if marker_matches and glyph_matches:
                result["collision_reasons"].append("marker_glyph_center_collision")
            if marker_matches and len(marker_reverse[marker_matches[0]]) != 1:
                result["collision_reasons"].append("multiple_candidates_share_marker_truth")
            if glyph_matches and len(glyph_reverse[glyph_matches[0]]) != 1:
                result["collision_reasons"].append("multiple_candidates_share_glyph_truth")

            if (not result["collision_reasons"] and row_status in
                    ("review_positive_connected", "review_positive_isolated") and
                    len(marker_matches) == 1 and len(glyph_matches) == 0):
                marker = markers[marker_matches[0]]
                expected_status = ("review_positive_connected" if marker["kind"] == "connected"
                                   else "review_positive_isolated")
                if row.get("truth_id") != marker["truth_id"] or row_status != expected_status:
                    result["collision_reasons"].append("positive_truth_id_or_connection_mismatch")
                else:
                    result["proposed_label"] = "plot_marker"
                    result["label_evidence"].append("unique_mutual_2px_annotation_match")
                    result["label_evidence"].append("connection_subtype_cross_checked")
                    result["matched_truth_id"] = marker["truth_id"]
            elif (not result["collision_reasons"] and row_status == "review_visible_legend_glyph" and
                  len(glyph_matches) == 1 and len(marker_matches) == 0):
                glyph = glyphs[glyph_matches[0]]
                if (not glyph["proof_valid"] or row.get("glyph_id") != glyph["glyph_id"] or
                    row.get("visibility_proof_sha256") != glyph.get("visibility_proof_sha256")):
                    result["collision_reasons"].append("glyph_ablation_identity_or_visibility_proof_mismatch")
                else:
                    result["proposed_label"] = "visible_legend_symbol"
                    result["label_evidence"].append("unique_mutual_2px_legend_annotation_match")
                    result["label_evidence"].append("separate_bound_ablation_report_has_changed_pixels")
                    result["matched_glyph_id"] = glyph["glyph_id"]
                    result["visibility_proof_sha256"] = glyph["visibility_proof_sha256"]
            elif not result["collision_reasons"]:
                if row_status in ("review_positive_connected", "review_positive_isolated",
                                  "review_visible_legend_glyph"):
                    result["collision_reasons"].append("review_label_has_no_unique_primary_evidence_match")
                else:
                    result["label_evidence"].append("unknown_is_not_a_negative_label")
        evidence_rows.append(result)

    # Cross-check full 2-view byte identity after independent geometry matching.
    conflicting = _tensor_conflict_rows(evidence_rows)
    for index in conflicting:
        row = evidence_rows[index]
        row["proposed_label"] = None
        row["collision_reasons"].append("exact_local_and_wide_tensor_pair_is_duplicated_across_candidates")
        row["label_evidence"].append("paired_tensor_duplicate_checked_within_source")
    return {"source_id": source_id, "scene_id": source["scene_id"],
            "source_group_id": source["scene_id"],
            "scene_sha256": source["scene_sha256"], "image_sha256": source["image_sha256"],
            "family_id": source["family_id"], "source_split": source["source_split"],
            "rows": evidence_rows}


def family_split_plan(curated_sources: list[dict[str, Any]]) -> dict[str, Any]:
    """Assign whole scene groups by declared family signature for review only.

    Distinct signatures do not prove independent rendering implementations,
    assets, or historical visual independence. This function never makes any
    row training-eligible.
    """
    family_sources: dict[str, set[str]] = defaultdict(set)
    family_labels: dict[str, Counter[str]] = defaultdict(Counter)
    source_family: dict[str, str] = {}
    for source in curated_sources:
        group_id, family = source["source_group_id"], source["family_id"]
        if group_id in source_family and source_family[group_id] != family:
            raise ValueError("scene-level source group spans multiple families")
        source_family[group_id] = family
        family_sources[family].add(group_id)
        for row in source["rows"]:
            if row["proposed_label"]:
                family_labels[family][row["proposed_label"]] += 1
    families = sorted(family_sources)
    group_counts = {family: len(family_sources[family]) for family in families}
    label_counts = {family: dict(family_labels[family]) for family in families}
    if len(families) < 2:
        return {"status": "blocked_no_assigned_validation_partition",
                "family_count": len(families), "families": families,
                "source_group_counts": group_counts, "label_counts": label_counts,
                "assigned_train_family_ids": [], "assigned_validation_family_ids": [],
                "training_eligible_rows": 0,
                "family_partition_basis": "declared_scene_family_signature_only",
                "independence_verified": False,
                "blocker": "At least two declared family signatures are required to assign a review partition; this does not establish independence."}
    validation_count = max(1, math.ceil(len(families) * 0.2))
    needed = {"plot_marker", "visible_legend_symbol"}
    selected: tuple[list[str], list[str]] | None = None
    searched = 0
    for validation_tuple in islice(combinations(families, validation_count), 4096):
        searched += 1
        validation_set = set(validation_tuple)
        train_tuple = tuple(family for family in families if family not in validation_set)
        train_classes = {name for family in train_tuple for name in family_labels[family]}
        validation_classes = {name for family in validation_tuple for name in family_labels[family]}
        if needed.issubset(train_classes) and needed.issubset(validation_classes):
            selected = (list(train_tuple), list(validation_tuple))
            break
    if selected is None:
        return {"status": "blocked_class_coverage_for_assigned_partition",
                "family_count": len(families), "families": families,
                "source_group_counts": group_counts, "label_counts": label_counts,
                "assigned_train_family_ids": [], "assigned_validation_family_ids": [],
                "training_eligible_rows": 0,
                "family_partition_basis": "declared_scene_family_signature_only",
                "independence_verified": False,
                "partitions_searched": searched,
                "blocker": "No deterministic whole-family partition among the first 4096 candidates represented both classes in train and validation."}
    train, validation = selected
    for source in curated_sources:
        split_name = "validation" if source["family_id"] in validation else "train"
        for row in source["rows"]:
            row["assigned_partition"] = split_name
            row["training_eligible"] = False
    return {"status": "assigned_signature_partition_review_only",
            "family_count": len(families), "families": families,
            "source_group_counts": group_counts, "label_counts": label_counts,
            "assigned_train_family_ids": train, "assigned_validation_family_ids": validation,
            "family_partition_basis": "declared_scene_family_signature_only",
            "training_eligible_rows": 0,
            "independence_verified": False}


def _validate_tensor(root: Path, source_dir: Path, metadata: dict[str, Any], label: str) -> None:
    expected_fields = {"bytes", "encoding", "path", "sha256"}
    if not isinstance(metadata, dict) or set(metadata) != expected_fields:
        raise ValueError(f"{label} tensor metadata fields differ")
    if metadata["bytes"] != TENSOR_BYTES or metadata["encoding"] != "raw_float32_le":
        raise ValueError(f"{label} tensor shape or encoding differs")
    relative = metadata["path"]
    if not isinstance(relative, str) or Path(relative).parts[0:1] != ("tensors",) or any(p in ("..", ".") for p in Path(relative).parts):
        raise ValueError(f"{label} tensor path differs")
    tensor_path = source_dir / relative
    if not tensor_path.resolve(strict=True).is_relative_to(root.resolve(strict=True)):
        raise ValueError("tensor path escapes project")
    _reject_reparse(root.resolve(strict=True), tensor_path)
    raw = tensor_path.read_bytes()
    if len(raw) != TENSOR_BYTES or digest(raw) != _sha(metadata["sha256"], f"{label} tensor SHA"):
        raise ValueError(f"{label} tensor bytes or hash differ")
    if any(not math.isfinite(value) or not 0.0 <= value <= 1.0
           for (value,) in struct.iter_unpack("<f", raw)):
        raise ValueError(f"{label} tensor values are outside finite [0,1]")


def load_current_train_inputs(root: Path, binding_raw: bytes, expected_binding_sha256: str) -> dict[str, Any]:
    """Load exact bound train-only evidence, rejecting unbound/local alternatives."""
    root = root.resolve(strict=True)
    if root != ROOT.resolve(strict=True):
        raise ValueError("only canonical E project root is admitted")
    _reject_reparse(root, root)
    if digest(binding_raw) != _sha(expected_binding_sha256, "external binding SHA"):
        raise ValueError("caller-pinned curation binding SHA differs")
    binding = _json(binding_raw, "curation binding")
    if canonical(binding) != binding_raw or binding.get("schema") != "graphreader.marker-label-curation-input-binding.v1" or binding.get("scope") != "synthetic_train_only_no_training":
        raise ValueError("curation binding schema or scope differs")
    if (binding.get("training_approved") is not False or binding.get("optimizer_steps") != 0 or
        binding.get("dev_input_supplied") is not False or binding.get("private_input_supplied") is not False or
        binding.get("sealed_input_supplied") is not False):
        raise ValueError("curation binding must remain train-only and non-training")
    entries = binding.get("inputs")
    required_inputs = {"batch_report", "batch_receipt", "complete_receipt", "identity_metadata",
                       "raster_manifest", "producer_binding", "capture_report", "truth_sidecar",
                       "artifact_report", "artifact_receipt"}
    if not isinstance(entries, dict) or set(entries) != required_inputs:
        raise ValueError("curation input inventory differs")
    if any(not isinstance(entries[key], dict) or entries[key].get("path") != INPUT_PATHS[key]
           for key in required_inputs):
        raise ValueError("curation input paths differ from the fixed train-only evidence set")
    raw_inputs = {key: _read_bound(root, entries[key], key) for key in sorted(required_inputs)}
    sources_by_id: dict[str, dict[str, Any]] = {}

    batch = _json(raw_inputs["batch_report"], "candidate batch report")
    receipt = _json(raw_inputs["batch_receipt"], "candidate batch receipt")
    complete = _json(raw_inputs["complete_receipt"], "corpus complete receipt")
    identity = _json(raw_inputs["identity_metadata"], "train identity metadata")
    capture = _json(raw_inputs["capture_report"], "train capture report")
    truth = _json(raw_inputs["truth_sidecar"], "train truth sidecar")
    artifact = _json(raw_inputs["artifact_report"], "artifact visibility report")
    artifact_receipt = _json(raw_inputs["artifact_receipt"], "artifact report receipt")

    if (batch.get("schema") != "graphreader.marker-candidate-tensor-bridge-batch-report.v1" or
        batch.get("status") != "completed_review_only" or batch.get("source_count") != 64 or
        batch.get("training_approved") is not False or batch.get("trainable_rows") != 0 or
        batch.get("negative_training_rows") != 0 or batch.get("dev_input_supplied") is not False or
        batch.get("private_input_supplied") is not False or batch.get("sealed_input_supplied") is not False):
        raise ValueError("candidate batch is not the fixed train-only review-only report")
    if (receipt.get("schema") != "graphreader.marker-candidate-tensor-fast-batch-receipt.v1" or
        receipt.get("status") != "completed_review_only" or receipt.get("source_count") != 64 or
        receipt.get("batch_report_sha256") != digest(raw_inputs["batch_report"]) or
        receipt.get("input_sha256", {}).get("complete_receipt") != digest(raw_inputs["complete_receipt"]) or
        receipt.get("input_sha256", {}).get("identity_metadata") != digest(raw_inputs["identity_metadata"]) or
        receipt.get("input_sha256", {}).get("capture_report") != digest(raw_inputs["capture_report"]) or
        receipt.get("input_sha256", {}).get("truth_sidecar") != digest(raw_inputs["truth_sidecar"]) or
        receipt.get("input_sha256", {}).get("artifact_report") != digest(raw_inputs["artifact_report"]) or
        receipt.get("training_approved") is not False or receipt.get("optimizer_steps") != 0 or
        receipt.get("dev_input_supplied") is not False or
        receipt.get("private_input_supplied") is not False or receipt.get("sealed_input_supplied") is not False or
        receipt.get("trainable_rows") != 0 or receipt.get("negative_training_rows") != 0):
        raise ValueError("fast batch receipt does not bind all curation inputs")
    if (complete.get("schema") != "marker-context-train-corpus-complete-v5" or
        complete.get("training_approved") is not False or complete.get("mode") != "full" or
        complete.get("source_count") != 64 or complete.get("identity_sha256") != digest(raw_inputs["identity_metadata"]) or
        complete.get("truth_sha256") != digest(raw_inputs["truth_sidecar"]) or
        complete.get("builder_binding_sha256") != digest(raw_inputs["producer_binding"]) or
        complete.get("raster_manifest_sha256") != digest(raw_inputs["raster_manifest"])):
        raise ValueError("full synthetic train completion identities differ")
    producer_binding = _json(raw_inputs["producer_binding"], "train corpus producer binding")
    raster_manifest = _json(raw_inputs["raster_manifest"], "train raster manifest")
    if (producer_binding.get("schema") != "marker-context-train-corpus-binding-v5" or
        producer_binding.get("training_approved") is not False or
        raster_manifest.get("schema") != "marker-context-train-raster-v5" or
        raster_manifest.get("mode") != "full" or raster_manifest.get("training_approved") is not False or
        raster_manifest.get("builder_binding_sha256") != digest(raw_inputs["producer_binding"]) or
        len(raster_manifest.get("sources", [])) != 64):
        raise ValueError("train producer binding or raster manifest scope differs")
    if (identity.get("schema") != "marker-context-train-identity-v5" or identity.get("mode") != "full" or
        identity.get("training_approved") is not False or len(identity.get("sources", [])) != 64 or
        truth.get("schema") != "marker-context-train-truth-v5" or truth.get("mode") != "full" or
        truth.get("training_approved") is not False or len(truth.get("sources", [])) != 64):
        raise ValueError("train identity or truth sidecar scope differs")
    if (capture.get("schema") != "graphreader.marker-context-bound-input-full-capture-report.v1" or
        capture.get("status") != "completed_all_sources" or capture.get("source_count") != 64 or
        capture.get("all_sources_succeeded") is not True or
        capture.get("source_accounting_complete") is not True or
        capture.get("truth_content_read") is not False or capture.get("truth_path_resolved") is not False or
        capture.get("training_approved") is not False or capture.get("optimizer_steps") != 0 or
        capture.get("dev_input_supplied") is not False or capture.get("private_input_supplied") is not False or
        capture.get("sealed_input_supplied") is not False):
        raise ValueError("capture report scope or denominator differs")
    if (artifact.get("schema") != "graphreader.marker-artifact-v5-train-visibility-audit.v1" or
        artifact.get("split") != "train" or artifact.get("source_count") != 64 or
        artifact.get("completed_source_count") != 64 or artifact.get("failed_source_count") != 0 or
        artifact.get("all_sources_succeeded") is not True or artifact.get("training_approved") is not False or
        artifact.get("trainable_rows") != 0 or artifact.get("negative_training_rows") != 0 or
        artifact.get("truth_content_read") is not False or artifact.get("truth_path_resolved") is not False or
        artifact.get("source_accounting_complete") is not True or
        artifact.get("dev_input_supplied") is not False or artifact.get("private_input_supplied") is not False or
        artifact.get("sealed_input_supplied") is not False):
        raise ValueError("artifact report scope or completion differs")
    if (artifact_receipt.get("schema") != "graphreader.marker-artifact-v5-analysis-receipt.v1" or
        artifact_receipt.get("report_sha256") != digest(raw_inputs["artifact_report"]) or
        artifact_receipt.get("source_count") != 64 or
        artifact_receipt.get("completed_source_count") != 64 or
        artifact_receipt.get("failed_source_count") != 0 or
        artifact_receipt.get("training_approved") is not False or
        artifact_receipt.get("optimizer_steps") != 0 or
        artifact_receipt.get("truth_content_read") is not False):
        raise ValueError("artifact report receipt does not bind the complete review-only report")

    identity_rows = identity["sources"]
    truth_by_image = {row.get("image_sha256"): row for row in truth["sources"]}
    raster_by_source = {row.get("source_id"): row for row in raster_manifest.get("sources", [])}
    capture_by_source = {row.get("source_id"): row for row in capture.get("sources", [])}
    artifact_by_source = {row.get("source_id"): row for row in artifact.get("sources", [])}
    batch_by_source = {row.get("source_id"): row for row in batch.get("sources", [])}
    if any(len(mapping) != 64 for mapping in
           (truth_by_image, raster_by_source, capture_by_source, artifact_by_source, batch_by_source)):
        raise ValueError("one or more source evidence inventories are incomplete or duplicate")

    # Verify the complete algorithm/provenance source closure before parsing
    # annotations or importing the existing label classifier.
    source_bindings = binding.get("source_bindings")
    if not isinstance(source_bindings, dict) or set(source_bindings) != set(SOURCE_PATHS):
        raise ValueError("curation source closure inventory differs")
    verified_sources: dict[str, bytes] = {}
    for key, relative in SOURCE_PATHS.items():
        entry = source_bindings[key]
        if not isinstance(entry, dict) or set(entry) != {"path", "sha256"} or entry.get("path") != relative:
            raise ValueError(f"curation source binding path differs: {key}")
        verified_sources[key] = _read_bound(root, entry, f"source:{key}")
    labels_path = root / SOURCE_PATHS["annotation_label_classifier"]
    labels_raw = verified_sources["annotation_label_classifier"]
    module_name = "_graphreader_curation_bound_labels"
    namespace: dict[str, Any] = {"__name__": module_name, "__file__": str(labels_path)}
    exec(compile(labels_raw, str(labels_path), "exec", dont_inherit=True), namespace)
    classify = namespace.get("classify")
    if not callable(classify):
        raise ValueError("bound annotation classifier API is missing")

    if len(batch["sources"]) != 64 or len(identity_rows) != 64:
        raise ValueError("source denominator differs")
    for index, batch_source in enumerate(batch["sources"]):
        expected_dir_name = f"source-{index:03d}"
        if batch_source.get("source_index") != index or batch_source.get("source_output") != f"sources/{expected_dir_name}":
            raise ValueError("batch source order or output location differs")
        source_id = batch_source.get("source_id")
        ident_matches = [item for item in identity_rows if item.get("source_id") == source_id]
        if len(ident_matches) != 1:
            raise ValueError("batch source has no unique identity metadata row")
        ident = ident_matches[0]
        truth_row = truth_by_image.get(ident.get("image_sha256"))
        raster_row = raster_by_source.get(source_id)
        capture_row = capture_by_source.get(source_id)
        artifact_source = artifact_by_source.get(source_id)
        if (truth_row is None or raster_row is None or capture_row is None or artifact_source is None or
            batch_source.get("source_raster_sha256") != ident.get("image_sha256") or
            raster_row.get("sha256") != ident.get("image_sha256") or
            raster_row.get("file") != f"{ident.get('image_sha256')}.png" or
            capture_row.get("image_sha256") != ident.get("image_sha256") or
            artifact_source.get("image_sha256") != ident.get("image_sha256") or
            truth_row.get("case_id") != ident.get("case_id") or
            truth_row.get("scene", {}).get("scene_id") != ident.get("scene_id") or
            _train_corpus_scene_sha256(truth_row.get("scene")) != ident.get("scene_sha256")):
            raise ValueError("source/image/scene/case cross-evidence join differs")
        if capture_row.get("status") not in ("completed", "success") or artifact_source.get("status") != "completed":
            raise ValueError("failed capture or artifact source cannot be admitted")
        family_id = derive_family_id(truth_row["scene"])
        annotation = truth_row.get("annotation")
        if not isinstance(annotation, dict) or annotation.get("scene_id") != ident.get("scene_id"):
            raise ValueError("annotation does not bind to authenticated scene")
        if classify(annotation, expected_split="train").get("scene_id") != ident.get("scene_id"):
            raise ValueError("train annotation classification identity differs")
        classified = classify(annotation, expected_split="train")
        glyph_records = [record for record in classified["records"]
                         if record["bin"] == "contextual_negative"]
        markers = _marker_truth_rows(classified["records"])
        glyph_by_id = {record["source_id"]: record for record in glyph_records}
        glyphs = []
        for item in artifact_source.get("glyphs", []):
            if item.get("glyph_id") not in glyph_by_id:
                raise ValueError("artifact report glyph is absent from source annotation")
            truth_glyph = glyph_by_id[item["glyph_id"]]
            if _point(item.get("center"), "artifact glyph center") != truth_glyph["center"]:
                raise ValueError("artifact report glyph center differs from source annotation")
            glyphs.append({
                "glyph_id": item["glyph_id"], "center": item["center"],
                "association_status": item.get("association_status"),
                "visibility_proof_sha256": item.get("visibility_proof_sha256"),
                "rendered_rgb_sha256": item.get("rendered_rgb_sha256"),
                "ablated_rgb_sha256": item.get("ablated_rgb_sha256"),
                "visible_changed_pixels": item.get("visible_changed_pixels"),
            })
        if {item["glyph_id"] for item in glyphs} != set(glyph_by_id):
            raise ValueError("artifact report does not account for every annotated legend glyph")
        source_dir = root / "artifacts/goal22-runs/marker-candidate-tensor-bridge-fast-train-batch-v1" / "sources" / expected_dir_name
        review_path = source_dir / "review-rows.json"
        _reject_reparse(root, review_path)
        review_raw = review_path.read_bytes()
        if digest(review_raw) != batch_source.get("review_rows_sha256"):
            raise ValueError("review rows differ from fast batch source binding")
        review = _json(review_raw, "per-source review rows")
        if (review.get("source_id") != source_id or review.get("source_group_id") != source_id or
            review.get("split") != "train" or batch_source.get("panel_id") != artifact_source.get("panel_id") or
            review.get("panel_image_sha256") != artifact_source.get("image_sha256") or
            review.get("panel_result_sha256") != artifact_source.get("panel_result_sha256") or
            review.get("source_raster_sha256") != ident.get("image_sha256") or
            review.get("training_approved") is not False or review.get("trainable_rows") != 0):
            raise ValueError("per-source review rows do not bind to source report")
        image_path = root / "artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only" / f"{ident['image_sha256']}.png"
        _reject_reparse(root, image_path)
        image_bytes = image_path.read_bytes()
        if digest(image_bytes) != ident["image_sha256"]:
            raise ValueError("source raster differs from identity metadata")
        if (artifact_source.get("scene_id") != ident.get("scene_id") or
            artifact_source.get("scene_sha256") != ident.get("scene_sha256") or
            artifact_source.get("panel_result_sha256") != review.get("panel_result_sha256")):
            raise ValueError("artifact panel/source provenance differs")
        scene_panels = truth_row.get("scene", {}).get("panels")
        annotation_panels = annotation.get("panels")
        if (not isinstance(scene_panels, list) or not isinstance(annotation_panels, list) or
            not scene_panels or
            any(not isinstance(panel, dict) or not isinstance(panel.get("panel_id"), str)
                for panel in scene_panels + annotation_panels)):
            raise ValueError("native scene/annotation panel identity arrays are malformed")
        scene_panel_ids = {panel["panel_id"] for panel in scene_panels}
        annotation_panel_ids = {panel["panel_id"] for panel in annotation_panels}
        if (len(scene_panel_ids) != len(scene_panels) or
            len(annotation_panel_ids) != len(annotation_panels) or
            scene_panel_ids != annotation_panel_ids):
            raise ValueError("native scene and annotation panel identities differ")
        overlay_center, overlay_boxes = _validate_overlay_record(
            truth_row.get("overlay"), scene_id=ident["scene_id"],
            scene_sha256=ident["scene_sha256"], scene_panel_ids=scene_panel_ids,
            width=raster_row.get("width"), height=raster_row.get("height"))
        if overlay_center != _point(artifact_source.get("overlay_center"), "artifact report overlay center"):
            raise ValueError("overlay center differs across independent reports")
        if artifact_source.get("candidate_count") != review.get("candidate_count"):
            raise ValueError("candidate denominator differs across reports")
        source_candidates = []
        tensor_files = review.get("tensor_files")
        if not isinstance(tensor_files, dict) or set(tensor_files) != {r.get("candidate_id") for r in review.get("rows", [])}:
            raise ValueError("candidate tensor inventory differs from review rows")
        for candidate_index, row in enumerate(review["rows"]):
            tensor_entry = tensor_files[row["candidate_id"]]
            if not isinstance(tensor_entry, dict) or set(tensor_entry) != {"local", "wide"}:
                raise ValueError("candidate view inventory differs")
            for view in ("local", "wide"):
                _validate_tensor(root, source_dir, tensor_entry[view], view)
            if row.get("local_tensor_sha256") != tensor_entry["local"].get("sha256") or row.get("wide_tensor_sha256") != tensor_entry["wide"].get("sha256"):
                raise ValueError("review row tensor hash differs from stored tensor index")
            source_candidates.append({
                "candidate_id": row.get("candidate_id"), "center": row.get("center"),
                "radius": row.get("radius"), "source_status": row.get("status"),
                "truth_id": row.get("truth_id"), "glyph_id": row.get("glyph_id"),
                "visibility_proof_sha256": row.get("visibility_proof_sha256"),
                "local_tensor_sha256": row.get("local_tensor_sha256"),
                "wide_tensor_sha256": row.get("wide_tensor_sha256"),
            })
        normalized = {
            "source_id": source_id, "scene_id": ident["scene_id"],
            "scene_sha256": ident["scene_sha256"], "image_sha256": ident["image_sha256"],
            "panel_id": artifact_source["panel_id"], "panel_result_sha256": artifact_source["panel_result_sha256"],
            "family_id": family_id, "markers": markers, "glyphs": glyphs,
            "overlay_center": overlay_center, "overlay_boxes": overlay_boxes,
            "candidates": source_candidates, "source_split": "train",
        }
        if source_id in sources_by_id:
            raise ValueError("duplicate train source identity")
        sources_by_id[source_id] = curate_source(normalized)

    if len(sources_by_id) != 64:
        raise ValueError("curation did not account for all train sources")
    curated = [sources_by_id[source["source_id"]] for source in batch["sources"]]
    cross_source_conflicts = _apply_cross_source_tensor_conflicts(curated)
    split = family_split_plan(curated)
    labels = Counter(row["proposed_label"] for source in curated for row in source["rows"] if row["proposed_label"])
    statuses = Counter("curated_label" if row["proposed_label"] else
                        "collision_or_mismatch" if row["collision_reasons"] else "unknown_or_excluded"
                        for source in curated for row in source["rows"])
    group_label_counts = Counter((row["proposed_label"], source["source_group_id"])
                                 for source in curated for row in source["rows"] if row["proposed_label"])
    distinct_groups = {label: len({group for candidate_label, group in group_label_counts if candidate_label == label})
                       for label in ("plot_marker", "visible_legend_symbol")}
    fam_sets = {source["family_id"] for source in curated}
    report = {
        "schema": "graphreader.marker-label-curation-report.v1",
        "scope": "synthetic_train_only_no_training",
        "status": "review_only_partition_available" if split["status"] == "assigned_signature_partition_review_only" else split["status"],
        "input_binding_sha256": expected_binding_sha256,
        "input_sha256": {key: digest(value) for key, value in raw_inputs.items()},
        "source_bundle_sha256": digest(canonical({key: digest(value) for key, value in verified_sources.items()})),
        "source_count": len(curated), "source_group_count": len({s["source_group_id"] for s in curated}),
        "candidate_count": sum(len(s["rows"]) for s in curated),
        "curated_label_counts": dict(labels), "candidate_status_counts": dict(statuses),
        "cross_source_tensor_conflict_rows": cross_source_conflicts,
        "source_groups_with_label": distinct_groups, "family_split": split,
        "next_data_plan": {
            "target_family_count": 10,
            "proposed_partition": "8 train families and 2 held-out validation families",
            "minimum_label_coverage": "both proposed labels in every family",
            "independence_requirement": "family signatures must bind distinct rendering sources and assets; recipe cells alone do not qualify",
            "status": "planning_target_not_existing_evidence",
        },
        "source_family_signature_count": len(fam_sets),
        "recipe_cell_count": len(complete.get("recipe_cells", [])),
        "candidate_rows": [row for source in curated for row in source["rows"]],
        "limitations": [
            "Candidate labels are recomputed from hash-bound synthetic train annotations and a separate bound glyph-ablation report; original review receipts are untouched.",
            "The glyph ablation proof is cross-checked by hash and metadata but is not re-rendered by this curation tool.",
            "Complete local+wide tensor-pair duplicates are excluded; shared single views with differing context remain valid for this dual-view hypothesis.",
            "Production classifier factory/runtime parity is not established; tensors remain raw 32x32 float32le sampler outputs.",
            "Declared family signatures are not evidence of independent rendering implementations, assets, or historical visual independence; no candidate row is training-eligible.",
            "Visible legend symbols are the only artifact class in scope; unknowns, generic overlays, and all other artifacts are not negatives.",
        ],
        "training_eligible_rows": 0,
        "training_approved": False, "optimizer_steps": 0,
        "dev_input_supplied": False, "private_input_supplied": False, "sealed_input_supplied": False,
    }
    return report


def write_new_report(output_path: Path, report: dict[str, Any]) -> str:
    if output_path != OUTPUT_PATH:
        raise ValueError("curation output path differs from the fixed fresh destination")
    if output_path.exists() or output_path.is_symlink():
        raise ValueError("curation output must be a fresh path")
    if not output_path.is_absolute() or not output_path.resolve(strict=False).is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("curation output must remain within the project")
    _reject_reparse(ROOT.resolve(strict=True), output_path.parent)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    stage_path = output_path.parent / f".{output_path.name}.stage-{uuid.uuid4().hex}"
    stage_path.mkdir()
    raw = canonical(report)
    report_path = stage_path / "report.json"
    with report_path.open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    receipt = {"schema": "graphreader.marker-label-curation-receipt.v1",
               "status": report["status"], "report_sha256": digest(raw),
               "input_binding_sha256": report["input_binding_sha256"],
               "source_count": report["source_count"],
               "candidate_count": report["candidate_count"],
               "curated_label_counts": report["curated_label_counts"],
               "training_eligible_rows": report["training_eligible_rows"],
               "training_approved": False, "optimizer_steps": 0}
    receipt_raw = canonical(receipt)
    with (stage_path / "receipt.json").open("xb") as stream:
        stream.write(receipt_raw)
        stream.flush()
        os.fsync(stream.fileno())
    if output_path.exists() or output_path.is_symlink():
        raise ValueError("curation output appeared during staged write")
    stage_path.rename(output_path)
    return digest(receipt_raw)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binding", required=True, help="absolute project-local input binding JSON")
    parser.add_argument("--binding-sha256", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    binding_path = Path(args.binding)
    if not binding_path.is_absolute():
        raise ValueError("binding path must be absolute")
    if not binding_path.resolve(strict=True).is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("binding path must remain inside E project")
    _reject_reparse(ROOT.resolve(strict=True), binding_path)
    if not binding_path.is_file():
        raise ValueError("binding must be a regular file")
    binding_raw = binding_path.read_bytes()
    if not Path(args.output).is_absolute():
        raise ValueError("output path must be absolute and project-local")
    output = Path(args.output)
    if output != OUTPUT_PATH:
        raise ValueError("output path differs from the fixed fresh destination")
    report = load_current_train_inputs(ROOT, binding_raw, args.binding_sha256)
    receipt_hash = write_new_report(output, report)
    print(json.dumps({"status": report["status"], "receipt_sha256": receipt_hash,
                      "source_count": report["source_count"], "candidate_count": report["candidate_count"],
                      "curated_label_counts": report["curated_label_counts"],
                      "family_split_status": report["family_split"]["status"],
                      "training_eligible_rows": report["training_eligible_rows"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
