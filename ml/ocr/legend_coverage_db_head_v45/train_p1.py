# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authorized train-only legend-coverage head repair with a verified CPU budget."""
from __future__ import annotations

import argparse
import ctypes
from dataclasses import asdict, dataclass
from hashlib import sha256
import json
import os
from pathlib import Path
import shutil
import time

import numpy as np

from ml.markers import training_budget
from ml.markers.gate_seal import (
    canonical_json_bytes, sha256_file, source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.ocr.inverse_db_target_v40 import train_p1 as v40
from ml.ocr.pretrained_db_head_v39 import train_p1 as v39
from ml.ocr.official_bakeoff import (
    db_inverse_targets, frozen_head_training, frozen_trunk_head,
    legend_coverage_head_inputs as legend_bridge,
    resource_limited_head_training as engine,
    supplemental_head_features,
    text_extent_head_inputs as bridge,
)

ROOT = Path("ml/ocr/legend_coverage_db_head_v45")
CONFIG_PATH = ROOT / "training/p1.json"
TASK = "ocr-detection"
REVISION = "graph-legend-coverage-db-head-v45"
REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
HISTORICAL_CAPTURE_SHA = "d5ca3328ec5ad2af0191fd92e078d02943692ee851f8be96d2a529352f603482"
SUPPLEMENTAL_COMPOSITION_SHA = "f0591ae25244b570ea2077d34b40dac96b40be1433d6ca89a0119989378529c0"
SUPPLEMENTAL_FEATURE_SHA = "4b2710b380be1b16d5fef7d9184920051c413304353ed821480bda0ab3a16d05"
SUPPLEMENTAL_FEATURE_MODEL_SHA = "6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081"
SUPPLEMENTAL_PREPARATION_SHA = "622357d57895755eacb631ea518eaa4d3268ac65aa0df4c910fe72cc0d56c737"
SUPPLEMENTAL_REQUEST_SHA = "0e795a4d17b1b1d0b2f652a30ffff30e038ec51eae61a9699510d65b43eb6cf1"
SUPPLEMENTAL_TRUTH_SHA = "13174cba2e740b47d1fc68f0e15c2078c720526be6475b91a8740e51195150b3"
SUPPLEMENTAL_CAPTURE_SHA = "2c605fb18cb0195453f1b569506546c22c3a873db19475e1fb87daa0bddaeb19"
HISTORICAL_BINARY_ROOT = Path(
    "artifacts/goal22-runs/ocr-v44-text-extent/runtime-before-word-stress-20260917"
)
HISTORICAL_BINARY_MANIFEST = HISTORICAL_BINARY_ROOT / "backup-manifest.json"
HISTORICAL_CAPTURE_SOURCE = Path(
    "artifacts/goal22-runs/ocr-supplemental-capture-base-snapshot/"
    "OfficialHeadTensorCapture.cs"
)
HISTORICAL_SOURCE_BINDING = Path(
    "artifacts/goal22-runs/ocr-supplemental-capture-base-snapshot/binding.json"
)
RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v40.RUNNER_SOURCE_PATHS,
    Path("ml/ocr/official_bakeoff/text_extent_head_inputs.py"),
    Path("ml/ocr/official_bakeoff/legend_coverage_head_inputs.py"),
    Path("ml/ocr/official_bakeoff/train_text_extent_preflight.py"),
    Path("ml/ocr/official_bakeoff/captured_head_features.py"),
    Path("ml/ocr/official_bakeoff/supplemental_head_features.py"),
    Path("ml/ocr/official_bakeoff/resource_limited_head_training.py"),
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_text_extent_candidate.py"),
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py"),
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_legend_coverage_candidate.py"),
    Path("docs/GOAL-22-OCR-LEGEND-EXTENT-DIAGNOSIS.json"),
    Path("docs/GOAL-22-OCR-LEGEND-HEAD-INPUTS.json"),
    Path("docs/GOAL-22-OCR-SUPPLEMENTAL-FEATURES.json"),
    ROOT / "__init__.py", ROOT / "train_p1.py", ROOT / "verify_export.py",
    ROOT / "prepare_candidate.py", ROOT / "protocol.json",
)))
RECIPE = {
    "seed": engine.SEED, "epochs": 240, "intraop_threads": 12,
    "batch_size": engine.BATCH_SIZE, "learning_rate": engine.LEARNING_RATE,
    "weight_decay": engine.WEIGHT_DECAY, "dice_epsilon": engine.DICE_EPSILON,
    "target_definition": "inverse_db_unclip_rectangle_v1",
    "supervision_mask": "all_ones_full_valid_runtime_crop",
    "empty_target_objective": "masked_mean_probability",
    "checkpoint_selection": "earliest_minimum_full_train_loss",
    "logical_processors": 12, "job_cpu_rate_maximum": 333,
    "priority": "Idle", "affinity_mask": 4095,
}
BOUND_KEYS = {
    "parent_model", "capture_request", "capture_report", "parity_report",
    "feature_model", "historical_capture", "supplemental_preparation",
    "supplemental_request", "supplemental_truth", "supplemental_capture_report",
    "supplemental_feature_report", "supplemental_feature_model",
    "supplemental_composition_report", "candidate_template",
}


