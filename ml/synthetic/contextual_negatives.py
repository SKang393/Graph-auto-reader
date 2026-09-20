# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Opt-in train/dev renderer whose negative labels refer to graph artifacts.

The historical renderer paints isolated circles, triangles and letters under
negative names, without the visible context implied by those names. This
profile retains the complete graph but replaces those injected glyphs with
labels for its already-rendered legends, ticks, text and other structures.
Historical source documents, default rendering and evaluation stay unchanged.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import math
from typing import Any, Mapping
from uuid import NAMESPACE_URL, uuid5

import numpy as np
from PIL import Image

from .renderer import render_scene
from .schema import validate_scene

VERSION = "synthetic-contextual-negatives-v1"
MARKER_CLEARANCE_PIXELS = 4  # Same clearance as historical injected glyphs.


def _box(points, padding=0.0):
    xs, ys = zip(*points)
    left, top = min(xs) - padding, min(ys) - padding
    return [left, top, max(xs) + padding - left, max(ys) + padding - top]


def _overlaps(first, second, margin=0):
    x, y, width, height = first
    a, b, w, h = second
    return x <= a + w + margin and a - margin <= x + width and y <= b + h + margin and b - margin <= y + height


def _records(annotation, category):
    return [*annotation.get(category, []), *(r for p in annotation["panels"] for r in p.get(category, []))]


def _intersection(first, second):
    """Unique closed-segment intersection, excluding parallel/collinear lines."""
    p, q = first[0], second[0]
    r = [first[1][i] - p[i] for i in range(2)]
    s = [second[1][i] - q[i] for i in range(2)]
    cross = lambda a, b: a[0] * b[1] - a[1] * b[0]
    denominator = cross(r, s)
    if abs(denominator) < 1e-10:
        return None
    delta = [q[i] - p[i] for i in range(2)]
    t, u = cross(delta, s) / denominator, cross(delta, r) / denominator
    return [p[i] + t * r[i] for i in range(2)] if 0 <= t <= 1 and 0 <= u <= 1 else None


def _candidates(annotation):
    """Yield a center, extent and reference to actual rendered graph context."""
    for panel in annotation["panels"]:
        panel_id = panel["panel_id"]

        def candidate(kind, reference, center, box, context):
            return {"kind": kind, "panel_id": panel_id, "source_reference": reference,
                    "geometry": {"center": list(center), "box": list(box)}, "context": context}

        for text in panel["texts"]:
            box = text.get("rendered_pixel_box")
            if text.get("visible", True) and text.get("text", "").strip() and box:
                yield candidate("text", text["text_id"], [box[0] + box[2] / 2, box[1] + box[3] / 2], box,
                                {"category": "texts", "text_id": text["text_id"], "role": text["role"]})

        for legend in panel["legends"]:
            if not legend.get("visible", True):
                continue
            for index, entry in enumerate(legend["entries"]):
                # A named native legend entry, not an independently painted circle.
                if not str(entry.get("text") or "").strip():
                    continue
                text_ids = [t["text_id"] for t in panel["texts"] if t.get("role") == "legend_text"
                            and t.get("text") == entry["text"] and t.get("visible", True)
                            and t.get("rendered_pixel_box") and _overlaps(t["rendered_pixel_box"], legend["box"])]
                if not text_ids:
                    continue
                yield candidate("legend_symbol", f"{legend['legend_id']}:{index}", entry["glyph_center"], entry["glyph_box"],
                                {"category": "legends", "legend_id": legend["legend_id"], "text_ids": text_ids})

        for tick in panel["ticks"]:
            axes = [a for a in panel["axes"] if a["axis"] == tick["axis"] and a.get("visible", True)
                    and _intersection(tick["line"], a["line"]) is not None]
            if tick.get("visible", True) and axes:
                yield candidate("tick_mark", tick["tick_id"], tick["center"], _box(tick["line"], 1),
                                {"category": "ticks", "tick_id": tick["tick_id"], "axis_id": axes[0]["axis_id"]})

        for arrow in panel["arrows"]:
            polygon = arrow["arrowhead_polygon"]
            if math.dist(arrow["start"], arrow["tip"]) <= 0 or not polygon:
                continue
            center = [sum(p[i] for p in polygon) / len(polygon) for i in range(2)]
            context = {"category": "arrows", "arrow_id": arrow["arrow_id"], "shaft": arrow["line"]}
            yield candidate("arrowhead", arrow["arrow_id"], center, _box(polygon), context)
            yield candidate("line_endpoint", arrow["arrow_id"] + ":start", arrow["start"], _box([arrow["start"]], 2), context)

        for bracket in panel["brackets"]:
            for index, point in enumerate(bracket["polyline"]):
                yield candidate("bracket", f"{bracket['bracket_id']}:{index}", point, _box([point], 2),
                                {"category": "brackets", "bracket_id": bracket["bracket_id"], "polyline": bracket["polyline"]})

        for divider in panel["dividers"]:
            if not divider.get("drawn", True):
                continue
            center = [(divider["line"][0][i] + divider["line"][1][i]) / 2 for i in range(2)]
            yield candidate("phase_divider", divider["divider_id"], center, _box([center], 2),
                            {"category": "dividers", "divider_id": divider["divider_id"], "line": divider["line"]})
            for edge in panel["edges"]:
                crossing = _intersection(divider["line"], edge["line"]) if edge.get("drawn", True) else None
                if crossing is not None:
                    yield candidate("phase_line_intersection", f"{divider['divider_id']}:{edge['edge_id']}", crossing,
                                    _box([crossing], 2), {"category": "dividers", "divider_id": divider["divider_id"], "edge_id": edge["edge_id"]})


