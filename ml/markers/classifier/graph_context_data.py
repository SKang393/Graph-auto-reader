# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only coverage of bent connections, print damage and text fragments.

The existing native-context development population is not regenerated here.
All choices come from deterministic owned recipes, never model predictions.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import hashlib
import itertools
import json
import math
import random

import numpy as np
from PIL import Image, ImageDraw

from ml.synthetic.renderer import _degrade, _draw_marker
from .native_context_data import ARTIFACTS, FILL_NAMES, _font
from .runtime_diagnostic import SHAPES
from .runtime_patches import extract_original_patch, original_luminance

VERSION = "classifier-graph-context-data-v2"
CONTEXTS = ("isolated", "horizontal_line", "oblique_line", "bent_line", "neighbor", "axis_contact")


@dataclass(frozen=True)
class CoverageCase:
    shape: str | None
    fill: str | None
    context: str
    variant: int

    @property
    def sample_id(self) -> str:
        body = json.dumps(asdict(self), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(f"{VERSION}:{body}".encode()).hexdigest()


def cases() -> tuple[CoverageCase, ...]:
    markers = tuple(CoverageCase(*item) for item in itertools.product(
        SHAPES, ("open", "filled", "degraded"), CONTEXTS, range(64)))
    negatives = tuple(CoverageCase(None, None, context, variant)
                      for context in ARTIFACTS for variant in range(256))
    return markers + negatives


def stages_for(variant: int, rng: random.Random) -> list[dict]:
    # The same profile applies to positives and negatives, preventing a
    # degradation-specific artifact label shortcut.
    profiles = (
        [], [("isotropic_blur", {"radius": rng.uniform(.1, .45)})],
        [("erosion", {"size": 3})], [("ink_bleed", {"size": rng.choice((3, 5))})],
        [("halftone", {"cell_size": rng.choice((2, 3, 4))})],
        [("line_marker_contact", {"strength": rng.uniform(.35, .75)})],
        [("erosion", {"size": 3}), ("line_marker_contact", {"strength": rng.uniform(.4, .75)})],
        [("ink_bleed", {"size": 3}), ("halftone", {"cell_size": rng.choice((2, 3, 4))})],
    )
    return [{"kind": kind, "parameters": params} for kind, params in profiles[variant % len(profiles)]]


def render_original(case: CoverageCase) -> tuple[Image.Image, dict]:
    if (type(case.variant) is not int or not 0 <= case.variant < (256 if case.shape is None else 64)
            or (case.shape is None and (case.fill is not None or case.context not in ARTIFACTS))
            or (case.shape is not None and (case.shape not in SHAPES or case.fill not in ("open", "filled", "degraded")
                                            or case.context not in CONTEXTS))):
        raise ValueError("Unknown owned coverage recipe")
    seed = int(case.sample_id[:16], 16)
    rng = random.Random(seed)
    scale = rng.choice((1, 2))
    center = (32+rng.uniform(-.8, .8), 32+rng.uniform(-.8, .8))
    radius = rng.uniform(3.5, 10)
    stages = stages_for(case.variant, rng)
    stroke = 3 if any(s["kind"] == "erosion" for s in stages) else rng.choice((1, 2, 3))
    # The existing halftone renderer samples one point per cell. A stroke
    # thinner than that grid can disappear entirely at particular offsets.
    # Constrain the authored ink before rendering, for both classes, rather
    # than dropping erased positive examples or inventing a visible shape.
    stroke = max(stroke, *(s["parameters"]["cell_size"] for s in stages if s["kind"] == "halftone"), 1)
    color_value = rng.choice((0, 17, 45))
    color = (color_value,) * 3
    image = Image.new("RGB", (64*scale, 64*scale), "white")
    mask = Image.new("L", image.size, 0)
    draw, mask_draw = ImageDraw.Draw(image), ImageDraw.Draw(mask)
    cx, cy = (v*scale for v in center)
    r, width = radius*scale, stroke*scale
    angles = []
    font_evidence = None

    def line(points):
        draw.line(points, fill=color, width=width)

    def marker(x, y, shape, fill):
        # A context glyph is also real ink. Contact degradation therefore has
        # no access to the positive/negative classification label.
        _draw_marker(draw, mask_draw, (x, y), r, shape, fill, color, width)

    if case.shape is not None:
        if case.context in ("horizontal_line", "oblique_line", "bent_line"):
            angle = 0 if case.context == "horizontal_line" else rng.uniform(-80, 80)
            second = rng.uniform(-80, 80) if case.context == "bent_line" else angle
            angles = [angle, second]
            a, b = math.radians(angle), math.radians(second)
            line(((cx-30*scale*math.cos(a), cy-30*scale*math.sin(a)), (cx, cy),
                  (cx+30*scale*math.cos(b), cy+30*scale*math.sin(b))))
        elif case.context == "axis_contact":
            line(((cx, 0), (cx, 64*scale)))
        elif case.context == "neighbor":
            marker(cx+rng.uniform(2.1, 3.2)*r, cy+rng.uniform(-.6, .6)*r,
                   rng.choice(SHAPES), rng.choice(("open", "filled")))
        marker(cx, cy, case.shape, case.fill)
    elif case.context == "text":
        word = rng.choice(("Baseline", "Intervention", "Probe", "Treatment", "ABC", "Followup", "30", "Session"))
        font, font_evidence = _font(rng.choice(("sans", "serif")), round(rng.uniform(8, 22)*scale))
        index = rng.randrange(len(word))
        preceding = font.getlength(word[:index])
        letter_width = font.getlength(word[index])
        bounds = font.getbbox(word, anchor="lt")
        draw.text((cx-preceding-letter_width/2, cy-(bounds[3]-bounds[1])/2), word,
                  font=font, fill=color, anchor="lt")
        radius = rng.uniform(2.5, 7)
    elif case.context in ("axis", "tick", "divider", "intersection"):
        if case.context != "tick":
            line(((cx, 0), (cx, 64*scale)))
        if case.context in ("intersection", "tick"):
            line(((0, cy), (64*scale, cy)))
        if case.context == "tick":
            line(((cx, cy-r*.7), (cx, cy+r*.7)))
        elif case.context == "axis":
            for offset in (-2, 0, 2):
                line(((cx-r*.7, cy+offset*r), (cx+r*.7, cy+offset*r)))
    elif case.context == "arrow":
        angle = rng.uniform(-math.pi, math.pi)
        c, s = math.cos(angle), math.sin(angle)
        line(((cx-3*r*c, cy-3*r*s), (cx, cy)))
        line(((cx-r*c+.6*r*s, cy-r*s-.6*r*c), (cx, cy),
              (cx-r*c-.6*r*s, cy-r*s+.6*r*c)))
    elif case.context == "bracket":
        line(((cx+r, cy-r), (cx-r, cy-r), (cx-r, cy+r), (cx+r, cy+r)))
    elif case.context == "legend":
        marker(cx, cy, rng.choice(SHAPES), rng.choice(("open", "filled")))
        draw.rectangle((cx-1.5*r, cy-1.5*r, cx+4*r, cy+1.5*r), outline=color, width=width)
        font, font_evidence = _font(rng.choice(("sans", "serif")), round(max(7, radius)*scale))
        draw.text((cx+1.7*r, cy-r*.6), "Series", font=font, fill=color, anchor="lt")
    # Apply the authored ink damage to both classes. For negatives without a
    # marker mask, use the rendered ink mask rather than the target label.
    if case.shape is None:
        mask.close()
        mask = image.convert("L").point(lambda value: 255 if value < 224 else 0)
    if scale != 1:
        resized = image.resize((64, 64), Image.Resampling.LANCZOS)
        image.close(); image = resized
        resized = mask.resize((64, 64), Image.Resampling.NEAREST)
        mask.close(); mask = resized
    result, result_mask, records = _degrade(image, mask, {}, stages, seed)
    image.close(); mask.close(); result_mask.close()
    crop_factor = rng.uniform(.8, 1.55)
    crop_center = (center[0]+rng.uniform(-.85, .85), center[1]+rng.uniform(-.85, .85))
    observable_fill = "unknown" if case.shape in ("asterisk", "cross") or case.fill == "degraded" else case.fill
    record = {**asdict(case), "sample_id": case.sample_id, "split": "train", "family": "graph-print-context",
              "artifact": case.shape is None, "shape_index": -1 if case.shape is None else SHAPES.index(case.shape),
              "fill_index": -1 if case.shape is None else FILL_NAMES.index(observable_fill),
              "center": center, "radius": radius, "stroke": stroke, "raster_scale": scale,
              "angles": angles, "degradations": records, "font": font_evidence,
              "crop_center": crop_center, "crop_radius": radius*crop_factor,
              "original_raster_sha256": hashlib.sha256(result.tobytes()).hexdigest()}
    return result, record


def prepare_case(case: CoverageCase) -> tuple[np.ndarray, dict]:
    image, row = render_original(case)
    try:
        patch = extract_original_patch(original_luminance(image), row["crop_center"], row["crop_radius"])
        row["patch_sha256"] = hashlib.sha256(patch.tobytes()).hexdigest()
        return patch, row
    finally:
        image.close()