class LegendCoverageTrainingError(ValueError):
    pass


@dataclass(frozen=True)
class PreparedTraining:
    config: dict
    config_sha256: str
    panels: tuple[frozen_head_training.TrainingPanel, ...]
    feature_inventory_sha256: str
    target_inventory_sha256: str
    parity_maximum_absolute_error: float


def _validate_cpu_observation(logical, flags, rate, priority, affinity):
    if (logical != 12 or flags != 5 or type(rate) is not int
            or not 1 <= rate <= 333 or priority != 0x40 or affinity != 4095):
        raise LegendCoverageTrainingError("Replacement CPU budget is not enforced")
    return {"logical_processors": logical, "job_cpu_flags": flags,
            "job_cpu_rate": rate, "priority": "Idle", "affinity_mask": affinity}


def _cpu_budget():
    if os.name != "nt":
        raise LegendCoverageTrainingError("This authorized run requires the Windows CPU budget")
    class Rate(ctypes.Structure):
        _fields_ = [("flags", ctypes.c_uint32), ("rate", ctypes.c_uint32)]
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.QueryInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p]
    kernel.GetPriorityClass.argtypes = [ctypes.c_void_p]
    kernel.GetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    process = kernel.GetCurrentProcess()
    observed = Rate()
    affinity, system = ctypes.c_size_t(), ctypes.c_size_t()
    if (not kernel.QueryInformationJobObject(None, 15, ctypes.byref(observed), ctypes.sizeof(observed), None)
            or not kernel.GetProcessAffinityMask(process, ctypes.byref(affinity), ctypes.byref(system))):
        raise LegendCoverageTrainingError("Cannot verify the active Windows CPU budget")
    return _validate_cpu_observation(os.cpu_count(), observed.flags, observed.rate,
                                     kernel.GetPriorityClass(process), affinity.value)


def _bound(root, descriptor):
    if type(descriptor) is not dict or set(descriptor) != {"path", "sha256"}:
        raise LegendCoverageTrainingError("Invalid bound artifact descriptor")
    path = (root / descriptor["path"]).resolve()
    if not path.is_relative_to(root) or sha256_file(path) != descriptor["sha256"]:
        raise LegendCoverageTrainingError("Bound artifact changed or escaped the repository")
    return path


def _json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise LegendCoverageTrainingError("Duplicate JSON field")
            result[key] = value
        return result
    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique)
    if type(value) is not dict:
        raise LegendCoverageTrainingError("Expected JSON object")
    return value


