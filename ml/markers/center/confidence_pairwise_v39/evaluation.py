# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Eval-only V39 runner over the fixed, complete V37 development scenes."""
from __future__ import annotations

import argparse
from contextlib import nullcontext
import json
import math
import os
from pathlib import Path
import stat
import time

import numpy as np
import onnx
import onnxruntime as ort
import torch

from ml.markers.gate_seal import sha256_file, source_bundle_sha256, verify_bound_source_snapshot
from ml.markers.center.confidence_bce_v33.runner import validate_export_parity
from ml.markers.center.confidence_reflection_v37 import runner as v37_runner
from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet
from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ml.markers.center.shape_coverage_cache import unpack_scene, write_json
from ml.markers.center import shape_coverage_evaluation as evaluation
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from . import runner as training_runner


PROJECT_ROOT = Path(__file__).resolve().parents[4]
TASK = "marker-center"
REVISION = "marker-center-confidence-pairwise-v39"
CANDIDATE_ID = "P1"
CONFIG_PATH = Path("ml/markers/center/confidence_pairwise_v39/p1.json")
PROTOCOL_PATH = Path("ml/markers/center/confidence_pairwise_v39/protocol.json")
TRAIN_OUTPUT = Path("artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1")
CHECKPOINT_NAME = "marker-center-pairwise.pt"
TRAINING_REPORT_NAME = "training-report.json"
OUTPUT_PATH = Path("artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval")
THRESHOLD = 0.1
MATCHING_TOLERANCE_PX = 5.0
DEV_SCOPES = {"component": "dev", "family": "validation"}
DEV_COUNTS = {"component": (167, 2004), "family": (9, 206)}
DEV_TOTALS = (176, 2210)

# This closure records the current evaluator and its imported metric, model,
# cache, historical-baseline and CPU-runtime dependencies. The training runner
# separately binds this file through RUNNER_SOURCES and the fixed P1 config.
EVALUATOR_SOURCE_PATHS = tuple(Path(value) for value in (
    "ml/markers/center/confidence_pairwise_v39/__init__.py",
    "ml/markers/center/confidence_pairwise_v39/evaluation.py",
    "ml/markers/center/confidence_pairwise_v39/test_evaluation.py",
    "ml/markers/gate_seal.py",
    "ml/markers/center/confidence_bce_v33/runner.py",
    "ml/markers/center/confidence_reflection_v37/runner.py",
    "ml/markers/center/confidence_reflection_v37/cache.py",
    "ml/markers/center/confidence_spatial_v35/model.py",
    "ml/markers/center/scale_classifier_v16/model.py",
    "ml/markers/center/shape_coverage_cache.py",
    "ml/markers/center/shape_coverage_evaluation.py",
    "ml/markers/center/balanced_support_diagnostic.py",
    "ml/markers/center/component_diversity_v27/diagnose_head_swap.py",
    "ml/markers/center/component_diversity_v27/diagnose_score_localization.py",
    "ml/markers/center/component_diversity_v27/train_p1.py",
    "ml/markers/center/line_aware_v1/pipeline.py",
    "ml/markers/center/mask_preserving_v24/mask_preserving.py",
    "ml/markers/center/mask_preserving_v24/train_p1.py",
    "ml/markers/center/plot_domain_v25/diagnose_dev.py",
    "ml/markers/center/plot_domain_v25/proposal_domain.py",
    "ml/markers/center/localization_confidence_v26/diagnose_suppressor_anchors.py",
    "ml/markers/center/shape_coverage_v28/runner.py",
    "ml/markers/classifier/native_context_v3/runner.py",
    "ml/markers/center/mask_preserving_v24/training/p1_retry13.json",
))

