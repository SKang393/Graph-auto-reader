# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Check existing train-only digitization anchors against captured panel crops."""
from __future__ import annotations

from collections import Counter
from hashlib import sha256
import json
import math
from pathlib import Path

import numpy as np

from score import ROOT, RUN, checked_json, digest

ANCHORS = ROOT / "artifacts/manual-dig-train-anchor-review-v1/anchor-samples.json"
ANCHORS_SHA = "899cbd19b7baa39ed00b9882ec3166d83cb80c2d359b4410af39290b601fac12"
PLAN = ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7/plan.json"
PLAN_SHA = "34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428"
CAPTURE = RUN / "provisional-origin-workflow-v1/report.json"
CAPTURE_SHA = "f6d7e6e0dcfa88e3f77c398d4ce6071da59f2d885cba785bb834d985c02f0016"
OUTPUT = RUN / "assisted-calibration-feasibility-v1.json"


def derive(anchors: list[dict], panels: list[dict]) -> str:
    if len(anchors) != 3:
        return "unsupported_anchor_count"
    try:
        matrix = np.array([[p["GraphX"], p["GraphY"], 1] for p in anchors], dtype=np.float64)
        screen_x = np.linalg.solve(matrix, np.array([p["ScreenX"] for p in anchors], dtype=np.float64))
        screen_y = np.linalg.solve(matrix, np.array([p["ScreenY"] for p in anchors], dtype=np.float64))
    except (KeyError, TypeError, ValueError, np.linalg.LinAlgError):
        return "invalid_or_singular_anchors"
    if not np.isfinite(matrix).all() or not np.isfinite(screen_x).all() or not np.isfinite(screen_y).all():
        return "invalid_or_singular_anchors"
    max_x, max_y = float(matrix[:, 0].max()), float(matrix[:, 1].max())
    if not math.isfinite(max_x) or not math.isfinite(max_y) or max_x <= 1 or max_y <= 0:
        return "unsupported_graph_range"
    coordinates = np.array([[1, 0, 1], [1, max_y, 1], [max_x, 0, 1]], dtype=np.float64)
    points = np.column_stack((coordinates @ screen_x, coordinates @ screen_y))
    if not np.isfinite(points).all():
        return "invalid_or_singular_anchors"
    y0, ymax, xmax = points
    if ymax[1] >= y0[1] or xmax[0] <= y0[0]:
        return "reversed_axes"
    if abs(ymax[0] - y0[0]) > 2 or abs(xmax[1] - y0[1]) > 2:
        return "skew_exceeds_manual_contract"
    matches = 0
    for panel in panels:
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        x, y, width, height = (float(crop[key]) for key in ("x", "y", "width", "height"))
        if all(x <= point[0] <= x + width and y <= point[1] <= y + height for point in points):
            matches += 1
    if matches != 1:
        return "ambiguous_or_outside_panel"
    return "eligible"


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Feasibility output already exists")
    anchors = checked_json(ANCHORS, ANCHORS_SHA)
    plan = checked_json(PLAN, PLAN_SHA)
    capture = checked_json(CAPTURE, CAPTURE_SHA)
    if (anchors["selected_train_examples"] != 8 or anchors["development_payloads_opened"] != 0 or
            anchors["sealed_payloads_opened"] != 0 or len(anchors["samples"]) != 8 or
            len(plan["source_rows"]) != 20 or len(capture["rows"]) != 20 or
            capture["truth_read"] is not False or capture["sealed_payload_reads"] != 0):
        raise ValueError("Training-only input scope differs")
    by_image = {row["image_sha256"].lower(): row for row in plan["source_rows"]}
    by_id = {row["id"]: row for row in capture["rows"]}
    if len(by_image) != 20 or len(by_id) != 20:
        raise ValueError("Input identity is not unique")
    counts = Counter()
    for sample in anchors["samples"]:
        source = by_image.get(sample["ImageSha256"].lower())
        if source is None:
            counts["source_not_selected"] += 1
            continue
        predicted = by_id[f"source-{source['source_index']:03d}"]
        if any(panel["provenance"]["source_sha256"] != predicted["image_sha256"] for panel in predicted["panels"]):
            raise ValueError("Captured panel source identity differs")
        counts[derive(sample["Anchors"], predicted["panels"])] += 1
    if sum(counts.values()) != 8:
        raise ValueError("Training source denominator differs")
    result = {"schema": "graphreader.assisted-calibration-feasibility.v1", "scope": "exposed-train-only",
              "anchors_sha256": ANCHORS_SHA, "plan_sha256": PLAN_SHA, "capture_sha256": CAPTURE_SHA,
              "source_sha256": digest(Path(__file__)), "images": 8, "counts": dict(counts),
              "optimizer_steps": 0, "development_payload_reads": 0, "sealed_payload_reads": 0,
              "production_approval": False}
    raw = (json.dumps(result, indent=2, sort_keys=True) + "\n").encode()
    with OUTPUT.open("xb") as stream:
        stream.write(raw)
    print(json.dumps({"counts": result["counts"], "report_sha256": sha256(raw).hexdigest()}))


if __name__ == "__main__":
    main()