def _config(root):
    config = _json(root / CONFIG_PATH)
    fixed = {"schema": "graphreader.ocr-legend-coverage-v45-candidate-config.v1",
             "task": TASK, "revision": REVISION, "candidate_id": "P1",
             "synthetic_only": True, "private_data": False, "sealed_data": False,
             "production_approval": False, "recipe": RECIPE}
    if (set(config) != set(fixed) | {"bound_files", "expected_runner_source_bundle_sha256"}
            or any(canonical_json_bytes({k: config.get(k)}) != canonical_json_bytes({k: v}) for k, v in fixed.items())
            or type(config["bound_files"]) is not dict
            or set(config["bound_files"]) != BOUND_KEYS
            or config["expected_runner_source_bundle_sha256"] != source_bundle_sha256(root, RUNNER_SOURCE_PATHS)):
        raise LegendCoverageTrainingError("V45 configuration or source bundle changed")
    for value in config["bound_files"].values():
        _bound(root, value)
    expected = {
        "parent_model": frozen_trunk_head.REVIEWED_DETECTOR_SHA256,
        "historical_capture": HISTORICAL_CAPTURE_SHA,
        "supplemental_preparation": SUPPLEMENTAL_PREPARATION_SHA,
        "supplemental_request": SUPPLEMENTAL_REQUEST_SHA,
        "supplemental_truth": SUPPLEMENTAL_TRUTH_SHA,
        "supplemental_capture_report": SUPPLEMENTAL_CAPTURE_SHA,
        "supplemental_feature_report": SUPPLEMENTAL_FEATURE_SHA,
        "supplemental_feature_model": SUPPLEMENTAL_FEATURE_MODEL_SHA,
        "supplemental_composition_report": SUPPLEMENTAL_COMPOSITION_SHA,
    }
    if any(config["bound_files"][key]["sha256"] != value
           for key, value in expected.items()):
        raise LegendCoverageTrainingError("Parent, historical, or supplemental evidence changed")
    return config


def _validate_input_counts(inputs):
    expected = {
        "train": (26, 34, 862),
        "dev": (3, 9, 183),
    }
    for name, split in (("train", inputs.train), ("dev", inputs.dev)):
        sources, panels, truths = expected[name]
        if (split.source_count != sources or split.panel_count != panels
                or split.full_source_truth_count != truths
                or split.projected_source_truth_count != truths
                or split.outside_runtime_crop_truth_count != 0
                or split.partial_source_truth_count != 0
                or split.overlapping_source_truth_count != 0):
            raise LegendCoverageTrainingError(f"{name} input inventory changed")


