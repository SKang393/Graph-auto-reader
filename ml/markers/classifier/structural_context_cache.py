# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Append authored graph structures to training; retain all prior train/dev bytes."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import shutil
import time

import numpy as np
from PIL import Image

from ml.training_cpu_budget import TrainingCpuBudget
from . import graph_context_cache as parent
from .native_context_cache import audit_records, observable_targets, sha256, write_json
from .runtime_patches import extract_original_patch, original_luminance

VERSION = "classifier-structural-context-data-v1"
BASE_CACHE = Path("artifacts/goal22-runs/classifier-graph-context-cache-v3")
BASE_SHA256 = "5612c96ea4758595cbb66d609a85fa98728418c213a14b3d2f0c7800592bf164"
SOURCE_PATHS = (*parent.SOURCE_PATHS, "ml/markers/classifier/structural_context_cache.py")
RADII = (2.5, 4., 6.)
SHIFTS = ((.375, -.625), (-.625, .375))
MAX_ANCHORS_PER_KIND = 64


def records(annotation: dict, key: str) -> list[dict]:
    return annotation.get(key, []) or [r for p in annotation["panels"] for r in p.get(key, [])]


def segment_intersection(first, second):
    p, q = np.asarray(first[0], float), np.asarray(second[0], float)
    r, s = np.asarray(first[1], float)-p, np.asarray(second[1], float)-q
    cross = lambda a, b: float(a[0]*b[1]-a[1]*b[0])
    denominator = cross(r, s)
    if abs(denominator) < 1e-9:
        return None
    t, u = cross(q-p, s)/denominator, cross(q-p, r)/denominator
    return tuple(p+t*r) if 0 <= t <= 1 and 0 <= u <= 1 else None


def structural_anchors(annotation: dict) -> list[tuple[str, tuple[float, float]]]:
    """Coordinates come only from authored geometry, never a model's answers."""
    lines = []
    for kind, key in (("axis", "axes"), ("tick", "ticks"), ("divider", "dividers"),
                      ("connector", "edges"), ("arrow", "arrows"), ("top_bar", "top_bars")):
        for item in records(annotation, key):
            if item.get("visible", True) and item.get("drawn", True):
                lines.append((kind, item["line"]))
    for item in records(annotation, "brackets"):
        points = item["points"]
        lines.extend(("bracket", [a, b]) for a, b in zip(points, points[1:]))
    for item in records(annotation, "legends"):
        if not item.get("visible", True):
            continue
        x, y, w, h = item["box"]
        corners = [(x, y), (x+w, y), (x+w, y+h), (x, y+h), (x, y)]
        lines.extend(("legend_frame", [a, b]) for a, b in zip(corners, corners[1:]))
    result = set()
    for kind, line in lines:
        if np.asarray(line).shape != (2, 2) or not np.isfinite(line).all():
            raise ValueError("Authored structural line must have two finite original-pixel endpoints")
        a, b = np.asarray(line[0], float), np.asarray(line[1], float)
        intervals = max(1, math.ceil(float(np.linalg.norm(b-a))/8))
        for index in range(intervals+1):
            result.add((kind, tuple(a+(b-a)*(index/intervals))))
    connectors = [line for kind, line in lines if kind == "connector"]
    dividers = [line for kind, line in lines if kind == "divider"]
    for index, first in enumerate(connectors):
        for second in dividers+connectors[index+1:]:
            point = segment_intersection(first, second)
            if point is not None:
                result.add(("intersection", point))
    return sorted(result)


def protected_boxes(annotation: dict) -> list[tuple[float, float, float, float]]:
    boxes = [tuple(m["box"]) for m in records(annotation, "markers")]
    # Legend glyphs must remain usable by the same classifier for series naming.
    boxes += [tuple(e["glyph_box"]) for legend in records(annotation, "legends")
              if legend.get("visible", True) for e in legend["entries"]]
    if any(len(box) != 4 or not all(math.isfinite(v) for v in box) or min(box[2:]) <= 0 for box in boxes):
        raise ValueError("Marker and legend protection requires finite positive original-pixel boxes")
    return boxes


def clear_of_markers(center, radius, boxes) -> bool:
    # Include bilinear interpolation's extra pixel and print-damage spread.
    extent = max(4., 2.25*radius)+2.
    x, y = center
    return not any(x+extent >= bx and x-extent <= bx+bw and
                   y+extent >= by and y-extent <= by+bh for bx, by, bw, bh in boxes)


