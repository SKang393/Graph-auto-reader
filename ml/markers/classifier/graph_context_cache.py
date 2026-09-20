# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Append owned train coverage while preserving every old train/dev example."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import time

import numpy as np
from PIL import Image

from ml.training_cpu_budget import TrainingCpuBudget
from . import graph_context_data as coverage
from .native_context_cache import (SOURCE_PATHS as NATIVE_SOURCES, audit_records,
    load_cache as load_native_cache, observable_targets, sha256, write_json)
from .native_context_data import FILL_NAMES
from .runtime_diagnostic import SHAPES
from .runtime_patches import extract_original_patch, original_luminance

SOURCE_PATHS = (*NATIVE_SOURCES, "ml/markers/classifier/graph_context_data.py",
                "ml/markers/classifier/graph_context_cache.py")
BASE_CACHE = Path("artifacts/goal22-runs/classifier-native-context-cache-v2")
BASE_SHA256 = "dcd6df06f2217cd6ab17168e4eb101cd0290829b63cb9e3dc789185bc7b872cd"
LAYOUT_REPORT = Path("artifacts/goal22-runs/condition-label-layout-audit-v3/report.json")
LAYOUT_SHA256 = "2891886457562d2d49fb96ef3b3b635463299b2013b1eddac03332dc10c31598"


def verified_json(repo: Path, ref: dict) -> dict:
    path = repo / ref["path"]
    if sha256(path) != ref["sha256"]:
        raise ValueError("Owned synthetic source binding changed")
    return json.loads(path.read_text(encoding="utf-8"))


def text_components(pixels: np.ndarray, box: list[float]) -> list[tuple[tuple[float, float], float]]:
    """Label all visible text components; never ask a model which ones fail."""
    x, y, w, h = box
    x0, y0 = max(0, math.floor(x)), max(0, math.floor(y))
    x1, y1 = min(pixels.shape[1], math.ceil(x+w)), min(pixels.shape[0], math.ceil(y+h))
    if x1 <= x0 or y1 <= y0:
        return []
    ink = pixels[y0:y1, x0:x1] < .65
    visited = np.zeros(ink.shape, dtype=bool)
    result = []
    for y, x in zip(*np.nonzero(ink)):
        if visited[y, x]:
            continue
        stack, component = [(int(x), int(y))], []
        visited[y, x] = True
        while stack:
            cx, cy = stack.pop()
            component.append((cx, cy))
            for ny in range(max(0, cy-1), min(ink.shape[0], cy+2)):
                for nx in range(max(0, cx-1), min(ink.shape[1], cx+2)):
                    if ink[ny, nx] and not visited[ny, nx]:
                        visited[ny, nx] = True
                        stack.append((nx, ny))
        positions = np.array(component)
        center = (x0+float(positions[:, 0].mean()), y0+float(positions[:, 1].mean()))
        radius = max(2., .5*(int(np.ptp(positions, axis=0).max())+1))
        result.append((center, radius))
    return result


