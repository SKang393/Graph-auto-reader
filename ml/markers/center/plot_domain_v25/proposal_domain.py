# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Plot-domain proposal extraction and postprocessing for the V25 hypothesis.

The domain is geometry supplied by the runtime axis stage.  This module never
derives a polygon from marker labels or other annotation truth.
"""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Literal, Sequence

import numpy as np
import torch
import torch.nn.functional as F

from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction, ProposalBatch
from ml.markers.center.mask_preserving_v24 import mask_preserving as v24


BOUNDARY_CONTEXT_PIXELS = 16.0
DomainKind = Literal["component_full_canvas", "runtime_plot_polygon"]


@dataclass(frozen=True)
class PlotDomain:
    """An immutable panel-pixel proposal domain."""

    kind: DomainKind
    width: int
    height: int
    polygon: tuple[tuple[float, float], ...]
    identity: str
    boundary_context_pixels: float = BOUNDARY_CONTEXT_PIXELS

    def __post_init__(self) -> None:
        if self.kind not in {"component_full_canvas", "runtime_plot_polygon"}:
            raise ValueError("plot domain kind is unsupported")
        if type(self.width) is not int or type(self.height) is not int:
            raise ValueError("plot domain dimensions must be integers")
        if self.width <= 0 or self.height <= 0 or not self.identity:
            raise ValueError("plot domain requires positive dimensions and an identity")
        if len(self.polygon) != 4:
            raise ValueError("plot domain requires exactly four polygon vertices")
        if len(set(self.polygon)) != 4:
            raise ValueError("plot domain polygon vertices must be distinct")
        if not math.isfinite(self.boundary_context_pixels) or self.boundary_context_pixels < 0:
            raise ValueError("plot domain boundary context must be finite and nonnegative")
        coordinates = tuple(coordinate for point in self.polygon for coordinate in point)
        if not all(math.isfinite(value) for value in coordinates):
            raise ValueError("plot domain polygon must be finite")
        if any(
            x < 0 or x > self.width or y < 0 or y > self.height
            for x, y in self.polygon
        ):
            raise ValueError("plot domain polygon exceeds the panel bounds")
        if abs(_signed_area(self.polygon)) <= 1e-9 or _self_intersects(self.polygon):
            raise ValueError("plot domain polygon must be simple and have positive area")
        if self.kind == "component_full_canvas":
            expected = (
                (0.0, float(self.height)),
                (float(self.width), float(self.height)),
                (float(self.width), 0.0),
                (0.0, 0.0),
            )
            if self.polygon != expected or self.boundary_context_pixels != 0:
                raise ValueError("component fixture domain must be the exact full canvas")

    @classmethod
    def component_full_canvas(
        cls,
        width: int,
        height: int,
        *,
        identity: str,
    ) -> PlotDomain:
        return cls(
            "component_full_canvas",
            width,
            height,
            (
                (0.0, float(height)),
                (float(width), float(height)),
                (float(width), 0.0),
                (0.0, 0.0),
            ),
            identity,
            0.0,
        )

    @classmethod
    def runtime_plot(
        cls,
        width: int,
        height: int,
        polygon: Sequence[Sequence[float]],
        *,
        identity: str,
    ) -> PlotDomain:
        try:
            points = tuple((float(point[0]), float(point[1])) for point in polygon)
        except (IndexError, TypeError, ValueError) as exception:
            raise ValueError("runtime plot polygon has invalid vertices") from exception
        return cls("runtime_plot_polygon", width, height, points, identity)

    def contains(self, x: float, y: float) -> bool:
        """Match the strict ray-crossing predicate used by MarkerPolygon.Contains."""

        if not math.isfinite(x) or not math.isfinite(y):
            return False
        inside = False
        for current, a in enumerate(self.polygon):
            b = self.polygon[current - 1]
            crosses = (a[1] > y) != (b[1] > y)
            if crosses and x < ((b[0] - a[0]) * (y - a[1]) / (b[1] - a[1])) + a[0]:
                inside = not inside
        return inside

    def supports_proposal(self, x: float, y: float) -> bool:
        if self.kind == "component_full_canvas":
            return 0 <= x < self.width and 0 <= y < self.height
        if self.contains(x, y):
            return True
        return min(
            _distance_to_segment(x, y, a[0], a[1], self.polygon[index - 1][0], self.polygon[index - 1][1])
            for index, a in enumerate(self.polygon)
        ) <= self.boundary_context_pixels


@dataclass(frozen=True)
class DomainProposalBatch:
    proposals: ProposalBatch
    ink_supported_count: int
    omitted_by_domain_count: int
    retained_ink_indices: tuple[int, ...]


@dataclass(frozen=True)
class DomainPostprocessResult:
    predictions: tuple[MarkerPrediction, ...]
    outputs_above_threshold: int
    decoded_outside_plot: int
    consensus_rejected: int | None
    nms_suppressed: int | None


def extract_proposals_in_domain(tensor: torch.Tensor, domain: PlotDomain) -> DomainProposalBatch:
    """Keep V24 ink support, patch values, channel order, stride, and ordering."""

    if tensor.ndim != 3 or tensor.shape[0] != 3:
        raise ValueError("expected [3,height,width] tensor")
    if tuple(tensor.shape[1:]) != (domain.height, domain.width):
        raise ValueError("proposal tensor dimensions differ from the bound plot domain")
    if domain.kind == "component_full_canvas":
        full = v24.extract_proposals(tensor)
        return DomainProposalBatch(full, len(full.patches), 0, tuple(range(len(full.patches))))
    # Runtime domains must filter the stride lattice before allocating patches.
    # The explicit padded slices are value-identical to V24's unfold rows.
    support = F.max_pool2d(
        tensor[0:1].unsqueeze(0),
        kernel_size=v24.INK_SUPPORT_WINDOW,
        stride=v24.STRIDE,
        padding=v24.INK_SUPPORT_WINDOW // 2,
    ).flatten()
    grid_width = math.ceil(domain.width / v24.STRIDE)
    ink_indices = torch.nonzero(
        support >= v24.INK_SUPPORT_THRESHOLD, as_tuple=False
    ).flatten()
    retained_ink: list[int] = []
    coordinates: list[tuple[int, int]] = []
    for ink_index, grid_index in enumerate(ink_indices.tolist()):
        y = (grid_index // grid_width) * v24.STRIDE
        x = (grid_index % grid_width) * v24.STRIDE
        if domain.supports_proposal(float(x), float(y)):
            retained_ink.append(ink_index)
            coordinates.append((x, y))
    if coordinates:
        padding = v24.PATCH_SIZE // 2
        padded = F.pad(tensor, (padding, padding, padding, padding))
        patches = torch.stack(
            tuple(
                padded[:, y:y + v24.PATCH_SIZE, x:x + v24.PATCH_SIZE]
                for x, y in coordinates
            )
        )
        proposals = ProposalBatch(
            patches,
            torch.tensor(coordinates, dtype=torch.float32, device=tensor.device),
        )
    else:
        proposals = ProposalBatch(
            tensor.new_empty((0, 3, v24.PATCH_SIZE, v24.PATCH_SIZE)),
            torch.empty((0, 2), dtype=torch.float32, device=tensor.device),
        )
    return DomainProposalBatch(
        proposals,
        len(ink_indices),
        len(ink_indices) - len(retained_ink),
        tuple(retained_ink),
    )


def postprocess_in_domain(
    scene: Any,
    proposals: ProposalBatch,
    output: np.ndarray,
    domain: PlotDomain,
) -> DomainPostprocessResult:
    """Decode V24 output and apply shipped plot containment before consensus/NMS."""

    if output.shape != (len(proposals.patches), 4):
        raise ValueError("expected [N,4] output")
    if tuple(scene.tensor.shape[1:]) != (domain.height, domain.width):
        raise ValueError("postprocess scene dimensions differ from the bound plot domain")
    if not np.isfinite(output).all():
        raise ValueError("marker model output must be finite")
    if domain.kind == "component_full_canvas":
        predictions = v24.postprocess(scene, proposals, output)
        above = int(np.count_nonzero(output[:, 0] >= 0.25))
        # The frozen V24 function does not expose separate consensus and NMS
        # counters. Do not infer either stage from the final count.
        return DomainPostprocessResult(predictions, above, 0, None, None)
    if any(
        not domain.supports_proposal(float(x), float(y))
        for x, y in proposals.coordinates.tolist()
    ):
        raise ValueError("runtime postprocess received a proposal outside its bound domain")

    candidates: list[MarkerPrediction] = []
    above = 0
    outside = 0
    consensus_rejected = 0
    for index in np.flatnonzero(output[:, 0] >= 0.25):
        above += 1
        base_x, base_y = proposals.coordinates[index].tolist()
        x = float(base_x + output[index, 1] * v24.STRIDE)
        y = float(base_y + output[index, 2] * v24.STRIDE)
        if not domain.contains(x, y):
            outside += 1
            continue
        radius = float(np.clip(output[index, 3], 2.5, 8.0))
        if not v24._consensus(scene, x, y):
            consensus_rejected += 1
            continue
        candidates.append(MarkerPrediction(x, y, radius, float(output[index, 0])))

    accepted: list[MarkerPrediction] = []
    for candidate in sorted(candidates, key=lambda item: (-item.confidence, item.y, item.x)):
        if any(
            math.hypot(candidate.x - previous.x, candidate.y - previous.y)
            < max(5.0, 1.25 * max(candidate.radius, previous.radius))
            for previous in accepted
        ):
            continue
        accepted.append(candidate)
    predictions = tuple(sorted(accepted, key=lambda item: (item.y, item.x, -item.confidence)))
    return DomainPostprocessResult(
        predictions,
        above,
        outside,
        consensus_rejected,
        len(candidates) - len(accepted),
    )


def _signed_area(points: tuple[tuple[float, float], ...]) -> float:
    return 0.5 * sum(
        point[0] * points[(index + 1) % len(points)][1]
        - points[(index + 1) % len(points)][0] * point[1]
        for index, point in enumerate(points)
    )


def _orientation(
    a: tuple[float, float], b: tuple[float, float], c: tuple[float, float]
) -> float:
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def _segments_cross(
    a: tuple[float, float],
    b: tuple[float, float],
    c: tuple[float, float],
    d: tuple[float, float],
) -> bool:
    return _orientation(a, b, c) * _orientation(a, b, d) < 0 and _orientation(c, d, a) * _orientation(c, d, b) < 0


def _self_intersects(points: tuple[tuple[float, float], ...]) -> bool:
    return _segments_cross(points[0], points[1], points[2], points[3]) or _segments_cross(
        points[1], points[2], points[3], points[0]
    )


def _distance_to_segment(
    x: float, y: float, ax: float, ay: float, bx: float, by: float
) -> float:
    dx, dy = bx - ax, by - ay
    denominator = dx * dx + dy * dy
    if denominator == 0:
        return math.hypot(x - ax, y - ay)
    position = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / denominator))
    return math.hypot(x - (ax + position * dx), y - (ay + position * dy))


__all__ = [
    "BOUNDARY_CONTEXT_PIXELS",
    "DomainPostprocessResult",
    "DomainProposalBatch",
    "PlotDomain",
    "extract_proposals_in_domain",
    "postprocess_in_domain",
]
