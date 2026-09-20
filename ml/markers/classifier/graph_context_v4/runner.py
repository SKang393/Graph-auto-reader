# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Owned graph-context adaptation; immutable previous dev, canonical admission."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import random
import time
import traceback

import numpy as np
import onnx
import torch

from ml.markers.gate_seal import verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ..graph_context_cache import SOURCE_PATHS, load_cache
from ..native_context_cache import sha256, write_json
from ..native_context_evaluation import create_session, infer, summarize
from ..native_context_v3.runner import (
    RUNNER_SOURCES as BASE_SOURCES, WorkBudget, tensors, loss_terms, torch_outputs)
from ..model import load_checkpoint, save_checkpoint
from ..runtime_v2 import PARITY_TOLERANCE, ProbabilityPackedRuntimeClassifier

TASK = "marker-classifier"
REVISION = "marker-classifier-graph-context-v4"
CONFIG_PATH = Path("ml/markers/classifier/graph_context_v4/p1.json")
RUNNER_SOURCES = tuple(dict.fromkeys((*BASE_SOURCES, *(Path(p) for p in SOURCE_PATHS),
    Path("ml/markers/classifier/graph_context_v4/runner.py"),
    Path("ml/markers/classifier/graph_context_v4/__init__.py"),
    Path("ml/markers/classifier/graph_context_v4/protocol.json"))))


def preflight(repo: Path, config: dict, budget: WorkBudget):
    if (config["task"], config["revision"], config["candidate_id"]) != (TASK, REVISION, "P1"):
        raise ValueError("Classifier candidate identity changed")
    if (os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or
            os.environ.get("GOAL22_TRAINING_DUTY_PERCENT") != "80" or
            os.environ.get("OMP_WAIT_POLICY") != "PASSIVE" or os.environ.get("KMP_BLOCKTIME") != "0"):
        raise RuntimeError("Launch through the verified CPU guard with passive worker waits")
    threads = int(os.environ["OMP_NUM_THREADS"])
    if threads < 1 or threads != os.cpu_count():
        raise RuntimeError("Training must leave all host logical processors eligible")
    torch.set_num_threads(threads)
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)
    random.seed(config["seed"])
    np.random.seed(config["seed"])
    torch.manual_seed(config["seed"])
    with budget.work_block():
        checkpoint = repo / config["source_checkpoint_path"]
        if sha256(checkpoint) != config["source_checkpoint_sha256"]:
            raise ValueError("Authenticated source checkpoint changed")
        cache_path = repo / config["cache_path"]
        data = load_cache(cache_path, config["cache_manifest_sha256"])
        manifest = json.loads((cache_path / "manifest.json").read_text(encoding="utf-8"))
        for source, expected in manifest["source_sha256"].items():
            if sha256(repo / source) != expected or sha256(cache_path / "source-snapshot" / source) != expected:
                raise ValueError("Native-context source snapshot changed")
        model, payload = load_checkpoint(checkpoint)
        if (payload["shape_temperature"], payload["fill_temperature"]) != (.7, .7):
            raise ValueError("Source probability temperatures changed")
        training_tensors = tensors(*data["train"])
    return model, payload, data, training_tensors


