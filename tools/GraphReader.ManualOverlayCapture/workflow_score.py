# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score a frozen train-only candidate workflow without dropping failures."""

from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

from native_score import PLAN, PLAN_SHA, TRUTH, TRUTH_SHA, score_image
from prepare_input import ORIGINALS, ORIGINAL_RECEIPT_SHA
from score import RUN, checked_json, digest

INPUT_SHA = "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6"
REFERENCE_SHA = "27d85db95b09e2c79f021de5d9cb4a65ac69c3fc82763d7b4656bdcaca28bfa2"
CLASSIFIER_SHA = "26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a"


def geometry(markers: list[dict]) -> list[tuple]:
    return sorted((item["center"]["x"], item["center"]["y"], item["radius"],
                   item["center_confidence"], item["artifact_probability"]) for item in markers)


def score_workflow_image(points: list[dict], panels: list[dict]) -> dict:
    before = [{**panel, "markers": panel["centers_before_classification"]} for panel in panels]
    initial = score_image(points, before, None)
    accepted = score_image(points, panels, None)
    if (initial["raw_points"], initial["exact_duplicate_points"], initial["unavailable_points"]) != (
            accepted["raw_points"], accepted["exact_duplicate_points"], accepted["unavailable_points"]):
        raise ValueError("workflow scoring denominator differs between stages")
    return {
        "unique_recorded_points": initial["raw_points"] - initial["exact_duplicate_points"],
        "unavailable_points": initial["unavailable_points"],
        "center_matches": initial["matched_points"],
        "accepted_matches": accepted["matched_points"],
        "centers": sum(len(panel["centers_before_classification"]) for panel in panels),
        "classified_plot": sum(len(panel["classified_plot"]) for panel in panels),
        "accepted": sum(len(panel["markers"]) for panel in panels),
        "workflow_points": sum(len(panel["workflow_points"]) for panel in panels),
        "panels": len(panels),
        "marker_stage_panels": sum(panel["marker_stage_reached"] is True for panel in panels),
        "completed_panels": sum(panel["status"] == "completed" for panel in panels),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    parser.add_argument("--capture-version", choices=("v1", "v2", "v3", "v4", "v5", "v6"), default="v1")
    parser.add_argument("--dense-experiment", action="store_true")
    args = parser.parse_args()
    manifest = checked_json(RUN / "original-raster-input-v1.json", INPUT_SHA)
    capture = (f"dense-workflow-full-{args.capture_version}" if args.dense_experiment
               else f"original-workflow-full-{args.capture_version}")
    workflow = checked_json(RUN / capture / "report.json", args.report_sha256)
    reference = checked_json(RUN / "original-capture-v3/report.json", REFERENCE_SHA)
    receipt = checked_json(ORIGINALS / "source-receipt.json", ORIGINAL_RECEIPT_SHA)
    plan = checked_json(PLAN, PLAN_SHA)
    truth = checked_json(TRUTH, TRUTH_SHA)
    if (manifest["scope"] != "train-only-original-raster-no-truth" or manifest["selection"] != "original" or
            manifest["receipt_sha256"] != ORIGINAL_RECEIPT_SHA or
            workflow["schema"] != "graphreader.manual-marker-workflow-capture.v1" or
            workflow["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            workflow.get("dense_tick_separation", False) is not args.dense_experiment or
            workflow["input_sha256"] != INPUT_SHA or workflow["runtime_description_sha256"] != args.runtime_sha256 or
            workflow["classifier_sha256"] != CLASSIFIER_SHA or workflow["smoke"] is not False or
            workflow["classification_requested"] is not True or workflow["truth_read"] is not False or
            workflow["optimizer_steps"] != 0 or workflow["sealed_payload_reads"] != 0 or
            workflow["training_approved"] is not False or reference["input_sha256"] != INPUT_SHA or
            reference["truth_read"] is not False or truth["split"] != "train"):
        raise ValueError("frozen workflow scope differs")
    sources = {row["source_index"]: row for row in receipt["sources"]}
    plans = {row["source_index"]: row for row in plan["source_rows"]}
    truths = {row["caseId"]: row for row in truth["rows"]}
    predictions = {row["id"]: row for row in workflow["rows"]}
    references = {row["id"]: row for row in reference["rows"]}
    if (len(sources) != 20 or len(plans) != 20 or len(truths) != 104 or len(manifest["rows"]) != 20 or
            len(predictions) != 20 or len(references) != 20 or len(workflow["rows"]) != 20):
        raise ValueError("frozen workflow inventory differs")
    rows = []
    errors: Counter[str] = Counter()
    for index, item in enumerate(manifest["rows"]):
        if item["id"] != f"source-{index:03d}":
            raise ValueError("original source order differs")
        prediction, prior = predictions[item["id"]], references[item["id"]]
        original, source = sources[index], plans[index]
        actual = truths[source["case_id"]]
        if (prediction["image_sha256"] != item["image_sha256"] or prior["image_sha256"] != item["image_sha256"] or
                original["extracted_png_sha256"] != item["image_sha256"] or
                original["component_sha256"] != source["component_sha256"] or
                actual["projectSha256"] != source["project_sha256"] or
                actual["embeddedImageSha256"] != source["image_sha256"] or actual["studyId"] != source["study_id"]):
            raise ValueError("workflow source identity differs")
        panels = prediction["panels"]
        prior_panels = {panel["panel_id"]: panel for panel in prior["panels"]}
        identical = 0
        for panel in panels:
            if panel["provenance"]["source_sha256"] != item["image_sha256"]:
                raise ValueError("workflow panel source differs")
            previous = prior_panels.get(panel["panel_id"])
            if (previous is not None and previous["panel_sha256"] == panel["panel_sha256"] and
                    previous["provenance"] == panel["provenance"] and panel["marker_stage_reached"] and
                    geometry(previous["markers"]) == geometry(panel["centers_before_classification"])):
                identical += 1
            if panel["status"] != "completed":
                errors[panel.get("failure_code") or panel.get("error_type") or "unknown"] += 1
        if prediction["status"] != "completed" and (prediction.get("error_type") or not panels):
            errors[prediction.get("error_type") or "source_failed"] += 1
        rows.append({"id": item["id"], "partition": original["assigned_partition"],
                     "status": prediction["status"], "identical_center_panels": identical,
                     **score_workflow_image(actual["curvePoints"], panels)})
    totals = {}
    for partition in ("internal_train_build", "internal_train_holdout"):
        selected = [row for row in rows if row["partition"] == partition]
        fields = ("unique_recorded_points", "unavailable_points", "center_matches", "accepted_matches", "centers",
                  "classified_plot", "accepted", "workflow_points", "panels", "marker_stage_panels",
                  "completed_panels", "identical_center_panels")
        totals[partition] = {"images": len(selected),
                             "completed_images": sum(row["status"] == "completed" for row in selected),
                             **{field: sum(row[field] for row in selected) for field in fields}}
    result = {"schema": "graphreader.manual-marker-workflow-score.v1", "scope": "train-diagnostic-not-acceptance",
              "report_sha256": args.report_sha256, "runtime_description_sha256": args.runtime_sha256,
              "reference_sha256": REFERENCE_SHA, "truth_sha256": TRUTH_SHA,
              "source_sha256": digest(Path(__file__)), "optimizer_steps": 0, "totals": totals,
              "failure_codes": dict(sorted(errors.items())), "rows": rows,
              "limitations": ["Incomplete recorded points cannot establish prediction precision.",
                              "Failed or missing panels retain their unique recorded-point denominator.",
                              "Marker-stage observations can exist even if the full workflow failed later.",
                              "This train diagnostic does not approve the candidate or establish release acceptance."]}
    output = RUN / (f"dense-workflow-score-{args.capture_version}.json" if args.dense_experiment
                    else f"original-workflow-score-{args.capture_version}.json")
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"partitions": totals, "failure_codes": result["failure_codes"], "sha256": digest(output)}))


if __name__ == "__main__":
    main()
