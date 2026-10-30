# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Verify exact selected-row recovery from fixed train-only raster evidence.

This preflight reads no development, private or sealed data and writes no
training outputs. It runs no model and performs no optimizer steps.
"""
from __future__ import annotations

import hashlib
from io import BytesIO
import json
from pathlib import Path

import numpy as np
import torch

from .crops import crop_context
from ..shape_coverage_cache import tensor_digest

PROJECT = Path(r"E:\Graph-auto-reader")
SOURCE = PROJECT / "artifacts/goal22-worktrees/resume-20260908"
ATTR = "artifacts/goal22-runs/reflection-train-extra-attribution-20261002-v3/report.json"
DENSE = "artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1/report.json"
SELECTION = "artifacts/goal22-runs/marker-component-selection-coverage-preflight/v3/proposed-selection-metadata.npz"
CACHE = "artifacts/goal22-runs/center-shape-coverage-cache-v3/train-{}.pt"
PINS = {
    ATTR: "267524896b30952c0aa9749de685f43a68e1de4346054c7122fdcbc7d2b4ede3",
    DENSE: "c15e8a05af21c46d60d05b17a70bd2d9a8e7b72e9c1e034f016c6b9d95fc07b8",
    SELECTION: "d31a25f48170a375fae22f196344d1c8fc85d2297ddc3394c8303d08d34a246b",
    CACHE.format("family"): "662ea1e06189360ed6db4e1824ee5c6871d0d0f14009f08293dbc9da07e7c695",
}
# The component masks were constructed from marker coordinates and are not an
# allowed input to this candidate, regardless of their historical cache parity.
EXPECTED = {"family": (28, 9053)}


def verified_bytes(path: str, expected: str) -> bytes:
    resolved = (SOURCE / path).resolve(strict=True)
    if not resolved.is_relative_to(SOURCE.resolve()):
        raise ValueError("Input is outside the owned project source")
    data = resolved.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError(f"Source identity changed: {path}")
    return data


def verify() -> dict:
    attr = json.loads(verified_bytes(ATTR, PINS[ATTR]).decode("utf-8-sig"))
    dense = json.loads(verified_bytes(DENSE, PINS[DENSE]).decode("utf-8-sig"))
    if (attr.get("development_panels_evaluated") != 0 or any(
        report.get(key) != 0 for report in (attr, dense)
        for key in ("private_reads", "sealed_reads", "optimizer_steps")
    )):
        raise ValueError("Evidence scope changed")
    evidence = {entry["path"]: entry["sha256"] for entry in dense["evidence"]}
    attr_evidence = {entry["path"]: entry["sha256"] for entry in attr["evidence"]}
    scene_ref = next(path for path in attr_evidence if path.endswith("/train-scenes.pt"))
    scenes = torch.load(BytesIO(verified_bytes(scene_ref, attr_evidence[scene_ref])), map_location="cpu", weights_only=True)
    result = {}
    with np.load(BytesIO(verified_bytes(SELECTION, PINS[SELECTION])), allow_pickle=False) as selection:
        for scope, (scene_count, selected_count) in EXPECTED.items():
            if len(scenes[scope]) != scene_count:
                raise ValueError(f"{scope} scene inventory changed")
            mask = selection[scope + "_proposed_selected"]
            if mask.dtype != np.bool_ or int(mask.sum()) != selected_count:
                raise ValueError(f"{scope} selected inventory changed")
            old = torch.load(BytesIO(verified_bytes(CACHE.format(scope), PINS[CACHE.format(scope)])),
                             map_location="cpu", weights_only=True)
            if len(old["patches"]) != selected_count or len(old["labels"]) != selected_count:
                raise ValueError(f"{scope} cache inventory changed")
            offset = row = 0
            for i, item in enumerate(scenes[scope]):
                if item["metadata"]["split"] != "train" or tensor_digest(item["tensor"]) != item["tensor_sha256"]:
                    raise ValueError(f"{scope} scene {i} is not the bound training raster")
                ref = f"artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1/{scope}-train-{i:03d}-coordinates.npy"
                coords = np.load(BytesIO(verified_bytes(ref, evidence[ref])), allow_pickle=False)
                selected = torch.from_numpy(coords[mask[offset:offset + len(coords)]].copy())
                n = len(selected)
                if not torch.equal(crop_context(item["tensor"], selected)[:, :, 16:49, 16:49],
                                   old["patches"][row:row+n]):
                    raise ValueError(f"{scope} scene {i} failed exact local crop parity")
                offset += len(coords)
                row += n
            if offset != len(mask) or row != selected_count:
                raise ValueError(f"{scope} proposal alignment changed")
            result[scope] = {"scenes": scene_count, "selected_rows": row,
                             "source_proposals": offset, "local_crop_exact": True}
    return {"status": "source_preflight_pass", "scope": "train_only", "populations": result,
            "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
            "development_reads": 0, "model_inference_runs": 0,
            "limitations": ["Only the 28 runtime-bound family panels are eligible; other V37 training groups are excluded.",
                            "Runtime reports mark the artifact mask incomplete and training_input_ready false.",
                            "This validates input provenance and geometry, not model accuracy or release readiness."]}


if __name__ == "__main__":
    print(json.dumps(verify(), sort_keys=True, indent=2))
