# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Owned train-only crowds with a pixel-visible truth for every marker.

The native grid labels against all authored centers. A neighbor is therefore
never background merely because another marker was the recipe's first target.
Counterfactual renders verify visibility and never become optimizer inputs.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import hashlib
import itertools
import json
import math
import random

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

from ml.synthetic.renderer import _draw_marker
from ml.markers.classifier.runtime_diagnostic import SHAPES
from ml.markers.classifier.runtime_patches import original_luminance
from .shape_coverage_data import ProposalRows, POSITIVE_DISTANCE, MAX_NEGATIVE_PER_POSITIVE

VERSION = "proposal-center-owned-crowded-coverage-v1"
CONTEXTS = ("separated_pair", "overlapping_pair", "close_row", "three_neighbors")
DRAW_ORDERS = ("target_first", "target_last")
VARIANTS = 16


@dataclass(frozen=True)
class CrowdedCase:
    shape: str
    fill: str
    context: str
    draw_order: str
    variant: int

    @property
    def sample_id(self) -> str:
        body = json.dumps(asdict(self), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(f"{VERSION}:{body}".encode()).hexdigest()


def cases() -> tuple[CrowdedCase, ...]:
    return tuple(CrowdedCase(*v) for v in itertools.product(
        SHAPES, ("open", "filled", "degraded"), CONTEXTS, DRAW_ORDERS, range(VARIANTS)))


def render_original(case: CrowdedCase) -> tuple[Image.Image, dict]:
    if (case.shape not in SHAPES or case.fill not in ("open", "filled", "degraded")
            or case.context not in CONTEXTS or case.draw_order not in DRAW_ORDERS
            or type(case.variant) is not int or not 0 <= case.variant < VARIANTS):
        raise ValueError("Unknown train-only crowded center recipe")
    rng = random.Random(int(case.sample_id[:16], 16))
    scale = rng.choice((1, 2))
    center = (48+rng.uniform(-2, 2), 48+rng.uniform(-2, 2))
    radius, stroke = rng.uniform(3.5, 10), rng.choice((1, 2, 3))
    color = (rng.choice((0, 17, 45)),)*3
    angle = rng.uniform(-math.pi, math.pi)
    offsets = {
        "separated_pair": [(2.4, 0.)],
        "overlapping_pair": [(1.6, 0.)],
        "close_row": [(1.8, 0.), (1.8, math.pi)],
        "three_neighbors": [(1.6, 0.), (1.6, 2*math.pi/3), (1.6, 4*math.pi/3)],
    }[case.context]
    markers = [{"center": center, "radius": radius, "shape": case.shape, "fill": case.fill}]
    for separation, offset in offsets:
        markers.append({
            "center": (center[0]+separation*radius*math.cos(angle+offset),
                       center[1]+separation*radius*math.sin(angle+offset)),
            "radius": radius*rng.uniform(.7, .9 if case.context == "three_neighbors" else 1.1),
            "shape": rng.choice(SHAPES), "fill": rng.choice(("open", "filled", "degraded"))})
    line_angle = rng.uniform(-math.pi, math.pi)
    connection = [(center[0]-40*math.cos(line_angle), center[1]-40*math.sin(line_angle)), center,
                  (center[0]+40*math.cos(line_angle+.35), center[1]+40*math.sin(line_angle+.35))] if case.variant % 2 else []
    blur = (0., .15, .3, 0.)[case.variant % 4]
    order = list(range(len(markers)))
    if case.draw_order == "target_last":
        order = order[1:]+order[:1]

    def render(omit=None):
        image = Image.new("RGB", (96*scale, 96*scale), "white")
        mask = Image.new("L", image.size, 0)
        draw, mask_draw = ImageDraw.Draw(image), ImageDraw.Draw(mask)
        if connection:
            draw.line([(x*scale, y*scale) for x, y in connection], fill=color, width=stroke*scale)
        for index in order:
            if index == omit:
                continue
            item = markers[index]
            _draw_marker(draw, mask_draw, tuple(v*scale for v in item["center"]),
                         item["radius"]*scale, item["shape"], item["fill"], color, stroke*scale)
        mask.close()
        if scale != 1:
            resized = image.resize((96, 96), Image.Resampling.LANCZOS)
            image.close()
            image = resized
        if blur:
            softened = image.filter(ImageFilter.GaussianBlur(blur))
            image.close()
            image = softened
        return image

    image = render()
    try:
        visible = []
        for index in range(len(markers)):
            absent = render(index)
            try:
                visible.append(int(np.any(np.asarray(image) != np.asarray(absent), axis=2).sum()))
            finally:
                absent.close()
        if not all(visible):
            raise ValueError("Invisible authored marker; repair the recipe before training")
        return image, {**asdict(case), "sample_id": case.sample_id, "split": "train", "family": VERSION,
                       "markers": markers, "visible_pixels_by_marker": visible, "connection": connection,
                       "stroke": stroke, "raster_scale": scale, "blur_radius": blur,
                       "original_raster_sha256": hashlib.sha256(image.tobytes()).hexdigest()}
    except BaseException:
        image.close()
        raise


def prepare_case(case: CrowdedCase) -> ProposalRows:
    image, record = render_original(case)
    try:
        ink = np.float32(1)-original_luminance(image)
    finally:
        image.close()
    tensor = np.stack((ink, np.zeros_like(ink), np.zeros_like(ink)))
    padded = np.pad(tensor, ((0, 0), (16, 16), (16, 16)))
    centers = np.array([m["center"] for m in record["markers"]], dtype=np.float64)
    radii_by_marker = np.array([m["radius"] for m in record["markers"]], dtype=np.float32)
    coordinates = [(x, y) for y in range(0, ink.shape[0], 4) for x in range(0, ink.shape[1], 4)
                   if ink[max(0, y-8):y+9, max(0, x-8):x+9].max() >= np.float32(.11)]
    positions = np.asarray(coordinates, dtype=np.float32)
    distances = np.linalg.norm(positions[:, None, :].astype(np.float64)-centers[None, :, :], axis=2)
    nearest = distances.argmin(axis=1)
    minimum = distances.min(axis=1)
    positives = np.flatnonzero(minimum <= POSITIVE_DISTANCE)
    nearby = np.flatnonzero((minimum > POSITIVE_DISTANCE) & (minimum <= 8))
    other = np.flatnonzero(minimum > 8)
    maximum = max(len(nearby), MAX_NEGATIVE_PER_POSITIVE*max(1, len(positives)))
    rng = np.random.default_rng(int(case.sample_id[:16], 16))
    selection = np.sort(np.concatenate((positives, nearby, rng.permutation(other)[:maximum-len(nearby)])))
    chosen = positions[selection]
    labels = (minimum[selection] <= POSITIVE_DISTANCE).astype(np.float32)
    target = nearest[selection]
    offsets = np.zeros((len(selection), 2), dtype=np.float32)
    positive = labels == 1
    offsets[positive] = ((centers[target[positive]]-chosen[positive])/4).astype(np.float32)
    patches = np.stack([padded[:, int(y):int(y)+33, int(x):int(x)+33] for x, y in chosen])
    radii = radii_by_marker[target]
    hard = ((labels == 0) & (minimum[selection] <= 8)).astype(np.float32)
    # Every positive anchor is retained. A visible but unsupported center stays
    # in the truth inventory instead of receiving an invented grid proposal.
    assigned = np.bincount(target[positive], minlength=len(centers))
    record.update({
        "truth_count": len(centers), "positive_rows_by_marker": assigned.tolist(),
        "unsupported_truth_indices": np.flatnonzero(assigned == 0).tolist(),
        "eligible_proposals": len(positions), "selected_proposal_coordinates": chosen.tolist(),
        "selected_nearest_marker_indices": target.tolist(),
        "positive_rows": int(positive.sum()), "negative_rows": int((~positive).sum()),
        "hard_negative_rows": int(hard.sum()),
        "tensor_sha256": hashlib.sha256(tensor.tobytes()).hexdigest(),
        "patches_sha256": hashlib.sha256(patches.tobytes()).hexdigest(),
        "labels_sha256": hashlib.sha256(labels.tobytes()).hexdigest()})
    return ProposalRows(patches, labels, offsets, radii, hard, record)


def definition() -> dict:
    population = cases()
    return {"version": VERSION, "scope": "new_owned_train_only_supplement", "count": len(population),
            "shapes": list(SHAPES), "contexts": list(CONTEXTS), "draw_orders": list(DRAW_ORDERS),
            "variants": VARIANTS, "positive_distance_px": POSITIVE_DISTANCE, "grid_stride": 4,
            "patch_shape": [3, 33, 33], "all_visible_neighbors_are_truth": True,
            "population_sha256": hashlib.sha256(json.dumps([c.sample_id for c in population],
                separators=(",", ":")).encode()).hexdigest(),
            "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0}
