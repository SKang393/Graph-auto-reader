# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Diagnose candidate confidence on fixed train-point neighborhoods; no optimization."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort

from native_score import PLAN, PLAN_SHA, TRUTH, TRUTH_SHA
from patches import lattice_near, proposal_patch, read_frame
from score import RECEIPT, RECEIPT_SHA, ROOT, RUN, checked_json, digest

MODEL = ROOT / "artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/marker-center-component-diversity-v27-p1.onnx"
MODEL_SHA = "4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04"


def session() -> ort.InferenceSession:
    if digest(MODEL) != MODEL_SHA:
        raise ValueError("frozen model differs")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    result = ort.InferenceSession(str(MODEL), sess_options=options, providers=["CPUExecutionProvider"])
    inputs, outputs = result.get_inputs(), result.get_outputs()
    if (result.get_providers() != ["CPUExecutionProvider"] or len(inputs) != 1 or len(outputs) != 1 or
            inputs[0].name != "candidate_patches" or inputs[0].shape[1:] != [3, 33, 33] or
            outputs[0].name != "candidate_predictions" or outputs[0].shape[1:] != [4]):
        raise ValueError("candidate inference contract differs")
    return result


def categories(max_probability: float | None, final_nearby: bool) -> str:
    if max_probability is None:
        return "no_supported_local_proposal"
    if not math.isfinite(max_probability) or not 0 <= max_probability <= 1:
        raise ValueError("invalid candidate probability")
    return ("at_or_above_threshold" if max_probability >= 0.1 else "below_threshold") + (
        "_final_nearby" if final_nearby else "_no_final_nearby")


