# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authorized train-only text-extent repair with a verified Windows CPU budget."""
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
    resource_limited_head_training as engine,
    text_extent_head_inputs as bridge,
)

ROOT = Path("ml/ocr/text_extent_db_head_v44")
CONFIG_PATH = ROOT / "training/p1.json"
TASK = "ocr-detection"
REVISION = "graph-text-extent-db-head-v44"
REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
HISTORICAL_CAPTURE_SHA = "d5ca3328ec5ad2af0191fd92e078d02943692ee851f8be96d2a529352f603482"
RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v40.RUNNER_SOURCE_PATHS,
    Path("ml/ocr/official_bakeoff/text_extent_head_inputs.py"),
    Path("ml/ocr/official_bakeoff/train_text_extent_preflight.py"),
    Path("ml/ocr/official_bakeoff/captured_head_features.py"),
    Path("ml/ocr/official_bakeoff/resource_limited_head_training.py"),
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_text_extent_candidate.py"),
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py"),
    Path("docs/GOAL-22-V43-REPRESENTATION-DIAGNOSIS.json"),
    ROOT / "__init__.py", ROOT / "train_p1.py", ROOT / "protocol.json",
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
    "feature_model", "historical_capture",
}


class TextExtentTrainingError(ValueError):
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
        raise TextExtentTrainingError("Replacement CPU budget is not enforced")
    return {"logical_processors": logical, "job_cpu_flags": flags,
            "job_cpu_rate": rate, "priority": "Idle", "affinity_mask": affinity}


def _cpu_budget():
    if os.name != "nt":
        raise TextExtentTrainingError("This authorized run requires the Windows CPU budget")
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
        raise TextExtentTrainingError("Cannot verify the active Windows CPU budget")
    return _validate_cpu_observation(os.cpu_count(), observed.flags, observed.rate,
                                     kernel.GetPriorityClass(process), affinity.value)


def _bound(root, descriptor):
    if type(descriptor) is not dict or set(descriptor) != {"path", "sha256"}:
        raise TextExtentTrainingError("Invalid bound artifact descriptor")
    path = (root / descriptor["path"]).resolve()
    if not path.is_relative_to(root) or sha256_file(path) != descriptor["sha256"]:
        raise TextExtentTrainingError("Bound artifact changed or escaped the repository")
    return path


def _json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise TextExtentTrainingError("Duplicate JSON field")
            result[key] = value
        return result
    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique)
    if type(value) is not dict:
        raise TextExtentTrainingError("Expected JSON object")
    return value


def _config(root):
    config = _json(root / CONFIG_PATH)
    fixed = {"schema": "graphreader.ocr-text-extent-v44-candidate-config.v1",
             "task": TASK, "revision": REVISION, "candidate_id": "P1",
             "synthetic_only": True, "private_data": False, "sealed_data": False,
             "production_approval": False, "recipe": RECIPE}
    if (set(config) != set(fixed) | {"bound_files", "expected_runner_source_bundle_sha256"}
            or any(canonical_json_bytes({k: config.get(k)}) != canonical_json_bytes({k: v}) for k, v in fixed.items())
            or type(config["bound_files"]) is not dict
            or set(config["bound_files"]) != BOUND_KEYS
            or config["expected_runner_source_bundle_sha256"] != source_bundle_sha256(root, RUNNER_SOURCE_PATHS)):
        raise TextExtentTrainingError("V44 configuration or source bundle changed")
    for value in config["bound_files"].values():
        _bound(root, value)
    if (config["bound_files"]["parent_model"]["sha256"] != frozen_trunk_head.REVIEWED_DETECTOR_SHA256
            or config["bound_files"]["historical_capture"]["sha256"] != HISTORICAL_CAPTURE_SHA):
        raise TextExtentTrainingError("Parent model or fixed dev capture changed")
    return config


def _training_panels(inputs, features):
    """Only train panels enter the optimizer; all truth projections remain supervised."""
    v39._validate_loaded_counts(inputs)
    panels, inventory, truth_count = [], sha256(), 0
    for panel in inputs.train.panels:
        if panel.split != "train":
            raise TextExtentTrainingError("Non-train panel reached target construction")
        feature, input_sha = features[panel.panel_id]
        if input_sha != panel.tensor_sha256:
            raise TextExtentTrainingError("Feature input differs from the captured tensor")
        _, _, height, width = panel.tensor_shape
        sx, sy = width / panel.width, height / panel.height
        boxes = [(p.panel_box[0]*sx, p.panel_box[1]*sy,
                  p.panel_box[2]*sx, p.panel_box[3]*sy) for p in panel.projections]
        built = db_inverse_targets.build_inverse_targets(width, height, boxes)
        if len(built.regions) != len(panel.projections):
            raise TextExtentTrainingError("Inverse target construction dropped truth")
        truth_count += len(built.regions)
        inventory.update(panel.panel_id.encode() + b"\0" + built.target.tobytes())
        mask = np.frombuffer(np.ones_like(built.target).tobytes(), dtype=np.float32).reshape(built.target.shape)
        panels.append(frozen_head_training.TrainingPanel(
            panel.panel_id, "train", feature, built.target, mask))
    if len(panels) != 28 or truth_count != 709:
        raise TextExtentTrainingError("Training target inventory changed")
    return tuple(panels), inventory.hexdigest()


