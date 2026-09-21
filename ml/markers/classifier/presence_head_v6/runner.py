# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fit only the existing rejection head on frozen features and presence labels."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import time
import traceback

import numpy as np
import onnx
import torch
import torch.nn.functional as functional

from ml.markers.gate_seal import verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ..model import load_checkpoint, save_checkpoint
from ..native_context_cache import sha256, write_json
from ..native_context_evaluation import create_session, infer, summarize
from ..native_context_v3.runner import WorkBudget, torch_outputs
from ..presence_context_cache import SOURCE_PATHS, load
from ..runtime_v2 import PARITY_TOLERANCE, ProbabilityPackedRuntimeClassifier
from ..structural_context_v5.runner import RUNNER_SOURCES as BASE_SOURCES

TASK = "marker-classifier"
REVISION = "marker-classifier-presence-head-v6"
CONFIG_PATH = Path("ml/markers/classifier/presence_head_v6/p1.json")
RUNNER_SOURCES = tuple(dict.fromkeys((*BASE_SOURCES, *(Path(p) for p in SOURCE_PATHS),
    Path("ml/markers/classifier/presence_head_v6/runner.py"),
    Path("ml/markers/classifier/presence_head_v6/__init__.py"),
    Path("ml/markers/classifier/presence_head_v6/protocol.json"))))
INITIALIZER_SHA256 = "92546ffffb7c863533a70069a4621e0d61b9c41a886cb1d40c32cb1079a5ea67"
RECIPE = {"optimizer": "full_batch_lbfgs", "maximum_iterations": 64,
          "maximum_evaluations": 96, "learning_rate": 1., "l2_weight": .0001,
          "batch_size": 128, "seed": 20260923,
          "trainable_parameters": "artifact_head_only", "feature_dropout": "disabled",
          "checkpoint_selection": "fixed_final_optimizer_state_no_dev_selection"}


def validate_config(config):
    if any(config.get(k) != v for k, v in RECIPE.items()):
        raise ValueError("Presence-head recipe changed")
    if config.get("source_checkpoint_sha256") != INITIALIZER_SHA256:
        raise ValueError("Presence-head initializer changed")
    if config.get("private_reads") != 0 or config.get("sealed_runs_authorized") != 0:
        raise ValueError("Only owned training and development are authorized")


def frozen_state(model):
    return {name: hashlib.sha256(value.detach().cpu().numpy().tobytes()).hexdigest()
            for name, value in model.state_dict().items() if not name.startswith("artifact_head.")}


def configure_head_only(model):
    model.eval()
    for name, parameter in model.named_parameters():
        parameter.requires_grad_(name.startswith("artifact_head."))
    names = {name for name, p in model.named_parameters() if p.requires_grad}
    if names != {"artifact_head.weight", "artifact_head.bias"}:
        raise ValueError("Unexpected trainable presence-head parameters")


def features_for(model, pixels, budget):
    chunks = []
    for start in range(0, len(pixels), RECIPE["batch_size"]):
        with budget.work_block(), torch.no_grad():
            chunks.append(model.projection(model.encoder(torch.from_numpy(pixels[start:start+128]))))
    return torch.cat(chunks)


def fit_head(model, features, labels, budget):
    if (features.ndim != 2 or labels.shape != (len(features),) or not torch.isfinite(features).all()
            or not ((labels == 0) | (labels == 1)).all() or not (labels == 0).any() or not (labels == 1).any()):
        raise ValueError("Presence fitting requires finite features and both binary classes")
    optimizer = torch.optim.LBFGS(model.artifact_head.parameters(), lr=RECIPE["learning_rate"],
        max_iter=RECIPE["maximum_iterations"], max_eval=RECIPE["maximum_evaluations"],
        history_size=10, line_search_fn="strong_wolfe")
    losses = []

    def closure():
        with budget.work_block():
            optimizer.zero_grad(set_to_none=True)
            logits = model.artifact_head(features).flatten()
            per_item = functional.binary_cross_entropy_with_logits(logits, labels, reduction="none")
            loss = .5*(per_item[labels == 0].mean()+per_item[labels == 1].mean())
            loss = loss + RECIPE["l2_weight"]*.5*model.artifact_head.weight.square().sum()
            loss.backward()
            losses.append(float(loss.detach()))
        return loss

    optimizer.step(closure)
    state = optimizer.state[next(iter(model.artifact_head.parameters()))]
    return {"iterations": state["n_iter"], "function_evaluations": state["func_evals"], "losses": losses}


