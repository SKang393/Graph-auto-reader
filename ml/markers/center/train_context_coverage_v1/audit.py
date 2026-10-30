# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare fixed 33px and 65px raster coverage on saved synthetic train outputs.

This is an aggregate-only diagnosis, not a runtime filter or training recipe.
Synthetic truth identifies a nearest reference primitive for measurement only;
it is not evidence that a primitive caused a prediction. No model runs.
"""
from __future__ import annotations

import hashlib
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
import torch

PROJECT = Path(r"E:\Graph-auto-reader")
SOURCE = PROJECT / "artifacts/goal22-worktrees/resume-20260908"
OUT = PROJECT / "artifacts/goal22-tools/train-context-coverage-v1/run"
ATTR = "artifacts/goal22-runs/reflection-train-extra-attribution-20261002-v3/report.json"
DENSE = "artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1/report.json"
PINS = {
    ATTR: "267524896b30952c0aa9749de685f43a68e1de4346054c7122fdcbc7d2b4ede3",
    DENSE: "c15e8a05af21c46d60d05b17a70bd2d9a8e7b72e9c1e034f016c6b9d95fc07b8",
    "ml/markers/center/real_range_generator_v1/generator.py":
        "2244094e8d8e3a8cacb83e11c5e7189ded46b25b1ce9ecdad1bd80e5dec78b41",
}


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def checked(relative: str, expected: str) -> Path:
    path = (SOURCE / relative).resolve(strict=True)
    if not path.is_relative_to(SOURCE.resolve()) or digest(path) != expected:
        raise ValueError(f"Train input identity mismatch: {relative}")
    return path


def context(mask: np.ndarray, x: int, y: int, radius: int) -> str:
    height, width = mask.shape
    total = int(mask.sum())
    if total == 0:
        raise ValueError("Reference glyph is empty")
    visible = int(mask[max(0, y-radius):min(height, y+radius+1),
                       max(0, x-radius):min(width, x+radius+1)].sum())
    return "whole" if visible == total else "partial" if visible else "none"


def marker_masks(scene: dict, scene_index: int, draw_marker) -> list[np.ndarray]:
    metadata = scene["metadata"]
    height, width = scene["tensor"].shape[-2:]
    masks = []
    for marker_index, (center, diameter) in enumerate(zip(metadata["centers"], metadata["diameters"], strict=True)):
        image = Image.new("L", (width, height), 255)
        draw = ImageDraw.Draw(image)
        xy = tuple(int(value) for value in center)
        if diameter == 1:
            draw.point(xy, fill=0)
        else:
            draw_marker(draw, xy, max(1, int(round(diameter / 2))),
                        scene_index * len(metadata["centers"]) + marker_index,
                        marker_index + scene_index)
        masks.append(np.asarray(image).copy() < 255)
        image.close()
    return masks


def main() -> None:
    if OUT.exists():
        raise ValueError("Refusing to overwrite the previous audit")
    for path, expected in PINS.items():
        checked(path, expected)
    attr = json.loads(checked(ATTR, PINS[ATTR]).read_text(encoding="utf-8-sig"))
    dense = json.loads(checked(DENSE, PINS[DENSE]).read_text(encoding="utf-8-sig"))
    if any(doc.get(key) != 0 for doc in (attr, dense)
           for key in ("private_reads", "sealed_reads", "optimizer_steps")):
        raise ValueError("Frozen evidence scope changed")
    if attr.get("development_panels_evaluated") != 0:
        raise ValueError("Attribution includes development panels")
    refs = {item["path"]: item["sha256"] for item in attr["evidence"]}
    evidence = {item["path"]: item["sha256"] for item in dense["evidence"]}
    pred_path = next(path for path in refs if path.endswith("/predictions.json"))
    scenes_path = next(path for path in refs if path.endswith("/train-scenes.pt"))
    rows = json.loads(checked(pred_path, refs[pred_path]).read_text(encoding="utf-8-sig"))
    scenes = torch.load(checked(scenes_path, refs[scenes_path]), map_location="cpu", weights_only=True)
    component = [row for row in rows if row["scope"] == "component"]
    if len(rows) != 2702 or len(scenes["component"]) != 167 or Counter(row["outcome"] for row in component) != {"matched": 1982, "extra": 133}:
        raise ValueError("Training population changed")
    sys.path.insert(0, str(SOURCE))
    from ml.markers.center.real_range_generator_v1.generator import _draw_range_marker
    from ml.markers.center.shape_coverage_cache import tensor_digest

    counts = defaultdict(Counter)
    arrays_checked = 0
    for index, scene in enumerate(scenes["component"]):
        meta = scene["metadata"]
        if meta["split"] != "train" or tensor_digest(scene["tensor"]) != scene["tensor_sha256"]:
            raise ValueError("Non-training scene or tensor drift")
        identity = f"{meta['split']}:{meta['family']}:{meta['seed']}"
        prefix = f"artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1/component-train-{index:03d}"
        arrays = []
        for suffix in ("coordinates", "predictions"):
            path = f"{prefix}-{suffix}.npy"
            arrays.append(np.load(checked(path, evidence[path]), allow_pickle=False))
            arrays_checked += 1
        coordinates, predictions = arrays
        masks = marker_masks(scene, index, _draw_range_marker)
        centers = np.asarray(meta["centers"], dtype=np.float64)
        local = [row for row in component if row["scene_index"] == index]
        for row in local:
            if row["identity"] != identity:
                raise ValueError("Saved proposal scene identity changed")
            proposal = row["equivalent_proposal_indices"][0]
            if not 0 <= proposal < len(coordinates) or abs(float(predictions[proposal, 0]) - row["confidence"]) > 1e-7:
                raise ValueError("Saved proposal identity changed")
            anchor = coordinates[proposal]
            if not np.all(np.isfinite(anchor)) or not np.all(anchor == np.round(anchor)):
                raise ValueError("Nonintegral proposal coordinate")
            x, y = map(int, anchor)
            height, width = masks[0].shape
            if not (0 <= x < width and 0 <= y < height):
                raise ValueError("Proposal is outside the source image")
            decoded = anchor + predictions[proposal, 1:3] * 4
            nearest = int(np.linalg.norm(centers - decoded, axis=1).argmin())
            mask = masks[nearest]
            outcome = row["outcome"]
            short, wide = context(mask, x, y, 16), context(mask, x, y, 32)
            counts[outcome]["total"] += 1
            counts[outcome][f"crop33_{short}"] += 1
            counts[outcome][f"crop65_{wide}"] += 1
            if short != "whole" and wide == "whole":
                counts[outcome]["incomplete33_whole65"] += 1
            for radius, label in ((16, "33"), (32, "65")):
                other = any(context(other_mask, x, y, radius) != "none"
                            for i, other_mask in enumerate(masks) if i != nearest)
                if other:
                    counts[outcome][f"crop{label}_includes_other_primitive"] += 1
    if arrays_checked != 334 or counts["extra"]["total"] != 133 or counts["matched"]["total"] != 1982:
        raise ValueError("Incomplete train audit")
    output = {"schema": "graphreader.train-context-coverage.v1", "split": "train",
              "status": "descriptive_diagnostic", "counts": {k: dict(v) for k, v in counts.items()},
              "scene_count": 167, "checked_proposal_arrays": arrays_checked,
              "prior_attribution_sha256": PINS[ATTR], "dense_audit_sha256": PINS[DENSE],
              "predictions_sha256": refs[pred_path], "train_scenes_sha256": refs[scenes_path],
              "private_reads": 0, "sealed_reads": 0, "development_reads": 0,
              "model_inference_runs": 0, "optimizer_steps": 0,
              "limitations": ["Nearest synthetic truth is a geometric reference and may not cause an extra.",
                              "Primitive masks omit blur, noise, connectors and visual occlusion.",
                              "Crop coverage does not demonstrate model accuracy or justify a runtime threshold.",
                              "Family glyph masks were not inferred."]}
    OUT.mkdir(parents=True)
    with (OUT / "report.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(output, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps(output, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
