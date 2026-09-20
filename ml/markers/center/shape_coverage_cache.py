# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cache authenticated historical rows plus an owned shape-only supplement.

This is preparation, not a training candidate. No model is opened. Development
scenes are serialized without regeneration or target changes after the existing
V27 preparation has authenticated their original inputs.
"""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import asdict, fields
import hashlib
import json
import os
from pathlib import Path
import shutil
import time
from types import SimpleNamespace

import numpy as np
import torch

from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ml.markers.classifier.graph_context_cache import SOURCE_PATHS as RENDER_SOURCES
from . import shape_coverage_data as coverage
from .component_diversity_v27 import train_p1 as v27
from .plot_domain_v25.proposal_domain import PlotDomain

VERSION = "marker-center-shape-coverage-cache-v1"
HISTORICAL_INVENTORY = "aaf650b405cb35764c9126e6f692db34664ff899b2e3bd19beb60c93f52e7229"
NAMES = ("patches", "labels", "offsets", "radii", "hard_negative")
SOURCE_PATHS = tuple(dict.fromkeys((
    *(p.as_posix() for p in v27.RUNNER_SOURCE_PATHS), *RENDER_SOURCES,
    "ml/markers/center/shape_coverage_data.py", "ml/markers/center/shape_coverage_cache.py",
    "ml/markers/classifier/native_context_v3/runner.py", "ml/training_cpu_budget.py",
    "tools/Run-TrainingCpuBudget.ps1")))


def write_json(path: Path, value) -> None:
    path.write_bytes(canonical_json_bytes(value))


def tensor_digest(value: torch.Tensor) -> str:
    array = value.detach().cpu().contiguous().numpy()
    return hashlib.sha256(memoryview(array).cast("B")).hexdigest()


def pack_scene(scene, domain: PlotDomain | None = None) -> dict:
    metadata = {f.name: getattr(scene, f.name) for f in fields(scene) if f.name != "tensor"}
    # JSON conversion rejects unsupported metadata instead of pickling objects.
    metadata = json.loads(canonical_json_bytes(metadata))
    return {"metadata": metadata, "tensor": scene.tensor,
            "tensor_sha256": tensor_digest(scene.tensor),
            "domain": None if domain is None else asdict(domain)}


def unpack_scene(record: dict, *, expected_split: str = "dev"):
    if tensor_digest(record["tensor"]) != record["tensor_sha256"]:
        raise ValueError("Historical development tensor changed")
    if record["metadata"]["split"] != expected_split:
        raise ValueError("Unexpected development split")
    scene = SimpleNamespace(**record["metadata"], tensor=record["tensor"])
    if record["domain"] is None:
        return scene
    values = dict(record["domain"])
    values["polygon"] = tuple(tuple(point) for point in values["polygon"])
    return SimpleNamespace(scene=scene, panel_domain=SimpleNamespace(domain=PlotDomain(**values)))


def validate_rows(values, *, expected_count: int | None = None) -> dict:
    if len(values) != 5:
        raise ValueError("Proposal training columns changed")
    patches, labels, offsets, radii, hard = values
    count = len(labels)
    if (count == 0 or (expected_count is not None and count != expected_count)
            or patches.shape != (count, 3, 33, 33) or offsets.shape != (count, 2)
            or labels.shape != (count,) or radii.shape != (count,) or hard.shape != (count,)):
        raise ValueError("Proposal training dimensions changed")
    # Avoid a second full-size temporary allocation for the patch audit.
    for start in range(0, count, 512):
        part = patches[start:start+512]
        if not torch.isfinite(part).all() or part.min() < 0 or part.max() > 1:
            raise ValueError("Proposal pixels must be finite probabilities")
    if (not all(torch.isfinite(t).all() for t in (labels, offsets, radii, hard))
            or not ((labels == 0) | (labels == 1)).all()
            or not ((hard == 0) | (hard == 1)).all()
            or (offsets[labels == 1].abs() > .750001).any() or (radii[labels == 1] <= 0).any()):
        raise ValueError("Proposal labels or offsets changed")
    return {"rows": count, "positive": int((labels == 1).sum()),
            "negative": int((labels == 0).sum()),
            "hard_negative": int(((hard == 1) & (labels == 0)).sum()),
            "positive_radius_over_model_maximum": int(((radii > 8) & (labels == 1)).sum())}


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    if (os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80"
            or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE"):
        raise RuntimeError("Use the verified CPU guard with passive worker waits")
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output / "CANCEL")
    source_hashes = {path: sha256_file(repo/path) for path in SOURCE_PATHS}
    for path in SOURCE_PATHS:
        target = output/"source-snapshot"/path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/path, target)
    with budget.work_block():
        base = v27.prepare_training(repository_root=repo)
        if base.training_tensor_inventory_sha256 != HISTORICAL_INVENTORY:
            raise ValueError("The exact historical 44891-row inventory changed")
        counts = {}
        for scope, values in (("component", base.component_values), ("family", base.family_values)):
            counts[scope] = validate_rows(values, expected_count={"component": 35838, "family": 9053}[scope])
            torch.save(dict(zip(NAMES, values, strict=True)), output/f"train-{scope}.pt")
        development = {
            "component": [pack_scene(s) for s in base.base.component_dev],
            "family": [pack_scene(s.scene, s.panel_domain.domain) for s in base.base.family_dev]}
        if {k: (len(v), sum(len(r["metadata"]["centers"]) for r in v)) for k, v in development.items()} != {
                "component": (167, 2004), "family": (9, 206)}:
            raise ValueError("The complete historical development denominator changed")
        torch.save(development, output/"dev.pt")
        write_json(output/"historical-preparation.json", base.base.report)
    del base
    rows, parts = [], [[] for _ in NAMES]
    recipes = coverage.cases()
    for start in range(0, len(recipes), 32):
        with budget.work_block():
            for case in recipes[start:start+32]:
                sample = coverage.prepare_case(case)
                rows.append(sample.record)
                for target, name in zip(parts, NAMES, strict=True):
                    target.append(torch.from_numpy(getattr(sample, name)))
    with budget.work_block():
        values = tuple(torch.cat(p) for p in parts)
        counts["coverage"] = validate_rows(values)
        torch.save(dict(zip(NAMES, values, strict=True)), output/"train-coverage.pt")
        write_json(output/"coverage.json", rows)
        inventory = {scope: [{"metadata": r["metadata"], "tensor_sha256": r["tensor_sha256"],
                              "domain": r["domain"]} for r in items] for scope, items in development.items()}
        write_json(output/"dev-inventory.json", inventory)
        # No selected/truth row is omitted for a difficult shape, fill or radius.
        shape_counts = Counter(row["shape"] for row in rows)
        if set(shape_counts.values()) != {240} or len(shape_counts) != 9:
            raise ValueError("Shape coverage inventory changed")
        files = {p.name: sha256_file(p) for p in output.iterdir() if p.is_file()}
    if source_hashes != {p: sha256_file(repo/p) for p in SOURCE_PATHS}:
        raise RuntimeError("Preparation source changed during execution")
    manifest = {"version": VERSION, "source_sha256": source_hashes, "files": files,
        "historical_training_tensor_inventory_sha256": HISTORICAL_INVENTORY,
        "counts": counts, "coverage_definition": coverage.definition(), "shape_counts": dict(shape_counts),
        "coverage_truths": {"count": len(rows),
            "supported_by_positive_proposal": sum(r["truth_supported_by_positive_proposal"] for r in rows),
            "unsupported_case_ids": [r["sample_id"] for r in rows if not r["truth_supported_by_positive_proposal"]]},
        "dev": {"component_scenes": 167, "family_scenes": 9, "truths": 2210,
                "inventory_sha256": files["dev-inventory.json"]},
        "radius_limitation": "Raw targets over the historical 8-pixel output radius remain in the cache and denominator. The unchanged regression loss clamps its radius target to 2.5..8 pixels.",
        "seconds": time.perf_counter()-started, "cpu": budget.report(),
        "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0}
    write_json(output/"manifest.json", manifest)
    return manifest


def load_cache(directory: Path, expected_sha256: str) -> tuple[dict, dict, dict]:
    if sha256_file(directory/"manifest.json") != expected_sha256:
        raise ValueError("Shape coverage cache manifest changed")
    manifest = json.loads((directory/"manifest.json").read_text())
    if (manifest["version"] != VERSION
            or manifest["historical_training_tensor_inventory_sha256"] != HISTORICAL_INVENTORY):
        raise ValueError("Historical training identity changed")
    for name, digest in manifest["files"].items():
        if Path(name).name != name or sha256_file(directory/name) != digest:
            raise ValueError("Shape coverage cache file changed")
    training = {}
    for scope in ("component", "family", "coverage"):
        payload = torch.load(directory/f"train-{scope}.pt", map_location="cpu", weights_only=True)
        training[scope] = tuple(payload[name] for name in NAMES)
        if validate_rows(training[scope]) != manifest["counts"][scope]:
            raise ValueError("Training population audit changed")
    if v27._tensor_inventory_sha256(training["component"], training["family"]) != HISTORICAL_INVENTORY:
        raise ValueError("Cached historical training rows changed")
    payload = torch.load(directory/"dev.pt", map_location="cpu", weights_only=True)
    # The frozen full-graph family calls its unsealed dev split "validation".
    # Preserve that original identity instead of relabeling the cached scenes.
    if set(payload) != {"component", "family"}:
        raise ValueError("Unexpected development scope")
    development = {scope: tuple(unpack_scene(r, expected_split={"component": "dev", "family": "validation"}[scope])
                               for r in rows) for scope, rows in payload.items()}
    return training, development, manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({"counts": report["counts"], "seconds": report["seconds"]}))
