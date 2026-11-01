# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare frozen workflow evidence after the provisional-origin repair."""
from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path

from propose_dense_tick_windows import RUN, checked_json
from score_outlying_lane import reasons
from score_workflow_tick_details import INPUT_SHA

BASELINE_SHA = "1fb3a30c5d57ecd0ec802ba4394a5077e106f81b5d2229550ca0f90bc481a909"
NO_ORIGIN = "No printed, shared-panel, expected, or manual session-1 origin is available."
FIRST_PREFIX = "The first observed column maps to session "


def summarize(previous: dict, current: dict) -> dict:
    def panels(report):
        rows = [panel for row in report["rows"] for panel in row["panels"]]
        indexed = {panel["panel_id"]: panel for panel in rows}
        if len(indexed) != len(rows):
            raise ValueError("Duplicate panel identity")
        return indexed
    before, after = panels(previous), panels(current)
    if len(before) != 21 or set(before) != set(after):
        raise ValueError("Panel inventory differs")
    counts = Counter()
    for key, old in before.items():
        new = after[key]
        old_reasons, new_reasons = reasons(old["error"]), reasons(new["error"])
        unsupported = [reason for reason in old_reasons if NO_ORIGIN in old_reasons and reason.startswith(FIRST_PREFIX)]
        counts["panels"] += 1
        counts["unsupported_first_session_claims_before"] += len(unsupported)
        counts["unsupported_first_session_claims_removed"] += sum(reason not in new_reasons for reason in unsupported)
        counts["same_other_reasons"] += [reason for reason in old_reasons if reason not in unsupported] == new_reasons
        counts["invalid_to_review"] += (old["calibration_validity"], new["calibration_validity"]) == (
            "InvalidSessionOrigin", "NeedsReview")
        counts["unchanged_validity"] += old["calibration_validity"] == new["calibration_validity"]
        counts["same_workflow_failure"] += (old["status"], old["failure_code"]) == (new["status"], new["failure_code"])
        counts["same_axis_review_flag"] += old["axis_needs_review"] == new["axis_needs_review"]
        counts["same_tick_regions"] += old["ocr_tick_regions"] == new["ocr_tick_regions"]
        counts["same_centers"] += old["centers_before_classification"] == new["centers_before_classification"]
        counts["same_accepted_markers"] += old["markers"] == new["markers"]
        counts["still_recalibration_required"] += new["failure_code"] == "WORKFLOW_RECALIBRATION_REQUIRED"
        counts["completed_panels"] += new["status"] == "completed"
    return dict(counts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    previous = checked_json(RUN / "wide-horizontal-review-v2/report.json", BASELINE_SHA)
    current = checked_json(RUN / "provisional-origin-workflow-v1/report.json", args.report_sha256)
    if (current["schema"] != "graphreader.manual-marker-workflow-capture.v1" or
            current["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            current["input_sha256"] != INPUT_SHA or current["runtime_description_sha256"] != args.runtime_sha256 or
            current["smoke"] is not False or current["detailed_capture"] is not True or
            current["wide_horizontal_model_orientation"] is not True or current["truth_read"] is not False or
            current["optimizer_steps"] != 0 or current["sealed_payload_reads"] != 0 or len(current["rows"]) != 20):
        raise ValueError("Candidate workflow scope differs")
    summary = summarize(previous, current)
    value = {"schema": "graphreader.provisional-origin-score.v1", "scope": "train-diagnostic-not-acceptance",
             "baseline_sha256": BASELINE_SHA, "report_sha256": args.report_sha256,
             "runtime_sha256": args.runtime_sha256, "source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
             "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "provisional-origin-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
