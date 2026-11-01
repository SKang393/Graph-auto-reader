# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare private, training-only calibration inputs for candidate diagnostics."""
from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path

import numpy as np

from assisted_calibration_feasibility import ANCHORS, ANCHORS_SHA, CAPTURE, CAPTURE_SHA, PLAN, PLAN_SHA, derive
from score import RUN, checked_json, digest

OUTPUT = RUN / "assisted-calibration-bindings-v1.json"


def prepare(sample: dict, source: dict, captured: dict) -> dict | None:
    if derive(sample["Anchors"], captured["panels"]) != "eligible":
        return None
    matrix = np.array([[p["GraphX"], p["GraphY"], 1] for p in sample["Anchors"]], dtype=np.float64)
    x = np.linalg.solve(matrix, np.array([p["ScreenX"] for p in sample["Anchors"]], dtype=np.float64))
    y = np.linalg.solve(matrix, np.array([p["ScreenY"] for p in sample["Anchors"]], dtype=np.float64))
    max_x, max_y = float(matrix[:, 0].max()), float(matrix[:, 1].max())
    graph = np.array([[1, 0, 1], [1, max_y, 1], [max_x, 0, 1]], dtype=np.float64)
    points = np.column_stack((graph @ x, graph @ y))
    matches = []
    for panel in captured["panels"]:
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        left, top, width, height = (float(crop[k]) for k in ("x", "y", "width", "height"))
        if all(left <= point[0] <= left + width and top <= point[1] <= top + height for point in points):
            matches.append((panel, left, top))
    if len(matches) != 1:
        raise ValueError("Previously eligible panel is no longer unique")
    panel, left, top = matches[0]
    local = points - [left, top]
    return {"id": f"source-{source['source_index']:03d}", "source_sha256": captured["image_sha256"],
            "panel_id": panel["panel_id"], "panel_sha256": panel["panel_sha256"],
            "origin": local[0].tolist(), "y_anchor": local[1].tolist(), "x_anchor": local[2].tolist(),
            "x_max": max_x, "y_max": max_y}


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Assisted bindings already exist")
    anchors, plan, capture = (checked_json(ANCHORS, ANCHORS_SHA), checked_json(PLAN, PLAN_SHA),
                              checked_json(CAPTURE, CAPTURE_SHA))
    if (anchors["selected_train_examples"] != 8 or anchors["development_payloads_opened"] != 0 or
            anchors["sealed_payloads_opened"] != 0 or capture["truth_read"] is not False or
            capture["sealed_payload_reads"] != 0 or len(plan["source_rows"]) != 20 or len(capture["rows"]) != 20):
        raise ValueError("Training-only scope differs")
    sources = {row["image_sha256"].lower(): row for row in plan["source_rows"]}
    captures = {row["id"]: row for row in capture["rows"]}
    rows = []
    for sample in anchors["samples"]:
        source = sources[sample["ImageSha256"].lower()]
        captured = captures[f"source-{source['source_index']:03d}"]
        if (sample["Width"] != source["image_width"] or sample["Height"] != source["image_height"] or
                any(panel["provenance"]["source_sha256"] != captured["image_sha256"] for panel in captured["panels"])):
            raise ValueError("Bound source identity differs")
        row = prepare(sample, source, captured)
        if row is not None:
            rows.append(row)
    rows.sort(key=lambda row: row["id"])
    if len(rows) != 7 or len({row["id"] for row in rows}) != 7:
        raise ValueError("Eligible training image count differs")
    result = {"schema": "graphreader.assisted-calibration-bindings.v1", "scope": "train-only-unapproved",
              "anchors_sha256": ANCHORS_SHA, "plan_sha256": PLAN_SHA, "capture_sha256": CAPTURE_SHA,
              "source_sha256": digest(Path(__file__)), "eligible_images": 7, "excluded_images": 1,
              "recorded_anchors_are_not_user_confirmation": True, "production_approval": False,
              "sealed_payload_reads": 0, "rows": rows}
    raw = (json.dumps(result, indent=2, sort_keys=True, allow_nan=False) + "\n").encode()
    with OUTPUT.open("xb") as stream:
        stream.write(raw)
    print(json.dumps({"eligible_images": len(rows), "excluded_images": 1, "sha256": sha256(raw).hexdigest()}))


if __name__ == "__main__":
    main()
