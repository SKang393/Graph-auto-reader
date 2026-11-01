# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Summarize emitted candidate-workflow tick evidence without printing text."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import statistics

from propose_dense_tick_windows import RUN, checked_json

INPUT_SHA = "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6"
PREVIOUS_SHA = "f7910323ae4536c8d84998fd71c5ffb13c12ea96182ceea725ba92c67b343efb"


def summarize(previous: dict, current: dict) -> dict:
    before = {panel["panel_id"]: panel for row in previous["rows"] for panel in row["panels"]}
    after = {panel["panel_id"]: panel for row in current["rows"] for panel in row["panels"]}
    if len(before) != 21 or len(after) != 21 or set(before) != set(after):
        raise ValueError("workflow panel inventory differs")
    counts: Counter[str] = Counter()
    for identifier, panel in after.items():
        old = before[identifier]
        counts["panels"] += 1
        counts["same_status_and_error"] += (old["status"], old["error"], old["failure_code"], old["calibration_validity"]) == (
            panel["status"], panel["error"], panel["failure_code"], panel["calibration_validity"])
        counts["same_center_observations"] += old["centers_before_classification"] == panel["centers_before_classification"]
        counts["same_accepted_markers"] += old["markers"] == panel["markers"]
        counts["axis_needs_review"] += panel["axis_needs_review"] is True
        counts["recalibration_required"] += panel["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
        regions = [region for region in (panel["ocr_tick_regions"] or []) if region["role"] == "YTick"]
        numeric = [region for region in regions if isinstance(region["numeric_value"], (int, float)) and
                   math.isfinite(region["numeric_value"])]
        if len({region["region_id"] for region in regions}) != len(regions):
            raise ValueError("duplicate tick region identity")
        counts["y_tick_regions"] += len(regions)
        counts["numeric_y_tick_regions"] += len(numeric)
        counts["literal_numeric_y_tick_regions"] += sum(region["literal_numeric"] for region in numeric)
        counts["panels_with_three_numeric_y_ticks"] += len(numeric) >= 3
        reasons = panel["calibration_reasons"] or []
        for label, prefix in (("panels_with_competing_y_readings", "ocr_tick_sequence_needs_review:YTick:competing_numeric_readings:"),
                              ("panels_with_axis_connected_y_readings", "ocr_tick_sequence_needs_review:YTick:axis_connected_text:")):
            counts[label] += any(reason.startswith(prefix) for reason in reasons)
        conflicts = {reason.rsplit(":", 1)[-1] for reason in reasons
                     if reason.startswith("ocr_tick_sequence_needs_review:YTick:competing_numeric_readings:")}
        counts["numeric_y_ticks_with_competing_readings"] += sum(region["region_id"] in conflicts for region in numeric)
        axis = panel["y_axis_line"]
        if len(numeric) >= 3 and axis:
            start, end = axis["start"], axis["end"]
            dy = end["y"] - start["y"]
            if dy == 0:
                raise ValueError("invalid selected Y axis")
            gaps = []
            for region in numeric:
                box = region["bounds"]
                if box["height"] <= 0:
                    raise ValueError("invalid tick region bounds")
                y = box["top"] + box["height"] / 2
                x = start["x"] + (y - start["y"]) * (end["x"] - start["x"]) / dy
                gaps.append((x - box["right"]) / box["height"])
            median = statistics.median(gaps)
            counts["panels_with_relative_gap_outlier"] += any(gap > median + 1.25 for gap in gaps)
            counts["relative_gap_outlier_regions"] += sum(gap > median + 1.25 for gap in gaps)
    return dict(counts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    previous = checked_json(RUN / "original-workflow-full-v4/report.json", PREVIOUS_SHA)
    current = checked_json(RUN / "original-workflow-full-v5/report.json", args.report_sha256)
    if (current["schema"] != "graphreader.manual-marker-workflow-capture.v1" or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["detailed_capture"] is not True or current["smoke"] is not False or
            current["dense_tick_separation"] is not False or current["truth_read"] is not False or
            current["optimizer_steps"] != 0 or current["sealed_payload_reads"] != 0 or
            previous["input_sha256"] != INPUT_SHA or len(current["rows"]) != 20):
        raise ValueError("workflow tick capture scope differs")
    summary = summarize(previous, current)
    result = {"schema": "graphreader.workflow-tick-details-score.v1", "scope": "train-diagnostic-not-acceptance",
              "previous_report_sha256": PREVIOUS_SHA, "report_sha256": args.report_sha256,
              "runtime_sha256": args.runtime_sha256, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-tick-details-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