def select_anchors(annotation: dict, source_sha256: str):
    boxes = protected_boxes(annotation)
    groups = defaultdict(list)
    for kind, center in structural_anchors(annotation):
        if all(clear_of_markers((center[0]+dx, center[1]+dy), max(RADII), boxes) for dx, dy in SHIFTS):
            key = hashlib.sha256(f"{VERSION}:{source_sha256}:{kind}:{center}".encode()).hexdigest()
            groups[kind].append((key, center))
    return [(kind, key, center) for kind in sorted(groups)
            for key, center in sorted(groups[kind])[:MAX_ANCHORS_PER_KIND]]


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    output.mkdir(parents=True, exist_ok=False)
    budget = TrainingCpuBudget()
    sources = {p: sha256(repo/p) for p in SOURCE_PATHS}
    for name in SOURCE_PATHS:
        target = output/"source-snapshot"/name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/name, target)
    with budget.work_block():
        base = parent.load_cache(repo/BASE_CACHE, BASE_SHA256)
        layout = parent.verified_json(repo, {"path": parent.LAYOUT_REPORT.as_posix(), "sha256": parent.LAYOUT_SHA256})
        refs = [r for r in layout["sources"] if r["split"] == "train"]
        if len(refs) != 20 or len({r["image"]["sha256"] for r in refs}) != 20:
            raise ValueError("The twenty owned training sources changed")
    patches, rows, inventory = [], [], []
    for ref in refs:
        with budget.work_block():
            annotation = parent.verified_json(repo, ref["annotation"])
            source = repo/ref["image"]["path"]
            if sha256(source) != ref["image"]["sha256"]:
                raise ValueError("Owned training image changed")
            with Image.open(source) as image:
                pixels = original_luminance(image)
            anchors = select_anchors(annotation, ref["image"]["sha256"])
        for start in range(0, len(anchors), 32):
            with budget.work_block():
                for kind, identity, anchor in anchors[start:start+32]:
                    for radius in RADII:
                        for dx, dy in SHIFTS:
                            center = (anchor[0]+dx, anchor[1]+dy)
                            patch = extract_original_patch(pixels, center, radius)
                            sample_id = hashlib.sha256(f"{identity}:{radius}:{dx}:{dy}".encode()).hexdigest()
                            rows.append({"sample_id": sample_id, "split": "train", "family": VERSION,
                                "shape": None, "fill": None, "shape_index": -1, "fill_index": -1,
                                "artifact": True, "context": kind, "source_sha256": ref["image"]["sha256"],
                                "crop_center": center, "crop_radius": radius,
                                "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest()})
                            patches.append(patch)
        inventory.append({"split": "train", "image": ref["image"], "annotation": ref["annotation"],
                          "selected_anchors": dict(Counter(a[0] for a in anchors))})
    with budget.work_block():
        all_rows = base["train"][1]+rows
        for row, target in zip(all_rows, observable_targets(all_rows), strict=True):
            row.update(target)
        all_pixels = np.concatenate((base["train"][0], np.stack(patches)))
        parent.validate_pixels(all_pixels, all_rows, "train")
        audit = audit_records({"train": all_rows, "dev": base["dev"][1]})
        if audit["status"] != "valid":
            write_json(output/"invalid-audit.json", audit)
            raise ValueError("Structural extension has cross-split duplicates or contradictory labels")
        np.save(output/"train.npy", all_pixels, allow_pickle=False)
        write_json(output/"train.json", all_rows)
        for suffix in ("npy", "json"):
            shutil.copyfile(repo/BASE_CACHE/f"dev.{suffix}", output/f"dev.{suffix}")
        shutil.copyfile(repo/BASE_CACHE/"manifest.json", output/"parent-manifest.json")
    if sources != {p: sha256(repo/p) for p in SOURCE_PATHS}:
        raise RuntimeError("Preparation source changed")
    manifest = {"version": VERSION, "parent_cache_path": BASE_CACHE.as_posix(),
        "parent_manifest_sha256": BASE_SHA256, "layout_report_sha256": parent.LAYOUT_SHA256,
        "source_sha256": sources, "train_sources": inventory, "audit": audit,
        "original_train_rows": len(base["train"][1]), "added_negative_rows": len(rows),
        "files": {split: {suffix: sha256(output/f"{split}.{suffix}") for suffix in ("npy", "json")}
                  for split in ("train", "dev")},
        "seconds": time.perf_counter()-started, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
        "scope": "Authored train geometry only; no prediction mining, dev-image access, or label-dependent selection.",
        "protection": "Every crop avoids full data-marker and legend-glyph boxes plus two pixels of interpolation/ink spread.",
        "recipe": {"radii": RADII, "shifts": SHIFTS, "maximum_anchors_per_source_kind": MAX_ANCHORS_PER_KIND}}
    write_json(output/"manifest.json", manifest)
    return manifest


def load_cache(directory: Path, expected_sha256: str) -> dict:
    if sha256(directory/"manifest.json") != expected_sha256:
        raise ValueError("Structural cache manifest checksum mismatch")
    manifest = json.loads((directory/"manifest.json").read_text())
    if (manifest["version"] != VERSION or manifest["parent_manifest_sha256"] != BASE_SHA256 or
            manifest["layout_report_sha256"] != parent.LAYOUT_SHA256 or manifest["audit"]["status"] != "valid"):
        raise ValueError("Structural cache lineage or audit changed")
    if sha256(directory/"parent-manifest.json") != BASE_SHA256:
        raise ValueError("Parent cache binding changed")
    previous = json.loads((directory/"parent-manifest.json").read_text())
    if manifest["files"]["dev"] != previous["files"]["dev"]:
        raise ValueError("Historical development bytes changed")
    result = {}
    for split in ("train", "dev"):
        for suffix in ("npy", "json"):
            if sha256(directory/f"{split}.{suffix}") != manifest["files"][split][suffix]:
                raise ValueError("Structural cache file changed")
        pixels = np.load(directory/f"{split}.npy", allow_pickle=False)
        rows = json.loads((directory/f"{split}.json").read_text())
        parent.validate_pixels(pixels, rows, split)
        result[split] = pixels, rows
    if audit_records({s: value[1] for s, value in result.items()}) != manifest["audit"]:
        raise ValueError("Structural cache targets changed")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({k: result[k] for k in ("added_negative_rows", "audit", "seconds")}))
