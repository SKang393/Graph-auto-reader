# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded V27 warm-start adaptation with train-only owned shape coverage."""
from __future__ import annotations

import argparse
import json
import math
import os
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
from ..shape_coverage_cache import SOURCE_PATHS, load_cache, write_json
from ..shape_coverage_evaluation import evaluate
from ..component_diversity_v27.train_p1 import _atomic_torch_save
from ..scale_classifier_v16.model import ScaleClassifierNet, ModelConfig
from ..focal_confidence_v21.focal_loss import v21_loss

TASK = "marker-center"
REVISION = "marker-center-shape-coverage-v28"
CONFIG_PATH = Path("ml/markers/center/shape_coverage_v28/p1.json")
RUNNER_SOURCES = tuple(dict.fromkeys((*(Path(p) for p in SOURCE_PATHS), *(
    Path(p) for p in (
        "ml/markers/center/shape_coverage_v28/runner.py",
        "ml/markers/center/shape_coverage_v28/__init__.py",
        "ml/markers/center/shape_coverage_v28/protocol.json",
        "ml/markers/center/shape_coverage_evaluation.py",
        "ml/markers/center/balanced_support_diagnostic.py",
        "ml/markers/center/enclosed_support_diagnostic.py",
        "ml/markers/center/component_diversity_v27/diagnose_head_swap.py",
        "ml/markers/center/component_diversity_v27/diagnose_score_localization.py",
        "ml/markers/center/plot_domain_v25/diagnose_dev.py",
        "ml/markers/center/localization_confidence_v26/diagnose_suppressor_anchors.py",
        "ml/markers/training_budget.py", "ml/markers/gate_seal.py",
        "ml/policy/evidence_policy.py", "ml/policy/evidence-policy.json", "ml/policy/acceptance-bars.json")))))
RECIPE = {"seed": 20260922, "epochs": 8, "batch_size": 128,
          "learning_rate": .0003, "weight_decay": .0001,
          "positive_loss_weight": 16., "hard_negative_loss_weight": 5.,
          "focal_alpha": .25, "focal_gamma": 2., "confidence_threshold": .25,
          "checkpoint_selection": "fixed_final_epoch_no_dev_selection"}


def configure_runtime() -> None:
    if (os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80"
            or os.environ.get("GOAL22_TRAINING_DUTY_PERCENT") != "80"
            or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE"
            or os.environ.get("KMP_BLOCKTIME") != "0"):
        raise RuntimeError("Use the verified CPU guard with passive worker waits")
    threads = int(os.environ["OMP_NUM_THREADS"])
    if threads != os.cpu_count():
        raise RuntimeError("All host logical processors must remain eligible")
    torch.set_num_threads(threads)
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)
    torch.manual_seed(RECIPE["seed"])
    random.seed(RECIPE["seed"])
    np.random.seed(RECIPE["seed"])


def recovery_binding(config: dict, row_count: int) -> dict:
    return {key: config[key] for key in (
        "task", "revision", "candidate_id", "recipe", "cache_manifest_sha256",
        "source_checkpoint_sha256", "expected_runner_source_bundle_sha256")} | {"training_rows": row_count}


def train_epochs(model, optimizer, training, recipe, binding, output, budget, *, resume=None):
    history, steps, resumed = [], 0, 0
    if resume is not None:
        payload = torch.load(resume, map_location="cpu", weights_only=True)
        if payload.get("binding") != binding:
            raise ValueError("Recovery belongs to a different training recipe or population")
        resumed = payload["completed_epochs"]
        if type(resumed) is not int or not 0 <= resumed <= recipe["epochs"]:
            raise ValueError("Invalid recovery epoch")
        history, steps = payload["history"], payload["optimizer_steps"]
        if len(history) != resumed or steps != resumed*math.ceil(len(training[0])/recipe["batch_size"]):
            raise ValueError("Recovery step count changed")
        model.load_state_dict(payload["state_dict"])
        optimizer.load_state_dict(payload["optimizer_state_dict"])
    for epoch in range(resumed, recipe["epochs"]):
        model.train()
        with budget.work_block():
            order = torch.randperm(len(training[0]), generator=torch.Generator().manual_seed(recipe["seed"]+epoch))
        losses = []
        for start in range(0, len(order), recipe["batch_size"]):
            with budget.work_block():
                index = order[start:start+recipe["batch_size"]]
                patches, labels, offsets, radii, hard = (t[index] for t in training)
                loss = v21_loss(model.forward_raw(patches), labels, offsets, radii, hard.bool(),
                    positive_weight=recipe["positive_loss_weight"], hard_weight=recipe["hard_negative_loss_weight"],
                    alpha=recipe["focal_alpha"], gamma=recipe["focal_gamma"])
                if not torch.isfinite(loss):
                    raise RuntimeError("Non-finite center training loss")
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 5.)
                optimizer.step()
                steps += 1
                losses.append(float(loss.detach()))
        history.append({"epoch": epoch+1, "optimizer_steps": steps, "mean_loss": float(np.mean(losses))})
        with budget.work_block():
            _atomic_torch_save(output/"recovery.pt", {"binding": binding,
                "completed_epochs": epoch+1, "optimizer_steps": steps, "history": history,
                "state_dict": model.state_dict(), "optimizer_state_dict": optimizer.state_dict()})
        write_json(output/"progress.json", {"status": "training", "epoch": epoch+1,
            "epochs": recipe["epochs"], "optimizer_steps": steps,
            "recovery_sha256": sha256_file(output/"recovery.pt")})
    return {"completed_epochs": recipe["epochs"], "optimizer_steps": steps,
            "resumed_from_epoch": resumed, "history": history}


