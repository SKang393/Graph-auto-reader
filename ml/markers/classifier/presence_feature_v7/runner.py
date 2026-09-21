# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Refine existing features with presence-only targets masked from other heads."""
from __future__ import annotations

import argparse
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
from ..metrics import supervised_embedding_loss
from ..model import load_checkpoint, save_checkpoint
from ..native_context_cache import sha256, write_json
from ..native_context_evaluation import create_session, infer, summarize
from ..native_context_v3.runner import WorkBudget, tensors, torch_outputs
from ..presence_context_cache import SOURCE_PATHS, load
from ..runtime_v2 import PARITY_TOLERANCE, ProbabilityPackedRuntimeClassifier
from ..structural_context_v5.runner import RUNNER_SOURCES as BASE_SOURCES

TASK = "marker-classifier"
REVISION = "marker-classifier-presence-feature-v7"
CONFIG_PATH = Path("ml/markers/classifier/presence_feature_v7/p1.json")
RUNNER_SOURCES = tuple(dict.fromkeys((*BASE_SOURCES, *(Path(p) for p in SOURCE_PATHS),
    Path("ml/markers/classifier/presence_feature_v7/runner.py"),
    Path("ml/markers/classifier/presence_feature_v7/__init__.py"),
    Path("ml/markers/classifier/presence_feature_v7/protocol.json"))))
INITIALIZER_SHA256 = "92546ffffb7c863533a70069a4621e0d61b9c41a886cb1d40c32cb1079a5ea67"
RECIPE = {"epochs": 12, "batch_size": 128, "learning_rate": .0001,
          "weight_decay": .0001, "seed": 20260924,
          "trainable_parameters": "existing_encoder_and_all_heads",
          "presence_only_auxiliary_targets": "masked_no_shape_fill_or_embedding_labels",
          "checkpoint_selection": "fixed_final_epoch_no_dev_selection"}


def validate_config(config):
    if any(config.get(k) != v for k, v in RECIPE.items()):
        raise ValueError("Presence-feature recipe changed")
    if config.get("source_checkpoint_sha256") != INITIALIZER_SHA256:
        raise ValueError("Presence-feature initializer changed")
    if config.get("private_reads") != 0 or config.get("sealed_runs_authorized") != 0:
        raise ValueError("Only owned training and development are authorized")


def training_tensors(base_pixels, base_rows, presence_pixels, presence_rows):
    if len(presence_pixels) != len(presence_rows) or any(
            row.get("split") != "train" or "shape_target_indices" in row or "fill_target_index" in row
            or row.get("target_kind") != "presence_only" or row.get("shape") is not None or row.get("fill") is not None
            for row in presence_rows):
        raise ValueError("Presence rows must be training-only with no invented auxiliary targets")
    previous = tensors(base_pixels, base_rows)
    count = len(presence_rows)
    return (torch.cat((previous[0], torch.from_numpy(presence_pixels))),
            torch.cat((previous[1], torch.zeros(count, 9))),
            torch.cat((previous[2], torch.full((count,), -1, dtype=torch.long))),
            torch.cat((previous[3], torch.tensor([r["artifact"] for r in presence_rows], dtype=torch.float32))),
            torch.cat((previous[4], torch.zeros(count, dtype=torch.bool))),
            torch.cat((torch.ones(len(base_rows), dtype=torch.bool), torch.zeros(count, dtype=torch.bool))))


def loss_terms(outputs, shapes, fills, artifacts, unambiguous, auxiliary_known):
    shape, fill, artifact, embedding = outputs
    marker = artifacts < .5
    known_marker = marker & auxiliary_known
    zero = shape.sum() * 0 + fill.sum() * 0 + embedding.sum() * 0
    shape_loss = functional.cross_entropy(shape[known_marker], shapes[known_marker]) if known_marker.any() else zero
    fill_loss = functional.cross_entropy(fill[known_marker], fills[known_marker]) if known_marker.any() else zero
    per_item = functional.binary_cross_entropy_with_logits(artifact[:, 0], artifacts, reduction="none")
    artifact_loss = torch.stack([per_item[mask].mean() for mask in (marker, ~marker) if mask.any()]).mean()
    identities = known_marker & unambiguous
    embedding_loss = supervised_embedding_loss(
        embedding[identities], shapes[identities].argmax(1) * 3 + fills[identities])
    # Preserve V5's weighting. The isolated change is allowing the existing
    # feature extractor to learn the already fixed presence-only extension.
    total = shape_loss + .75 * fill_loss + .65 * artifact_loss + .08 * embedding_loss
    return total, (shape_loss, fill_loss, artifact_loss, embedding_loss)


