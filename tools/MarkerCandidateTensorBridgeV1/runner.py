# SPDX-License-Identifier: Apache-2.0
"""Exclusive writer for one authenticated, review-only synthetic train panel."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from typing import Any

from . import bridge


RUNNER_PROTOCOL = Path(__file__).with_name("runner_protocol.json")
INPUT_KEYS = {
    "panel_capture", "source_raster", "panel_raster", "complete_receipt",
    "identity_metadata", "capture_report", "truth_sidecar", "artifact_report",
}
FIRST_SOURCE_ID = "f9b0902a-1f41-7b53-bbef-6c3ff54bc94b"
FIRST_SOURCE_SEED = 20820000
BATCH_SOURCE_COUNT = 64
BATCH_INPUT_PATHS = {
    "complete_receipt": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json",
    "identity_metadata": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json",
    "capture_report": "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/report.json",
    "truth_sidecar": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/post-capture-truth/truth.json",
    "artifact_report": "artifacts/goal22-runs/marker-artifact-v5-train-full-analysis-v2/report.json",
}
BATCH_CAPTURE_PANEL_ROOT = "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/panels"
BATCH_RASTER_ROOT = "artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only"


def _hash(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _resolve_input(project_root: Path, value: Any) -> Path:
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError("input paths must be project-relative POSIX strings")
    relative = Path(value)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in value.split("/")):
        raise ValueError("input path is not a normalized project-relative path")
    path = project_root.joinpath(*value.split("/"))
    bridge._reject_reparse_ancestors(path, "runner input")
    if not path.is_file():
        raise ValueError("runner input is not a regular file")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(project_root):
        raise ValueError("runner input escapes the project root")
    return resolved


def preflight_descriptor_paths(*, descriptor_bytes: bytes, project_root: Path) -> dict[str, Any]:
    """Check the first-source descriptor's eight paths without opening inputs."""
    root = Path(project_root).resolve(strict=True)
    if root != bridge.CANONICAL_PROJECT_ROOT.resolve(strict=True):
        raise ValueError("runner preflight requires canonical E project root")
    bridge._reject_reparse_ancestors(root, "project root")
    descriptor = json.loads(descriptor_bytes, object_pairs_hook=bridge._strict_object)
    if (descriptor.get("schema") != "graphreader.marker-candidate-tensor-bridge-run.v1" or
            set(descriptor) != {"schema", "source", "inputs"} or
            not isinstance(descriptor.get("source"), dict) or
            not isinstance(descriptor.get("inputs"), dict) or set(descriptor["inputs"]) != INPUT_KEYS):
        raise ValueError("runner descriptor schema/fields differ")
    source = descriptor["source"]
    if (source.get("source_id") != FIRST_SOURCE_ID or source.get("source_group_id") != FIRST_SOURCE_ID or
            source.get("split") != "train"):
        raise ValueError("runner descriptor is not the fixed first synthetic train source")
    image_sha = source.get("source_raster_sha256")
    expected_paths = {
        "panel_capture": "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/panels/panel-0000.json",
        "source_raster": f"artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/{image_sha}.png",
        "panel_raster": f"artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/{image_sha}.png",
        "complete_receipt": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json",
        "identity_metadata": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json",
        "capture_report": "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/report.json",
        "truth_sidecar": "artifacts/goal22-runs/marker-context-train-corpus-v5-full/post-capture-truth/truth.json",
        "artifact_report": "artifacts/goal22-runs/marker-artifact-v5-train-full-analysis-v2/report.json",
    }
    if descriptor["inputs"] != expected_paths:
        raise ValueError("first-source descriptor input paths differ from the fixed synthetic inventory")
    records = {}
    for key, relative in expected_paths.items():
        path = _resolve_input(root, relative)
        records[key] = {"path": relative, "bytes": path.stat().st_size, "regular_file": True}
    return {"schema": "graphreader.marker-candidate-tensor-bridge-path-preflight.v1",
            "status": "paths_valid_contents_unopened", "source_id": FIRST_SOURCE_ID,
            "seed": FIRST_SOURCE_SEED, "input_count": len(records), "inputs": records,
            "annotation_content_read": False, "bridge_executed": False,
            "training_approved": False, "optimizer_steps": 0}


