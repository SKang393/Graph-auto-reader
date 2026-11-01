# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare the candidate's review signals without exposing region text."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import INPUT_SHA, reasons
from score_wide_horizontal_candidate import BASELINE_SHA
from score_workflow_y_review import MANIFEST_SHA

PRIOR_SHA = "14ea74095ea0f368ab8996f15186cb0559438b17e9af9ef6e787f0bf28df1247"
OVERRIDE = "ocr_tick_sequence_needs_review:"


def summarize(baseline: dict, prior: dict, current: dict, manifest: list[dict]) -> dict:
    def panels(report: dict) -> dict:
        rows = [panel for row in report["rows"] for panel in row["panels"]]
        indexed = {panel["panel_id"]: panel for panel in rows}
        if len(indexed) != len(rows):
            raise ValueError("duplicate panel")
        return indexed

    old, before, after = panels(baseline), panels(prior), panels(current)
    if len(old) != 21 or set(old) != set(before) or set(old) != set(after):
        raise ValueError("panel inventory differs")
    counts = Counter()
    for key, panel in after.items():
        previous = before[key]
        old_reasons, new_reasons = reasons(previous["error"]), reasons(panel["error"])
        overrides = [r for r in new_reasons if r.startswith(OVERRIDE) and ":orientation_override:" in r]
        counts["panels"] += 1
        counts["panels_with_orientation_review"] += bool(overrides)
        counts["orientation_review_warnings"] += len(overrides)
        counts["same_other_failure_reasons"] += old_reasons == [r for r in new_reasons if r not in overrides]
        counts["same_status_and_validity"] += (previous["status"], previous["failure_code"], previous["calibration_validity"]) == (
            panel["status"], panel["failure_code"], panel["calibration_validity"])
        counts["same_tick_regions"] += previous["ocr_tick_regions"] == panel["ocr_tick_regions"]
        counts["same_center_observations"] += previous["centers_before_classification"] == panel["centers_before_classification"]
        counts["same_accepted_markers"] += previous["markers"] == panel["markers"]
        counts["recalibration_required"] += panel["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
    if len({item["id"] for item in manifest}) != len(manifest):
        raise ValueError("duplicate review identity")
    for item in manifest:
        region_id = item["region_id"]
        def has_review(panel: dict) -> bool:
            return any(region_id in reason and "needs_review" in reason for reason in reasons(panel["error"]))
        previous_warning = has_review(old[item["panel_id"]])
        candidate_warning = has_review(before[item["panel_id"]])
        current_warning = has_review(after[item["panel_id"]])
        counts["reviewed_regions"] += 1
        counts["review_warning_lost_by_prior_candidate"] += previous_warning and not candidate_warning
        counts["lost_warning_replaced_by_current_candidate"] += previous_warning and not candidate_warning and current_warning
        counts["current_lost_region_warning"] += previous_warning and not current_warning
    return dict(counts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    baseline = checked_json(RUN / "original-workflow-full-v7/report.json", BASELINE_SHA)
    prior = checked_json(RUN / "wide-horizontal-candidate-v1/report.json", PRIOR_SHA)
    current = checked_json(RUN / "wide-horizontal-review-v2/report.json", args.report_sha256)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    if (current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["smoke"] is not False or current["detailed_capture"] is not True or
            current["wide_horizontal_model_orientation"] is not True or current["truth_read"] is not False or
            current["optimizer_steps"] != 0 or current["sealed_payload_reads"] != 0 or
            len(current["rows"]) != 20 or len(manifest["rows"]) != 115):
        raise ValueError("candidate capture scope differs")
    summary = summarize(baseline, prior, current, manifest["rows"])
    result = {"schema": "graphreader.orientation-review-score.v1", "scope": "train-diagnostic-not-acceptance",
              "baseline_sha256": BASELINE_SHA, "prior_sha256": PRIOR_SHA, "report_sha256": args.report_sha256,
              "runtime_sha256": args.runtime_sha256, "manifest_sha256": MANIFEST_SHA,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "orientation-review-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
