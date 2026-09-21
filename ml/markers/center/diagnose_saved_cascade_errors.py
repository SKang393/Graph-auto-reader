# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Attribute existing owned development errors without model inference or tuning."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import time

import numpy as np

from ml.markers.center.cascade_component_diagnostic import counts, retain
from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction
from ml.markers.center.shape_coverage_evaluation import match_predictions
from types import SimpleNamespace


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path, expected=None):
    if expected is not None and sha(path) != expected:
        raise ValueError(f"Bound evidence changed: {path}")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")


def analyze(root, output):
    started = time.perf_counter()
    baseline = root / "artifacts/goal22-runs/marker-cascade-component-v28-v1"
    candidate = root / "artifacts/goal22-runs/classifier-presence-feature-v7/cascade-dev"
    baseline_report = load(baseline / "report.json", "8d65718715c22c765cc38ee040d7220671f61e8b32d7d25538af6945caa00c28")
    candidate_report = load(candidate / "report.json", "d78a7bc113e6084f56ff9c5fc56b7e2c2a4519b609cba8c4fb1cccf1152c25de")
    baseline_records = load(baseline / "details.json", baseline_report["details_sha256"])
    candidate_records = load(candidate / "details.json", candidate_report["details_sha256"])
    baseline_binding = load(baseline / "binding.json", baseline_report["binding_sha256"])
    candidate_binding = load(candidate / "binding.json", candidate_report["binding_sha256"])
    if (baseline_binding["center_candidate"] != "v28" or baseline_binding["center_threshold"] != .1 or
            candidate_binding["center_threshold"] != .1 or baseline_binding["artifact_threshold"] != .5 or
            candidate_binding["artifact_threshold"] != .5 or baseline_binding["private_reads"] or
            baseline_binding["sealed_reads"] or candidate_report["private_reads"] or candidate_report["sealed_reads"]):
        raise ValueError("Unexpected saved development scope")
    inventory_path = "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev-inventory.json"
    inventory = load(root / inventory_path, baseline_binding["inputs"][inventory_path])
    for source in ("ml/markers/center/cascade_component_diagnostic.py",
                   "ml/markers/center/shape_coverage_evaluation.py",
                   "ml/markers/center/plot_domain_v25/diagnose_dev.py",
                   "ml/markers/center/mask_preserving_v24/mask_preserving.py"):
        if sha(root / source) != candidate_binding["inputs"][source]:
            raise ValueError("Frozen matching/counting implementation changed")
    expected = {(scope, index) for scope, records in inventory.items() for index in range(len(records))}
    if len(baseline_records) != len(expected) or len(candidate_records) != len(expected):
        raise ValueError("Saved scene denominator changed")
    totals = {name: {scope: Counter() for scope in inventory} for name in ("v5", "v7")}
    extras = {name: {scope: Counter() for scope in inventory} for name in totals}
    missed = {name: {scope: Counter() for scope in inventory} for name in totals}
    sizes = {name: {scope: defaultdict(Counter) for scope in inventory} for name in totals}
    paired = {scope: Counter() for scope in inventory}
    details = []
    seen = set()
    for record, newer in zip(baseline_records, candidate_records, strict=True):
        scope, index = record["scope"], record["index"]
        if (scope, index) in seen or (scope, index) not in expected or any(
                record[key] != newer[key] for key in ("scope", "index", "identity")):
            raise ValueError("Scene identity changed")
        seen.add((scope, index))
        metadata = inventory[scope][index]["metadata"]
        if metadata["split"] != ("dev" if scope == "component" else "validation"):
            raise ValueError("Only frozen open development scenes allowed")
        scene = SimpleNamespace(**metadata)
        diameters = metadata.get("rendered_diameters", metadata["diameters"])
        cache = baseline / record["cache"]["path"]
        if cache.parent != baseline or sha(cache) != record["cache"]["sha256"]:
            raise ValueError("Saved predictions changed")
        new_cache = candidate / f"{scope}-{index:03d}.npy"
        if sha(new_cache) != newer["classification_sha256"]:
            raise ValueError("Saved classifier output changed")
        with np.load(cache, allow_pickle=False) as archive:
            centers = tuple(MarkerPrediction(*[float(value) for value in row]) for row in archive["centers"])
            output_rows = {"v5": archive["classification"].copy(), "v7": np.load(new_cache, allow_pickle=False)}
        matched_truth = {}
        row_result = {"scope": scope, "index": index, "identity": record["identity"], "classifiers": {}}
        for name, classification in output_rows.items():
            kept = retain(centers, classification)
            measured = counts(scene, kept, record["metrics"]["balanced"]["proposal_true_positives"])
            prior = record["metrics"]["classified"] if name == "v5" else newer["metrics"]
            if measured != prior:
                raise ValueError("Saved metric reproduction failed")
            totals[name][scope].update(measured)
            matching = match_predictions(kept, scene.centers)
            matched_predictions = {p for p, _ in matching}
            matched_truth[name] = {t for _, t in matching}
            false_rows, missing_rows = [], []
            for pi, prediction in enumerate(kept):
                if pi in matched_predictions:
                    continue
                distances = [math.hypot(prediction.x-x, prediction.y-y) for x, y in scene.centers]
                nearest = min(range(len(distances)), key=distances.__getitem__)
                distance = distances[nearest]
                kinds = sorted({kind for kind, x, y in scene.hard_negatives
                                if math.hypot(prediction.x-x, prediction.y-y) <= 5})
                near_glyph = distance <= diameters[nearest] / 2
                bucket = ("annotated_structure_center" if kinds else
                          "within_nominal_marker_radius" if near_glyph else "unattributed")
                extras[name][scope][bucket] += 1
                extras[name][scope].update("structure:" + kind for kind in kinds)
                false_rows.append({"x": prediction.x, "y": prediction.y, "radius": prediction.radius,
                                   "nearest_truth_distance": distance, "nearest_truth_diameter": diameters[nearest],
                                   "structure_kinds": kinds, "bucket": bucket})
            for ti, (x, y) in enumerate(scene.centers):
                diameter = str(diameters[ti])
                sizes[name][scope][diameter]["truth"] += 1
                sizes[name][scope][diameter]["matched"] += ti in matched_truth[name]
                if ti in matched_truth[name]:
                    continue
                close = [i for i, item in enumerate(centers) if math.hypot(item.x-x, item.y-y) <= 5]
                if not close:
                    reason = "no_geometric_center_within_5px_before_classifier"
                elif all(classification[i, 12] >= .5 for i in close):
                    reason = "all_nearby_centers_rejected_by_classifier"
                else:
                    reason = "accepted_nearby_center_assigned_to_another_truth"
                missed[name][scope][reason] += 1
                missing_rows.append({"truth_index": ti, "diameter": diameters[ti], "reason": reason})
            if len(false_rows) != measured["false_positives"] or len(missing_rows) != measured["false_negatives"]:
                raise ValueError("Error attribution lost rows")
            row_result["classifiers"][name] = {"false_positives": false_rows, "misses": missing_rows}
        paired[scope].update({"retained": len(matched_truth["v5"] & matched_truth["v7"]),
                              "recovered": len(matched_truth["v7"] - matched_truth["v5"]),
                              "lost": len(matched_truth["v5"] - matched_truth["v7"]),
                              "missed_by_both": len(scene.centers) - len(matched_truth["v5"] | matched_truth["v7"])})
        details.append(row_result)
    if seen != expected or {s: t["truth_count"] for s, t in totals["v7"].items()} != {"component": 2004, "family": 206}:
        raise ValueError("Complete truth denominator changed")
    output.mkdir(parents=True, exist_ok=False)
    write(output / "details.json", details)
    report = {"schema": "graphreader.saved-cascade-error-diagnosis.v1", "scope": "owned-open-synthetic-development",
              "totals": totals, "false_positive_proximity": extras, "miss_causes": missed,
              "diameter_counts": sizes, "paired_truth_outcomes": paired,
              "details_sha256": sha(output / "details.json"), "seconds": time.perf_counter() - started,
              "model_inference_runs": 0, "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
              "thresholds_changed": False, "production_approved": False,
              "limits": "Nominal-radius and hard-negative-point proximity are descriptive, not pixel-level semantic attribution. V28 cached geometry is not native V27 workflow parity."}
    write(output / "report.json", report)
    print(json.dumps({key: report[key] for key in ("totals", "false_positive_proximity", "miss_causes", "paired_truth_outcomes", "seconds")}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    analyze(Path.cwd().resolve(), options.output.resolve())
