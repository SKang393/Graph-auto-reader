# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Classifier diagnostics at the unchanged shipped artifact threshold."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import time

import numpy as np
import onnxruntime as ort

from ml.policy.evidence_policy import tier1_acceptance_bars
from ml.training_cpu_budget import TrainingCpuBudget
from .native_context_cache import load_cache, sha256, write_json

ARTIFACT_THRESHOLD = .5


def summarize(rows: list[dict], outputs: np.ndarray) -> dict:
    if outputs.shape != (len(rows), 25) or not len(rows) or not np.isfinite(outputs).all():
        raise ValueError("Classifier output inventory or values are invalid")
    if ((outputs[:, :13] < 0).any() or (outputs[:, :13] > 1).any() or
            not np.allclose(outputs[:, :9].sum(1), 1, atol=1e-5) or
            not np.allclose(outputs[:, 9:12].sum(1), 1, atol=1e-5)):
        raise ValueError("Classifier probability contract changed")
    marker = np.array([not row["artifact"] for row in rows])
    shape = np.array([row["shape_index"] for row in rows])
    fill = np.array([row["fill_target_index"] for row in rows])
    shape_correct = outputs[:, :9].argmax(1) == shape
    fill_correct = outputs[:, 9:12].argmax(1) == fill

    def counts(mask: np.ndarray, threshold: float = ARTIFACT_THRESHOLD) -> dict:
        positive, negative = mask & marker, mask & ~marker
        accepted = mask & (outputs[:, 12] < threshold)
        true_accepted, false_accepted = int((accepted & marker).sum()), int((accepted & ~marker).sum())
        n, a = int(positive.sum()), int(negative.sum())
        return {
            "count": int(mask.sum()), "markers": n, "artifacts": a,
            "shape_correct": int((positive & shape_correct).sum()),
            "shape_accuracy": float(shape_correct[positive].mean()) if n else None,
            "fill_correct": int((positive & fill_correct).sum()),
            "fill_accuracy": float(fill_correct[positive].mean()) if n else None,
            "true_markers_retained": true_accepted, "artifacts_accepted": false_accepted,
            "retained_marker_recall": true_accepted / n if n else None,
            "retained_marker_precision": true_accepted / (true_accepted + false_accepted) if accepted.any() else 0.,
            "prohibited_structure_hit_rate": false_accepted / a if a else None,
            "retained_correct_shape": int((accepted & marker & shape_correct).sum()),
            "shape_ambiguous_rows": sum(row["shape_label_ambiguous"] for row, include in zip(rows, positive) if include),
            "fill_ambiguous_rows": sum(row["fill_label_ambiguous"] for row, include in zip(rows, positive) if include),
        }

    all_rows = np.ones(len(rows), dtype=bool)
    totals = counts(all_rows)
    bars = tier1_acceptance_bars()
    gates = {
        "shape": totals["shape_accuracy"] is not None and totals["shape_accuracy"] >= bars["marker_shape_accuracy_minimum"],
        "fill": totals["fill_accuracy"] is not None and totals["fill_accuracy"] >= bars["marker_fill_accuracy_minimum"],
        "retention_recall": totals["retained_marker_recall"] is not None and totals["retained_marker_recall"] >= bars["marker_center_recall_minimum"],
        "retention_precision": totals["retained_marker_precision"] >= bars["marker_center_precision_minimum"],
        "prohibited_structure_hits": totals["prohibited_structure_hit_rate"] is not None and totals["prohibited_structure_hit_rate"] <= bars["prohibited_structure_hit_rate_maximum"],
    }
    return {
        "totals": totals, "gates": gates, "clears_shared_dev_bars": all(gates.values()),
        "artifact_threshold": ARTIFACT_THRESHOLD,
        "acceptance_bars": "ml/policy/acceptance-bars.json",
        "grouped": {key: {str(value): counts(np.array([row[key] == value for row in rows]))
                          for value in sorted({row[key] for row in rows}, key=str)}
                    for key in ("family", "context", "shape")},
        "sensitivity_descriptive_only": {str(t): counts(all_rows, t) for t in (.4, .6)},
        "scope": "Patch classification and rejection only; does not prove center proposal recall or complete workflow acceptance",
    }


def create_session(model: Path) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = int(os.environ.get("OMP_NUM_THREADS", os.cpu_count() or 1))
    options.inter_op_num_threads = 1
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    return ort.InferenceSession(model.read_bytes(), options, providers=["CPUExecutionProvider"])


def infer(session: ort.InferenceSession, pixels: np.ndarray, budget: TrainingCpuBudget, batch_size: int = 128) -> np.ndarray:
    outputs = []
    for start in range(0, len(pixels), batch_size):
        with budget.work_block():
            outputs.append(session.run(["classification_probabilities"], {"marker_patch": pixels[start:start+batch_size]})[0])
    return np.concatenate(outputs)


def baseline(cache: Path, cache_sha256: str, model: Path, model_sha256: str, output: Path) -> dict:
    started = time.perf_counter()
    if sha256(model) != model_sha256:
        raise ValueError("Classifier baseline checksum mismatch")
    output.mkdir(parents=True, exist_ok=False)
    budget = TrainingCpuBudget()
    with budget.work_block():
        data = load_cache(cache, cache_sha256)
        session = create_session(model)
    results = {}
    for split, (pixels, rows) in data.items():
        values = infer(session, pixels, budget)
        with budget.work_block():
            np.save(output / f"{split}-outputs.npy", values, allow_pickle=False)
            results[split] = summarize(rows, values)
    report = {
        "status": "baseline_only", "model_sha256": model_sha256,
        "cache_manifest_sha256": cache_sha256, "splits": results,
        "seconds": time.perf_counter() - started, "optimizer_steps": 0,
        "private_reads": 0, "sealed_reads": 0, "production_approval": False,
        "output_sha256": {split: sha256(output / f"{split}-outputs.npy") for split in data},
    }
    write_json(output / "report.json", report)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("cache", "model", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    for name in ("cache-sha256", "model-sha256"):
        parser.add_argument(f"--{name}", required=True)
    args = parser.parse_args()
    report = baseline(args.cache, args.cache_sha256, args.model, args.model_sha256, args.output)
    print(json.dumps({split: metrics["totals"] for split, metrics in report["splits"].items()}))