def render_contextual_scene(scene: Mapping[str, Any], *, split: str, source_profile: str = "historical") -> tuple[Image.Image, dict[str, Any], Image.Image]:
    """Render a separate owned train/dev profile, with every omission audited.

    The unchanged source scene plus this profile ID are required for replay.
    Negative sampling is descriptive annotation only; no ink is added, moved
    or erased from the native graph, and no scientific truth is changed.
    """
    if split not in ("train", "dev"):
        raise ValueError("Contextual negative profile is restricted to train/dev")
    if source_profile not in ("historical", "visible-content-v3"):
        raise ValueError("Unknown contextual negative source profile")
    validate_scene(scene)
    provenance = scene["provenance"]
    if provenance["private_data"] or provenance["source"] != "procedural" or provenance["external_assets"]:
        raise ValueError("Contextual negative profile accepts project-owned procedural scenes only")
    if provenance["license"] != "Apache-2.0":
        raise ValueError("Contextual negative profile requires the owned Apache-2.0 scene license")

    scratch = deepcopy(dict(scene))
    requests = scratch["hard_negatives"]
    # The input still obeys the historical schema, including minItems=1. Only
    # this explicit renderer's internal drawing input omits orphan requests.
    scratch["hard_negatives"] = []
    if source_profile == "historical":
        image, annotation, marker_mask = render_scene(scratch)
    else:
        from .runtime_graph_visible_content_v3 import _render_physical_source_from_clean, _validated_stages
        stages = _validated_stages(scene)
        scratch["degradations"] = []
        clean_image, clean_annotation, clean_mask = render_scene(scratch)
        rendered = _render_physical_source_from_clean(scene, clean_image, clean_annotation, clean_mask, stages)
        image, annotation, marker_mask = rendered.image, rendered.annotation, rendered.marker_mask
    marker_boxes = [m["box"] for m in _records(annotation, "markers")]
    ink = np.any(np.asarray(image.convert("RGB")) < 255, axis=2)
    marker_pixels = np.asarray(marker_mask)
    omitted = []
    for item in _candidates(annotation):
        x, y, width, height = item["geometry"]["box"]
        center = item["geometry"]["center"]
        reason = None
        if not (0 <= x < x + width <= image.width and 0 <= y < y + height <= image.height):
            reason = "clipped_context_extent"
        elif not (x <= center[0] <= x + width and y <= center[1] <= y + height):
            reason = "center_outside_context"
        elif any(_overlaps(item["geometry"]["box"], box, MARKER_CLEARANCE_PIXELS) for box in marker_boxes):
            reason = "context_overlaps_true_marker_clearance"
        elif marker_pixels[math.floor(y):math.ceil(y + height), math.floor(x):math.ceil(x + width)].any():
            reason = "context_overlaps_rendered_marker_pixels"
        elif not ink[math.floor(y):math.ceil(y + height), math.floor(x):math.ceil(x + width)].any():
            reason = "no_visible_ink_in_context"
        if reason:
            omitted.append({"kind": item["kind"], "source_reference": item["source_reference"], "reason": reason})
            continue
        item.update(hard_negative_id=str(uuid5(NAMESPACE_URL, f"{VERSION}:{scene['scene_id']}:{item['kind']}:{item['source_reference']}")),
                    coordinate_space="original_pixels", excluded_from_marker_mask=True)
        annotation["hard_negatives"].append(item)
    annotation["contextual_negative_profile"] = {
        "id": VERSION, "split": split, "source_profile": source_profile, "source_scene_id": scene["scene_id"],
        "source_scene_canonical_sha256": hashlib.sha256(json.dumps(scene, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
        "replaced_orphan_requests": requests, "omitted_native_negative_candidates": omitted,
        "marker_clearance_pixels": MARKER_CLEARANCE_PIXELS,
        "historical_default_rendering_unchanged": True,
    }
    return image, annotation, marker_mask
