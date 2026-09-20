# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Additional owned training shapes for the unchanged proposal-center contract.

This population supplements historical training. It has no development/private
input, model call or threshold selection. All emitted patches keep the native
33-pixel crop, four-pixel grid and three input channels used by the application.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import itertools
import json
import math

import numpy as np
from PIL import Image

from ml.markers.classifier.graph_context_data import CoverageCase, render_original
from ml.markers.classifier.runtime_diagnostic import SHAPES
from ml.markers.classifier.runtime_patches import original_luminance

VERSION = "proposal-center-owned-shape-coverage-v1"
# The neighbor renderer does not return the second marker's exact center.
# Define a single-target population instead of incorrectly labeling that ink
# as background. Historical multi-marker training remains part of the union.
CONTEXTS = ("isolated", "horizontal_line", "oblique_line", "bent_line", "axis_contact")
VARIANTS = 16
POSITIVE_DISTANCE = 3.0
MAX_NEGATIVE_PER_POSITIVE = 10


@dataclass(frozen=True)
class ProposalRows:
    patches: np.ndarray
    labels: np.ndarray
    offsets: np.ndarray
    radii: np.ndarray
    hard_negative: np.ndarray
    record: dict


def cases() -> tuple[CoverageCase, ...]:
    return tuple(CoverageCase(*values) for values in itertools.product(
        SHAPES, ("open", "filled", "degraded"), CONTEXTS, range(VARIANTS)))


def prepare_case(case: CoverageCase) -> ProposalRows:
    if (case.shape not in SHAPES or case.fill not in ("open", "filled", "degraded")
            or case.context not in CONTEXTS or type(case.variant) is not int
            or not 0 <= case.variant < VARIANTS):
        raise ValueError("Unknown training-only shape-coverage recipe")
    identity = hashlib.sha256(f"{VERSION}:{case.sample_id}".encode()).hexdigest()
    seed = int(identity[:16], 16)
    rng = np.random.default_rng(seed)
    image, authored = render_original(case)
    # Cover every phase of the deployed stride, not only a centered training
    # marker. Moving this owned canvas also moves its truth by the same amount.
    dx, dy = (int(value) for value in rng.integers(0, 4, size=2))
    shifted = Image.new("RGB", image.size, "white")
    try:
        shifted.paste(image, (dx, dy))
        luminance = original_luminance(shifted)
    finally:
        image.close()
        shifted.close()
    center = np.array(authored["center"], dtype=np.float64) + [dx, dy]
    radius = float(authored["radius"])
    ink = np.float32(1) - luminance
    # The added scenes have no OCR or supplied structural-mask inputs. Their
    # original ink, including crossings/axis contact, stays visible to the net.
    tensor = np.stack((ink, np.zeros_like(ink), np.zeros_like(ink)))
    padded = np.pad(tensor, ((0, 0), (16, 16), (16, 16)))
    coordinates = []
    for y in range(0, ink.shape[0], 4):
        for x in range(0, ink.shape[1], 4):
            if ink[max(0, y-8):y+9, max(0, x-8):x+9].max() >= np.float32(.11):
                coordinates.append((x, y))
    positions = np.array(coordinates, dtype=np.float32)
    distances = np.linalg.norm(positions.astype(np.float64)-center, axis=1)
    positives = np.flatnonzero(distances <= POSITIVE_DISTANCE)
    # A large hollow glyph can be visible while its center has no ink within
    # the deployed 17-pixel support window. Keep that case and record the
    # unsupported truth explicitly; never invent a positive grid anchor.
    # Keep all nearby competing anchors, then deterministic random background.
    # No model scores, validation cases or answers choose the negatives.
    negatives = np.flatnonzero(distances > POSITIVE_DISTANCE)
    nearby = negatives[distances[negatives] <= 8]
    other = negatives[distances[negatives] > 8]
    maximum = max(len(nearby), MAX_NEGATIVE_PER_POSITIVE*max(1, len(positives)))
    selection = np.concatenate((positives, nearby, rng.permutation(other)[:maximum-len(nearby)]))
    selection.sort()
    chosen = positions[selection]
    labels = (distances[selection] <= POSITIVE_DISTANCE).astype(np.float32)
    patches = np.stack([padded[:, int(y):int(y)+33, int(x):int(x)+33] for x, y in chosen])
    offsets = np.zeros((len(selection), 2), dtype=np.float32)
    offsets[labels == 1] = ((center-chosen[labels == 1])/4).astype(np.float32)
    radii = np.full(len(selection), radius, dtype=np.float32)
    hard = ((labels == 0) & (distances[selection] <= 8)).astype(np.float32)
    # A white center is normal for an open marker. Require actual local ink,
    # without deleting sparse examples or assigning a point to blank pixels.
    cx, cy = (round(float(v)) for v in center)
    extent = math.ceil(radius+2)
    visible_pixels = int(np.count_nonzero(ink[cy-extent:cy+extent+1, cx-extent:cx+extent+1] >= .12))
    if visible_pixels == 0:
        raise ValueError("An authored positive was erased by its rendering recipe")
    record = {
        "sample_id": identity, "split": "train", "family": VERSION,
        "shape": case.shape, "fill": case.fill, "context": case.context,
        "variant": case.variant, "base_case_id": case.sample_id,
        "original_raster_sha256": authored["original_raster_sha256"],
        "tensor_sha256": hashlib.sha256(tensor.tobytes()).hexdigest(),
        "stride_translation": [dx, dy], "center": center.tolist(), "radius": radius,
        "visible_local_pixels": visible_pixels, "eligible_proposals": len(positions),
        "truth_count": 1, "truth_supported_by_positive_proposal": len(positives) > 0,
        "selected_proposal_coordinates": chosen.tolist(),
        "positive_rows": int(labels.sum()), "negative_rows": int(len(labels)-labels.sum()),
        "hard_negative_rows": int(hard.sum()),
        "patches_sha256": hashlib.sha256(patches.tobytes()).hexdigest(),
        "labels_sha256": hashlib.sha256(labels.tobytes()).hexdigest(),
    }
    return ProposalRows(patches, labels, offsets, radii, hard, record)


def definition() -> dict:
    population = cases()
    return {"version": VERSION, "scope": "new_owned_train_only_supplement",
            "count": len(population), "shapes": list(SHAPES), "contexts": list(CONTEXTS),
            "variants": VARIANTS, "positive_distance_px": POSITIVE_DISTANCE,
            "grid_stride": 4, "patch_shape": [3, 33, 33],
            "population_sha256": hashlib.sha256(json.dumps([c.sample_id for c in population],
                separators=(",", ":")).encode()).hexdigest(),
            "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0}
