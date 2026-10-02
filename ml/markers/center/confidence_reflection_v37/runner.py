# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fit the unchanged spatial confidence projection on fixed reflections."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import random
import time
import traceback

import numpy as np
import onnx
import onnxruntime as ort
import torch

from ml.markers.gate_seal import sha256_file, verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ..confidence_spatial_v35 import runner as spatial
from ..confidence_bce_v33.runner import train_epochs, validate_export_parity, recovery_binding
from ..scale_classifier_v16.model import ScaleClassifierNet, ModelConfig
from ..shape_coverage_cache import unpack_scene, write_json
from ..shape_coverage_v28 import runner as previous
from .. import shape_coverage_evaluation as evaluation
from ..confidence_spatial_v35.model import SpatialCenterNet, TRAINABLE_PARAMETERS
from .cache import load_training
from .augmentation import TRANSFORMS

TASK = "marker-center"
REVISION = "marker-center-confidence-reflection-v37"
CONFIG_PATH = Path("ml/markers/center/confidence_reflection_v37/p1.json")
MODEL_SHA256 = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
TRAINING_ROWS = {f'{scope}:{transform}': count for scope, count in spatial.TRAINING_ROWS.items() for transform in TRANSFORMS}
RECIPE = dict(spatial.RECIPE)
RUNNER_SOURCES = tuple(dict.fromkeys((*spatial.RUNNER_SOURCES, *(Path(p) for p in (
    "ml/markers/center/confidence_reflection_v37/__init__.py",
    "ml/markers/center/confidence_reflection_v37/runner.py",
    "ml/markers/center/confidence_reflection_v37/augmentation.py",
    "ml/markers/center/confidence_reflection_v37/cache.py",
    "ml/markers/center/confidence_reflection_v37/protocol.json")))))


def validate_config(config: dict) -> None:
    if (config.get("task"), config.get("revision"), config.get("candidate_id")) != (TASK, REVISION, "P1"):
        raise ValueError("Candidate identity changed")
    if (config.get("recipe") != RECIPE or config.get("training_group_rows") != TRAINING_ROWS or
            config.get("source_checkpoint_sha256") != MODEL_SHA256 or config.get("private_reads") != 0 or
            config.get("sealed_runs_authorized") != 0 or config.get("production_approval") is not False):
        raise ValueError("Frozen adaptation recipe, initializer, population or read authorization changed")