def _write_exclusive(path: Path, payload: bytes) -> None:
    with path.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def _verified_runner_context(*, root: Path, expected_runner_protocol_sha256: str) -> tuple[bytes, dict[str, Any]]:
    bridge._reject_reparse_ancestors(RUNNER_PROTOCOL, "runner protocol")
    protocol_bytes = RUNNER_PROTOCOL.read_bytes()
    if _hash(protocol_bytes) != expected_runner_protocol_sha256:
        raise ValueError("runner protocol hash differs from external pin")
    protocol = json.loads(protocol_bytes, object_pairs_hook=bridge._strict_object)
    if protocol.get("schema") != "graphreader.marker-candidate-tensor-bridge-runner.v1":
        raise ValueError("runner protocol schema differs")
    expected_source_closure = bridge._hash(bridge._canonical(bridge.SOURCE_BINDINGS))
    if (protocol.get("source_closure_sha256") != expected_source_closure or
            protocol.get("expected_inputs", {}).get("full_capture_report_sha256") != bridge.FULL_CAPTURE_REPORT_SHA256 or
            protocol.get("expected_inputs", {}).get("identity_metadata_sha256") != bridge.IDENTITY_METADATA_SHA256 or
            protocol.get("expected_inputs", {}).get("full_capture_complete_sha256") != bridge.FULL_CAPTURE_COMPLETE_SHA256 or
            protocol.get("expected_inputs", {}).get("full_capture_binding_sha256") != bridge.FULL_CAPTURE_BINDING_SHA256 or
            protocol.get("expected_inputs", {}).get("corpus_builder_binding_sha256") != bridge.CORPUS_BUILDER_BINDING_SHA256 or
            protocol.get("expected_inputs", {}).get("artifact_report_sha256") != bridge.ARTIFACT_REPORT_SHA256 or
            protocol.get("expected_inputs", {}).get("association_report_sha256") != bridge.ASSOCIATION_REPORT_SHA256):
        raise ValueError("runner protocol source or evidence pins differ")
    runner_path = Path(__file__).resolve(strict=True)
    bridge_path = Path(bridge.__file__).resolve(strict=True)
    package_init = runner_path.with_name("__init__.py")
    if (not runner_path.is_relative_to(root) or not bridge_path.is_relative_to(root) or
            not package_init.is_relative_to(root)):
        raise ValueError("runner package source paths are outside the canonical project root")
    for path, key in ((runner_path, "runner.py"), (bridge_path, "bridge.py"),
                      (package_init, "__init__.py")):
        bridge._reject_reparse_ancestors(path, "runner source")
        if _hash(path.read_bytes()) != protocol.get("source_bindings", {}).get(key):
            raise ValueError(f"runner source pin differs: {key}")
    return protocol_bytes, protocol


def _exclusive_run_directory(root: Path, output_relative: str) -> Path:
    run_root = root / "artifacts" / "goal22-runs"
    bridge._reject_reparse_ancestors(run_root, "output root")
    output_parts = output_relative.split("/") if isinstance(output_relative, str) else []
    if (not isinstance(output_relative, str) or "\\" in output_relative or len(output_parts) != 3 or
            output_parts[:2] != ["artifacts", "goal22-runs"] or
            output_parts[2] in ("", ".", "..")):
        raise ValueError("runner output must be one fresh child of artifacts/goal22-runs")
    output_path = root.joinpath(*output_parts)
    bridge._reject_reparse_ancestors(run_root, "output parent")
    if output_path.exists():
        raise FileExistsError("review output directory already exists")
    return output_path


def _parse_batch_descriptor(descriptor_bytes: bytes, source_count: int) -> dict[str, Any]:
    descriptor = json.loads(descriptor_bytes, object_pairs_hook=bridge._strict_object)
    if (not isinstance(descriptor, dict) or
            set(descriptor) != {"schema", "source_ids"} or
            descriptor.get("schema") != "graphreader.marker-candidate-tensor-bridge-batch.v1" or
            not isinstance(descriptor.get("source_ids"), list) or
            len(descriptor["source_ids"]) != source_count or
            any(not isinstance(value, str) or not value for value in descriptor["source_ids"]) or
            descriptor["source_ids"] != sorted(set(descriptor["source_ids"]))):
        raise ValueError("batch descriptor source inventory differs")
    return descriptor


