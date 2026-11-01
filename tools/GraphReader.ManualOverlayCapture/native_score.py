# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare frozen train-image predictions with recorded unaltered curve points."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np

from patches import read_frame
from score import RECEIPT, RECEIPT_SHA, ROOT, RUN, checked_json, digest

PLAN = ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7/plan.json"
PLAN_SHA = "34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428"
TRUTH = ROOT / "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json"
TRUTH_SHA = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"


def matched_point_indices(points: list[tuple[float, float]], markers: list[tuple[float, float]], tolerance: float = 5) -> set[int]:
    """Return one-to-one matches without letting one prediction satisfy two labels."""
    edges = [[index for index, marker in enumerate(markers)
              if math.hypot(point[0] - marker[0], point[1] - marker[1]) <= tolerance]
             for point in points]
    owners: dict[int, int] = {}

    def assign(point_index: int, seen: set[int]) -> bool:
        for marker_index in edges[point_index]:
            if marker_index in seen:
                continue
            seen.add(marker_index)
            if marker_index not in owners or assign(owners[marker_index], seen):
                owners[marker_index] = point_index
                return True
        return False

    for index in range(len(points)):
        assign(index, set())
    return set(owners.values())


def maximum_matches(points: list[tuple[float, float]], markers: list[tuple[float, float]], tolerance: float = 5) -> int:
    return len(matched_point_indices(points, markers, tolerance))