def full_scene_training(repo: Path, budget: TrainingCpuBudget):
    report = verified_json(repo, {"path": LAYOUT_REPORT.as_posix(), "sha256": LAYOUT_SHA256})
    refs = [r for r in report["sources"] if r["split"] == "train"]
    if len(refs) != 20 or len({r["image"]["sha256"] for r in refs}) != 20:
        raise ValueError("The fixed twenty owned training sources changed")
    rows, patches, sources = [], [], []
    omitted_overlap = 0
    for ref in refs:
        with budget.work_block():
            scene = verified_json(repo, ref["scene"])
            annotation = verified_json(repo, ref["annotation"])
            path = repo / ref["image"]["path"]
            if sha256(path) != ref["image"]["sha256"]:
                raise ValueError("Owned training raster changed")
            if scene["coordinate_space"] != "original_pixels" or scene["seed"] != ref["seed"]:
                raise ValueError("Owned source geometry or identity changed")
            with Image.open(path) as image:
                pixels = original_luminance(image)
            points = [p for panel in scene["panels"] for p in panel["points"]]
            sources.append({"split": "train", "seed": ref["seed"],
                            **{k: ref[k] for k in ("image", "scene", "annotation")}})

            def append(center, radius, shape, fill, identity, context):
                patch = extract_original_patch(pixels, center, radius)
                sample_id = hashlib.sha256(f'{coverage.VERSION}:{ref["image"]["sha256"]}:{identity}'.encode()).hexdigest()
                target_fill = "unknown" if fill == "degraded" or shape in ("asterisk", "cross") else fill
                rows.append({"sample_id": sample_id, "split": "train", "family": "owned-complete-graph",
                    "shape": shape, "fill": fill, "context": context, "artifact": shape is None,
                    "shape_index": -1 if shape is None else SHAPES.index(shape),
                    "fill_index": -1 if shape is None else FILL_NAMES.index(target_fill),
                    "source_sha256": ref["image"]["sha256"], "source_seed": ref["seed"],
                    "source_identity": identity, "crop_center": center, "crop_radius": radius,
                    "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest()})
                patches.append(patch)

            for point in points:
                # Every one of the 500 authored training points gets all three
                # crops, independent of either classifier's predictions.
                for index, (factor, shift) in enumerate(((1., 0.), (.8, .5), (1.3, -.5))):
                    center = (point["center"][0]+shift, point["center"][1]-shift)
                    append(center, point["radius"]*factor, point["shape"], point["fill"],
                           f'{point["point_id"]}:{index}', "complete_graph_marker")
            texts = annotation.get("texts", []) or [t for p in annotation["panels"] for t in p["texts"]]
            for text in texts:
                if not text.get("visible", True):
                    continue
                for index, (center, radius) in enumerate(text_components(pixels, text.get("rendered_pixel_box", text["box"]))):
                    # A negative crop containing a real marker is an invalid
                    # label, not an opportunity to teach the model rejection.
                    extent = max(4., 2.25*radius)
                    if any(abs(center[0]-p["center"][0]) <= extent+p["radius"] and
                           abs(center[1]-p["center"][1]) <= extent+p["radius"] for p in points):
                        omitted_overlap += 1
                        continue
                    append(center, radius, None, None, f'{text["text_id"]}:{index}', "complete_graph_text")
    if sum(not r["artifact"] for r in rows) != 1500:
        raise ValueError("The complete 500-point training population changed")
    return np.stack(patches), rows, {"sources": sources, "negative_crops_omitted_for_true_marker_overlap": omitted_overlap}


def validate_pixels(pixels: np.ndarray, rows: list[dict], split: str) -> None:
    if (pixels.shape != (len(rows), 1, 32, 32) or pixels.dtype != np.float32 or
            not np.isfinite(pixels).all() or pixels.min() < 0 or pixels.max() > 1 or
            len({r["sample_id"] for r in rows}) != len(rows)):
        raise ValueError("Graph-context tensor inventory or range changed")
    for row, patch in zip(rows, pixels, strict=True):
        if row["split"] != split or hashlib.sha256(patch.tobytes()).hexdigest() != row["patch_sha256"]:
            raise ValueError("Graph-context split or patch identity changed")
        if row["artifact"]:
            if row["shape_index"] != -1 or row["fill_index"] != -1:
                raise ValueError("Negative patch acquired a marker label")
        elif row["shape_index"] not in range(9) or row["fill_index"] not in range(3) or np.ptp(patch) == 0:
            raise ValueError(f"Invisible marker or unknown marker target: {row['sample_id']}")
    for row, target in zip(rows, observable_targets(rows), strict=True):
        if any(row.get(k) != v for k, v in target.items()):
            raise ValueError("Graph-context observability target changed")


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    output.mkdir(parents=True, exist_ok=False)
    budget = TrainingCpuBudget()
    source_hashes = {p: sha256(repo/p) for p in SOURCE_PATHS}
    for path in SOURCE_PATHS:
        target = output / "source-snapshot" / path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/path, target)
    with budget.work_block():
        base = load_native_cache(repo/BASE_CACHE, BASE_SHA256)
        shutil.copyfile(repo/BASE_CACHE/"manifest.json", output/"source-snapshot-base-manifest.json")
    recipes = coverage.cases()
    new_pixels = np.empty((len(recipes), 1, 32, 32), np.float32)
    new_rows = []
    for start in range(0, len(recipes), 64):
        with budget.work_block():
            for i in range(start, min(start+64, len(recipes))):
                new_pixels[i], row = coverage.prepare_case(recipes[i])
                new_rows.append(row)
    full_pixels, full_rows, full_sources = full_scene_training(repo, budget)
    with budget.work_block():
        pixels = np.concatenate((base["train"][0], new_pixels, full_pixels))
        rows = base["train"][1] + new_rows + full_rows
        for row, target in zip(rows, observable_targets(rows), strict=True):
            row.update(target)
        np.save(output/"train.npy", pixels, allow_pickle=False)
        write_json(output/"train.json", rows)
        invisible = [r["sample_id"] for r, p in zip(rows, pixels, strict=True) if not r["artifact"] and np.ptp(p) == 0]
        write_json(output/"visibility-audit.json", {"invisible_marker_ids": invisible, "count": len(invisible)})
        validate_pixels(pixels, rows, "train")
        # Preserve the exact previous evaluation tensors, rows and labels.
        for suffix in ("npy", "json"):
            shutil.copyfile(repo/BASE_CACHE/f"dev.{suffix}", output/f"dev.{suffix}")
        records = {"train": rows, "dev": base["dev"][1]}
        audit = audit_records(records)
    if source_hashes != {p: sha256(repo/p) for p in SOURCE_PATHS}:
        raise RuntimeError("Source changed during preparation")
    manifest = {"version": coverage.VERSION, "source_sha256": source_hashes,
        "base_cache_sha256": BASE_SHA256, "base_cache_path": BASE_CACHE.as_posix(),
        "layout_report_sha256": LAYOUT_SHA256, "train_scene_sources": full_sources,
        "additional_recipe_count": len(recipes), "full_scene_training_count": len(full_rows),
        "audit": audit, "files": {split: {suffix: sha256(output/f"{split}.{suffix}") for suffix in ("npy", "json")}
                                   for split in ("train", "dev")},
        "seconds": time.perf_counter()-started, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
        "scope": "Owned train-only extension. Exact previous dev bytes retained; full-scene dev pixels are not loaded here.",
        "negative_label_rule": "All authored text components except crops overlapping a true marker; no prediction-based selection.",
        "blank_negative_rule": "An erased negative remains a valid non-marker; a blank positive is invalid.",
        "ambiguity_rule": "Shared exact-pixel shape alternatives and unknown fill; all authored rows retained."}
    write_json(output/"manifest.json", manifest)
    return manifest


def load_cache(directory: Path, expected_sha256: str) -> dict:
    if sha256(directory/"manifest.json") != expected_sha256:
        raise ValueError("Graph-context manifest checksum mismatch")
    manifest = json.loads((directory/"manifest.json").read_text())
    if (manifest["version"] != coverage.VERSION or manifest["base_cache_sha256"] != BASE_SHA256 or
            manifest["layout_report_sha256"] != LAYOUT_SHA256 or manifest["audit"]["status"] != "valid"):
        raise ValueError("Graph-context cache is not valid owned coverage")
    result = {}
    for split in ("train", "dev"):
        for suffix in ("npy", "json"):
            if sha256(directory/f"{split}.{suffix}") != manifest["files"][split][suffix]:
                raise ValueError("Graph-context data checksum mismatch")
        pixels = np.load(directory/f"{split}.npy", allow_pickle=False)
        rows = json.loads((directory/f"{split}.json").read_text())
        validate_pixels(pixels, rows, split)
        result[split] = pixels, rows
    base_manifest = json.loads((directory/"source-snapshot-base-manifest.json").read_text())
    if sha256(directory/"source-snapshot-base-manifest.json") != BASE_SHA256:
        raise ValueError("Historical dev binding changed")
    if manifest["files"]["dev"] != base_manifest["files"]["dev"]:
        raise ValueError("Historical development examples changed")
    if audit_records({s: data[1] for s, data in result.items()}) != manifest["audit"]:
        raise ValueError("Graph-context audit changed")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({"audit": report["audit"], "seconds": report["seconds"]}))
