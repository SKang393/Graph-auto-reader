# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Append train-only crowded glyphs; preserve every V5 row and dev byte."""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import shutil
import time

import numpy as np

from ml.training_cpu_budget import TrainingCpuBudget
from . import crowded_context_data as coverage
from . import structural_context_cache as parent
from .graph_context_cache import validate_pixels
from .native_context_cache import audit_records, observable_targets, sha256, write_json

BASE_CACHE = Path("artifacts/goal22-runs/classifier-structural-context-cache-v1")
BASE_SHA256 = "8cf200793651e65aff02661ef9e8fcbe6fbe9281a618170d21da8552c02c259f"
SOURCE_PATHS = (*parent.SOURCE_PATHS, "ml/markers/classifier/crowded_context_data.py",
                "ml/markers/classifier/crowded_context_cache.py")


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    output.mkdir(parents=True, exist_ok=False)
    def check_cancel():
        if (output/"CANCEL").exists():
            raise InterruptedError("Crowded-context preparation cancelled")
    budget = TrainingCpuBudget(cancellation_check=check_cancel)
    source_hashes = {p: sha256(repo/p) for p in SOURCE_PATHS}
    for name in SOURCE_PATHS:
        target = output/"source-snapshot"/name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/name, target)
    with budget.work_block():
        base = parent.load_cache(repo/BASE_CACHE, BASE_SHA256)
        original_rows = copy.deepcopy(base["train"][1])
        if len(original_rows) != 62512 or len(base["dev"][1]) != 3136:
            raise ValueError("V5 training/development inventory changed")
        recipes = coverage.cases()
        extra_pixels = np.empty((len(recipes), 1, 32, 32), np.float32)
    extra_rows = []
    for start in range(0, len(recipes), 32):
        with budget.work_block():
            for index in range(start, min(start+32, len(recipes))):
                try:
                    extra_pixels[index], row = coverage.prepare_case(recipes[index])
                except BaseException as error:
                    write_json(output/"invalid-recipe.json", {
                        "sample_id": recipes[index].sample_id, "index": index,
                        "error": str(error), "completed_examples": len(extra_rows),
                        "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0})
                    raise
                extra_rows.append(row)
    with budget.work_block():
        rows = base["train"][1] + extra_rows
        for row, target in zip(rows, observable_targets(rows), strict=True):
            row.update(target)
        if rows[:len(original_rows)] != original_rows:
            raise ValueError("New recipes would alter an earlier training target")
        pixels = np.concatenate((base["train"][0], extra_pixels))
        if not np.array_equal(pixels[:len(original_rows)], base["train"][0]):
            raise ValueError("Earlier training pixels changed")
        validate_pixels(pixels, rows, "train")
        audit = audit_records({"train": rows, "dev": base["dev"][1]})
        if audit["status"] != "valid":
            write_json(output/"invalid-audit.json", audit)
            raise ValueError("Crowded recipes leak splits or introduce conflicting targets")
        np.save(output/"train.npy", pixels, allow_pickle=False)
        write_json(output/"train.json", rows)
        for suffix in ("npy", "json"):
            shutil.copyfile(repo/BASE_CACHE/f"dev.{suffix}", output/f"dev.{suffix}")
        shutil.copyfile(repo/BASE_CACHE/"manifest.json", output/"parent-manifest.json")
    if source_hashes != {p: sha256(repo/p) for p in SOURCE_PATHS}:
        raise RuntimeError("Preparation source changed")
    manifest = {"version": coverage.VERSION, "parent_cache_path": BASE_CACHE.as_posix(),
        "parent_manifest_sha256": BASE_SHA256, "source_sha256": source_hashes,
        "preserved_train_rows": len(original_rows), "preserved_train_targets": True,
        "added_marker_rows": len(extra_rows), "added_artifact_rows": 0,
        "minimum_visible_target_pixels": min(r["target_visible_pixel_count"] for r in extra_rows),
        "audit": audit, "files": {split: {suffix: sha256(output/f"{split}.{suffix}")
            for suffix in ("npy", "json")} for split in ("train", "dev")},
        "seconds": time.perf_counter()-started, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
        "scope": "Every V5 positive and negative retained; new owned train-only crowding recipes. Previous development bytes copied unchanged. No prediction mining or private input.",
        "limits": "Shared rendering primitives remain synthetic evidence; no real-data or production claim."}
    write_json(output/"manifest.json", manifest)
    return manifest


def load_cache(directory: Path, expected_sha256: str) -> dict:
    if sha256(directory/"manifest.json") != expected_sha256:
        raise ValueError("Crowded-context manifest checksum mismatch")
    manifest = json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    if (manifest["version"] != coverage.VERSION or manifest["parent_manifest_sha256"] != BASE_SHA256 or
            manifest["audit"]["status"] != "valid" or manifest["preserved_train_rows"] != 62512 or
            manifest["added_marker_rows"] != 6912 or manifest["added_artifact_rows"] != 0 or
            manifest["minimum_visible_target_pixels"] <= 0):
        raise ValueError("Crowded-context lineage, targets or visibility changed")
    if sha256(directory/"parent-manifest.json") != BASE_SHA256:
        raise ValueError("V5 parent binding changed")
    previous = json.loads((directory/"parent-manifest.json").read_text(encoding="utf-8"))
    if manifest["files"]["dev"] != previous["files"]["dev"]:
        raise ValueError("Historical development bytes changed")
    result = {}
    for split in ("train", "dev"):
        for suffix in ("npy", "json"):
            if sha256(directory/f"{split}.{suffix}") != manifest["files"][split][suffix]:
                raise ValueError("Crowded-context cache file changed")
        pixels = np.load(directory/f"{split}.npy", allow_pickle=False)
        rows = json.loads((directory/f"{split}.json").read_text(encoding="utf-8"))
        validate_pixels(pixels, rows, split)
        result[split] = pixels, rows
    if audit_records({s: value[1] for s, value in result.items()}) != manifest["audit"]:
        raise ValueError("Crowded-context audit changed")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({key: result[key] for key in ("added_marker_rows", "minimum_visible_target_pixels", "audit", "seconds")}))