def run_one_source(*, descriptor_bytes: bytes, project_root: Path,
                   output_relative: str, expected_runner_protocol_sha256: str) -> dict[str, Any]:
    """Run one bound source and write fresh result/receipt files exclusively.

    The descriptor contains one source object and project-relative input paths.
    This function performs no inference or training; every output row remains
    review-only and explicitly unadmitted.
    """
    root = Path(project_root).resolve(strict=True)
    if root != bridge.CANONICAL_PROJECT_ROOT.resolve(strict=True):
        raise ValueError("runner requires canonical E project root")
    bridge._reject_reparse_ancestors(root, "project root")
    bridge._reject_reparse_ancestors(RUNNER_PROTOCOL, "runner protocol")
    protocol_bytes = RUNNER_PROTOCOL.read_bytes()
    if _hash(protocol_bytes) != expected_runner_protocol_sha256:
        raise ValueError("runner protocol hash differs from external pin")
    protocol = json.loads(protocol_bytes, object_pairs_hook=bridge._strict_object)
    if protocol.get("schema") != "graphreader.marker-candidate-tensor-bridge-runner.v1":
        raise ValueError("runner protocol schema differs")
    expected_source_closure = bridge._hash(bridge._canonical(bridge.SOURCE_BINDINGS))
    if (protocol.get("source_closure_sha256") != expected_source_closure or
            protocol.get("expected_inputs", {}).get("full_capture_report_sha256") != bridge.FULL_CAPTURE_REPORT_SHA256 or
            protocol.get("expected_inputs", {}).get("identity_metadata_sha256") != bridge.IDENTITY_METADATA_SHA256 or
            protocol.get("expected_inputs", {}).get("full_capture_complete_sha256") != bridge.FULL_CAPTURE_COMPLETE_SHA256 or
            protocol.get("expected_inputs", {}).get("full_capture_binding_sha256") != bridge.FULL_CAPTURE_BINDING_SHA256 or
            protocol.get("expected_inputs", {}).get("corpus_builder_binding_sha256") != bridge.CORPUS_BUILDER_BINDING_SHA256 or
            protocol.get("expected_inputs", {}).get("artifact_report_sha256") != bridge.ARTIFACT_REPORT_SHA256 or
            protocol.get("expected_inputs", {}).get("association_report_sha256") != bridge.ASSOCIATION_REPORT_SHA256):
        raise ValueError("runner protocol source or evidence pins differ")
    runner_path = Path(__file__).resolve(strict=True)
    bridge_path = Path(bridge.__file__).resolve(strict=True)
    package_init = runner_path.with_name("__init__.py")
    if (not runner_path.is_relative_to(root) or not bridge_path.is_relative_to(root) or
            not package_init.is_relative_to(root)):
        raise ValueError("runner package source paths are outside the canonical project root")
    for path, key in ((runner_path, "runner.py"), (bridge_path, "bridge.py"),
                      (package_init, "__init__.py")):
        bridge._reject_reparse_ancestors(path, "runner source")
        if _hash(path.read_bytes()) != protocol.get("source_bindings", {}).get(key):
            raise ValueError(f"runner source pin differs: {key}")

    descriptor = json.loads(descriptor_bytes, object_pairs_hook=bridge._strict_object)
    if (descriptor.get("schema") != "graphreader.marker-candidate-tensor-bridge-run.v1" or
            set(descriptor) != {"schema", "source", "inputs"} or
            not isinstance(descriptor.get("source"), dict) or
            not isinstance(descriptor.get("inputs"), dict) or set(descriptor["inputs"]) != INPUT_KEYS):
        raise ValueError("runner descriptor schema/fields differ")
    paths = {key: _resolve_input(root, value) for key, value in descriptor["inputs"].items()}
    input_bytes = {key: path.read_bytes() for key, path in paths.items()}

    run_root = root / "artifacts" / "goal22-runs"
    bridge._reject_reparse_ancestors(run_root, "output root")
    output_parts = output_relative.split("/") if isinstance(output_relative, str) else []
    if (not isinstance(output_relative, str) or "\\" in output_relative or len(output_parts) != 3 or
            output_parts[:2] != ["artifacts", "goal22-runs"] or
            output_parts[2] in ("", ".", "..")):
        raise ValueError("runner output must be one fresh child of artifacts/goal22-runs")
    output_path = root.joinpath(*output_parts)
    bridge._reject_reparse_ancestors(run_root, "output parent")
    if output_path.exists():
        raise FileExistsError("review output directory already exists")

    result = bridge.materialize_bound_full_capture_panel(
        source=descriptor["source"], panel_capture_bytes=input_bytes["panel_capture"],
        source_raster_bytes=input_bytes["source_raster"], panel_raster_bytes=input_bytes["panel_raster"],
        complete_receipt_bytes=input_bytes["complete_receipt"],
        identity_metadata_bytes=input_bytes["identity_metadata"],
        capture_report_bytes=input_bytes["capture_report"],
        truth_sidecar_bytes=input_bytes["truth_sidecar"],
        artifact_report_bytes=input_bytes["artifact_report"], project_root=root)
    if result.get("training_approved") is not False or result.get("trainable_rows") != 0 or \
            result.get("negative_training_rows") != 0 or any(row.get("training_admitted") is not False
                                                               for row in result.get("rows", [])):
        raise ValueError("bridge result violates review-only training boundary")

    tensor_payloads = []
    tensor_references = {}
    for index, row in enumerate(result["rows"]):
        candidate_id = row["candidate_id"]
        saved = result["tensor_bytes_by_candidate"][candidate_id]
        stem = f"candidate-{index:04d}"
        refs = {}
        for view in ("local", "wide"):
            raw = saved[f"{view}_bytes"]
            sha = saved[f"{view}_sha256"]
            if _hash(raw) != sha:
                raise ValueError("tensor payload SHA differs from review row")
            name = f"{stem}.{view}.f32le"
            tensor_payloads.append((name, raw))
            refs[view] = {"path": f"tensors/{name}", "sha256": sha,
                          "bytes": len(raw), "encoding": "raw_float32_le"}
        tensor_references[candidate_id] = refs
    report = dict(result)
    report.pop("tensor_bytes_by_candidate")
    report["tensor_files"] = tensor_references
    result_bytes = bridge._canonical(report)
    output_path.mkdir(exist_ok=False)
    tensors_directory = output_path / "tensors"
    tensors_directory.mkdir(exist_ok=False)
    for filename, payload in tensor_payloads:
        _write_exclusive(tensors_directory / filename, payload)
    result_path = output_path / "review-rows.json"
    _write_exclusive(result_path, result_bytes)
    receipt = {
        "schema": "graphreader.marker-candidate-tensor-bridge-run-receipt.v1",
        "status": "completed_review_only", "source_id": descriptor["source"].get("source_id"),
        "source_group_id": descriptor["source"].get("source_group_id"),
        "grouping_basis": "source_id_only; participant/study independence is unverified",
        "input_sha256": {key: _hash(value) for key, value in input_bytes.items()},
        "descriptor_sha256": _hash(descriptor_bytes),
        "runner_protocol_sha256": _hash(protocol_bytes),
        "source_closure_sha256": result["source_closure_sha256"],
        "review_rows_sha256": _hash(result_bytes),
        "tensor_manifest_sha256": _hash(bridge._canonical(tensor_references)),
        "tensor_file_count": len(tensor_payloads), "candidate_count": result["candidate_count"],
        "trainable_rows": 0, "negative_training_rows": 0, "training_approved": False,
        "optimizer_steps": 0, "private_input_supplied": False, "sealed_input_supplied": False,
        "dev_input_supplied": False,
    }
    receipt_path = output_path / "receipt.json"
    _write_exclusive(receipt_path, bridge._canonical(receipt))
    return receipt


