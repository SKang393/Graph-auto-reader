# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare full-workflow and direct OCR crop identities on two exposed cases."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_workflow_y_review import LABEL_SHA, MANIFEST_SHA

INPUT_SHA = "95bf2f5e3bec1ba4db708242850fc98d56a7a5b17cacf86f8e7633a1376dc5ad"
PREVIOUS_SHA = "95aed5f0d9274db083abba601f78a3d4992fbf4619e18fec4aca9ee2447493e8"
CASES = {"workflow-y-0033": "source-006", "workflow-y-0109": "source-019"}


def compare_case(item: dict, expected: str, trace: dict, previous: dict, direct: dict) -> dict:
    if (len(trace["rows"]) != 1 or trace["rows"][0]["id"] != item["source_id"] or
            trace["recognition_traced"] is not True or trace["truth_read"] is not False):
        raise ValueError("workflow trace source differs")
    trace_panels = {panel["panel_id"]: panel for panel in trace["rows"][0]["panels"]}
    prior_panels = {panel["panel_id"]: panel for row in previous["rows"] for panel in row["panels"]}
    panel, prior = trace_panels[item["panel_id"]], prior_panels[item["panel_id"]]
    detections = [region for observation in panel["detection_trace"] or []
                  for region in observation["raw_detector_regions"] if region["region_id"] == item["region_id"]]
    crops = [crop for crop in panel["recognition_trace"] or [] if crop["region_id"] == item["region_id"]]
    regions = [region for region in panel["ocr_tick_regions"] or [] if region["region_id"] == item["region_id"]]
    if len(detections) != 1 or len(crops) != 1 or len(regions) != 1:
        raise ValueError("workflow trace region inventory differs")
    detected, crop, final = detections[0], crops[0], regions[0]
    alternatives = crop["alternatives"] or []
    if not alternatives:
        raise ValueError("workflow trace has no raw recognition")
    raw = min(alternatives, key=lambda row: (-row["confidence"], row["text"]))["text"]
    bounds = detected["polygon"]["bounds"]
    orientation = detected["orientation_degrees"]
    effective_orientation = -90 if abs(orientation) <= 5e-324 and bounds["height"] > 1.4 * bounds["width"] else orientation
    return {"id": item["id"], "same_status_and_error": (panel["status"], panel["error"], panel["failure_code"]) ==
            (prior["status"], prior["error"], prior["failure_code"]),
            "same_centers": panel["centers_before_classification"] == prior["centers_before_classification"],
            "same_accepted_markers": panel["markers"] == prior["markers"],
            "raw_detector_orientation_degrees": orientation, "effective_orientation_degrees": effective_orientation,
            "workflow_crop_width": crop["width"], "workflow_crop_height": crop["height"],
            "direct_crop_width": direct["crop_width"], "direct_crop_height": direct["crop_height"],
            "same_crop_sha256": crop["crop_sha256"] == direct["crop_sha256"],
            "same_detected_and_final_polygon": detected["polygon"] == final["polygon"],
            "raw_workflow_exact": raw == expected, "final_workflow_exact": final["text"] == expected,
            "direct_exact": direct["text"] == expected,
            "raw_matches_final": raw == final["text"], "raw_matches_direct": raw == direct["text"]}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--trace-a-sha256", required=True)
    parser.add_argument("--trace-b-sha256", required=True)
    parser.add_argument("--direct-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-y-review-v1/visual-labels.json", LABEL_SHA)
    previous = checked_json(RUN / "original-workflow-full-v7/report.json", PREVIOUS_SHA)
    direct = checked_json(RUN / "workflow-y-padded-trace-v1.json", args.direct_sha256)
    traces = {"source-006": checked_json(RUN / "trace-source-006-v1/report.json", args.trace_a_sha256),
              "source-019": checked_json(RUN / "trace-source-019-v1/report.json", args.trace_b_sha256)}
    if (labels["manifest_sha256"] != MANIFEST_SHA or direct["input_sha256"] != INPUT_SHA or
            direct["runtime_description_sha256"] != args.runtime_sha256 or direct["source_split"] != "internal_train_workflow" or
            direct["truth_read"] is not False or direct["optimizer_steps"] != 0 or direct["sealed_payload_reads"] != 0 or
            len(direct["rows"]) != 114 or any(trace["runtime_description_sha256"] != args.runtime_sha256 or
            trace["optimizer_steps"] != 0 or trace["sealed_payload_reads"] != 0 for trace in traces.values())):
        raise ValueError("workflow crop trace scope differs")
    items = {row["id"]: row for row in manifest["rows"]}
    truth = {row["id"]: row for row in labels["rows"]}
    predictions = {row["id"]: row["server"] for row in direct["rows"]}
    results = []
    for identifier, source in CASES.items():
        item, label = items[identifier], truth[identifier]
        if item["source_id"] != source or label["classification"] != "numeric_tick" or label["expected_text"] is None:
            raise ValueError("workflow crop trace selection differs")
        results.append(compare_case(item, label["expected_text"], traces[source], previous, predictions[identifier]))
    value = {"schema": "graphreader.workflow-crop-trace-score.v1", "scope": "exposed-train-diagnostic-not-acceptance",
             "trace_a_sha256": args.trace_a_sha256, "trace_b_sha256": args.trace_b_sha256,
             "direct_sha256": args.direct_sha256, "runtime_sha256": args.runtime_sha256,
             "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "results": results}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-crop-trace-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"results": results, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
