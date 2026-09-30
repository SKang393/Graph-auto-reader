# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Retain the complete crowded train population and mine its background errors."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import shutil
import time

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.gate_seal import sha256_file
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from . import crowded_coverage_cache as parent
from . import shape_coverage_cache as tensors
from .component_diversity_v27 import train_p1 as v27
from .mask_preserving_v24 import mask_preserving as proposals
from .train_negative_coverage import select_negative_indices, selected_rows

VERSION = "marker-center-crowded-negative-coverage-v1"
MODEL_PATH = "artifacts/goal22-runs/center-crowded-coverage-v30/P1/marker-center.onnx"
MODEL_SHA256 = "87c15ac392cd25c5f419a0b1279134323a7ec43c4cea8b354ad45744e767abe3"
PARENT_PATH = "artifacts/goal22-runs/center-crowded-coverage-cache-v1"
PARENT_SHA256 = "1458a7f285f382b968b32e6b4ceb3e431544483bfcb2b0f5de3661278f3d8f03"
DIAGNOSTIC_PATH = "artifacts/goal22-runs/center-crowded-training-coverage-20260930-v1"
ORIGINAL_ROWS = {"component": 35838, "family": 9053, "coverage": 42747, "crowded": 182614}
SOURCE_PATHS = tuple(dict.fromkeys((*parent.SOURCE_PATHS,
    "ml/markers/center/train_negative_coverage.py",
    "ml/markers/center/crowded_negative_coverage.py")))


def validate_diagnostic(report: dict) -> None:
    if (report.get("scope") != "fixed_candidate_on_authenticated_synthetic_train_only" or
            report.get("model_sha256") != MODEL_SHA256 or
            report.get("parent_train_cache_sha256") != PARENT_SHA256 or
            report.get("all_v30_training_rows") != sum(ORIGINAL_ROWS.values()) or
            report.get("training_tensor_inventory_sha256") != tensors.HISTORICAL_INVENTORY or
            report.get("operating_threshold") != .25 or
            any(report.get(k) != 0 for k in ("optimizer_steps", "private_reads", "sealed_reads"))):
        raise ValueError("Training-only fixed-model diagnostic changed")
    records = report.get("records", [])
    if (len(records) != 28 or [r.get("scene_index") for r in records] != list(range(28)) or
            any(r.get("split") != "train" for r in records)):
        raise ValueError("Complete authenticated training family changed")


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the verified CPU guard with passive waits")
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    diagnostic = repo / DIAGNOSTIC_PATH
    report = json.loads((diagnostic / "report.json").read_text())
    validate_diagnostic(report)
    if sha256_file(diagnostic / "train-scenes.pt") != report["train_scenes_sha256"]:
        raise ValueError("Authenticated training scene bytes changed")
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output / "CANCEL")
    source_hashes = {name: sha256_file(repo / name) for name in SOURCE_PATHS}
    for name in SOURCE_PATHS:
        destination = output / "source-snapshot" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo / name, destination)
    model = repo / MODEL_PATH
    if sha256_file(model) != MODEL_SHA256:
        raise ValueError("Fixed mining model changed")
    options = ort.SessionOptions()
    options.intra_op_num_threads = int(os.environ["OMP_NUM_THREADS"])
    options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
    with budget.work_block():
        original, dev, old = parent.load_cache(repo, repo / PARENT_PATH, PARENT_SHA256)
        if {k: len(v[0]) for k, v in original.items()} != ORIGINAL_ROWS:
            raise ValueError("Complete crowded training population changed")
        del original, dev
        base = v27.prepare_training(repository_root=repo)
        if base.training_tensor_inventory_sha256 != tensors.HISTORICAL_INVENTORY:
            raise ValueError("Historical training inputs changed")
        packed = torch.load(diagnostic / "train-scenes.pt", map_location="cpu", weights_only=True)
        family = [tensors.unpack_scene(item, expected_split="train") for item in packed]
        if len(family) != len(base.base.family_train):
            raise ValueError("Training family denominator changed")
        for saved, current in zip(family, base.base.family_train, strict=True):
            if (tensors.tensor_digest(saved.scene.tensor) != tensors.tensor_digest(current.scene.tensor) or
                    tuple(map(tuple, saved.scene.centers)) != tuple(map(tuple, current.scene.centers)) or
                    saved.panel_domain.domain != current.panel_domain.domain):
                raise ValueError("Training pixels, truth or proposal domain changed")
        component = base.base.component_train
        del base, packed
    parts = [[] for _ in tensors.NAMES]
    records, totals = [], Counter()
    for scope, scenes in (("component", component), ("family", family)):
        for index, bound in enumerate(scenes):
            scene = bound.scene if scope == "family" else bound
            if scene.split != "train":
                raise ValueError("Only training scenes may be mined")
            if scope == "family":
                path = diagnostic / f"family-{index:03d}.npz"
                expected = report["records"][index]
                if sha256_file(path) != expected["cache_sha256"]:
                    raise ValueError("Fixed-model training predictions changed")
                with np.load(path, allow_pickle=False) as saved:
                    coordinates, values = saved["coordinates"], saved["outputs"]
            else:
                with budget.work_block():
                    batch = proposals.extract_proposals(scene.tensor)
                    coordinates = batch.coordinates.numpy()
                chunks = []
                for start in range(0, len(batch.patches), 128):
                    with budget.work_block():
                        chunks.append(session.run(["candidate_predictions"],
                            {"candidate_patches": batch.patches[start:start + 128].numpy()})[0])
                values = np.concatenate(chunks)
                del batch
            with budget.work_block():
                indices = select_negative_indices(scene, coordinates, values)
                rows = selected_rows(scene, coordinates, indices)
                for part, tensor in zip(parts, rows, strict=True):
                    part.append(tensor)
                counts = {"proposals": len(coordinates), "selected": len(indices), "truth": len(scene.centers)}
                totals.update({scope + "_" + key: value for key, value in counts.items()})
                path = output / f"{scope}-{index:03d}-selection.npz"
                np.savez_compressed(path, coordinates=coordinates, outputs=values, selected_indices=indices)
                records.append({"scope": scope, "scene_index": index, "split": scene.split,
                    "scene_tensor_sha256": tensors.tensor_digest(scene.tensor), "counts": counts,
                    "cache_file": path.name, "cache_sha256": sha256_file(path)})
    with budget.work_block():
        values = tuple(torch.cat(part) for part in parts)
        counts = tensors.validate_rows(values)
        if counts["positive"] != 0 or counts["hard_negative"] != counts["rows"]:
            raise ValueError("Mining must add only clear background negatives")
        torch.save(dict(zip(tensors.NAMES, values, strict=True)), output / "train-negative.pt")
        tensors.write_json(output / "selection.json", records)
        if source_hashes != {name: sha256_file(repo / name) for name in SOURCE_PATHS}:
            raise ValueError("Mining source changed during preparation")
        manifest = {"version": VERSION, "parent_cache_path": PARENT_PATH,
            "parent_manifest_sha256": PARENT_SHA256, "fixed_model_sha256": MODEL_SHA256,
            "training_diagnostic_sha256": sha256_file(diagnostic / "report.json"),
            "files": {p.name: sha256_file(p) for p in output.iterdir() if p.is_file()},
            "source_sha256": source_hashes, "counts": counts, "totals": dict(totals),
            "original_rows": ORIGINAL_ROWS, "dev": old["dev"],
            "all_original_training_rows_retained": True, "all_development_bytes_and_truth_unchanged": True,
            "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
            "seconds": time.perf_counter() - started, "cpu": budget.report()}
        tensors.write_json(output / "manifest.json", manifest)
    return manifest