def run(root: Path, output: Path):
    started = time.perf_counter()
    config = json.loads((root / CONFIG_PATH).read_text(encoding="utf-8"))
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
        budget = WorkBudget(output / "CANCEL")
        with budget.work_block():
            checkpoint = root / config["source_checkpoint_path"]
            if sha256(checkpoint) != INITIALIZER_SHA256:
                raise ValueError("Source checkpoint bytes changed")
            model, payload = load_checkpoint(checkpoint)
            if (payload["shape_temperature"], payload["fill_temperature"]) != (.7, .7):
                raise ValueError("Classifier probability temperatures changed")
            base, presence_pixels, presence_rows, manifest = load(root, root / config["cache_path"], config["cache_manifest_sha256"])
            for source, expected in manifest["source_sha256"].items():
                if sha256(root / source) != expected or sha256(root / config["cache_path"] / "source-snapshot" / source) != expected:
                    raise ValueError("Presence preparation source snapshot changed")
            training = training_tensors(*base["train"], presence_pixels, presence_rows)
            optimizer = torch.optim.AdamW(model.parameters(), lr=config["learning_rate"], weight_decay=config["weight_decay"])
        history, optimizer_steps = [], 0
        for epoch in range(config["epochs"]):
            model.train()
            with budget.work_block():
                order = torch.randperm(len(training[0]), generator=torch.Generator().manual_seed(config["seed"] + epoch))
            losses = []
            for start in range(0, len(order), config["batch_size"]):
                with budget.work_block():
                    index = order[start:start + config["batch_size"]]
                    inputs, *targets = (value[index] for value in training)
                    optimizer.zero_grad(set_to_none=True)
                    loss, _ = loss_terms(model(inputs), *targets)
                    if not torch.isfinite(loss):
                        raise RuntimeError("Non-finite training loss")
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(model.parameters(), 5.)
                    optimizer.step()
                    optimizer_steps += 1
                    losses.append(float(loss.detach()))
            history.append({"epoch": epoch + 1, "loss": float(np.mean(losses))})
            (output / "progress.json").write_text(json.dumps({"status": "training", "epoch": epoch + 1,
                "epochs": config["epochs"], "optimizer_steps": optimizer_steps}) + "\n", encoding="utf-8")
        model.eval()
        with budget.work_block():
            saved, exported = output / "marker-classifier.pt", output / "marker-classifier.onnx"
            save_checkpoint(saved, model, dataset_manifest_sha256=config["cache_manifest_sha256"],
                shape_temperature=.7, fill_temperature=.7, training_revision=REVISION)
            runtime = ProbabilityPackedRuntimeClassifier(model, .7, .7).eval()
            torch.onnx.export(runtime, training[0][:8], exported,
                input_names=["marker_patch"], output_names=["classification_probabilities"],
                dynamic_axes={"marker_patch": {0: "batch"}, "classification_probabilities": {0: "batch"}},
                opset_version=18, dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            session = create_session(exported)
        results, parity = {}, {}
        for split, (pixels, rows) in {**base, "presence": (presence_pixels, presence_rows)}.items():
            expected = torch_outputs(runtime, pixels, budget, config["batch_size"])
            actual = infer(session, pixels, budget, config["batch_size"])
            with budget.work_block():
                parity[split] = {"count": len(rows), "maximum_absolute_error": float(np.abs(expected - actual).max()),
                    "shape_decision_changes": int((expected[:, :9].argmax(1) != actual[:, :9].argmax(1)).sum()),
                    "fill_decision_changes": int((expected[:, 9:12].argmax(1) != actual[:, 9:12].argmax(1)).sum()),
                    "artifact_decision_changes": int(((expected[:, 12] >= .5) != (actual[:, 12] >= .5)).sum())}
                np.save(output / f"{split}-outputs.npy", actual, allow_pickle=False)
                if split != "presence":
                    results[split] = summarize(rows, actual)
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
            "config_sha256": sha256(root / CONFIG_PATH), "cache_manifest_sha256": config["cache_manifest_sha256"],
            "source_checkpoint_sha256": INITIALIZER_SHA256, "checkpoint_sha256": sha256(saved),
            "onnx_sha256": sha256(exported), "splits": results, "parity": parity, "parity_tolerance": PARITY_TOLERANCE,
            "training_history": history, "optimizer_steps": optimizer_steps,
            "checkpoint_selection": RECIPE["checkpoint_selection"], "cpu": budget.report(),
            "trainable_parameter_count": sum(p.numel() for p in model.parameters() if p.requires_grad),
            "auxiliary_unknown_rows": len(presence_rows), "torch_threads": torch.get_num_threads(),
            "seconds": time.perf_counter() - started, "private_reads": 0, "sealed_reads": 0,
            "budget_consumed": False, "production_approval": False}
        write_json(output / "report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256(output / "report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output / "exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
        void_candidate(authorization, error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(Path.cwd(), args.output)
    print(json.dumps({k: result[k] for k in ("status", "seconds", "optimizer_steps", "trainable_parameter_count")}))
