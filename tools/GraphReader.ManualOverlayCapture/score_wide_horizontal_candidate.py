# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare an exposed-train orientation candidate with the frozen workflow."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import INPUT_SHA, reasons
from score_workflow_y_review import LABEL_SHA, MANIFEST_SHA

BASELINE_SHA = "95aed5f0d9274db083abba601f78a3d4992fbf4619e18fec4aca9ee2447493e8"
TICK_BASELINE_SHA = "20437621a2de87625273f2ca3e6148e2b06bf56f374bd6236d509873b73226a3"


def summarize(previous: dict, previous_ticks: dict, current: dict, manifest: list[dict], labels: list[dict]) -> dict:
    def panels(report: dict) -> dict:
        values = [panel for row in report["rows"] for panel in row["panels"]]
        result = {panel["panel_id"]: panel for panel in values}
        if len(values) != len(result):
            raise ValueError("duplicate panel")
        return result

    before, tick_before, after = panels(previous), panels(previous_ticks), panels(current)
    labelled = {row["id"]: row for row in labels}
    if (len(before) != 21 or set(before) != set(after) or set(before) != set(tick_before) or len(labelled) != len(labels) or
            len({row["id"] for row in manifest}) != len(manifest) or
            {row["id"] for row in manifest} != set(labelled)):
        raise ValueError("candidate inventory differs")
    workflow = Counter()
    for panel_id, panel in after.items():
        old = before[panel_id]
        workflow["panels"] += 1
        workflow["recalibration_required"] += panel["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
        workflow["same_status_and_validity"] += (old["status"], old["failure_code"], old["calibration_validity"]) == (
            panel["status"], panel["failure_code"], panel["calibration_validity"])
        workflow["same_error"] += old["error"] == panel["error"]
        workflow["same_center_observations"] += old["centers_before_classification"] == panel["centers_before_classification"]
        workflow["same_accepted_markers"] += old["markers"] == panel["markers"]
        old_y = {r["region_id"] for r in tick_before[panel_id]["ocr_tick_regions"] or [] if r["role"] == "YTick"}
        new_y = {r["region_id"] for r in panel["ocr_tick_regions"] or [] if r["role"] == "YTick"}
        workflow["new_y_regions"] += len(new_y - old_y)
        workflow["lost_y_regions"] += len(old_y - new_y)
    categories = defaultdict(Counter)
    for item in manifest:
        old, new = before[item["panel_id"]], after[item["panel_id"]]
        def find(panel: dict) -> dict | None:
            matches = [r for r in panel["ocr_tick_regions"] or [] if r["region_id"] == item["region_id"]]
            if len(matches) > 1:
                raise ValueError("duplicate reviewed region")
            return matches[0] if matches and matches[0]["role"] == "YTick" else None
        prior, later = find(tick_before[item["panel_id"]]), find(new)
        if prior is None:
            raise ValueError("baseline reviewed region missing")
        label = labelled[item["id"]]
        expected = label["expected_text"]
        if (label["classification"] == "numeric_tick") != (expected is not None):
            raise ValueError("visual classification differs")
        bucket = categories[label["classification"]]
        bucket["reviewed"] += 1
        bucket["missing_or_reclassified"] += later is None
        if later is None:
            continue
        bucket["same_text"] += prior["text"] == later["text"]
        old_warning = any(item["region_id"] in r and "needs_review" in r for r in reasons(old["error"]))
        new_warning = any(item["region_id"] in r and "needs_review" in r for r in reasons(new["error"]))
        bucket["gained_region_review"] += new_warning and not old_warning
        bucket["lost_region_review"] += old_warning and not new_warning
        if expected is not None:
            before_correct = prior["numeric_value"] == float(expected)
            after_correct = later["numeric_value"] == float(expected)
            bucket["previous_numeric_correct"] += before_correct
            bucket["candidate_numeric_correct"] += after_correct
            bucket["corrected"] += not before_correct and after_correct
            bucket["regressed"] += before_correct and not after_correct
            bucket["candidate_exact"] += later["text"] == expected
    return {"workflow": dict(workflow),
            "visual_categories": {key: dict(value) for key, value in sorted(categories.items())}}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    previous = checked_json(RUN / "original-workflow-full-v7/report.json", BASELINE_SHA)
    previous_ticks = checked_json(RUN / "original-workflow-full-v5/report.json", TICK_BASELINE_SHA)
    current = checked_json(RUN / "wide-horizontal-candidate-v1/report.json", args.report_sha256)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-y-review-v1/visual-labels.json", LABEL_SHA)
    if (current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["smoke"] is not False or current["detailed_capture"] is not True or
            current["wide_horizontal_model_orientation"] is not True or current["truth_read"] is not False or
            current["optimizer_steps"] != 0 or current["sealed_payload_reads"] != 0 or
            len(current["rows"]) != 20 or labels["manifest_sha256"] != MANIFEST_SHA or len(labels["rows"]) != 115 or
            manifest["report_sha256"] != TICK_BASELINE_SHA or previous_ticks["detailed_capture"] is not True):
        raise ValueError("candidate capture scope differs")
    summary = summarize(previous, previous_ticks, current, manifest["rows"], labels["rows"])
    value = {"schema": "graphreader.wide-horizontal-candidate-score.v1", "scope": "train-diagnostic-not-acceptance",
             "previous_report_sha256": BASELINE_SHA, "tick_baseline_sha256": TICK_BASELINE_SHA,
             "report_sha256": args.report_sha256,
             "runtime_sha256": args.runtime_sha256, "manifest_sha256": MANIFEST_SHA, "labels_sha256": LABEL_SHA,
             "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "wide-horizontal-candidate-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