def load_cache(repo: Path, directory: Path, expected_sha256: str):
    if sha256_file(directory / "manifest.json") != expected_sha256:
        raise ValueError("Crowded negative cache manifest changed")
    manifest = json.loads((directory / "manifest.json").read_text())
    if (manifest["version"] != VERSION or manifest["parent_cache_path"] != PARENT_PATH or
            manifest["parent_manifest_sha256"] != PARENT_SHA256 or
            manifest["fixed_model_sha256"] != MODEL_SHA256 or manifest["original_rows"] != ORIGINAL_ROWS or
            set(manifest["source_sha256"]) != set(SOURCE_PATHS)):
        raise ValueError("Crowded negative cache lineage changed")
    for name, digest in manifest["source_sha256"].items():
        if sha256_file(repo / name) != digest or sha256_file(directory / "source-snapshot" / name) != digest:
            raise ValueError("Negative preparation source changed")
    for name, digest in manifest["files"].items():
        if Path(name).name != name or sha256_file(directory / name) != digest:
            raise ValueError("Negative cache file changed")
    scopes, dev, old = parent.load_cache(repo, repo / PARENT_PATH, PARENT_SHA256)
    if {k: len(v[0]) for k, v in scopes.items()} != ORIGINAL_ROWS or old["dev"] != manifest["dev"]:
        raise ValueError("Original training population or development changed")
    payload = torch.load(directory / "train-negative.pt", map_location="cpu", weights_only=True)
    values = tuple(payload[name] for name in tensors.NAMES)
    if tensors.validate_rows(values) != manifest["counts"] or (values[1] != 0).any() or (values[4] != 1).any():
        raise ValueError("Mined negative labels or population changed")
    scopes["crowded_mined_negative"] = values
    return scopes, dev, manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({k: result[k] for k in ("counts", "totals", "seconds")}))
