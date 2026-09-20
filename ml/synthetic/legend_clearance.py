# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Opt-in relocation of synthetic legends that cover existing graph content.

The complete legend moves as one unit. Observations and all other annotations
stay fixed. A scene without enough space is returned with an unresolved report,
never made easier by removing data or silently changing its placement category.
"""
from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
import math
from typing import Any, Mapping
from uuid import NAMESPACE_URL, uuid5

import numpy as np

from .annotation_clearance import CLEARANCE, _ltrb, _overlap
from .fonts import FontResolver
from .renderer import _font_settings, render_scene
from .schema import validate_scene

VERSION = "synthetic-legend-clearance-v1"


@dataclass(frozen=True)
class LegendClearanceResult:
    scene: dict[str, Any]
    changes: tuple[dict[str, Any], ...]
    unresolved: tuple[dict[str, str], ...]
    version: str = VERSION


def separate_legends(scene: Mapping[str, Any]) -> LegendClearanceResult:
    """Move only colliding legends, using clean rendered occupancy, not OCR."""
    validate_scene(scene)
    if scene["provenance"]["private_data"] or scene["provenance"]["source"] != "procedural":
        raise ValueError("Legend clearance accepts project-owned procedural scenes only")
    result = deepcopy(dict(scene))
    requested, size, paths = _font_settings(result)
    font = FontResolver(paths).resolve(requested, size).load()
    changes, unresolved = [], []
    regions = result["annotations"]["text_regions"]
    for panel in result["panels"]:
        legend = panel["legend"]
        if not legend.get("visible") or not legend.get("box"):
            continue
        panel_id = panel["panel_id"]
        old_box = legend["box"][:]
        frame = _ltrb(old_box)
        labels = []
        for entry in legend["entries"]:
            matches = [r for r in regions if r["panel_id"] == panel_id and r["role"] == "legend_text"
                       and r["box"][:2] == entry["text_box"][:2] and r["text"] == entry["text"]]
            if len(matches) != 1 or matches[0] in labels:
                break
            labels.append(matches[0])
        if len(labels) != len(legend["entries"]):
            unresolved.append({"panel_id": panel_id, "reason": "legend_text_binding_not_unique"})
            continue
        # Include actual glyph/text extents even when they overflow a declared box.
        bounds = [frame] + [_ltrb(e["glyph_box"]) for e in legend["entries"]]
        for region in labels:
            if region.get("visible", True):
                x, y = region["box"][:2]
                a, b, c, d = font.getbbox(region["text"], anchor="lt")
                bounds.append((x + a, y + b, x + c, y + d))
        footprint = (min(b[0] for b in bounds), min(b[1] for b in bounds),
                     max(b[2] for b in bounds), max(b[3] for b in bounds))
        scratch = deepcopy(result)
        scratch["degradations"] = []
        next(p for p in scratch["panels"] if p["panel_id"] == panel_id)["legend"]["visible"] = False
        label_ids = {r["region_id"] for r in labels}
        for region in scratch["annotations"]["text_regions"]:
            if region["region_id"] in label_ids:
                region["visible"] = False
        image, _, _ = render_scene(scratch)
        occupied = np.any(np.asarray(image.convert("RGB")) != 255, axis=2).astype(np.int64)
        integral = np.pad(occupied.cumsum(0).cumsum(1), ((1, 0), (1, 0)))

        def occupied_pixels(box):
            x0, y0 = max(0, math.floor(box[0] - CLEARANCE)), max(0, math.floor(box[1] - CLEARANCE))
            x1, y1 = min(image.width, math.ceil(box[2] + CLEARANCE)), min(image.height, math.ceil(box[3] + CLEARANCE))
            return int(integral[y1, x1] - integral[y0, x1] - integral[y1, x0] + integral[y0, x0])

        covered = occupied_pixels(footprint)
        if covered == 0:
            continue
        placement = legend["position"]
        if placement not in ("inside", "outside"):
            unresolved.append({"panel_id": panel_id, "reason": "unsupported_legend_placement"})
            continue
        area = _ltrb(panel["plot_box"] if placement == "inside" else panel["box"])
        plot = _ltrb(panel["plot_box"])
        width, height = footprint[2] - footprint[0], footprint[3] - footprint[1]
        candidates = ((x, y) for y in range(math.ceil(area[1] + CLEARANCE), math.floor(area[3] - height - CLEARANCE) + 1, CLEARANCE)
                      for x in range(math.ceil(area[0] + CLEARANCE), math.floor(area[2] - width - CLEARANCE) + 1, CLEARANCE))
        chosen = None
        for x, y in sorted(candidates, key=lambda p: ((p[0] - footprint[0]) ** 2 + (p[1] - footprint[1]) ** 2, p[1], p[0])):
            candidate = (x, y, x + width, y + height)
            if placement == "outside" and _overlap(candidate, plot, CLEARANCE):
                continue
            if occupied_pixels(candidate) == 0:
                chosen = (x - footprint[0], y - footprint[1])
                break
        if chosen is None:
            unresolved.append({"panel_id": panel_id, "reason": "no_clear_legend_area"})
            continue
        dx, dy = chosen

        def moved(box):
            return [round(box[0] + dx, 4), round(box[1] + dy, 4), box[2], box[3]]

        legend["box"] = moved(old_box)
        artifact_moves = [("legend", "legend", old_box, legend["box"])]
        label_changes = []
        for entry, region in zip(legend["entries"], labels):
            entry["glyph_box"] = moved(entry["glyph_box"])
            entry["text_box"] = moved(entry["text_box"])
            prior_box, prior_id = region["box"][:], region["region_id"]
            region["box"] = moved(prior_box)
            region["region_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{prior_id}:{chosen}"))
            artifact_moves.append(("text", "legend_text", prior_box, region["box"]))
            label_changes.append({"old_region_id": prior_id, "region_id": region["region_id"]})
        for artifact in result["annotations"]["artifacts"]:
            if artifact["panel_id"] != panel_id:
                continue
            for kind, role, before, after in artifact_moves:
                if (artifact["kind"] == kind and artifact["role"] == role and
                        artifact["geometry"] == {"kind": "box", "coordinates": before}):
                    artifact["geometry"]["coordinates"] = after[:]
                    artifact["artifact_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{artifact['artifact_id']}:{chosen}"))
                    break
        changes.append({"panel_id": panel_id, "old_box": old_box, "box": legend["box"][:],
                        "translation": list(chosen), "labels": label_changes,
                        "underlying_occupied_pixels_before": covered, "underlying_occupied_pixels_after": 0})
    if changes:
        result["scene_id"] = str(uuid5(NAMESPACE_URL, f"{VERSION}:{scene['scene_id']}:{changes}"))
    validate_scene(result)
    return LegendClearanceResult(result, tuple(changes), tuple(unresolved))
