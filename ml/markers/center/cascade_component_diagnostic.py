# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replay authenticated synthetic development proposals through the V5 classifier.

This measures the current unapproved diagnostic composition. It preserves the
historical raw-model failure and cannot authorize sealed/private evaluation.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time

import numpy as np
import torch

from ml.markers.classifier.native_context_evaluation import create_session, infer
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ml.markers.classifier.runtime_patches import extract_original_patch
from .balanced_support_diagnostic import balanced_center_support
from .component_diversity_v27 import diagnose_head_swap as historical
from .component_diversity_v27 import diagnose_score_localization as source
from .line_aware_v1.pipeline import MarkerPrediction
from .mask_preserving_v24 import mask_preserving as legacy
from .mask_preserving_v24 import train_p1 as bars
from .shape_coverage_cache import unpack_scene
from .shape_coverage_evaluation import match_predictions, predictions, summarize_counts

INPUTS = {
    "artifacts/goal22-runs/center-shape-coverage-cache-v3/manifest.json":
        "d61e43d62891a5266dfd68dfd8286fdb0d28d685d2146763a9f49dfb24471700",
    "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt":
        "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41",
    "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev-inventory.json":
        "e2843baf05fd33b9b5c7287b00c4f20ce0a278dfe9481308daf612d6c2dde099",
    "artifacts/goal22-runs/classifier-structural-context-v5/P1/marker-classifier.onnx":
        "7f15c0ed0d8740bf4cc1d220764e670d999ca4f4a1dbfe5c631b01db5faaebba",
}
SCORE = .10
ARTIFACT = .5
FIXED_CENTERS = {
    "v28": ("artifacts/goal22-runs/center-shape-v28/P1-retry1",
            "a9db0b508a8c3b909d457adbe0e6aa4259369b6389cdad8e726164ea467ba099",
            "624b35bf67d9903448bd0b20026c9436af4afc57806e5b66fbf24381e4697f2d"),
    "v29": ("artifacts/goal22-runs/center-train-negative-v29/P1",
            "6fd23f5bd8c91ad76ef48a9fc6df97bdc85fe642b5840dd751b0e87b25f98a35",
            "8abdd8ecb476e27db2f6c85ea03b91cdaf2b5803d7a53b64dd66c727caaf3327"),
}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path: Path, value) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")


def decode(scene, coordinates, values, domain, *, threshold=SCORE):
    if (coordinates.shape != (len(values), 2) or values.shape != (len(values), 4)
            or not np.isfinite(coordinates).all() or not np.isfinite(values).all()
            or not math.isfinite(threshold) or not 0 <= threshold <= 1
            or np.any((values[:, 0] < 0) | (values[:, 0] > 1))):
        raise ValueError("Invalid cached proposal dimensions, values or threshold")
    decoded = []
    for index in np.flatnonzero(values[:, 0] >= threshold):
        x = float(coordinates[index, 0] + values[index, 1] * 4.)
        y = float(coordinates[index, 1] + values[index, 2] * 4.)
        if domain is None or domain.contains(x, y):
            decoded.append(MarkerPrediction(x, y, float(np.clip(values[index, 3], 2.5, 8.)),
                                            float(values[index, 0])))
    geometric = [p for p in decoded if balanced_center_support(scene.tensor[0].numpy(), p.x, p.y)]
    accepted = []
    for item in sorted(geometric, key=lambda p: (-p.confidence, p.y, p.x)):
        if not any(math.hypot(item.x-p.x, item.y-p.y) < max(5., 1.25*max(item.radius, p.radius))
                   for p in accepted):
            accepted.append(item)
    return decoded, geometric, tuple(sorted(accepted, key=lambda p: (p.y, p.x, -p.confidence)))


def retain(items, output):
    if (output.shape != (len(items), 25) or not np.isfinite(output).all()
            or np.any((output[:, :13] < 0) | (output[:, :13] > 1))
            or not np.allclose(output[:, :9].sum(1), 1, atol=1e-5)
            or not np.allclose(output[:, 9:12].sum(1), 1, atol=1e-5)):
        raise ValueError("Classifier probability contract changed")
    return tuple(p for p, row in zip(items, output, strict=True) if row[12] < ARTIFACT)


def counts(scene, items, supported):
    pairs = match_predictions(items, scene.centers)
    metrics = bars.center_metrics(items, scene.centers, 5.)
    return {"scene_count": 1, "truth_count": len(scene.centers), "true_positives": len(pairs),
            "false_positives": len(items)-len(pairs), "false_negatives": len(scene.centers)-len(pairs),
            "proposal_true_positives": supported, "duplicate_count": metrics.duplicate_count,
            "prohibited_structure_hits": sum(legacy.prohibited_hits(items, scene).values())}


