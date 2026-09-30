# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only proposal negatives with a visible but noncentered marker.

Reuse each owned crowded raster without changing its pixels. Place the crop
six pixels from its authored target and outside the existing five-pixel native
matching tolerance of every neighbor. Predictions never choose the crop.
"""
from __future__ import annotations

import hashlib
import math

import numpy as np

from .crowded_context_data import CrowdedContextCase, cases, render_original
from .runtime_patches import extract_original_patch, original_luminance

VERSION = "classifier-off-center-context-data-v1"
NATIVE_MATCH_DISTANCE = 5.
PROPOSAL_OFFSET = 6.


def prepare_case(case: CrowdedContextCase) -> tuple[np.ndarray, dict]:
    image, source = render_original(case)
    try:
        centers = [source["center"]] + [neighbor["center"] for neighbor in source["neighbors"]]
        selected = None
        for index in range(8):
            direction = (case.variant + index) % 8
            angle = direction * math.pi / 4
            candidate = (source["center"][0] + PROPOSAL_OFFSET * math.cos(angle),
                         source["center"][1] + PROPOSAL_OFFSET * math.sin(angle))
            if min(math.dist(candidate, center) for center in centers) > NATIVE_MATCH_DISTANCE:
                selected = candidate, direction
                break
        if selected is None:
            raise ValueError("No unambiguous off-center proposal in the owned recipe")
        center, direction = selected
        patch = extract_original_patch(original_luminance(image), center, source["crop_radius"])
        sample_id = hashlib.sha256(f"{VERSION}:{case.sample_id}".encode()).hexdigest()
        row = {**source, "sample_id": sample_id, "parent_sample_id": case.sample_id,
               "family": VERSION, "artifact": True, "shape": None, "fill": None,
               "shape_index": -1, "fill_index": -1, "crop_center": center,
               "authored_target_shape": case.shape, "authored_target_fill": case.fill,
               "offset_direction": direction, "proposal_offset_pixels": PROPOSAL_OFFSET,
               "nearest_authored_center_distance": min(math.dist(center, other) for other in centers),
               "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest()}
        return patch, row
    finally:
        image.close()
