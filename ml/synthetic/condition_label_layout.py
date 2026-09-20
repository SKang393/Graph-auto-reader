# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Opt-in repair of explicitly bound synthetic condition-label placement.

Bindings are authored region -> condition-bar identities, supplied separately
from the frozen scene schema. Never infer repeated labels' ownership from their
current positions: the historical layout can put them in a neighboring phase.
Historical scenes and default rendering are unchanged.
"""
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

VERSION = "synthetic-condition-label-phase-layout-v1"


def bind_condition_label_layout(
    scene: Mapping[str, Any], *, split: str, label_bindings: Mapping[str, str]
) -> AnnotationClearanceResult:
    """Move a bound caption only inside its authored phase's header interval.

    Supply every visible condition label exactly once. Missing, duplicate or
    contradictory bindings are rejected before rendering. No-space layouts
    retain their original labels and return an explicit unresolved finding.
    """
    if split not in ("train", "dev"):
        raise ValueError("Condition layout repair is restricted to train/dev")
    validate_scene(scene)
    provenance = scene["provenance"]
    if (provenance["private_data"] or provenance["source"] != "procedural" or
            provenance["external_assets"] or provenance["license"] != "Apache-2.0"):
        raise ValueError("Condition layout requires owned Apache-2.0 procedural scenes")
    result = deepcopy(dict(scene))
    regions = [r for r in result["annotations"]["text_regions"]
               if r["role"] == "condition_label" and r.get("visible", True)]
    if (set(label_bindings) != {r["region_id"] for r in regions} or
            len(set(label_bindings.values())) != len(label_bindings)):
        raise ValueError("Every visible condition label needs one unique authored bar binding")
    panels = {p["panel_id"]: p for p in result["panels"]}
    validated = []
    for region in regions:
        panel = panels[region["panel_id"]]
        bars = [b for b in panel["condition_bars"] if b["bar_id"] == label_bindings[region["region_id"]]]
        if len(bars) != 1 or bars[0]["label"] != region["text"]:
            raise ValueError("Condition binding must name a same-panel bar with identical text")
        bar = bars[0]
        phases = panel["phases"]
        matches = [(i, p) for i, p in enumerate(phases) if not p.get("blank", False) and
                   [p["screen_x_min"], p["screen_x_max"]] == [bar["line"][0], bar["line"][2]]]
        if len(matches) != 1:
            raise ValueError("Condition bar must bind one authored phase")
        index, phase = matches[0]
        plot_left, plot_top, plot_right, _ = _ltrb(panel["plot_box"])
        left = (phases[index - 1]["screen_x_max"] + phase["screen_x_min"]) / 2 if index else plot_left
        right = (phase["screen_x_max"] + phases[index + 1]["screen_x_min"]) / 2 if index + 1 < len(phases) else plot_right
        area = (left, panel["box"][1], right, plot_top)
        artifacts = [a for a in result["annotations"]["artifacts"] if a["panel_id"] == region["panel_id"] and
                     a["kind"] == "text" and a["role"] == "condition_label" and
                     a["geometry"]["kind"] == "box" and
                     a["geometry"]["coordinates"][:2] == region["box"][:2]]
        if len(artifacts) != 1 or not plot_left <= left < right <= plot_right:
            raise ValueError("Condition label requires unique artifact geometry and an ordered phase interval")
        validated.append((region, artifacts[0], phase, bar, area))

    requested, size, paths = _font_settings(result)
    font = FontResolver(paths).resolve(requested, size).load()
    changes, unresolved = [], []
    for region, artifact, phase, bar, area in validated:
        old_box = region["box"][:]
        measured = font.getbbox(region["text"], anchor="lt")
        footprint = (old_box[0] + measured[0], old_box[1] + measured[1],
                     old_box[0] + measured[2], old_box[1] + measured[3])
        width, height = footprint[2] - footprint[0], footprint[3] - footprint[1]
        scratch = deepcopy(result)
        scratch["degradations"] = []
        next(r for r in scratch["annotations"]["text_regions"] if r["region_id"] == region["region_id"])["visible"] = False
        image, _, _ = render_scene(scratch)
        try:
            occupied = np.any(np.asarray(image.convert("RGB")) != 255, axis=2).astype(np.int64)
            integral = np.pad(occupied.cumsum(0).cumsum(1), ((1, 0), (1, 0)))

            def count(box):
                x0, y0 = max(0, math.floor(box[0] - CLEARANCE)), max(0, math.floor(box[1] - CLEARANCE))
                x1, y1 = min(image.width, math.ceil(box[2] + CLEARANCE)), min(image.height, math.ceil(box[3] + CLEARANCE))
                return int(integral[y1, x1] - integral[y0, x1] - integral[y1, x0] + integral[y0, x0])

            inside = area[0] <= footprint[0] and footprint[2] <= area[2] and area[1] <= footprint[1] and footprint[3] <= area[3]
            occupied_before = count(footprint)
            if inside and occupied_before == 0:
                continue
            candidates = [(x, y) for y in range(math.ceil(area[1] + CLEARANCE), math.floor(area[3] - height - CLEARANCE) + 1, CLEARANCE)
                          for x in range(math.ceil(area[0] + CLEARANCE), math.floor(area[2] - width - CLEARANCE) + 1, CLEARANCE)]
            candidates.sort(key=lambda p: ((p[0] - footprint[0]) ** 2 + (p[1] - footprint[1]) ** 2, p[1], p[0]))
            chosen = next((p for p in candidates if count((p[0], p[1], p[0] + width, p[1] + height)) == 0), None)
            if chosen is None:
                unresolved.append({"region_id": region["region_id"], "reason": "no_clear_header_space_in_authored_phase"})
                continue
            x, y = chosen
            dx, dy = x - footprint[0], y - footprint[1]
            old_id = region["region_id"]
            region["box"] = [round(old_box[0] + dx, 4), round(old_box[1] + dy, 4), *old_box[2:]]
            region["region_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{old_id}:{region['box']}"))
            artifact["geometry"]["coordinates"] = region["box"][:]
            artifact["artifact_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{artifact['artifact_id']}:{region['box']}"))
            changes.append({"old_region_id": old_id, "region_id": region["region_id"], "phase_id": phase["phase_id"],
                            "bar_id": bar["bar_id"], "old_box": old_box, "box": region["box"][:],
                            "phase_header_area": list(area), "outside_phase_before": not inside,
                            "occupied_pixels_before": occupied_before, "occupied_pixels_after": 0})
        finally:
            image.close()
    if changes:
        result["scene_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{scene['scene_id']}:{changes}"))
    validate_scene(result)
    return AnnotationClearanceResult(result, tuple(changes), tuple(unresolved), VERSION)
