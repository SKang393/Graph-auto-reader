# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reclassify all authenticated V28 development centers without detector reruns."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import sys
import time

import numpy as np
import torch

from ml.markers.center.cascade_component_diagnostic import INPUTS, counts, retain, sha, write
from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction
from ml.markers.center.mask_preserving_v24 import train_p1 as bars
from ml.markers.center.shape_coverage_cache import unpack_scene
from ml.markers.center.shape_coverage_evaluation import summarize_counts
from ml.markers.classifier.native_context_evaluation import create_session, infer
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ml.markers.classifier.runtime_patches import extract_original_patch


def evaluate(root: Path, output: Path):
    started = time.perf_counter()
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the established CPU guard and passive waits")
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output/"CANCEL")
    load = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
    index = load(root/"docs/GOAL-22-EXISTING-MARKER-CANDIDATES.json")["candidates"]["v28"]
    report_path = root/index["report"]["path"]
    if sha(report_path) != index["report"]["sha256"]:
        raise ValueError("Fixed V28 development report changed")
    source_report = load(report_path)
    directory = report_path.parent
    binding_path = directory/"binding.json"
    details_path = directory/"details.json"
    if sha(binding_path) != source_report["binding_sha256"] or sha(details_path) != source_report["details_sha256"]:
        raise ValueError("Fixed V28 development input or cache inventory changed")
    prior_binding = load(binding_path)
    if (prior_binding["center_candidate"] != "v28" or prior_binding["center_threshold"] != .1
            or prior_binding["artifact_threshold"] != .5 or prior_binding["private_reads"] != 0
            or prior_binding["sealed_reads"] != 0):
        raise ValueError("Unexpected fixed development scope")
    trained = root/"artifacts/goal22-runs/classifier-presence-head-v6/P1"
    training = load(trained/"report.json")
    receipt_path = root/"ml/markers/training-seals/marker-classifier/marker-classifier-presence-head-v6/P1/result.json"
    receipt = load(receipt_path)
    if (sha(trained/"report.json") != receipt["report_sha256"]
            or training["revision"] != "marker-classifier-presence-head-v6"
            or training["private_reads"] != 0 or training["sealed_reads"] != 0
            or training["production_approval"] or sha(trained/"marker-classifier.onnx") != training["onnx_sha256"]):
        raise ValueError("Classifier run identity or scope changed")
    sources = [Path(m.__file__).resolve() for name, m in tuple(sys.modules.items())
               if name.startswith("ml.") and getattr(m, "__file__", None)
               and Path(m.__file__).resolve().is_relative_to(root)]
    sources += [Path(__file__).resolve(), root/"ml/policy/acceptance-bars.json"]
    bound = {p.relative_to(root).as_posix(): sha(p) for p in sources}
    bound.update({p: h for p, h in INPUTS.items() if not p.endswith(".onnx")})
    for p in (report_path, binding_path, details_path, receipt_path, trained/"report.json", trained/"marker-classifier.onnx"):
        bound[p.relative_to(root).as_posix()] = sha(p)
    with budget.work_block():
        if any(sha(root/p) != h for p, h in bound.items()):
            raise ValueError("Bound evaluation input changed")
        scenes = torch.load(root/next(p for p in INPUTS if p.endswith("dev.pt")), weights_only=True, map_location="cpu")
        session = create_session(trained/"marker-classifier.onnx")
        records = load(details_path)
    write(output/"binding.json", {"inputs": bound, "center_threshold": .1, "artifact_threshold": .5,
                                 "geometry": prior_binding["geometry"], "native_runtime_parity_proven": False})
    totals, results = {s: Counter() for s in scenes}, []
    maximum_non_artifact_change, decisions = 0., 0
    seen = set()
    for record in records:
        scope, index = record["scope"], record["index"]
        if (scope, index) in seen:
            raise ValueError("Duplicated development scene")
        seen.add((scope, index))
        with budget.work_block():
            path = directory/record["cache"]["path"]
            if path.resolve().parent != directory.resolve() or sha(path) != record["cache"]["sha256"]:
                raise ValueError("Development prediction cache changed")
            archive = np.load(path, allow_pickle=False)
            items = tuple(MarkerPrediction(float(x), float(y), float(r), float(c)) for x, y, r, c in archive["centers"])
            baseline = archive["classification"].copy()
            archive.close()
            value = unpack_scene(scenes[scope][index], expected_split="dev" if scope == "component" else "validation")
            scene = value.scene if hasattr(value, "scene") else value
            pixels = np.stack([extract_original_patch(1-scene.tensor[0].numpy(), (p.x, p.y), p.radius) for p in items])
        actual = infer(session, pixels, budget)
        with budget.work_block():
            kept = retain(items, actual)
            measured = counts(scene, kept, record["metrics"]["balanced"]["proposal_true_positives"])
            totals[scope].update(measured)
            unchanged = list(range(12))+list(range(13, 25))
            maximum_non_artifact_change = max(maximum_non_artifact_change, float(np.abs(actual[:, unchanged]-baseline[:, unchanged]).max()))
            decisions += int((actual[:, :9].argmax(1) != baseline[:, :9].argmax(1)).sum())
            decisions += int((actual[:, 9:12].argmax(1) != baseline[:, 9:12].argmax(1)).sum())
            np.save(output/f"{scope}-{index:03d}.npy", actual, allow_pickle=False)
            results.append({"scope": scope, "index": index, "identity": record["identity"], "metrics": measured,
                            "classification_sha256": sha(output/f"{scope}-{index:03d}.npy")})
    if seen != {(s, i) for s, values in scenes.items() for i in range(len(values))}:
        raise ValueError("Development scene inventory was reduced")
    if {s: v["truth_count"] for s, v in totals.items()} != {"component": 2004, "family": 206}:
        raise ValueError("Development truth denominator changed")
    if any(sha(root/p) != h for p, h in bound.items()):
        raise ValueError("Evaluation inputs changed during execution")
    measured = {s: summarize_counts(v) for s, v in totals.items()}
    bar = bars._shared_marker_acceptance_bar()
    write(output/"details.json", results)
    report = {"schema": "graphreader.presence-head-cascade-development.v1", "status": "diagnostic_only_unapproved",
              "metrics": measured, "acceptance_bar": bar, "clears_shared_dev_bars": bars._passes_required_dev_gates(
                  measured["component"], measured["family"], bar), "binding_sha256": sha(output/"binding.json"),
              "details_sha256": sha(output/"details.json"), "maximum_non_artifact_output_change": maximum_non_artifact_change,
              "shape_or_fill_decision_changes": decisions, "center_inference_runs": 0, "optimizer_steps": 0,
              "private_reads": 0, "sealed_reads": 0, "production_approved": False,
              "native_runtime_parity_proven": False, "cpu": budget.report(), "seconds": time.perf_counter()-started}
    write(output/"report.json", report)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(Path.cwd().resolve(), args.output)
    print(json.dumps({k: result[k] for k in ("metrics", "clears_shared_dev_bars", "seconds")}))
