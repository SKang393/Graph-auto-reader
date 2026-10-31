# SPDX-License-Identifier: Apache-2.0
"""Bounded metadata and glyph-visibility audit for the fixed V5 train capture.

The module's full 64-source renderer is intentionally separate from the
single-source v2 association entry. `prepare_full_capture_input_manifest`
reads only the pinned capture report and its no-truth panel-result JSON files.
The later audit path regenerates scenes from the fixed train recipes, never
opens captured raster or truth sidecars, and emits review-only evidence.
"""
from collections import Counter
from hashlib import sha256
import json
import math
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
CAPTURE_REPORT_REL = Path("artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/report.json")
PANEL_RESULTS_REL = Path("artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/panels")
CAPTURE_REPORT_SHA256 = "c46e2a909df60d8665a7121c0a60a2bf0f6a64e60ce36b36dad19d40b8ff8de3"
TRAIN_SOURCE_BINDING_SHA256 = "f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4"
MATCH_TOLERANCE_PX = 2.0
ANALYSIS_SOURCE_PATHS = (
    "tools/MarkerArtifactAssociationV1/association.py",
    "tools/MarkerArtifactAssociationV1/producer.py",
    "tools/MarkerArtifactAssociationV1/full_capture_producer.py",
    "tools/MarkerArtifactAssociationV1/batch_capture.py",
    "tools/MarkerArtifactAssociationV1/batch_producer.py",
    "tools/MarkerArtifactAssociationV1/run_batch_analysis.py",
)


def _digest(raw):
    return sha256(raw).hexdigest()


def _strict_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON field")
        value[key] = item
    return value


def _read_project_file(relative):
    path = ROOT
    for part in Path(relative).parts:
        path = path / part
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("input path contains a link or reparse point")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve(strict=True)) or not resolved.is_file():
        raise ValueError("input path escapes project or is not a file")
    return resolved.read_bytes()


def _safe_project_directory(relative):
    path = ROOT
    for part in Path(relative).parts:
        path = path / part
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("input directory contains a link or reparse point")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve(strict=True)) or not resolved.is_dir():
        raise ValueError("input directory escapes project or is not a directory")
    return resolved


def _validate_result_reference(result_reference):
    if not isinstance(result_reference, str) or not result_reference:
        raise ValueError("caller must supply a fresh result reference")
    relative = Path(result_reference)
    if (relative.is_absolute() or ".." in relative.parts or
            len(relative.parts) < 3 or relative.parts[0:2] != ("artifacts", "goal22-runs")):
        raise ValueError("result reference must be a relative artifacts/goal22-runs path")
    current = ROOT
    for part in relative.parts[:-1]:
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if current.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("result reference contains a link or reparse point")
        if not current.is_dir():
            raise ValueError("result reference ancestor is not a directory")
    target = ROOT / relative
    if target.exists() or target.is_symlink():
        raise ValueError("result reference already exists")
    return relative.as_posix()


def build_source_runtime_manifest(input_manifest, *, result_reference):
    """Create the caller-pinned code/runtime identity required before full analysis."""
    from .full_capture_producer import verify_train_capture_bundle
    import numpy as np
    import PIL

    input_sha = _digest(_canonical(input_manifest))
    sources = {}
    for relative in ANALYSIS_SOURCE_PATHS:
        sources[relative] = _digest(_read_project_file(relative))
    train_bundle = verify_train_capture_bundle()
    result_path = _validate_result_reference(result_reference)
    return {
        "schema": "graphreader.marker-artifact-v5-analysis-source-runtime.v1",
        "input_manifest_sha256": input_sha,
        "capture_report_sha256": input_manifest["capture_report_sha256"],
        "capture_binding_sha256": input_manifest["capture_binding_sha256"],
        "result_reference": result_path,
        "analysis_source_sha256": sources,
        "train_corpus_source_binding_sha256": train_bundle["train_corpus_builder_binding_sha256"],
        "renderer_bundle_sha256": train_bundle["renderer_bundle_sha256"],
        "renderer_bundle_combined_sha256": train_bundle["sha256"],
        "runtime": {"python_version": sys.version, "pillow_version": PIL.__version__,
                    "numpy_version": np.__version__},
        "split": "train", "match_tolerance_px": MATCH_TOLERANCE_PX,
        "model_inference_performed_by_analysis": False,
        "optimizer_steps": 0, "training_approved": False,
    }


