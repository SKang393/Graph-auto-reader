# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare frozen marker-stage evidence and mask provenance near train points."""

from __future__ import annotations

import argparse
import gzip
import json
import math
from pathlib import Path
import struct

import cv2
import numpy as np

from native_score import PLAN, PLAN_SHA, TRUTH, TRUTH_SHA
from patches import read_frame
from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest


def marker_values(panel: dict) -> list[tuple[float, float, float, float]]:
    return sorted((marker["center"]["x"], marker["center"]["y"], marker["radius"], marker["center_confidence"])
                  for marker in panel["markers"])


def read_attribution(path: Path, expected: str, planes: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    if digest(path) != expected:
        raise ValueError("attribution identity differs")
    _, height, width = planes.shape
    count = width * height
    expected_size = 12 + 9 * count
    with gzip.open(path, "rb") as stream:
        payload = stream.read(expected_size + 1)
    if len(payload) != expected_size or struct.unpack("<III", payload[:12]) != (0x314D5247, width, height):
        raise ValueError("attribution format differs")
    seed = np.frombuffer(payload, dtype="<f4", count=count, offset=12).reshape(height, width)
    residual = np.frombuffer(payload, dtype="<f4", count=count, offset=12 + 4 * count).reshape(height, width)
    bits = np.frombuffer(payload, dtype=np.uint8, count=count, offset=12 + 8 * count).reshape(height, width)
    if (not np.isfinite(seed).all() or not np.isfinite(residual).all() or seed.min() < 0 or residual.min() < 0 or
            seed.max() > 1 or residual.max() > 1 or np.any(bits > 15) or
            not np.array_equal(np.maximum(seed, residual), planes[2])):
        raise ValueError("artifact attribution does not reproduce the captured merged mask")
    return seed, residual, bits


def stage_points(items: list[dict], nested: bool = False) -> np.ndarray:
    values = [item["center"] if nested else item for item in items]
    points = np.array([(point["x"], point["y"]) for point in values], dtype=np.float64).reshape(-1, 2)
    if not np.isfinite(points).all():
        raise ValueError("stage has a nonfinite coordinate")
    return points


def any_near(points: np.ndarray, x: float, y: float, radius: float) -> bool:
    if not len(points):
        return False
    return bool(np.any((points[:, 0] - x) ** 2 + (points[:, 1] - y) ** 2 <= radius * radius))


def local_pattern(emitted: bool, decoded: bool, pre_nms: bool, final: bool) -> str:
    if final:
        return "final_nearby"
    if pre_nms:
        return "pre_nms_without_final_nearby"
    if decoded:
        return "decoded_without_pre_nms_nearby"
    return "emitted_without_decoded_nearby" if emitted else "no_emitted_proposal_nearby"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-sha256", required=True)
    parser.add_argument("--previous-report-sha256", required=True)
    parser.add_argument("--stage-report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    manifest = checked_json(RUN / "smoke-raster-input-v1.json", args.input_sha256)
    previous = checked_json(RUN / "smoke-capture-v1/report.json", args.previous_report_sha256)
    capture_dir = RUN / "smoke-capture-v2"
    capture = checked_json(capture_dir / "report.json", args.stage_report_sha256)
    receipt = checked_json(RECEIPT, RECEIPT_SHA)
    plan = checked_json(PLAN, PLAN_SHA)
    truth = checked_json(TRUTH, TRUTH_SHA)
    full_input = checked_json(RUN / "full-raster-input-v1.json", "f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608")
    full_score = checked_json(RUN / "full-score-v2.json", "ac02af58fb6fe1d79e07693e7c6fbe29eb750ccdd4307adc11cbafbb605643f4")
    if (manifest["scope"] != "train-only-raster-no-truth" or manifest["selection"] != "smoke" or
            previous["input_sha256"] != args.input_sha256 or capture["input_sha256"] != args.input_sha256 or
            capture["runtime_description_sha256"] != args.runtime_sha256 or capture["truth_read"] is not False or
            capture["optimizer_steps"] != 0 or truth["split"] != "train" or
            full_score["input_sha256"] != "f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608"):
        raise ValueError("frozen train scope differs")
    overlays = {row["output_sha256"]: row for row in receipt["rows"]}
    plans = {row["source_index"]: row for row in plan["source_rows"]}
    truths = {row["caseId"]: row for row in truth["rows"]}
    predictions = {row["id"]: row for row in capture["rows"]}
    prior = {row["id"]: row for row in previous["rows"]}
    full_images = {row["id"]: row for row in full_input["rows"]}
    positive_by_group: dict[int, list[bool]] = {}
    for row in full_score["rows"]:
        if row["label"] == "injected_positive_only" and row["status"] == "scored":
            source_index = overlays[full_images[row["id"]]["image_sha256"]]["source_index"]
            positive_by_group.setdefault(source_index, []).append(row["nearby_centers"] > 0)
    if (len(overlays) != 200 or len(plans) != 20 or len(truths) != 104 or len(predictions) != 20 or
            len(prior) != 20 or len(manifest["rows"]) != 20 or len(positive_by_group) != 20 or
            any(len(values) != 5 for values in positive_by_group.values())):
        raise ValueError("frozen inventory differs")
    rows = []
    seen: set[str] = set()
    for item in manifest["rows"]:
        current, old = predictions[item["id"]], prior[item["id"]]
        overlay = overlays[item["image_sha256"]]
        source = plans[overlay["source_index"]]
        actual = truths[source["case_id"]]
        if (current["status"] != "completed" or old["status"] != "completed" or
                current["image_sha256"] != item["image_sha256"] or old["image_sha256"] != item["image_sha256"] or
                source["component_sha256"] in seen or overlay["component_sha256"] != source["component_sha256"] or
                actual["projectSha256"] != source["project_sha256"] or actual["embeddedImageSha256"] != source["image_sha256"] or
                actual["studyId"] != source["study_id"] or len(current["panels"]) != 1 or len(old["panels"]) != 1):
            raise ValueError("source or panel inventory differs")
        seen.add(source["component_sha256"])
        panel, original = current["panels"][0], old["panels"][0]
        if (panel["panel_sha256"] != original["panel_sha256"] or panel["frame_sha256"] != original["frame_sha256"] or
                marker_values(panel) != marker_values(original)):
            raise ValueError("stage capture does not reproduce frozen marker output")
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        if (panel["provenance"]["source_sha256"] != item["image_sha256"] or
                panel["provenance"]["panel_to_source_matrix"] != [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1]):
            raise ValueError("panel provenance differs")
        for key in ("frame_file", "attribution_file", "stage_file"):
            if Path(panel[key]).name != panel[key]:
                raise ValueError("invalid capture file name")
        planes = read_frame(capture_dir / panel["frame_file"], panel["frame_sha256"], panel["frame_width"], panel["frame_height"])
        seed, residual, bits = read_attribution(capture_dir / panel["attribution_file"], panel["attribution_sha256"], planes)
        stage_path = capture_dir / panel["stage_file"]
        if digest(stage_path) != panel["stage_sha256"]:
            raise ValueError("stage identity differs")
        with gzip.open(stage_path, "rb") as stream:
            stage_raw = stream.read(128_000_001)
        if len(stage_raw) > 128_000_000:
            raise ValueError("stage evidence exceeds its size limit")
        stage = json.loads(stage_raw)
        counters = stage["counters"]
        if (stage["schema"] != "graphreader.manual-marker-stage-evidence.v1" or counters["operating_threshold"] != 0.1 or
                len(stage["emitted_proposals"]) != counters["emitted_proposals"] or
                len(stage["above_threshold_decoded"]) != counters["outputs_above_operating_threshold"] or
                len(stage["pre_nms_candidates"]) != counters["candidates_before_nms"] or
                len(panel["markers"]) != counters["final_candidates"]):
            raise ValueError("stage accounting differs")
        emitted = stage_points(stage["emitted_proposals"])
        decoded = stage_points(stage["above_threshold_decoded"])
        pre = stage_points(stage["pre_nms_candidates"], nested=True)
        final = stage_points(panel["markers"], nested=True)
        polygon = np.array([(point["x"], point["y"]) for point in stage["plot"]], dtype=np.float32)
        if polygon.shape != (4, 2) or not np.isfinite(polygon).all():
            raise ValueError("plot geometry differs")
        left, top, right, bottom = overlay["patch_ltrb_half_open"]
        counts: dict[str, int] = {}
        unique = list(dict.fromkeys((float(point["x"]), float(point["y"])) for point in actual["curvePoints"]))
        for x, y in unique:
            if not math.isfinite(x) or not math.isfinite(y):
                raise ValueError("nonfinite recorded point")
            if left - 5 <= x < right + 5 and top - 5 <= y < bottom + 5:
                continue
            px, py = x - crop["x"], y - crop["y"]
            if not (0 <= px < panel["frame_width"] and 0 <= py < panel["frame_height"]):
                counts["unavailable"] = counts.get("unavailable", 0) + 1
                continue
            emitted_near = any_near(emitted, px, py, 3)
            decoded_near = any_near(decoded, px, py, 5)
            pre_near = any_near(pre, px, py, 5)
            final_near = any_near(final, px, py, 5)
            category = local_pattern(emitted_near, decoded_near, pre_near, final_near)
            counts[category] = counts.get(category, 0) + 1
            if not final_near:
                distance = cv2.pointPolygonTest(polygon, (float(px), float(py)), True)
                if distance < -2:
                    counts["miss_outside_plot_plus_2px"] = counts.get("miss_outside_plot_plus_2px", 0) + 1
                if distance < -16:
                    counts["miss_outside_plot_plus_16px"] = counts.get("miss_outside_plot_plus_16px", 0) + 1
                ix, iy = round(px), round(py)
                x0, x1 = max(0, ix - 2), min(panel["frame_width"], ix + 3)
                y0, y1 = max(0, iy - 2), min(panel["frame_height"], iy + 3)
                if x0 >= x1 or y0 >= y1:
                    raise ValueError("point attribution window is outside the frame")
                if np.max(seed[y0:y1, x0:x1]) >= np.float32(0.35):
                    counts["miss_seed_mask_overlap"] = counts.get("miss_seed_mask_overlap", 0) + 1
                if np.max(residual[y0:y1, x0:x1]) >= np.float32(0.35):
                    counts["miss_residual_mask_overlap"] = counts.get("miss_residual_mask_overlap", 0) + 1
                category_bits = int(np.bitwise_or.reduce(bits[y0:y1, x0:x1].ravel()))
                for flag, name in ((1, "arrow"), (2, "bracket"), (4, "legend"), (8, "intersection")):
                    if category_bits & flag:
                        key = "miss_residual_" + name
                        counts[key] = counts.get(key, 0) + 1
        anchor_x = (left + right) / 2 - crop["x"]
        anchor_y = (top + bottom) / 2 - crop["y"]
        anchor_distance = cv2.pointPolygonTest(polygon, (float(anchor_x), float(anchor_y)), True)
        rows.append({"id": item["id"], "partition": overlay["assigned_partition"], "counts": counts,
                     "controlled_marker_hits_out_of_five": sum(positive_by_group[overlay["source_index"]]),
                     "smoke_anchor_outside_plot_plus_2px": anchor_distance < -2,
                     "smoke_anchor_outside_plot_plus_16px": anchor_distance < -16})
    totals = {}
    for partition in ("internal_train_build", "internal_train_holdout"):
        chosen = [row for row in rows if row["partition"] == partition]
        names = {key for row in chosen for key in row["counts"]}
        totals[partition] = {"images": len(chosen), "counts": {name: sum(row["counts"].get(name, 0) for row in chosen)
                                                               for name in sorted(names)},
                             "zero_of_five_groups": sum(row["controlled_marker_hits_out_of_five"] == 0 for row in chosen),
                             "zero_of_five_outside_plot_plus_2px": sum(row["controlled_marker_hits_out_of_five"] == 0 and
                                                                      row["smoke_anchor_outside_plot_plus_2px"] for row in chosen),
                             "zero_of_five_outside_plot_plus_16px": sum(row["controlled_marker_hits_out_of_five"] == 0 and
                                                                       row["smoke_anchor_outside_plot_plus_16px"] for row in chosen)}
    result = {"schema": "graphreader.manual-marker-stage-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": args.input_sha256, "previous_report_sha256": args.previous_report_sha256,
              "stage_report_sha256": args.stage_report_sha256, "runtime_description_sha256": args.runtime_sha256,
              "scorer_source_sha256": digest(Path(__file__)), "truth_sha256": TRUTH_SHA, "optimizer_steps": 0,
              "totals": totals, "rows": rows,
              "limitations": ["Local stage proximity is not a linked proposal trace or one-to-one accuracy.",
                              "Mask overlap identifies evidence at a recorded point, not its cause of rejection.",
                              "The plot for each group comes from one smoke overlay and can differ across the other overlays.",
                              "Recorded points are imperfect and unmatched native predictions are not negatives."]}
    output = RUN / "smoke-stage-score-v1.json"
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"partitions": totals, "sha256": digest(output)}))


if __name__ == "__main__":
    main()
