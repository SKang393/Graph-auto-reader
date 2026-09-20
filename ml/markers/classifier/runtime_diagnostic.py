# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Balanced, unsealed diagnostics of native raster versus fine raster sampling.

This is not a training split or acceptance corpus. No external input is read.
The historical classifier data and the full-scene renderer remain unchanged.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import itertools
import math

import numpy as np
from PIL import Image, ImageDraw

from ml.synthetic.renderer import _draw_marker
from .runtime_patches import extract_original_patch, original_luminance

SHAPES = ("circle", "square", "triangle_up", "triangle_down", "diamond", "star",
          "asterisk", "cross", "other")
FILLS = ("open", "filled", "degraded")
CONTEXTS = ("isolated", "horizontal_line", "oblique_line", "neighbor", "axis_contact")
RADII = (3.5, 4.5, 6.0, 8.0)
STROKE_WIDTHS = (1, 2)
CENTERS = ((32.0, 32.0), (32.4, 31.6))
FINE_RASTER_SCALE = 4
DIAGNOSTIC_ID = "native-raster-classifier-coverage-v1"


@dataclass(frozen=True)
class DiagnosticCase:
    index: int
    shape: str
    fill: str
    context: str
    radius: float
    stroke_width: int
    center: tuple[float, float]

    @property
    def expected_fill(self) -> str | None:
        # The renderer draws open and filled crosses/asterisks identically.
        # Degraded is a rendering condition, not a classifier fill identity.
        return None if self.fill == "degraded" or self.shape in ("asterisk", "cross") else self.fill

    def record(self) -> dict:
        return {**asdict(self), "expected_fill": self.expected_fill}


def diagnostic_cases() -> tuple[DiagnosticCase, ...]:
    """2,160 equally weighted cases; every shape sees every raster condition."""
    return tuple(DiagnosticCase(index, *values) for index, values in enumerate(
        itertools.product(SHAPES, FILLS, CONTEXTS, RADII, STROKE_WIDTHS, CENTERS)
    ))


def _render(case: DiagnosticCase, scale: int) -> Image.Image:
    image = Image.new("RGB", (64 * scale, 64 * scale), "white")
    mask = Image.new("L", image.size, 0)
    draw, mask_draw = ImageDraw.Draw(image), ImageDraw.Draw(mask)
    cx, cy = (v * scale for v in case.center)
    radius = case.radius * scale
    stroke = case.stroke_width * scale
    if case.context in ("horizontal_line", "oblique_line"):
        angle = 0 if case.context == "horizontal_line" else math.radians(28)
        dx, dy = 26 * scale * math.cos(angle), 26 * scale * math.sin(angle)
        draw.line(((cx - dx, cy - dy), (cx + dx, cy + dy)), fill="black", width=stroke)
    elif case.context == "axis_contact":
        draw.line(((cx, 4 * scale), (cx, 60 * scale)), fill="black", width=stroke)
    elif case.context == "neighbor":
        other = "square" if case.shape != "square" else "circle"
        _draw_marker(draw, mask_draw, (cx + 2.4 * radius, cy), radius, other,
                     "filled", (0, 0, 0), stroke)
    elif case.context != "isolated":
        raise ValueError(f"Unknown diagnostic context: {case.context}")
    _draw_marker(draw, mask_draw, (cx, cy), radius, case.shape, case.fill, (0, 0, 0), stroke)
    return image


def diagnostic_pair(case: DiagnosticCase) -> tuple[np.ndarray, np.ndarray]:
    """Return native and fine-raster reference ink patches with identical intent.

    Both use the runtime crop helper. Fine raster is sampled directly, without
    first reducing it to the small original raster. It is a controlled reference,
    not the historical training pipeline and not a proposed runtime repair.
    Marker positions, shape, fill, line geometry and relative stroke width match.
    """
    patches = []
    for scale in (1, FINE_RASTER_SCALE):
        with _render(case, scale) as image:
            patches.append(extract_original_patch(
                original_luminance(image), tuple(v * scale for v in case.center),
                case.radius * scale))
    return patches[0], patches[1]
