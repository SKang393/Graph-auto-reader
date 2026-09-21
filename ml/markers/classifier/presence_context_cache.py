# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Presence-only labels for every owned training component, never guessed shapes."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
import os
from pathlib import Path
import shutil
import time

import numpy as np

from ml.markers.center.real_range_generator_v1.generator import build_split
from . import structural_context_cache as parent
from .native_context_cache import sha256, write_json
from .native_context_v3.runner import WorkBudget
from .runtime_patches import extract_original_patch

VERSION = "marker-classifier-presence-context-cache-v1"
BASE = Path("artifacts/goal22-runs/classifier-structural-context-cache-v1")
BASE_SHA = "8cf200793651e65aff02661ef9e8fcbe6fbe9281a618170d21da8552c02c259f"
# Crop uncertainty is authored on training data, independent of predictions.
CROPS = ((.7, 0., 0.), (1., .5, -.5), (1.3, -.5, .5),
         (.85, 1.25, -.75), (1.15, -1.25, .75), (1., 0., 1.5))
SOURCE_PATHS = tuple(dict.fromkeys((*parent.SOURCE_PATHS,
    "ml/markers/classifier/presence_context_cache.py",
    "ml/markers/center/real_range_generator_v1/generator.py",
    "ml/markers/center/dataset.py")))


def crop_radius(diameter, factor):
    return float(np.clip(diameter*.5*factor, 2.5, 8.))


def validate(pixels, rows):
    if (pixels.dtype != np.float32 or pixels.shape != (len(rows), 1, 32, 32)
            or not np.isfinite(pixels).all() or (pixels < 0).any() or (pixels > 1).any()
            or len({r["sample_id"] for r in rows}) != len(rows)):
        raise ValueError("Presence crop inventory or pixels changed")
    for row, patch in zip(rows, pixels, strict=True):
        import hashlib
        if (row["split"] != "train" or row["target_kind"] != "presence_only"
                or row.get("shape") is not None or row.get("fill") is not None
                or row["patch_sha256"] != hashlib.sha256(patch.tobytes()).hexdigest()
                or (not row["artifact"] and patch.max() <= 0)):
            raise ValueError("Invalid split, invented identity, or invisible positive")


def audit(base, rows):
    labels = defaultdict(set)
    for row in base["train"][1]+rows:
        labels[row["patch_sha256"]].add(row["artifact"])
    dev = {r["patch_sha256"] for r in base["dev"][1]}
    overlaps = sorted(set(labels) & dev)
    conflicts = sorted(k for k, v in labels.items() if len(v) != 1)
    return {"status": "valid" if not overlaps and not conflicts else "invalid",
            "train_dev_exact_pixel_overlap": overlaps, "contradictory_presence_targets": conflicts,
            "supplement_rows": len(rows), "supplement_markers": sum(not r["artifact"] for r in rows),
            "supplement_artifacts": sum(r["artifact"] for r in rows),
            "contexts": dict(Counter(r["context"] for r in rows))}