def _panel_source_binding(*, identity: dict[str, Any], captured: dict[str, Any],
                          event: dict[str, Any], panel: dict[str, Any],
                          derived: dict[str, Any], input_hashes: dict[str, str]) -> dict[str, Any]:
    provenance = panel.get("provenance")
    frame = panel.get("frame")
    if not isinstance(provenance, dict) or not isinstance(frame, dict):
        raise ValueError("panel provenance or frame is missing")
    width, height = provenance.get("source_width"), provenance.get("source_height")
    pixels = frame.get("channels_first_pixels")
    if (type(width) is not int or type(height) is not int or width <= 0 or height <= 0 or
            frame.get("width") != width or frame.get("height") != height or
            frame.get("channel_count") != 1 or not isinstance(pixels, list)):
        raise ValueError("panel frame geometry differs from full-source capture contract")
    frame_bytes = bridge.tensor_f32le(pixels, expected_count=width * height)
    captured_panels = captured.get("panels")
    if (captured.get("status") != "completed" or captured.get("failure") not in (None, "") or
            captured.get("panel_count") != 1 or not isinstance(captured_panels, list) or
            len(captured_panels) != 1 or captured_panels[0].get("status") != "completed"):
        raise ValueError("source does not have exactly one completed panel")
    if (captured_panels[0].get("panel_id") != panel.get("panel_id") or
            event.get("source_id") != identity.get("source_id") or
            event.get("panel_id") != panel.get("panel_id") or
            provenance.get("source_sha256") != identity.get("image_sha256") or
            provenance.get("panel_image_sha256") != identity.get("image_sha256")):
        raise ValueError("source, event, panel, and raster identity do not join")
    return {
        "source_id": identity["source_id"], "source_group_id": identity["source_id"],
        "split": "train", "scene_id": identity["scene_id"],
        "scene_sha256": identity["scene_sha256"],
        "annotation_sha256": derived["annotation_sha256"],
        "source_raster_sha256": identity["image_sha256"],
        "panel_id": panel["panel_id"], "panel_image_sha256": identity["image_sha256"],
        "panel_result_sha256": event["sha256"], "frame_f32le_sha256": _hash(frame_bytes),
        "width": width, "height": height,
        "capture_report_sha256": bridge.FULL_CAPTURE_REPORT_SHA256,
        "capture_complete_sha256": bridge.FULL_CAPTURE_COMPLETE_SHA256,
        "capture_binding_sha256": bridge.FULL_CAPTURE_BINDING_SHA256,
        "builder_binding_sha256": bridge.CORPUS_BUILDER_BINDING_SHA256,
        "identity_metadata_sha256": bridge.IDENTITY_METADATA_SHA256,
        "association_report_sha256": bridge.ASSOCIATION_REPORT_SHA256,
        "artifact_report_sha256": bridge.ARTIFACT_REPORT_SHA256,
        "capture_status": "completed",
        "panel_status_counts": {"completed": 1, "failed": 0, "not_processed": 0},
        "candidate_count": captured_panels[0].get("marker_count"),
    }


