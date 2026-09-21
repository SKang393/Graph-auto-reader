# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Owned train-only neighboring and partially occluded marker recipes.

No development pixels, predictions or private data determine these examples.
The counterfactual omits the target only to detect invisible supervision; it
is not assigned a negative label or supplied to an optimizer.
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
from .native_context_data import FILL_NAMES
from .runtime_diagnostic import SHAPES
from .runtime_patches import extract_original_patch, original_luminance

VERSION = "classifier-crowded-context-data-v1"
CONTEXTS = ("separated_pair", "overlapping_pair", "close_row", "three_neighbors")
DRAW_ORDERS = ("target_first", "target_last")


@dataclass(frozen=True)
class CrowdedContextCase:
    shape: str
    fill: str
    context: str
    draw_order: str
    variant: int

    @property
    def sample_id(self) -> str:
        body = json.dumps(asdict(self), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(f"{VERSION}:{body}".encode()).hexdigest()


def cases() -> tuple[CrowdedContextCase, ...]:
    return tuple(CrowdedContextCase(*values) for values in itertools.product(
        SHAPES, ("open", "filled", "degraded"), CONTEXTS, DRAW_ORDERS, range(32)))


def render_original(case: CrowdedContextCase) -> tuple[Image.Image, dict]:
    if (case.shape not in SHAPES or case.fill not in ("open", "filled", "degraded") or
            case.context not in CONTEXTS or case.draw_order not in DRAW_ORDERS or
            type(case.variant) is not int or not 0 <= case.variant < 32):
        raise ValueError("Unknown owned crowded-context recipe")
    rng = random.Random(int(case.sample_id[:16], 16))
    scale = rng.choice((1, 2))
    center = (32+rng.uniform(-.8, .8), 32+rng.uniform(-.8, .8))
    radius = rng.uniform(3.5, 10)
    stroke = rng.choice((1, 2, 3))
    value = rng.choice((0, 17, 45))
    color = (value,) * 3
    base_angle = rng.uniform(-math.pi, math.pi)
    offsets = {
        "separated_pair": [(2.4, 0.)],
        "overlapping_pair": [(1.6, 0.)],
        "close_row": [(1.8, 0.), (1.8, math.pi)],
        "three_neighbors": [(1.6, 0.), (1.6, 2*math.pi/3), (1.6, 4*math.pi/3)],
    }[case.context]
    neighbors = []
    for separation, angle in offsets:
        r = radius*rng.uniform(.7, .9 if case.context == "three_neighbors" else 1.1)
        neighbors.append({"center": (center[0]+separation*radius*math.cos(base_angle+angle),
                                     center[1]+separation*radius*math.sin(base_angle+angle)),
                          "radius": r, "shape": rng.choice(SHAPES),
                          "fill": rng.choice(("open", "filled", "degraded"))})
    line_angle = rng.uniform(-math.pi, math.pi)
    line = [(center[0]-30*math.cos(line_angle), center[1]-30*math.sin(line_angle)),
            center,
            (center[0]+30*math.cos(line_angle+.35), center[1]+30*math.sin(line_angle+.35))]
    blur = (0., .15, .3, 0.)[case.variant % 4]

    def render(include_target):
        image = Image.new("RGB", (64*scale, 64*scale), "white")
        mask = Image.new("L", image.size, 0)
        draw, mask_draw = ImageDraw.Draw(image), ImageDraw.Draw(mask)
        if case.variant % 2:
            draw.line([(x*scale, y*scale) for x,y in line], fill=color, width=stroke*scale)

        def marker(item):
            _draw_marker(draw, mask_draw, tuple(v*scale for v in item["center"]),
                         item["radius"]*scale, item["shape"], item["fill"], color, stroke*scale)

        target = {"center": center, "radius": radius, "shape": case.shape, "fill": case.fill}
        if include_target and case.draw_order == "target_first":
            marker(target)
        for item in neighbors:
            marker(item)
        if include_target and case.draw_order == "target_last":
            marker(target)
        mask.close()
        if scale != 1:
            resized = image.resize((64, 64), Image.Resampling.LANCZOS)
            image.close(); image = resized
        if blur:
            softened = image.filter(ImageFilter.GaussianBlur(blur))
            image.close(); image = softened
        return image

    image, absent = render(True), render(False)
    try:
        changed = np.any(np.asarray(image) != np.asarray(absent), axis=2)
        visible_pixels = int(changed.sum())
        if visible_pixels == 0:
            raise ValueError("Authored target is invisible; repair the generator before training")
        absent_sha = hashlib.sha256(absent.tobytes()).hexdigest()
    except BaseException:
        image.close()
        raise
    finally:
        absent.close()
    crop_center = (center[0]+rng.uniform(-.85, .85), center[1]+rng.uniform(-.85, .85))
    crop_radius = radius*rng.uniform(.8, 1.55)
    fill = "unknown" if case.fill == "degraded" or case.shape in ("asterisk", "cross") else case.fill
    return image, {**asdict(case), "sample_id": case.sample_id, "split": "train", "family": VERSION,
                   "artifact": False, "shape_index": SHAPES.index(case.shape), "fill_index": FILL_NAMES.index(fill),
                   "center": center, "radius": radius, "stroke": stroke, "raster_scale": scale,
                   "blur_radius": blur, "neighbors": neighbors, "connection": line if case.variant % 2 else [],
                   "crop_center": crop_center, "crop_radius": crop_radius,
                   "target_visible_pixel_count": visible_pixels,
                   "without_target_raster_sha256": absent_sha,
                   "original_raster_sha256": hashlib.sha256(image.tobytes()).hexdigest()}


def prepare_case(case: CrowdedContextCase) -> tuple[np.ndarray, dict]:
    image, row = render_original(case)
    try:
        patch = extract_original_patch(original_luminance(image), row["crop_center"], row["crop_radius"])
        if np.ptp(patch) == 0:
            raise ValueError("Target crop has no visible ink variation")
        row["patch_sha256"] = hashlib.sha256(patch.tobytes()).hexdigest()
        return patch, row
    finally:
        image.close()