def run(repo: Path, output: Path, *, resume: Path | None = None, resume_sha256: str | None = None) -> dict:
    started = time.perf_counter()
    config = json.loads((repo/CONFIG_PATH).read_text())
    if config["recipe"] != RECIPE:
        raise ValueError("Registered adaptation recipe changed")
    if (resume is None) != (resume_sha256 is None) or (resume is not None and sha256_file(resume) != resume_sha256):
        raise ValueError("Recovery checkpoint requires its exact recorded checksum")
    authorization = acquire_training_candidate(repo, task=TASK, revision=REVISION,
        candidate_id="P1", config_path=CONFIG_PATH, runner_source_paths=RUNNER_SOURCES)
    try:
        output.mkdir(parents=True, exist_ok=False)
        configure_runtime()
        budget = WorkBudget(output/"CANCEL")
        with budget.work_block():
            scopes, dev, manifest = load_cache(repo/config["cache_path"], config["cache_manifest_sha256"])
            for name, digest in manifest["source_sha256"].items():
                if sha256_file(repo/name) != digest or sha256_file(repo/config["cache_path"]/"source-snapshot"/name) != digest:
                    raise ValueError("Coverage preparation source changed")
            initializer = repo/config["source_checkpoint_path"]
            if sha256_file(initializer) != config["source_checkpoint_sha256"]:
                raise ValueError("V27 checkpoint changed")
            model = ScaleClassifierNet(ModelConfig())
            model.load_state_dict(torch.load(initializer, map_location="cpu", weights_only=True)["state_dict"])
            training = tuple(torch.cat([values[i] for values in scopes.values()]) for i in range(5))
            del scopes
            if len(training[0]) != config["training_rows"]:
                raise ValueError("The complete training population changed")
            optimizer = torch.optim.AdamW(model.parameters(), lr=RECIPE["learning_rate"], weight_decay=RECIPE["weight_decay"])
        training_report = train_epochs(model, optimizer, training, RECIPE,
            recovery_binding(config, len(training[0])), output, budget, resume=resume)
        if training_report["optimizer_steps"] != config["optimizer_steps_expected"]:
            raise ValueError("Training step count differs from the registered candidate")
        del training, optimizer
        model.eval()
        checkpoint, exported = output/"marker-center.pt", output/"marker-center.onnx"
        with budget.work_block():
            torch.save({"state_dict": model.state_dict(), "config": model.export_contract()}, checkpoint)
            torch.onnx.export(model, torch.zeros(1, 3, 33, 33), exported,
                input_names=["candidate_patches"], output_names=["candidate_predictions"],
                dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
                opset_version=18, dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            options = ort.SessionOptions()
            options.intra_op_num_threads = 1
            options.inter_op_num_threads = 1
            options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
            session = ort.InferenceSession(str(exported), sess_options=options, providers=["CPUExecutionProvider"])
        write_json(output/"progress.json", {"status": "development_evaluation", **training_report})
        development = evaluate(repo, dev, model, session, budget, output)
        if development["parity"]["maximum_absolute_error"] > 1e-5 or development["parity"]["confidence_decision_changes"]:
            raise RuntimeError("The exported model failed numerical or operating-decision parity")
        verify_bound_source_snapshot(repo, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"task": TASK, "revision": REVISION, "candidate_id": "P1",
            "status": "dev_pass" if development["clears_shared_dev_bars"] else "failed_dev_unconsumed",
            "binding": authorization.binding, "config_sha256": sha256_file(repo/CONFIG_PATH),
            "checkpoint_sha256": sha256_file(checkpoint), "onnx_sha256": sha256_file(exported),
            "cache_manifest_sha256": config["cache_manifest_sha256"], "source_checkpoint_sha256": config["source_checkpoint_sha256"],
            "training": training_report, "development": development, "cpu": budget.report(),
            "torch_threads": torch.get_num_threads(), "onnx_threads": 1,
            "seconds": time.perf_counter()-started, "private_reads": 0, "sealed_reads": 0,
            "budget_consumed": False, "production_approval": False, "release_eligible": False}
        write_json(output/"report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256_file(output/"report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output/"exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
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
