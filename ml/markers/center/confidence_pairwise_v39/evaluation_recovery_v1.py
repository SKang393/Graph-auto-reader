# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Versioned eval-only recovery for V39's incomplete preflight result."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from ml.markers.gate_seal import sha256_file, source_bundle_sha256
from . import evaluation


PROJECT_ROOT = Path(__file__).resolve().parents[4]
BINDING_PATH = Path("artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval-recovery-v2/binding.json")
OUTPUT_PATH = Path("artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval-recovery-v2/output")
RECOVERY_TEST_PATH = Path("ml/markers/center/confidence_pairwise_v39/test_evaluation_recovery_v1.py")
RECOVERY_SOURCE_PATHS = (Path(__file__).resolve().relative_to(PROJECT_ROOT), RECOVERY_TEST_PATH)
INITIAL_FAILED_EVAL_LOG = Path("artifacts/goal22-runs/v39-evaluation-attempt-20261031-v1/evaluation.log")


def _read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("Recovery binding must be a JSON object")
    return value


def _full_preflight(project_root: Path) -> dict:
    """Retain original admission checks and return all validated data bindings."""
    summary = evaluation.preflight(project_root)
    config = evaluation._read_json(evaluation._safe_project_file(project_root, evaluation.CONFIG_PATH))
    protocol = evaluation._read_json(evaluation._safe_project_file(project_root, evaluation.PROTOCOL_PATH))
    complete = evaluation.validate_protocol(project_root, config, protocol)
    return {**complete, **summary}


def _validate_binding(project_root: Path, expected_binding_sha256: str,
                      expected_checkpoint_sha256: str,
                      expected_training_report_sha256: str) -> dict:
    binding_path = evaluation._safe_project_file(project_root, BINDING_PATH)
    binding_digest = sha256_file(binding_path)
    if binding_digest != evaluation._require_sha256(expected_binding_sha256, "V39 recovery binding"):
        raise ValueError("Recovery binding differs from its external SHA-256")
    binding = _read_json(binding_path)
    if binding.get("schema") != "graphreader.marker-center-confidence-pairwise-v39-eval-recovery-binding.v1":
        raise ValueError("Unsupported V39 recovery binding schema")
    report_path = evaluation._safe_project_file(
        project_root, evaluation.TRAIN_OUTPUT / evaluation.TRAINING_REPORT_NAME)
    actual_report_sha256 = sha256_file(report_path)
    if actual_report_sha256 != evaluation._require_sha256(expected_training_report_sha256, "V39 training report"):
        raise ValueError("V39 training report differs from its external recovery pin")
    training_report = _read_json(report_path)
    if sha256_file(report_path) != actual_report_sha256:
        raise ValueError("V39 training report changed while reading recovery metadata")
    expected = {
        "checkpoint_sha256": evaluation._require_sha256(expected_checkpoint_sha256, "V39 checkpoint"),
        "training_report_sha256": actual_report_sha256,
        "protocol_sha256": sha256_file(project_root / evaluation.PROTOCOL_PATH),
        "config_sha256": sha256_file(project_root / evaluation.CONFIG_PATH),
        "original_evaluator_sha256": sha256_file(project_root / evaluation.EVALUATOR_SOURCE_PATHS[1]),
        "shared_helper_sha256": sha256_file(project_root / Path("ml/markers/center/shape_coverage_evaluation.py")),
        "recovery_source_sha256": sha256_file(Path(__file__)),
        "recovery_test_sha256": sha256_file(project_root / RECOVERY_TEST_PATH),
        "initial_failed_eval_log_sha256": sha256_file(evaluation._safe_project_file(project_root, INITIAL_FAILED_EVAL_LOG)),
        "v27_initializer_sha256": evaluation.V27_CHECKPOINT_SHA256,
        "dev_file_sha256": evaluation.DEV_SHA256,
        "evaluator_source_bundle_sha256": _read_json(project_root / evaluation.PROTOCOL_PATH)["eval_only"]["source_bundle_sha256"],
        "training_source_bundle_sha256": training_report.get("runner_source_bundle_sha256"),
    }
    if binding.get("inputs") != expected:
        raise ValueError("Recovery input identities differ from the independently pinned run")
    if binding.get("recovery_source_paths") != [path.as_posix() for path in RECOVERY_SOURCE_PATHS]:
        raise ValueError("Recovery source path set changed")
    if binding.get("recovery_source_sha256") != {
        path.as_posix(): sha256_file(project_root / path) for path in RECOVERY_SOURCE_PATHS
    } or binding.get("recovery_source_bundle_sha256") != source_bundle_sha256(project_root, RECOVERY_SOURCE_PATHS):
        raise ValueError("Recovery source closure changed")
    if binding.get("output_path") != OUTPUT_PATH.as_posix():
        raise ValueError("Recovery output path changed")
    if binding.get("fixed_threshold") != evaluation.THRESHOLD or binding.get("matching_tolerance_px") != evaluation.MATCHING_TOLERANCE_PX:
        raise ValueError("Recovery operating point changed")
    return {"path": BINDING_PATH.as_posix(), "sha256": binding_digest, "binding": binding}