def transition(original: float, ablated: float) -> str:
    if not all(math.isfinite(value) and 0 <= value <= 1 for value in (original, ablated)):
        raise ValueError("invalid candidate probability")
    if original < 0.1 <= ablated:
        return "crossed_up"
    if ablated < 0.1 <= original:
        return "crossed_down"
    return "unchanged_side"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-sha256", required=True)
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    manifest = checked_json(RUN / "smoke-raster-input-v1.json", args.input_sha256)
    capture = checked_json(RUN / "smoke-capture-v1/report.json", args.report_sha256)
    receipt = checked_json(RECEIPT, RECEIPT_SHA)
    plan = checked_json(PLAN, PLAN_SHA)
    truth = checked_json(TRUTH, TRUTH_SHA)
    if (manifest["scope"] != "train-only-raster-no-truth" or manifest["selection"] != "smoke" or
            manifest["receipt_sha256"] != RECEIPT_SHA or capture["input_sha256"] != args.input_sha256 or
            capture["runtime_description_sha256"] != args.runtime_sha256 or capture["truth_read"] is not False or
            capture["optimizer_steps"] != 0 or truth["split"] != "train"):
        raise ValueError("frozen train scope differs")
    overlays = {row["output_sha256"]: row for row in receipt["rows"]}
    plans = {row["source_index"]: row for row in plan["source_rows"]}
    truths = {row["caseId"]: row for row in truth["rows"]}
    predictions = {row["id"]: row for row in capture["rows"]}
    if len(overlays) != 200 or len(plans) != 20 or len(truths) != 104 or len(predictions) != 20 or len(manifest["rows"]) != 20:
        raise ValueError("frozen inventory differs")
    runtime = session()
    rows = []
    seen: set[str] = set()
    for item in manifest["rows"]:
        prediction = predictions[item["id"]]
        overlay = overlays[item["image_sha256"]]
        source = plans[overlay["source_index"]]
        actual = truths[source["case_id"]]
        if (prediction["image_sha256"] != item["image_sha256"] or overlay["component_sha256"] != source["component_sha256"] or
                source["component_sha256"] in seen or actual["projectSha256"] != source["project_sha256"] or
                actual["embeddedImageSha256"] != source["image_sha256"] or actual["studyId"] != source["study_id"]):
            raise ValueError("train source identity differs")
        seen.add(source["component_sha256"])
        counts: dict[str, int] = {}
        ablation: dict[str, int] = {}
        eligible = excluded = unavailable = 0
        if prediction["status"] != "completed":
            rows.append({"id": item["id"], "partition": overlay["assigned_partition"], "status": "capture_failed"})
            continue
        frames = {}
        for panel in prediction["panels"]:
            provenance = panel["provenance"]
            crop = provenance["encoded_crop_source_pixels"]
            name = panel["frame_file"]
            if (provenance["source_sha256"] != item["image_sha256"] or Path(name).name != name or
                    provenance["panel_to_source_matrix"] != [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1]):
                raise ValueError("captured panel identity differs")
            frames[name] = read_frame(RUN / "smoke-capture-v1" / name, panel["frame_sha256"],
                                      panel["frame_width"], panel["frame_height"])
        patch_left, patch_top, patch_right, patch_bottom = overlay["patch_ltrb_half_open"]
        points = list(dict.fromkeys((float(point["x"]), float(point["y"])) for point in actual["curvePoints"]))
        for x, y in points:
            if not math.isfinite(x) or not math.isfinite(y):
                raise ValueError("nonfinite recorded point")
            # Any 33px patch on a lattice point at most three pixels from this
            # target must avoid the modified rectangle entirely.
            if patch_left - 19 <= x < patch_right + 19 and patch_top - 19 <= y < patch_bottom + 19:
                excluded += 1
                continue
            matching = []
            for panel in prediction["panels"]:
                crop = panel["provenance"]["encoded_crop_source_pixels"]
                if crop["x"] <= x < crop["x"] + crop["width"] and crop["y"] <= y < crop["y"] + crop["height"]:
                    matching.append(panel)
            if len(matching) != 1:
                unavailable += 1
                continue
            panel = matching[0]
            crop = panel["provenance"]["encoded_crop_source_pixels"]
            planes = frames[panel["frame_file"]]
            local_x, local_y = x - crop["x"], y - crop["y"]
            patches = [candidate for gx, gy in lattice_near(local_x, local_y, panel["frame_width"], panel["frame_height"])
                       if (candidate := proposal_patch(planes, gx, gy)) is not None]
            maximum = None
            ablated_maximum = None
            if patches:
                tensor = np.stack(patches).astype(np.float32, copy=False)
                zero_artifact = tensor.copy()
                zero_artifact[:, 2] = 0
                output = runtime.run(["candidate_predictions"], {"candidate_patches": np.concatenate((tensor, zero_artifact))})[0]
                if output.shape != (2 * len(patches), 4) or not np.isfinite(output).all():
                    raise ValueError("candidate inference output differs")
                maximum = float(output[:len(patches), 0].max())
                ablated_maximum = float(output[len(patches):, 0].max())
            final_nearby = any(math.hypot(marker["center"]["x"] - local_x, marker["center"]["y"] - local_y) <= 5
                               for marker in panel["markers"])
            category = categories(maximum, final_nearby)
            counts[category] = counts.get(category, 0) + 1
            if maximum is not None and ablated_maximum is not None:
                key = transition(maximum, ablated_maximum)
                ablation[key] = ablation.get(key, 0) + 1
                if np.max(tensor[:, 2]) >= np.float32(0.35):
                    ablation["artifact_present_in_patch"] = ablation.get("artifact_present_in_patch", 0) + 1
                    if key == "crossed_up" and not final_nearby:
                        ablation["crossed_up_no_final_nearby_with_artifact"] = ablation.get("crossed_up_no_final_nearby_with_artifact", 0) + 1
            eligible += 1
        rows.append({"id": item["id"], "partition": overlay["assigned_partition"], "status": "scored",
                     "eligible_points": eligible, "patch_excluded": excluded, "unavailable_points": unavailable,
                     "counts": counts, "artifact_ablation": ablation})
    totals = {}
    for partition in ("internal_train_build", "internal_train_holdout"):
        chosen = [row for row in rows if row["partition"] == partition]
        scored = [row for row in chosen if row["status"] == "scored"]
        names = {key for row in scored for key in row["counts"]}
        ablation_names = {key for row in scored for key in row["artifact_ablation"]}
        totals[partition] = {"images": len(chosen), "scored_images": len(scored),
                             "eligible_points": sum(row["eligible_points"] for row in scored),
                             "patch_excluded": sum(row["patch_excluded"] for row in scored),
                             "unavailable_points": sum(row["unavailable_points"] for row in scored),
                             "counts": {name: sum(row["counts"].get(name, 0) for row in scored) for name in sorted(names)},
                             "artifact_ablation": {name: sum(row["artifact_ablation"].get(name, 0) for row in scored)
                                                   for name in sorted(ablation_names)}}
    result = {"schema": "graphreader.manual-marker-local-confidence.v2", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": args.input_sha256, "report_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256, "model_sha256": MODEL_SHA,
              "scorer_source_sha256": digest(Path(__file__)), "provider": "CPUExecutionProvider", "optimizer_steps": 0,
              "local_proposal_radius_pixels": 3, "final_nearby_radius_pixels": 5, "operating_threshold": 0.1,
              "totals": totals, "rows": rows,
              "limitations": ["Grid proposals are not checked against the runtime plot domain.",
                              "A nearby final marker is not one-to-one matched and can come from a different grid point.",
                              "Zeroing the artifact channel is a counterfactual raw-model probe, not a workflow or precision test.",
                              "Recorded points are imperfect; target patches near modified image regions are excluded.",
                              "This does not measure full-image precision or production acceptance."]}
    output = RUN / "smoke-local-confidence-v2.json"
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"partitions": totals, "sha256": digest(output)}))


if __name__ == "__main__":
    main()
