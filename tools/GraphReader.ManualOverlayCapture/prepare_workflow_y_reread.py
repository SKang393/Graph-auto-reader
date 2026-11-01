# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze source-coordinate workflow crops without OCR text or visual labels."""

from __future__ import annotations

import hashlib
import json

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import INPUT_SHA, PREVIOUS_SHA
from score_workflow_y_review import MANIFEST_SHA


def padded_inside_panel(box: dict, crop: dict) -> bool:
    return (box["width"] > 0 and box["height"] > 0 and box["left"] >= 1 and box["top"] >= 1 and
            box["right"] + 1 <= crop["width"] and box["bottom"] + 1 <= crop["height"])


def main() -> None:
    sources = checked_json(RUN / "original-raster-input-v1.json", INPUT_SHA)
    report = checked_json(RUN / "original-workflow-full-v5/report.json", PREVIOUS_SHA)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    if (report["input_sha256"] != INPUT_SHA or report["detailed_capture"] is not True or
            report["truth_read"] is not False or report["optimizer_steps"] != 0 or
            report["sealed_payload_reads"] != 0 or manifest["report_sha256"] != PREVIOUS_SHA):
        raise ValueError("workflow reread scope differs")
    source_by_id = {row["id"]: row for row in sources["rows"]}
    panels = {panel["panel_id"]: panel for row in report["rows"] for panel in row["panels"]}
    rows, excluded = [], []
    for item in manifest["rows"]:
        source, panel = source_by_id[item["source_id"]], panels[item["panel_id"]]
        matches = [region for region in panel["ocr_tick_regions"] or [] if region["region_id"] == item["region_id"]]
        if (len(matches) != 1 or matches[0]["role"] != "YTick" or
                source["image_sha256"] != item["image_sha256"] or
                panel["provenance"]["source_sha256"] != item["image_sha256"]):
            raise ValueError("workflow reread identity differs")
        crop, box = panel["provenance"]["encoded_crop_source_pixels"], matches[0]["bounds"]
        if not padded_inside_panel(box, crop):
            excluded.append({"id": item["id"], "reason": "padded_box_crosses_panel_boundary"})
            continue
        rows.append({"id": item["id"], "image_path": source["image_path"], "image_sha256": source["image_sha256"],
                     "box_xyxy": [crop["x"] + box["left"], crop["y"] + box["top"],
                                  crop["x"] + box["right"], crop["y"] + box["bottom"]]})
    if (len(manifest["rows"]) != 115 or len(rows) != 114 or len(excluded) != 1 or
            len({row["image_sha256"] for row in rows}) != 19 or len({row["id"] for row in rows}) != 114):
        raise ValueError("workflow reread inventory differs")
    value = {"schema": "graphreader.manual-axis-ocr-input.v1", "scope": "exposed-train-workflow-no-transcriptions",
             "source_split": "internal_train_workflow", "rows": rows}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-y-reread-input-v1.json").open("xb") as stream:
        stream.write(encoded)
    exclusion = {"schema": "graphreader.workflow-y-reread-exclusions.v1", "manifest_sha256": MANIFEST_SHA,
                 "input_sha256": hashlib.sha256(encoded).hexdigest(), "rows": excluded}
    excluded_bytes = (json.dumps(exclusion, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-y-reread-exclusions-v1.json").open("xb") as stream:
        stream.write(excluded_bytes)
    print(json.dumps({"regions": len(rows), "excluded": len(excluded), "sources": 19,
                      "input_sha256": hashlib.sha256(encoded).hexdigest(),
                      "exclusions_sha256": hashlib.sha256(excluded_bytes).hexdigest()}))


if __name__ == "__main__":
    main()
