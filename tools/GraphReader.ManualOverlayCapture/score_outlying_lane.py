# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare a review-only lane safeguard with frozen workflow observations."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_workflow_tick_details import INPUT_SHA

PREVIOUS_SHA = "20437621a2de87625273f2ca3e6148e2b06bf56f374bd6236d509873b73226a3"
MANIFEST_SHA = "a45e054c18ea9241fac9e215e6ac6f44b9d020fb9024961991b0f6f98fbbb805"
LABEL_SHA = "344f84c0d66f786578c7451be47deb1c284c7da9b4e5eceeb1fe5ee5e483e903"
WARNING = "ocr_tick_sequence_needs_review:YTick:outlying_text_lane:"
PREFIX = "Automatic calibration is incomplete: "


def reasons(error: str | None) -> list[str]:
    return (error or "").removeprefix(PREFIX).split(" | ")


def summarize(previous: dict, current: dict, reviewed: list[dict]) -> dict:
    before = {panel["panel_id"]: panel for row in previous["rows"] for panel in row["panels"]}
    after = {panel["panel_id"]: panel for row in current["rows"] for panel in row["panels"]}
    if len(before) != 21 or len(after) != 21 or set(before) != set(after):
        raise ValueError("workflow panel inventory differs")
    counts: Counter[str] = Counter()
    for identifier, panel in after.items():
        old = before[identifier]
        old_reasons, new_reasons = reasons(old["error"]), reasons(panel["error"])
        lane_reasons = [reason for reason in new_reasons if reason.startswith(WARNING)]
        counts["panels"] += 1
        counts["panels_with_lane_warning"] += bool(lane_reasons)
        counts["lane_warning_count"] += len(lane_reasons)
        counts["same_other_reasons"] += old_reasons == [reason for reason in new_reasons if not reason.startswith(WARNING)]
        counts["same_status_and_validity"] += (old["status"], old["failure_code"], old["calibration_validity"]) == (
            panel["status"], panel["failure_code"], panel["calibration_validity"])
        counts["same_center_observations"] += old["centers_before_classification"] == panel["centers_before_classification"]
        counts["same_accepted_markers"] += old["markers"] == panel["markers"]
        counts["recalibration_required"] += panel["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
    for item in reviewed:
        identifier, region = item["panel_id"], item["region_id"]
        if identifier not in after:
            raise ValueError("reviewed panel missing")
        counts["reviewed_fragments"] += 1
        counts["reviewed_fragments_with_lane_warning"] += WARNING + region in reasons(after[identifier]["error"])
        counts["reviewed_fragments_without_prior_region_warning"] += not any(
            region in reason and "needs_review" in reason for reason in before[identifier]["calibration_reasons"] or [])
    return dict(counts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    previous = checked_json(RUN / "original-workflow-full-v5/report.json", PREVIOUS_SHA)
    current = checked_json(RUN / "original-workflow-full-v6/report.json", args.report_sha256)
    manifest = checked_json(RUN / "workflow-gap-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-gap-review-v1/visual-labels.json", LABEL_SHA)
    if (current["schema"] != "graphreader.manual-marker-workflow-capture.v1" or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["smoke"] is not False or current["dense_tick_separation"] is not False or
            current["truth_read"] is not False or current["optimizer_steps"] != 0 or
            current["sealed_payload_reads"] != 0 or len(current["rows"]) != 20 or
            labels["manifest_sha256"] != MANIFEST_SHA or len(manifest["rows"]) != 3 or
            {row["id"] for row in labels["rows"] if row["classification"] == "other_text_fragment"} !=
            {row["id"] for row in manifest["rows"]}):
        raise ValueError("outlying lane evidence scope differs")
    summary = summarize(previous, current, manifest["rows"])
    result = {"schema": "graphreader.outlying-lane-score.v1", "scope": "train-diagnostic-not-acceptance",
              "previous_report_sha256": PREVIOUS_SHA, "report_sha256": args.report_sha256,
              "runtime_sha256": args.runtime_sha256, "review_manifest_sha256": MANIFEST_SHA,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "outlying-lane-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