def verify_source_runtime_manifest(input_manifest, source_runtime_manifest, *,
                                   expected_source_runtime_sha256, result_reference):
    canonical = _canonical(source_runtime_manifest)
    if _digest(canonical) != expected_source_runtime_sha256:
        raise ValueError("caller-pinned source/runtime manifest SHA-256 differs")
    expected = build_source_runtime_manifest(input_manifest, result_reference=result_reference)
    if canonical != _canonical(expected):
        raise ValueError("source/runtime manifest differs from current code, runtime, or inputs")
    return _digest(canonical)


def _identity_matrix(value):
    return value == [1, 0, 0, 0, 1, 0, 0, 0, 1]


def bind_report_metadata(report, panel_json_by_file, *, report_sha256, expected_seeds):
    """Validate source/panel/result identity without loading pixels or truth."""
    if report_sha256 != CAPTURE_REPORT_SHA256:
        raise ValueError("full train capture report SHA-256 differs")
    if (not isinstance(report, dict) or
            report.get("schema") != "graphreader.marker-context-bound-input-full-capture-report.v1" or
            report.get("source_count") != 64 or report.get("source_accounting_complete") is not True or
            report.get("all_sources_succeeded") is not True or report.get("truth_content_read") is not False or
            report.get("truth_path_resolved") is not False or report.get("private_input_supplied") is not False or
            report.get("sealed_input_supplied") is not False or report.get("dev_input_supplied") is not False or
            report.get("training_approved") is not False or report.get("optimizer_steps") != 0):
        raise ValueError("full train capture scope or completion flags differ")
    sources = report.get("sources")
    events = report.get("events")
    if not isinstance(sources, list) or len(sources) != 64 or not isinstance(events, list) or len(events) != 64:
        raise ValueError("full train capture must contain exactly 64 source and panel event rows")
    if len(set(expected_seeds)) != 64 or {row.get("seed") for row in sources} != set(expected_seeds):
        raise ValueError("captured seed inventory differs from the fixed V5 train plan")
    event_by_source = {}
    for event in events:
        if not isinstance(event, dict) or set(event) != {"source_id", "panel_id", "file", "sha256"}:
            raise ValueError("panel event schema differs")
        if event["source_id"] in event_by_source:
            raise ValueError("source has duplicate panel events")
        if Path(event["file"]).name != event["file"] or not event["file"].startswith("panel-"):
            raise ValueError("panel event file is not a fixed panel basename")
        event_by_source[event["source_id"]] = event
    if len({row.get("source_id") for row in sources}) != 64 or len({row.get("scene_id") for row in sources}) != 64:
        raise ValueError("captured source or scene IDs are not unique")
    if {row.get("source_index") for row in sources} != set(range(64)):
        raise ValueError("captured source indices are not the fixed 0..63 inventory")
    if set(event_by_source) != {row.get("source_id") for row in sources}:
        raise ValueError("panel event source inventory differs")

    rows = []
    seen_files = set()
    for source in sorted(sources, key=lambda row: row.get("source_index", -1)):
        if (source.get("status") != "completed" or source.get("failure") not in (None, "") or
                type(source.get("seed")) is not int or type(source.get("source_index")) is not int or
                source.get("panel_count") != 1 or not isinstance(source.get("panels"), list) or
                len(source["panels"]) != 1):
            raise ValueError("source is not one completed full-image train panel")
        panel_report = source["panels"][0]
        event = event_by_source[source["source_id"]]
        if event["file"] != f"panel-{source['source_index']:04d}.json":
            raise ValueError("panel output file ordering differs from the fixed capture")
        if panel_report.get("status") != "completed" or panel_report.get("panel_id") != event["panel_id"]:
            raise ValueError("source panel and output event identity differ")
        provenance = panel_report.get("provenance")
        if (not isinstance(provenance, dict) or provenance.get("identity") is not True or
                provenance.get("source_sha256") != source.get("image_sha256") or
                provenance.get("panel_image_sha256") != source.get("image_sha256") or
                not _identity_matrix(provenance.get("source_to_panel_matrix")) or
                not _identity_matrix(provenance.get("panel_to_source_matrix"))):
            raise ValueError("train source/panel identity transform differs")
        file_name = event["file"]
        if file_name in seen_files or file_name not in panel_json_by_file:
            raise ValueError("panel output file is duplicated or absent")
        seen_files.add(file_name)
        panel_value = panel_json_by_file[file_name]
        if (not isinstance(panel_value, tuple) or len(panel_value) != 2 or
                not isinstance(panel_value[0], dict) or not isinstance(panel_value[1], str)):
            raise ValueError("panel JSON bytes and their SHA-256 are required")
        panel, panel_bytes_sha = panel_value
        if not isinstance(panel, dict) or set(panel) != {
                "source_id", "case_id", "scene_id", "scene_sha256", "seed", "panel_id",
                "panel_sha256", "provenance", "frame", "center_markers", "ocr_regions",
                "classifier_inputs", "symbol_input_ids", "symbol_crop_input_ids",
                "original_pixel_content_bounds", "sampled_local_and_experimental_wide_views"}:
            raise ValueError("saved panel result schema differs or contains unrecognized fields")
        if (panel.get("source_id") != source["source_id"] or panel.get("scene_id") != source["scene_id"] or
                panel.get("scene_sha256") != source["scene_sha256"] or panel.get("seed") != source["seed"] or
                panel.get("panel_id") != event["panel_id"] or panel.get("panel_sha256") != source["image_sha256"]):
            raise ValueError("saved panel result identity differs from report row")
        if panel_bytes_sha != event["sha256"]:
            raise ValueError("saved panel result bytes differ from report event hash")
        frame = panel.get("frame")
        if not isinstance(frame, dict) or type(frame.get("width")) is not int or type(frame.get("height")) is not int:
            raise ValueError("saved panel frame dimensions are invalid")
        if (frame["width"] != provenance.get("source_width") or
                frame["height"] != provenance.get("source_height")):
            raise ValueError("saved panel frame differs from identity source dimensions")
        centers = panel.get("center_markers")
        if not isinstance(centers, list) or len(centers) != panel_report.get("marker_count"):
            raise ValueError("saved proposal count differs from capture report")
        ids = set()
        for candidate in centers:
            if (not isinstance(candidate, dict) or candidate.get("coordinate_space") != "original_pixels" or
                    not isinstance(candidate.get("marker_id"), str) or not candidate["marker_id"] or
                    candidate["marker_id"] in ids):
                raise ValueError("saved proposal identity or coordinate space is invalid")
            ids.add(candidate["marker_id"])
            point = candidate.get("center")
            if (not isinstance(point, dict) or point.get("is_finite") is not True or
                    any(type(point.get(axis)) not in (int, float) or not math.isfinite(point[axis])
                        for axis in ("x", "y")) or
                    not (0 <= point["x"] < frame["width"] and 0 <= point["y"] < frame["height"])):
                raise ValueError("saved proposal center is not a finite in-frame point")
        rows.append({
            "source_index": source["source_index"], "seed": source["seed"],
            "source_id": source["source_id"], "scene_id": source["scene_id"],
            "scene_sha256": source["scene_sha256"], "source_raster_sha256": source["image_sha256"],
            "width": frame["width"], "height": frame["height"],
            "panel_id": event["panel_id"], "panel_result_file": file_name,
            "panel_result_sha256": event["sha256"], "candidate_count": len(centers),
            "source_to_panel_matrix": provenance["source_to_panel_matrix"],
            "panel_to_source_matrix": provenance["panel_to_source_matrix"],
        })
    if seen_files != set(panel_json_by_file):
        raise ValueError("unreferenced panel result files are present")
    return {
        "schema": "graphreader.marker-artifact-v5-train-input-binding.v1",
        "split": "train", "capture_report_sha256": report_sha256,
        "capture_binding_sha256": report["capture_binding_sha256"],
        "train_source_binding_sha256": TRAIN_SOURCE_BINDING_SHA256,
        "source_count": len(rows), "panel_count": len(rows),
        "truth_content_read": False, "truth_path_resolved": False,
        "dev_input_supplied": False, "private_input_supplied": False,
        "sealed_input_supplied": False, "training_approved": False,
        "sources": rows,
    }


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def prepare_full_capture_input_manifest():
    """Read the report and the exact 64 saved no-truth proposal JSON outputs."""
    from ml.synthetic.marker_context_train_corpus_v5.plan import fixed_plan
    report_raw = _read_project_file(CAPTURE_REPORT_REL)
    if _digest(report_raw) != CAPTURE_REPORT_SHA256:
        raise ValueError("bound full train capture report changed")
    report = json.loads(report_raw, object_pairs_hook=_strict_object)
    panel_paths = {}
    panel_root = _safe_project_directory(PANEL_RESULTS_REL)
    for item in panel_root.iterdir():
        info = item.lstat()
        if item.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("panel result directory contains a link or reparse point")
        if not item.is_file() or item.suffix != ".json":
            raise ValueError("panel result directory contains an unexpected entry")
        relative = (PANEL_RESULTS_REL / item.name).as_posix()
        panel_raw = _read_project_file(relative)
        panel_paths[item.name] = (json.loads(panel_raw, object_pairs_hook=_strict_object), _digest(panel_raw))
    seeds = [row["recipe"]["seed"] for row in fixed_plan()]
    bound = bind_report_metadata(report, panel_paths, report_sha256=CAPTURE_REPORT_SHA256,
                                 expected_seeds=seeds)
    bound["panel_result_directory"] = PANEL_RESULTS_REL.as_posix()
    return bound


