# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Measure original-pixel axis connectivity for frozen train proposals."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import cv2
import numpy as np

from tick_rank_probe import choose

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
REPORT_SHA = "74ed5eaf7b446c3fbce0a08e9fdf2f859c1f173f94cb566fa041de2e760a6ed6"
INPUT_SHA = "469b5ee37f1d2ad80b4dcb638a3d2183c4949e003de3322d9a186e140228779a"
JOIN_SHA = "537684e62452558a6370980e761f9b92e488959ce691b467f73835f3545f412d"
RANKER_SHA = "e61192366927c3f48b580a9fade972a2e1286777488b4490c633523ea1d0b69d"
BUILD_REPORT_SHA = "2b8e8c5d64d5cdfe9419ab2f66e5535780ae800a332bc3aadd3e5f307c5b7e9c"
BUILD_INPUT_SHA = "06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81"
BUILD_SOURCE_SHA = "ed420156153e1e5496a97b8edce657aa9e63730837c06ba07c46943d713887ae"


def read_checked(path: Path, expected: str) -> bytes:
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError("input hash differs")
    return data


def connected_to_axes(image: np.ndarray, box: list[int], axes: tuple[tuple[float, float], ...],
                      threshold: int) -> tuple[bool, bool]:
    """Test whether candidate ink and measured axes share a local component."""
    left, top, right, bottom = box
    if image.ndim != 2 or not (0 <= left < right <= image.shape[1] and 0 <= top < bottom <= image.shape[0]):
        raise ValueError("invalid proposal bounds")
    (origin_x, origin_y), (y_top_x, y_top_y), (x_right_x, x_right_y) = axes
    if not all(math.isfinite(value) for point in axes for value in point) or \
            abs(y_top_y - origin_y) < 1 or abs(x_right_x - origin_x) < 1:
        raise ValueError("invalid measured axis geometry")
    height = bottom - top
    margin = max(8, height * 2)
    x0 = max(0, left - margin)
    y0 = max(0, top - margin)
    x1 = min(image.shape[1], right + margin)
    y1 = min(image.shape[0], bottom + margin)
    foreground = (image[y0:y1, x0:x1] <= threshold).astype(np.uint8)
    _, components = cv2.connectedComponents(foreground, connectivity=8)
    box_ids = set(np.unique(components[top-y0:bottom-y0, left-x0:right-x0]).tolist()) - {0}
    vertical, horizontal = set(), set()
    for y in range(y0, y1):
        x = round(origin_x + (y - origin_y) * (y_top_x - origin_x) / (y_top_y - origin_y))
        for offset in (-1, 0, 1):
            if x0 <= x + offset < x1:
                vertical.add(int(components[y-y0, x+offset-x0]))
    for x in range(x0, x1):
        y = round(origin_y + (x - origin_x) * (x_right_y - origin_y) / (x_right_x - origin_x))
        for offset in (-1, 0, 1):
            if y0 <= y + offset < y1:
                horizontal.add(int(components[y+offset-y0, x-x0]))
    return bool(box_ids & (vertical - {0})), bool(box_ids & (horizontal - {0}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true")
    build = parser.parse_args().build
    report_sha = BUILD_REPORT_SHA if build else REPORT_SHA
    input_sha = BUILD_INPUT_SHA if build else INPUT_SHA
    report_name = "panel-diagnostic-build-v1/report.json" if build else "panel-diagnostic-scale-v1/report.json"
    input_name = "input.json" if build else "tick-ocr-input-v1.json"
    report = json.loads(read_checked(RUN / report_name, report_sha))
    inputs = json.loads(read_checked(RUN / input_name, input_sha))
    if build:
        source_manifest = json.loads(read_checked(RUN / "axis-input.json", BUILD_SOURCE_SHA))
        sources = {row["id"]: row for row in source_manifest["rows"]}
        joins = [{"id": row["id"], "sample_id": Path(row["image_path"]).stem, "box": row["box_xyxy"]}
                 for row in inputs["rows"]]
    else:
        sources = {}
        joins = json.loads(read_checked(RUN / "tick-ocr-join-v1.json", JOIN_SHA))
    read_checked(Path(__file__).with_name("tick_rank_probe.py"), RANKER_SHA)
    split = "internal_train_build" if build else "internal_train_holdout"
    scope = "assistant-reviewed-training-only" if build else "exposed-train-only"
    count, source_count = (76, 8) if build else (62, 5)
    if (report["source_split"] != split or report["optimizer_steps"] != 0 or
            inputs["scope"] != scope or inputs["source_split"] != split or
            len(inputs["rows"]) != count or len(joins) != count or
            build and (report["input_sha256"] != BUILD_SOURCE_SHA or source_manifest["source_split"] != split)):
        raise ValueError("train proposal scope differs")
    images = {row["id"]: row for row in report["rows"]}
    input_by_id = {row["id"]: row for row in inputs["rows"]}
    if len(images) != source_count or len(input_by_id) != count or {row["id"] for row in joins} != set(input_by_id):
        raise ValueError("proposal inventory differs")
    cache: dict[str, np.ndarray] = {}
    rows = []
    for item in joins:
        source = input_by_id[item["id"]]
        source_root = ("artifacts/manual-dig-train-anchor-review-v1/images/" if build else
                       "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/")
        if item["box"] != source["box_xyxy"] or not source["image_path"].startswith(source_root):
            raise ValueError("proposal source binding differs")
        if build and (item["sample_id"] not in sources or
                      sources[item["sample_id"]]["image_sha256"] != source["image_sha256"] or
                      sources[item["sample_id"]]["image_path"] != source["image_path"]):
            raise ValueError("build image identity differs")
        if source["image_sha256"] not in cache:
            path = (ROOT / source["image_path"]).resolve(strict=True)
            if not path.is_relative_to(ROOT.resolve()):
                raise ValueError("source escaped the project")
            data = read_checked(path, source["image_sha256"])
            decoded = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
            if decoded is None:
                raise ValueError("source image decode failed")
            cache[source["image_sha256"]] = decoded
        box = item["box"]
        panels = []
        for panel in images[item["sample_id"]]["panels"]:
            crop = panel["crop"]
            if (panel["axis"]["success"] and crop["X"] <= box[0] < box[2] <= crop["X"] + crop["Width"] and
                    crop["Y"] <= box[1] < box[3] <= crop["Y"] + crop["Height"]):
                panels.append(panel)
        if len(panels) != 1:
            rows.append({"id": item["id"], "status": "ambiguous_panel"})
            continue
        panel = panels[0]
        selected, _, _ = choose(panel)
        crop = panel["crop"]
        axes = tuple((selected[key]["X"] + crop["X"], selected[key]["Y"] + crop["Y"])
                     for key in ("Intersection", "TopLeft", "BottomRight"))
        dark = connected_to_axes(cache[source["image_sha256"]], box, axes, 128)
        faint = connected_to_axes(cache[source["image_sha256"]], box, axes, 200)
        rows.append({"id": item["id"], "status": "measured", "dark_vertical": dark[0], "dark_horizontal": dark[1],
                     "faint_vertical": faint[0], "faint_horizontal": faint[1]})
    result = {"schema": "graphreader.train-axis-connectivity-probe.v1", "scope": "train-diagnostic-not-acceptance",
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "input_sha256": input_sha,
              "join_sha256": None if build else JOIN_SHA, "axis_report_sha256": report_sha, "truth_read": False,
              "optimizer_steps": 0, "sealed_payload_reads": 0, "rows": rows}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    output = "axis-connectivity-build-features-v1.json" if build else "axis-connectivity-features-v1.json"
    with (RUN / output).open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"proposals": len(rows), "measured": sum(row["status"] == "measured" for row in rows),
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