def run(root: Path, output: Path):
    started = time.perf_counter()
    config = json.loads((root/CONFIG_PATH).read_text(encoding="utf-8"))
    validate_config(config)
    authorization = acquire_training_candidate(root, task=TASK, revision=REVISION, candidate_id="P1",
        config_path=CONFIG_PATH, runner_source_paths=RUNNER_SOURCES)
    try:
        output.mkdir(parents=True, exist_ok=False)
        if (os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80"
                or os.environ.get("GOAL22_TRAINING_DUTY_PERCENT") != "80"
                or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE"
                or os.environ.get("KMP_BLOCKTIME") != "0"
                or int(os.environ.get("OMP_NUM_THREADS", "0")) != os.cpu_count()):
            raise RuntimeError("Use all eligible processors through the verified 80% CPU guard")
        torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
        torch.set_num_interop_threads(1)
        torch.use_deterministic_algorithms(True)
        torch.manual_seed(RECIPE["seed"])
        budget = WorkBudget(output/"CANCEL")
        with budget.work_block():
            checkpoint = root/config["source_checkpoint_path"]
            if sha256(checkpoint) != INITIALIZER_SHA256:
                raise ValueError("Source checkpoint bytes changed")
            model, payload = load_checkpoint(checkpoint)
            if (payload["shape_temperature"], payload["fill_temperature"]) != (.7, .7):
                raise ValueError("Classifier probability temperatures changed")
            base, pixels, rows, manifest = load(root, root/config["cache_path"], config["cache_manifest_sha256"])
            for source, expected in manifest["source_sha256"].items():
                if sha256(root/source) != expected or sha256(root/config["cache_path"]/"source-snapshot"/source) != expected:
                    raise ValueError("Presence preparation source snapshot changed")
            before = frozen_state(model)
            configure_head_only(model)
        base_features = features_for(model, base["train"][0], budget)
        presence_features = features_for(model, pixels, budget)
        with budget.work_block():
            features = torch.cat((base_features, presence_features))
            labels = torch.tensor([r["artifact"] for r in base["train"][1]+rows], dtype=torch.float32)
        fit = fit_head(model, features, labels, budget)
        del features, base_features, presence_features
        with budget.work_block():
            if frozen_state(model) != before:
                raise ValueError("Presence fitting changed shape, fill, embedding or encoder parameters")
            saved = output/"marker-classifier.pt"
            exported = output/"marker-classifier.onnx"
            save_checkpoint(saved, model, dataset_manifest_sha256=config["cache_manifest_sha256"],
                shape_temperature=.7, fill_temperature=.7, training_revision=REVISION)
            runtime = ProbabilityPackedRuntimeClassifier(model, .7, .7).eval()
            torch.onnx.export(runtime, torch.from_numpy(pixels[:8]), exported,
                input_names=["marker_patch"], output_names=["classification_probabilities"],
                dynamic_axes={"marker_patch": {0: "batch"}, "classification_probabilities": {0: "batch"}},
                opset_version=18, dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            session = create_session(exported)
        results, parity = {}, {}
        for split, (split_pixels, split_rows) in {**base, "presence": (pixels, rows)}.items():
            expected = torch_outputs(runtime, split_pixels, budget, 128)
            actual = infer(session, split_pixels, budget)
            with budget.work_block():
                parity[split] = {"count": len(split_rows), "maximum_absolute_error": float(np.abs(expected-actual).max()),
                    "shape_decision_changes": int((expected[:, :9].argmax(1) != actual[:, :9].argmax(1)).sum()),
                    "fill_decision_changes": int((expected[:, 9:12].argmax(1) != actual[:, 9:12].argmax(1)).sum()),
                    "artifact_decision_changes": int(((expected[:, 12] >= .5) != (actual[:, 12] >= .5)).sum())}
                np.save(output/f"{split}-outputs.npy", actual, allow_pickle=False)
                if split != "presence":
                    results[split] = summarize(split_rows, actual)
                else:
                    target = np.array([r["artifact"] for r in rows])
                    results[split] = {"count": len(rows), "marker_count": int((~target).sum()),
                        "markers_retained": int(((~target) & (actual[:, 12] < .5)).sum()),
                        "artifact_count": int(target.sum()), "artifacts_accepted": int((target & (actual[:, 12] < .5)).sum()),
                        "shape_and_fill_labels": "unspecified_not_trained_or_scored"}
        if max(p["maximum_absolute_error"] for p in parity.values()) > PARITY_TOLERANCE:
            raise RuntimeError("Unchanged classifier numerical parity tolerance failed")
        verify_bound_source_snapshot(root, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"status": "dev_pass" if results["dev"]["clears_shared_dev_bars"] else "failed_dev_unconsumed",
            "task": TASK, "revision": REVISION, "candidate_id": "P1", "binding": authorization.binding,
            "config_sha256": sha256(root/CONFIG_PATH), "cache_manifest_sha256": config["cache_manifest_sha256"],
            "source_checkpoint_sha256": INITIALIZER_SHA256, "checkpoint_sha256": sha256(saved),
            "onnx_sha256": sha256(exported), "splits": results, "parity": parity, "parity_tolerance": PARITY_TOLERANCE,
            "fit": fit, "optimizer_steps": fit["iterations"], "checkpoint_selection": RECIPE["checkpoint_selection"],
            "unchanged_non_artifact_state_sha256": before, "cpu": budget.report(),
            "trainable_parameter_count": sum(p.numel() for p in model.parameters() if p.requires_grad),
            "torch_threads": torch.get_num_threads(), "seconds": time.perf_counter()-started,
            "private_reads": 0, "sealed_reads": 0, "budget_consumed": False, "production_approval": False}
        write_json(output/"report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256(output/"report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output/"exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
        void_candidate(authorization, error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(Path.cwd(), args.output)
    print(json.dumps({k: result[k] for k in ("status", "seconds", "optimizer_steps", "trainable_parameter_count")}))