def classify_artifact_proximity(*, candidates, marker_points, artifact_points, overlay_points,
                                source_width, source_height, capture_complete=True):
    """Apply the fixed inclusive 2px one-to-one rule, returning artifact outcomes only."""
    def point(raw):
        if isinstance(raw, dict):
            x, y = raw.get("x"), raw.get("y")
        else:
            x, y = raw
        if (type(x) not in (int, float) or type(y) not in (int, float) or
                not math.isfinite(x) or not math.isfinite(y) or
                not (0 <= x < source_width and 0 <= y < source_height)):
            raise ValueError("matching point is not finite and in frame")
        return float(x), float(y)

    candidate_points = [point(item["center"]) for item in candidates]
    positives = [point(item) for item in marker_points]
    artifacts = [point(item) for item in artifact_points]
    overlays = [point(item) for item in overlay_points]
    if len({item.get("candidate_id") for item in candidates}) != len(candidates):
        raise ValueError("candidate IDs must be unique")
    exact = Counter(candidate_points)
    eligible = []
    for index, item in enumerate(candidates):
        p = candidate_points[index]
        suppressed = (not capture_complete or item.get("detector_candidate") is not True or
                      item.get("symbol_input") is not False or exact[p] > 1 or
                      any(math.dist(p, overlay) <= MATCH_TOLERANCE_PX for overlay in overlays))
        if not suppressed:
            eligible.append(index)
    truth_points = positives + artifacts
    links_by_candidate, links_by_truth = {}, {}
    for candidate_index in eligible:
        for truth_index, truth in enumerate(truth_points):
            if math.dist(candidate_points[candidate_index], truth) <= MATCH_TOLERANCE_PX:
                links_by_candidate.setdefault(candidate_index, []).append(truth_index)
                links_by_truth.setdefault(truth_index, []).append(candidate_index)
    ambiguous_truths = set()
    for candidate_index, truth_indices in links_by_candidate.items():
        if len(truth_indices) > 1:
            ambiguous_truths.update(truth_indices)
        for truth_index in truth_indices:
            if len(links_by_truth.get(truth_index, [])) > 1:
                ambiguous_truths.add(truth_index)
    result = Counter()
    statuses = []
    offset = len(positives)
    for index in range(len(artifacts)):
        truth_index = offset + index
        if not capture_complete:
            status = "not_observed_capture_incomplete"
        elif any(math.dist(artifacts[index], overlay) <= MATCH_TOLERANCE_PX for overlay in overlays):
            status = "review_overlay_conflict"
        elif truth_index in ambiguous_truths:
            status = "review_ambiguous"
        else:
            linked = links_by_truth.get(truth_index, [])
            status = ("matched_artifact_review_only" if len(linked) == 1 and
                      links_by_candidate.get(linked[0]) == [truth_index]
                      else "unmatched_review_only")
        statuses.append(status)
        result[status] += 1
    return {"status_counts": dict(result), "glyph_statuses": statuses}