def run_train_batch(*, descriptor_bytes: bytes, project_root: Path,
                    output_relative: str, expected_runner_protocol_sha256: str) -> dict[str, Any]:
    """Materialize the frozen 64-source train inventory in one validated pass.

    Full truth/identity/capture/artifact validation is performed once before
    per-source tensor work. Source outputs are then written one at a time, so
    candidate tensors for the full corpus are never retained in memory.
    """
    root = Path(project_root).resolve(strict=True)
    if root != bridge.CANONICAL_PROJECT_ROOT.resolve(strict=True):
        raise ValueError("runner requires canonical E project root")
    bridge._reject_reparse_ancestors(root, "project root")
    protocol_bytes, protocol = _verified_runner_context(
        root=root, expected_runner_protocol_sha256=expected_runner_protocol_sha256)
    batch_spec = protocol.get("batch_execution")
    if batch_spec != {"source_count": BATCH_SOURCE_COUNT,
                      "validation_passes": 1,
                      "per_source_tensor_retention": "one_source_at_a_time"}:
        raise ValueError("batch execution protocol differs")

    descriptor = _parse_batch_descriptor(descriptor_bytes, BATCH_SOURCE_COUNT)

    paths = {key: _resolve_input(root, relative) for key, relative in BATCH_INPUT_PATHS.items()}
    input_bytes = {key: path.read_bytes() for key, path in paths.items()}
    input_hashes = {key: _hash(value) for key, value in input_bytes.items()}
    expected = protocol.get("expected_inputs", {})
    for key, protocol_key in (("complete_receipt", "full_capture_complete_sha256"),
                              ("identity_metadata", "identity_metadata_sha256"),
                              ("capture_report", "full_capture_report_sha256"),
                              ("artifact_report", "artifact_report_sha256")):
        if input_hashes[key] != expected.get(protocol_key):
            raise ValueError(f"batch input hash differs from runner protocol: {key}")

    source_closure = bridge.verify_source_bindings(root)
    source_closure_sha256 = _hash(bridge._canonical(source_closure))
    classifier = bridge._load_bound_classifier(root, source_closure)
    derived_by_source, inventory_rows = bridge._derive_bound_inventories(
        source=None, complete_receipt_bytes=input_bytes["complete_receipt"],
        identity_metadata_bytes=input_bytes["identity_metadata"],
        capture_report_bytes=input_bytes["capture_report"],
        truth_sidecar_bytes=input_bytes["truth_sidecar"],
        artifact_report_bytes=input_bytes["artifact_report"],
        classify_annotations=classifier)
    if (len(derived_by_source) != BATCH_SOURCE_COUNT or len(inventory_rows) != BATCH_SOURCE_COUNT or
            descriptor["source_ids"] != sorted(derived_by_source)):
        raise ValueError("batch descriptor differs from the exhaustive train inventory")

    identity_doc = json.loads(input_bytes["identity_metadata"], object_pairs_hook=bridge._strict_object)
    capture_doc = json.loads(input_bytes["capture_report"], object_pairs_hook=bridge._strict_object)
    identities = {row["source_id"]: row for row in identity_doc.get("sources", [])}
    captured = {row["source_id"]: row for row in capture_doc.get("sources", [])}
    events = {(row.get("source_id"), row.get("panel_id")): row for row in capture_doc.get("events", [])}
    if set(identities) != set(derived_by_source) or set(captured) != set(derived_by_source):
        raise ValueError("identity/capture map differs from validated train inventory")
    captured_candidate_counts = {}
    for source_id, capture_row in captured.items():
        panel_rows = capture_row.get("panels")
        if (capture_row.get("status") != "completed" or capture_row.get("panel_count") != 1 or
                not isinstance(panel_rows, list) or len(panel_rows) != 1 or
                panel_rows[0].get("status") != "completed" or
                type(panel_rows[0].get("marker_count")) is not int or panel_rows[0]["marker_count"] < 0):
            raise ValueError("capture source inventory failed full-batch preflight")
        captured_candidate_counts[source_id] = panel_rows[0]["marker_count"]
    captured_candidate_total = sum(captured_candidate_counts.values())

    output_path = _exclusive_run_directory(root, output_relative)
    output_path.mkdir(exist_ok=False)
    sources_directory = output_path / "sources"
    sources_directory.mkdir(exist_ok=False)
    source_results: list[dict[str, Any]] = []
    candidate_total = positive_total = legend_total = unknown_total = tensor_total = 0
    for ordinal, source_id in enumerate(descriptor["source_ids"]):
        source_dir_name = f"source-{ordinal:03d}"
        partial_dir_name = f".{source_dir_name}.partial"
        captured_candidate_count = captured_candidate_counts[source_id]
        try:
            identity = identities[source_id]
            capture_row = captured[source_id]
            panel_rows = capture_row.get("panels")
            if (capture_row.get("status") != "completed" or capture_row.get("panel_count") != 1 or
                    not isinstance(panel_rows, list) or len(panel_rows) != 1 or
                    panel_rows[0].get("status") != "completed"):
                raise ValueError("capture source status is not one completed panel")
            panel_summary = panel_rows[0]
            if panel_summary["marker_count"] != captured_candidate_count:
                raise ValueError("capture candidate count changed after preflight")
            event = events.get((source_id, panel_summary.get("panel_id")))
            if (not isinstance(event, dict) or not isinstance(event.get("file"), str) or
                    Path(event["file"]).name != event["file"] or
                    event["file"] != f"panel-{capture_row.get('source_index'):04d}.json"):
                raise ValueError("panel event file identity is invalid")
            panel_relative = f"{BATCH_CAPTURE_PANEL_ROOT}/{event['file']}"
            image_sha = identity.get("image_sha256")
            panel_path = _resolve_input(root, panel_relative)
            image_path = _resolve_input(root, f"{BATCH_RASTER_ROOT}/{image_sha}.png")
            panel_bytes = panel_path.read_bytes()
            panel = json.loads(panel_bytes, object_pairs_hook=bridge._strict_object)
            if _hash(panel_bytes) != event.get("sha256"):
                raise ValueError("panel event SHA differs from saved panel bytes")
            derived = derived_by_source[source_id]
            source = _panel_source_binding(identity=identity, captured=capture_row,
                event=event, panel=panel, derived=derived, input_hashes=input_hashes)
            raster_bytes = image_path.read_bytes()
            evidence = bridge._full_capture_candidate_evidence(panel, panel_bytes, source)
            result = bridge._materialize_source_core(source=source,
                panel_result_bytes=panel_bytes, source_raster_bytes=raster_bytes,
                panel_raster_bytes=raster_bytes, candidate_evidence=evidence,
                positive_truths=derived["positive_truths"],
                visible_glyphs=derived["visible_glyphs"],
                generic_overlays=derived["generic_overlays"])
            result["sampler_input_format"] = "full_capture_panel_inline_v1"
            result["source_closure_sha256"] = source_closure_sha256
            result["grouping_basis"] = "source_id_only; participant/study independence is unverified"
            result["truth_inventory_binding"] = {
                "schema": "graphreader.marker-truth-inventory-binding.v1",
                "full_inventory_sha256": _hash(bridge._canonical(inventory_rows)),
                "source_count": len(inventory_rows), "source_id": source_id,
                "annotation_sha256": derived["annotation_sha256"],
                "positive_truth_count": len(derived["positive_truths"]),
                "visible_glyph_count": len(derived["visible_glyphs"]),
                "overlay_count": len(derived["generic_overlays"]),
            }
            result["limitations"].append(
                "Sampler tensors are read inline from hash-bound panel bytes; adapter-generated evidence envelopes are not independent capture files.")
            if (result.get("training_approved") is not False or result.get("trainable_rows") != 0 or
                    result.get("negative_training_rows") != 0 or
                    any(row.get("training_admitted") is not False for row in result.get("rows", []))):
                raise ValueError("bridge result violates review-only training boundary")
            tensor_refs: dict[str, Any] = {}
            tensor_payloads: list[tuple[str, bytes]] = []
            for candidate_index, row in enumerate(result["rows"]):
                candidate_id = row["candidate_id"]
                saved = result["tensor_bytes_by_candidate"][candidate_id]
                references = {}
                for view in ("local", "wide"):
                    raw = saved[f"{view}_bytes"]
                    if _hash(raw) != saved[f"{view}_sha256"]:
                        raise ValueError("tensor payload SHA differs from review row")
                    name = f"candidate-{candidate_index:04d}.{view}.f32le"
                    tensor_payloads.append((name, raw))
                    references[view] = {"path": f"tensors/{name}",
                        "sha256": saved[f"{view}_sha256"], "bytes": len(raw),
                        "encoding": "raw_float32_le"}
                tensor_refs[candidate_id] = references
            report = dict(result)
            report.pop("tensor_bytes_by_candidate")
            report["tensor_files"] = tensor_refs
            report_bytes = bridge._canonical(report)
            source_directory = sources_directory / source_dir_name
            partial_directory = sources_directory / partial_dir_name
            partial_directory.mkdir(exist_ok=False)
            tensor_directory = partial_directory / "tensors"
            tensor_directory.mkdir(exist_ok=False)
            for filename, payload in tensor_payloads:
                _write_exclusive(tensor_directory / filename, payload)
            _write_exclusive(partial_directory / "review-rows.json", report_bytes)
            source_receipt = {"source_id": source_id, "status": "completed_review_only",
                "panel_id": source["panel_id"], "panel_result_sha256": source["panel_result_sha256"],
                "source_raster_sha256": source["source_raster_sha256"],
                "review_rows_sha256": _hash(report_bytes),
                "tensor_manifest_sha256": _hash(bridge._canonical(tensor_refs)),
                "tensor_file_count": len(tensor_payloads), "candidate_count": result["candidate_count"],
                "positive_review_only_rows": result["matched_positive_review_rows"],
                "legend_review_only_rows": result["visible_legend_review_rows"],
                "unknown_or_excluded_rows": result["unknown_or_excluded_rows"],
                "trainable_rows": 0, "negative_training_rows": 0,
                "training_approved": False, "optimizer_steps": 0}
            _write_exclusive(partial_directory / "receipt.json", bridge._canonical(source_receipt))
            partial_directory.rename(source_directory)
            source_results.append({"source_index": ordinal, **source_receipt,
                "source_output": f"sources/{source_dir_name}",
                "captured_candidate_count": captured_candidate_count,
                "failure_code": None})
            candidate_total += result["candidate_count"]
            positive_total += result["matched_positive_review_rows"]
            legend_total += result["visible_legend_review_rows"]
            unknown_total += result["unknown_or_excluded_rows"]
            tensor_total += len(tensor_payloads)
        except Exception as exc:
            source_results.append({"source_index": ordinal, "source_id": source_id,
                "status": "failed", "failure_code": type(exc).__name__,
                "captured_candidate_count": captured_candidate_count,
                "partial_output": f"sources/{partial_dir_name}" if (sources_directory / partial_dir_name).exists() else None,
                "candidate_count": 0, "trainable_rows": 0, "negative_training_rows": 0,
                "training_approved": False, "optimizer_steps": 0})

    counts = {"completed": sum(row["status"] == "completed_review_only" for row in source_results),
              "failed": sum(row["status"] == "failed" for row in source_results)}
    status = "completed_review_only" if counts["failed"] == 0 else "completed_with_source_failures_review_only"
    report = {"schema": "graphreader.marker-candidate-tensor-bridge-batch-report.v1",
        "status": status, "source_count": len(source_results), "source_status_counts": counts,
        "sources": source_results,
        "inventory_sha256": _hash(bridge._canonical(inventory_rows)),
        "input_sha256": input_hashes, "descriptor_sha256": _hash(descriptor_bytes),
        "runner_protocol_sha256": _hash(protocol_bytes),
        "source_closure_sha256": source_closure_sha256,
        "candidate_count": candidate_total, "positive_review_only_rows": positive_total,
        "captured_candidate_count": captured_candidate_total,
        "legend_review_only_rows": legend_total, "unknown_or_excluded_rows": unknown_total,
        "tensor_file_count": tensor_total, "trainable_rows": 0, "negative_training_rows": 0,
        "training_approved": False, "optimizer_steps": 0,
        "private_input_supplied": False, "sealed_input_supplied": False,
        "dev_input_supplied": False,
        "grouping_basis": "source_id_only; participant/study independence is unverified",
        "limitations": ["Review-only candidate labels; no row is admitted to a training corpus.",
                        "Failures are accounted per source; no source is silently dropped.",
                        "Tensor files are raw pre-normalization sampler outputs; classifier normalization is not applied or verified.",
                        "Production model-store factory selection and runtime parity are not verified by this materializer."]}
    report_bytes = bridge._canonical(report)
    _write_exclusive(output_path / "batch-report.json", report_bytes)
    receipt = {"schema": "graphreader.marker-candidate-tensor-bridge-batch-receipt.v1",
        "status": status, "source_count": BATCH_SOURCE_COUNT, "source_status_counts": counts,
        "batch_report_sha256": _hash(report_bytes),
        "input_sha256": input_hashes, "descriptor_sha256": _hash(descriptor_bytes),
        "runner_protocol_sha256": _hash(protocol_bytes),
        "source_closure_sha256": source_closure_sha256,
        "candidate_count": candidate_total, "tensor_file_count": tensor_total,
        "trainable_rows": 0, "negative_training_rows": 0,
        "training_approved": False, "optimizer_steps": 0,
        "private_input_supplied": False, "sealed_input_supplied": False,
        "dev_input_supplied": False,
        "limitations": ["Review-only materialization; no training admission.",
                        "Classifier normalization and production model-store factory/runtime parity are not verified."]}
    _write_exclusive(output_path / "receipt.json", bridge._canonical(receipt))
    return receipt


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("one-source", "train-batch"), default="one-source")
    parser.add_argument("--descriptor", required=True, help="project-relative runner descriptor JSON")
    parser.add_argument("--output", required=True, help="fresh project-relative goal22-runs child")
    parser.add_argument("--expected-runner-protocol-sha256", required=True)
    args = parser.parse_args()
    root = bridge.CANONICAL_PROJECT_ROOT.resolve(strict=True)
    descriptor_path = _resolve_input(root, args.descriptor)
    if args.mode == "train-batch":
        receipt = run_train_batch(descriptor_bytes=descriptor_path.read_bytes(), project_root=root,
            output_relative=args.output,
            expected_runner_protocol_sha256=args.expected_runner_protocol_sha256)
    else:
        receipt = run_one_source(descriptor_bytes=descriptor_path.read_bytes(), project_root=root,
            output_relative=args.output,
            expected_runner_protocol_sha256=args.expected_runner_protocol_sha256)
    print(json.dumps(receipt, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
