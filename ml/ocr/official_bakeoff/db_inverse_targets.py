# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic inverse-unclip DB targets for model-free geometry evaluation."""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence

import numpy as np


UNCLIP_RATIO = 1.5
MINIMUM_CONTOUR_EXTENT = 3

Box = tuple[float, float, float, float]
RasterBox = tuple[int, int, int, int]


class InverseTargetError(ValueError):
    """An inverse target cannot be represented by the declared raster."""


@dataclass(frozen=True)
class InverseTargetRegion:
    index: int
    tensor_box: Box
    inverse_offset: float
    continuous_contour_box: Box
    raster_contour_box: RasterBox
    raster_extent: tuple[int, int]
    minimum_extent_clamped_x: bool
    minimum_extent_clamped_y: bool
    translated_to_fit_x: bool
    translated_to_fit_y: bool


@dataclass(frozen=True)
class TargetContact:
    first_index: int
    second_index: int
    kind: str


@dataclass(frozen=True)
class InverseTargetBuild:
    target: np.ndarray
    regions: tuple[InverseTargetRegion, ...]
    contacts: tuple[TargetContact, ...]


def build_inverse_targets(
    width: int,
    height: int,
    boxes: Sequence[Sequence[float]],
) -> InverseTargetBuild:
    """Build one binary DB map while retaining one metadata row per truth box.

    Raster contour boxes use inclusive endpoints. Therefore an extent of three
    occupies four pixels and produces an OpenCV axis-aligned contour extent of
    three. Contacts describe where union rasterization can merge truth targets;
    they never remove or mask a truth.
    """

    _validate_dimension(width, "width")
    _validate_dimension(height, "height")
    if width <= MINIMUM_CONTOUR_EXTENT or height <= MINIMUM_CONTOUR_EXTENT:
        raise InverseTargetError(
            "The raster cannot represent the fixed minimum contour extent."
        )
    if isinstance(boxes, (str, bytes)) or not isinstance(boxes, Sequence):
        raise InverseTargetError("boxes must be a sequence of xyxy records")

    target = np.zeros((1, height, width), dtype=np.float32)
    regions: list[InverseTargetRegion] = []
    for index, value in enumerate(boxes):
        box = _validate_box(value, width, height, index)
        left, top, right, bottom = box
        box_width = right - left
        box_height = bottom - top
        offset = _inverse_offset(box_width, box_height)
        contour_width = box_width - (2.0 * offset)
        contour_height = box_height - (2.0 * offset)
        if contour_width <= 0 or contour_height <= 0:
            raise InverseTargetError(f"box {index} produced an empty inverse contour")

        rounded_width = _round_half_up(contour_width)
        rounded_height = _round_half_up(contour_height)
        extent_x = max(MINIMUM_CONTOUR_EXTENT, rounded_width)
        extent_y = max(MINIMUM_CONTOUR_EXTENT, rounded_height)
        if extent_x >= width or extent_y >= height:
            raise InverseTargetError(
                f"box {index} cannot fit its minimum contour inside the raster"
            )

        center_x = (left + right) / 2.0
        center_y = (top + bottom) / 2.0
        ideal_left = center_x - (extent_x / 2.0)
        ideal_top = center_y - (extent_y / 2.0)
        raster_left = _round_half_up(ideal_left)
        raster_top = _round_half_up(ideal_top)
        fitted_left = min(max(0, raster_left), width - 1 - extent_x)
        fitted_top = min(max(0, raster_top), height - 1 - extent_y)
        raster_right = fitted_left + extent_x
        raster_bottom = fitted_top + extent_y

        target[0, fitted_top : raster_bottom + 1, fitted_left : raster_right + 1] = 1
        regions.append(
            InverseTargetRegion(
                index=index,
                tensor_box=box,
                inverse_offset=offset,
                continuous_contour_box=(
                    left + offset,
                    top + offset,
                    right - offset,
                    bottom - offset,
                ),
                raster_contour_box=(
                    fitted_left,
                    fitted_top,
                    raster_right,
                    raster_bottom,
                ),
                raster_extent=(extent_x, extent_y),
                minimum_extent_clamped_x=extent_x != rounded_width,
                minimum_extent_clamped_y=extent_y != rounded_height,
                translated_to_fit_x=fitted_left != raster_left,
                translated_to_fit_y=fitted_top != raster_top,
            )
        )

    contacts = _contacts(regions)
    target.setflags(write=False)
    return InverseTargetBuild(target, tuple(regions), contacts)


def _inverse_offset(width: float, height: float) -> float:
    total = width + height
    leading = 4.0 * (2.0 + UNCLIP_RATIO)
    linear = 2.0 * (1.0 + UNCLIP_RATIO) * total
    discriminant = (linear * linear) - (
        4.0 * leading * UNCLIP_RATIO * width * height
    )
    if not math.isfinite(discriminant) or discriminant < 0:
        raise InverseTargetError("inverse-unclip discriminant is invalid")
    result = (linear - math.sqrt(discriminant)) / (2.0 * leading)
    if not math.isfinite(result) or result < 0:
        raise InverseTargetError("inverse-unclip offset is invalid")
    return result


def _contacts(regions: Sequence[InverseTargetRegion]) -> tuple[TargetContact, ...]:
    result: list[TargetContact] = []
    for first_index, first in enumerate(regions):
        left1, top1, right1, bottom1 = first.raster_contour_box
        for second in regions[first_index + 1 :]:
            left2, top2, right2, bottom2 = second.raster_contour_box
            overlap_x = max(left1, left2) <= min(right1, right2)
            overlap_y = max(top1, top2) <= min(bottom1, bottom2)
            if overlap_x and overlap_y:
                result.append(TargetContact(first.index, second.index, "overlap"))
                continue
            pixel_distance_x = max(0, max(left1, left2) - min(right1, right2))
            pixel_distance_y = max(0, max(top1, top2) - min(bottom1, bottom2))
            if pixel_distance_x <= 1 and pixel_distance_y <= 1:
                result.append(TargetContact(first.index, second.index, "touch"))
    return tuple(result)


def _validate_dimension(value: int, label: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise InverseTargetError(f"{label} must be a positive integer")


def _validate_box(
    value: Sequence[float], width: int, height: int, index: int
) -> Box:
    if isinstance(value, (str, bytes)) or not isinstance(value, Sequence) or len(value) != 4:
        raise InverseTargetError(f"box {index} must contain four coordinates")
    coordinates: list[float] = []
    for coordinate in value:
        if isinstance(coordinate, bool) or not isinstance(coordinate, (int, float)):
            raise InverseTargetError(f"box {index} coordinates must be numeric")
        converted = float(coordinate)
        if not math.isfinite(converted):
            raise InverseTargetError(f"box {index} coordinates must be finite")
        coordinates.append(converted)
    left, top, right, bottom = coordinates
    if left < 0 or top < 0 or right > width or bottom > height:
        raise InverseTargetError(f"box {index} escaped the tensor raster")
    if right <= left or bottom <= top:
        raise InverseTargetError(f"box {index} must have positive area")
    return left, top, right, bottom


def _round_half_up(value: float) -> int:
    return math.floor(value + 0.5)


__all__ = [
    "InverseTargetBuild",
    "InverseTargetError",
    "InverseTargetRegion",
    "MINIMUM_CONTOUR_EXTENT",
    "TargetContact",
    "UNCLIP_RATIO",
    "build_inverse_targets",
]