def analyze_bound_full_capture(input_manifest, *, expected_manifest_sha256,
                               source_runtime_manifest, expected_source_runtime_sha256,
                               result_reference):
    """Regenerate and compare the 64 bound train inputs; never read truth sidecars.

    This full analysis is deliberately a separate call from metadata preflight.
    A reviewer must pin the canonical input-manifest SHA before it is called.
    """
    canonical_manifest = _canonical(input_manifest)
    if _digest(canonical_manifest) != expected_manifest_sha256:
        raise ValueError("externally pinned train input manifest SHA-256 differs")
    source_runtime_sha = verify_source_runtime_manifest(
        input_manifest, source_runtime_manifest,
        expected_source_runtime_sha256=expected_source_runtime_sha256,
        result_reference=result_reference,
    )
    if (input_manifest.get("schema") != "graphreader.marker-artifact-v5-train-input-binding.v1" or
            input_manifest.get("split") != "train" or input_manifest.get("source_count") != 64 or
            input_manifest.get("panel_count") != 64 or input_manifest.get("truth_content_read") is not False or
            input_manifest.get("truth_path_resolved") is not False or
            input_manifest.get("dev_input_supplied") is not False or
            input_manifest.get("private_input_supplied") is not False or
            input_manifest.get("sealed_input_supplied") is not False or
            input_manifest.get("training_approved") is not False):
        raise ValueError("input manifest is not the fixed train-only no-truth binding")
    refreshed = prepare_full_capture_input_manifest()
    if _canonical(refreshed) != canonical_manifest:
        raise ValueError("capture report or saved panel metadata changed after manifest review")

    from .batch_producer import produce_v5_source_visibility
    from ml.synthetic.marker_context_train_corpus_v5.plan import fixed_plan
    if input_manifest.get("train_source_binding_sha256") != TRAIN_SOURCE_BINDING_SHA256:
        raise ValueError("V5 generator source binding differs")
    expected_seeds = {row["recipe"]["seed"] for row in fixed_plan()}
    source_rows = input_manifest.get("sources")
    if not isinstance(source_rows, list) or len(source_rows) != 64 or {row.get("seed") for row in source_rows} != expected_seeds:
        raise ValueError("input manifest source inventory differs from the fixed V5 train plan")

    source_results = []
    aggregate = Counter()
    for row in source_rows:
        identity = {key: row[key] for key in
                    ("seed", "source_id", "scene_id", "scene_sha256", "width", "height")}
        identity["image_sha256"] = row["source_raster_sha256"]
        try:
            panel_raw = _read_project_file(PANEL_RESULTS_REL / row["panel_result_file"])
            if _digest(panel_raw) != row["panel_result_sha256"]:
                raise ValueError("bound panel result changed")
            panel = json.loads(panel_raw, object_pairs_hook=_strict_object)
            if (panel.get("source_id") != row["source_id"] or panel.get("panel_id") != row["panel_id"] or
                    panel.get("panel_sha256") != row["source_raster_sha256"]):
                raise ValueError("panel result identity changed")
            candidates = []
            symbol_ids = set(panel["symbol_input_ids"]) | set(panel["symbol_crop_input_ids"])
            for candidate in panel["center_markers"]:
                candidates.append({
                    "candidate_id": candidate["marker_id"],
                    "center": [candidate["center"]["x"], candidate["center"]["y"]],
                    "detector_candidate": True,
                    "symbol_input": candidate["marker_id"] in symbol_ids,
                })
            rendered = produce_v5_source_visibility(seed=row["seed"], expected_source=identity)
            if rendered.get("source_identity") != identity:
                raise ValueError("producer output identity differs from the caller-bound source row")
            if rendered["source_raster_sha256"] != row["source_raster_sha256"]:
                raise ValueError("regenerated baseline raster differs from bound source hash")
            glyph_points = [glyph["center"] for glyph in rendered["glyph_results"]]
            comparison = classify_artifact_proximity(
                candidates=candidates,
                marker_points=rendered["positive_marker_points_for_suppression"],
                artifact_points=glyph_points,
                overlay_points=[rendered["rendered_overlay_center"]],
                source_width=row["width"], source_height=row["height"], capture_complete=True,
            )
            glyph_summaries = []
            for glyph, status in zip(rendered["glyph_results"], comparison["glyph_statuses"], strict=True):
                evidence = glyph["visibility_evidence"]
                glyph_summaries.append({
                    "glyph_id": glyph["glyph_id"],
                    "source_reference": glyph["source_reference"],
                    "center": glyph["center"], "glyph_box": glyph["glyph_box"],
                    "visible_changed_pixels": evidence["changed_pixel_count"],
                    "visibility_proof_sha256": evidence["proof_sha256"],
                    "rendered_rgb_sha256": evidence["rendered_rgb_sha256"],
                    "ablated_rgb_sha256": evidence["ablated_rgb_sha256"],
                    "association_status": status,
                })
            aggregate.update(comparison["status_counts"])
            source_results.append({
                **identity, "panel_id": row["panel_id"],
                "panel_result_sha256": row["panel_result_sha256"],
                "candidate_count": len(candidates), "status": "completed",
                "overlay_center": rendered["rendered_overlay_center"],
                "legend_glyph_count": len(glyph_summaries),
                "artifact_status_counts": comparison["status_counts"],
                "glyphs": glyph_summaries,
            })
        except Exception as error:
            source_results.append({**identity, "panel_id": row["panel_id"],
                                   "panel_result_sha256": row["panel_result_sha256"],
                                   "candidate_count": row["candidate_count"],
                                   "status": "failed", "failure_type": type(error).__name__,
                                   "legend_glyph_count": 0, "artifact_status_counts": {}})
    final_inputs = prepare_full_capture_input_manifest()
    if _canonical(final_inputs) != canonical_manifest:
        raise ValueError("capture report or panel metadata changed during full analysis")
    completed = sum(row["status"] == "completed" for row in source_results)
    failed = len(source_results) - completed
    return {
        "schema": "graphreader.marker-artifact-v5-train-visibility-audit.v1",
        "status": "completed" if failed == 0 else "completed_with_source_failures",
        "split": "train", "input_manifest_sha256": expected_manifest_sha256,
        "source_runtime_manifest_sha256": source_runtime_sha,
        "result_reference": _validate_result_reference(result_reference),
        "train_source_binding_sha256": TRAIN_SOURCE_BINDING_SHA256,
        "source_count": len(source_results), "source_accounting_complete": len(source_results) == 64,
        "all_sources_succeeded": failed == 0, "completed_source_count": completed,
        "failed_source_count": failed, "legend_glyph_count": sum(r["legend_glyph_count"] for r in source_results),
        "artifact_status_counts": dict(aggregate), "sources": source_results,
        "truth_content_read": False, "truth_path_resolved": False,
        "dev_input_supplied": False, "private_input_supplied": False,
        "sealed_input_supplied": False, "negative_training_rows": 0,
        "trainable_rows": 0, "optimizer_steps": 0, "training_approved": False,
        "limitations": [
            "Only the fixed synthetic V5 train capture is considered.",
            "Positive marker locations are generated in memory and used only to suppress ambiguous 2px collisions.",
            "Unmatched detector proposals are not classified as false positives or negatives.",
            "Visibility evidence is renderer ablation evidence, not production crop/mask parity or model validation.",
        ],
    }
