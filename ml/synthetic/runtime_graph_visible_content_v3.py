# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Versioned physical-source rendering with element-attributed support audits.

V3 leaves the historical renderer and v2 source untouched. It widens the
complete clean ink layer before the declared non-geometric degradation recipe,
then propagates separately captured marker and text glyph layers through the
same recipe. Element layers are audit evidence only and are never composited
back into the runtime raster or used as model input.
"""

from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from threading import Lock
from typing import Any, Mapping, Sequence

from PIL import Image, ImageChops, ImageDraw, ImageFilter

import ml.synthetic.renderer as _renderer
from ml.synthetic.runtime_graph_axis_preserving_v2 import (
    AxisCrossSectionAudit,
    AxisPreservingSourceError,
    _apply_stages,
    _axis_audits,
    _records,
    _stage_kind,
    required_source_stroke_width,
)
from ml.synthetic.schema import validate_scene


GENERATOR_VERSION = "runtime-graph-visible-content-v3"
_GEOMETRIC_STAGES = frozenset({"skew", "perspective", "hand_drawn_jitter"})
_UNBOUNDED_ATTRIBUTION_STAGES = frozenset(
    {"gaussian_noise", "poisson_noise", "impulse_noise", "paper_texture"}
)
_CAPTURE_LOCK = Lock()


@dataclass(frozen=True)
class ElementSupportAudit:
    content_id: str
    kind: str
    declared_source_box: tuple[float, float, float, float] | None
    clean_annotation_box: tuple[float, float, float, float]
    clean_rendered_bounds: tuple[int, int, int, int] | None
    attributed_final_bounds: tuple[int, int, int, int] | None
    clean_support_pixels: int | None
    final_attributed_support_pixels: int | None
    support_assessment: str
    composited_visibility_assessment: str
    readability_assessment: str | None
    open_interior_retained: bool | None
    clean_hole_count: int | None
    attributed_final_hole_count: int | None
    attributed_component_count: int | None
    overlaps_other_elements: tuple[str, ...] | None


@dataclass(frozen=True)
class VisibleContentRenderAudit:
    generator_version: str
    source_dilation_size: int
    degradation_kinds: tuple[str, ...]
    axes: tuple[AxisCrossSectionAudit, ...]
    markers: tuple[ElementSupportAudit, ...]
    texts: tuple[ElementSupportAudit, ...]
    ungraded_stage_kinds: tuple[str, ...]
    failures: tuple[str, ...]
    limitations: tuple[str, ...]

    @property
    def definitely_erased_marker_count(self) -> int:
        return sum(item.support_assessment == "definite_attributed_erasure" for item in self.markers)

    @property
    def definitely_erased_text_count(self) -> int:
        return sum(item.support_assessment == "definite_attributed_erasure" for item in self.texts)


@dataclass(frozen=True)
class VisibleContentRenderResult:
    image: Image.Image
    annotation: dict[str, Any]
    marker_mask: Image.Image
    audit: VisibleContentRenderAudit


@dataclass(frozen=True)
class VisibleContentSourceResult:
    image: Image.Image
    annotation: dict[str, Any]
    marker_mask: Image.Image
    source_dilation_size: int
    degradation_kinds: tuple[str, ...]


@dataclass(frozen=True)
class _CapturedElement:
    content_id: str
    kind: str
    declared_source_box: tuple[float, float, float, float] | None
    clean_annotation_box: tuple[float, float, float, float]
    source_layer: Image.Image
    binary_mask: Image.Image
    open_marker: bool


def _declared_text_boxes(
    scene: Mapping[str, Any],
) -> dict[str, tuple[float, float, float, float]]:
    return {
        str(record["region_id"]): tuple(float(value) for value in record["box"])
        for record in scene.get("annotations", {}).get("text_regions", [])
        if isinstance(record, Mapping)
        and record.get("region_id") is not None
        and isinstance(record.get("box"), Sequence)
        and len(record["box"]) == 4
    }


def _binary_difference(image: Image.Image) -> Image.Image:
    white = Image.new("RGB", image.size, "white")
    difference = ImageChops.difference(image.convert("RGB"), white).convert("L")
    return difference.point(lambda value: 255 if value else 0)


def _capture_clean_scene(
    scene: Mapping[str, Any],
) -> tuple[Image.Image, dict[str, Any], Image.Image, list[_CapturedElement]]:
    text_masks: dict[str, Image.Image] = {}
    marker_calls: list[tuple[tuple[float, float], float, str, str, Image.Image]] = []
    original_text = _renderer._draw_text
    original_marker = _renderer._draw_marker

    def capture_text(
        image: Image.Image,
        draw: ImageDraw.ImageDraw,
        item: Mapping[str, Any],
        *,
        font: Any,
        default_position: tuple[float, float],
        default_role: str,
        default_id: str,
    ) -> dict[str, Any]:
        record = original_text(image, draw, item, font=font, default_position=default_position, default_role=default_role, default_id=default_id)
        layer = Image.new("RGB", image.size, "white")
        original_text(layer, ImageDraw.Draw(layer), item, font=font, default_position=default_position, default_role=default_role, default_id=default_id)
        content_id = str(record["text_id"])
        if content_id in text_masks:
            raise AxisPreservingSourceError(f"duplicate rendered text id '{content_id}'")
        text_masks[content_id] = layer
        return record

    def capture_marker(
        draw: ImageDraw.ImageDraw,
        mask_draw: ImageDraw.ImageDraw,
        center: tuple[float, float],
        radius: float,
        shape: str,
        fill_state: str,
        color: tuple[int, int, int],
        stroke_width: int,
    ) -> None:
        original_marker(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width)
        layer = Image.new("RGB", draw._image.size, "white")
        ignored_mask = Image.new("L", layer.size, 0)
        original_marker(ImageDraw.Draw(layer), ImageDraw.Draw(ignored_mask), center, radius, shape, fill_state, color, stroke_width)
        marker_calls.append(((float(center[0]), float(center[1])), float(radius), shape, fill_state, layer))

    clean_scene = deepcopy(dict(scene))
    clean_scene["degradations"] = []
    with _CAPTURE_LOCK:
        _renderer._draw_text = capture_text
        _renderer._draw_marker = capture_marker
        try:
            image, annotation, marker_mask = _renderer.render_scene(clean_scene)
        finally:
            _renderer._draw_text = original_text
            _renderer._draw_marker = original_marker

    captured: list[_CapturedElement] = []
    remaining_markers = list(marker_calls)
    for marker in _records(annotation, "markers"):
        center = tuple(float(value) for value in marker["center"])
        radius = float(marker["radius"])
        shape = str(marker["shape"])
        fill_state = str(marker["fill"])
        match = next((index for index, candidate in enumerate(remaining_markers) if candidate[0] == center and candidate[1] == radius and candidate[2] == shape and candidate[3] == fill_state), None)
        if match is None:
            raise AxisPreservingSourceError(f"could not bind rendered marker '{marker['marker_id']}' to its source glyph")
        candidate = remaining_markers.pop(match)
        marker_box = tuple(float(value) for value in marker["box"])
        captured.append(_CapturedElement(str(marker["marker_id"]), "marker", marker_box, marker_box, candidate[4], _binary_difference(candidate[4]), fill_state == "open"))
    declared_text_boxes = _declared_text_boxes(scene)
    for text in _records(annotation, "texts"):
        if text.get("visible") is False:
            continue
        content_id = str(text["text_id"])
        if content_id not in text_masks:
            raise AxisPreservingSourceError(f"could not bind rendered text '{content_id}' to its source glyph")
        source_layer = text_masks[content_id]
        captured.append(_CapturedElement(content_id, "text", declared_text_boxes.get(content_id), tuple(float(value) for value in text["box"]), source_layer, _binary_difference(source_layer), False))
    return image.copy(), deepcopy(annotation), marker_mask.copy(), captured


def _pre_dilate(image: Image.Image, size: int) -> Image.Image:
    if size <= 1:
        return image.convert("RGB").copy()
    return image.convert("RGB").filter(ImageFilter.MinFilter(size))


def _validated_stages(scene: Mapping[str, Any]) -> tuple[Mapping[str, object], ...]:
    validate_scene(scene)
    stages = tuple(deepcopy(list(scene["degradations"])))
    geometric = sorted({_stage_kind(stage) for stage in stages} & _GEOMETRIC_STAGES)
    if geometric:
        raise AxisPreservingSourceError(
            "runtime-graph-visible-content-v3 supports the non-geometric fixed source scope; "
            f"found {', '.join(geometric)}"
        )
    return stages


def _render_physical_source_from_clean(
    scene: Mapping[str, Any],
    clean_image: Image.Image,
    clean_annotation: Mapping[str, Any],
    clean_marker_mask: Image.Image,
    stages: Sequence[Mapping[str, object]],
) -> VisibleContentSourceResult:
    dilation_size = required_source_stroke_width(stages)
    stage_annotation = deepcopy(dict(clean_annotation))
    final_image, final_marker_mask, degradation_records = _apply_stages(
        _pre_dilate(clean_image, dilation_size),
        clean_marker_mask.copy(),
        stage_annotation,
        stages,
        int(scene["seed"]),
    )
    # Mask-conditioned visual stages may expand their working marker mask and
    # stage-local annotation. Target centers, radii and boxes remain the clean
    # source semantics; observed support bounds belong only in the audit.
    annotation = deepcopy(dict(clean_annotation))
    annotation["degradations"] = degradation_records
    return VisibleContentSourceResult(
        final_image,
        annotation,
        final_marker_mask,
        dilation_size,
        tuple(_stage_kind(stage) for stage in stages),
    )


def render_visible_content_source(scene: Mapping[str, Any]) -> VisibleContentSourceResult:
    """Render the exact v3 raster without per-element attribution work."""

    stages = _validated_stages(scene)
    clean_scene = deepcopy(dict(scene))
    clean_scene["degradations"] = []
    clean_image, clean_annotation, clean_marker_mask = _renderer.render_scene(clean_scene)
    return _render_physical_source_from_clean(
        scene,
        clean_image,
        clean_annotation,
        clean_marker_mask,
        stages,
    )


def _attributed_final_mask(
    source_layer: Image.Image,
    blank_final: Image.Image,
    annotation: Mapping[str, Any],
    stages: Sequence[Mapping[str, object]],
    seed: int,
    dilation_size: int,
    marker_mask: Image.Image,
) -> Image.Image:
    element_source = _pre_dilate(source_layer, dilation_size)
    element_final, _, _ = _apply_stages(element_source, marker_mask.copy(), deepcopy(dict(annotation)), stages, seed)
    difference = ImageChops.difference(element_final.convert("RGB"), blank_final)
    return difference.convert("L").point(lambda value: 255 if value else 0)


def _component_count(mask: Image.Image) -> int:
    box = mask.getbbox()
    if box is None:
        return 0
    cropped = mask.crop(box)
    pixels = cropped.load()
    width, height = cropped.size
    seen: set[tuple[int, int]] = set()
    count = 0
    for y in range(height):
        for x in range(width):
            if not pixels[x, y] or (x, y) in seen:
                continue
            count += 1
            stack = [(x, y)]
            seen.add((x, y))
            while stack:
                current_x, current_y = stack.pop()
                for next_x, next_y in ((current_x - 1, current_y), (current_x + 1, current_y), (current_x, current_y - 1), (current_x, current_y + 1)):
                    if 0 <= next_x < width and 0 <= next_y < height and pixels[next_x, next_y] and (next_x, next_y) not in seen:
                        seen.add((next_x, next_y))
                        stack.append((next_x, next_y))
    return count


def _hole_count(mask: Image.Image) -> int:
    box = mask.getbbox()
    if box is None:
        return 0
    crop = mask.crop(box)
    background = ImageChops.invert(crop)
    pixels = background.load()
    width, height = background.size
    outside: set[tuple[int, int]] = set()
    stack = [(x, y) for y in range(height) for x in range(width) if (x in {0, width - 1} or y in {0, height - 1}) and pixels[x, y]]
    outside.update(stack)
    while stack:
        x, y = stack.pop()
        for next_x, next_y in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= next_x < width and 0 <= next_y < height and pixels[next_x, next_y] and (next_x, next_y) not in outside:
                outside.add((next_x, next_y))
                stack.append((next_x, next_y))
    unseen = {(x, y) for y in range(height) for x in range(width) if pixels[x, y] and (x, y) not in outside}
    holes = 0
    while unseen:
        holes += 1
        stack = [unseen.pop()]
        while stack:
            x, y = stack.pop()
            for neighbor in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if neighbor in unseen:
                    unseen.remove(neighbor)
                    stack.append(neighbor)
    return holes


def _support_pixels(mask: Image.Image) -> int:
    return sum(mask.histogram()[1:])


def _annotation_box_bounds(box: Sequence[float] | None) -> tuple[int, int, int, int] | None:
    if box is None or len(box) != 4:
        return None
    x, y, width, height = (float(value) for value in box)
    return (math.floor(x), math.floor(y), math.ceil(x + width), math.ceil(y + height))


def _ungraded_element_audits(
    scene: Mapping[str, Any], annotation: Mapping[str, Any]
) -> tuple[tuple[ElementSupportAudit, ...], tuple[ElementSupportAudit, ...]]:
    declared_text_boxes = _declared_text_boxes(scene)
    markers = tuple(
        ElementSupportAudit(
            content_id=str(item["marker_id"]),
            kind="marker",
            declared_source_box=tuple(float(value) for value in item["box"]),
            clean_annotation_box=tuple(float(value) for value in item["box"]),
            clean_rendered_bounds=None,
            attributed_final_bounds=None,
            clean_support_pixels=None,
            final_attributed_support_pixels=None,
            support_assessment="ungradable_full_canvas_stochastic_stage",
            composited_visibility_assessment="ungradable_full_canvas_stochastic_stage",
            readability_assessment=None,
            open_interior_retained=None,
            clean_hole_count=None,
            attributed_final_hole_count=None,
            attributed_component_count=None,
            overlaps_other_elements=None,
        )
        for item in _records(annotation, "markers")
    )
    texts = tuple(
        ElementSupportAudit(
            content_id=str(item["text_id"]),
            kind="text",
            declared_source_box=declared_text_boxes.get(str(item["text_id"])),
            clean_annotation_box=tuple(float(value) for value in item["box"]),
            clean_rendered_bounds=None,
            attributed_final_bounds=None,
            clean_support_pixels=None,
            final_attributed_support_pixels=None,
            support_assessment="ungradable_full_canvas_stochastic_stage",
            composited_visibility_assessment="ungradable_full_canvas_stochastic_stage",
            readability_assessment="ungradable_full_canvas_stochastic_stage",
            open_interior_retained=None,
            clean_hole_count=None,
            attributed_final_hole_count=None,
            attributed_component_count=None,
            overlaps_other_elements=None,
        )
        for item in _records(annotation, "texts")
        if item.get("visible") is not False
    )
    return markers, texts


def _masks_overlap(left: Image.Image, right: Image.Image) -> bool:
    left_box = left.getbbox()
    right_box = right.getbbox()
    if left_box is None or right_box is None:
        return False
    intersection = (
        max(left_box[0], right_box[0]),
        max(left_box[1], right_box[1]),
        min(left_box[2], right_box[2]),
        min(left_box[3], right_box[3]),
    )
    if intersection[0] >= intersection[2] or intersection[1] >= intersection[3]:
        return False
    return ImageChops.multiply(left.crop(intersection), right.crop(intersection)).getbbox() is not None


def render_visible_content_scene(scene: Mapping[str, Any]) -> VisibleContentRenderResult:
    """Render v3 with diagnostics in an isolated single-threaded process.

    Capture temporarily instruments the historical renderer. The lock protects
    cooperating V3 calls, but ordinary renderer callers do not acquire it.
    Shared threaded processes must use ``render_visible_content_source`` and
    run this diagnostic API in a separate process.
    """

    stages = _validated_stages(scene)
    ungraded_stages = tuple(
        kind
        for kind in (_stage_kind(stage) for stage in stages)
        if kind in _UNBOUNDED_ATTRIBUTION_STAGES
    )
    if ungraded_stages:
        clean_scene = deepcopy(dict(scene))
        clean_scene["degradations"] = []
        clean_image, clean_annotation, marker_mask = _renderer.render_scene(clean_scene)
        source = _render_physical_source_from_clean(
            scene, clean_image, clean_annotation, marker_mask, stages
        )
        markers, texts = _ungraded_element_audits(scene, source.annotation)
        axes = _axis_audits(clean_image, source.image, source.annotation, stages)
        failures = tuple(
            f"axis:{item.panel_id}:{item.axis}:{item.classification}"
            for item in axes
            if item.classification in {"invalid_clean_source", "destructively_erased"}
        )
        return VisibleContentRenderResult(
            source.image,
            source.annotation,
            source.marker_mask,
            VisibleContentRenderAudit(
                generator_version=GENERATOR_VERSION,
                source_dilation_size=source.source_dilation_size,
                degradation_kinds=source.degradation_kinds,
                axes=axes,
                markers=markers,
                texts=texts,
                ungraded_stage_kinds=ungraded_stages,
                failures=failures,
                limitations=(
                    "per-element attribution and topology are ungradable for full-canvas stochastic stages",
                    "ungraded elements remain in every source and metric denominator",
                    "no exact attributed final bounds or readability claim is available",
                ),
            ),
        )
    clean_image, clean_annotation, marker_mask, captured = _capture_clean_scene(scene)
    source = _render_physical_source_from_clean(
        scene,
        clean_image,
        clean_annotation,
        marker_mask,
        stages,
    )
    annotation = source.annotation
    final_image = source.image
    final_marker_mask = source.marker_mask
    dilation_size = source.source_dilation_size
    blank_final, _, _ = _apply_stages(Image.new("RGB", clean_image.size, "white"), marker_mask.copy(), deepcopy(clean_annotation), stages, int(scene["seed"]))
    final_masks = [_attributed_final_mask(item.source_layer, blank_final, clean_annotation, stages, int(scene["seed"]), dilation_size, marker_mask) for item in captured]
    overlaps: list[list[str]] = [[] for _ in captured]
    for left in range(len(captured)):
        for right in range(left + 1, len(captured)):
            if _masks_overlap(final_masks[left], final_masks[right]):
                overlaps[left].append(captured[right].content_id)
                overlaps[right].append(captured[left].content_id)

    audits: list[ElementSupportAudit] = []
    for index, item in enumerate(captured):
        final_mask = final_masks[index]
        clean_pixels = _support_pixels(item.binary_mask)
        final_pixels = _support_pixels(final_mask)
        clean_holes = _hole_count(item.binary_mask)
        final_holes = _hole_count(final_mask)
        center_x = round(item.clean_annotation_box[0] + item.clean_annotation_box[2] / 2.0)
        center_y = round(item.clean_annotation_box[1] + item.clean_annotation_box[3] / 2.0)
        open_retained = None
        if item.open_marker:
            open_retained = not (0 <= center_x < final_mask.width and 0 <= center_y < final_mask.height and bool(final_mask.getpixel((center_x, center_y))))
        audits.append(ElementSupportAudit(
            content_id=item.content_id,
            kind=item.kind,
            declared_source_box=item.declared_source_box,
            clean_annotation_box=item.clean_annotation_box,
            clean_rendered_bounds=item.binary_mask.getbbox(),
            attributed_final_bounds=final_mask.getbbox(),
            clean_support_pixels=clean_pixels,
            final_attributed_support_pixels=final_pixels,
            support_assessment="definite_attributed_erasure" if clean_pixels > 0 and final_pixels == 0 else "attributed_support_present",
            composited_visibility_assessment="not_established_overlapping_ink_ambiguous",
            readability_assessment="not_established_by_pixel_support" if item.kind == "text" else None,
            open_interior_retained=open_retained,
            clean_hole_count=clean_holes,
            attributed_final_hole_count=final_holes,
            attributed_component_count=_component_count(final_mask),
            overlaps_other_elements=tuple(sorted(overlaps[index])),
        ))

    axes = _axis_audits(clean_image, final_image, annotation, stages)
    markers = tuple(item for item in audits if item.kind == "marker")
    texts = tuple(item for item in audits if item.kind == "text")
    failures = tuple(
        [f"axis:{item.panel_id}:{item.axis}:{item.classification}" for item in axes if item.classification in {"invalid_clean_source", "destructively_erased"}]
        + [f"{item.kind}:{item.content_id}:definite_attributed_erasure" for item in audits if item.support_assessment == "definite_attributed_erasure"]
        + [f"marker:{item.content_id}:open_interior_lost" for item in markers if item.open_interior_retained is False]
        + [f"text:{item.content_id}:counter_topology_changed" for item in texts if item.clean_hole_count != item.attributed_final_hole_count]
    )
    audit = VisibleContentRenderAudit(
        generator_version=GENERATOR_VERSION,
        source_dilation_size=dilation_size,
        degradation_kinds=tuple(_stage_kind(stage) for stage in stages),
        axes=axes,
        markers=markers,
        texts=texts,
        ungraded_stage_kinds=(),
        failures=failures,
        limitations=(
            "element masks prove isolated source-owned support only; overlapping ink does not prove composited glyph visibility",
            "pixel support and unchanged hole count do not establish text readability",
            "component counts, counters and open-interior checks are diagnostics, not an acceptance gate",
            "per-element capture is restricted to an isolated single-threaded diagnostic process",
        ),
    )
    return VisibleContentRenderResult(final_image, annotation, final_marker_mask, audit)


__all__ = ["ElementSupportAudit", "GENERATOR_VERSION", "VisibleContentRenderAudit", "VisibleContentRenderResult", "VisibleContentSourceResult", "render_visible_content_scene", "render_visible_content_source"]
