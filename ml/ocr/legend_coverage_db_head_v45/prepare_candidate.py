# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare the unapproved V45 development candidate after export parity."""
from __future__ import annotations

import argparse
from copy import deepcopy
import json
import math
from pathlib import Path
import shutil

from ml.markers.gate_seal import canonical_json_bytes, sha256_file, verify_bound_source_snapshot
from ml.ocr.legend_coverage_db_head_v45 import train_p1 as runner, verify_export


MODEL_ID = "graph-legend-coverage-db-head-v45-p1"
MODEL_NAME = "detector-legend-coverage-v45-p1.onnx"
PARENT_SHA256 = "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
RECOGNIZER_SHA256 = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"
PINNED_BASE_PATH = "artifacts/goal22-runs/ocr-text-extent-baseline-candidate-v2/candidate.json"
PINNED_BASE_SHA256 = "34f86cd6431b6cfdfc7ff10f85f5d7a859fc8e524dce557432afe93070efb361"
ASSEMBLY_NAMES = {
    "GraphReader.SyntheticRuntimeEvidence",
    "GraphReader.App",
    "GraphReader.Ocr",
    "GraphReader.Inference",
}


def _authenticate_template(root, descriptor):
    path = runner._bound(root, descriptor)
    base = runner._json(path)
    pinned_path = runner._bound(
        root, {"path": PINNED_BASE_PATH, "sha256": PINNED_BASE_SHA256}
    )
    pinned = runner._json(pinned_path)
    comparable = deepcopy(base)
    comparable["execution_assemblies"] = pinned.get("execution_assemblies")
    if canonical_json_bytes(comparable) != canonical_json_bytes(pinned):
        raise ValueError("Candidate template differs from the pinned base outside runtime assemblies")
    fixed = {
        "schema": "graphreader.frozen-db-head-ocr-candidate.v1",
        "scope": "project-owned-synthetic-train-dev-unapproved-frozen-candidate",
        "production_approved": False,
        "training_input_ready": False,
        "composition_version": "original-db-head-candidate-v1",
        "native_scope": "reviewed-source-runtime-local-diagnostic",
    }
    if any(type(base.get(key)) is not type(value) or base.get(key) != value for key, value in fixed.items()):
        raise ValueError("Candidate template scope or composition changed")
    if not isinstance(base.get("license_inputs"), list) or not base["license_inputs"]:
        raise ValueError("Candidate template license inventory is missing")
    assemblies = base.get("execution_assemblies")
    if not isinstance(assemblies, list) or len(assemblies) != 4:
        raise ValueError("Candidate template must bind exactly four runtime assemblies")
    observed_names: set[str] = set()
    runtime_directories: set[Path] = set()
    for item in assemblies:
        if (
            not isinstance(item, dict)
            or set(item) != {"name", "path", "sha256"}
            or item.get("name") not in ASSEMBLY_NAMES
            or item["name"] in observed_names
            or Path(item["path"]).name != item["name"] + ".dll"
        ):
            raise ValueError("Candidate template runtime assembly inventory changed")
        observed_names.add(item["name"])
        runtime_directories.add(Path(item["path"]).parent)
    if observed_names != ASSEMBLY_NAMES or len(runtime_directories) != 1:
        raise ValueError("Candidate template runtime assemblies must share one complete runtime directory")
    for item in base["license_inputs"] + assemblies:
        runner._bound(root, {"path": item["path"], "sha256": item["sha256"]})
    runner._bound(root, {"path": base["native_path"], "sha256": base["native_sha256"]})
    for role, expected_model in (("detector", PARENT_SHA256), ("recognizer", RECOGNIZER_SHA256)):
        row = base.get(role)
        if not isinstance(row, dict) or row.get("model_sha256") != expected_model:
            raise ValueError(f"Candidate template {role} identity changed")
        runner._bound(root, {"path": row["model_path"], "sha256": row["model_sha256"]})
        runner._bound(root, {"path": row["manifest_path"], "sha256": row["manifest_sha256"]})
    detector_manifest = runner._json(
        runner._bound(
            root,
            {
                "path": base["detector"]["manifest_path"],
                "sha256": base["detector"]["manifest_sha256"],
            },
        )
    )
    return base, detector_manifest