def prepare(root: Path, output: Path):
    import hashlib
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the existing CPU guard with passive waits")
    started = time.perf_counter()
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output/"CANCEL")
    sources = {p: sha256(root/p) for p in SOURCE_PATHS}
    for path in sources:
        target = output/"source-snapshot"/path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root/path, target)
    with budget.work_block():
        base = parent.load_cache(root/BASE, BASE_SHA)
        scenes = build_split("train")
        if len(scenes) != 167 or sum(len(s.centers) for s in scenes) != 2004:
            raise ValueError("Existing training component inventory changed")
    rows, patches, inventory = [], [], []
    for scene in scenes:
        with budget.work_block():
            if scene.split != "train" or scene.seed not in range(4100, 4267):
                raise ValueError("Only the complete original training component split is allowed")
            scene_sha = hashlib.sha256(scene.tensor.numpy().tobytes()).hexdigest()
            inventory.append({"seed": scene.seed, "sha256": scene_sha, "truth_count": len(scene.centers)})
            luminance = 1-scene.tensor[0].numpy()

            def append(center, radius, artifact, identity, context):
                patch = extract_original_patch(luminance, center, radius)
                row = {"sample_id": hashlib.sha256(f"{VERSION}:{scene_sha}:{identity}".encode()).hexdigest(),
                       "source_sha256": scene_sha, "seed": scene.seed, "split": "train",
                       "target_kind": "presence_only", "shape": None, "fill": None,
                       "artifact": artifact, "context": context, "crop_center": center, "crop_radius": radius,
                       "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest()}
                rows.append(row); patches.append(patch)

            for index, (center, diameter) in enumerate(zip(scene.centers, scene.rendered_diameters, strict=True)):
                for crop, (factor, dx, dy) in enumerate(CROPS):
                    append((center[0]+dx, center[1]+dy), crop_radius(diameter, factor), False,
                           f"marker:{index}:{crop}", "component_marker")
            for index, (kind, x, y) in enumerate(scene.hard_negatives):
                for radius in (2.5, 4., 6.):
                    extent = 2.25*radius+2
                    if any(abs(x-cx) <= extent+d*.5 and abs(y-cy) <= extent+d*.5
                           for (cx, cy), d in zip(scene.centers, scene.rendered_diameters, strict=True)):
                        continue
                    append((x+.375, y-.625), radius, True, f"negative:{index}:{radius}", kind)
    with budget.work_block():
        pixels = np.stack(patches)
        validate(pixels, rows)
        audited = audit(base, rows)
        if audited["status"] != "valid" or audited["supplement_markers"] != 2004*len(CROPS):
            raise ValueError("Presence split/target audit failed")
        np.save(output/"presence.npy", pixels, allow_pickle=False)
        write_json(output/"presence.json", rows)
        for suffix in ("json", "npy"):
            shutil.copyfile(root/BASE/f"dev.{suffix}", output/f"dev.{suffix}")
        manifest = {"version": VERSION, "parent_path": BASE.as_posix(), "parent_manifest_sha256": BASE_SHA,
                    "source_sha256": sources, "train_inventory": inventory, "train_scenes": len(scenes),
                    "parent_train_rows": len(base["train"][1]), "audit": audited, "crop_recipes": CROPS,
                    "files": {p.name: sha256(p) for p in output.iterdir() if p.is_file()},
                    "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
                    "scope": "Every training marker receives every fixed crop. Only presence is labeled; shape and fill are unspecified.",
                    "seconds": time.perf_counter()-started}
        if sources != {p: sha256(root/p) for p in sources}:
            raise ValueError("Presence preparation source changed")
        write_json(output/"manifest.json", manifest)
    return manifest


def load(root: Path, directory: Path, expected_sha: str):
    if sha256(directory/"manifest.json") != expected_sha:
        raise ValueError("Presence manifest changed")
    manifest = json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    if manifest["version"] != VERSION or manifest["parent_manifest_sha256"] != BASE_SHA:
        raise ValueError("Presence cache lineage changed")
    for name, expected in manifest["files"].items():
        if sha256(directory/name) != expected:
            raise ValueError("Presence cache bytes changed")
    base = parent.load_cache(root/BASE, BASE_SHA)
    pixels = np.load(directory/"presence.npy", allow_pickle=False)
    rows = json.loads((directory/"presence.json").read_text(encoding="utf-8"))
    validate(pixels, rows)
    if audit(base, rows) != manifest["audit"] or manifest["audit"]["status"] != "valid":
        raise ValueError("Presence targets or split audit changed")
    for suffix in ("npy", "json"):
        if sha256(directory/f"dev.{suffix}") != sha256(root/BASE/f"dev.{suffix}"):
            raise ValueError("Previous classifier development bytes changed")
    return base, pixels, rows, manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare(Path.cwd(), args.output)
    print(json.dumps({k: result[k] for k in ("audit", "seconds")}))