def _merge_features(inputs, historical_features, historical_report,
                    supplemental_report, supplemental_rows):
    """Join authenticated caches and bind all 43 feature identities once."""
    feature_hashes = {}
    historical_rows = historical_report.get("panels")
    if not isinstance(historical_rows, list) or len(historical_rows) != 37:
        raise LegendCoverageTrainingError("Historical feature inventory changed")
    for row in historical_rows:
        if (not isinstance(row, dict) or row.get("panel_id") in feature_hashes
                or not isinstance(row.get("feature_sha256"), str)):
            raise LegendCoverageTrainingError("Historical feature identity changed")
        feature_hashes[row["panel_id"]] = row["feature_sha256"]

    features = dict(historical_features)
    if len(supplemental_rows) != 6 or supplemental_report.get("panel_count") != 6:
        raise LegendCoverageTrainingError("Supplemental feature inventory changed")
    for row, feature in supplemental_rows:
        panel_id = row.get("panel_id")
        if panel_id in features or panel_id in feature_hashes:
            raise LegendCoverageTrainingError("Feature panel identity collides")
        features[panel_id] = (feature, row.get("input_sha256"))
        feature_hashes[panel_id] = row.get("feature_sha256")

    panels = (*inputs.train.panels, *inputs.dev.panels)
    by_id = {panel.panel_id: panel for panel in panels}
    if len(by_id) != 43 or set(features) != set(by_id) or set(feature_hashes) != set(by_id):
        raise LegendCoverageTrainingError("Combined feature and panel inventories differ")
    inventory = sha256()
    for panel_id in sorted(by_id):
        panel = by_id[panel_id]
        feature, input_sha = features[panel_id]
        expected_shape = (1, frozen_trunk_head.FEATURE_CHANNELS,
                          panel.tensor_shape[2] // 4, panel.tensor_shape[3] // 4)
        if (input_sha != panel.tensor_sha256 or feature.shape != expected_shape
                or feature.dtype != np.float32 or not np.isfinite(feature).all()):
            raise LegendCoverageTrainingError(f"Feature does not match panel: {panel_id}")
        inventory.update(panel_id.encode("utf-8") + b"\0")
        inventory.update(input_sha.encode("ascii") + b"\0")
        inventory.update(feature_hashes[panel_id].encode("ascii") + b"\n")
    return features, inventory.hexdigest()


def _training_panels(inputs, features):
    """Only train panels enter the optimizer; all truth projections remain supervised."""
    _validate_input_counts(inputs)
    panels, inventory, truth_count = [], sha256(), 0
    for panel in inputs.train.panels:
        if panel.split != "train":
            raise LegendCoverageTrainingError("Non-train panel reached target construction")
        feature, input_sha = features[panel.panel_id]
        if input_sha != panel.tensor_sha256:
            raise LegendCoverageTrainingError("Feature input differs from the captured tensor")
        immutable_feature = _immutable_feature(feature)
        _, _, height, width = panel.tensor_shape
        sx, sy = width / panel.width, height / panel.height
        boxes = [(p.panel_box[0]*sx, p.panel_box[1]*sy,
                  p.panel_box[2]*sx, p.panel_box[3]*sy) for p in panel.projections]
        built = db_inverse_targets.build_inverse_targets(width, height, boxes)
        if len(built.regions) != len(panel.projections):
            raise LegendCoverageTrainingError("Inverse target construction dropped truth")
        truth_count += len(built.regions)
        inventory.update(panel.panel_id.encode() + b"\0" + built.target.tobytes())
        mask = np.frombuffer(np.ones_like(built.target).tobytes(), dtype=np.float32).reshape(built.target.shape)
        panels.append(frozen_head_training.TrainingPanel(
            panel.panel_id, "train", immutable_feature, built.target, mask))
    if len(panels) != 34 or truth_count != 862:
        raise LegendCoverageTrainingError("Training target inventory changed")
    return tuple(panels), inventory.hexdigest()


def _immutable_feature(feature):
    """Copy authenticated feature bytes into the trainer's immutable boundary."""
    if (
        not isinstance(feature, np.ndarray)
        or feature.dtype != np.float32
        or not feature.flags.c_contiguous
        or not np.isfinite(feature).all()
    ):
        raise LegendCoverageTrainingError(
            "Feature must be finite C-contiguous float32 before optimizer admission"
        )
    payload = feature.tobytes(order="C")
    immutable = np.frombuffer(payload, dtype=np.float32).reshape(feature.shape)
    if immutable.flags.writeable or immutable.tobytes(order="C") != payload:
        raise LegendCoverageTrainingError("Immutable feature boundary changed authenticated bytes")
    return immutable


def _supplemental_preparation(root, files, base_preparation):
    stored = _json(_bound(root, files["supplemental_preparation"]))
    expected = {
        "fixed_dev_truths": 183,
        "historical_request_sha256": files["capture_request"]["sha256"],
        "historical_tensor_dev_truths": 183,
        "historical_tensor_train_truths": 709,
        "optimizer_steps": 0,
        "private_reads": 0,
        "production_approval": False,
        "request": files["supplemental_request"],
        "sealed_reads": 0,
        "status": "supplemental_capture_request_prepared",
        "supplemental_panels": 6,
        "supplemental_truths": 153,
        "truth": files["supplemental_truth"],
    }
    if stored != expected:
        raise LegendCoverageTrainingError("Supplemental preparation changed")
    request = _json(_bound(root, files["supplemental_request"]))
    reports = request.get("reports")
    assemblies = request.get("assemblies")
    if (not isinstance(reports, list) or len(reports) != 1
            or not isinstance(reports[0], dict) or reports[0].get("split") != "train"
            or not isinstance(assemblies, list) or len(assemblies) != 4
            or not all(isinstance(row, dict) for row in assemblies)):
        raise LegendCoverageTrainingError("Supplemental capture request changed")
    report = reports[0]
    preparation = legend_bridge.prepare_legend_coverage_capture_request(
        base_preparation,
        root / request["binding"]["path"], request["binding"]["sha256"],
        root / files["supplemental_truth"]["path"], files["supplemental_truth"]["sha256"],
        root / report["manifest_path"], report["manifest_sha256"],
        root / report["report_path"], report["report_sha256"],
        root / request["candidate"]["path"], request["candidate"]["sha256"],
        repository_root=root,
        capture_binary_root=root / Path(assemblies[0]["path"]).parent,
        capture_source_path=root / request["capture_source"]["path"],
    )
    if preparation.request != request:
        raise LegendCoverageTrainingError("Supplemental capture request reconstruction changed")
    return preparation


def _prepare_historical_capture(root, files, request):
    reports = [bridge.RuntimeReportEvidence(
        row["split"], root / row["manifest_path"], row["manifest_sha256"],
        root / row["report_path"], row["report_sha256"],
    ) for row in request["reports"]]
    preparation = legend_bridge.prepare_historical_base_capture(
        root / request["binding"]["path"], request["binding"]["sha256"], reports,
        root / request["candidate"]["path"], request["candidate"]["sha256"],
        root / files["capture_request"]["path"], files["capture_request"]["sha256"],
        root / HISTORICAL_BINARY_ROOT, root / HISTORICAL_CAPTURE_SOURCE,
        root / HISTORICAL_BINARY_MANIFEST,
        legend_bridge.EXPECTED_HISTORICAL_BINARY_MANIFEST_SHA256,
        root / HISTORICAL_SOURCE_BINDING,
        legend_bridge.EXPECTED_HISTORICAL_SOURCE_BINDING_SHA256,
        repository_root=root,
    )
    if preparation.request != request:
        raise LegendCoverageTrainingError("Authenticated historical capture request changed")
    return preparation


def prepare_training(repository_root=REPOSITORY_ROOT):
    root = Path(repository_root).resolve()
    config = _config(root)
    files = config["bound_files"]
    request = _json(_bound(root, files["capture_request"]))
    base_preparation = _prepare_historical_capture(root, files, request)
    capture_path = _bound(root, files["capture_report"])
    capture = _json(capture_path)
    base_inputs = bridge.load_text_extent_head_inputs(
        base_preparation, capture_path, files["capture_report"]["sha256"], repository_root=root)
    historical = _json(_bound(root, files["historical_capture"]))
    def dev_hashes(report):
        return {p["panel_id"]: p["tensor"]["sha256"] for p in report["panels"] if p["split"] == "validation"}
    if dev_hashes(capture) != dev_hashes(historical) or len(dev_hashes(capture)) != 9:
        raise LegendCoverageTrainingError("Fixed dev tensor bytes changed")
    supplemental_preparation = _supplemental_preparation(root, files, base_preparation)
    supplemental_capture_path = _bound(root, files["supplemental_capture_report"])
    inputs = legend_bridge.load_legend_coverage_head_inputs(
        base_inputs, supplemental_preparation, supplemental_capture_path,
        files["supplemental_capture_report"]["sha256"], repository_root=root)
    if inputs.dev is not base_inputs.dev:
        raise LegendCoverageTrainingError("Fixed development split object changed")
    _validate_input_counts(inputs)
    parity_path = _bound(root, files["parity_report"])
    parity = _json(parity_path)
    historical_features, _, historical_maximum = v39._validate_parity_and_features(
        parity, parity_path.parent, capture, files["capture_report"]["sha256"],
        sha256_file(root/"ml/ocr/official_bakeoff/captured_head_features.py"),
        sha256_file(root/"ml/ocr/official_bakeoff/frozen_trunk_head.py"),
        files["feature_model"]["sha256"])
    composition_path = _bound(root, files["supplemental_composition_report"])
    if composition_path != (root / supplemental_head_features.COMPOSITION_PATH).resolve():
        raise LegendCoverageTrainingError("Supplemental composition path changed")
    supplemental_feature_path = _bound(root, files["supplemental_feature_report"])
    supplemental_feature_model = _bound(root, files["supplemental_feature_model"])
    if supplemental_feature_model != supplemental_feature_path.parent / "frozen-trunk.onnx":
        raise LegendCoverageTrainingError("Supplemental feature model path changed")
    supplemental_report, supplemental_rows = supplemental_head_features.load_cached_features(
        repository_root=root,
        feature_report=Path(files["supplemental_feature_report"]["path"]),
        expected_report_sha256=files["supplemental_feature_report"]["sha256"],
    )
    features, feature_hash = _merge_features(
        inputs, historical_features, parity, supplemental_report, supplemental_rows)
    maximum = max(historical_maximum, supplemental_report["maximum_absolute_error"])
    panels, target_hash = _training_panels(inputs, features)
    return PreparedTraining(config, sha256_file(root/CONFIG_PATH), panels, feature_hash, target_hash, maximum)


def _resume_source(root, checkpoint, expected_sha256):
    if checkpoint is None and expected_sha256 is None:
        return None
    if checkpoint is None or not isinstance(expected_sha256, str) or len(expected_sha256) != 64:
        raise LegendCoverageTrainingError("Recovery requires a checkpoint and its SHA-256")
    path = (root / checkpoint).resolve()
    if not path.is_relative_to(root / "artifacts") or not path.is_file() or sha256_file(path) != expected_sha256:
        raise LegendCoverageTrainingError("Recovery checkpoint is missing, changed, or outside artifacts")
    return path


def train_candidate(output_directory, repository_root=REPOSITORY_ROOT, *,
                    resume_checkpoint=None, resume_checkpoint_sha256=None):
    root = Path(repository_root).resolve()
    output = (root / output_directory).resolve()
    if not output.is_relative_to(root/"artifacts") or output.exists():
        raise LegendCoverageTrainingError("Use a new artifacts output directory")
    resource = _cpu_budget()
    _config(root)
    resume_source = _resume_source(root, resume_checkpoint, resume_checkpoint_sha256)
    authorization = training_budget.acquire_training_candidate(
        root, task=TASK, revision=REVISION, candidate_id="P1",
        config_path=CONFIG_PATH, runner_source_paths=RUNNER_SOURCE_PATHS)
    started = time.perf_counter()
    phase = "preflight"
    result = None
    try:
        output.mkdir(parents=True, exist_ok=False)
        prepared = prepare_training(root)
        if authorization.binding["candidate_config_sha256"] != prepared.config_sha256:
            raise LegendCoverageTrainingError("Prepared configuration differs from authorization")
        verify_bound_source_snapshot(root, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        parent = _bound(root, prepared.config["bound_files"]["parent_model"])
        bundle = frozen_trunk_head.extract_frozen_trunk_head(parent)
        recovery = output / "recovery.pt"
        if resume_source is not None:
            shutil.copyfile(resume_source, recovery)
            if sha256_file(recovery) != resume_checkpoint_sha256:
                raise LegendCoverageTrainingError("Recovery checkpoint changed while copying")
        phase = "optimization"
        def progress(epoch):
            record = {"epoch": asdict(epoch), "elapsed_ms": (time.perf_counter()-started)*1000,
                      "status": "training", "production_approved": False}
            temporary = output/"progress.pending.json"
            temporary.write_bytes(canonical_json_bytes(record))
            os.replace(temporary, output/"progress.json")
        result = engine.train_resource_limited_head(
            bundle.head, prepared.panels, cancellation_check=_cpu_budget, progress_callback=progress,
            checkpoint_path=recovery, checkpoint_binding={
                "candidate_config_sha256": prepared.config_sha256,
                "feature_inventory_sha256": prepared.feature_inventory_sha256,
                "target_inventory_sha256": prepared.target_inventory_sha256,
            })
        if (result.optimizer_steps != 8160 or result.epochs != 240 or result.intraop_threads != 12
                or result.frozen_batch_norm_sha256_before != result.frozen_batch_norm_sha256_after):
            raise LegendCoverageTrainingError("Training result changed the authorized recipe")
        phase = "artifacts"
        checkpoint = output/"selected-head.pt"
        model = output/"detector-legend-coverage-v45-p1.onnx"
        v39._write_checkpoint(checkpoint, bundle.head)
        patch = frozen_trunk_head.patch_head_constants(parent, model, bundle.head)
        v40._validate_patch(patch, model)
        verify_bound_source_snapshot(root, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"schema": "graphreader.ocr-legend-coverage-v45-training-stage.v1",
                  "status": "trained_pending_export_parity_and_dev", "task": TASK,
                  "revision": REVISION, "candidate_id": "P1", "training": asdict(result),
                  "training_authorization": authorization.binding, "resource": resource,
                  "feature_inventory_sha256": prepared.feature_inventory_sha256,
                  "target_inventory_sha256": prepared.target_inventory_sha256,
                  "checkpoint_sha256": sha256_file(checkpoint), "model_sha256": sha256_file(model),
                  "resume_checkpoint": None if resume_source is None else {
                      "path": resume_source.relative_to(root).as_posix(),
                      "sha256": resume_checkpoint_sha256},
                  "elapsed_ms": (time.perf_counter()-started)*1000,
                  "private_reads": 0, "sealed_reads": 0, "production_approved": False}
        (output/"training-stage.json").write_bytes(canonical_json_bytes(report))
        return report
    except BaseException as error:
        failure = {"status": "failed_presealed", "phase": phase,
                   "optimizer_steps": 0 if phase == "preflight" else result.optimizer_steps if result else None,
                   "exception_type": type(error).__name__, "exception_message": str(error),
                   "private_reads": 0, "sealed_reads": 0, "production_approved": False}
        try:
            if output.is_dir() and not (output/"training-stage.json").exists():
                (output/"training-stage.json").write_bytes(canonical_json_bytes(failure))
        finally:
            training_budget.void_candidate(authorization, error)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--resume-checkpoint", type=Path)
    parser.add_argument("--resume-checkpoint-sha256")
    args = parser.parse_args()
    if args.prepare_only:
        prepared = prepare_training()
        print(json.dumps({
            "train_sources": 26, "train_panels": len(prepared.panels),
            "train_truths": 862, "dev_sources": 3, "dev_panels": 9,
            "dev_truths": 183,
            "feature_inventory_sha256": prepared.feature_inventory_sha256,
            "target_inventory_sha256": prepared.target_inventory_sha256,
            "parity_maximum_absolute_error": prepared.parity_maximum_absolute_error,
            "optimizer_steps": 0, "resource": _cpu_budget(),
        }))
    elif args.output:
        result = train_candidate(args.output, resume_checkpoint=args.resume_checkpoint,
                                 resume_checkpoint_sha256=args.resume_checkpoint_sha256)
        print(json.dumps({"status": result["status"], "elapsed_ms": result["elapsed_ms"]}))
    else:
        parser.error("Specify --prepare-only or --output")


if __name__ == "__main__":
    main()