def score_image(curve_points: list[dict], panels: list[dict], patch: list[int],
                frames: dict[str, np.ndarray] | None = None) -> dict:
    points = [(float(point["x"]), float(point["y"])) for point in curve_points]
    if not all(math.isfinite(x) and math.isfinite(y) for x, y in points):
        raise ValueError("nonfinite recorded curve point")
    unique = list(dict.fromkeys(points))
    left, top, right, bottom = patch
    if not (left < right and top < bottom):
        raise ValueError("invalid controlled patch")
    remaining = [point for point in unique if not (left - 5 <= point[0] < right + 5 and top - 5 <= point[1] < bottom + 5)]
    assigned: list[list[tuple[float, float]]] = [[] for _ in panels]
    unavailable = 0
    for point in remaining:
        matching = []
        for index, panel in enumerate(panels):
            crop = panel["provenance"]["encoded_crop_source_pixels"]
            if crop["x"] <= point[0] < crop["x"] + crop["width"] and crop["y"] <= point[1] < crop["y"] + crop["height"]:
                matching.append(index)
        if len(matching) == 1:
            assigned[matching[0]].append(point)
        else:
            unavailable += 1
    matched = 0
    overlap = {"matched_ocr_overlap": 0, "missed_ocr_overlap": 0,
               "matched_artifact_overlap": 0, "missed_artifact_overlap": 0}
    for panel, targets in zip(panels, assigned, strict=True):
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        if panel["provenance"]["panel_to_source_matrix"] != [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1]:
            raise ValueError("panel-to-source transform differs")
        markers = []
        for marker in panel["markers"]:
            x, y = marker["center"]["x"], marker["center"]["y"]
            if not (math.isfinite(x) and math.isfinite(y) and 0 <= x < crop["width"] and 0 <= y < crop["height"]):
                raise ValueError("marker is outside its captured panel")
            markers.append((x + crop["x"], y + crop["y"]))
        matched_indices = matched_point_indices(targets, markers)
        matched += len(matched_indices)
        if frames is not None:
            planes = frames[panel["frame_file"]]
            for index, (x, y) in enumerate(targets):
                px, py = round(x - crop["x"]), round(y - crop["y"])
                x0, x1 = max(0, px - 2), min(planes.shape[2], px + 3)
                y0, y1 = max(0, py - 2), min(planes.shape[1], py + 3)
                if x0 >= x1 or y0 >= y1:
                    raise ValueError("curve point mask window is outside the frame")
                prefix = "matched" if index in matched_indices else "missed"
                overlap[f"{prefix}_ocr_overlap"] += int(np.max(planes[1, y0:y1, x0:x1]) >= np.float32(0.35))
                overlap[f"{prefix}_artifact_overlap"] += int(np.max(planes[2, y0:y1, x0:x1]) >= np.float32(0.35))
    return {"raw_points": len(points), "exact_duplicate_points": len(points) - len(unique),
            "patch_excluded": len(unique) - len(remaining), "unavailable_points": unavailable,
            "eligible_points": len(remaining) - unavailable, "matched_points": matched, **overlap}


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
    source_rows = {row["source_index"]: row for row in plan["source_rows"]}
    truth_rows = {row["caseId"]: row for row in truth["rows"]}
    predictions = {row["id"]: row for row in capture["rows"]}
    if (len(overlays) != 200 or len(source_rows) != 20 or len(truth_rows) != 104 or
            len(predictions) != 20 or len(manifest["rows"]) != 20):
        raise ValueError("frozen inventory differs")
    rows = []
    seen_groups: set[str] = set()
    for item in manifest["rows"]:
        prediction = predictions[item["id"]]
        overlay = overlays[item["image_sha256"]]
        source = source_rows[overlay["source_index"]]
        actual = truth_rows[source["case_id"]]
        if (prediction["image_sha256"] != item["image_sha256"] or overlay["component_sha256"] != source["component_sha256"] or
                source["component_sha256"] in seen_groups or actual["projectSha256"] != source["project_sha256"] or
                actual["embeddedImageSha256"] != source["image_sha256"] or actual["studyId"] != source["study_id"]):
            raise ValueError("train source identity differs")
        seen_groups.add(source["component_sha256"])
        base = {"id": item["id"], "partition": overlay["assigned_partition"]}
        if prediction["status"] != "completed":
            rows.append({**base, "status": "capture_failed"})
            continue
        frames = {}
        for panel in prediction["panels"]:
            if panel["provenance"]["source_sha256"] != item["image_sha256"]:
                raise ValueError("captured source hash differs")
            name = panel["frame_file"]
            if Path(name).name != name or not name.endswith(".bin.gz"):
                raise ValueError("frame name differs")
            frames[name] = read_frame(RUN / "smoke-capture-v1" / name, panel["frame_sha256"],
                                      panel["frame_width"], panel["frame_height"])
        rows.append({**base, "status": "scored", **score_image(actual["curvePoints"], prediction["panels"],
                                                                 overlay["patch_ltrb_half_open"], frames)})
    totals = {}
    for partition in ("internal_train_build", "internal_train_holdout"):
        selected = [row for row in rows if row["partition"] == partition]
        scored = [row for row in selected if row["status"] == "scored"]
        totals[partition] = {"images": len(selected), "scored_images": len(scored),
                             **{key: sum(row[key] for row in scored) for key in
                                ("raw_points", "exact_duplicate_points", "patch_excluded", "unavailable_points", "eligible_points", "matched_points",
                                 "matched_ocr_overlap", "missed_ocr_overlap", "matched_artifact_overlap", "missed_artifact_overlap")}}
    result = {"schema": "graphreader.manual-marker-native-point-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": args.input_sha256, "report_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256, "truth_sha256": TRUTH_SHA,
              "plan_sha256": PLAN_SHA, "scorer_source_sha256": digest(Path(__file__)),
              "match_tolerance_pixels": 5, "patch_exclusion_margin_pixels": 5, "totals": totals, "rows": rows,
              "limitations": ["Manual digitized points are imperfect and may omit other visible series.",
                              "Only recorded points outside the modified patch are scored; unmatched predictions are not negatives.",
                              "Mask overlap is measured near recorded points, not proven to cause misses.",
                              "Five-pixel one-to-one matching does not establish full-image precision or release acceptance.",
                              "Historical and sealed-source overlap are unknown."]}
    output = RUN / "smoke-native-score-v2.json"
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"partitions": totals, "sha256": digest(output)}))


if __name__ == "__main__":
    main()