def run(repo: Path, output: Path, *, resume: Path | None = None, resume_sha256: str | None = None) -> dict:
    started = time.perf_counter()
    config = json.loads((repo / CONFIG_PATH).read_text())
    validate_config(config)
    if (resume is None) != (resume_sha256 is None) or (resume is not None and sha256_file(resume) != resume_sha256):
        raise ValueError("Recovery requires its exact recorded checksum")
    binding = recovery_binding(config, resume, resume_sha256)
    authorization = acquire_training_candidate(repo, task=TASK, revision=REVISION,
        candidate_id="P1", config_path=CONFIG_PATH, runner_source_paths=RUNNER_SOURCES)
    try:
        output.mkdir(parents=True, exist_ok=False)
        previous.configure_runtime()
        torch.manual_seed(RECIPE["seed"])
        random.seed(RECIPE["seed"])
        np.random.seed(RECIPE["seed"])
        if torch.get_num_threads() != 12:
            raise RuntimeError("The registered local run requires twelve CPU threads")
        budget = WorkBudget(output / "CANCEL")
        training, feature_report = load_training(repo, Path(config["feature_report_path"]),
            config["feature_report_sha256"], TRAINING_ROWS, MODEL_SHA256, budget)
        with budget.work_block():
            initializer = repo / config["source_checkpoint_path"]
            if sha256_file(initializer) != MODEL_SHA256:
                raise ValueError("Retained initializer changed")
            base = ScaleClassifierNet(ModelConfig())
            base.load_state_dict(torch.load(initializer, map_location="cpu", weights_only=True)["state_dict"])
            original = {key: value.detach().clone() for key, value in base.state_dict().items()}
            model = SpatialCenterNet(base)
            head = model.confidence
            if sum(p.numel() for p in head.parameters()) != RECIPE["trainable_parameters"]:
                raise ValueError("Confidence-only parameter budget changed")
            optimizer = torch.optim.AdamW(head.parameters(), lr=RECIPE["learning_rate"], weight_decay=RECIPE["weight_decay"])
        offset = 0
        initial_confidence_error = 0.
        initial_confidence_crossings = 0
        for group in feature_report["groups"]:
            raw = np.load(repo / group["raw"]["path"], allow_pickle=False)
            for start in range(0, group["rows"], 256):
                with budget.work_block(), torch.no_grad():
                    features = training[0][offset + start:offset + min(start + 256, group["rows"])]
                    actual = model.base.head[1:](features).numpy()
                    if actual.shape != raw[start:start + 256].shape or not np.allclose(actual, raw[start:start + 256], atol=1e-5, rtol=0):
                        raise ValueError("Cached features do not reconstruct the retained model outputs")
                    original_probability = torch.sigmoid(torch.from_numpy(raw[start:start + 256, 0])).numpy()
                    initial_probability = torch.sigmoid(model.confidence(features)).numpy()
                    initial_confidence_error = max(initial_confidence_error, float(np.abs(initial_probability - original_probability).max()))
                    initial_confidence_crossings += int(np.count_nonzero((initial_probability >= .1) != (original_probability >= .1)))
                    if initial_confidence_error > 1e-5:
                        raise ValueError("Initial confidence projection failed numerical parity")
            offset += group["rows"]
        training_report = train_epochs(head, optimizer, training, RECIPE, binding, output, budget, resume=resume,
            require_completed=config.get("completed_training_recovery") is not None)
        training_report["optimizer_steps_this_execution"] = training_report["optimizer_steps"] - (
            training_report["resumed_from_epoch"] * math.ceil(sum(TRAINING_ROWS.values()) / RECIPE["batch_size"]))
        training_report["training_source_binding"] = binding
        if training_report["optimizer_steps"] != config["optimizer_steps_expected"]:
            raise ValueError("Training step count differs from the fixed recipe")
        model.verify_frozen_state(original)
        del training, optimizer, head
        with budget.work_block():
            dev_ref = config["development_file"]
            if sha256_file(repo / dev_ref["path"]) != dev_ref["sha256"]:
                raise ValueError("Frozen development file changed")
            packed = torch.load(repo / dev_ref["path"], map_location="cpu", weights_only=True)
            if set(packed) != {"component", "family"}:
                raise ValueError("Development membership changed")
            development_scenes = {scope: tuple(unpack_scene(row, expected_split={"component": "dev", "family": "validation"}[scope])
                for row in rows) for scope, rows in packed.items()}
            if {scope: (len(rows), sum(len(row["metadata"]["centers"]) for row in rows)) for scope, rows in packed.items()} != {
                    "component": (167, 2004), "family": (9, 206)}:
                raise ValueError("Complete development denominator changed")
            del packed
            checkpoint, exported = output / "marker-center.pt", output / "marker-center.onnx"
            torch.save({"state_dict": model.state_dict(), "config": model.export_contract()}, checkpoint)
            torch.onnx.export(model, torch.zeros(1, 3, 33, 33), exported,
                input_names=["candidate_patches"], output_names=["candidate_predictions"],
                dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
                opset_version=18, dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            options = ort.SessionOptions()
            options.intra_op_num_threads = torch.get_num_threads()
            options.inter_op_num_threads = 1
            options.add_session_config_entry("session.intra_op.allow_spinning", "0")
            options.add_session_config_entry("session.inter_op.allow_spinning", "0")
            options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
            session = ort.InferenceSession(str(exported), sess_options=options, providers=["CPUExecutionProvider"])
        write_json(output / "progress.json", {"status": "development_evaluation", **training_report})
        development = evaluation.evaluate(repo, development_scenes, model, session, budget, output,
                                          operating_threshold=RECIPE["confidence_threshold"])
        write_json(output / "development.json", development)
        validate_export_parity(development["parity"])
        baseline_report, baseline_cache = evaluation.historical._load_v27_cache(repo)
        geometry_difference = 0.
        for record in development["records"]:
            scope, index = record["scope"], record["scene_index"]
            old = baseline_report[scope + "_dev"]["cache_manifest"][index]
            original_output = baseline_cache[old["v27_candidate_predictions_key"]]
            actual_output = np.load(output / f"{scope}-{index:03d}-predictions.npy", allow_pickle=False)
            geometry_difference = max(geometry_difference, float(np.abs(original_output[:, 1:] - actual_output[:, 1:]).max()))
        if geometry_difference > 1e-5:
            raise RuntimeError("Frozen geometry changed on complete development proposals")
        model.verify_frozen_state(original)
        verify_bound_source_snapshot(repo, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"task": TASK, "revision": REVISION, "candidate_id": "P1",
            "status": "dev_pass" if development["clears_shared_dev_bars"] else "failed_dev_unconsumed",
            "binding": authorization.binding, "config_sha256": sha256_file(repo / CONFIG_PATH),
            "checkpoint_sha256": sha256_file(checkpoint), "onnx_sha256": sha256_file(exported),
            "source_checkpoint_sha256": MODEL_SHA256, "feature_report_sha256": config["feature_report_sha256"],
            "training": training_report, "development": development, "geometry_maximum_absolute_change": geometry_difference,
            "all_retained_parameters_and_normalization_state_unchanged": True,
            "initial_confidence_maximum_absolute_error": initial_confidence_error,
            "initial_confidence_raw_boundary_crossings": initial_confidence_crossings,
            "retained_confidence_parameters_unchanged": True, "trainable_parameters": TRAINABLE_PARAMETERS,
            "cpu": budget.report(), "torch_threads": torch.get_num_threads(), "onnx_threads": options.intra_op_num_threads,
            "seconds": time.perf_counter() - started, "private_reads": 0, "sealed_reads": 0,
            "budget_consumed": False, "production_approval": False, "release_eligible": False}
        write_json(output / "report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256_file(output / "report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output / "exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
        void_candidate(authorization, error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--resume-from", type=Path)
    parser.add_argument("--resume-sha256")
    args = parser.parse_args()
    result = run(Path.cwd(), args.output, resume=args.resume_from, resume_sha256=args.resume_sha256)
    print(json.dumps({"status": result["status"], "seconds": result["seconds"]}))
