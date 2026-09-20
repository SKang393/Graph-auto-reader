# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Opt-in placement repair for synthetic arrow labels that cover legends.

Historical scenes and renderers remain unchanged. This transform uses declared
synthetic geometry and clean rendered occupancy, never model scores or private
images. It preserves every label and scientific observation, recording cases
where no safe placement can be found instead of dropping them.
"""
from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
import math
from typing import Any, Mapping
from uuid import NAMESPACE_URL, uuid5

import numpy as np

from .fonts import FontResolver
from .renderer import _font_settings, render_scene
from .schema import validate_scene

VERSION = "synthetic-arrow-label-clearance-v1"
CLEARANCE = 4


@dataclass(frozen=True)
class AnnotationClearanceResult:
    scene: dict[str, Any]
    changes: tuple[dict[str, Any], ...]
    unresolved: tuple[dict[str, str], ...]
    version: str = VERSION


def _ltrb(box):
    x, y, width, height = box
    return (x, y, x + width, y + height)


def _overlap(a, b, margin=0):
    return (a[0] < b[2] + margin and b[0] - margin < a[2] and
            a[1] < b[3] + margin and b[1] - margin < a[3])


def _line_hits(start, end, box):
    """Closed segment/rectangle intersection; zero-length segments are supported."""
    low, high = 0.0, 1.0
    for axis in (0, 1):
        delta = end[axis] - start[axis]
        if delta == 0:
            if start[axis] < box[axis] or start[axis] > box[axis + 2]:
                return False
        else:
            enter = (box[axis] - start[axis]) / delta
            leave = (box[axis + 2] - start[axis]) / delta
            low, high = max(low, min(enter, leave)), min(high, max(enter, leave))
            if low > high:
                return False
    return True


def separate_arrow_labels(scene: Mapping[str, Any]) -> AnnotationClearanceResult:
    """Return a new scene and explicit repairs, without changing a frozen input."""
    validate_scene(scene)
    if scene["provenance"]["private_data"] or scene["provenance"]["source"] != "procedural":
        raise ValueError("Annotation clearance accepts project-owned procedural scenes only")
    result = deepcopy(dict(scene))
    requested, size, paths = _font_settings(result)
    font = FontResolver(paths).resolve(requested, size).load()
    changes, unresolved = [], []
    regions = result["annotations"]["text_regions"]
    panels = {p["panel_id"]: p for p in result["panels"]}
    for region in regions:
        if region["role"] != "annotation" or not region.get("visible", True):
            continue
        panel = panels[region["panel_id"]]
        legend = panel["legend"]
        if not legend.get("visible") or not legend.get("box"):
            continue
        measured = font.getbbox(region["text"], anchor="lt")
        old_box = region["box"][:]
        footprint = (old_box[0] + measured[0], old_box[1] + measured[1],
                     old_box[0] + measured[2], old_box[1] + measured[3])
        frame = _ltrb(legend["box"])
        if not _overlap(footprint, frame, CLEARANCE):
            continue
        arrows = [a for a in panel["arrows"] if a.get("label") == region["text"]]
        if len(arrows) != 1:
            unresolved.append({"region_id": region["region_id"], "reason": "arrow_binding_not_unique"})
            continue
        arrow = arrows[0]
        if _line_hits(arrow["tip"], arrow["tip"], frame):
            unresolved.append({"region_id": region["region_id"], "reason": "arrow_target_inside_legend"})
            continue
        # Render only a clean occupancy reference. Remove the relocating label and
        # its shaft; all other text, glyphs, axes and observations remain obstacles.
        scratch = deepcopy(result)
        scratch["degradations"] = []
        next(r for r in scratch["annotations"]["text_regions"] if r["region_id"] == region["region_id"])["visible"] = False
        scratch_panel = next(p for p in scratch["panels"] if p["panel_id"] == panel["panel_id"])
        scratch_panel["arrows"] = [a for a in scratch_panel["arrows"] if a["arrow_id"] != arrow["arrow_id"]]
        image, annotation, _ = render_scene(scratch)
        occupied = np.any(np.asarray(image.convert("RGB")) != 255, axis=2).astype(np.int64)
        integral = np.pad(occupied.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
        blockers = [_ltrb(t["rendered_pixel_box"]) for p in annotation["panels"] for t in p["texts"]
                    if t.get("rendered_pixel_box") is not None]
        blockers.append(frame)
        for point in panel["points"]:
            if point["center"] == arrow["tip"]:
                continue
            x, y = point["center"]
            radius = point["radius"] + CLEARANCE
            blockers.append((x - radius, y - radius, x + radius, y + radius))
        width, height = measured[2] - measured[0], measured[3] - measured[1]
        left, top, right, bottom = _ltrb(panel["box"])
        step = max(4, math.ceil(height / 2))
        candidates = ((x, y) for y in range(math.ceil(top + CLEARANCE), math.floor(bottom - height - CLEARANCE), step)
                      for x in range(math.ceil(left + CLEARANCE), math.floor(right - width - CLEARANCE), step))
        chosen = None
        for x, y in sorted(candidates, key=lambda pos: ((pos[0] - footprint[0]) ** 2 + (pos[1] - footprint[1]) ** 2, pos[1], pos[0])):
            bounds = (x, y, x + width, y + height)
            if _overlap(bounds, frame, CLEARANCE):
                continue
            x0, y0 = max(0, x - CLEARANCE), max(0, y - CLEARANCE)
            x1, y1 = min(image.width, math.ceil(x + width + CLEARANCE)), min(image.height, math.ceil(y + height + CLEARANCE))
            if integral[y1, x1] - integral[y0, x1] - integral[y1, x0] + integral[y0, x0]:
                continue
            start = (x + width / 2, y + height + 2)
            if any(_line_hits(start, arrow["tip"], b) for b in blockers):
                continue
            chosen = (x, y, start)
            break
        if chosen is None:
            unresolved.append({"region_id": region["region_id"], "reason": "no_clear_label_and_arrow_path"})
            continue
        x, y, start = chosen
        old_id, old_arrow_id, old_start = region["region_id"], arrow["arrow_id"], arrow["start"][:]
        new_box = [float(x - measured[0]), float(y - measured[1]), max(old_box[2], float(width)), max(old_box[3], float(height))]
        region["box"] = new_box
        region["region_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{old_id}:{new_box}"))
        arrow["start"] = list(start)
        arrow["arrow_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{old_arrow_id}:{start}"))
        for artifact in result["annotations"]["artifacts"]:
            if (artifact["panel_id"] == panel["panel_id"] and artifact["kind"] == "text" and
                    artifact["role"] == region["role"] and artifact["geometry"] == {"kind": "box", "coordinates": old_box}):
                artifact["geometry"]["coordinates"] = new_box[:]
                artifact["artifact_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{artifact['artifact_id']}:{new_box}"))
        changes.append({"old_region_id": old_id, "region_id": region["region_id"], "old_box": old_box, "box": new_box,
                        "old_arrow_start": old_start, "arrow_start": list(start), "arrow_tip": arrow["tip"][:]})
    if changes:
        result["scene_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{scene['scene_id']}:{changes}"))
    validate_scene(result)
    return AnnotationClearanceResult(result, tuple(changes), tuple(unresolved))