V37_COMPATIBILITY_PATHS = tuple(Path(value) for value in (
    "ml/markers/center/confidence_reflection_v37/runner.py",
    "ml/markers/center/confidence_reflection_v37/cache.py",
    "ml/markers/center/confidence_spatial_v35/model.py",
    "ml/markers/center/scale_classifier_v16/model.py",
    "ml/markers/center/shape_coverage_cache.py",
    "ml/markers/center/balanced_support_diagnostic.py",
    "ml/markers/center/component_diversity_v27/diagnose_head_swap.py",
    "ml/markers/center/component_diversity_v27/diagnose_score_localization.py",
    "ml/markers/center/line_aware_v1/pipeline.py",
    "ml/markers/center/mask_preserving_v24/mask_preserving.py",
    "ml/markers/center/mask_preserving_v24/train_p1.py",
    "ml/markers/center/plot_domain_v25/diagnose_dev.py",
    "ml/markers/center/plot_domain_v25/proposal_domain.py",
    "ml/markers/center/localization_confidence_v26/diagnose_suppressor_anchors.py",
    "ml/markers/center/shape_coverage_v28/runner.py",
))

DATA_ROOT = Path("artifacts/goal22-worktrees/resume-20260908")
DEV_FILE = Path("artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt")
DEV_SHA256 = "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41"
TRAIN_FEATURE_REPORT = Path("artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json")
TRAIN_FEATURE_SHA256 = "284ebfa305b7b827c59383711122b1668398460ddde2489007252335ee55f500"
PARENT_FEATURE_SHA256 = "3a98504c7b31f16af8f4b127ab438cba3fe7918d51c9417021221f96bb6ad7cc"
V27_CHECKPOINT_SHA256 = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
TRAIN_SPLIT_CONFIG = Path("ml/markers/center/mask_preserving_v24/training/p1_retry13.json")
HISTORICAL_PREPARATION = DATA_ROOT / "artifacts/goal22-runs/center-shape-coverage-cache-v3/historical-preparation.json"
DEV_INVENTORY = DATA_ROOT / "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev-inventory.json"
CACHE_MANIFEST = DATA_ROOT / "artifacts/goal22-runs/center-shape-coverage-cache-v3/manifest.json"
V37_REPORT = DATA_ROOT / "artifacts/goal22-runs/center-confidence-reflection-v37/P1/report.json"
EXPECTED_V37_RUNNER_BUNDLE = "e3f2ea3f1affd16a7190233388c60990982da9035859c83c832269966658bcca"


class _NoopBudget:
    def work_block(self):
        return nullcontext()


def _read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object at {path}")
    return value


