# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Opt-in clearance for peripheral labels, without moving scientific geometry."""
from __future__ import annotations

from copy import deepcopy
import math
from typing import Any, Mapping
from uuid import NAMESPACE_URL, uuid5

import numpy as np

from .annotation_clearance import AnnotationClearanceResult, CLEARANCE, _ltrb
from .fonts import FontResolver
from .renderer import _font_settings, render_scene
from .schema import validate_scene

VERSION = "synthetic-peripheral-text-clearance-v1"


def separate_peripheral_labels(scene: Mapping[str, Any]) -> AnnotationClearanceResult:
    """Keep captions in their semantic area and retain unresolved layouts."""
    validate_scene(scene)
    if scene["provenance"]["private_data"] or scene["provenance"]["source"] != "procedural":
        raise ValueError("Text clearance accepts project-owned procedural scenes only")
    result = deepcopy(dict(scene))
    requested, size, paths = _font_settings(result)
    font = FontResolver(paths).resolve(requested, size).load()
    changes, unresolved = [], []
    panels = {p["panel_id"]: p for p in result["panels"]}
    for region in result["annotations"]["text_regions"]:
        role = region["role"]
        if role not in ("participant", "axis_title", "condition_label") or not region.get("visible", True):
            continue
        panel = panels[region["panel_id"]]
        old_box = region["box"][:]
        measured = font.getbbox(region["text"], anchor="lt")
        footprint = (old_box[0] + measured[0], old_box[1] + measured[1],
                     old_box[0] + measured[2], old_box[1] + measured[3])
        width, height = footprint[2] - footprint[0], footprint[3] - footprint[1]
        scratch = deepcopy(result)
        scratch["degradations"] = []
        next(r for r in scratch["annotations"]["text_regions"] if r["region_id"] == region["region_id"])["visible"] = False
        image, _, _ = render_scene(scratch)
        occupied = np.any(np.asarray(image.convert("RGB")) != 255, axis=2).astype(np.int64)
        integral = np.pad(occupied.cumsum(0).cumsum(1), ((1, 0), (1, 0)))

        def count(box):
            x0, y0 = max(0, math.floor(box[0] - CLEARANCE)), max(0, math.floor(box[1] - CLEARANCE))
            x1, y1 = min(image.width, math.ceil(box[2] + CLEARANCE)), min(image.height, math.ceil(box[3] + CLEARANCE))
            return int(integral[y1, x1] - integral[y0, x1] - integral[y1, x0] + integral[y0, x0])

        overlap_pixels = count(footprint)
        if overlap_pixels == 0:
            continue
        px, py, pr, pb = _ltrb(panel["box"])
        left, top, right, bottom = _ltrb(panel["plot_box"])
        if role == "participant":
            areas = [("participant_left", (px, top, left, bottom)),
                     ("participant_header", (px, py, pr, top))]
        elif role == "axis_title":
            if footprint[0] < left and footprint[1] < bottom:
                areas = [("y_axis_margin", (px, top, left, bottom))]
            elif footprint[1] >= bottom:
                areas = [("x_axis_margin", (left, bottom, right, pb))]
            else:
                unresolved.append({"region_id": region["region_id"], "reason": "axis_title_area_ambiguous"})
                continue
        else:
            center = (footprint[0] + footprint[2]) / 2
            phases = [p for p in panel["phases"] if p["code"].upper() == region["text"] and
                      p["screen_x_min"] <= center <= p["screen_x_max"]]
            if len(phases) != 1:
                unresolved.append({"region_id": region["region_id"], "reason": "condition_phase_binding_not_unique"})
                continue
            phase = phases[0]
            areas = [("same_phase_header", (phase["screen_x_min"], py, phase["screen_x_max"], top))]
        artifacts = [a for a in result["annotations"]["artifacts"] if a["panel_id"] == region["panel_id"] and
                     a["kind"] == "text" and a["role"] == role and a["geometry"]["kind"] == "box" and
                     a["geometry"]["coordinates"][:2] == old_box[:2]]
        if len(artifacts) != 1:
            unresolved.append({"region_id": region["region_id"], "reason": "text_artifact_binding_not_unique"})
            continue
        candidates = []
        for name, (x0, y0, x1, y1) in areas:
            candidates.extend((x, y, name) for y in range(math.ceil(y0 + CLEARANCE), math.floor(y1 - height - CLEARANCE) + 1, CLEARANCE)
                              for x in range(math.ceil(x0 + CLEARANCE), math.floor(x1 - width - CLEARANCE) + 1, CLEARANCE))
        candidates.sort(key=lambda p: ((p[0] - footprint[0]) ** 2 + (p[1] - footprint[1]) ** 2, p[1], p[0], p[2]))
        chosen = next((p for p in candidates if count((p[0], p[1], p[0] + width, p[1] + height)) == 0), None)
        if chosen is None:
            unresolved.append({"region_id": region["region_id"], "reason": "no_clear_semantic_label_area"})
            continue
        x, y, area = chosen
        dx, dy = x - footprint[0], y - footprint[1]
        old_id = region["region_id"]
        region["box"] = [round(old_box[0] + dx, 4), round(old_box[1] + dy, 4), *old_box[2:]]
        region["region_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{old_id}:{region['box']}"))
        artifact = artifacts[0]
        ab = artifact["geometry"]["coordinates"]
        artifact["geometry"]["coordinates"] = [round(ab[0] + dx, 4), round(ab[1] + dy, 4), *ab[2:]]
        artifact["artifact_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{artifact['artifact_id']}:{dx}:{dy}"))
        changes.append({"old_region_id": old_id, "region_id": region["region_id"], "role": role,
                        "old_box": old_box, "box": region["box"][:], "area": area,
                        "underlying_occupied_pixels_before": overlap_pixels, "underlying_occupied_pixels_after": 0})
    if changes:
        result["scene_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{scene['scene_id']}:{changes}"))
    validate_scene(result)
    return AnnotationClearanceResult(result, tuple(changes), tuple(unresolved), VERSION)
