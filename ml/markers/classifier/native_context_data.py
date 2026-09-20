# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Owned train/dev marker patches sampled from original-raster graph context.

No model, private input, historical dataset or sealed corpus is loaded. Recipe
families, fonts, raster scales, blur, sizes and subpixel layouts are disjoint
between train and dev. Both contain every shape and context. Shared drawing
primitives mean this is synthetic family separation, not independent real data.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
from functools import lru_cache
import hashlib
import itertools
import json
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

from ml.synthetic.fonts import FontResolver
from ml.synthetic.renderer import _draw_marker
from .runtime_diagnostic import SHAPES, CONTEXTS
from .runtime_patches import extract_original_patch, original_luminance

VERSION = "classifier-native-context-data-v2"
ARTIFACTS = ("text", "axis", "tick", "divider", "arrow", "bracket", "intersection", "legend")
FILL_NAMES = ("filled", "open", "unknown")


@dataclass(frozen=True)
class RasterFamily:
    name: str
    split: str
    font: str
    scale: int
    blur: float
    radii: tuple[float, ...]
    centers: tuple[tuple[float, float], ...]
    crop_radius_factors: tuple[float, ...]
    line_angle: float


FAMILIES = (
    RasterFamily("native-sans", "train", "sans", 1, 0,
                 (3., 4.5, 6., 8., 10.), ((32.1, 32.2), (32.65, 32.75)), (.85, 1.25), 28),
    RasterFamily("antialiased-serif", "train", "serif", 2, .15,
                 (3., 4.5, 6., 8., 10.), ((32.1, 32.2), (32.65, 32.75)), (.85, 1.25), -21),
    RasterFamily("soft-monospace", "dev", "monospace", 3, .3,
                 (3.75, 5.25, 7., 9.), ((32.35, 32.45), (32.8, 32.15)), (1., 1.45), 37),
)


