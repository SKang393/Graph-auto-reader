# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Audit V3's actual rendered annotations without inventing artifact masks.

The supplied scene is declared train-only by the caller; this function cannot
authenticate its provenance. It neither opens files nor writes corpus outputs.
The original annotation geometry is descriptive, not final-pixel truth.
"""

from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict
from hashlib import sha256
from typing import Any, Mapping

from ml.markers.center.artifact_mask_public_gate import PROHIBITED_STRUCTURE_KINDS
from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_scene


_ANNOTATION_KEYS = {
    "text": "texts",
    "axis": "axes",
    "tick": "ticks",
    "divider": "dividers",
    "bracket": "brackets",
    "arrow_shaft": "arrows",
    "arrowhead": "arrows",
    "legend": "legends",
    # V3 does not annotate all crossings of drawn lines as a separate class.
    "line_intersection": None,
}


def _records(annotation: Mapping[str, Any], key: str) -> list[dict[str, Any]]:
    records = list(annotation.get(key, []))
    for panel in annotation.get("panels", []):
        records.extend(panel.get(key, []))
    return deepcopy(records)


def audit_train_scene(scene: Mapping[str, Any], *, split: str) -> dict[str, Any]:
    """Render one caller-declared training scene and record available evidence.

    No filename, seed, or split label proves that a source is disjoint from a
    validation or sealed set. Source provenance must be checked separately.
    """
    if split != "train":
        raise ValueError("only caller-declared train scenes are supported")
    if set(_ANNOTATION_KEYS) != set(PROHIBITED_STRUCTURE_KINDS):
        raise RuntimeError("artifact taxonomy changed; review the capability map")

    result = render_visible_content_scene(scene)
    annotation = result.annotation
    categories: dict[str, dict[str, Any]] = {}
    for kind in PROHIBITED_STRUCTURE_KINDS:
        key = _ANNOTATION_KEYS[kind]
        records = [] if key is None else _records(annotation, key)
        categories[kind] = {
            "annotation_key": key,
            "semantic_records": records,
            "semantic_record_count": len(records),
            "full_final_pixel_mask_available": False,
        }

    return {
        "schema": "graphreader.v3-artifact-capability-audit.v1",
        "split_declaration": "train",
        "split_provenance_verified": False,
        "scene_id": annotation["scene_id"],
        "raster_mode": result.image.mode,
        "raster_size": list(result.image.size),
        "raster_sha256": sha256(result.image.tobytes()).hexdigest(),
        "marker_mask_sha256": sha256(result.marker_mask.tobytes()).hexdigest(),
        "coordinate_space": annotation["coordinate_space"],
        "categories": categories,
        # Renderer hard-negative geometry records actual selected placement;
        # requested centers are not substituted for relocated geometry.
        "hard_negatives": _records(annotation, "hard_negatives"),
        "text_support_audits": [asdict(item) for item in result.audit.texts],
        "complete_artifact_mask": False,
        "training_input_ready": False,
        "production_approved": False,
        "limitations": [
            "Semantic records retain clean source geometry and visibility flags; they are not complete final raster masks.",
            "Arrow shaft and head share an arrow record; the individual geometry fields do not establish post-degradation masks.",
            "Line intersections are not exhaustively enumerated by the V3 annotation contract.",
            "Text support audits do not establish readability or visible composited ownership.",
            "Caller-declared train membership is not verified for source or split independence.",
        ],
    }
