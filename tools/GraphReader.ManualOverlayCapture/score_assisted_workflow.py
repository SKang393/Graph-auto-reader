# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the fixed assisted candidate without discarding failed panels."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

from assisted_calibration_feasibility import CAPTURE, CAPTURE_SHA, PLAN, PLAN_SHA
from native_score import TRUTH, TRUTH_SHA, score_image
from score import RUN, checked_json, digest
from workflow_score import CLASSIFIER_SHA, INPUT_SHA, geometry

BINDING = RUN / "assisted-calibration-bindings-v1.json"
BINDING_SHA = "e092e6c352b3bd25ccf59debd72c6df60a5bfe2daf0b6a9b2384f0a8ad19cb9c"
REPORT = RUN / "assisted-workflow-v1/report.json"
OUTPUT = RUN / "assisted-workflow-score-v1.json"


def score_points(points: list[dict], panels: list[dict], field: str) -> dict:
    converted = []
    for panel in panels:
        if field == "workflow_points":
            markers = [{"center": {"x": point["x"], "y": point["y"]}} for point in panel[field]]
        else:
            markers = panel[field]
        converted.append({**panel, "markers": markers})
    return score_image(points, converted, None)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    if OUTPUT.exists():
        raise FileExistsError("Assisted score already exists")
    binding, baseline, plan, truth, report = (
        checked_json(BINDING, BINDING_SHA), checked_json(CAPTURE, CAPTURE_SHA), checked_json(PLAN, PLAN_SHA),
        checked_json(TRUTH, TRUTH_SHA), checked_json(REPORT, args.report_sha256))
    if (binding["scope"] != "train-only-unapproved" or binding["production_approval"] is not False or
            binding["recorded_anchors_are_not_user_confirmation"] is not True or len(binding["rows"]) != 7 or
            truth["split"] != "train" or report["scope"] != "train-only-assisted-unapproved-not-acceptance" or
            report["input_sha256"] != INPUT_SHA or report["runtime_description_sha256"] != args.runtime_sha256 or
            report["binding_sha256"] != BINDING_SHA or report["classifier_sha256"] != CLASSIFIER_SHA or
            report["simulated_manual_confirmation"] is not True or report["truth_read"] is not True or
            report["optimizer_steps"] != 0 or report["sealed_payload_reads"] != 0 or
            report["training_approved"] is not False or len(report["rows"]) != 7):
        raise ValueError("Assisted diagnostic scope differs")
    sources = {row["source_index"]: row for row in plan["source_rows"]}
    truths = {row["caseId"]: row for row in truth["rows"]}
    before = {row["id"]: row for row in baseline["rows"]}
    after = {row["id"]: row for row in report["rows"]}
    if len(sources) != 20 or len(truths) != 104 or len(before) != 20 or len(after) != 7:
        raise ValueError("Frozen input inventory differs")
    totals, errors = Counter(), Counter()
    for bound in binding["rows"]:
        identifier = bound["id"]
        source = sources[int(identifier.removeprefix("source-"))]
        actual, old, new = truths[source["case_id"]], before[identifier], after[identifier]
        if (actual["projectSha256"] != source["project_sha256"] or
                actual["embeddedImageSha256"] != source["image_sha256"] or
                actual["studyId"] != source["study_id"] or
                old["image_sha256"] != bound["source_sha256"] or new["image_sha256"] != bound["source_sha256"]):
            raise ValueError("Assisted source identity differs")
        old_panels = [panel for panel in old["panels"] if panel["panel_id"] == bound["panel_id"]]
        new_panels = [panel for panel in new["panels"] if panel["panel_id"] == bound["panel_id"]]
        if len(old_panels) != 1 or len(new_panels) > 1:
            raise ValueError("Assisted panel identity differs")
        for panel in [*old_panels, *new_panels]:
            if panel["panel_sha256"] != bound["panel_sha256"] or panel["provenance"]["source_sha256"] != bound["source_sha256"]:
                raise ValueError("Assisted panel image differs")
        totals["images"] += 1
        totals["completed_images"] += int(new["status"] == "completed")
        totals["captured_panels"] += len(new_panels)
        totals["completed_panels"] += sum(panel["status"] == "completed" for panel in new_panels)
        if new["status"] != "completed":
            errors[(new_panels[0].get("failure_code") or new_panels[0].get("error_type") or "unknown")
                   if new_panels else new.get("error_type") or "source_failed"] += 1
        if new_panels and new_panels[0]["marker_stage_reached"]:
            totals["identical_marker_panels"] += int(geometry(old_panels[0]["markers"]) == geometry(new_panels[0]["markers"]))
        for prefix, panels, field in (("baseline", old_panels, "markers"),
                                      ("assisted", new_panels, "markers"),
                                      ("workflow", new_panels, "workflow_points")):
            score = score_points(actual["curvePoints"], panels, field)
            totals[prefix + "_unique_points"] += score["raw_points"] - score["exact_duplicate_points"]
            totals[prefix + "_unavailable_points"] += score["unavailable_points"]
            totals[prefix + "_matches"] += score["matched_points"]
        totals["workflow_output_points"] += sum(len(panel["workflow_points"]) for panel in new_panels)
    if totals["images"] != 7 or not (totals["baseline_unique_points"] == totals["assisted_unique_points"] == totals["workflow_unique_points"]):
        raise ValueError("Assisted scoring denominator differs")
    result = {"schema": "graphreader.assisted-workflow-score.v1", "scope": "train-only-assisted-not-acceptance",
              "report_sha256": args.report_sha256, "runtime_sha256": args.runtime_sha256,
              "binding_sha256": BINDING_SHA, "baseline_sha256": CAPTURE_SHA, "truth_sha256": TRUTH_SHA,
              "source_sha256": digest(Path(__file__)), "totals": dict(totals), "failure_codes": dict(errors),
              "optimizer_steps": 0, "sealed_payload_reads": 0, "production_approval": False}
    with OUTPUT.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, sort_keys=True, indent=2)
        stream.write("\n")
    print(json.dumps({"totals": result["totals"], "failure_codes": result["failure_codes"], "sha256": digest(OUTPUT)}))


if __name__ == "__main__":
    main()
