# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Versioned synthetic graph rendering with physical axis-stroke preservation.

This module does not replace or alter the historical v1 generator.  It accepts
one schema-valid v1 scene, renders a wider physical source stroke when declared
morphology would otherwise consume an axis, and applies the original stages in
their original order.  Halftone is the sole changed degradation: v2 integrates
the complete cell instead of sampling its top-left pixel.

Annotations are used only to audit project-owned synthetic output.  Callers
must pass only returned raster bytes, never the annotations or audit, to the
runtime workflow.
"""

from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
import math
from typing import Any, Mapping, Sequence

from PIL import Image, ImageDraw

from ml.synthetic.renderer import _degrade, render_scene
from ml.synthetic.schema import validate_scene


GENERATOR_VERSION = "runtime-graph-axis-preserving-v2"
_GEOMETRIC_STAGES = frozenset({"skew", "perspective", "hand_drawn_jitter"})
_INK_THRESHOLD = 200
_RUNTIME_LINE_GAP_PIXELS = 2


class AxisPreservingSourceError(ValueError):
    """Raised when a scene or rendered source violates the v2 source contract."""


@dataclass(frozen=True)
class AxisCrossSectionAudit:
    panel_id: str
    axis: str
    clean_supported_cross_sections: int
    degraded_supported_cross_sections: int
    lost_clean_supported_cross_sections: int
    longest_degraded_gap: int
    classification: str


@dataclass(frozen=True)
class BoundingBoxSupportAudit:
    """A coarse support proxy that cannot establish element visibility.

    Pixels from axes, connectors, or overlapping glyphs may fall inside the
    semantic box.  Zero degraded support after positive clean support proves
    erasure within the box; positive support is deliberately inconclusive.
    """

    content_id: str
    kind: str
    clean_bbox_dark_pixels: int
    degraded_bbox_dark_pixels: int
    support_assessment: str


@dataclass(frozen=True)
class AxisPreservingRenderAudit:
    generator_version: str
    source_stroke_width: int
    degradation_kinds: tuple[str, ...]
    degradation_parameters: tuple[tuple[tuple[str, object], ...], ...]
    axes: tuple[AxisCrossSectionAudit, ...]
    markers: tuple[BoundingBoxSupportAudit, ...]
    texts: tuple[BoundingBoxSupportAudit, ...]
    failures: tuple[str, ...]

    @property
    def definitely_erased_marker_count(self) -> int:
        return sum(
            item.support_assessment == "definite_erasure_zero_bbox_support"
            for item in self.markers
        )

    @property
    def definitely_erased_text_count(self) -> int:
        return sum(
            item.support_assessment == "definite_erasure_zero_bbox_support"
            for item in self.texts
        )


@dataclass(frozen=True)
class AxisPreservingRenderResult:
    image: Image.Image
    annotation: dict[str, Any]
    marker_mask: Image.Image
    audit: AxisPreservingRenderAudit


def _stage_kind(stage: Mapping[str, object]) -> str:
    return str(stage.get("kind", "")).casefold().replace("-", "_")


def _stage_parameters(stage: Mapping[str, object]) -> Mapping[str, object]:
    value = stage.get("parameters", {})
    if not isinstance(value, Mapping):
        raise AxisPreservingSourceError("degradation parameters must be an object")
    return value


def required_source_stroke_width(stages: Sequence[Mapping[str, object]]) -> int:
    """Return the physical source width needed to leave one pixel after morphology."""

    required = 1
    for stage in reversed(stages):
        kind = _stage_kind(stage)
        parameters = _stage_parameters(stage)
        if kind == "erosion":
            size = max(3, int(parameters.get("size", 3)) | 1)
            required += size - 1
        elif kind in {"dilation", "ink_bleed"}:
            size = max(3, int(parameters.get("size", 3)) | 1)
            required = max(1, required - (size - 1))
    return required


def area_integrated_halftone(image: Image.Image, cell_size: int) -> Image.Image:
    """Render cell dots from complete-cell mean darkness, independent of grid phase."""

    step = max(2, int(cell_size))
    gray = image.convert("L")
    reduced = gray.reduce(step)
    output = Image.new("L", gray.size, 255)
    draw = ImageDraw.Draw(output)
    for cell_y in range(reduced.height):
        top = cell_y * step
        bottom = min(gray.height, top + step)
        for cell_x in range(reduced.width):
            left = cell_x * step
            right = min(gray.width, left + step)
            darkness = (255.0 - float(reduced.getpixel((cell_x, cell_y)))) / 255.0
            if darkness <= 0.0:
                continue
            area = darkness * (right - left) * (bottom - top)
            radius = math.sqrt(area / math.pi)
            center_x = (left + right) / 2.0
            center_y = (top + bottom) / 2.0
            draw.ellipse(
                (
                    center_x - radius,
                    center_y - radius,
                    center_x + radius,
                    center_y + radius,
                ),
                fill=0,
            )
    return output.convert("RGB")


def _physical_source_scene(
    scene: Mapping[str, Any], source_stroke_width: int
) -> dict[str, Any]:
    working = deepcopy(dict(scene))
    working["degradations"] = []
    for panel in working["panels"]:
        axes = panel["axes"]
        inherited_width = _source_width(axes.get("width", 1), "axes.width")
        axes["x"]["width"] = max(
            _source_width(axes["x"].get("width", inherited_width), "axes.x.width"),
            source_stroke_width,
        )
        axes["y"]["width"] = max(
            _source_width(axes["y"].get("width", inherited_width), "axes.y.width"),
            source_stroke_width,
        )
        for tick in panel["ticks"]:
            tick["width"] = max(
                _source_width(tick.get("width", inherited_width), "tick.width"),
                source_stroke_width,
            )
    return working


def _source_width(value: object, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise AxisPreservingSourceError(f"{name} must be a positive integer")
    return value


def _rng_padding_stage(index: int) -> dict[str, object]:
    return {
        "stage": index + 1,
        "family_key": "runtime_graph_axis_preserving_v2_rng_padding",
        "kind": "none",
        "parameters": {},
        "deterministic": True,
    }


def _apply_stages(
    image: Image.Image,
    marker_mask: Image.Image,
    annotation: dict[str, Any],
    stages: Sequence[Mapping[str, object]],
    seed: int,
) -> tuple[Image.Image, Image.Image, list[dict[str, Any]]]:
    result = image
    result_mask = marker_mask
    records: list[dict[str, Any]] = []
    for index, source_stage in enumerate(stages):
        kind = _stage_kind(source_stage)
        if kind == "halftone":
            parameters = _stage_parameters(source_stage)
            result = area_integrated_halftone(
                result, int(parameters.get("cell_size", 3))
            )
            records.append(
                {
                    "stage": int(source_stage.get("stage", index + 1)),
                    "family_key": source_stage.get("family_key"),
                    "kind": "halftone",
                    "parameters": dict(parameters),
                    "deterministic": bool(source_stage.get("deterministic", True)),
                    "applied": True,
                    "implementation": "area_integrated_v2",
                    "geometry_preserved": True,
                    "cell_integration": "complete_cell_mean_luma",
                }
            )
            continue
        padded = [_rng_padding_stage(item) for item in range(index)] + [dict(source_stage)]
        result, result_mask, stage_records = _degrade(
            result, result_mask, annotation, padded, seed
        )
        records.append(stage_records[-1])
    return result, result_mask, records


def _line_support(
    gray: Image.Image,
    line: Sequence[Sequence[float]],
    corridor_radius: int,
) -> tuple[int, int, int]:
    (x1, y1), (x2, y2) = line
    pixels = gray.load()
    width, height = gray.size
    support: list[bool] = []
    if abs(float(y2) - float(y1)) <= 1e-6:
        y = round(float(y1))
        for x in range(round(min(float(x1), float(x2))), round(max(float(x1), float(x2))) + 1):
            support.append(
                any(
                    0 <= x < width
                    and 0 <= candidate_y < height
                    and pixels[x, candidate_y] < _INK_THRESHOLD
                    for candidate_y in range(y - corridor_radius, y + corridor_radius + 1)
                )
            )
    elif abs(float(x2) - float(x1)) <= 1e-6:
        x = round(float(x1))
        for y in range(round(min(float(y1), float(y2))), round(max(float(y1), float(y2))) + 1):
            support.append(
                any(
                    0 <= candidate_x < width
                    and 0 <= y < height
                    and pixels[candidate_x, y] < _INK_THRESHOLD
                    for candidate_x in range(x - corridor_radius, x + corridor_radius + 1)
                )
            )
    else:
        raise AxisPreservingSourceError("v2 fixed source axes must remain axis-aligned")
    longest_gap = 0
    current_gap = 0
    for available in support:
        if available:
            current_gap = 0
        else:
            current_gap += 1
            longest_gap = max(longest_gap, current_gap)
    return len(support), sum(support), longest_gap


def _axis_audits(
    clean_image: Image.Image,
    degraded_image: Image.Image,
    annotation: Mapping[str, Any],
    stages: Sequence[Mapping[str, object]],
) -> tuple[AxisCrossSectionAudit, ...]:
    clean_gray = clean_image.convert("L")
    degraded_gray = degraded_image.convert("L")
    maximum_cell = max(
        (
            int(_stage_parameters(stage).get("cell_size", 2))
            for stage in stages
            if _stage_kind(stage) == "halftone"
        ),
        default=0,
    )
    corridor = math.ceil(maximum_cell / 2)
    audits: list[AxisCrossSectionAudit] = []
    for axis in _records(annotation, "axes"):
        total, clean_supported, _ = _line_support(clean_gray, axis["line"], corridor)
        _, degraded_supported, longest_gap = _line_support(
            degraded_gray, axis["line"], corridor
        )
        if clean_supported <= 0:
            classification = "invalid_clean_source"
        elif degraded_supported <= 0:
            classification = "destructively_erased"
        elif longest_gap > _RUNTIME_LINE_GAP_PIXELS:
            classification = "fragmented_or_occluded"
        else:
            classification = "supported"
        audits.append(
            AxisCrossSectionAudit(
                panel_id=str(axis["panel_id"]),
                axis=str(axis["axis"]),
                clean_supported_cross_sections=clean_supported,
                degraded_supported_cross_sections=degraded_supported,
                lost_clean_supported_cross_sections=max(0, clean_supported - degraded_supported),
                longest_degraded_gap=longest_gap,
                classification=classification,
            )
        )
        if total <= 0:
            raise AxisPreservingSourceError("declared axis has no measurable span")
    return tuple(audits)


def _records(
    annotation: Mapping[str, Any], key: str
) -> tuple[Mapping[str, Any], ...]:
    records = list(annotation.get(key, []))
    for panel in annotation.get("panels", []):
        records.extend(panel.get(key, []))
    return tuple(records)


def _dark_pixels(image: Image.Image, box: Sequence[float]) -> int:
    x, y, box_width, box_height = (float(value) for value in box)
    left = max(0, math.floor(x))
    top = max(0, math.floor(y))
    right = min(image.width, math.ceil(x + box_width))
    bottom = min(image.height, math.ceil(y + box_height))
    if right <= left or bottom <= top:
        return 0
    gray = image.convert("L").crop((left, top, right, bottom))
    return sum(value < _INK_THRESHOLD for value in gray.get_flattened_data())


def _content_audits(
    clean_image: Image.Image,
    degraded_image: Image.Image,
    records: Sequence[Mapping[str, Any]],
    *,
    kind: str,
    identifier: str,
) -> tuple[BoundingBoxSupportAudit, ...]:
    audits: list[BoundingBoxSupportAudit] = []
    for record in records:
        if record.get("visible") is False:
            continue
        box = record.get("rendered_pixel_box") if kind == "text" else record.get("box")
        if not isinstance(box, Sequence) or isinstance(box, (str, bytes)) or len(box) != 4:
            continue
        clean_pixels = _dark_pixels(clean_image, box)
        degraded_pixels = _dark_pixels(degraded_image, box)
        support_assessment = (
            "definite_erasure_zero_bbox_support"
            if clean_pixels > 0 and degraded_pixels == 0
            else "inconclusive_bbox_support_present"
        )
        audits.append(
            BoundingBoxSupportAudit(
                content_id=str(record[identifier]),
                kind=kind,
                clean_bbox_dark_pixels=clean_pixels,
                degraded_bbox_dark_pixels=degraded_pixels,
                support_assessment=support_assessment,
            )
        )
    return tuple(audits)


def render_axis_preserving_scene(scene: Mapping[str, Any]) -> AxisPreservingRenderResult:
    """Render one schema-valid scene through the bounded v2 degradation path."""

    validate_scene(scene)
    stages = tuple(deepcopy(list(scene["degradations"])))
    geometric = sorted({_stage_kind(stage) for stage in stages} & _GEOMETRIC_STAGES)
    if geometric:
        raise AxisPreservingSourceError(
            "runtime-graph-axis-preserving-v2 supports the non-geometric fixed source scope; "
            f"found {', '.join(geometric)}"
        )
    source_width = required_source_stroke_width(stages)
    working = _physical_source_scene(scene, source_width)
    clean_image, annotation, marker_mask = render_scene(working)
    clean_image = clean_image.copy()
    clean_annotation = deepcopy(annotation)
    degraded, degraded_mask, records = _apply_stages(
        clean_image.copy(), marker_mask.copy(), annotation, stages, int(scene["seed"])
    )
    annotation["degradations"] = records

    axes = _axis_audits(clean_image, degraded, annotation, stages)
    markers = _content_audits(
        clean_image,
        degraded,
        _records(clean_annotation, "markers"),
        kind="marker",
        identifier="marker_id",
    )
    texts = _content_audits(
        clean_image,
        degraded,
        _records(clean_annotation, "texts"),
        kind="text",
        identifier="text_id",
    )
    failures = tuple(
        [
            f"axis:{item.panel_id}:{item.axis}:{item.classification}"
            for item in axes
            if item.classification in {"invalid_clean_source", "destructively_erased"}
        ]
        + [
            f"marker:{item.content_id}:definite_erasure_zero_bbox_support"
            for item in markers
            if item.support_assessment == "definite_erasure_zero_bbox_support"
        ]
        + [
            f"text:{item.content_id}:definite_erasure_zero_bbox_support"
            for item in texts
            if item.support_assessment == "definite_erasure_zero_bbox_support"
        ]
    )
    audit = AxisPreservingRenderAudit(
        generator_version=GENERATOR_VERSION,
        source_stroke_width=source_width,
        degradation_kinds=tuple(_stage_kind(stage) for stage in stages),
        degradation_parameters=tuple(
            tuple(sorted(_stage_parameters(stage).items())) for stage in stages
        ),
        axes=axes,
        markers=markers,
        texts=texts,
        failures=failures,
    )
    return AxisPreservingRenderResult(degraded, annotation, degraded_mask, audit)


def require_complete_visible_content(
    result: AxisPreservingRenderResult,
) -> AxisPreservingRenderResult:
    """Fail closed because bounding-box support cannot prove element visibility."""

    if result.audit.failures:
        raise AxisPreservingSourceError(
            "runtime-graph-axis-preserving-v2 visible-content validation failed: "
            + "; ".join(result.audit.failures)
        )
    raise AxisPreservingSourceError(
        "runtime-graph-axis-preserving-v2 cannot establish complete visible content "
        "from its bounding-box support proxy; source-owned element masks are required"
    )


__all__ = [
    "AxisCrossSectionAudit",
    "AxisPreservingRenderAudit",
    "AxisPreservingRenderResult",
    "AxisPreservingSourceError",
    "GENERATOR_VERSION",
    "BoundingBoxSupportAudit",
    "area_integrated_halftone",
    "render_axis_preserving_scene",
    "require_complete_visible_content",
    "required_source_stroke_width",
]