@dataclass(frozen=True)
class NativeContextCase:
    family: str
    split: str
    shape: str | None
    fill: str | None
    context: str
    radius: float
    stroke: int
    center: tuple[float, float]
    crop_radius_factor: float

    @property
    def artifact(self) -> bool:
        return self.shape is None

    @property
    def observable_fill(self) -> str | None:
        return None if self.artifact or self.shape in ("cross", "asterisk") else self.fill

    @property
    def sample_id(self) -> str:
        body = json.dumps(asdict(self), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(f"{VERSION}:{body}".encode()).hexdigest()

    def record(self) -> dict:
        return {**asdict(self), "sample_id": self.sample_id, "artifact": self.artifact,
                "observable_fill": self.observable_fill,
                "shape_index": -1 if self.artifact else SHAPES.index(self.shape),
                "fill_index": -1 if self.artifact else FILL_NAMES.index(self.observable_fill or "unknown")}


def cases(split: str) -> tuple[NativeContextCase, ...]:
    if split not in ("train", "dev"):
        raise ValueError("Only the explicitly defined train/dev recipes are available")
    output = []
    for family in FAMILIES:
        if family.split != split:
            continue
        layouts = tuple(itertools.product(family.radii, (1, 2), family.centers, family.crop_radius_factors))
        for shape, fill, context, layout in itertools.product(SHAPES, ("open", "filled"), CONTEXTS, layouts):
            output.append(NativeContextCase(family.name, split, shape, fill, context, *layout))
        for context, layout in itertools.product(ARTIFACTS, layouts):
            output.append(NativeContextCase(family.name, split, None, None, context, *layout))
    return tuple(output)


@lru_cache(maxsize=96)
def _font(name: str, size: int):
    resolved = FontResolver().resolve(name, size)
    return resolved.load(), resolved.provenance()


def _family(case: NativeContextCase) -> RasterFamily:
    family = next((f for f in FAMILIES if f.name == case.family and f.split == case.split), None)
    if (family is None or case.radius not in family.radii or case.stroke not in (1, 2) or
            case.center not in family.centers or case.crop_radius_factor not in family.crop_radius_factors):
        raise ValueError("Case does not belong to its declared source family/layout")
    if case.artifact:
        if case.fill is not None or case.context not in ARTIFACTS:
            raise ValueError("Artifact recipe must use a contextual negative type")
    elif case.shape not in SHAPES or case.fill not in ("open", "filled") or case.context not in CONTEXTS:
        raise ValueError("Marker recipe has an unknown shape, fill or context")
    return family


def render_original(case: NativeContextCase) -> tuple[Image.Image, dict | None]:
    """Draw an owned raster around an authored center, with no model feedback."""
    family = _family(case)
    scale = family.scale
    image = Image.new("RGB", (64 * scale, 64 * scale), "white")
    mask = Image.new("L", image.size, 0)
    draw, mask_draw = ImageDraw.Draw(image), ImageDraw.Draw(mask)
    cx, cy = (v * scale for v in case.center)
    radius, stroke = case.radius * scale, case.stroke * scale
    font_evidence = None

    def line(points):
        draw.line(points, fill="black", width=stroke)

    def marker(x, y, shape, fill):
        _draw_marker(draw, mask_draw, (x, y), radius, shape, fill, (0, 0, 0), stroke)

    if not case.artifact:
        if case.context in ("horizontal_line", "oblique_line"):
            angle = 0 if case.context == "horizontal_line" else math.radians(family.line_angle)
            dx, dy = 29 * scale * math.cos(angle), 29 * scale * math.sin(angle)
            line(((cx-dx, cy-dy), (cx+dx, cy+dy)))
        elif case.context == "axis_contact":
            line(((cx, 3*scale), (cx, 61*scale)))
        elif case.context == "neighbor":
            marker(cx + 2.4*radius, cy, "square" if case.shape != "square" else "circle", "filled")
        marker(cx, cy, case.shape, case.fill)
    elif case.context == "text":
        words = {"native-sans": "Baseline", "antialiased-serif": "Intervention", "soft-monospace": "Outcome"}
        font, font_evidence = _font(family.font, round(max(8, case.radius * 2) * scale))
        word = words[family.name]
        bounds = font.getbbox(word, anchor="lt")
        draw.text((cx-(bounds[2]-bounds[0])/2, cy-(bounds[3]-bounds[1])/2), word, fill="black", font=font, anchor="lt")
    elif case.context in ("axis", "tick", "divider", "intersection"):
        if case.context in ("axis", "divider", "intersection"):
            line(((cx, 2*scale), (cx, 62*scale)))
        if case.context == "tick":
            line(((2*scale, cy), (62*scale, cy)))
            line(((cx, cy-radius*.6), (cx, cy+radius*.6)))
        elif case.context == "intersection":
            line(((2*scale, cy), (62*scale, cy)))
        elif case.context == "axis":
            for offset in (-2, 0, 2):
                y = cy + offset*radius
                line(((cx-radius*.6, y), (cx+radius*.6, y)))
    elif case.context == "arrow":
        line(((cx-2.8*radius, cy+1.6*radius), (cx, cy)))
        line(((cx-radius, cy), (cx, cy), (cx-radius*.4, cy+radius)))
    elif case.context == "bracket":
        line(((cx+radius, cy-radius), (cx-radius, cy-radius), (cx-radius, cy+radius), (cx+radius, cy+radius)))
    elif case.context == "legend":
        marker(cx, cy, "circle", "open")
        draw.rectangle((cx-1.6*radius, cy-1.6*radius, cx+4*radius, cy+1.6*radius), outline="black", width=stroke)
        font, font_evidence = _font(family.font, round(max(7, case.radius*1.5)*scale))
        draw.text((cx+1.7*radius, cy-radius*.7), "Series", font=font, fill="black", anchor="lt")
    mask.close()
    if scale != 1:
        resized = image.resize((64, 64), Image.Resampling.LANCZOS)
        image.close(); image = resized
    if family.blur:
        blurred = image.filter(ImageFilter.GaussianBlur(family.blur))
        image.close(); image = blurred
    return image, font_evidence


def prepare_case(case: NativeContextCase) -> tuple[np.ndarray, dict]:
    image, font_evidence = render_original(case)
    try:
        # Center perturbation represents localization error, not a label change.
        sign = 1 if case.stroke == 1 else -1
        center = (case.center[0] + .4*sign, case.center[1] - .35*sign)
        patch = extract_original_patch(original_luminance(image), center, case.radius*case.crop_radius_factor)
        record = {**case.record(), "original_raster_sha256": hashlib.sha256(image.tobytes()).hexdigest(),
                  "crop_center": center, "crop_radius": case.radius*case.crop_radius_factor,
                  "patch_sha256": hashlib.sha256(patch.tobytes()).hexdigest(), "font": font_evidence}
        return patch, record
    finally:
        image.close()
