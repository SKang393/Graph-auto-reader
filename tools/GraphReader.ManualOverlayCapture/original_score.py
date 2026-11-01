# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare frozen original-image marker centers with recorded train points."""

from __future__ import annotations

import argparse
import gzip
import json
from pathlib import Path

import cv2
import numpy as np

from native_score import PLAN, PLAN_SHA, TRUTH, TRUTH_SHA, score_image
from patches import read_frame
from prepare_input import ORIGINAL_RECEIPT_SHA, ORIGINALS
from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest
from stage_score import read_attribution


def anchor_plot_status(panels: list[dict], stages: dict[str, dict], x: float, y: float) -> str:
    matching = []
    for panel in panels:
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        if crop["x"] <= x < crop["x"] + crop["width"] and crop["y"] <= y < crop["y"] + crop["height"]:
            matching.append(panel)
    if len(matching) != 1:
        return "ambiguous_or_missing_panel"
    panel = matching[0]
    crop = panel["provenance"]["encoded_crop_source_pixels"]
    plot = stages[panel["stage_file"]]["plot"]
    polygon = np.array([(point["x"], point["y"]) for point in plot], dtype=np.float32)
    if polygon.shape != (4, 2) or not np.isfinite(polygon).all():
        raise ValueError("plot polygon differs")
    distance = cv2.pointPolygonTest(polygon, (float(x - crop["x"]), float(y - crop["y"])), True)
    if distance < -16:
        return "outside_plot_plus_16px"
    if distance < -2:
        return "outside_plot_plus_2px"
    return "inside_plot_plus_2px"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-sha256", required=True)
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    parser.add_argument("--capture-version", choices=("v1", "v2", "v3"), default="v1")
    args = parser.parse_args()
    manifest = checked_json(RUN / "original-raster-input-v1.json", args.input_sha256)
    capture_dir = RUN / f"original-capture-{args.capture_version}"
    capture = checked_json(capture_dir / "report.json", args.report_sha256)
    original_receipt = checked_json(ORIGINALS / "source-receipt.json", ORIGINAL_RECEIPT_SHA)
    overlay_receipt = checked_json(RECEIPT, RECEIPT_SHA)
    plan = checked_json(PLAN, PLAN_SHA)
    truth = checked_json(TRUTH, TRUTH_SHA)
    smoke_manifest = checked_json(RUN / "smoke-raster-input-v1.json", "28f6480b72126ad8b1598cab758a6484a4ac2071daa12fc8986e47ef6ae6510f")
    smoke_score = checked_json(RUN / "smoke-native-score-v2.json", "7773a19d4a345b58fddcd45e9be040112648d6e4d272b7b0641021d9fa24240a")
    smoke_stage = checked_json(RUN / "smoke-stage-score-v1.json", "69031b8aeadc193c43a6a7a7891ffefe35add5daa977ffdeb10ebffdf326fc1e")
    if (manifest["scope"] != "train-only-original-raster-no-truth" or manifest["selection"] != "original" or
            manifest["receipt_sha256"] != ORIGINAL_RECEIPT_SHA or capture["input_sha256"] != args.input_sha256 or
            capture["runtime_description_sha256"] != args.runtime_sha256 or capture["truth_read"] is not False or
            capture["optimizer_steps"] != 0 or truth["split"] != "train"):
        raise ValueError("frozen train scope differs")
    original_sources = {row["source_index"]: row for row in original_receipt["sources"]}
    overlays = {row["output_sha256"]: row for row in overlay_receipt["rows"]}
    plans = {row["source_index"]: row for row in plan["source_rows"]}
    truths = {row["caseId"]: row for row in truth["rows"]}
    predictions = {row["id"]: row for row in capture["rows"]}
    smoke_images = {row["id"]: row for row in smoke_manifest["rows"]}
    smoke_by_id = {row["id"]: row for row in smoke_score["rows"]}
    stage_by_id = {row["id"]: row for row in smoke_stage["rows"]}
    smoke_by_group = {overlays[row["image_sha256"]]["source_index"]: row["id"] for row in smoke_manifest["rows"]}
    if (len(original_sources) != 20 or len(overlays) != 200 or len(plans) != 20 or len(truths) != 104 or
            len(predictions) != 20 or len(smoke_by_group) != 20 or len(manifest["rows"]) != 20):
        raise ValueError("frozen inventory differs")
    rows = []
    for index, item in enumerate(manifest["rows"]):
        if item["id"] != f"source-{index:03d}":
            raise ValueError("original source order differs")
        prediction = predictions[item["id"]]
        source, original = plans[index], original_sources[index]
        actual = truths[source["case_id"]]
        smoke_id = smoke_by_group[index]
        overlay = overlays[smoke_images[smoke_id]["image_sha256"]]
        prior, prior_stage = smoke_by_id[smoke_id], stage_by_id[smoke_id]
        if (prediction["image_sha256"] != item["image_sha256"] or original["extracted_png_sha256"] != item["image_sha256"] or
                original["component_sha256"] != source["component_sha256"] or actual["projectSha256"] != source["project_sha256"] or
                actual["embeddedImageSha256"] != source["image_sha256"] or actual["studyId"] != source["study_id"] or
                overlay["component_sha256"] != source["component_sha256"] or prior["status"] != "scored"):
            raise ValueError("source identity differs")
        base = {"id": item["id"], "partition": original["assigned_partition"]}
        if prediction["status"] != "completed":
            rows.append({**base, "status": "capture_failed"})
            continue
        frames = {}
        stages = {}
        for panel in prediction["panels"]:
            if panel["provenance"]["source_sha256"] != item["image_sha256"]:
                raise ValueError("panel source identity differs")
            for key in ("frame_file", "attribution_file", "stage_file"):
                if Path(panel[key]).name != panel[key]:
                    raise ValueError("invalid capture file name")
            planes = read_frame(capture_dir / panel["frame_file"], panel["frame_sha256"], panel["frame_width"], panel["frame_height"])
            read_attribution(capture_dir / panel["attribution_file"], panel["attribution_sha256"], planes)
            frames[panel["frame_file"]] = planes
            stage_path = capture_dir / panel["stage_file"]
            if digest(stage_path) != panel["stage_sha256"]:
                raise ValueError("stage identity differs")
            with gzip.open(stage_path, "rb") as stream:
                payload = stream.read(128_000_001)
            if len(payload) > 128_000_000:
                raise ValueError("stage evidence exceeds its size limit")
            stage = json.loads(payload)
            if stage["schema"] != "graphreader.manual-marker-stage-evidence.v1":
                raise ValueError("stage schema differs")
            stages[panel["stage_file"]] = stage
        points = actual["curvePoints"]
        all_points = score_image(points, prediction["panels"], None, frames)
        same_points = score_image(points, prediction["panels"], overlay["patch_ltrb_half_open"], frames)
        left, top, right, bottom = overlay["patch_ltrb_half_open"]
        anchor_status = anchor_plot_status(prediction["panels"], stages, (left + right) / 2, (top + bottom) / 2)
        rows.append({**base, "status": "scored", "panels": len(prediction["panels"]),
                     "all_points": all_points, "same_points_as_overlay": same_points,
                     "overlay_eligible_points": prior["eligible_points"], "overlay_matched_points": prior["matched_points"],
                     "controlled_marker_hits_out_of_five": prior_stage["controlled_marker_hits_out_of_five"],
                     "overlay_anchor_outside_plot_plus_16px": prior_stage["smoke_anchor_outside_plot_plus_16px"],
                     "original_anchor_plot_status": anchor_status})
    totals = {}
    for partition in ("internal_train_build", "internal_train_holdout"):
        chosen = [row for row in rows if row["partition"] == partition]
        scored = [row for row in chosen if row["status"] == "scored"]
        totals[partition] = {"images": len(chosen), "scored_images": len(scored),
                             "multi_panel_images": sum(row["panels"] > 1 for row in scored),
                             "all_eligible_points": sum(row["all_points"]["eligible_points"] for row in scored),
                             "all_matched_points": sum(row["all_points"]["matched_points"] for row in scored),
                             "same_eligible_original": sum(row["same_points_as_overlay"]["eligible_points"] for row in scored),
                             "same_matched_original": sum(row["same_points_as_overlay"]["matched_points"] for row in scored),
                             "same_eligible_overlay": sum(row["overlay_eligible_points"] for row in scored),
                             "same_matched_overlay": sum(row["overlay_matched_points"] for row in scored),
                             "zero_of_five_groups": sum(row["controlled_marker_hits_out_of_five"] == 0 for row in scored),
                             "zero_of_five_original_outside_plus_16px": sum(row["controlled_marker_hits_out_of_five"] == 0 and
                                                                              row["original_anchor_plot_status"] == "outside_plot_plus_16px" for row in scored),
                             "zero_of_five_original_ambiguous_panel": sum(row["controlled_marker_hits_out_of_five"] == 0 and
                                                                           row["original_anchor_plot_status"] == "ambiguous_or_missing_panel" for row in scored)}
    result = {"schema": "graphreader.manual-marker-original-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": args.input_sha256, "report_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256, "truth_sha256": TRUTH_SHA,
              "source_sha256": digest(Path(__file__)), "optimizer_steps": 0, "totals": totals, "rows": rows,
              "limitations": ["Recorded points are imperfect and need not include every visible marker or series.",
                              "Unmatched predictions are unknown, so full-image precision is not measured.",
                              "Source groups and earlier diagnostics are not independent release acceptance.",
                              "The result measures center candidates, not downstream classification and recovery."]}
    output = RUN / f"original-native-score-{args.capture_version}.json"
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"partitions": totals, "sha256": digest(output)}))


if __name__ == "__main__":
    main()
