# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed-weight diagnostic counterpart of the explicit balanced-ring candidate.

This does not replace historical postprocessors or authorize any production
model. It never moves a decoded point, reads truth or changes a learned score.
"""
from __future__ import annotations

import math
import numpy as np

from .enclosed_support_diagnostic import enclosed_center_support

GEOMETRY_SUPPORT = "multiradius_enclosed_balanced_v2"
POSTPROCESSING_ALGORITHM = "mask_preserving_multiradius_enclosed_balanced_v2"


def balanced_center_support(ink: np.ndarray, x: float, y: float) -> bool:
    """Require ring support to bracket the decoded pixel on both axes.

The existing 3..12 pixel radii, three-hit count, 0.12 ink threshold, 0.28
central mean and thin-outline enclosure rule remain unchanged. Three hits
along one nearby wall alone cannot supply a marker center.
"""
    if ink.ndim != 2 or not math.isfinite(x) or not math.isfinite(y):
        raise ValueError("Expected a 2D ink plane and finite coordinates")
    ix, iy = round(x), round(y)
    height, width = ink.shape
    if not (0 <= ix < width and 0 <= iy < height):
        return False
    local = ink[max(0, iy - 12):iy + 13, max(0, ix - 12):ix + 13]
    if not np.isfinite(local).all() or np.any((local < 0) | (local > 1)):
        raise ValueError("Ink probabilities must be finite and normalized")
    center = ink[max(0, iy - 2):iy + 3, max(0, ix - 2):ix + 3]
    if center.sum(dtype=np.float64) / center.size >= 0.28:
        return True
    for radius in range(3, 13):
        hits = [(dx, dy) for dx, dy in (
            (-radius, 0), (radius, 0), (0, -radius), (0, radius),
            (-radius, -radius), (radius, -radius), (-radius, radius), (radius, radius),
        ) if 0 <= ix + dx < width and 0 <= iy + dy < height
            and ink[iy + dy, ix + dx] >= np.float32(0.12)]
        if len(hits) >= 3 and min(dx for dx, _ in hits) <= 0 <= max(dx for dx, _ in hits) \
                and min(dy for _, dy in hits) <= 0 <= max(dy for _, dy in hits):
            return True
    return enclosed_center_support(ink, x, y)
