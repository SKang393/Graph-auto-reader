# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Summarize phase validation conditions on the fixed assisted train panels."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

from score import RUN, checked_json, digest
from workflow_score import geometry

BEFORE = RUN / "assisted-workflow-v1/report.json"
BEFORE_SHA = "190ea158d83837b1207cfaa8fdc3899bc3f447709f4fb71ed6f1b550030e3b2f"
AFTER = RUN / "assisted-phase-validation-v1/report.json"
OUTPUT = RUN / "assisted-phase-validation-score-v1.json"
FIELDS = ("invalid_or_duplicate_ids", "outside_plot", "too_close_pairs", "invalid_style_or_source",
          "invalid_confidence", "invalid_segment_ids", "invalid_panel_ids")


def summarize(before: dict, after: dict) -> dict:
    old = {row["id"]: row for row in before["rows"]}
    new = {row["id"]: row for row in after["rows"]}
    if len(old) != 7 or len(new) != 7 or set(old) != set(new):
        raise ValueError("Assisted source inventory differs")
    totals, failures = Counter(), Counter()
    for identifier, previous in old.items():
        current = new[identifier]
        if previous["image_sha256"] != current["image_sha256"] or len(previous["panels"]) != 1 or len(current["panels"]) != 1:
            raise ValueError("Assisted source or panel count differs")
        left, right = previous["panels"][0], current["panels"][0]
        if left["panel_id"] != right["panel_id"] or left["panel_sha256"] != right["panel_sha256"] or left["provenance"] != right["provenance"]:
            raise ValueError("Assisted panel identity differs")
        totals["panels"] += 1
        totals["same_status"] += int((left["status"], left["failure_code"], left["calibration_validity"]) ==
                                     (right["status"], right["failure_code"], right["calibration_validity"]))
        totals["same_accepted_markers"] += int(geometry(left["markers"]) == geometry(right["markers"]))
        summary = right["phase_validation"]
        if summary is not None:
            totals["phase_observed_panels"] += 1
            for field in FIELDS:
                totals[field] += summary[field]
        if right["status"] != "completed":
            failures[right.get("failure_code") or right.get("error_type") or "unknown"] += 1
            if right["failure_code"] == "PHASE_INVALID_DIVIDER_OUTPUT" and summary is not None:
                for field in FIELDS:
                    failures["invalid_divider_" + field] += int(summary[field] > 0)
    return {"totals": dict(totals), "failures": dict(failures)}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    if OUTPUT.exists():
        raise FileExistsError("Phase diagnostic score already exists")
    before, after = checked_json(BEFORE, BEFORE_SHA), checked_json(AFTER, args.report_sha256)
    if (after["scope"] != "train-only-assisted-unapproved-not-acceptance" or
            after["runtime_description_sha256"] != args.runtime_sha256 or
            after["binding_sha256"] != before["binding_sha256"] or
            after["simulated_manual_confirmation"] is not True or after["truth_read"] is not True or
            after["optimizer_steps"] != 0 or after["sealed_payload_reads"] != 0 or
            after["training_approved"] is not False):
        raise ValueError("Phase diagnostic scope differs")
    result = {"schema": "graphreader.assisted-phase-validation-score.v1", "scope": "train-only-diagnostic",
              "before_sha256": BEFORE_SHA, "after_sha256": args.report_sha256,
              "runtime_sha256": args.runtime_sha256, "source_sha256": digest(Path(__file__)),
              "optimizer_steps": 0, "sealed_payload_reads": 0, "production_approval": False,
              **summarize(before, after)}
    with OUTPUT.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, sort_keys=True, indent=2)
        stream.write("\n")
    print(json.dumps({"totals": result["totals"], "failures": result["failures"], "sha256": digest(OUTPUT)}))


if __name__ == "__main__":
    main()
