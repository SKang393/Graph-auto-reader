# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Materialize and authenticate only the owned native-context train/dev recipes."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import asdict
import hashlib
import json
from pathlib import Path
import time

import numpy as np

from ml.training_cpu_budget import TrainingCpuBudget
from .native_context_data import FAMILIES, VERSION, cases, prepare_case

SOURCE_PATHS = (
    "ml/markers/classifier/native_context_cache.py",
    "ml/markers/classifier/native_context_data.py",
    "ml/markers/classifier/runtime_patches.py",
    "ml/markers/classifier/runtime_diagnostic.py",
    "ml/synthetic/renderer.py", "ml/synthetic/fonts.py", "ml/training_cpu_budget.py",
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def observable_targets(rows: list[dict]) -> list[dict]:
    """Keep authored truth, but do not train contradictory exact-pixel labels.

    This uses one split's pixels/labels only, never model predictions. Shape
    ambiguity gets a uniform target over its authored alternatives. Fill that
    cannot be distinguished gets the existing unknown label. Authored shape
    accuracy still counts every case separately during evaluation.
    """
    groups = defaultdict(list)
    for row in rows:
        groups[row["patch_sha256"]].append(row)
    output = []
    for row in rows:
        equivalent = groups[row["patch_sha256"]]
        shapes = sorted({r["shape_index"] for r in equivalent})
        fills = {r["fill_index"] for r in equivalent}
        output.append({
            "shape_target_indices": shapes,
            "fill_target_index": 2 if len(fills) > 1 else row["fill_index"],
            "shape_label_ambiguous": len(shapes) > 1,
            "fill_label_ambiguous": len(fills) > 1,
        })
    return output


def audit_records(records: dict[str, list[dict]]) -> dict:
    """Report exact tensor leakage and incompatible labels without dropping rows."""
    labels, targets = defaultdict(set), defaultdict(set)
    hashes = {}
    for split, rows in records.items():
        hashes[split] = {r["patch_sha256"] for r in rows}
        for row in rows:
            labels[row["patch_sha256"]].add((row["artifact"], row["shape_index"], row["fill_index"]))
            targets[row["patch_sha256"]].add((row["artifact"], tuple(row["shape_target_indices"]), row["fill_target_index"]))
    conflicts = sorted(key for key, value in labels.items() if len(value) > 1)
    target_conflicts = sorted(key for key, value in targets.items() if len(value) > 1)
    overlap = sorted(hashes["train"] & hashes["dev"])
    return {
        "status": "valid" if not target_conflicts and not overlap else "invalid",
        "cross_split_identical_tensors": overlap,
        "conflicting_label_tensors": conflicts,
        "conflicting_training_targets": target_conflicts,
        "splits": {split: {
            "count": len(rows), "unique_tensors": len(hashes[split]),
            "markers": sum(not row["artifact"] for row in rows),
            "artifacts": sum(row["artifact"] for row in rows),
            "shape_ambiguous_rows": sum(row["shape_label_ambiguous"] for row in rows),
            "fill_ambiguous_rows": sum(row["fill_label_ambiguous"] for row in rows),
            "families": dict(Counter(row["family"] for row in rows)),
            "contexts": dict(Counter(row["context"] for row in rows)),
        } for split, rows in records.items()},
    }


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    output.mkdir(parents=True, exist_ok=False)
    sources = {path: sha256(repo / path) for path in SOURCE_PATHS}
    snapshot = output / "source-snapshot"
    for path in SOURCE_PATHS:
        destination = snapshot / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes((repo / path).read_bytes())
    budget = TrainingCpuBudget()
    records, files = {}, {}
    for split in ("train", "dev"):
        recipes = cases(split)
        patches = np.empty((len(recipes), 1, 32, 32), dtype=np.float32)
        rows = []
        for start in range(0, len(recipes), 64):
            with budget.work_block():
                for index in range(start, min(start + 64, len(recipes))):
                    patches[index], record = prepare_case(recipes[index])
                    rows.append(record)
        with budget.work_block():
            if (not np.isfinite(patches).all() or patches.min() < 0 or patches.max() > 1 or
                    np.any(np.ptp(patches, axis=(1, 2, 3)) == 0)):
                raise ValueError("Blank, non-finite or unnormalized native-context patch")
            for row, targets in zip(rows, observable_targets(rows), strict=True):
                row.update(targets)
            np.save(output / f"{split}.npy", patches, allow_pickle=False)
            write_json(output / f"{split}.json", rows)
            records[split] = rows
            files[split] = {suffix: sha256(output / f"{split}.{suffix}") for suffix in ("npy", "json")}
    if sources != {path: sha256(repo / path) for path in SOURCE_PATHS}:
        raise RuntimeError("Preparation source changed while rendering")
    report = {
        "version": VERSION, "source_sha256": sources, "files": files,
        "families": [asdict(family) for family in FAMILIES],
        "audit": audit_records(records), "seconds": time.perf_counter() - started,
        "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
        "scope": "Owned synthetic train/dev only; shared drawing primitives, not independent real evidence",
        "ambiguity_rule": "Exact-pixel shape alternatives receive uniform targets; indistinguishable fill is unknown. All authored labels and rows are retained; shape accuracy uses the original authored label.",
    }
    write_json(output / "manifest.json", report)
    return report


def load_cache(directory: Path, expected_sha256: str) -> dict[str, tuple[np.ndarray, list[dict]]]:
    """Fail closed on altered files, labels, recipes, split overlap or tensor data."""
    manifest_path = directory / "manifest.json"
    if sha256(manifest_path) != expected_sha256:
        raise ValueError("Native-context manifest checksum mismatch")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest["version"] != VERSION or manifest["audit"]["status"] != "valid":
        raise ValueError("Native-context cache is not an audited current recipe")
    result = {}
    for split in ("train", "dev"):
        for suffix in ("npy", "json"):
            if sha256(directory / f"{split}.{suffix}") != manifest["files"][split][suffix]:
                raise ValueError("Native-context data checksum mismatch")
        pixels = np.load(directory / f"{split}.npy", allow_pickle=False)
        rows = json.loads((directory / f"{split}.json").read_text(encoding="utf-8"))
        recipes = cases(split)
        if pixels.shape != (len(recipes), 1, 32, 32) or pixels.dtype != np.float32 or len(rows) != len(recipes):
            raise ValueError("Native-context inventory changed")
        for recipe, row, patch in zip(recipes, rows, pixels, strict=True):
            expected = json.loads(json.dumps(recipe.record()))
            if any(row.get(key) != value for key, value in expected.items()):
                raise ValueError("Native-context labels or source identity changed")
            if hashlib.sha256(patch.tobytes()).hexdigest() != row["patch_sha256"]:
                raise ValueError("Native-context patch checksum mismatch")
        for row, targets in zip(rows, observable_targets(rows), strict=True):
            if any(row.get(key) != value for key, value in targets.items()):
                raise ValueError("Native-context observability targets changed")
        if (not np.isfinite(pixels).all() or pixels.min() < 0 or pixels.max() > 1 or
                np.any(np.ptp(pixels, axis=(1, 2, 3)) == 0)):
            raise ValueError("Native-context pixels are invalid")
        result[split] = (pixels, rows)
    if audit_records({split: value[1] for split, value in result.items()}) != manifest["audit"]:
        raise ValueError("Native-context audit mismatch")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({"audit": report["audit"], "seconds": report["seconds"]}))
