# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Load the fixed, runtime-bound family training subset for the proposed trial."""
from __future__ import annotations

import json
from io import BytesIO

import numpy as np
import torch

from .crops import crop_context
from .prepare import ATTR, CACHE, DENSE, PINS, SELECTION, verified_bytes, verify
from .verify_family_runtime import verify as verify_runtime


def load() -> tuple[torch.Tensor, torch.Tensor, dict]:
    provenance = verify_runtime()
    verify()
    attr = json.loads(verified_bytes(ATTR, PINS[ATTR]).decode("utf-8-sig"))
    dense = json.loads(verified_bytes(DENSE, PINS[DENSE]).decode("utf-8-sig"))
    refs = {row["path"]: row["sha256"] for row in attr["evidence"]}
    evidence = {row["path"]: row["sha256"] for row in dense["evidence"]}
    scenes_path = next(path for path in refs if path.endswith("/train-scenes.pt"))
    scenes = torch.load(BytesIO(verified_bytes(scenes_path, refs[scenes_path])), map_location="cpu", weights_only=True)["family"]
    old = torch.load(BytesIO(verified_bytes(CACHE.format("family"), PINS[CACHE.format("family")])),
                     map_location="cpu", weights_only=True)
    with np.load(BytesIO(verified_bytes(SELECTION, PINS[SELECTION])), allow_pickle=False) as selection:
        mask = selection["family_proposed_selected"].copy()
    contexts = []
    cursor = 0
    for i, scene in enumerate(scenes):
        ref = f"artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1/family-train-{i:03d}-coordinates.npy"
        coords = np.load(BytesIO(verified_bytes(ref, evidence[ref])), allow_pickle=False)
        selected = torch.from_numpy(coords[mask[cursor:cursor + len(coords)]].copy())
        contexts.append(crop_context(scene["tensor"], selected))
        cursor += len(coords)
    result = torch.cat(contexts)
    labels = old["labels"].clone()
    if (cursor != len(mask) or result.shape != (9053, 3, 65, 65) or labels.shape != (9053,) or
            not torch.all((labels == 0) | (labels == 1)) or
            not torch.equal(result[:, :, 16:49, 16:49], old["patches"])):
        raise ValueError("Bound family training rows changed")
    return result, labels, provenance