def truth_diagnosis(scene, stages):
    matched = {name: {j for _, j in match_predictions(items, scene.centers)}
               for name, items in stages.items()}
    rows = []
    ink = scene.tensor[0].numpy()
    diameters = getattr(scene, "rendered_diameters", getattr(scene, "diameters", None))
    for index, (x, y) in enumerate(scene.centers):
        ix, iy = round(x), round(y)
        patch = ink[max(0, iy-5):iy+6, max(0, ix-5):ix+6]
        cause = next((name for name in stages if index not in matched[name]), "retained")
        # Earlier greedy pairings can change when false candidates disappear.
        if index in matched["classified"]:
            cause = "retained"
        rows.append({"truth_index": index, "rendered_diameter": diameters[index] if diameters else None,
                     "local_maximum_ink": float(patch.max()),
                     "local_ink_pixels_at_012": int((patch >= np.float32(.12)).sum()),
                     "geometry_at_true_center": balanced_center_support(ink, x, y),
                     "first_unmatched_stage": cause})
    return rows


def infer_center(session, scene, coordinates, budget):
    if not np.equal(coordinates, np.floor(coordinates)).all():
        raise ValueError("Frozen proposal coordinates must be integral")
    padded = np.pad(scene.tensor.numpy(), ((0, 0), (16, 16), (16, 16)))
    chunks = []
    for start in range(0, len(coordinates), 128):
        with budget.work_block():
            pixels = np.stack([padded[:, int(y):int(y)+33, int(x):int(x)+33]
                               for x, y in coordinates[start:start+128]])
            actual = session.run(["candidate_predictions"], {"candidate_patches": pixels})[0]
            if actual.shape != (len(pixels), 4) or not np.isfinite(actual).all():
                raise ValueError("Invalid fixed-center runtime output")
            chunks.append(actual)
    return np.concatenate(chunks)


