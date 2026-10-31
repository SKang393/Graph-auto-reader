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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--descriptor", required=True, help="project-relative runner descriptor JSON")
    parser.add_argument("--output", required=True, help="fresh project-relative goal22-runs child")
    parser.add_argument("--expected-runner-protocol-sha256", required=True)
    args = parser.parse_args()
    root = bridge.CANONICAL_PROJECT_ROOT.resolve(strict=True)
    descriptor_path = _resolve_input(root, args.descriptor)
    receipt = run_one_source(descriptor_bytes=descriptor_path.read_bytes(), project_root=root,
                             output_relative=args.output,
                             expected_runner_protocol_sha256=args.expected_runner_protocol_sha256)
    print(json.dumps(receipt, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
