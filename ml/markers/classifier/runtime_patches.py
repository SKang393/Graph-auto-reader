# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Original-raster patch preparation matching the C# classifier input path.

Use this for newly authored synthetic diagnostics/data. Historical classifier
datasets and consumed evidence remain unchanged. This module does not train,
select, load a model, inspect a corpus, or assign shape/fill labels.
"""

from __future__ import annotations

import math

import numpy as np
from PIL import Image


def original_luminance(image: Image.Image) -> np.ndarray:
    """Match ProductionRasterFrameDecoder's white alpha composite and BT.601."""
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint32)
    alpha = rgba[..., 3:4]
    rgb = (rgba[..., :3] * alpha + 255 * (255 - alpha) + 127) // 255
    gray = (299 * rgb[..., 0] + 587 * rgb[..., 1] + 114 * rgb[..., 2] + 500) // 1000
    return gray.astype(np.float32) / np.float32(255)


def extract_original_patch(
    luminance: np.ndarray,
    center: tuple[float, float],
    radius: float,
    *,
    width: int = 32,
    height: int = 32,
    radius_scale: float = 2.25,
    minimum_half_extent: float = 4,
    padding_ink: float = 0,
) -> np.ndarray:
    """Return [1,H,W] float32 ink using the app's original-pixel bilinear crop.

    Center/radius are inputs, not inferred labels. Pixels and their positions
    are never altered. Out-of-image samples use the specified ink padding.
    This implements the unchanged identity-transform, one-channel production
    route; enhanced transforms and legend-specific content isolation are outside
    this helper's scope.
    """
    pixels = np.asarray(luminance, dtype=np.float32)
    if pixels.ndim != 2 or not all(pixels.shape) or not np.isfinite(pixels).all() or (
        (pixels < 0).any() or (pixels > 1).any()
    ):
        raise ValueError("Expected a nonempty normalized finite 2-D luminance raster.")
    if len(center) != 2 or not all(math.isfinite(v) for v in center):
        raise ValueError("Center must contain two finite original-pixel coordinates.")
    if not all(math.isfinite(v) and v > 0 for v in (radius, radius_scale, minimum_half_extent)):
        raise ValueError("Radius and crop extents must be finite and positive.")
    if any(not isinstance(v, int) or isinstance(v, bool) or v <= 0 for v in (width, height)):
        raise ValueError("Patch dimensions must be positive integers.")
    if not math.isfinite(padding_ink) or not 0 <= padding_ink <= 1:
        raise ValueError("Ink padding must be finite and within [0,1].")
    extent = max(minimum_half_extent, radius * radius_scale)
    if not math.isfinite(extent):
        raise ValueError("Mapped crop extent must remain finite.")

    source_x = center[0] + (((np.arange(width) + 0.5) / width) * 2 - 1) * extent
    source_y = center[1] + (((np.arange(height) + 0.5) / height) * 2 - 1) * extent
    valid_x = (source_x >= 0) & (source_x <= pixels.shape[1] - 1)
    valid_y = (source_y >= 0) & (source_y <= pixels.shape[0] - 1)
    # Clip before integer conversion so far-outside centers remain safe padding.
    safe_x = np.clip(source_x, 0, pixels.shape[1] - 1)
    safe_y = np.clip(source_y, 0, pixels.shape[0] - 1)
    x0 = np.floor(safe_x).astype(np.intp)
    y0 = np.floor(safe_y).astype(np.intp)
    x1 = np.minimum(x0 + 1, pixels.shape[1] - 1)
    y1 = np.minimum(y0 + 1, pixels.shape[0] - 1)
    fx = (safe_x - x0).astype(np.float32)[None, :]
    fy = (safe_y - y0).astype(np.float32)[:, None]
    top_left = pixels[y0[:, None], x0[None, :]]
    top_right = pixels[y0[:, None], x1[None, :]]
    bottom_left = pixels[y1[:, None], x0[None, :]]
    bottom_right = pixels[y1[:, None], x1[None, :]]
    top = top_left + (top_right - top_left) * fx
    bottom = bottom_left + (bottom_right - bottom_left) * fx
    ink = np.float32(1) - (top + (bottom - top) * fy)
    return np.where(valid_y[:, None] & valid_x[None, :], ink, np.float32(padding_ink))[None]
