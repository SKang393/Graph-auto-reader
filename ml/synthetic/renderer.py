# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic Pillow renderer for resolved synthetic SCD graph scenes.

Input contract
--------------
``render_scene`` accepts a resolved ``dict`` containing ``canvas`` and
``panels`` plus optional top-level ``annotations``, ``hard_negatives``,
``degradations``, ``presentation``, and ``families``.  Pixel geometry may use
``[x, y]`` points, ``[x1, y1, x2, y2]`` lines, and
``[x, y, width, height]`` boxes.  Points without an
explicit pixel ``center`` are mapped from graph values through the owning
panel's plot box and axis ranges.

Public API
----------
``render_scene(scene: dict) -> tuple[PIL.Image.Image, dict, PIL.Image.Image]``
returns the RGB graph image, perfect original-pixel annotations, and a single
mode-``L`` binary mask containing marker glyph pixels only.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping, Sequence
import io
import math
import random
from typing import Any

from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageFilter, ImageFont

from .fonts import FontResolver


RENDERER_ID = "graph-auto-reader-pillow"
RENDERER_VERSION = "1.0"
COORDINATE_SPACE = "original_pixels"


def production_resize_dimensions(
    width: int,
    height: int,
    *,
    maximum_side: int = 960,
    stride: int = 128,
) -> dict[str, float | int]:
    """Return deterministic DB resize and stride-alignment metadata."""

    if min(width, height, maximum_side, stride) <= 0:
        raise ValueError("resize dimensions and stride must be positive")
    scale = maximum_side / float(max(width, height))
    resized_width = max(1, int(width * scale))
    resized_height = max(1, int(height * scale))
    aligned_width = ((resized_width + stride - 1) // stride) * stride
    aligned_height = ((resized_height + stride - 1) // stride) * stride
    return {
        "scale": scale,
        "resized_width": resized_width,
        "resized_height": resized_height,
        "aligned_width": aligned_width,
        "aligned_height": aligned_height,
        "tensor_scale_x": aligned_width / float(width),
        "tensor_scale_y": aligned_height / float(height),
        "maximum_side": maximum_side,
        "stride": stride,
    }

MARKER_SHAPES = frozenset(
    {
        "circle",
        "square",
        "triangle_up",
        "triangle_down",
        "diamond",
        "star",
        "asterisk",
        "cross",
        "other",
    }
)
MARKER_FILLS = frozenset({"filled", "open", "degraded"})
LINE_STYLES = frozenset({"solid", "dashed", "dotted", "missing", "partially_occluded"})
HARD_NEGATIVE_KINDS = frozenset(
    {
        "marker_like_letter",
        "arrowhead",
        "divider_intersection",
        "tick",
        "dotted_segment",
        "legend_glyph",
        "bracket",
        "punctuation",
        "endpoint",
    }
)


def _float(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be numeric")
    number = float(value)
    if not math.isfinite(number):
        raise ValueError(f"{name} must be finite")
    return number


def _positive_int(value: Any, name: str) -> int:
    number = _float(value, name)
    if number <= 0 or not number.is_integer():
        raise ValueError(f"{name} must be a positive integer")
    return int(number)


def _point(value: Any, name: str = "point") -> tuple[float, float]:
    if isinstance(value, Mapping):
        if "x" in value and "y" in value:
            return _float(value["x"], f"{name}.x"), _float(value["y"], f"{name}.y")
        if "pixel_x" in value and "pixel_y" in value:
            return (
                _float(value["pixel_x"], f"{name}.pixel_x"),
                _float(value["pixel_y"], f"{name}.pixel_y"),
            )
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes)) and len(value) == 2:
        return _float(value[0], f"{name}[0]"), _float(value[1], f"{name}[1]")
    raise ValueError(f"{name} must be [x, y] or an x/y mapping")


def _box(value: Any, name: str = "box") -> tuple[float, float, float, float]:
    if isinstance(value, Mapping):
        keys = ("left", "top", "right", "bottom")
        if all(key in value for key in keys):
            result = tuple(_float(value[key], f"{name}.{key}") for key in keys)
        elif all(key in value for key in ("x", "y", "width", "height")):
            left = _float(value["x"], f"{name}.x")
            top = _float(value["y"], f"{name}.y")
            result = (
                left,
                top,
                left + _float(value["width"], f"{name}.width"),
                top + _float(value["height"], f"{name}.height"),
            )
        else:
            raise ValueError(f"{name} mapping must contain box coordinates")
    elif isinstance(value, Sequence) and not isinstance(value, (str, bytes)) and len(value) == 4:
        x = _float(value[0], f"{name}[0]")
        y = _float(value[1], f"{name}[1]")
        width = _float(value[2], f"{name}[2]")
        height = _float(value[3], f"{name}[3]")
        result = (x, y, x + width, y + height)
    else:
        raise ValueError(f"{name} must be [x, y, width, height] or a box mapping")
    left, top, right, bottom = result
    if right < left or bottom < top:
        raise ValueError(f"{name} has inverted bounds")
    return left, top, right, bottom


def _json_point(point: tuple[float, float]) -> list[float]:
    return [round(point[0], 6), round(point[1], 6)]


def _json_box(box: tuple[float, float, float, float]) -> list[float]:
    return [
        round(box[0], 6),
        round(box[1], 6),
        round(box[2] - box[0], 6),
        round(box[3] - box[1], 6),
    ]


def _line_points(value: Any, name: str = "line") -> tuple[tuple[float, float], tuple[float, float]]:
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
        if len(value) == 4:
            return (
                (_float(value[0], f"{name}[0]"), _float(value[1], f"{name}[1]")),
                (_float(value[2], f"{name}[2]"), _float(value[3], f"{name}[3]")),
            )
        if len(value) == 2:
            return _point(value[0], f"{name}[0]"), _point(value[1], f"{name}[1]")
    raise ValueError(f"{name} must be [x1, y1, x2, y2] or two points")


def _identifier(item: Mapping[str, Any], kind: str, fallback: str) -> str:
    for key in (f"{kind}_id", "id"):
        value = item.get(key)
        if value is not None and str(value):
            return str(value)
    return fallback


def _color(value: Any, default: tuple[int, int, int] = (0, 0, 0)) -> tuple[int, int, int]:
    if value is None:
        return default
    if isinstance(value, int) and not isinstance(value, bool):
        channel = max(0, min(255, value))
        return channel, channel, channel
    if isinstance(value, str):
        normalized = value.strip().lstrip("#")
        if len(normalized) == 3:
            normalized = "".join(character * 2 for character in normalized)
        if len(normalized) == 6:
            try:
                return tuple(int(normalized[index : index + 2], 16) for index in (0, 2, 4))
            except ValueError:
                pass
        named = {"black": (0, 0, 0), "white": (255, 255, 255), "gray": (128, 128, 128)}
        if value.strip().casefold() in named:
            return named[value.strip().casefold()]
    if isinstance(value, Sequence) and len(value) >= 3:
        return tuple(max(0, min(255, int(value[index]))) for index in range(3))
    raise ValueError(f"Unsupported color value: {value!r}")


def _line(
    draw: ImageDraw.ImageDraw,
    start: tuple[float, float],
    end: tuple[float, float],
    *,
    fill: tuple[int, int, int] | int,
    width: int,
    style: str = "solid",
    dash: tuple[float, float] = (6.0, 4.0),
) -> bool:
    """Draw a styled segment and report whether any pixels were drawn."""

    style = style.casefold().replace("-", "_")
    if style == "dotted":
        dash = (1.5, 3.5)
        style = "dashed"
    if style == "missing":
        return False
    if style == "partially_occluded":
        dx = end[0] - start[0]
        dy = end[1] - start[1]
        left_end = (start[0] + dx * 0.38, start[1] + dy * 0.38)
        right_start = (start[0] + dx * 0.62, start[1] + dy * 0.62)
        draw.line((start, left_end), fill=fill, width=width)
        draw.line((right_start, end), fill=fill, width=width)
        return True
    if style == "solid":
        draw.line((start, end), fill=fill, width=width)
        return True
    if style != "dashed":
        raise ValueError(f"Unsupported line style '{style}'")

    dx = end[0] - start[0]
    dy = end[1] - start[1]
    length = math.hypot(dx, dy)
    if length == 0:
        draw.point(start, fill=fill)
        return True
    on, off = dash
    distance = 0.0
    while distance < length:
        segment_end = min(length, distance + on)
        p0 = (start[0] + dx * distance / length, start[1] + dy * distance / length)
        p1 = (start[0] + dx * segment_end / length, start[1] + dy * segment_end / length)
        draw.line((p0, p1), fill=fill, width=width)
        distance += on + off
    return True


def _regular_polygon(
    center: tuple[float, float], radius: float, vertices: int, rotation: float
) -> list[tuple[float, float]]:
    return [
        (
            center[0] + radius * math.cos(rotation + index * math.tau / vertices),
            center[1] + radius * math.sin(rotation + index * math.tau / vertices),
        )
        for index in range(vertices)
    ]


def _star(center: tuple[float, float], radius: float) -> list[tuple[float, float]]:
    points: list[tuple[float, float]] = []
    for index in range(10):
        current_radius = radius if index % 2 == 0 else radius * 0.43
        angle = -math.pi / 2 + index * math.pi / 5
        points.append(
            (
                center[0] + current_radius * math.cos(angle),
                center[1] + current_radius * math.sin(angle),
            )
        )
    return points


def _marker_geometry(
    shape: str, center: tuple[float, float], radius: float
) -> tuple[str, Any]:
    left, top = center[0] - radius, center[1] - radius
    right, bottom = center[0] + radius, center[1] + radius
    if shape == "circle":
        return "ellipse", (left, top, right, bottom)
    if shape == "square":
        return "polygon", [(left, top), (right, top), (right, bottom), (left, bottom)]
    if shape == "triangle_up":
        return "polygon", [(center[0], top), (right, bottom), (left, bottom)]
    if shape == "triangle_down":
        return "polygon", [(left, top), (right, top), (center[0], bottom)]
    if shape == "diamond":
        return "polygon", [(center[0], top), (right, center[1]), (center[0], bottom), (left, center[1])]
    if shape == "star":
        return "polygon", _star(center, radius)
    if shape == "other":
        return "polygon", _regular_polygon(center, radius, 6, math.pi / 6)
    if shape == "asterisk":
        return "asterisk", None
    if shape == "cross":
        return "cross", None
    raise ValueError(f"Unsupported marker shape '{shape}'")


def _draw_marker(
    draw: ImageDraw.ImageDraw,
    mask_draw: ImageDraw.ImageDraw,
    center: tuple[float, float],
    radius: float,
    shape: str,
    fill_state: str,
    color: tuple[int, int, int],
    stroke_width: int,
) -> None:
    if shape not in MARKER_SHAPES:
        raise ValueError(f"Unsupported marker shape '{shape}'")
    if fill_state not in MARKER_FILLS:
        raise ValueError(f"Unsupported marker fill '{fill_state}'")
    geometry_kind, geometry = _marker_geometry(shape, center, radius)
    marker_fill = color if fill_state == "filled" else ((255, 255, 255) if fill_state == "open" else (176, 176, 176))
    # The combined training mask represents the full marker extent, including
    # the center of open glyphs.  This guarantees one positive center per
    # plotted point while the separate ``fill`` label retains the hole state.
    mask_fill = 255

    if geometry_kind == "ellipse":
        draw.ellipse(geometry, fill=marker_fill, outline=color, width=stroke_width)
        mask_draw.ellipse(geometry, fill=mask_fill, outline=255, width=stroke_width)
    elif geometry_kind == "polygon":
        draw.polygon(geometry, fill=marker_fill)
        draw.line((*geometry, geometry[0]), fill=color, width=stroke_width, joint="curve")
        mask_draw.polygon(geometry, fill=mask_fill)
        mask_draw.line((*geometry, geometry[0]), fill=255, width=stroke_width, joint="curve")
    elif geometry_kind == "asterisk":
        for angle in (0.0, math.pi / 3, 2 * math.pi / 3):
            dx = radius * math.cos(angle)
            dy = radius * math.sin(angle)
            draw.line(((center[0] - dx, center[1] - dy), (center[0] + dx, center[1] + dy)), fill=color, width=stroke_width)
            mask_draw.line(((center[0] - dx, center[1] - dy), (center[0] + dx, center[1] + dy)), fill=255, width=stroke_width)
    else:
        for slope in (-1, 1):
            start = (center[0] - radius, center[1] - slope * radius)
            end = (center[0] + radius, center[1] + slope * radius)
            draw.line((start, end), fill=color, width=stroke_width)
            mask_draw.line((start, end), fill=255, width=stroke_width)

    if fill_state == "degraded":
        # A deterministic broken-ink stripe distinguishes degraded fill while
        # retaining the exact marker center and extent.
        gap_y = center[1] + max(1.0, radius * 0.15)
        draw.line(
            ((center[0] - radius * 0.55, gap_y), (center[0] + radius * 0.15, gap_y)),
            fill=(255, 255, 255),
            width=max(1, stroke_width),
        )
        mask_draw.line(
            ((center[0] - radius * 0.55, gap_y), (center[0] + radius * 0.15, gap_y)),
            fill=0,
            width=max(1, stroke_width),
        )


def _font_settings(scene: Mapping[str, Any]) -> tuple[str | None, int, list[str]]:
    presentation = scene.get("presentation") if isinstance(scene.get("presentation"), Mapping) else {}
    families = scene.get("families") if isinstance(scene.get("families"), Mapping) else {}
    font_contract = scene.get("font") if isinstance(scene.get("font"), Mapping) else {}
    requested = (
        presentation.get("font")
        or presentation.get("font_family")
        or font_contract.get("path")
        or font_contract.get("generic_family")
    )
    if not requested and isinstance(families.get("font"), str):
        requested = families.get("font")
    size = presentation.get("font_size_px", presentation.get("font_size", 14))
    paths = presentation.get("font_search_paths", [])
    if not isinstance(paths, list):
        raise ValueError("presentation.font_search_paths must be a list")
    return (str(requested) if requested else None), _positive_int(size, "font_size_px"), [str(path) for path in paths]


def _text_bbox(
    draw: ImageDraw.ImageDraw,
    position: tuple[float, float],
    text: str,
    font: ImageFont.FreeTypeFont,
    anchor: str,
) -> tuple[float, float, float, float]:
    bbox = draw.textbbox(position, text, font=font, anchor=anchor)
    return tuple(float(value) for value in bbox)


def _draw_text(
    image: Image.Image,
    draw: ImageDraw.ImageDraw,
    item: Mapping[str, Any],
    *,
    font: ImageFont.FreeTypeFont,
    default_position: tuple[float, float],
    default_role: str,
    default_id: str,
) -> dict[str, Any]:
    text = str(item.get("text", item.get("label", "")))
    position_value = item.get("position", item.get("origin", default_position))
    position = _point(position_value, f"{default_id}.position")
    anchor = str(item.get("anchor", "la"))
    visible = bool(item.get("visible", not item.get("hidden", False)))
    angle = _float(item.get("angle", item.get("rotation_degrees", 0.0)), f"{default_id}.angle")
    fill = _color(item.get("color"))
    measured_box = (position[0], position[1], position[0], position[1])
    rendered_pixel_box: list[float] | None = None

    if visible and text:
        nominal = _text_bbox(draw, position, text, font, anchor)
        padding = 3
        origin_x = math.floor(nominal[0]) - padding
        origin_y = math.floor(nominal[1]) - padding
        scratch_width = max(1, math.ceil(nominal[2]) - origin_x + padding)
        scratch_height = max(1, math.ceil(nominal[3]) - origin_y + padding)
        layer = Image.new("RGBA", (scratch_width, scratch_height), (255, 255, 255, 0))
        layer_draw = ImageDraw.Draw(layer)
        layer_draw.text(
            (position[0] - origin_x, position[1] - origin_y),
            text,
            font=font,
            fill=(*fill, 255),
            anchor=anchor,
        )

        paste_x, paste_y = origin_x, origin_y
        if angle != 0:
            center_x = origin_x + layer.width / 2
            center_y = origin_y + layer.height / 2
            layer = layer.rotate(-angle, expand=True, resample=Image.Resampling.BICUBIC)
            paste_x = round(center_x - layer.width / 2)
            paste_y = round(center_y - layer.height / 2)

        visible_fraction = max(0.0, min(1.0, float(item.get("visible_fraction", 1.0))))
        if visible_fraction < 1.0:
            clip_x = int(round(layer.width * visible_fraction))
            if clip_x < layer.width:
                ImageDraw.Draw(layer).rectangle(
                    (clip_x, 0, layer.width, layer.height),
                    fill=(255, 255, 255, 0),
                )

        alpha_box = layer.getchannel("A").getbbox()
        if alpha_box is not None:
            raw_box = (
                float(paste_x + alpha_box[0]),
                float(paste_y + alpha_box[1]),
                float(paste_x + alpha_box[2]),
                float(paste_y + alpha_box[3]),
            )
            measured_box = (
                max(0.0, raw_box[0]),
                max(0.0, raw_box[1]),
                min(float(image.width), raw_box[2]),
                min(float(image.height), raw_box[3]),
            )
            if measured_box[2] >= measured_box[0] and measured_box[3] >= measured_box[1]:
                rendered_pixel_box = _json_box(measured_box)
            image.paste(layer, (paste_x, paste_y), layer)

    return {
        "text_id": _identifier(item, "text", default_id),
        "region_id": str(item.get("region_id", _identifier(item, "text", default_id))),
        "text": text,
        "role": str(item.get("role", default_role)),
        "box": _json_box(measured_box),
        "rendered_pixel_box": rendered_pixel_box,
        "position": _json_point(position),
        "rotation_degrees": angle,
        "visible": visible,
        "partial": bool(item.get("partial", float(item.get("visible_fraction", 1.0)) < 1.0)),
        "coordinate_space": COORDINATE_SPACE,
    }


def _axis_range(panel: Mapping[str, Any], axis: str, default: tuple[float, float]) -> tuple[float, float]:
    direct = panel.get(f"{axis}_range")
    axes = panel.get("axes") if isinstance(panel.get("axes"), Mapping) else {}
    axis_data = axes.get(axis) if isinstance(axes.get(axis), Mapping) else {}
    candidate = direct or axis_data.get("range")
    if isinstance(candidate, Sequence) and not isinstance(candidate, (str, bytes)) and len(candidate) == 2:
        low, high = _float(candidate[0], f"{axis}_range[0]"), _float(candidate[1], f"{axis}_range[1]")
    else:
        low = _float(axis_data.get("min", default[0]), f"{axis}_min")
        high = _float(axis_data.get("max", default[1]), f"{axis}_max")
    if high == low:
        raise ValueError(f"{axis}-axis range cannot have zero span")
    return low, high


def _point_center(point: Mapping[str, Any], panel: Mapping[str, Any], plot_box: tuple[float, float, float, float]) -> tuple[float, float]:
    for key in ("center", "screen", "pixel", "original_pixel"):
        if key in point:
            return _point(point[key], f"point.{key}")
    if "pixel_x" in point and "pixel_y" in point:
        return _float(point["pixel_x"], "pixel_x"), _float(point["pixel_y"], "pixel_y")
    if "x_px" in point and "y_px" in point:
        return _float(point["x_px"], "x_px"), _float(point["y_px"], "y_px")

    x_value = point.get("x_value", point.get("session", point.get("x")))
    y_value = point.get("y_value", point.get("value", point.get("y")))
    if x_value is None or y_value is None:
        raise ValueError("Every visible point needs a pixel center or graph x/y values")
    x_number = _float(x_value, "point.x_value")
    y_number = _float(y_value, "point.y_value")

    positions = panel.get("session_positions")
    x_pixel: float | None = None
    if isinstance(positions, Mapping):
        raw = positions.get(str(x_value), positions.get(x_value))
        if raw is not None:
            x_pixel = _float(raw, "session_positions")
    elif isinstance(positions, Sequence) and not isinstance(positions, (str, bytes)):
        x_min, _ = _axis_range(panel, "x", (1.0, float(max(2, len(positions)))))
        index = int(round(x_number - x_min))
        if 0 <= index < len(positions):
            x_pixel = _float(positions[index], "session_positions")

    x_min, x_max = _axis_range(panel, "x", (1.0, 100.0))
    y_min, y_max = _axis_range(panel, "y", (0.0, 100.0))
    left, top, right, bottom = plot_box
    if x_pixel is None:
        x_pixel = left + (x_number - x_min) / (x_max - x_min) * (right - left)
    y_pixel = bottom - (y_number - y_min) / (y_max - y_min) * (bottom - top)
    return x_pixel, y_pixel


def _flatten_annotations(scene: Mapping[str, Any], kind: str) -> list[Mapping[str, Any]]:
    annotations = scene.get("annotations")
    if isinstance(annotations, Mapping):
        value = annotations.get(kind, [])
        return [item for item in value if isinstance(item, Mapping)] if isinstance(value, list) else []
    if isinstance(annotations, list):
        return [
            item
            for item in annotations
            if isinstance(item, Mapping) and str(item.get("kind", item.get("type", ""))).casefold() == kind.rstrip("s").casefold()
        ]
    return []


def _draw_arrow(draw: ImageDraw.ImageDraw, item: Mapping[str, Any], fallback: str) -> dict[str, Any]:
    start = _point(item.get("start", item.get("from")), f"{fallback}.start")
    end = _point(item.get("tip", item.get("end", item.get("to"))), f"{fallback}.tip")
    color = _color(item.get("color"))
    width = _positive_int(item.get("width", 1), f"{fallback}.width")
    draw.line((start, end), fill=color, width=width)
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    head = _float(item.get("head_size", 7), f"{fallback}.head_size")
    polygon = [
        end,
        (end[0] - head * math.cos(angle - math.pi / 6), end[1] - head * math.sin(angle - math.pi / 6)),
        (end[0] - head * math.cos(angle + math.pi / 6), end[1] - head * math.sin(angle + math.pi / 6)),
    ]
    draw.polygon(polygon, fill=color)
    return {
        "arrow_id": _identifier(item, "arrow", fallback),
        "line": [_json_point(start), _json_point(end)],
        "start": _json_point(start),
        "tip": _json_point(end),
        "arrowhead_polygon": [_json_point(point) for point in polygon],
        "role": str(item.get("role", "annotation")),
        "label": item.get("label"),
        "coordinate_space": COORDINATE_SPACE,
    }


def _draw_bracket(draw: ImageDraw.ImageDraw, item: Mapping[str, Any], fallback: str) -> dict[str, Any]:
    supplied_points = item.get("points")
    if isinstance(supplied_points, list) and len(supplied_points) >= 3:
        vertices = [_point(point, f"{fallback}.points") for point in supplied_points]
        color = _color(item.get("color"))
        width = _positive_int(item.get("width", 1), f"{fallback}.width")
        draw.line(vertices, fill=color, width=width, joint="curve")
        return {
            "bracket_id": _identifier(item, "bracket", fallback),
            "polyline": [_json_point(point) for point in vertices],
            "points": [_json_point(point) for point in vertices],
            "label": item.get("label"),
            "coordinate_space": COORDINATE_SPACE,
        }
    start = _point(item.get("start", item.get("from")), f"{fallback}.start")
    end = _point(item.get("end", item.get("to")), f"{fallback}.end")
    depth = _float(item.get("depth", item.get("cap_length", 6)), f"{fallback}.depth")
    width = _positive_int(item.get("width", 1), f"{fallback}.width")
    color = _color(item.get("color"))
    orientation = str(item.get("orientation", "horizontal"))
    if orientation == "vertical":
        vertices = [start, (start[0] + depth, start[1]), (end[0] + depth, end[1]), end]
    else:
        vertices = [start, (start[0], start[1] + depth), (end[0], end[1] + depth), end]
    draw.line(vertices, fill=color, width=width, joint="curve")
    return {
        "bracket_id": _identifier(item, "bracket", fallback),
        "polyline": [_json_point(point) for point in vertices],
        "points": [_json_point(point) for point in vertices],
        "label": item.get("label"),
        "coordinate_space": COORDINATE_SPACE,
    }


def _hard_negative_center(item: Mapping[str, Any]) -> tuple[float, float]:
    for key in ("center", "position", "point"):
        if key in item:
            return _point(item[key], f"hard_negative.{key}")
    if "box" in item:
        left, top, right, bottom = _box(item["box"], "hard_negative.box")
        return (left + right) / 2, (top + bottom) / 2
    geometry = item.get("geometry")
    if isinstance(geometry, Mapping):
        coordinates = geometry.get("coordinates")
        geometry_kind = str(geometry.get("kind", "point"))
        if isinstance(coordinates, Sequence) and not isinstance(coordinates, (str, bytes)):
            values = [_float(value, "hard_negative.geometry.coordinates") for value in coordinates]
            if geometry_kind == "point" and len(values) >= 2:
                return values[0], values[1]
            if geometry_kind == "line" and len(values) >= 4:
                return (values[0] + values[2]) / 2, (values[1] + values[3]) / 2
            if geometry_kind == "box" and len(values) >= 4:
                return values[0] + values[2] / 2, values[1] + values[3] / 2
            if geometry_kind == "polyline" and len(values) >= 2:
                point_count = len(values) // 2
                middle_offset = (point_count // 2) * 2
                return values[middle_offset], values[middle_offset + 1]
    raise ValueError("Hard negative needs center, position, point, or box")


def _ensure_canvas_point(
    point: tuple[float, float], width: int, height: int, name: str
) -> None:
    if not (0.0 <= point[0] < width and 0.0 <= point[1] < height):
        raise ValueError(
            f"{name} lies outside the {width}x{height} canvas: {point}"
        )


def _validate_requested_hard_negative(
    item: Mapping[str, Any], width: int, height: int, name: str
) -> None:
    geometry = item.get("geometry")
    if not isinstance(geometry, Mapping):
        raise ValueError(f"{name}.geometry must be an object")
    kind = str(geometry.get("kind", ""))
    coordinates = geometry.get("coordinates")
    if not isinstance(coordinates, Sequence) or isinstance(coordinates, (str, bytes)):
        raise ValueError(f"{name}.geometry.coordinates must be an array")
    values = [_float(value, f"{name}.geometry.coordinates") for value in coordinates]
    if kind == "point":
        if len(values) != 2:
            raise ValueError(f"{name} point geometry must have two coordinates")
        _ensure_canvas_point((values[0], values[1]), width, height, name)
    elif kind == "line":
        if len(values) != 4:
            raise ValueError(f"{name} line geometry must have four coordinates")
        _ensure_canvas_point((values[0], values[1]), width, height, name)
        _ensure_canvas_point((values[2], values[3]), width, height, name)
    elif kind == "box":
        if len(values) != 4 or values[2] <= 0 or values[3] <= 0:
            raise ValueError(f"{name} box geometry must be [x, y, positive width, positive height]")
        _ensure_canvas_point((values[0], values[1]), width, height, name)
        if values[0] + values[2] > width or values[1] + values[3] > height:
            raise ValueError(f"{name} box geometry extends outside the canvas")
    elif kind == "polyline":
        if len(values) < 4 or len(values) % 2:
            raise ValueError(f"{name} polyline geometry needs complete x/y pairs")
        for offset in range(0, len(values), 2):
            _ensure_canvas_point((values[offset], values[offset + 1]), width, height, name)
    else:
        raise ValueError(f"{name} has unsupported geometry kind '{kind}'")


def _validate_rendered_hard_negative(
    record: Mapping[str, Any], width: int, height: int, name: str
) -> None:
    geometry = record.get("geometry")
    if not isinstance(geometry, Mapping):
        raise ValueError(f"{name} rendered geometry is missing")
    if "center" in geometry:
        _ensure_canvas_point(_point(geometry["center"], f"{name}.center"), width, height, name)
    if "box" in geometry:
        left, top, right, bottom = _box(geometry["box"], f"{name}.box")
        if left < 0 or top < 0 or right > width or bottom > height:
            raise ValueError(f"{name} rendered box extends outside the canvas")
    for key in ("line", "polygon", "polyline"):
        value = geometry.get(key)
        if isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
            points = value if value and isinstance(value[0], Sequence) else [value]
            for point_value in points:
                _ensure_canvas_point(_point(point_value, f"{name}.{key}"), width, height, name)
    segments = geometry.get("segments")
    if isinstance(segments, Sequence):
        for segment in segments:
            if isinstance(segment, Sequence):
                for point_value in segment:
                    _ensure_canvas_point(_point(point_value, f"{name}.segments"), width, height, name)


def _paint_hard_negative(
    image: Image.Image,
    draw: ImageDraw.ImageDraw,
    item: Mapping[str, Any],
    font: ImageFont.FreeTypeFont,
    kind: str,
    center: tuple[float, float],
    radius: float,
    color: tuple[int, int, int],
    width: int,
) -> dict[str, Any]:
    geometry: dict[str, Any] = {"center": _json_point(center)}

    if kind == "marker_like_letter":
        text = str(item.get("text", "o"))
        bbox = draw.textbbox(center, text, font=font, anchor="mm")
        draw.text(center, text, font=font, fill=color, anchor="mm")
        geometry["box"] = _json_box(tuple(float(value) for value in bbox))
        geometry["text"] = text
    elif kind == "arrowhead":
        polygon = _regular_polygon(center, radius, 3, -math.pi / 2)
        draw.polygon(polygon, fill=color)
        geometry["polygon"] = [_json_point(point) for point in polygon]
    elif kind == "divider_intersection":
        horizontal = ((center[0] - radius, center[1]), (center[0] + radius, center[1]))
        vertical = ((center[0], center[1] - radius), (center[0], center[1] + radius))
        draw.line(horizontal, fill=color, width=width)
        draw.line(vertical, fill=color, width=width)
        geometry["segments"] = [[_json_point(point) for point in horizontal], [_json_point(point) for point in vertical]]
    elif kind == "tick":
        segment = ((center[0], center[1] - radius), (center[0], center[1] + radius))
        draw.line(segment, fill=color, width=width)
        geometry["line"] = [_json_point(point) for point in segment]
    elif kind == "dotted_segment":
        start, end = (center[0], center[1] - radius), (center[0], center[1] + radius)
        _line(draw, start, end, fill=color, width=width, style="dotted")
        geometry["line"] = [_json_point(start), _json_point(end)]
    elif kind == "legend_glyph":
        shape = str(item.get("shape", "circle"))
        fill_state = str(item.get("fill", "open"))
        ignored_mask = Image.new("L", image.size, 0)
        _draw_marker(draw, ImageDraw.Draw(ignored_mask), center, radius, shape, fill_state, color, width)
        geometry.update({"shape": shape, "fill": fill_state, "box": _json_box((center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius))})
    elif kind == "bracket":
        vertices = [(center[0] - radius, center[1] - radius), (center[0] - radius, center[1] + radius), (center[0] + radius, center[1] + radius), (center[0] + radius, center[1] - radius)]
        draw.line(vertices, fill=color, width=width)
        geometry["polyline"] = [_json_point(point) for point in vertices]
    elif kind == "punctuation":
        text = str(item.get("text", "."))
        bbox = draw.textbbox(center, text, font=font, anchor="mm")
        draw.text(center, text, font=font, fill=color, anchor="mm")
        geometry.update({"box": _json_box(tuple(float(value) for value in bbox)), "text": text})
    else:
        start, end = (center[0] - radius, center[1]), center
        draw.line((start, end), fill=color, width=width)
        draw.ellipse((center[0] - width, center[1] - width, center[0] + width, center[1] + width), fill=color)
        geometry["line"] = [_json_point(start), _json_point(end)]
    return geometry


def _draw_hard_negative(
    image: Image.Image,
    draw: ImageDraw.ImageDraw,
    item: Mapping[str, Any],
    font: ImageFont.FreeTypeFont,
    fallback: str,
    marker_mask: Image.Image,
    reserved_mask: Image.Image,
    safe_box: tuple[float, float, float, float],
    rng: random.Random,
    clearance_px: int,
) -> tuple[dict[str, Any], Image.Image]:
    _validate_requested_hard_negative(item, image.width, image.height, fallback)
    raw_kind = str(item.get("kind", item.get("type", ""))).casefold().replace("-", "_")
    aliases = {
        "letter": "marker_like_letter",
        "marker_like_letters": "marker_like_letter",
        "phase_line_intersection": "divider_intersection",
        "tick_mark": "tick",
        "dotted_divider_segment": "dotted_segment",
        "legend_symbol": "legend_glyph",
        "isolated_punctuation": "punctuation",
        "line_endpoint": "endpoint",
    }
    kind = aliases.get(raw_kind, raw_kind)
    if kind not in HARD_NEGATIVE_KINDS:
        raise ValueError(f"Unsupported hard-negative kind '{raw_kind}'")
    requested_center = _hard_negative_center(item)
    radius = _float(item.get("radius", item.get("size", 5)), f"{fallback}.radius")
    color = _color(item.get("color"))
    width = _positive_int(item.get("width", 1), f"{fallback}.width")

    clearance_size = max(3, clearance_px * 2 + 1)
    clearance = marker_mask.filter(ImageFilter.MaxFilter(clearance_size))
    extent = max(radius + width + 2.0, float(getattr(font, "size", 14)) / 2.0 + 3.0)
    left, top, right, bottom = safe_box
    step = max(8, int(math.ceil(extent * 2.0 + 2.0)))
    candidates = [requested_center]
    lane_candidates = [
        (float(x), float(y))
        for y in range(math.ceil(top + extent), math.floor(bottom - extent) + 1, step)
        for x in range(math.ceil(left + extent), math.floor(right - extent) + 1, step)
    ]
    rng.shuffle(lane_candidates)
    candidates.extend(lane_candidates[:512])

    selected_center: tuple[float, float] | None = None
    selected_geometry: dict[str, Any] | None = None
    selected_mask: Image.Image | None = None
    for candidate in candidates:
        scratch = Image.new("RGB", image.size, (255, 255, 255))
        scratch_draw = ImageDraw.Draw(scratch)
        geometry = _paint_hard_negative(
            scratch,
            scratch_draw,
            item,
            font,
            kind,
            candidate,
            radius,
            (0, 0, 0),
            width,
        )
        candidate_record = {"geometry": geometry}
        try:
            _validate_rendered_hard_negative(
                candidate_record, image.width, image.height, fallback
            )
        except ValueError:
            continue
        footprint = ImageChops.invert(scratch.convert("L")).point(
            lambda value: 255 if value else 0
        )
        if footprint.getbbox() is None:
            continue
        if ImageChops.multiply(footprint, clearance).getbbox() is not None:
            continue
        if ImageChops.multiply(footprint, reserved_mask).getbbox() is not None:
            continue
        selected_center = candidate
        selected_geometry = geometry
        selected_mask = footprint
        break

    if selected_center is None or selected_geometry is None or selected_mask is None:
        raise ValueError(
            f"{fallback} could not be placed mask-disjoint from markers after "
            f"{len(candidates)} deterministic candidates"
        )

    geometry = _paint_hard_negative(
        image,
        draw,
        item,
        font,
        kind,
        selected_center,
        radius,
        color,
        width,
    )

    record = {
        "hard_negative_id": str(item.get("request_id", _identifier(item, "hard_negative", fallback))),
        "request_id": str(item.get("request_id", _identifier(item, "hard_negative", fallback))),
        "panel_id": item.get("panel_id"),
        # Preserve the frozen scene vocabulary in emitted labels.  The local
        # canonical name is used only to choose a drawing primitive.
        "kind": raw_kind,
        "geometry": geometry,
        "rendered_pixel_box": _json_box(
            tuple(float(value) for value in selected_mask.getbbox())
        ),
        "requested_center": _json_point(requested_center),
        "relocated": _json_point(selected_center) != _json_point(requested_center),
        "placement_attempts": candidates.index(selected_center) + 1,
        "excluded_from_marker_mask": True,
        "coordinate_space": COORDINATE_SPACE,
    }
    _validate_rendered_hard_negative(record, image.width, image.height, fallback)
    return record, selected_mask


def _apply_noise(image: Image.Image, rng: random.Random, sigma: float, impulse: float = 0.0) -> Image.Image:
    result = image.convert("RGB")
    pixels = result.load()
    for y in range(result.height):
        for x in range(result.width):
            source = pixels[x, y]
            if impulse > 0 and rng.random() < impulse:
                channel = 0 if rng.random() < 0.5 else 255
                pixels[x, y] = (channel, channel, channel)
            elif sigma > 0:
                pixels[x, y] = tuple(max(0, min(255, round(channel + rng.gauss(0, sigma)))) for channel in source)
    return result


def _scan_shadow(image: Image.Image, strength: float, side: str) -> Image.Image:
    result = image.convert("RGB")
    pixels = result.load()
    strength = max(0.0, min(0.9, strength))
    for y in range(result.height):
        for x in range(result.width):
            if side in {"top", "bottom"}:
                fraction = y / max(1, result.height - 1)
                if side == "bottom":
                    fraction = 1.0 - fraction
            else:
                fraction = x / max(1, result.width - 1)
                if side == "right":
                    fraction = 1.0 - fraction
            factor = 1.0 - strength * max(0.0, 1.0 - fraction * 4.0)
            pixels[x, y] = tuple(round(channel * factor) for channel in pixels[x, y])
    return result


def _clip_content_edge(
    image: Image.Image,
    marker_mask: Image.Image,
    amount_px: int,
    side: str,
) -> tuple[Image.Image, Image.Image, dict[str, Any]]:
    """Remove a strip from the requested edge of current rendered content.

    Clipping is deliberately lossy rather than geometric: coordinates do not
    move, while the identical realized rectangle is blanked in the RGB image
    and marker mask.  Basing the rectangle on current non-white pixels keeps
    the small 1-3 px degradation meaningful when the canvas has white margins.
    """

    if side not in {"left", "right", "top", "bottom"}:
        raise ValueError(f"Unsupported clipping side '{side}'")

    result = image.convert("RGB")
    result_mask = marker_mask.convert("L")
    white = Image.new("RGB", result.size, (255, 255, 255))
    foreground = ImageChops.difference(result, white)
    content_box = foreground.getbbox()
    realized_box: tuple[int, int, int, int] | None = None
    removed_content_pixels = 0
    removed_marker_pixels = 0

    if amount_px > 0 and content_box is not None:
        left, top, right, bottom = content_box
        if side == "left":
            realized_box = (left, top, min(right, left + amount_px), bottom)
        elif side == "right":
            realized_box = (max(left, right - amount_px), top, right, bottom)
        elif side == "top":
            realized_box = (left, top, right, min(bottom, top + amount_px))
        else:
            realized_box = (left, max(top, bottom - amount_px), right, bottom)

        clipped_foreground = foreground.crop(realized_box).convert("L")
        removed_content_pixels = sum(clipped_foreground.histogram()[1:])
        clipped_markers = result_mask.crop(realized_box)
        removed_marker_pixels = sum(clipped_markers.histogram()[1:])
        result.paste((255, 255, 255), realized_box)
        result_mask.paste(0, realized_box)

    content_box_after = ImageChops.difference(result, white).getbbox()
    metadata: dict[str, Any] = {
        "content_pixel_box_before": list(content_box) if content_box else None,
        "content_pixel_box_after": list(content_box_after) if content_box_after else None,
        "realized_clipping_pixel_box": list(realized_box) if realized_box else None,
        "removed_content_pixel_count": removed_content_pixels,
        "removed_marker_pixel_count": removed_marker_pixels,
        "annotation_coordinates_unchanged": True,
        "visibility_loss_recorded": realized_box is not None,
    }
    return result, result_mask, metadata


_IDENTITY_MATRIX = (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)


def _matrix_multiply(left: Sequence[float], right: Sequence[float]) -> tuple[float, ...]:
    return tuple(
        sum(left[row * 3 + offset] * right[offset * 3 + column] for offset in range(3))
        for row in range(3)
        for column in range(3)
    )


def _matrix_inverse(matrix: Sequence[float]) -> tuple[float, ...]:
    a, b, c, d, e, f, g, h, i = matrix
    determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    if abs(determinant) < 1e-12:
        raise ValueError("Geometric degradation produced a singular transform")
    return (
        (e * i - f * h) / determinant,
        (c * h - b * i) / determinant,
        (b * f - c * e) / determinant,
        (f * g - d * i) / determinant,
        (a * i - c * g) / determinant,
        (c * d - a * f) / determinant,
        (d * h - e * g) / determinant,
        (b * g - a * h) / determinant,
        (a * e - b * d) / determinant,
    )


def _apply_matrix(matrix: Sequence[float], point: tuple[float, float]) -> tuple[float, float]:
    denominator = matrix[6] * point[0] + matrix[7] * point[1] + matrix[8]
    if abs(denominator) < 1e-12:
        raise ValueError("Point maps to infinity under geometric degradation")
    return (
        (matrix[0] * point[0] + matrix[1] * point[1] + matrix[2]) / denominator,
        (matrix[3] * point[0] + matrix[4] * point[1] + matrix[5]) / denominator,
    )


def _solve_linear(system: list[list[float]], values: list[float]) -> list[float]:
    size = len(values)
    augmented = [row[:] + [values[index]] for index, row in enumerate(system)]
    for column in range(size):
        pivot = max(range(column, size), key=lambda row: abs(augmented[row][column]))
        if abs(augmented[pivot][column]) < 1e-12:
            raise ValueError("Perspective corner mapping is singular")
        augmented[column], augmented[pivot] = augmented[pivot], augmented[column]
        pivot_value = augmented[column][column]
        augmented[column] = [value / pivot_value for value in augmented[column]]
        for row in range(size):
            if row == column:
                continue
            factor = augmented[row][column]
            if factor:
                augmented[row] = [
                    augmented[row][entry] - factor * augmented[column][entry]
                    for entry in range(size + 1)
                ]
    return [augmented[index][-1] for index in range(size)]


def _homography(
    source: Sequence[tuple[float, float]], destination: Sequence[tuple[float, float]]
) -> tuple[float, ...]:
    system: list[list[float]] = []
    values: list[float] = []
    for (x, y), (target_x, target_y) in zip(source, destination, strict=True):
        system.append([x, y, 1.0, 0.0, 0.0, 0.0, -x * target_x, -y * target_x])
        values.append(target_x)
        system.append([0.0, 0.0, 0.0, x, y, 1.0, -x * target_y, -y * target_y])
        values.append(target_y)
    return (*_solve_linear(system, values), 1.0)


def _warp(
    image: Image.Image,
    marker_mask: Image.Image,
    forward: Sequence[float],
) -> tuple[Image.Image, Image.Image, tuple[float, ...]]:
    inverse = _matrix_inverse(forward)
    normalized = tuple(value / inverse[8] for value in inverse)
    coefficients = normalized[:8]
    transformed_image = image.transform(
        image.size,
        Image.Transform.PERSPECTIVE,
        coefficients,
        resample=Image.Resampling.BICUBIC,
        fillcolor=(255, 255, 255),
    )
    transformed_mask = marker_mask.transform(
        marker_mask.size,
        Image.Transform.PERSPECTIVE,
        coefficients,
        resample=Image.Resampling.NEAREST,
        fillcolor=0,
    ).point(lambda value: 255 if value else 0)
    return transformed_image, transformed_mask, inverse


def _warp_mask(mask: Image.Image, forward: Sequence[float]) -> Image.Image:
    inverse = _matrix_inverse(forward)
    normalized = tuple(value / inverse[8] for value in inverse)
    return mask.transform(
        mask.size,
        Image.Transform.PERSPECTIVE,
        normalized[:8],
        resample=Image.Resampling.NEAREST,
        fillcolor=0,
    ).point(lambda value: 255 if value else 0)


def _transformed_box_bounds(
    value: Any,
    matrix: Sequence[float],
) -> tuple[float, float, float, float]:
    left, top, right, bottom = _box(value, "annotation.box")
    points = [
        _apply_matrix(matrix, point)
        for point in ((left, top), (right, top), (right, bottom), (left, bottom))
    ]
    return (
        min(point[0] for point in points),
        min(point[1] for point in points),
        max(point[0] for point in points),
        max(point[1] for point in points),
    )


def _transform_box_value(value: Any, matrix: Sequence[float]) -> list[float]:
    return _json_box(_transformed_box_bounds(value, matrix))


def _transform_rendered_pixel_box(
    value: Any,
    matrix: Sequence[float],
    canvas_size: tuple[int, int],
) -> list[float] | None:
    """Transform realized text pixels and intersect them with the raster canvas."""

    left, top, right, bottom = _transformed_box_bounds(value, matrix)
    canvas_width, canvas_height = (float(dimension) for dimension in canvas_size)
    clipped = (
        max(0.0, left),
        max(0.0, top),
        min(canvas_width, right),
        min(canvas_height, bottom),
    )
    if clipped[2] <= clipped[0] or clipped[3] <= clipped[1]:
        return None

    result = _json_box(clipped)
    # Independent six-decimal rounding can place the reconstructed right or
    # bottom edge infinitesimally beyond an integer canvas boundary.
    for start_index, extent_index, boundary in ((0, 2, canvas_width), (1, 3, canvas_height)):
        if result[start_index] + result[extent_index] > boundary:
            result[extent_index] = round(result[extent_index] - 0.000001, 6)
    if result[2] <= 0 or result[3] <= 0:
        return None
    return result


def _transform_annotation_mapping(
    record: dict[str, Any], matrix: Sequence[float], canvas_size: tuple[int, int]
) -> None:
    point_keys = {"center", "position", "screen", "glyph_center", "start", "tip", "end"}
    box_keys = {"box", "plot_box", "glyph_box", "rendered_pixel_box"}
    line_keys = {"line"}
    poly_keys = {"polygon", "polyline", "arrowhead_polygon", "points"}

    for key, value in list(record.items()):
        if value is None or key == "graph":
            continue
        if key in point_keys and isinstance(value, Sequence) and not isinstance(value, (str, bytes)) and len(value) == 2:
            record[key] = _json_point(_apply_matrix(matrix, _point(value, f"annotation.{key}")))
        elif key in box_keys and isinstance(value, Sequence) and not isinstance(value, (str, bytes)) and len(value) == 4:
            record[key] = (
                _transform_rendered_pixel_box(value, matrix, canvas_size)
                if key == "rendered_pixel_box"
                else _transform_box_value(value, matrix)
            )
        elif key in line_keys and isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
            start, end = _line_points(value, f"annotation.{key}")
            record[key] = [
                _json_point(_apply_matrix(matrix, start)),
                _json_point(_apply_matrix(matrix, end)),
            ]
        elif key in poly_keys and isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
            if value and isinstance(value[0], Sequence):
                record[key] = [
                    _json_point(_apply_matrix(matrix, _point(point, f"annotation.{key}")))
                    for point in value
                ]
        elif key == "segments" and isinstance(value, Sequence):
            record[key] = [
                [
                    _json_point(_apply_matrix(matrix, _point(point, "annotation.segment")))
                    for point in segment
                ]
                for segment in value
            ]
        elif isinstance(value, dict):
            _transform_annotation_mapping(value, matrix, canvas_size)
        elif isinstance(value, list) and value and isinstance(value[0], dict):
            for item in value:
                _transform_annotation_mapping(item, matrix, canvas_size)

    if "screen_x_min" in record and "screen_x_max" in record:
        x_min = float(record["screen_x_min"])
        x_max = float(record["screen_x_max"])
        height = float(canvas_size[1] - 1)
        transformed = [
            _apply_matrix(matrix, point)
            for point in ((x_min, 0.0), (x_min, height), (x_max, 0.0), (x_max, height))
        ]
        record["screen_x_min"] = round(min(point[0] for point in transformed), 6)
        record["screen_x_max"] = round(max(point[0] for point in transformed), 6)
    if "x" in record and "divider_id" in record and isinstance(record.get("line"), list):
        record["x"] = record["line"][0][0]
    if "radius" in record and isinstance(record.get("box"), list):
        left, top, right, bottom = _box(record["box"], "annotation.marker.box")
        record["radius"] = round(max(right - left, bottom - top) / 2.0, 6)


def _transform_annotations(annotation: dict[str, Any], matrix: Sequence[float]) -> None:
    canvas_size = (int(annotation["canvas"]["width"]), int(annotation["canvas"]["height"]))
    for key in (
        "panels",
        "plots",
        "axes",
        "anchors",
        "ticks",
        "texts",
        "markers",
        "edges",
        "legends",
        "arrows",
        "brackets",
        "top_bars",
        "dividers",
        "phases",
        "hard_negatives",
    ):
        value = annotation.get(key)
        if isinstance(value, list):
            for record in value:
                if isinstance(record, dict):
                    _transform_annotation_mapping(record, matrix, canvas_size)


def _expand_marker_annotations(
    annotation: dict[str, Any], amount: float, side: str
) -> None:
    if amount <= 0:
        return
    width = float(annotation["canvas"]["width"])
    height = float(annotation["canvas"]["height"])
    for panel in annotation.get("panels", []):
        if not isinstance(panel, Mapping):
            continue
        for marker in panel.get("markers", []):
            if not isinstance(marker, dict) or not isinstance(marker.get("box"), list):
                continue
            left, top, right, bottom = _box(marker["box"], "annotation.marker.box")
            expanded = {
                "left": (max(0.0, left - amount), top, right, bottom),
                "right": (left, top, min(width, right + amount), bottom),
                "top": (left, max(0.0, top - amount), right, bottom),
                "bottom": (left, top, right, min(height, bottom + amount)),
                "all": (
                    max(0.0, left - amount),
                    max(0.0, top - amount),
                    min(width, right + amount),
                    min(height, bottom + amount),
                ),
            }[side]
            marker["box"] = _json_box(expanded)
            marker["radius"] = round(
                max(expanded[2] - expanded[0], expanded[3] - expanded[1]) / 2.0,
                6,
            )


_DEGRADATION_PARAMETERS: dict[str, frozenset[str]] = {
    "none": frozenset(),
    "identity": frozenset(),
    "downsample": frozenset({"scale", "resampler"}),
    "isotropic_blur": frozenset({"radius", "sigma"}),
    "anisotropic_blur": frozenset({"radius", "sigma"}),
    "gaussian_noise": frozenset({"sigma"}),
    "poisson_noise": frozenset({"scale"}),
    "impulse_noise": frozenset({"probability"}),
    "jpeg": frozenset({"quality"}),
    "ringing": frozenset({"factor"}),
    "overshoot": frozenset({"factor"}),
    "grayscale": frozenset(),
    "threshold": frozenset({"cutoff"}),
    "halftone": frozenset({"cell_size"}),
    "paper_texture": frozenset({"sigma"}),
    "faded_ink": frozenset({"opacity"}),
    "erosion": frozenset({"size"}),
    "dilation": frozenset({"size"}),
    "stroke_dropout": frozenset({"count", "length", "width"}),
    "ink_bleed": frozenset({"size"}),
    "scan_shadow": frozenset({"strength", "side"}),
    "clipping": frozenset({"amount_px", "side"}),
    "skew": frozenset({"strength", "side"}),
    "perspective": frozenset({"strength"}),
    "hand_drawn_jitter": frozenset({"strength", "side"}),
    "inconsistent_marker_outlines": frozenset({"strength"}),
    "line_marker_contact": frozenset({"strength"}),
}


def _degrade(
    image: Image.Image,
    marker_mask: Image.Image,
    annotation: dict[str, Any],
    stages: Any,
    seed: int,
) -> tuple[Image.Image, Image.Image, list[dict[str, Any]]]:
    if stages is None:
        stages = []
    if not isinstance(stages, list):
        raise ValueError("degradations must be a list")
    if len(stages) > 2:
        raise ValueError("A scene may contain at most two degradation stages")
    result = image.convert("RGB")
    result_mask = marker_mask.convert("L")
    records: list[dict[str, Any]] = []
    rng = random.Random(seed ^ 0x4D455441)
    cumulative_forward = _IDENTITY_MATRIX

    aliases = {
        "gaussian": "gaussian_noise",
        "noise": "gaussian_noise",
        "blur": "isotropic_blur",
        "jpeg_compression": "jpeg",
        "grayscale_conversion": "grayscale",
        "faded": "faded_ink",
        "thresholding": "threshold",
        "downsampling": "downsample",
    }
    for index, raw_stage in enumerate(stages):
        source_stage = {"kind": raw_stage} if isinstance(raw_stage, str) else dict(raw_stage)
        allowed_stage_keys = {
            "stage",
            "family_key",
            "kind",
            "parameters",
            "deterministic",
        }
        unknown_stage_keys = set(source_stage) - allowed_stage_keys
        if unknown_stage_keys:
            raise ValueError(
                f"degradations[{index}] contains unknown top-level keys: "
                f"{', '.join(sorted(unknown_stage_keys))}"
            )
        supplied_parameters = source_stage.get("parameters", {})
        if not isinstance(supplied_parameters, Mapping):
            raise ValueError(f"degradations[{index}].parameters must be an object")
        stage = dict(supplied_parameters)
        stage.update(source_stage)
        raw_kind = str(stage.get("kind", stage.get("type", stage.get("name", "")))).casefold().replace("-", "_")
        kind = aliases.get(raw_kind, raw_kind)
        parameters = {str(key): value for key, value in supplied_parameters.items()}
        if kind not in _DEGRADATION_PARAMETERS:
            raise ValueError(f"Unsupported degradation kind '{raw_kind}'")
        unknown_parameters = set(parameters) - _DEGRADATION_PARAMETERS[kind]
        if unknown_parameters:
            raise ValueError(
                f"degradations[{index}].parameters contains unknown keys for "
                f"'{kind}': {', '.join(sorted(unknown_parameters))}"
            )
        # Stage randomness is derived only from scene seed and stage order.
        # Per-stage seed parameters are intentionally rejected by the same
        # closed parameter vocabulary as the frozen scene schema.
        stage_rng = random.Random(rng.getrandbits(64))
        record: dict[str, Any] = {
            "stage": int(source_stage.get("stage", index + 1)),
            "family_key": source_stage.get("family_key"),
            "kind": kind,
            "parameters": parameters,
            "deterministic": bool(source_stage.get("deterministic", True)),
            "applied": True,
            "implementation": "genuine",
            "geometry_preserved": True,
        }
        forward: tuple[float, ...] | None = None

        if kind == "grayscale":
            result = result.convert("L").convert("RGB")
        elif kind in {"isotropic_blur", "anisotropic_blur"}:
            radius = max(0.0, float(stage.get("radius", stage.get("sigma", 0.8))))
            if kind == "isotropic_blur":
                result = result.filter(ImageFilter.GaussianBlur(radius))
            else:
                # Directional five-tap horizontal point-spread function.  It
                # is intentionally not reducible to an isotropic Gaussian.
                center_weight = max(1.0, 4.0 - min(radius, 3.0))
                taps = (1.0, 2.0, center_weight, 2.0, 1.0)
                kernel = [0.0] * 25
                kernel[10:15] = taps
                result = result.filter(
                    ImageFilter.Kernel((5, 5), kernel, scale=sum(taps))
                )
                record["direction"] = "horizontal"
        elif kind == "gaussian_noise":
            result = _apply_noise(result, stage_rng, max(0.0, float(stage.get("sigma", 4.0))))
        elif kind == "poisson_noise":
            # Signal-dependent Gaussian approximation is deterministic and
            # avoids a numerical-library dependency.
            scale = max(0.0, float(stage.get("scale", 0.12)))
            noisy = result.copy()
            pixels = noisy.load()
            for y in range(noisy.height):
                for x in range(noisy.width):
                    pixels[x, y] = tuple(max(0, min(255, round(channel + stage_rng.gauss(0, math.sqrt(max(1, channel)) * scale)))) for channel in pixels[x, y])
            result = noisy
        elif kind == "impulse_noise":
            result = _apply_noise(result, stage_rng, 0.0, max(0.0, min(1.0, float(stage.get("probability", 0.005)))))
        elif kind == "jpeg":
            quality = max(1, min(95, int(stage.get("quality", 70))))
            buffer = io.BytesIO()
            result.save(buffer, format="JPEG", quality=quality, optimize=False, progressive=False, subsampling=0)
            buffer.seek(0)
            with Image.open(buffer) as decoded:
                result = decoded.convert("RGB")
        elif kind in {"ringing", "overshoot"}:
            result = ImageEnhance.Sharpness(result).enhance(max(0.0, float(stage.get("factor", 2.0))))
        elif kind == "threshold":
            cutoff = max(0, min(255, int(stage.get("cutoff", 192))))
            result = result.convert("L").point(lambda value: 255 if value >= cutoff else 0).convert("RGB")
        elif kind == "halftone":
            step = max(2, int(stage.get("cell_size", 3)))
            gray = result.convert("L")
            output = Image.new("L", result.size, 255)
            output_draw = ImageDraw.Draw(output)
            for y in range(0, result.height, step):
                for x in range(0, result.width, step):
                    value = gray.getpixel((x, y))
                    if value < 224:
                        radius = (255 - value) / 255 * step / 2
                        output_draw.ellipse((x + step / 2 - radius, y + step / 2 - radius, x + step / 2 + radius, y + step / 2 + radius), fill=0)
            result = output.convert("RGB")
        elif kind == "paper_texture":
            result = _apply_noise(result, stage_rng, max(0.0, float(stage.get("sigma", 2.0))))
        elif kind == "faded_ink":
            opacity = max(0.0, min(1.0, float(stage.get("opacity", 0.75))))
            result = Image.blend(Image.new("RGB", result.size, (255, 255, 255)), result, opacity)
        elif kind == "erosion":
            size = max(3, int(stage.get("size", 3)) | 1)
            result = result.filter(ImageFilter.MaxFilter(size))
        elif kind in {"dilation", "ink_bleed"}:
            size = max(3, int(stage.get("size", 3)) | 1)
            result = result.filter(ImageFilter.MinFilter(size))
        elif kind == "stroke_dropout":
            count = max(1, int(stage.get("count", max(1, result.width * result.height // 90000))))
            dropout_draw = ImageDraw.Draw(result)
            for _ in range(count):
                x = stage_rng.randrange(result.width)
                y = stage_rng.randrange(result.height)
                length = max(2, int(stage.get("length", 6)))
                dropout_draw.line((x, y, min(result.width - 1, x + length), y), fill=(255, 255, 255), width=max(1, int(stage.get("width", 1))))
        elif kind == "scan_shadow":
            result = _scan_shadow(result, float(stage.get("strength", 0.18)), str(stage.get("side", "left")))
        elif kind == "clipping":
            amount = max(0, int(stage.get("amount_px", 2)))
            side = str(stage.get("side", "right"))
            result, result_mask, clipping_metadata = _clip_content_edge(
                result,
                result_mask,
                amount,
                side,
            )
            record.update(clipping_metadata)
            record["lossy_visibility_change"] = amount > 0
        elif kind == "downsample":
            scale = max(0.1, min(1.0, float(stage.get("scale", 0.5))))
            small_size = (max(1, round(result.width * scale)), max(1, round(result.height * scale)))
            method_name = str(stage.get("resampler", "bilinear")).casefold()
            methods = {"nearest": Image.Resampling.NEAREST, "bilinear": Image.Resampling.BILINEAR, "bicubic": Image.Resampling.BICUBIC, "lanczos": Image.Resampling.LANCZOS}
            if method_name not in methods:
                raise ValueError(f"Unsupported downsample resampler '{method_name}'")
            method = methods[method_name]
            result = result.resize(small_size, method).resize(image.size, method)
        elif kind == "skew":
            strength = max(0.0, min(0.9, float(stage.get("strength", 0.06))))
            direction = -1.0 if str(stage.get("side", "left")) in {"left", "top"} else 1.0
            angle = direction * strength * 24.0
            shear = math.tan(math.radians(angle))
            center_y = (result.height - 1) / 2.0
            forward = (1.0, shear, -shear * center_y, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)
        elif kind == "perspective":
            strength = max(0.0, min(0.9, float(stage.get("strength", 0.08))))
            shift = min(strength * min(result.size) * 0.25, min(result.size) / 5.0)
            max_x, max_y = float(result.width - 1), float(result.height - 1)
            source = ((0.0, 0.0), (max_x, 0.0), (max_x, max_y), (0.0, max_y))
            destination = (
                (stage_rng.uniform(0.0, shift), stage_rng.uniform(0.0, shift)),
                (max_x - stage_rng.uniform(0.0, shift), stage_rng.uniform(0.0, shift)),
                (max_x - stage_rng.uniform(0.0, shift), max_y - stage_rng.uniform(0.0, shift)),
                (stage_rng.uniform(0.0, shift), max_y - stage_rng.uniform(0.0, shift)),
            )
            forward = _homography(source, destination)
            record["destination_corners"] = [_json_point(point) for point in destination]
        elif kind == "hand_drawn_jitter":
            strength = max(0.0, min(0.9, float(stage.get("strength", 0.06))))
            amplitude = strength * 20.0
            dx = stage_rng.uniform(-amplitude, amplitude)
            dy = stage_rng.uniform(-amplitude, amplitude)
            side = str(stage.get("side", "left"))
            if side == "left":
                dx = -abs(dx)
            elif side == "right":
                dx = abs(dx)
            elif side == "top":
                dy = -abs(dy)
            elif side == "bottom":
                dy = abs(dy)
            angle = math.radians(stage_rng.uniform(-amplitude * 0.18, amplitude * 0.18))
            cosine, sine = math.cos(angle), math.sin(angle)
            center_x, center_y = (result.width - 1) / 2.0, (result.height - 1) / 2.0
            forward = (
                cosine,
                -sine,
                center_x - cosine * center_x + sine * center_y + dx,
                sine,
                cosine,
                center_y - sine * center_x - cosine * center_y + dy,
                0.0,
                0.0,
                1.0,
            )
            record["realized_jitter"] = {
                "translation_px": [round(dx, 6), round(dy, 6)],
                "rotation_degrees": round(math.degrees(angle), 6),
            }
        elif kind == "inconsistent_marker_outlines":
            strength = max(0.0, min(0.9, float(stage.get("strength", 0.06))))
            amplitude = max(1, round(strength * 16.0))
            filter_size = max(3, amplitude * 2 + 1)
            expanded = result_mask.filter(ImageFilter.MaxFilter(filter_size))
            outline_zone = ImageChops.subtract(expanded, result_mask)
            pattern = Image.new("L", result.size, 0)
            pattern_draw = ImageDraw.Draw(pattern)
            probability = max(0.05, min(0.95, 0.35 + strength))
            for y in range(result.height):
                if stage_rng.random() <= probability:
                    pattern_draw.line((0, y, result.width - 1, y), fill=255)
            affected = ImageChops.multiply(outline_zone, pattern)
            darkened = result.filter(ImageFilter.MinFilter(filter_size))
            result = Image.composite(darkened, result, affected)
            result_mask = ImageChops.lighter(result_mask, affected).point(lambda value: 255 if value else 0)
            _expand_marker_annotations(annotation, float(amplitude), "all")
            record["affected_pixel_box"] = list(affected.getbbox()) if affected.getbbox() else None
        elif kind == "line_marker_contact":
            strength = max(0.0, min(0.9, float(stage.get("strength", 0.7))))
            radius = max(1, round(1.0 + strength * 4.0))
            filter_size = radius * 2 + 1
            contact_zone = result_mask.filter(ImageFilter.MaxFilter(filter_size))
            darkened = result.filter(ImageFilter.MinFilter(filter_size))
            blended = Image.blend(result, darkened, strength)
            result = Image.composite(blended, result, contact_zone)
            record["contact_pixel_box"] = list(contact_zone.getbbox()) if contact_zone.getbbox() else None
        elif kind in {"none", "identity"}:
            pass

        if forward is not None:
            result, result_mask, inverse = _warp(result, result_mask, forward)
            _transform_annotations(annotation, forward)
            cumulative_forward = _matrix_multiply(forward, cumulative_forward)
            cumulative_inverse = _matrix_inverse(cumulative_forward)
            record["geometry_preserved"] = False
            record["geometry_transformed"] = True
            record["source_space"] = "synthetic_clean_pixels"
            record["target_space"] = COORDINATE_SPACE
            record["stage_forward_matrix_3x3"] = [round(value, 12) for value in forward]
            record["stage_inverse_matrix_3x3"] = [round(value, 12) for value in inverse]
            record["forward_matrix_3x3"] = [round(value, 12) for value in cumulative_forward]
            record["inverse_matrix_3x3"] = [round(value, 12) for value in cumulative_inverse]
            annotation.setdefault("transforms", []).append(
                {
                    "transform_id": f"degradation-stage-{record['stage']}",
                    "kind": kind,
                    "source_space": "synthetic_clean_pixels",
                    "target_space": COORDINATE_SPACE,
                    "forward_matrix_3x3": record["forward_matrix_3x3"],
                    "inverse_matrix_3x3": record["inverse_matrix_3x3"],
                    "lossy_raster_resampling": True,
                    "geometry_invertible": True,
                }
            )
        records.append(record)
    return result, result_mask, records


def render_scene(scene: dict[str, Any]) -> tuple[Image.Image, dict[str, Any], Image.Image]:
    """Render a resolved scene into image, annotation, and binary marker mask.

    The function has no global random state.  All stochastic degradations use
    ``scene['seed']`` and all returned geometry remains in original canvas pixel
    coordinates.
    """

    if not isinstance(scene, dict):
        raise TypeError("scene must be a dict")
    canvas = scene.get("canvas")
    if not isinstance(canvas, Mapping):
        raise ValueError("scene.canvas must be an object")
    width = _positive_int(canvas.get("width"), "canvas.width")
    height = _positive_int(canvas.get("height"), "canvas.height")
    seed = int(scene.get("seed", 0))
    background = _color(canvas.get("background", canvas.get("background_color")), (255, 255, 255))
    image = Image.new("RGB", (width, height), background)
    draw = ImageDraw.Draw(image)
    marker_mask = Image.new("L", (width, height), 0)
    mask_draw = ImageDraw.Draw(marker_mask)

    requested_font, font_size, font_search_paths = _font_settings(scene)
    resolver = FontResolver(font_search_paths)
    resolved_font = resolver.resolve(requested_font, font_size)
    font = resolved_font.load()

    annotation: dict[str, Any] = {
        "schema_version": scene.get("schema_version", 1),
        "scene_id": str(scene.get("scene_id", "scene")),
        "seed": seed,
        "design": scene.get("design"),
        "coordinate_space": COORDINATE_SPACE,
        "canvas": {"width": width, "height": height, "box": [0.0, 0.0, float(width), float(height)]},
        "panels": [],
        "plots": [],
        "axes": [],
        "anchors": [],
        "ticks": [],
        "texts": [],
        "markers": [],
        "edges": [],
        "legends": [],
        "arrows": [],
        "brackets": [],
        "top_bars": [],
        "dividers": [],
        "phases": [],
        "hard_negatives": [],
        "renderer": {
            "renderer_id": RENDERER_ID,
            "version": RENDERER_VERSION,
            "library": "Pillow",
            "deterministic_seed": seed,
        },
        "fonts": [resolved_font.provenance()],
        "font": resolved_font.provenance(),
        "degradations": [],
        "transforms": [],
        "marker_mask": {"mode": "L", "background": 0, "foreground": 255, "combined": True},
    }

    panels = scene.get("panels", [])
    if not isinstance(panels, list) or not 1 <= len(panels) <= 6:
        raise ValueError("scene.panels must contain between 1 and 6 panels")
    annotation_contract = scene.get("annotations") if isinstance(scene.get("annotations"), Mapping) else {}
    declared_text_regions = annotation_contract.get("text_regions", [])
    if not isinstance(declared_text_regions, list):
        raise ValueError("scene.annotations.text_regions must be a list")
    has_declared_text_regions = bool(declared_text_regions)

    for panel_index, panel_raw in enumerate(panels):
        if not isinstance(panel_raw, Mapping):
            raise ValueError(f"panels[{panel_index}] must be an object")
        panel = panel_raw
        nested_keys = (
            "axes",
            "anchors",
            "ticks",
            "texts",
            "markers",
            "edges",
            "series",
            "legends",
            "arrows",
            "brackets",
            "top_bars",
            "dividers",
            "phases",
        )
        record_starts = {key: len(annotation.get(key, [])) for key in nested_keys}
        panel_id = _identifier(panel, "panel", f"panel-{panel_index + 1}")
        panel_box = _box(panel.get("panel_box", panel.get("box", [0, 0, width, height])), f"{panel_id}.panel_box")
        plot_value = panel.get("plot_box", panel.get("plot"))
        plot_box = _box(plot_value, f"{panel_id}.plot_box") if plot_value is not None else panel_box
        panel_record: dict[str, Any] = {"panel_id": panel_id, "box": _json_box(panel_box), "plot_box": _json_box(plot_box), "coordinate_space": COORDINATE_SPACE}
        annotation["panels"].append(panel_record)
        annotation["plots"].append({"panel_id": panel_id, "box": _json_box(plot_box), "polygon": [_json_point(point) for point in ((plot_box[0], plot_box[1]), (plot_box[2], plot_box[1]), (plot_box[2], plot_box[3]), (plot_box[0], plot_box[3]))], "coordinate_space": COORDINATE_SPACE})

        axes = panel.get("axes") if isinstance(panel.get("axes"), Mapping) else {}
        axis_color = _color(axes.get("color") if isinstance(axes, Mapping) else None)
        axis_width = _positive_int(axes.get("width", 1) if isinstance(axes, Mapping) else 1, f"{panel_id}.axes.width")
        default_axes = {
            "x": ((plot_box[0], plot_box[3]), (plot_box[2], plot_box[3])),
            "y": ((plot_box[0], plot_box[3]), (plot_box[0], plot_box[1])),
        }
        for axis_name in ("x", "y"):
            axis_item = axes.get(axis_name, {}) if isinstance(axes, Mapping) else {}
            if not isinstance(axis_item, Mapping):
                axis_item = {}
            raw_line = axis_item.get("line")
            if isinstance(raw_line, Sequence) and len(raw_line) in {2, 4}:
                start, end = _line_points(raw_line, f"{panel_id}.{axis_name}.line")
            else:
                start, end = default_axes[axis_name]
            visible = bool(axis_item.get("visible", True))
            if visible:
                draw.line((start, end), fill=_color(axis_item.get("color"), axis_color), width=_positive_int(axis_item.get("width", axis_width), f"{panel_id}.{axis_name}.width"))
            annotation["axes"].append({"axis_id": _identifier(axis_item, "axis", f"{panel_id}-axis-{axis_name}"), "panel_id": panel_id, "axis": axis_name, "line": [_json_point(start), _json_point(end)], "visible": visible, "coordinate_space": COORDINATE_SPACE})

        anchors = list(panel.get("calibration_anchors", panel.get("anchors", [])))
        if isinstance(axes, Mapping):
            anchors.extend(axes.get("anchors", []))
        if not anchors:
            x_range = _axis_range(panel, "x", (1.0, 100.0))
            y_range = _axis_range(panel, "y", (0.0, 100.0))
            anchors = [
                {"kind": "session1_y0", "screen": [plot_box[0], plot_box[3]], "graph": [x_range[0], y_range[0]]},
                {"kind": "session1_ymax", "screen": [plot_box[0], plot_box[1]], "graph": [x_range[0], y_range[1]]},
                {"kind": "sessionmax_y0", "screen": [plot_box[2], plot_box[3]], "graph": [x_range[1], y_range[0]]},
            ]
        for anchor_index, anchor in enumerate(anchors):
            if not isinstance(anchor, Mapping):
                continue
            screen = _point(anchor.get("screen", anchor.get("position")), f"{panel_id}.anchor")
            graph = _point(anchor.get("graph", [0, 0]), f"{panel_id}.anchor.graph")
            annotation["anchors"].append({"anchor_id": _identifier(anchor, "anchor", f"{panel_id}-anchor-{anchor_index + 1}"), "panel_id": panel_id, "kind": str(anchor.get("kind", "custom")), "screen": _json_point(screen), "graph": _json_point(graph), "coordinate_space": COORDINATE_SPACE})

        ticks: list[Any] = list(panel.get("ticks", []))
        for axis_name in ("x", "y"):
            axis_item = axes.get(axis_name, {}) if isinstance(axes, Mapping) else {}
            if isinstance(axis_item, Mapping):
                ticks.extend({**tick, "axis": tick.get("axis", axis_name)} for tick in axis_item.get("ticks", []) if isinstance(tick, Mapping))
        for tick_index, tick in enumerate(ticks):
            if not isinstance(tick, Mapping):
                continue
            axis_name = str(tick.get("axis", "x"))
            if "center" in tick or "position" in tick:
                center = _point(tick.get("center", tick.get("position")), f"{panel_id}.tick")
            else:
                value = _float(tick.get("value", 0), f"{panel_id}.tick.value")
                if axis_name == "y":
                    y_min, y_max = _axis_range(panel, "y", (0, 100))
                    center = (plot_box[0], plot_box[3] - (value - y_min) / (y_max - y_min) * (plot_box[3] - plot_box[1]))
                else:
                    x_min, x_max = _axis_range(panel, "x", (1, 100))
                    center = (plot_box[0] + (value - x_min) / (x_max - x_min) * (plot_box[2] - plot_box[0]), plot_box[3])
            length = _float(tick.get("length", 5), f"{panel_id}.tick.length")
            if axis_name == "y":
                segment = ((center[0] - length, center[1]), (center[0] + length, center[1]))
                default_label_position = (center[0] - length - 3, center[1])
                anchor = "ra"
            else:
                segment = ((center[0], center[1] - length), (center[0], center[1] + length))
                default_label_position = (center[0], center[1] + length + 3)
                anchor = "ma"
            supplied_line = tick.get("line")
            if isinstance(supplied_line, Sequence) and len(supplied_line) in {2, 4}:
                segment = _line_points(supplied_line, f"{panel_id}.tick.line")
            visible = bool(tick.get("visible", not tick.get("hidden", False)))
            if visible:
                draw.line(segment, fill=_color(tick.get("color"), axis_color), width=_positive_int(tick.get("width", axis_width), f"{panel_id}.tick.width"))
            tick_id = _identifier(tick, "tick", f"{panel_id}-tick-{tick_index + 1}")
            label = tick.get("label", tick.get("text"))
            label_id: str | None = None
            if label is not None and not has_declared_text_regions:
                label_item = dict(tick.get("label_style", {})) if isinstance(tick.get("label_style"), Mapping) else {}
                label_item.update({"text": str(label), "position": tick.get("label_position", default_label_position), "anchor": tick.get("label_anchor", anchor), "visible": tick.get("label_visible", not tick.get("hidden_label", False)), "partial": tick.get("partial_label", False), "visible_fraction": tick.get("label_visible_fraction", 1.0)})
                text_record = _draw_text(image, draw, label_item, font=font, default_position=default_label_position, default_role=f"{axis_name}_tick", default_id=f"{tick_id}-label")
                annotation["texts"].append(text_record)
                label_id = text_record["text_id"]
            annotation["ticks"].append({"tick_id": tick_id, "panel_id": panel_id, "axis": axis_name, "role": str(tick.get("role", f"{axis_name}_tick")), "center": _json_point(center), "line": [_json_point(point) for point in segment], "value": tick.get("value"), "visible": visible, "label_id": label_id, "coordinate_space": COORDINATE_SPACE})

        phases = panel.get("phases", [])
        if not isinstance(phases, list):
            raise ValueError(f"{panel_id}.phases must be a list")
        for phase_index, phase in enumerate(phases):
            if not isinstance(phase, Mapping):
                continue
            phase_id = _identifier(phase, "phase", f"{panel_id}-phase-{phase_index + 1}")
            x_min = _float(phase.get("screen_x_min", phase.get("x_min", plot_box[0])), f"{phase_id}.x_min")
            x_max = _float(phase.get("screen_x_max", phase.get("x_max", plot_box[2])), f"{phase_id}.x_max")
            phase_record = {"phase_id": phase_id, "panel_id": panel_id, "order": int(phase.get("order", phase_index + 1)), "code": str(phase.get("code", "a" if phase_index == 0 else ("b" if phase_index == 1 else f"phase{phase_index + 1}"))), "normalized_type": str(phase.get("normalized_type", phase.get("type", "baseline" if phase_index == 0 else ("intervention" if phase_index == 1 else "unknown")))), "label_text": phase.get("label_text", phase.get("label")), "screen_x_min": x_min, "screen_x_max": x_max, "coordinate_space": COORDINATE_SPACE}
            annotation["phases"].append(phase_record)
            if phase_record["label_text"] and not phase.get("hidden_label", False) and not has_declared_text_regions:
                label_item = {"text": str(phase_record["label_text"]), "position": phase.get("label_position", [(x_min + x_max) / 2, plot_box[1] - font_size - 4]), "anchor": phase.get("label_anchor", "ma"), "role": "phase_heading", "visible_fraction": phase.get("label_visible_fraction", 1.0)}
                annotation["texts"].append(_draw_text(image, draw, label_item, font=font, default_position=((x_min + x_max) / 2, plot_box[1] - 4), default_role="phase_heading", default_id=f"{phase_id}-label"))

        dividers = list(panel.get("dividers", []))
        if not dividers and len(phases) > 1:
            dividers = [{"x": phase.get("screen_x_min", phase.get("x_min")), "style": phase.get("divider_style", "dotted")} for phase in phases[1:] if isinstance(phase, Mapping)]
        for divider_index, divider in enumerate(dividers):
            if not isinstance(divider, Mapping):
                continue
            supplied_line = divider.get("line")
            if isinstance(supplied_line, Sequence) and len(supplied_line) in {2, 4}:
                start, end = _line_points(supplied_line, f"{panel_id}.divider.line")
                x = start[0]
            else:
                x = _float(divider.get("x", divider.get("screen_x", 0)), f"{panel_id}.divider.x")
                start = _point(divider.get("start", [x, plot_box[1]]), f"{panel_id}.divider.start")
                end = _point(divider.get("end", [x, plot_box[3]]), f"{panel_id}.divider.end")
            style = str(divider.get("style", "dotted"))
            drawn = _line(draw, start, end, fill=_color(divider.get("color")), width=_positive_int(divider.get("width", 1), f"{panel_id}.divider.width"), style=style)
            annotation["dividers"].append({"divider_id": _identifier(divider, "divider", f"{panel_id}-divider-{divider_index + 1}"), "panel_id": panel_id, "x": x, "line": [_json_point(start), _json_point(end)], "style": style, "drawn": drawn, "coordinate_space": COORDINATE_SPACE})

        series_source = panel.get("series", [])
        if not isinstance(series_source, list):
            raise ValueError(f"{panel_id}.series must be a list")
        series_items: list[dict[str, Any]] = [dict(series) for series in series_source if isinstance(series, Mapping)]
        orphan_points = panel.get("points", [])
        if isinstance(orphan_points, list) and orphan_points:
            grouped: dict[str, list[Mapping[str, Any]]] = {}
            for point in orphan_points:
                if isinstance(point, Mapping):
                    grouped.setdefault(str(point.get("series_id", "series-1")), []).append(point)
            existing_ids: set[str] = set()
            for index, series in enumerate(series_items):
                series_id = _identifier(series, "series", f"{panel_id}-series-{index + 1}")
                existing_ids.add(series_id)
                series["points"] = list(series.get("points", [])) + list(grouped.get(series_id, []))
            series_items.extend(
                {"series_id": series_id, "points": points}
                for series_id, points in grouped.items()
                if series_id not in existing_ids
            )

        connections_by_series: dict[str, list[Mapping[str, Any]]] = {}
        panel_connections = panel.get("connections", [])
        if isinstance(panel_connections, list):
            for connection in panel_connections:
                if isinstance(connection, Mapping):
                    connections_by_series.setdefault(str(connection.get("series_id", "")), []).append(connection)
        for index, series in enumerate(series_items):
            series_id = _identifier(series, "series", f"{panel_id}-series-{index + 1}")
            if "edges" not in series and series_id in connections_by_series:
                series["edges"] = connections_by_series[series_id]

        prepared_series: list[tuple[Mapping[str, Any], str, list[tuple[Mapping[str, Any], str, tuple[float, float]]]]] = []
        for series_index, series in enumerate(series_items):
            if not isinstance(series, Mapping):
                continue
            series_id = _identifier(series, "series", f"{panel_id}-series-{series_index + 1}")
            raw_points = series.get("points", [])
            if not isinstance(raw_points, list):
                raise ValueError(f"{series_id}.points must be a list")
            resolved_points: list[tuple[Mapping[str, Any], str, tuple[float, float]]] = []
            for point_index, point in enumerate(raw_points):
                if point is None or not isinstance(point, Mapping) or point.get("missing", False) or point.get("visible") is False:
                    continue
                point_id = _identifier(point, "point", _identifier(point, "marker", f"{series_id}-point-{point_index + 1}"))
                resolved_points.append((point, point_id, _point_center(point, panel, plot_box)))
            prepared_series.append((series, series_id, resolved_points))

        # Edges precede marker glyphs so centers remain visually dominant.
        for series, series_id, resolved_points in prepared_series:
            by_id = {point_id: (point, center) for point, point_id, center in resolved_points}
            explicit_edges = series.get("edges")
            edge_specs: list[Mapping[str, Any]] = []
            if isinstance(explicit_edges, list):
                edge_specs = [edge for edge in explicit_edges if isinstance(edge, Mapping)]
            else:
                for left_index in range(len(resolved_points) - 1):
                    left_point, left_id, _ = resolved_points[left_index]
                    right_point, right_id, _ = resolved_points[left_index + 1]
                    if left_point.get("break_after", False) or right_point.get("break_before", False) or left_point.get("connect_next") is False:
                        continue
                    edge_specs.append({"from_marker_id": left_id, "to_marker_id": right_id})
            default_style = str(series.get("line_style", series.get("connection_style", "solid"))).casefold().replace("-", "_")
            for edge_index, edge in enumerate(edge_specs):
                from_id = str(edge.get("from_marker_id", edge.get("from_point_id", edge.get("from", ""))))
                to_id = str(edge.get("to_marker_id", edge.get("to_point_id", edge.get("to", ""))))
                if from_id not in by_id or to_id not in by_id:
                    continue
                start, end = by_id[from_id][1], by_id[to_id][1]
                style = str(edge.get("style", default_style)).casefold().replace("-", "_")
                if edge.get("visible") is False:
                    style = "missing"
                if style not in LINE_STYLES and style != "dotted":
                    raise ValueError(f"Unsupported connection style '{style}'")
                drawn = _line(draw, start, end, fill=_color(edge.get("color", series.get("stroke", series.get("color")))), width=_positive_int(edge.get("width", series.get("line_width", 1)), f"{series_id}.edge.width"), style=style)
                annotation["edges"].append({"edge_id": _identifier(edge, "edge", f"{series_id}-edge-{edge_index + 1}"), "panel_id": panel_id, "series_id": series_id, "from_marker_id": from_id, "to_marker_id": to_id, "line": [_json_point(start), _json_point(end)], "style": style, "drawn": drawn, "coordinate_space": COORDINATE_SPACE})

        for series, series_id, resolved_points in prepared_series:
            default_shape = str(series.get("shape", "circle")).casefold().replace("-", "_")
            default_fill = str(series.get("fill", "filled")).casefold()
            point_ids: list[str] = []
            for point, point_id, center in resolved_points:
                shape = str(point.get("shape", default_shape)).casefold().replace("-", "_")
                fill_state = str(point.get("fill", default_fill)).casefold()
                radius = _float(point.get("radius", series.get("radius", series.get("marker_radius", 4))), f"{point_id}.radius")
                stroke_width = _positive_int(point.get("stroke_width", series.get("stroke_width", 1)), f"{point_id}.stroke_width")
                _draw_marker(draw, mask_draw, center, radius, shape, fill_state, _color(point.get("color", series.get("stroke", series.get("color")))), stroke_width)
                marker_id = str(point.get("marker_id", point_id))
                point_ids.append(point_id)
                graph = point.get("graph")
                graph_point = _point(graph, f"{point_id}.graph") if graph is not None else None
                x_value = point.get("x_value", point.get("session", point.get("x", graph_point[0] if graph_point else None)))
                y_value = point.get("y_value", point.get("value", point.get("y", graph_point[1] if graph_point else None)))
                annotation["markers"].append({"marker_id": marker_id, "point_id": point_id, "panel_id": panel_id, "series_id": series_id, "center": _json_point(center), "radius": radius, "box": _json_box((center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius)), "shape": shape, "fill": fill_state, "x_value": x_value, "y_value": y_value, "graph": _json_point(graph_point) if graph_point else None, "observation_index": point.get("observation_index"), "printed_x_value": point.get("printed_x_value"), "estimated_x_value": point.get("estimated_x_value"), "phase_id": point.get("phase_id"), "coordinate_space": COORDINATE_SPACE})
            annotation.setdefault("series", []).append({"series_id": series_id, "panel_id": panel_id, "shape": default_shape, "fill": default_fill, "display_name": series.get("display_name", series.get("label", series_id)), "semantic_role": series.get("semantic_role", "unknown"), "point_ids": point_ids})

        text_items = list(panel.get("text", panel.get("texts", []))) if not has_declared_text_regions else []
        if not has_declared_text_regions:
            for role_key, role in (("participant", "participant"), ("axis_titles", "axis_title")):
                value = panel.get(role_key)
                if isinstance(value, str):
                    text_items.append({"text": value, "role": role})
                elif isinstance(value, list):
                    text_items.extend(value)
        for text_index, text_item in enumerate(text_items):
            if not isinstance(text_item, Mapping):
                continue
            annotation["texts"].append(_draw_text(image, draw, text_item, font=font, default_position=(plot_box[0], panel_box[1]), default_role="other", default_id=f"{panel_id}-text-{text_index + 1}"))

        legends = panel.get("legends", panel.get("legend", []))
        if isinstance(legends, Mapping):
            legends = [legends]
        if isinstance(legends, list):
            for legend_index, legend in enumerate(legends):
                if not isinstance(legend, Mapping):
                    continue
                legend_id = _identifier(legend, "legend", f"{panel_id}-legend-{legend_index + 1}")
                visible = bool(legend.get("visible", True)) and legend.get("box") is not None
                default_legend_box = [plot_box[2] - 100, plot_box[1] + 8, 92, 28]
                legend_box = _box(legend.get("box", default_legend_box), f"{legend_id}.box") if visible else None
                if legend_box is not None:
                    draw.rectangle(legend_box, outline=_color(legend.get("color")), width=_positive_int(legend.get("width", 1), f"{legend_id}.width"))
                entries = legend.get("entries", [])
                entry_records: list[dict[str, Any]] = []
                series_lookup = {str(series.get("series_id")): series for series in series_items}
                for entry_index, entry in enumerate(entries if isinstance(entries, list) else []):
                    if not isinstance(entry, Mapping) or not visible or legend_box is None:
                        continue
                    glyph_value = entry.get("glyph_box")
                    if glyph_value is not None:
                        glyph_box = _box(glyph_value, f"{legend_id}.entry.glyph_box")
                        center = ((glyph_box[0] + glyph_box[2]) / 2, (glyph_box[1] + glyph_box[3]) / 2)
                        radius = min(glyph_box[2] - glyph_box[0], glyph_box[3] - glyph_box[1]) / 2
                    else:
                        center = _point(entry.get("glyph_center", [legend_box[0] + 10, legend_box[1] + 12 + entry_index * (font_size + 5)]), f"{legend_id}.entry.center")
                        radius = _float(entry.get("radius", 4), f"{legend_id}.entry.radius")
                        glyph_box = (center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius)
                    series_style = series_lookup.get(str(entry.get("series_id")), {})
                    shape = str(entry.get("shape", series_style.get("shape", "circle")))
                    fill_state = str(entry.get("fill", series_style.get("fill", "filled")))
                    ignored_mask = Image.new("L", image.size, 0)
                    _draw_marker(draw, ImageDraw.Draw(ignored_mask), center, radius, shape, fill_state, _color(entry.get("color", series_style.get("stroke"))), _positive_int(entry.get("stroke_width", 1), f"{legend_id}.entry.stroke_width"))
                    text_id = None
                    if not has_declared_text_regions:
                        text_box_value = entry.get("text_box")
                        text_box = _box(text_box_value, f"{legend_id}.entry.text_box") if text_box_value is not None else None
                        position = (text_box[0], text_box[1]) if text_box else (center[0] + radius + 5, center[1])
                        text_item = {"text": str(entry.get("text", entry.get("label", ""))), "position": position, "anchor": "lt" if text_box else "lm", "role": "legend_text"}
                        text_record = _draw_text(image, draw, text_item, font=font, default_position=position, default_role="legend_text", default_id=f"{legend_id}-entry-{entry_index + 1}-text")
                        annotation["texts"].append(text_record)
                        text_id = text_record["text_id"]
                    entry_records.append({"series_id": entry.get("series_id"), "glyph_center": _json_point(center), "glyph_box": _json_box(glyph_box), "shape": shape, "fill": fill_state, "text_id": text_id, "text": entry.get("text")})
                annotation["legends"].append({"legend_id": legend_id, "panel_id": panel_id, "box": _json_box(legend_box) if legend_box else None, "visible": visible, "placement": str(legend.get("position", legend.get("placement", "inside"))), "entries": entry_records, "coordinate_space": COORDINATE_SPACE})

        for arrow_index, arrow in enumerate(panel.get("arrows", [])):
            if isinstance(arrow, Mapping):
                record = _draw_arrow(draw, arrow, f"{panel_id}-arrow-{arrow_index + 1}")
                record["panel_id"] = panel_id
                annotation["arrows"].append(record)
        for bracket_index, bracket in enumerate(panel.get("brackets", [])):
            if isinstance(bracket, Mapping):
                record = _draw_bracket(draw, bracket, f"{panel_id}-bracket-{bracket_index + 1}")
                record["panel_id"] = panel_id
                annotation["brackets"].append(record)
        top_bars = panel.get("top_bars", panel.get("condition_bars", []))
        for bar_index, bar in enumerate(top_bars if isinstance(top_bars, list) else []):
            if not isinstance(bar, Mapping):
                continue
            supplied_line = bar.get("line")
            if isinstance(supplied_line, Sequence) and len(supplied_line) in {2, 4}:
                start, end = _line_points(supplied_line, f"{panel_id}.top_bar.line")
            else:
                start = _point(bar.get("start", [plot_box[0], plot_box[1] - 10]), f"{panel_id}.top_bar.start")
                end = _point(bar.get("end", [plot_box[2], plot_box[1] - 10]), f"{panel_id}.top_bar.end")
            draw.line((start, end), fill=_color(bar.get("color")), width=_positive_int(bar.get("width", 2), f"{panel_id}.top_bar.width"))
            bar_id = str(bar.get("bar_id", _identifier(bar, "top_bar", f"{panel_id}-top-bar-{bar_index + 1}")))
            label_id = None
            if bar.get("label") is not None and not has_declared_text_regions:
                text_record = _draw_text(image, draw, {"text": str(bar["label"]), "position": bar.get("label_position", [(start[0] + end[0]) / 2, start[1] - 2]), "anchor": "mb", "role": "condition_label"}, font=font, default_position=((start[0] + end[0]) / 2, start[1] - 2), default_role="condition_label", default_id=f"{bar_id}-label")
                annotation["texts"].append(text_record)
                label_id = text_record["text_id"]
            annotation["top_bars"].append({"top_bar_id": bar_id, "panel_id": panel_id, "line": [_json_point(start), _json_point(end)], "label_id": label_id, "coordinate_space": COORDINATE_SPACE})

        # Text-region boxes are the scene's authoritative label geometry.
        # Render each once and keep its supplied box and ID unchanged.
        for region in declared_text_regions:
            if not isinstance(region, Mapping) or str(region.get("panel_id")) != panel_id:
                continue
            region_box = _box(region.get("box"), f"{panel_id}.text_region.box")
            text_item = {
                "text": str(region.get("text", "")),
                "position": [region_box[0], region_box[1]],
                "anchor": "lt",
                "role": str(region.get("role", "other")),
                "visible": bool(region.get("visible", True)),
                "region_id": region.get("region_id"),
                "text_id": region.get("region_id"),
            }
            text_record = _draw_text(
                image,
                draw,
                text_item,
                font=font,
                default_position=(region_box[0], region_box[1]),
                default_role=str(region.get("role", "other")),
                default_id=str(region.get("region_id", f"{panel_id}-region")),
            )
            annotation["texts"].append(text_record)

        # Keep panel annotations self-contained.  Top-level lists remain empty
        # to avoid double-counting consumers that aggregate both scopes.
        for key in nested_keys:
            records = annotation.get(key, [])[record_starts[key] :]
            for record in records:
                if isinstance(record, dict):
                    record.setdefault("panel_id", panel_id)
            panel_record[key] = records
            del annotation[key][record_starts[key] :]

    for kind, draw_function, output_key in (("arrows", _draw_arrow, "arrows"), ("brackets", _draw_bracket, "brackets")):
        for index, item in enumerate(_flatten_annotations(scene, kind)):
            annotation[output_key].append(draw_function(draw, item, f"scene-{kind.rstrip('s')}-{index + 1}"))
    for index, item in enumerate(_flatten_annotations(scene, "text")):
        annotation["texts"].append(_draw_text(image, draw, item, font=font, default_position=(0, 0), default_role="other", default_id=f"scene-text-{index + 1}"))

    hard_negatives = scene.get("hard_negatives", [])
    if not isinstance(hard_negatives, list):
        raise ValueError("scene.hard_negatives must be a list")
    hard_negative_mask = Image.new("L", image.size, 0)
    reserved_negative_mask = Image.new("L", image.size, 0)
    placement_rng = random.Random(seed ^ 0x484152444E4547)
    panel_safe_boxes: dict[str, tuple[float, float, float, float]] = {}
    for panel in panels:
        if not isinstance(panel, Mapping) or panel.get("panel_id") is None:
            continue
        safe_value = panel.get(
            "panel_box",
            panel.get("box", [0.0, 0.0, float(image.width), float(image.height)]),
        )
        panel_safe_boxes[str(panel["panel_id"])] = _box(safe_value, "panel.box")
    marker_clearance_px = 4
    for stage in scene.get("degradations", []):
        if not isinstance(stage, Mapping) or stage.get("kind") != "inconsistent_marker_outlines":
            continue
        parameters = stage.get("parameters", {})
        if isinstance(parameters, Mapping):
            expansion = max(1, round(float(parameters.get("strength", 0.06)) * 16.0))
            marker_clearance_px = max(marker_clearance_px, expansion + 2)
    for index, item in enumerate(hard_negatives):
        if isinstance(item, Mapping):
            safe_box = panel_safe_boxes.get(
                str(item.get("panel_id")),
                (0.0, 0.0, float(image.width), float(image.height)),
            )
            record, footprint = _draw_hard_negative(
                image,
                draw,
                item,
                font,
                f"hard-negative-{index + 1}",
                marker_mask,
                reserved_negative_mask,
                safe_box,
                placement_rng,
                marker_clearance_px,
            )
            annotation["hard_negatives"].append(record)
            hard_negative_mask = ImageChops.lighter(hard_negative_mask, footprint)
            reserved_negative_mask = ImageChops.lighter(
                reserved_negative_mask,
                footprint.filter(ImageFilter.MaxFilter(3)),
            )

    image, marker_mask, degradation_records = _degrade(
        image,
        marker_mask,
        annotation,
        scene.get("degradations", []),
        seed,
    )
    annotation["degradations"] = degradation_records
    if annotation["transforms"]:
        hard_negative_mask = _warp_mask(
            hard_negative_mask,
            annotation["transforms"][-1]["forward_matrix_3x3"],
        )
    if ImageChops.multiply(hard_negative_mask, marker_mask).getbbox() is not None:
        raise ValueError(
            "Rendered hard-negative pixels intersect the final marker mask"
        )
    for index, record in enumerate(annotation["hard_negatives"]):
        _validate_rendered_hard_negative(
            record,
            image.width,
            image.height,
            f"hard-negative-{index + 1}",
        )
    return image, annotation, marker_mask


__all__ = [
    "HARD_NEGATIVE_KINDS",
    "LINE_STYLES",
    "MARKER_FILLS",
    "MARKER_SHAPES",
    "production_resize_dimensions",
    "render_scene",
]
