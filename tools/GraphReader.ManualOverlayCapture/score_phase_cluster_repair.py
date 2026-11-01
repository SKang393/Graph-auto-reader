# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare the fixed assisted phase clustering repair against its baseline."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

from score import RUN, checked_json, digest
from workflow_score import geometry

BEFORE = RUN / "assisted-phase-validation-v1/report.json"
BEFORE_SHA = "6764d4bc8445024ea9bd71ddb85ae41fbab808dbdbab64d4517a0161a4ccf848"
AFTER = RUN / "assisted-phase-cluster-repair-v1/report.json"
OUTPUT = RUN / "assisted-phase-cluster-repair-score-v1.json"
WARNING = "phase_divider_wide_cluster_needs_review"


def summarize(before: dict, after: dict) -> dict:
    previous = {row["id"]: row for row in before["rows"]}
    current = {row["id"]: row for row in after["rows"]}
    if len(previous) != 7 or set(previous) != set(current):
        raise ValueError("Assisted source inventory differs")
    counts, errors = Counter(), Counter()
    for key, old in previous.items():
        new = current[key]
        if old["image_sha256"] != new["image_sha256"] or len(old["panels"]) != 1 or len(new["panels"]) != 1:
            raise ValueError("Assisted source identity differs")
        left, right = old["panels"][0], new["panels"][0]
        if (left["panel_id"] != right["panel_id"] or left["panel_sha256"] != right["panel_sha256"] or
                left["provenance"] != right["provenance"]):
            raise ValueError("Assisted panel identity differs")
        counts["panels"] += 1
        counts["baseline_completed"] += int(left["status"] == "completed")
        counts["candidate_completed"] += int(right["status"] == "completed")
        counts["same_accepted_markers"] += int(geometry(left["markers"]) == geometry(right["markers"]))
        counts["previous_failure_now_completed"] += int(left["status"] != "completed" and right["status"] == "completed")
        counts["new_failure"] += int(left["status"] == "completed" and right["status"] != "completed")
        if left["status"] == right["status"] == "completed":
            counts["same_completed_point_geometry"] += int(sorted(left["workflow_points"], key=lambda point: (point["x"], point["y"])) ==
                                                            sorted(right["workflow_points"], key=lambda point: (point["x"], point["y"])))
        counts["baseline_output_points"] += len(left["workflow_points"])
        counts["candidate_output_points"] += len(right["workflow_points"])
        counts["wide_cluster_warning_panels"] += int(WARNING in right["workflow_warnings"])
        validation = right["phase_validation"]
        if validation is not None:
            counts["phase_observed_panels"] += 1
            counts["too_close_pairs"] += validation["too_close_pairs"]
        if right["status"] != "completed":
            errors[right.get("failure_code") or right.get("error_type") or "unknown"] += 1
    return {"counts": dict(counts), "failure_codes": dict(errors)}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    if OUTPUT.exists():
        raise FileExistsError("Phase repair score already exists")
    before, after = checked_json(BEFORE, BEFORE_SHA), checked_json(AFTER, args.report_sha256)
    if (after["scope"] != "train-only-assisted-unapproved-not-acceptance" or
            after["runtime_description_sha256"] != args.runtime_sha256 or
            after["binding_sha256"] != before["binding_sha256"] or
            after["simulated_manual_confirmation"] is not True or after["truth_read"] is not True or
            after["optimizer_steps"] != 0 or after["sealed_payload_reads"] != 0 or
            after["training_approved"] is not False):
        raise ValueError("Phase repair diagnostic scope differs")
    result = {"schema": "graphreader.assisted-phase-cluster-repair-score.v1", "scope": "train-only-diagnostic",
              "before_sha256": BEFORE_SHA, "after_sha256": args.report_sha256,
              "runtime_sha256": args.runtime_sha256, "source_sha256": digest(Path(__file__)),
              "optimizer_steps": 0, "sealed_payload_reads": 0, "production_approval": False,
              **summarize(before, after)}
    with OUTPUT.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, sort_keys=True, indent=2)
        stream.write("\n")
    print(json.dumps({"counts": result["counts"], "failure_codes": result["failure_codes"], "sha256": digest(OUTPUT)}))


if __name__ == "__main__":
    main()
