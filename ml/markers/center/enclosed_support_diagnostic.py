# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unsealed diagnostic of thin closed-marker support, never a production gate.

The frozen V24 consensus remains unchanged. This alternative tests whether the
decoded point lies inside an ink enclosure instead of sampling eight rays. It
never moves the point, consults truth, changes a score, or changes a mask.
"""
from __future__ import annotations

from collections import deque
import math

import numpy as np

INK_THRESHOLD = 0.12  # Same visible-ink threshold as the frozen postprocessor.
MAX_RADIUS = 12  # Same maximum radius as its existing multiradius check.


def enclosed_center_support(ink: np.ndarray, x: float, y: float) -> bool:
    """Return whether visible ink encloses the rounded decoded pixel locally.

    Use four-connected background and eight-connected ink, the complementary
    digital topology that permits a one-pixel diagonal outline. Reaching the
    image or local window boundary establishes an open region and rejects it.
    This is deliberately only a proposed supplemental geometric check.
    """
    if ink.ndim != 2 or not math.isfinite(x) or not math.isfinite(y):
        raise ValueError('Expected a 2D ink plane and finite coordinates')
    ix, iy = round(x), round(y)
    height, width = ink.shape
    if not (0 <= ix < width and 0 <= iy < height):
        return False
    left, right = max(0, ix - MAX_RADIUS), min(width - 1, ix + MAX_RADIUS)
    top, bottom = max(0, iy - MAX_RADIUS), min(height - 1, iy + MAX_RADIUS)
    patch = ink[top:bottom + 1, left:right + 1]
    if not np.isfinite(patch).all() or np.any((patch < 0) | (patch > 1)):
        raise ValueError('Ink probabilities must be finite and normalized')
    if ink[iy, ix] >= INK_THRESHOLD:
        return False
    pending = deque([(ix, iy)])
    visited = {(ix, iy)}
    while pending:
        px, py = pending.popleft()
        if px in (left, right) or py in (top, bottom):
            return False
        for nx, ny in ((px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1)):
            if (nx, ny) not in visited and ink[ny, nx] < INK_THRESHOLD:
                visited.add((nx, ny))
                pending.append((nx, ny))
    return True
