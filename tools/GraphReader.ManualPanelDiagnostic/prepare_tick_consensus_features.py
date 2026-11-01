# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze axis-distance and ink features without reading crop labels."""

from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

from axis_connectivity_probe import (BUILD_INPUT_SHA, BUILD_REPORT_SHA, RANKER_SHA, RUN, read_checked)
from prepare_connectivity_parity import panel_contains
from tick_rank_probe import choose

PARITY_SHA = "7e5e8dc0bf40521bdbc66fa91a370a7613eb8da65d9cd128bd9fca1af2151dd5"
BUILD_FEATURE_SHA = "d890336d4c5fd5004a72cd6b208e17bc550c2407902c1a2925ceb0ddc88ff5e2"


def normalized_gap(box: list, axis: list) -> float:
    left, top, right, bottom = box
    x0, y0, x1, y1 = axis
    if (not all(math.isfinite(value) for value in (*box, *axis)) or right <= left or bottom <= top or y0 == y1):
        raise ValueError("invalid crop or vertical axis")
    axis_x = x0 + (((top + bottom) / 2 - y0) * (x1 - x0) / (y1 - y0))
    return (axis_x - right) / (bottom - top)


def main() -> None:
    read_checked(Path(__file__).with_name("tick_rank_probe.py"), RANKER_SHA)
    inputs = json.loads(read_checked(RUN / "input.json", BUILD_INPUT_SHA))
    report = json.loads(read_checked(RUN / "panel-diagnostic-build-v1/report.json", BUILD_REPORT_SHA))
    build = json.loads(read_checked(RUN / "axis-connectivity-build-features-v1.json", BUILD_FEATURE_SHA))
    parity = json.loads(read_checked(RUN / "axis-connectivity-parity-input-v1.json", PARITY_SHA))
    if (inputs["scope"] != "assistant-reviewed-training-only" or report["source_split"] != "internal_train_build" or
            build["input_sha256"] != BUILD_INPUT_SHA or build["truth_read"] is not False or
            parity["scope"] != "train-and-dev-no-label-truth" or parity["truth_read"] is not False or
            build["optimizer_steps"] != 0 or parity["optimizer_steps"] != 0 or
            build["sealed_payload_reads"] != 0 or parity["sealed_payload_reads"] != 0):
        raise ValueError("tick feature scope differs")
    images = {row["id"]: row for row in report["rows"]}
    connections = {row["id"]: row for row in build["rows"]}
    cached = {}
    rows = []
    for item in inputs["rows"]:
        key = Path(item["image_path"]).stem
        matching = [panel for panel in images[key]["panels"] if panel_contains(panel, item["box_xyxy"])]
        if len(matching) != 1 or connections[item["id"]]["status"] != "measured":
            raise ValueError("training box does not resolve to one measured panel")
        panel = matching[0]
        if key not in cached:
            candidate, _, _ = choose(panel)
            crop = panel["crop"]
            cached[key] = [candidate["Intersection"]["X"] + crop["X"], candidate["Intersection"]["Y"] + crop["Y"],
                           candidate["TopLeft"]["X"] + crop["X"], candidate["TopLeft"]["Y"] + crop["Y"]]
        rows.append({"id": "build:" + item["id"], "image_sha256": item["image_sha256"],
                     "gap_height": normalized_gap(item["box_xyxy"], cached[key]),
                     "axis_connected": connections[item["id"]]["faint_vertical"]})
    for item in parity["rows"]:
        rows.append({"id": item["id"], "image_sha256": item["image_sha256"],
                     "gap_height": normalized_gap(item["box"], item["axis"]),
                     "axis_connected": item["expected_connected"]})
    if len(rows) != 272 or len({row["id"] for row in rows}) != 272 or len({row["image_sha256"] for row in rows}) != 28:
        raise ValueError("tick feature inventory differs")
    result = {"schema": "graphreader.tick-consensus-features.v1", "scope": "exposed-train-and-dev-no-transcriptions",
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "truth_read": False,
              "optimizer_steps": 0, "sealed_payload_reads": 0, "rows": rows}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "tick-consensus-features-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"rows": len(rows), "sources": len({row["image_sha256"] for row in rows}),
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
