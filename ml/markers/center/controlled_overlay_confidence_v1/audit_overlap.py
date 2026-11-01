# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Audit exact controlled-patch and ink-plane overlap without model predictions."""

from __future__ import annotations

from collections import defaultdict
import hashlib
from io import BytesIO
import json
from pathlib import Path

import numpy as np

from .baseline import DATASET_SHA, RUN


def select_unique(build: list[tuple[str, str, int]], holdout: list[tuple[str, str, int]]) -> dict:
    if not build or not holdout:
        raise ValueError("both partitions require patches")
    labels_by_ink = defaultdict(set)
    for full, ink, label in build + holdout:
        if (not isinstance(full, str) or not isinstance(ink, str) or len(full) != 64 or len(ink) != 64 or
                label not in (0, 1)):
            raise ValueError("invalid patch identity or label")
        labels_by_ink[ink].add(label)
    if any(len(labels) != 1 for labels in labels_by_ink.values()):
        raise ValueError("identical ink has conflicting controlled labels")
    build_full = {full for full, _, _ in build}
    build_ink = {ink for _, ink, _ in build}
    first_build, first_holdout, used_build, used_holdout = [], [], set(), set()
    for index, (_, ink, _) in enumerate(build):
        if ink not in used_build:
            first_build.append(index)
            used_build.add(ink)
    exact_overlap = sum(full in build_full for full, _, _ in holdout)
    ink_overlap = sum(ink in build_ink for _, ink, _ in holdout)
    for index, (_, ink, _) in enumerate(holdout):
        if ink not in build_ink and ink not in used_holdout:
            first_holdout.append(index)
            used_holdout.add(ink)
    return {"build_indices": first_build, "holdout_indices": first_holdout,
            "holdout_exact_overlap": exact_overlap, "holdout_ink_overlap": ink_overlap,
            "build_duplicate_ink_excluded": len(build) - len(first_build),
            "holdout_duplicate_novel_ink_excluded": len(holdout) - ink_overlap - len(first_holdout)}


def load_partition(dataset: Path, entry: dict) -> tuple[dict, list[tuple[str, str, int]]]:
    directory = dataset / entry["partition"]
    arrays = {}
    for name in ("patches", "labels", "groups", "images", "patch_sha256"):
        raw = (directory / f"{name}.npy").read_bytes()
        if hashlib.sha256(raw).hexdigest() != entry["files"][name]:
            raise ValueError("dataset file hash differs")
        arrays[name] = np.load(BytesIO(raw), allow_pickle=False)
    patches, labels = arrays["patches"], arrays["labels"]
    n = entry["patches"]
    if (patches.shape != (n, 3, 33, 33) or patches.dtype != np.float32 or
            any(len(value) != n for value in arrays.values()) or not np.isfinite(patches).all() or
            patches.min() < 0 or patches.max() > 1 or not np.isin(labels, (0, 1)).all() or
            int(labels.sum()) != entry["positive"] or len(set(arrays["groups"].tolist())) != entry["groups"]):
        raise ValueError("dataset partition differs")
    identities = []
    for patch, label, expected in zip(patches, labels, arrays["patch_sha256"], strict=True):
        full = hashlib.sha256(patch.tobytes(order="C")).hexdigest()
        if full != expected:
            raise ValueError("patch hash differs")
        identities.append((full, hashlib.sha256(patch[0].tobytes(order="C")).hexdigest(), int(label)))
    return arrays, identities


def main() -> None:
    dataset = RUN / "controlled-domain-dataset-v1"
    raw = (dataset / "report.json").read_bytes()
    if hashlib.sha256(raw).hexdigest() != DATASET_SHA:
        raise ValueError("dataset receipt differs")
    report = json.loads(raw)
    if (report["schema"] != "graphreader.controlled-marker-domain-dataset.v1" or
            report["scope"] != "train-only-controlled-overlays" or report["optimizer_steps"] != 0 or
            report["training_approved"] is not False or report["sealed_payload_reads"] != 0 or
            report["native_unmarked_content_labeled_negative"] is not False):
        raise ValueError("dataset scope differs")
    entries = {item["partition"]: item for item in report["partitions"]}
    if set(entries) != {"internal_train_build", "internal_train_holdout"}:
        raise ValueError("dataset partitions differ")
    build, build_ids = load_partition(dataset, entries["internal_train_build"])
    holdout, holdout_ids = load_partition(dataset, entries["internal_train_holdout"])
    if set(build["groups"].tolist()) & set(holdout["groups"].tolist()):
        raise ValueError("source groups overlap")
    result = select_unique(build_ids, holdout_ids)
    build_idx = np.array(result.pop("build_indices"), dtype=np.int64)
    holdout_idx = np.array(result.pop("holdout_indices"), dtype=np.int64)
    def counts(arrays: dict, indices: np.ndarray) -> dict:
        return {"patches": len(indices), "positive": int(arrays["labels"][indices].sum()),
                "distractor": int(len(indices) - arrays["labels"][indices].sum()),
                "groups": len(set(arrays["groups"][indices].tolist())),
                "images": len(set(arrays["images"][indices].tolist()))}
    out = RUN / "controlled-overlap-audit-v1"
    out.mkdir(exist_ok=False)
    files = {}
    for name, indices in (("build_indices", build_idx), ("holdout_indices", holdout_idx)):
        path = out / f"{name}.npy"
        with path.open("xb") as stream:
            np.save(stream, indices, allow_pickle=False)
        files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    value = {"schema": "graphreader.controlled-marker-overlap-audit.v1", "scope": "train-only-metadata-audit",
             "dataset_sha256": DATASET_SHA, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
             "optimizer_steps": 0, "model_inference_steps": 0, "development_reads": 0, "sealed_payload_reads": 0,
             "training_approved": False, "build": counts(build, build_idx), "holdout": counts(holdout, holdout_idx),
             "exclusions": result, "files": files}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (out / "report.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"build": value["build"], "holdout": value["holdout"], "exclusions": result,
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
