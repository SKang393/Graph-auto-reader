# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score a frozen visual review of detector-emitted Y-tick regions."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import INPUT_SHA, PREVIOUS_SHA, WARNING, reasons

MANIFEST_SHA = "6d793b8887d82f334d8adabcdd8bf2e557749f95842c3fc1a617d17303447e78"
LABEL_SHA = "c9c01aa57693311c04fa587e97b5c9cb3f82357f70489143a2312c6ffe33c309"
PROTOCOL_SHA = "365dcec516813324725cf0c861a072a75433ec26a5ad26865c6746c017b2a7a7"


def summarize(previous: dict, current: dict, manifest: list[dict], labels: list[dict]) -> dict:
    before = {panel["panel_id"]: panel for row in previous["rows"] for panel in row["panels"]}
    after = {panel["panel_id"]: panel for row in current["rows"] for panel in row["panels"]}
    labelled = {row["id"]: row for row in labels}
    if (len(labelled) != len(labels) or len({row["id"] for row in manifest}) != len(manifest) or
            {row["id"] for row in manifest} != set(labelled) or set(before) != set(after)):
        raise ValueError("workflow Y-review inventory differs")
    categories = defaultdict(Counter)
    for item in manifest:
        panel = before[item["panel_id"]]
        later = after[item["panel_id"]]
        matches = [region for region in panel["ocr_tick_regions"] or [] if region["region_id"] == item["region_id"]]
        if len(matches) != 1 or matches[0]["role"] != "YTick":
            raise ValueError("reviewed region is missing or duplicated")
        region, label = matches[0], labelled[item["id"]]
        category = label["classification"]
        expected = label["expected_text"]
        if (category == "numeric_tick") != (expected is not None):
            raise ValueError("workflow visual transcription differs from its classification")
        counts = categories[category]
        counts["regions"] += 1
        counts["previous_region_review"] += any(
            item["region_id"] in reason and "needs_review" in reason for reason in panel["calibration_reasons"] or [])
        counts["lane_warning"] += WARNING + item["region_id"] in reasons(later["error"])
        counts["axis_review"] += panel["axis_needs_review"] is True
        if expected is not None:
            counts["exact"] += region["text"] == expected
            counts["numeric_correct"] += region["numeric_value"] == float(expected)
            counts["numeric_wrong"] += region["numeric_value"] != float(expected)
            counts["wrong_without_previous_region_review"] += (
                region["numeric_value"] != float(expected) and not any(
                    item["region_id"] in reason and "needs_review" in reason
                    for reason in panel["calibration_reasons"] or []))
    return {key: dict(value) for key, value in sorted(categories.items())}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    before = checked_json(RUN / "original-workflow-full-v5/report.json", PREVIOUS_SHA)
    after = checked_json(RUN / "original-workflow-full-v6/report.json", args.report_sha256)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-y-review-v1/visual-labels.json", LABEL_SHA)
    protocol = Path(__file__).with_name("WORKFLOW_Y_REVIEW_PROTOCOL.md")
    if (hashlib.sha256(protocol.read_bytes()).hexdigest() != PROTOCOL_SHA or
            labels["protocol_sha256"] != PROTOCOL_SHA or labels["manifest_sha256"] != MANIFEST_SHA or
            labels["scope"] != "assistant-train-review-not-independent" or
            manifest["report_sha256"] != PREVIOUS_SHA or len(labels["rows"]) != 115 or
            after["input_sha256"] != INPUT_SHA or after["runtime_description_sha256"] != args.runtime_sha256 or
            after["truth_read"] is not False or after["optimizer_steps"] != 0 or after["sealed_payload_reads"] != 0):
        raise ValueError("workflow Y-review evidence scope differs")
    summary = summarize(before, after, manifest["rows"], labels["rows"])
    value = {"schema": "graphreader.workflow-y-review-score.v1", "scope": "train-diagnostic-not-acceptance",
             "previous_report_sha256": PREVIOUS_SHA, "report_sha256": args.report_sha256,
             "manifest_sha256": MANIFEST_SHA, "labels_sha256": LABEL_SHA, "protocol_sha256": PROTOCOL_SHA,
             "runtime_sha256": args.runtime_sha256, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
             "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-y-review-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