def _safe_project_file(root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Bound project path must be relative and traversal-free")
    root = root.resolve(strict=True)
    path = root / relative
    current = root
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for part in relative.parts:
        current = current / part
        if current.exists() and getattr(current.stat(), "st_file_attributes", 0) & reparse_flag:
            raise ValueError(f"Reparse-point evidence path is forbidden: {relative}")
    resolved = path.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise ValueError("Evidence path resolved outside its project root") from error
    if not resolved.is_file():
        raise ValueError("Evidence reference is not a regular file")
    return resolved


def _data_file(project_root: Path, relative: Path) -> Path:
    data_root = _safe_project_directory(project_root, DATA_ROOT)
    return _safe_project_file(data_root, relative)


def _safe_project_directory(project_root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Bound data root must be relative and traversal-free")
    root = project_root.resolve(strict=True)
    path = root / relative
    current = root
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for part in relative.parts:
        current = current / part
        if current.exists() and getattr(current.stat(), "st_file_attributes", 0) & reparse_flag:
            raise ValueError("Reparse-point data root is forbidden")
    resolved = path.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise ValueError("Data root escaped the project") from error
    if not resolved.is_dir():
        raise ValueError("Bound data root is not a directory")
    return resolved


def validate_protocol(project_root: Path, config: dict, protocol: dict) -> dict:
    training_runner.validate_config(project_root, config)
    if protocol.get("schema") != "graphreader.marker-center-confidence-pairwise-protocol.v1":
        raise ValueError("V39 protocol schema changed")
    if (protocol.get("task"), protocol.get("revision"), protocol.get("candidate_id")) != (TASK, REVISION, CANDIDATE_ID):
        raise ValueError("V39 protocol candidate identity changed")
    dev = protocol.get("development", {})
    if (dev.get("path") != DEV_FILE.as_posix() or dev.get("sha256") != DEV_SHA256 or
            dev.get("threshold") != THRESHOLD or dev.get("matching_tolerance_px") != MATCHING_TOLERANCE_PX):
        raise ValueError("Frozen development split or operating point changed")
    if dev.get("required_scopes") != {scope: list(counts) for scope, counts in DEV_COUNTS.items()} or \
            dev.get("truth_denominator") != dict(zip(("scenes", "truths"), DEV_TOTALS, strict=True)):
        raise ValueError("Development split denominator changed")
    if protocol.get("train_dev_separation", {}).get("per_scene_overlap_verified") is not False:
        raise ValueError("Do not overstate per-scene train/dev separation")
    if config.get("development_file") != {"path": DEV_FILE.as_posix(), "sha256": DEV_SHA256}:
        raise ValueError("P1 config omitted or changed the fixed development input")
    if config.get("training_output_path") != TRAIN_OUTPUT.as_posix():
        raise ValueError("P1 config omitted or changed the fixed V39 output location")
    if config.get("development_evaluation_output_path") != OUTPUT_PATH.as_posix():
        raise ValueError("P1 config omitted or changed the eval-only output location")
    evaluator = protocol.get("eval_only", {})
    if evaluator.get("evaluation_thresholds") != [THRESHOLD]:
        raise ValueError("Registered evaluator must use only the fixed 0.1 threshold")
    if protocol.get("execution", {}).get("memory_preflight") != {
        "minimum_available_physical_bytes": training_runner.MIN_AVAILABLE_MEMORY_BYTES,
        "position": "before canonical ledger admission",
        "reason": "V37-compatible cache loading temporarily holds source arrays and their concatenated feature array; refuse before the single-use admission if the desktop lacks conservative headroom.",
    }:
        raise ValueError("Pre-admission memory guard changed")
    if evaluator.get("source_paths") != [path.as_posix() for path in EVALUATOR_SOURCE_PATHS]:
        raise ValueError("Eval-only source path set changed")
    source_hashes = {path.as_posix(): sha256_file(project_root / path) for path in EVALUATOR_SOURCE_PATHS}
    if evaluator.get("source_sha256") != source_hashes:
        raise ValueError("Eval-only source hashes changed")
    if evaluator.get("source_bundle_sha256") != source_bundle_sha256(project_root, EVALUATOR_SOURCE_PATHS):
        raise ValueError("Eval-only source bundle changed")
    frozen = evaluator.get("v37_compatibility_sha256")
    if frozen != {path.as_posix(): sha256_file(project_root / path) for path in V37_COMPATIBILITY_PATHS}:
        raise ValueError("V37-compatible evaluation/preprocessing sources changed")
    data_root = _safe_project_directory(project_root, DATA_ROOT)
    data_paths = {
        "feature_report": _data_file(project_root, TRAIN_FEATURE_REPORT),
        "dev_file": _data_file(project_root, DEV_FILE),
        "training_split_config": _safe_project_file(project_root, TRAIN_SPLIT_CONFIG),
        "historical_preparation": _safe_project_file(data_root, HISTORICAL_PREPARATION.relative_to(DATA_ROOT)),
        "dev_inventory": _safe_project_file(data_root, DEV_INVENTORY.relative_to(DATA_ROOT)),
        "cache_manifest": _safe_project_file(data_root, CACHE_MANIFEST.relative_to(DATA_ROOT)),
        "v37_report": _safe_project_file(data_root, V37_REPORT.relative_to(DATA_ROOT)),
    }
    expected_data_hashes = evaluator.get("input_sha256")
    actual_data_hashes = {name: sha256_file(path) for name, path in data_paths.items()}
    if expected_data_hashes != actual_data_hashes:
        raise ValueError("Fixed V37 train/dev input hashes changed")
    if sha256_file(data_paths["feature_report"]) != TRAIN_FEATURE_SHA256:
        raise ValueError("V37 synthetic train feature report changed")
    train_report = _read_json(data_paths["feature_report"])
    if (train_report.get("status") != "fixed_reflection_training_features_measured" or
            train_report.get("development_tensor_reads") != 0 or train_report.get("private_reads") != 0 or
            train_report.get("sealed_reads") != 0 or train_report.get("parent_feature_report", {}).get("sha256") != PARENT_FEATURE_SHA256):
        raise ValueError("Training feature report does not identify the pinned train-only population")
    groups = train_report.get("groups", [])
    if [group.get("scope") for group in groups] != list(training_runner.TRAINING_ROWS) or \
            {group.get("scope"): group.get("rows") for group in groups} != training_runner.TRAINING_ROWS:
        raise ValueError("Train feature group identities/counts differ from the fixed recipe")
    split_config = _read_json(data_paths["training_split_config"])
    split_identity = {
        "component_train_split": split_config.get("train_split_sha256"),
        "component_dev_split": split_config.get("dev_split_sha256"),
        "family_train_split": split_config.get("family_train_split_sha256"),
        "family_dev_split": split_config.get("family_dev_split_sha256"),
        "configured_family_train_tensor_set": split_config.get("family_train_tensor_set_sha256"),
        "configured_family_dev_tensor_set": split_config.get("family_dev_tensor_set_sha256"),
    }
    expected_split_identity = protocol["train_dev_separation"]["split_sha256"]
    if split_identity != expected_split_identity:
        raise ValueError("Frozen train/dev split identities changed")
    if split_config.get("train_split") != "real-range-generator-v1-train" or split_config.get("dev_split") != "real-range-generator-v1-dev":
        raise ValueError("Train/dev split labels changed")
    preparation = _read_json(data_paths["historical_preparation"])
    if preparation.get("private_reads") != 0 or preparation.get("sealed_runs") != 0 or preparation.get("optimizer_steps_run") != 0:
        raise ValueError("Historical preparation scope changed")
    if preparation.get("component_recipe", {}).get("train_split_sha256") != split_identity["component_train_split"]:
        raise ValueError("Historical component train split differs from its config")
    if preparation.get("component_recipe", {}).get("dev_split_sha256") != split_identity["component_dev_split"]:
        raise ValueError("Historical component dev split differs from its config")
    family_training = preparation.get("family_binding", {})
    if (family_training.get("train_tensor_set_sha256") != protocol["train_dev_separation"]["historical_family_train_tensor_set_sha256"] or
            family_training.get("dev_tensor_set_sha256") != protocol["train_dev_separation"]["historical_family_dev_tensor_set_sha256"]):
        raise ValueError("Historical family train/dev tensor-set identities changed")
    if family_training.get("train_tensor_set_sha256") == family_training.get("dev_tensor_set_sha256"):
        raise ValueError("Historical family train/dev tensor sets are not distinct")
    dev_manifest = _read_json(data_paths["cache_manifest"])
    dev_inventory = _read_json(data_paths["dev_inventory"])
    if dev_manifest.get("dev") != {"component_scenes": 167, "family_scenes": 9, "truths": 2210,
                                   "inventory_sha256": actual_data_hashes["dev_inventory"]}:
        raise ValueError("Dev inventory identity or denominator changed")
    if set(dev_inventory) != set(DEV_COUNTS):
        raise ValueError("Dev inventory scope changed")
    for scope, records in dev_inventory.items():
        if len(records) != DEV_COUNTS[scope][0]:
            raise ValueError("Dev scene count changed")
        expected_split = DEV_SCOPES[scope]
        if any(record.get("metadata", {}).get("split") != expected_split for record in records):
            raise ValueError("Dev inventory contains a row with the wrong split label")
        if sum(len(record.get("metadata", {}).get("centers", [])) for record in records) != DEV_COUNTS[scope][1]:
            raise ValueError("Dev truth denominator changed")
    v37_report = _read_json(data_paths["v37_report"])
    if (v37_report.get("binding", {}).get("runner_source_bundle_sha256") != EXPECTED_V37_RUNNER_BUNDLE or
            v37_report.get("binding", {}).get("source_binding_mode") != "immutable_pre_run_snapshot" or
            source_bundle_sha256(data_root, v37_runner.RUNNER_SOURCES) != EXPECTED_V37_RUNNER_BUNDLE):
        raise ValueError("V37 historical source binding changed")
    for path in V37_COMPATIBILITY_PATHS:
        if sha256_file(project_root / path) != sha256_file(data_root / path):
            raise ValueError(f"Current evaluator differs from V37-pinned source: {path.as_posix()}")
    return {
        "project_root": project_root.resolve(), "data_root": data_root,
        "paths": data_paths, "hashes": actual_data_hashes,
        "scene_counts": DEV_COUNTS, "scene_total": DEV_TOTALS[0], "truth_total": DEV_TOTALS[1],
        "per_scene_overlap_verified": False,
        "split_identity": split_identity,
    }


def preflight(project_root: Path = PROJECT_ROOT) -> dict:
    project_root = project_root.resolve(strict=True)
    config = _read_json(_safe_project_file(project_root, CONFIG_PATH))
    protocol = _read_json(_safe_project_file(project_root, PROTOCOL_PATH))
    binding = validate_protocol(project_root, config, protocol)
    candidate_directory = project_root / TRAIN_OUTPUT
    if candidate_directory.exists():
        _safe_project_directory(project_root, TRAIN_OUTPUT)
    checkpoint_path = candidate_directory / CHECKPOINT_NAME
    training_report_path = candidate_directory / TRAINING_REPORT_NAME
    checkpoint_present = checkpoint_path.is_file()
    report_present = training_report_path.is_file()
    if checkpoint_present != report_present:
        raise ValueError("Completed V39 checkpoint/report pair is incomplete")
    if checkpoint_present:
        _safe_project_file(project_root, TRAIN_OUTPUT / CHECKPOINT_NAME)
        _safe_project_file(project_root, TRAIN_OUTPUT / TRAINING_REPORT_NAME)
    return {
        "status": "eval_preflight_pass_no_candidate_scored",
        "candidate": f"{TASK}/{REVISION}/{CANDIDATE_ID}",
        "evaluator_source_bundle_sha256": protocol["eval_only"]["source_bundle_sha256"],
        "protocol_sha256": sha256_file(project_root / PROTOCOL_PATH),
        "config_sha256": sha256_file(project_root / CONFIG_PATH),
        "fixed_dev_sha256": binding["hashes"]["dev_file"],
        "scope_counts": binding["scene_counts"],
        "scenes": binding["scene_total"], "truths": binding["truth_total"],
        "threshold": THRESHOLD, "matching_tolerance_px": MATCHING_TOLERANCE_PX,
        "train_dev_split_identity": binding["split_identity"],
        "per_scene_overlap_verified": False,
        "private_reads": 0, "sealed_reads": 0,
        "optimizer_steps": 0, "production_approval": False,
        "completed_checkpoint_present": checkpoint_present,
        "completed_training_report_present": report_present,
    }


def _require_sha256(value: str | None, name: str) -> str:
    if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
        raise ValueError(f"An externally pinned lowercase SHA-256 is required for {name}")
    return value


def _training_binding(repo: Path, config: dict, report: dict, checkpoint: Path,
                      *, expected_checkpoint_sha256: str, expected_report_sha256: str,
                      report_path: Path) -> dict:
    digest = sha256_file(checkpoint)
    if digest != _require_sha256(expected_checkpoint_sha256, "V39 checkpoint"):
        raise ValueError("V39 checkpoint differs from the externally pinned completed artifact")
    actual_report_sha256 = sha256_file(report_path)
    if actual_report_sha256 != _require_sha256(expected_report_sha256, "V39 training report"):
        raise ValueError("V39 training report differs from its external receipt pin")
    if (report.get("task"), report.get("revision"), report.get("candidate_id")) != (TASK, REVISION, CANDIDATE_ID):
        raise ValueError("Training report belongs to a different candidate")
    if report.get("schema") != "graphreader.marker-center-confidence-pairwise-training-report.v1" or \
            report.get("status") != "training_complete_awaiting_dev_evaluation":
        raise ValueError("V39 training run is not complete")
    if report.get("checkpoint_sha256") != digest:
        raise ValueError("Completed V39 checkpoint does not match its report digest")
    if report.get("initializer_sha256") != training_runner.INITIALIZER_SHA256:
        raise ValueError("V39 run used a different V27 initializer")
    if (report.get("feature_report_sha256") != training_runner.FEATURE_REPORT_SHA256 or
            report.get("parent_feature_report_sha256") != training_runner.PARENT_FEATURE_REPORT_SHA256):
        raise ValueError("V39 run used a different synthetic training report")
    if (report.get("development_reads") != 0 or report.get("private_reads") != 0 or
            report.get("sealed_reads") != 0 or report.get("production_approval") is not False or
            report.get("release_eligible") is not False):
        raise ValueError("V39 training report indicates a prohibited read or budget use")
    training = report.get("training")
    if not isinstance(training, dict) or training.get("optimizer_steps") != training_runner.EXPECTED_STEPS:
        raise ValueError("V39 training report does not show the complete fixed recipe")
    if (report.get("runner_source_bundle_sha256") != config["expected_runner_source_bundle_sha256"] or
            report.get("feature_groups") != len(training_runner.TRAINING_ROWS) or
            report.get("training_rows") != sum(training_runner.TRAINING_ROWS.values()) or
            report.get("device") != {"name": "AMD Radeon RX 6600 XT", "hip_architecture": "gfx1032", "torch_device": "cuda:0"} or
            report.get("synthetic_train_only") is not True):
        raise ValueError("Completed training report source, population, or device binding changed")
    candidate_payload = torch.load(checkpoint, map_location="cpu", weights_only=True)
    if set(candidate_payload) != {"state_dict", "config", "candidate_binding"}:
        raise ValueError("Completed checkpoint schema changed")
    opened = candidate_payload.get("candidate_binding")
    opened_ref = report.get("canonical_opened_record")
    expected_opened_path = Path("ml/markers/training-seals/marker-center/marker-center-confidence-pairwise-v39/P1/opened.json")
    if opened_ref != expected_opened_path.as_posix():
        raise ValueError("Training report canonical-open record path changed")
    opened_record = _read_json(_safe_project_file(repo, expected_opened_path))
    if (opened_record.get("status") != "opened" or opened_record.get("binding") != opened or
            not isinstance(opened, dict) or
            (opened.get("task"), opened.get("revision"), opened.get("candidate_id")) != (TASK, REVISION, CANDIDATE_ID)):
        raise ValueError("Training report has no canonical source-snapshot binding")
    if opened.get("runner_source_bundle_sha256") != config["expected_runner_source_bundle_sha256"]:
        raise ValueError("Training report runner bundle differs from the fixed candidate")
    snapshot_ref = opened.get("source_snapshot_path")
    if not isinstance(snapshot_ref, str):
        raise ValueError("Training report omitted its immutable source snapshot")
    snapshot_path = _safe_project_file(repo, Path(snapshot_ref))
    if sha256_file(snapshot_path) != opened.get("source_snapshot_sha256"):
        raise ValueError("V39 source snapshot digest changed")
    verify_bound_source_snapshot(repo, snapshot_path, opened.get("source_snapshot_sha256"))
    snapshot = _read_json(snapshot_path)
    if snapshot.get("identity") != {"task": TASK, "revision": REVISION, "candidate_id": CANDIDATE_ID}:
        raise ValueError("V39 source snapshot identity changed")
    if (opened.get("candidate_config_path") != CONFIG_PATH.as_posix() or
            opened.get("candidate_config_sha256") != sha256_file(repo / CONFIG_PATH)):
        raise ValueError("Canonical opened record config differs from the fixed P1 config")
    return {"checkpoint_sha256": digest, "source_snapshot_sha256": sha256_file(snapshot_path),
            "opened_binding": opened, "checkpoint_payload": candidate_payload,
            "training_report_sha256": actual_report_sha256}


def summarize_metrics(development: dict) -> dict:
    results = development.get("results")
    if not isinstance(results, dict) or set(results) != set(DEV_COUNTS):
        raise ValueError("Shared evaluator omitted a required dev scope")
    summary = {}
    for scope in ("component", "family"):
        kinds = results[scope]
        baseline = kinds["v27_balanced"].get(str(THRESHOLD))
        candidate = kinds["candidate_balanced"].get(str(THRESHOLD))
        if not isinstance(baseline, dict) or not isinstance(candidate, dict):
            raise ValueError("Shared evaluator omitted comparable V27/V39 operating-point metrics")
        for name, metrics in (("v27_balanced", baseline), ("candidate_balanced", candidate)):
            if metrics.get("truth_count") != DEV_COUNTS[scope][1]:
                raise ValueError(f"{scope} truth denominator changed in {name}")
            summary.setdefault(scope, {})[name] = {
                key: metrics[key] for key in (
                    "scene_count", "truth_count", "true_positives", "false_positives", "false_negatives",
                    "precision", "recall", "proposal_recall", "prohibited_structure_hits", "prohibited_structure_hit_rate",
                )
            }
    return summary


def run(project_root: Path = PROJECT_ROOT, output: Path | None = None, *,
        expected_checkpoint_sha256: str | None = None,
        expected_training_report_sha256: str | None = None) -> dict:
    project_root = project_root.resolve(strict=True)
    output = OUTPUT_PATH if output is None else output
    output_path = training_runner.validate_output_path(project_root, output)
    binding = preflight(project_root)
    config = _read_json(project_root / CONFIG_PATH)
    protocol = _read_json(project_root / PROTOCOL_PATH)
    data_root = binding["data_root"]
    train_output = _safe_project_directory(project_root, TRAIN_OUTPUT)
    checkpoint = _safe_project_file(train_output, Path(CHECKPOINT_NAME))
    training_report_path = _safe_project_file(train_output, Path(TRAINING_REPORT_NAME))
    training_report = _read_json(training_report_path)
    candidate = _training_binding(project_root, config, training_report, checkpoint,
        expected_checkpoint_sha256=expected_checkpoint_sha256,
        expected_report_sha256=expected_training_report_sha256,
        report_path=training_report_path)
    if protocol.get("evaluation_command", {}).get("requires_fixed_checkpoint_path") != TRAIN_OUTPUT.joinpath(CHECKPOINT_NAME).as_posix():
        raise ValueError("Candidate checkpoint path is not the fixed V39 output")
    output_path.mkdir(parents=True, exist_ok=False)
    started = time.perf_counter()
    try:
        # This is an evaluation-only process. It never constructs an optimizer.
        torch.set_num_threads(12)
        torch.set_num_interop_threads(1)
        torch.use_deterministic_algorithms(True)
        data_ref = config["development_file"]
        if Path(data_ref["path"]) != DEV_FILE:
            raise ValueError("P1 development path is not the fixed resume-worktree artifact")
        dev_path = _data_file(project_root, Path(data_ref["path"]))
        if sha256_file(dev_path) != data_ref["sha256"] or data_ref["sha256"] != DEV_SHA256:
            raise ValueError("Fixed development file changed")
        packed = torch.load(dev_path, map_location="cpu", weights_only=True)
        if not isinstance(packed, dict) or set(packed) != set(DEV_COUNTS):
            raise ValueError("Development membership changed")
        scenes = {
            scope: tuple(unpack_scene(row, expected_split=DEV_SCOPES[scope]) for row in rows)
            for scope, rows in packed.items()
        }
        if {scope: (len(rows), sum(len(row["metadata"]["centers"]) for row in packed[scope]))
                for scope, rows in scenes.items()} != DEV_COUNTS:
            raise ValueError("Complete development denominator changed")
        del packed
        base = ScaleClassifierNet(ModelConfig())
        initializer = _data_file(project_root, Path(config["source_checkpoint_path"]))
        if sha256_file(initializer) != training_runner.INITIALIZER_SHA256:
            raise ValueError("V27 initializer changed")
        base.load_state_dict(torch.load(initializer, map_location="cpu", weights_only=True)["state_dict"])
        original = {name: value.detach().clone() for name, value in base.state_dict().items()}
        model = SpatialCenterNet(base)
        state = candidate["checkpoint_payload"]
        if state["config"] != model.export_contract():
            raise ValueError("V39 checkpoint export contract changed")
        model.load_state_dict(state["state_dict"], strict=True)
        model.verify_frozen_state(original)
        model.eval()
        onnx_path = output_path / "candidate.onnx"
        torch.onnx.export(model, torch.zeros(1, 3, 33, 33), onnx_path,
            input_names=["candidate_patches"], output_names=["candidate_predictions"],
            dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
            opset_version=18, dynamo=False)
        onnx.checker.check_model(onnx.load(onnx_path))
        options = ort.SessionOptions()
        options.intra_op_num_threads = torch.get_num_threads()
        options.inter_op_num_threads = 1
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
        session = ort.InferenceSession(str(onnx_path), sess_options=options, providers=["CPUExecutionProvider"])
        results = evaluation.evaluate(data_root, scenes, model, session, _NoopBudget(), output_path,
                                      operating_threshold=THRESHOLD,
                                      evaluation_thresholds=(THRESHOLD,))
        validate_export_parity(results["parity"])
        baseline_report, baseline_cache = evaluation.historical._load_v27_cache(data_root)
        geometry_difference = 0.0
        for record in results["records"]:
            scope, index = record["scope"], record["scene_index"]
            old = baseline_report[scope + "_dev"]["cache_manifest"][index]
            baseline = baseline_cache[old["v27_candidate_predictions_key"]]
            actual = np.load(output_path / f"{scope}-{index:03d}-predictions.npy", allow_pickle=False)
            geometry_difference = max(geometry_difference, float(np.abs(baseline[:, 1:] - actual[:, 1:]).max()))
        if not math.isfinite(geometry_difference) or geometry_difference > 1e-5:
            raise RuntimeError("Frozen geometry changed on complete development proposals")
        model.verify_frozen_state(original)
        if sha256_file(checkpoint) != candidate["checkpoint_sha256"]:
            raise RuntimeError("V39 checkpoint changed during evaluation")
        if sha256_file(training_report_path) != candidate["training_report_sha256"]:
            raise RuntimeError("V39 training report changed during evaluation")
        validate_protocol(project_root, config, protocol)
        summary = summarize_metrics(results)
        report = {
            "schema": "graphreader.marker-center-confidence-pairwise-v39-dev-evaluation.v1",
            "status": "exploratory_dev_scored_no_independent_acceptance",
            "task": TASK, "revision": REVISION, "candidate_id": CANDIDATE_ID,
            "candidate_checkpoint_path": TRAIN_OUTPUT.joinpath(CHECKPOINT_NAME).as_posix(),
            "candidate_checkpoint_sha256": candidate["checkpoint_sha256"],
            "training_report_sha256": candidate["training_report_sha256"],
            "training_source_snapshot_sha256": candidate["source_snapshot_sha256"],
            "protocol_sha256": sha256_file(project_root / PROTOCOL_PATH),
            "config_sha256": sha256_file(project_root / CONFIG_PATH),
            "evaluator_source_bundle_sha256": protocol["eval_only"]["source_bundle_sha256"],
            "development_file_sha256": binding["hashes"]["dev_file"],
            "scope_counts": DEV_COUNTS, "scene_count": DEV_TOTALS[0], "truth_count": DEV_TOTALS[1],
            "threshold": THRESHOLD, "matching_tolerance_px": MATCHING_TOLERANCE_PX,
            "evaluation_thresholds": [THRESHOLD],
            "metrics": summary,
            "shared_evaluator": results,
            "onnx_sha256": sha256_file(onnx_path),
            "export_parity": results["parity"],
            "frozen_geometry_maximum_absolute_change": geometry_difference,
            "train_dev_split_identity": binding["split_identity"],
            "per_scene_train_dev_overlap_verified": False,
            "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
            "production_approval": False, "release_eligible": False,
            "seconds": time.perf_counter() - started,
        }
        write_json(output_path / "report.json", report)
        return report
    except BaseException as error:
        (output_path / "exception.txt").write_text(f"{type(error).__name__}: {error}\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight-only", action="store_true")
    parser.add_argument("--checkpoint-sha256")
    parser.add_argument("--training-report-sha256")
    args = parser.parse_args()
    result = preflight() if args.preflight_only else run(
        expected_checkpoint_sha256=args.checkpoint_sha256,
        expected_training_report_sha256=args.training_report_sha256)
    print(json.dumps(result if args.preflight_only else {
        "status": result["status"], "metrics": result["metrics"], "seconds": result["seconds"]
    }, sort_keys=True))