def run(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    config = json.loads((repo / CONFIG_PATH).read_text(encoding="utf-8"))
    authorization = acquire_training_candidate(repo, task=TASK, revision=REVISION,
        candidate_id="P1", config_path=CONFIG_PATH, runner_source_paths=RUNNER_SOURCES)
    try:
        output.mkdir(parents=True, exist_ok=False)
        budget = WorkBudget(output / "CANCEL")
        model, payload, data, training = preflight(repo, config, budget)
        with budget.work_block():
            optimizer = torch.optim.AdamW(model.parameters(), lr=config["learning_rate"], weight_decay=config["weight_decay"])
        history, optimizer_steps = [], 0
        for epoch in range(config["epochs"]):
            model.train()
            with budget.work_block():
                order = torch.randperm(len(training[0]), generator=torch.Generator().manual_seed(config["seed"] + epoch))
            losses = []
            for start in range(0, len(order), config["batch_size"]):
                with budget.work_block():
                    index = order[start:start+config["batch_size"]]
                    inputs, shapes, fills, artifacts, unambiguous = (value[index] for value in training)
                    optimizer.zero_grad(set_to_none=True)
                    loss, _ = loss_terms(model(inputs), shapes, fills, artifacts, unambiguous)
                    if not torch.isfinite(loss):
                        raise RuntimeError("Non-finite training loss")
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(model.parameters(), 5.)
                    optimizer.step()
                    optimizer_steps += 1
                    losses.append(float(loss.detach()))
            history.append({"epoch": epoch+1, "loss": float(np.mean(losses))})
            (output / "progress.json").write_text(json.dumps({"status": "training", "epoch": epoch+1,
                "epochs": config["epochs"], "optimizer_steps": optimizer_steps}) + "\n", encoding="utf-8")
        model.eval()
        checkpoint, exported = output / "marker-classifier.pt", output / "marker-classifier.onnx"
        with budget.work_block():
            save_checkpoint(checkpoint, model, dataset_manifest_sha256=config["cache_manifest_sha256"],
                shape_temperature=.7, fill_temperature=.7, training_revision=REVISION)
            runtime = ProbabilityPackedRuntimeClassifier(model, .7, .7).eval()
            torch.onnx.export(runtime, training[0][:8], exported, input_names=["marker_patch"],
                output_names=["classification_probabilities"],
                dynamic_axes={"marker_patch": {0: "batch"}, "classification_probabilities": {0: "batch"}},
                opset_version=18, dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            session = create_session(exported)
        results, parity = {}, {}
        for split, (pixels, rows) in data.items():
            expected = torch_outputs(runtime, pixels, budget, config["batch_size"])
            actual = infer(session, pixels, budget, config["batch_size"])
            with budget.work_block():
                error = float(np.max(np.abs(expected - actual)))
                parity[split] = {"count": len(rows), "maximum_absolute_error": error,
                    "shape_decision_changes": int((expected[:, :9].argmax(1) != actual[:, :9].argmax(1)).sum()),
                    "fill_decision_changes": int((expected[:, 9:12].argmax(1) != actual[:, 9:12].argmax(1)).sum()),
                    "artifact_decision_changes": int(((expected[:, 12] >= .5) != (actual[:, 12] >= .5)).sum())}
                np.save(output / f"{split}-outputs.npy", actual, allow_pickle=False)
                results[split] = summarize(rows, actual)
        if max(item["maximum_absolute_error"] for item in parity.values()) > PARITY_TOLERANCE:
            raise RuntimeError("Classifier export failed the unchanged numerical parity tolerance")
        verify_bound_source_snapshot(repo, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {
            "status": "dev_pass" if results["dev"]["clears_shared_dev_bars"] else "failed_dev_unconsumed",
            "task": TASK, "revision": REVISION, "candidate_id": "P1", "binding": authorization.binding,
            "config_sha256": sha256(repo / CONFIG_PATH), "cache_manifest_sha256": config["cache_manifest_sha256"],
            "source_checkpoint_sha256": config["source_checkpoint_sha256"],
            "checkpoint_sha256": sha256(checkpoint), "onnx_sha256": sha256(exported),
            "splits": results, "parity": parity, "parity_tolerance": PARITY_TOLERANCE,
            "epochs": config["epochs"], "checkpoint_selection": "fixed_final_epoch_no_dev_selection",
            "optimizer_steps": optimizer_steps, "training_history": history, "cpu": budget.report(),
            "torch_threads": torch.get_num_threads(), "worker_wait_policy": os.environ["OMP_WAIT_POLICY"],
            "seconds": time.perf_counter()-started, "private_reads": 0, "sealed_reads": 0,
            "budget_consumed": False, "production_approval": False, "release_eligible": False,
        }
        write_json(output / "report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256(output / "report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output / "exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
        void_candidate(authorization, error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(Path.cwd(), args.output)
    print(json.dumps({"status": result["status"], "seconds": result["seconds"], "dev": result["splits"]["dev"]["totals"]}))