def prepare(stage_path, stage_sha, parity_path, parity_sha, output_path, root=runner.REPOSITORY_ROOT):
    root = Path(root).resolve()
    stage_path = runner._bound(root, {"path": str(stage_path), "sha256": stage_sha})
    parity_path = runner._bound(root, {"path": str(parity_path), "sha256": parity_sha})
    stage = runner._json(stage_path)
    parity = runner._json(parity_path)
    config = runner._config(root)
    authorization = stage["training_authorization"]
    config_sha256 = sha256_file(root / runner.CONFIG_PATH)
    expected_authorization = {
        "task": runner.TASK,
        "revision": runner.REVISION,
        "candidate_id": "P1",
        "candidate_config_path": runner.CONFIG_PATH.as_posix(),
        "candidate_config_sha256": config_sha256,
        "runner_source_bundle_sha256": config["expected_runner_source_bundle_sha256"],
        "runner_source_paths": sorted(path.as_posix() for path in runner.RUNNER_SOURCE_PATHS),
    }
    if not isinstance(authorization, dict) or any(
        authorization.get(key) != value for key, value in expected_authorization.items()
    ):
        raise ValueError("Training authorization identity changed")
    snapshot = (root / authorization["source_snapshot_path"]).resolve()
    if not snapshot.is_relative_to(root):
        raise ValueError("Snapshot escaped repository")
    verify_bound_source_snapshot(root, snapshot, authorization["source_snapshot_sha256"])
    verify_export.validate_training_result(stage["training"])
    fixed_stage = {
        "schema": verify_export.STAGE_SCHEMA,
        "status": "trained_pending_export_parity_and_dev",
        "task": runner.TASK,
        "revision": runner.REVISION,
        "candidate_id": "P1",
        "production_approved": False,
        "private_reads": 0,
        "sealed_reads": 0,
    }
    if any(type(stage.get(key)) is not type(value) or stage.get(key) != value for key, value in fixed_stage.items()):
        raise ValueError("Training is not an unapproved synthetic candidate")
    expected_parity = {
        "schema": verify_export.PARITY_SCHEMA,
        "stage": {"path": stage_path.relative_to(root).as_posix(), "sha256": stage_sha},
        "source_sha256": sha256_file(Path(verify_export.__file__)),
        "model_sha256": stage["model_sha256"],
        "checkpoint_sha256": stage["checkpoint_sha256"],
        "feature_inventory_sha256": stage["feature_inventory_sha256"],
        "panel_count": 43,
        "train_panel_count": 34,
        "development_panel_count": 9,
        "tolerance": verify_export.TOLERANCE,
        "torch_threads": 12,
        "onnx_threads": 1,
        "graph_optimization": "ORT_ENABLE_ALL",
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "passed": True,
    }
    if any(type(parity.get(key)) is not type(value) or parity.get(key) != value for key, value in expected_parity.items()):
        raise ValueError("Trained parity identity or scope changed")
    historical = runner._json(runner._bound(root, config["bound_files"]["capture_report"]))
    supplemental = runner._json(runner._bound(root, config["bound_files"]["supplemental_capture_report"]))
    capture_rows = historical.get("panels", []) + supplemental.get("panels", [])
    expected_panels = {
        row["panel_id"]: (row["split"], row["tensor"]["sha256"])
        for row in capture_rows
    }
    rows = parity.get("panels")
    if (
        len(capture_rows) != 43
        or len(expected_panels) != 43
        or not isinstance(rows, list)
        or len(rows) != 43
        or any(not isinstance(row, dict) for row in rows)
        or {row["panel_id"]: (row["split"], row["input_sha256"]) for row in rows} != expected_panels
    ):
        raise ValueError("Parity omitted, repeated, or changed a captured panel")
    errors = [row.get("maximum_absolute_error") for row in rows]
    if (
        any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in errors)
        or max(errors) > verify_export.TOLERANCE
        or type(parity.get("maximum_absolute_error")) not in (int, float)
        or parity["maximum_absolute_error"] != max(errors)
    ):
        raise ValueError("Trained numerical export parity failed")
    model = stage_path.parent / MODEL_NAME
    checkpoint = stage_path.parent / "selected-head.pt"
    if sha256_file(model) != stage["model_sha256"] or sha256_file(checkpoint) != stage["checkpoint_sha256"]:
        raise ValueError("Trained model or checkpoint changed")
    base, bound_manifest = _authenticate_template(
        root, config["bound_files"]["candidate_template"]
    )
    output = (root / output_path).resolve()
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise ValueError("Use a new candidate artifact directory")
    output.mkdir(parents=True)
    target = output / model.name
    shutil.copyfile(model, target)
    if sha256_file(target) != stage["model_sha256"]:
        raise ValueError("Model copy changed")
    manifest = deepcopy(bound_manifest)
    manifest.update(
        model_id=MODEL_ID,
        model_version="0.0.1",
        sha256=stage["model_sha256"],
        files=[target.name],
    )
    manifest["benchmarks"] = [
        {
            "scope": base["scope"],
            "production_approved": False,
            "accuracy_status": "pending-actual-application-evaluation",
            "training_stage_sha256": stage_sha,
            "trained_export_parity_sha256": parity_sha,
            "parent_model_sha256": PARENT_SHA256,
            "candidate_preparation_helper_sha256": sha256_file(Path(__file__)),
        }
    ]
    manifest_path = output / "detector.json"
    manifest_path.write_bytes(canonical_json_bytes(manifest))
    notice = output / "MODIFICATION-NOTICE.txt"
    notice.write_text(
        "Derived from the Apache-2.0 PaddleOCR PP-OCRv5 mobile detector.\n"
        "Copyright 2026 Sungwoo Kang. Nine DB head constants trained on project-owned synthetic train data.\n"
        "Frozen trunk and batch-normalization statistics remain unchanged. Not production approved.\n"
        "Original notices and license remain in LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt and\n"
        "LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt.\n",
        encoding="utf-8",
    )
    candidate = deepcopy(base)
    candidate["detector"] = {
        "model_path": target.relative_to(root).as_posix(),
        "model_id": MODEL_ID,
        "model_version": "0.0.1",
        "model_sha256": stage["model_sha256"],
        "manifest_path": manifest_path.relative_to(root).as_posix(),
        "manifest_sha256": sha256_file(manifest_path),
    }
    candidate["license_inputs"].append(
        {"path": notice.relative_to(root).as_posix(), "sha256": sha256_file(notice)}
    )
    candidate_path = output / "candidate.json"
    candidate_path.write_bytes(canonical_json_bytes(candidate))
    _authenticate_template(root, config["bound_files"]["candidate_template"])
    verify_bound_source_snapshot(root, snapshot, authorization["source_snapshot_sha256"])
    return {"path": candidate_path.relative_to(root).as_posix(), "sha256": sha256_file(candidate_path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("stage", "stage-sha", "parity", "parity-sha", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.stage, args.stage_sha, args.parity, args.parity_sha, args.output)))


if __name__ == "__main__":
    main()
