# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze original-pixel inputs for Python/C# axis-connectivity parity."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from axis_connectivity_probe import INPUT_SHA, JOIN_SHA, REPORT_SHA, RUN, read_checked
from dev_connectivity_probe import MANIFEST_SHA, REPORT_SHA as DEV_REPORT_SHA
from dev_connectivity_review import PROPOSAL_SHA
from tick_rank_probe import choose

FEATURES_SHA = "7b4993a21fde2e6322774f85439ef6ee25634dc170186232e76291dd76ebfd96"
TRAIN_SOURCE_SHA = "c49f7693ce9401c8e90b7b196fc280d528c2a21f012c1e4a41d410a1ae4199fc"


def panel_contains(panel: dict, box: list[int]) -> bool:
    crop = panel["crop"]
    return (panel["axis"]["success"] and crop["X"] <= box[0] < box[2] <= crop["X"] + crop["Width"] and
            crop["Y"] <= box[1] < box[3] <= crop["Y"] + crop["Height"])


def axis_for(panel: dict) -> list[float]:
    selected, _, _ = choose(panel)
    crop = panel["crop"]
    return [selected["Intersection"]["X"] + crop["X"], selected["Intersection"]["Y"] + crop["Y"],
            selected["TopLeft"]["X"] + crop["X"], selected["TopLeft"]["Y"] + crop["Y"]]


def main() -> None:
    train_input = json.loads(read_checked(RUN / "tick-ocr-input-v1.json", INPUT_SHA))
    joins = json.loads(read_checked(RUN / "tick-ocr-join-v1.json", JOIN_SHA))
    features = json.loads(read_checked(RUN / "axis-connectivity-features-v1.json", FEATURES_SHA))
    train_sources = json.loads(read_checked(RUN / "holdout-axis-input.json", TRAIN_SOURCE_SHA))
    train_report = json.loads(read_checked(RUN / "panel-diagnostic-scale-v1/report.json", REPORT_SHA))
    dev_manifest = json.loads(read_checked(RUN / "dev-axis-capture-v1/manifest.json", MANIFEST_SHA))
    dev_report = json.loads(read_checked(RUN / "panel-diagnostic-dev-contrast-v4/report.json", DEV_REPORT_SHA))
    dev = json.loads(read_checked(RUN / "dev-connectivity-proposals-v1.json", PROPOSAL_SHA))
    if (train_input["source_split"] != "internal_train_holdout" or train_report["source_split"] != "internal_train_holdout" or
            features["truth_read"] is not False or features["input_sha256"] != INPUT_SHA or
            features["optimizer_steps"] != 0 or features["sealed_payload_reads"] != 0 or
            dev_manifest["source_split"] != "internal_dev" or dev_report["input_sha256"] != MANIFEST_SHA or
            dev["truth_read"] is not False or dev["manifest_sha256"] != MANIFEST_SHA or dev["axis_report_sha256"] != DEV_REPORT_SHA or
            dev["sealed_payload_reads"] != 0):
        raise ValueError("parity source scope differs")
    sources = {row["id"]: row for row in train_sources["rows"]}
    images = {row["id"]: row for row in train_report["rows"]}
    items = {row["id"]: row for row in train_input["rows"]}
    observed = {row["id"]: row for row in features["rows"]}
    rows = []
    for join in joins:
        item, source, feature = items[join["id"]], sources[join["sample_id"]], observed[join["id"]]
        panels = [panel for panel in images[join["sample_id"]]["panels"] if panel_contains(panel, join["box"])]
        if (len(panels) != 1 or item["box_xyxy"] != join["box"] or item["image_path"] != source["image_path"] or
                item["image_sha256"] != source["image_sha256"] or feature["status"] != "measured"):
            raise ValueError("train parity identity differs")
        rows.append({"id": "train:" + join["id"], "image_path": item["image_path"], "image_sha256": item["image_sha256"],
                     "width": source["width"], "height": source["height"], "box": join["box"],
                     "axis": axis_for(panels[0]), "expected_connected": feature["faint_vertical"]})
    dev_sources = {row["id"]: row for row in dev_manifest["rows"]}
    dev_images = {row["id"]: row for row in dev_report["rows"]}
    for image in dev["rows"]:
        source = dev_sources[image["id"]]
        for panel in image["panels"]:
            matches = [item for item in dev_images[image["id"]]["panels"] if item["Order"] == panel["order"]]
            if len(matches) != 1 or panel["status"] != "measured":
                raise ValueError("development parity panel differs")
            axis = axis_for(matches[0])
            for item in panel["proposals"]:
                if not panel_contains(matches[0], item["box"]):
                    raise ValueError("development parity crop differs")
                rows.append({"id": "dev:" + item["id"], "image_path": source["image_path"],
                             "image_sha256": source["image_sha256"], "width": source["width"], "height": source["height"],
                             "box": item["box"], "axis": axis, "expected_connected": item["faint_vertical"]})
    if len(rows) != 196 or len({row["id"] for row in rows}) != 196:
        raise ValueError("parity inventory differs")
    result = {"schema": "graphreader.axis-connectivity-parity-input.v1", "scope": "train-and-dev-no-label-truth",
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "train_features_sha256": FEATURES_SHA,
              "dev_proposals_sha256": PROPOSAL_SHA, "truth_read": False, "optimizer_steps": 0,
              "sealed_payload_reads": 0, "rows": rows}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "axis-connectivity-parity-input-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"rows": len(rows), "sources": len({row["image_sha256"] for row in rows}),
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
