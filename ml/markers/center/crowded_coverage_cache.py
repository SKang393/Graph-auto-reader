# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Append owned crowded positives while retaining exact historical train/dev bytes."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import shutil
import time

import torch

from ml.markers.gate_seal import sha256_file
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from . import crowded_coverage_data as coverage
from . import shape_coverage_cache as parent

VERSION = "marker-center-crowded-coverage-cache-v1"
PARENT_PATH = "artifacts/goal22-runs/center-shape-coverage-cache-v3"
PARENT_SHA256 = "d61e43d62891a5266dfd68dfd8286fdb0d28d685d2146763a9f49dfb24471700"
SOURCE_PATHS = tuple(dict.fromkeys((*parent.SOURCE_PATHS,
    "ml/markers/center/crowded_coverage_data.py", "ml/markers/center/crowded_coverage_cache.py")))


def load_parent(repo: Path):
    directory = repo/PARENT_PATH
    training, development, manifest = parent.load_cache(directory, PARENT_SHA256)
    # These are immutable previously generated tensors, not a regeneration.
    # Authenticate their original source archive; the current CPU guard is
    # independently bound in this cache's new preparation source snapshot.
    for name, digest in manifest["source_sha256"].items():
        if sha256_file(directory/"source-snapshot"/name) != digest:
            raise ValueError("Historical generation source archive changed")
    if sum(c["rows"] for c in manifest["counts"].values()) != 87638:
        raise ValueError("Historical training denominator changed")
    if {k: len(v) for k, v in development.items()} != {"component": 167, "family": 9}:
        raise ValueError("Frozen development scene denominator changed")
    return training, development, manifest


def audit_records(records: list[dict]) -> dict:
    expected = {case.sample_id for case in coverage.cases()}
    if len(records) != len(expected) or {r["sample_id"] for r in records} != expected:
        raise ValueError("Complete crowded training population changed")
    for row in records:
        if (row["split"] != "train" or row["family"] != coverage.VERSION
                or row["truth_count"] != len(row["markers"])
                or row["truth_count"] != len(row["visible_pixels_by_marker"])
                or min(row["visible_pixels_by_marker"]) <= 0
                or row["truth_count"] != len(row["positive_rows_by_marker"])
                or row["positive_rows"] != sum(row["positive_rows_by_marker"])):
            raise ValueError("Crowded target visibility or supervision changed")
    return {"scenes": len(records), "truths": sum(r["truth_count"] for r in records),
            "positive_rows": sum(r["positive_rows"] for r in records),
            "negative_rows": sum(r["negative_rows"] for r in records),
            "unsupported_truths": sum(len(r["unsupported_truth_indices"]) for r in records),
            "shape_counts": dict(Counter(m["shape"] for r in records for m in r["markers"])),
            "context_counts": dict(Counter(r["context"] for r in records))}


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the verified CPU guard with passive waits")
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output/"CANCEL")
    source_hashes = {name: sha256_file(repo/name) for name in SOURCE_PATHS}
    for name in SOURCE_PATHS:
        target = output/"source-snapshot"/name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/name, target)
    with budget.work_block():
        training, development, old = load_parent(repo)
        del training, development
    records, parts = [], [[] for _ in parent.NAMES]
    recipes = coverage.cases()
    for start in range(0, len(recipes), 32):
        with budget.work_block():
            for case in recipes[start:start+32]:
                sample = coverage.prepare_case(case)
                records.append(sample.record)
                for target, name in zip(parts, parent.NAMES, strict=True):
                    target.append(torch.from_numpy(getattr(sample, name)))
    with budget.work_block():
        values = tuple(torch.cat(p) for p in parts)
        counts = parent.validate_rows(values)
        inventory = audit_records(records)
        if counts["positive"] != inventory["positive_rows"] or counts["negative"] != inventory["negative_rows"]:
            raise ValueError("Cached labels do not match the authored population")
        torch.save(dict(zip(parent.NAMES, values, strict=True)), output/"train-crowded.pt")
        parent.write_json(output/"crowded.json", records)
        if source_hashes != {p: sha256_file(repo/p) for p in SOURCE_PATHS}:
            raise ValueError("Crowded preparation source changed during execution")
        manifest = {"version": VERSION, "parent_cache_path": PARENT_PATH, "parent_manifest_sha256": PARENT_SHA256,
            "files": {p.name: sha256_file(p) for p in output.iterdir() if p.is_file()},
            "source_sha256": source_hashes, "counts": counts, "inventory": inventory,
            "coverage_definition": coverage.definition(), "original_counts": old["counts"], "dev": old["dev"],
            "historical_generation_source_archive_verified": True,
            "all_original_training_rows_retained": True, "all_development_bytes_and_truth_unchanged": True,
            "radius_limitation": old["radius_limitation"], "seconds": time.perf_counter()-started,
            "cpu": budget.report(), "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0}
        parent.write_json(output/"manifest.json", manifest)
    return manifest


def load_cache(repo: Path, directory: Path, expected_sha256: str):
    if sha256_file(directory/"manifest.json") != expected_sha256:
        raise ValueError("Crowded cache manifest changed")
    manifest = json.loads((directory/"manifest.json").read_text())
    if (manifest["version"] != VERSION or manifest["parent_cache_path"] != PARENT_PATH
            or manifest["parent_manifest_sha256"] != PARENT_SHA256
            or manifest["coverage_definition"] != coverage.definition()
            or set(manifest["source_sha256"]) != set(SOURCE_PATHS)):
        raise ValueError("Crowded population or source inventory changed")
    for name, digest in manifest["source_sha256"].items():
        if sha256_file(repo/name) != digest or sha256_file(directory/"source-snapshot"/name) != digest:
            raise ValueError("Crowded preparation source changed")
    for name, digest in manifest["files"].items():
        if Path(name).name != name or sha256_file(directory/name) != digest:
            raise ValueError("Crowded cache file changed")
    records = json.loads((directory/"crowded.json").read_text())
    if audit_records(records) != manifest["inventory"]:
        raise ValueError("Crowded authored inventory changed")
    training, development, old = load_parent(repo)
    if old["dev"] != manifest["dev"] or old["counts"] != manifest["original_counts"]:
        raise ValueError("Historical population changed")
    payload = torch.load(directory/"train-crowded.pt", map_location="cpu", weights_only=True)
    values = tuple(payload[name] for name in parent.NAMES)
    if parent.validate_rows(values) != manifest["counts"]:
        raise ValueError("Crowded training tensor audit changed")
    training["crowded"] = values
    return training, development, manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({k: report[k] for k in ("counts", "inventory", "seconds")}))