def preflight(project_root: Path = PROJECT_ROOT) -> dict:
    result = _full_preflight(project_root.resolve(strict=True))
    required = {"data_root", "hashes", "scene_counts", "scene_total", "truth_total", "split_identity"}
    if not required.issubset(result):
        raise ValueError("Full validated preflight omitted a required runtime binding")
    return result


def run(project_root: Path = PROJECT_ROOT, *, expected_binding_sha256: str,
        expected_checkpoint_sha256: str, expected_training_report_sha256: str) -> dict:
    project_root = project_root.resolve(strict=True)
    binding = _validate_binding(project_root, expected_binding_sha256,
                                expected_checkpoint_sha256, expected_training_report_sha256)
    full_preflight = _full_preflight(project_root)
    required = {"data_root", "hashes", "scene_counts", "scene_total", "truth_total", "split_identity"}
    if not required.issubset(full_preflight):
        raise ValueError("Recovery preflight omitted a validated runtime binding")
    output_path = evaluation.training_runner.validate_output_path(project_root, OUTPUT_PATH)
    if output_path.exists():
        raise FileExistsError(f"Fresh recovery output already exists: {OUTPUT_PATH.as_posix()}")

    original_preflight = evaluation.preflight
    evaluation.preflight = lambda _root=project_root: full_preflight
    try:
        report = evaluation.run(
            project_root,
            output=OUTPUT_PATH,
            expected_checkpoint_sha256=expected_checkpoint_sha256,
            expected_training_report_sha256=expected_training_report_sha256,
        )
    finally:
        evaluation.preflight = original_preflight

    report_path = output_path / "report.json"
    receipt = {
        "schema": "graphreader.marker-center-confidence-pairwise-v39-eval-recovery-receipt.v1",
        "status": "exploratory_dev_scored_no_independent_acceptance",
        "report_path": report_path.relative_to(project_root).as_posix(),
        "report_sha256": sha256_file(report_path),
        "binding_path": binding["path"],
        "binding_sha256": binding["sha256"],
        "recovery_source_path": Path(__file__).relative_to(project_root).as_posix(),
        "recovery_source_sha256": sha256_file(Path(__file__)),
        "original_evaluator_sha256": sha256_file(project_root / evaluation.EVALUATOR_SOURCE_PATHS[1]),
        "preflight_repair": "Merge unchanged preflight summary with the full validate_protocol result to supply data_root and input hashes already validated by the original evaluator.",
        "fixed_threshold": evaluation.THRESHOLD,
        "matching_tolerance_px": evaluation.MATCHING_TOLERANCE_PX,
        "device_provider": "CPUExecutionProvider",
        "per_scene_train_dev_overlap_verified": False,
        "private_reads": report["private_reads"], "sealed_reads": report["sealed_reads"],
        "optimizer_steps": report["optimizer_steps"],
        "production_approval": False, "release_eligible": False,
    }
    with (output_path / "recovery-receipt.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(receipt, stream, indent=2)
        stream.write("\n")
    return receipt


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binding-sha256", required=True)
    parser.add_argument("--checkpoint-sha256", required=True)
    parser.add_argument("--training-report-sha256", required=True)
    parser.add_argument("--preflight-only", action="store_true")
    args = parser.parse_args()
    if args.preflight_only:
        result = preflight()
        print(json.dumps({"status": "recovery_preflight_pass_no_inference", "scene_total": result["scene_total"],
                          "truth_total": result["truth_total"], "fixed_dev_sha256": result["hashes"]["dev_file"],
                          "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0}))
        return 0
    result = run(expected_binding_sha256=args.binding_sha256,
                 expected_checkpoint_sha256=args.checkpoint_sha256,
                 expected_training_report_sha256=args.training_report_sha256)
    print(json.dumps({"status": result["status"], "report_sha256": result["report_sha256"],
                      "output_path": result["report_path"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