def run(root: Path, output: Path, *, center_candidate: str = "v27") -> dict:
    started = time.perf_counter()
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the existing CPU guard and passive worker waits")
    output.mkdir(parents=True, exist_ok=False)
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    budget = WorkBudget(output/"CANCEL")
    if center_candidate not in ("v27", *FIXED_CENTERS):
        raise ValueError("Only the named existing development candidates are allowed")
    inputs = dict(INPUTS)
    if center_candidate != "v27":
        directory, model_sha, report_sha = FIXED_CENTERS[center_candidate]
        inputs[directory+"/marker-center.onnx"] = model_sha
        inputs[directory+"/report.json"] = report_sha
    sources = {Path(m.__file__).resolve().relative_to(root).as_posix(): sha(Path(m.__file__))
               for name, m in tuple(sys.modules.items()) if name.startswith("ml.")
               and getattr(m, "__file__", None) and Path(m.__file__).resolve().is_relative_to(root)}
    sources[Path(__file__).resolve().relative_to(root).as_posix()] = sha(Path(__file__))
    sources["ml/policy/acceptance-bars.json"] = sha(root/"ml/policy/acceptance-bars.json")
    with budget.work_block():
        if {p: sha(root/p) for p in inputs} != inputs:
            raise ValueError("Frozen development input changed")
        paths, old_report, prior_report, hashes = source._authenticate(root, historical.SOURCE_DIAGNOSTIC_SHA256)
        coordinates_cache, _ = source._load_caches(paths, old_report, prior_report)
        baseline_report, baseline_cache = historical._load_v27_cache(root)
        packed = torch.load(root/next(p for p in INPUTS if p.endswith("dev.pt")), weights_only=True, map_location="cpu")
        if {s: (len(v), sum(len(r["metadata"]["centers"]) for r in v)) for s, v in packed.items()} != {
                "component": (167, 2004), "family": (9, 206)}:
            raise ValueError("Complete development denominator changed")
        session = create_session(root/next(p for p in INPUTS if p.endswith(".onnx")))
        center_session = None if center_candidate == "v27" else create_session(root/directory/"marker-center.onnx")
    binding = {"schema": "graphreader.marker-cascade-development-binding.v1", "inputs": inputs,
               "sources": sources, "center_threshold": SCORE, "artifact_threshold": ARTIFACT,
               "center_candidate": center_candidate,
               "geometry": "multiradius_enclosed_balanced_v2", "match_distance_pixels": 5,
               "historical_threshold": .25, "private_reads": 0, "sealed_reads": 0,
               "optimizer_steps": 0, "production_approved": False}
    write(output/"binding.json", binding)
    totals, records, classification_rows = {}, [], 0
    for scope, entries in packed.items():
        totals[scope] = {name: Counter() for name in ("historical", "balanced", "classified")}
        for index, record in enumerate(entries):
            with budget.work_block():
                bound = unpack_scene(record, expected_split="dev" if scope == "component" else "validation")
                scene = bound.scene if hasattr(bound, "scene") else bound
                domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
                old = old_report[scope+"_dev"]["cache_manifest"][index]
                baseline = baseline_report[scope+"_dev"]["cache_manifest"][index]
                coordinates = coordinates_cache[old["proposal_coordinates_key"]]
                values = baseline_cache[baseline["v27_candidate_predictions_key"]]
                if (old["scene_identity"] != baseline["scene_identity"]
                        or old["scene_identity"] != source._scene_identity(scene)
                        or source.cache_helper._array_sha(coordinates) != baseline["proposal_coordinates_sha256"]
                        or source.cache_helper._array_sha(values) != baseline["v27_candidate_predictions_sha256"]):
                    raise ValueError("Scene, coordinate or prediction join changed")
                historical_items = predictions(scene, coordinates, values, domain, balanced=False)
            if center_session is not None:
                values = infer_center(center_session, scene, coordinates, budget)
            with budget.work_block():
                decoded, geometric, balanced = decode(scene, coordinates, values, domain)
                if decode(scene, coordinates, values, domain, threshold=.25)[2] != predictions(
                        scene, coordinates, values, domain, balanced=True):
                    raise ValueError("Frozen balanced decoder parity failed")
                centers = np.asarray(scene.centers, dtype=np.float32).reshape(-1, 2)
                supported = int((np.linalg.norm(coordinates[:, None, :]-centers[None, :, :], axis=2).min(0) <= 5).sum())
                patches = np.stack([extract_original_patch(1-scene.tensor[0].numpy(), (p.x, p.y), p.radius)
                                    for p in balanced]) if balanced else np.empty((0, 1, 32, 32), np.float32)
            classification = infer(session, patches, budget) if len(patches) else np.empty((0, 25), np.float32)
            with budget.work_block():
                classified = retain(balanced, classification)
                classification_rows += len(classification)
                np.savez_compressed(output/f"{scope}-{index:03d}.npz", classification=classification,
                    proposal_predictions=values,
                    centers=np.asarray([(p.x, p.y, p.radius, p.confidence) for p in balanced], dtype=np.float32).reshape(-1, 4))
                per_scene = {}
                for kind, items in (("historical", historical_items), ("balanced", balanced), ("classified", classified)):
                    per_scene[kind] = counts(scene, items, supported)
                    totals[scope][kind].update(per_scene[kind])
                rows = truth_diagnosis(scene, {"decoded": decoded, "geometry": geometric,
                                               "nms": balanced, "classified": classified})
                records.append({"scope": scope, "index": index, "identity": old["scene_identity"],
                                "metrics": per_scene, "truth_diagnosis": rows,
                                "cache": {"path": f"{scope}-{index:03d}.npz", "sha256": sha(output/f"{scope}-{index:03d}.npz")}})
    for scope, expected in (("component", (1796, 238, 208)), ("family", (190, 39, 16))):
        if tuple(totals[scope]["historical"][k] for k in ("true_positives", "false_positives", "false_negatives")) != expected:
            raise ValueError("Historical raw-model failure did not reproduce")
    with budget.work_block():
        source._verify_inputs_unchanged(root, paths, hashes)
        if {p: sha(root/p) for p in inputs} != inputs or {p: sha(root/p) for p in sources} != sources:
            raise ValueError("Diagnostic input or source changed during execution")
    results = {s: {k: summarize_counts(c) for k, c in kinds.items()} for s, kinds in totals.items()}
    bar = bars._shared_marker_acceptance_bar()
    write(output/"details.json", records)
    result = {"schema": "graphreader.marker-cascade-development-diagnostic.v1", "status": "diagnostic_only_unapproved",
              "binding_sha256": sha(output/"binding.json"), "results": results,
              "first_unmatched_stage": {s: dict(Counter(row["first_unmatched_stage"] for r in records if r["scope"] == s
                                                       for row in r["truth_diagnosis"])) for s in totals},
              "acceptance_bar": bar, "clears_shared_dev_bars": bars._passes_required_dev_gates(
                  results["component"]["classified"], results["family"]["classified"], bar),
              "details_sha256": sha(output/"details.json"), "center_inference_runs": 0 if center_session is None else len(records),
              "center_candidate": center_candidate,
              "classifier_rows": classification_rows, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
              "production_approved": False, "native_runtime_parity_proven": False,
              "cpu": budget.report(), "seconds": time.perf_counter()-started}
    write(output/"report.json", result)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--center-candidate", choices=("v27", *FIXED_CENTERS), default="v27")
    arguments = parser.parse_args()
    result = run(Path.cwd().resolve(), arguments.output, center_candidate=arguments.center_candidate)
    print(json.dumps({k: result[k] for k in ("status", "results", "first_unmatched_stage", "seconds")}))
