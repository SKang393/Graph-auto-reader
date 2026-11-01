# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score native adjacent-ink warnings against frozen visual classifications."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import INPUT_SHA, reasons
from score_workflow_y_review import LABEL_SHA, MANIFEST_SHA

PREVIOUS_SHA = "e2d1b086d55e81d8527a3429a824021d491a8352312eb1233b8c85f1bdb82cf2"
WARNING = "ocr_tick_sequence_needs_review:YTick:adjacent_ink:"


def summarize(previous: dict, current: dict, manifest: list[dict], labels: list[dict]) -> dict:
    before = {panel["panel_id"]: panel for row in previous["rows"] for panel in row["panels"]}
    after = {panel["panel_id"]: panel for row in current["rows"] for panel in row["panels"]}
    labelled = {row["id"]: row for row in labels}
    if (len(before) != 21 or len(after) != 21 or set(before) != set(after) or
            len(labelled) != len(labels) or len({row["id"] for row in manifest}) != len(manifest) or
            {row["id"] for row in manifest} != set(labelled)):
        raise ValueError("adjacent-ink evidence inventory differs")
    counts: Counter[str] = Counter()
    for identifier, panel in after.items():
        old = before[identifier]
        old_reasons, new_reasons = reasons(old["error"]), reasons(panel["error"])
        adjacent = [reason for reason in new_reasons if reason.startswith(WARNING)]
        counts["panels"] += 1
        counts["panels_with_adjacent_warning"] += bool(adjacent)
        counts["adjacent_warning_count"] += len(adjacent)
        counts["same_other_reasons"] += old_reasons == [reason for reason in new_reasons if not reason.startswith(WARNING)]
        counts["same_status_and_validity"] += (old["status"], old["failure_code"], old["calibration_validity"]) == (
            panel["status"], panel["failure_code"], panel["calibration_validity"])
        counts["same_center_observations"] += old["centers_before_classification"] == panel["centers_before_classification"]
        counts["same_accepted_markers"] += old["markers"] == panel["markers"]
        counts["recalibration_required"] += panel["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
    categories = defaultdict(Counter)
    for item in manifest:
        old, new = before[item["panel_id"]], after[item["panel_id"]]
        category = labelled[item["id"]]["classification"]
        bucket = categories[category]
        old_reasons, new_reasons = reasons(old["error"]), reasons(new["error"])
        flagged = WARNING + item["region_id"] in new_reasons
        previously_reviewed = any(item["region_id"] in reason and "needs_review" in reason for reason in old_reasons)
        bucket["regions"] += 1
        bucket["adjacent_warning"] += flagged
        bucket["new_region_warning"] += flagged and not previously_reviewed
        bucket["prior_axis_review"] += old["axis_needs_review"] is True
    return {"workflow": dict(counts), "visual_categories": {key: dict(value) for key, value in sorted(categories.items())}}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    previous = checked_json(RUN / "original-workflow-full-v6/report.json", PREVIOUS_SHA)
    current = checked_json(RUN / "original-workflow-full-v7/report.json", args.report_sha256)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-y-review-v1/visual-labels.json", LABEL_SHA)
    if (current["schema"] != "graphreader.manual-marker-workflow-capture.v1" or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["smoke"] is not False or current["dense_tick_separation"] is not False or
            current["truth_read"] is not False or current["optimizer_steps"] != 0 or
            current["sealed_payload_reads"] != 0 or len(current["rows"]) != 20 or
            labels["manifest_sha256"] != MANIFEST_SHA or len(labels["rows"]) != 115):
        raise ValueError("adjacent-ink capture scope differs")
    summary = summarize(previous, current, manifest["rows"], labels["rows"])
    value = {"schema": "graphreader.adjacent-ink-score.v1", "scope": "train-diagnostic-not-acceptance",
             "previous_report_sha256": PREVIOUS_SHA, "report_sha256": args.report_sha256,
             "runtime_sha256": args.runtime_sha256, "manifest_sha256": MANIFEST_SHA, "labels_sha256": LABEL_SHA,
             "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "adjacent-ink-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