def prepare_training(repository_root=REPOSITORY_ROOT):
    root = Path(repository_root).resolve()
    config = _config(root)
    files = config["bound_files"]
    request = _json(_bound(root, files["capture_request"]))
    reports = [bridge.RuntimeReportEvidence(
        row["split"], root/row["manifest_path"], row["manifest_sha256"],
        root/row["report_path"], row["report_sha256"]) for row in request["reports"]]
    preparation = bridge.prepare_text_extent_capture_request(
        root/request["binding"]["path"], request["binding"]["sha256"], reports,
        root/request["candidate"]["path"], request["candidate"]["sha256"], repository_root=root)
    if preparation.request != request:
        raise TextExtentTrainingError("Application capture request changed")
    capture_path = _bound(root, files["capture_report"])
    capture = _json(capture_path)
    inputs = bridge.load_text_extent_head_inputs(
        preparation, capture_path, files["capture_report"]["sha256"], repository_root=root)
    historical = _json(_bound(root, files["historical_capture"]))
    def dev_hashes(report):
        return {p["panel_id"]: p["tensor"]["sha256"] for p in report["panels"] if p["split"] == "validation"}
    if dev_hashes(capture) != dev_hashes(historical) or len(dev_hashes(capture)) != 9:
        raise TextExtentTrainingError("Fixed dev tensor bytes changed")
    parity_path = _bound(root, files["parity_report"])
    parity = _json(parity_path)
    features, feature_hash, maximum = v39._validate_parity_and_features(
        parity, parity_path.parent, capture, files["capture_report"]["sha256"],
        sha256_file(root/"ml/ocr/official_bakeoff/captured_head_features.py"),
        sha256_file(root/"ml/ocr/official_bakeoff/frozen_trunk_head.py"),
        files["feature_model"]["sha256"])
    panels, target_hash = _training_panels(inputs, features)
    return PreparedTraining(config, sha256_file(root/CONFIG_PATH), panels, feature_hash, target_hash, maximum)


def _resume_source(root, checkpoint, expected_sha256):
    if checkpoint is None and expected_sha256 is None:
        return None
    if checkpoint is None or not isinstance(expected_sha256, str) or len(expected_sha256) != 64:
        raise TextExtentTrainingError("Recovery requires a checkpoint and its SHA-256")
    path = (root / checkpoint).resolve()
    if not path.is_relative_to(root / "artifacts") or not path.is_file() or sha256_file(path) != expected_sha256:
        raise TextExtentTrainingError("Recovery checkpoint is missing, changed, or outside artifacts")
    return path


def train_candidate(output_directory, repository_root=REPOSITORY_ROOT, *,
                    resume_checkpoint=None, resume_checkpoint_sha256=None):
    root = Path(repository_root).resolve()
    output = (root / output_directory).resolve()
    if not output.is_relative_to(root/"artifacts") or output.exists():
        raise TextExtentTrainingError("Use a new artifacts output directory")
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
            raise TextExtentTrainingError("Prepared configuration differs from authorization")
        verify_bound_source_snapshot(root, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        parent = _bound(root, prepared.config["bound_files"]["parent_model"])
        bundle = frozen_trunk_head.extract_frozen_trunk_head(parent)
        recovery = output / "recovery.pt"
        if resume_source is not None:
            shutil.copyfile(resume_source, recovery)
            if sha256_file(recovery) != resume_checkpoint_sha256:
                raise TextExtentTrainingError("Recovery checkpoint changed while copying")
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
        if (result.optimizer_steps != 6720 or result.epochs != 240 or result.intraop_threads != 12
                or result.frozen_batch_norm_sha256_before != result.frozen_batch_norm_sha256_after):
            raise TextExtentTrainingError("Training result changed the authorized recipe")
        phase = "artifacts"
        checkpoint, model = output/"selected-head.pt", output/"detector-text-extent-v44-p1.onnx"
        v39._write_checkpoint(checkpoint, bundle.head)
        patch = frozen_trunk_head.patch_head_constants(parent, model, bundle.head)
        v40._validate_patch(patch, model)
        verify_bound_source_snapshot(root, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"schema": "graphreader.ocr-text-extent-v44-training-stage.v1",
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
        print(json.dumps({"train_panels": len(prepared.panels), "target_inventory_sha256": prepared.target_inventory_sha256,
                          "optimizer_steps": 0, "resource": _cpu_budget()}))
    elif args.output:
        result = train_candidate(args.output, resume_checkpoint=args.resume_checkpoint,
                                 resume_checkpoint_sha256=args.resume_checkpoint_sha256)
        print(json.dumps({"status": result["status"], "elapsed_ms": result["elapsed_ms"]}))
    else:
        parser.error("Specify --prepare-only or --output")


if __name__ == "__main__":
    main()
