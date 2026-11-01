# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Apply the frozen tick probe to the separate development partition."""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
import math
from pathlib import Path
import re

import numpy as np

from tick_rank_probe import choose

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
MANIFEST_SHA = "d0adfe55915cc323ff408acf342110972eca2a92ae188ac43b8090d91054afd4"
TICK_SOURCE_SHA = "e61192366927c3f48b580a9fade972a2e1286777488b4490c633523ea1d0b69d"


def digest(path: Path) -> str:
    value = sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def read_checked(path: Path, expected: str) -> dict:
    if digest(path) != expected:
        raise ValueError("input hash differs")
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def write_new(path: Path, value: dict) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def predict(report_sha: str) -> None:
    if digest(Path(__file__).with_name("tick_rank_probe.py")) != TICK_SOURCE_SHA:
        raise ValueError("frozen tick probe differs")
    report = read_checked(RUN / "panel-diagnostic-dev-v1/report.json", report_sha)
    if report["source_split"] != "internal_dev" or report["input_sha256"] != MANIFEST_SHA or report["optimizer_steps"] != 0:
        raise ValueError("development report scope differs")
    rows = []
    for image in report["rows"]:
        if not image["panels"]:
            rows.append({"id": image["id"], "panel": 0, "success": False})
        for panel in image["panels"]:
            if not panel["axis"]["success"]:
                rows.append({"id": image["id"], "panel": panel["Order"], "success": False})
                continue
            selected, regular, clusters = choose(panel)
            baseline = panel["axis"]["ranked_candidates"][0]
            rows.append({"id": image["id"], "panel": panel["Order"], "success": True, "crop": panel["crop"],
                         "baseline": baseline["Intersection"], "selected": selected["Intersection"],
                         "selected_rank": selected["Rank"], "regular_tick_run": regular, "tick_clusters": clusters})
    if len(report["rows"]) != 16 or len({row["id"] for row in rows}) != 16:
        raise ValueError("development image inventory differs")
    path = RUN / "tick-rank-dev-v1.json"
    write_new(path, {"schema": "graphreader.axis-tick-rank-probe.v1", "split": "dev", "input_sha256": report_sha,
                     "manifest_sha256": MANIFEST_SHA, "tick_source_sha256": TICK_SOURCE_SHA,
                     "truth_read": False, "optimizer_steps": 0, "rows": rows})
    print(json.dumps({"images": 16, "panels": len(rows), "successful_panels": sum(row["success"] for row in rows),
                      "changed": sum(row.get("selected_rank", 0) != 0 for row in rows), "sha256": digest(path)}))


def score(truth_sha: str, prediction_sha: str) -> None:
    manifest = read_checked(RUN / "dev-axis-capture-v1/manifest.json", MANIFEST_SHA)
    truth = read_checked(RUN / "dev-axis-capture-v1/anchors.json", truth_sha)
    predictions = read_checked(RUN / "tick-rank-dev-v1.json", prediction_sha)
    if truth["manifest_sha256"] != MANIFEST_SHA or truth["predictions_sha256"] != prediction_sha or \
            predictions["manifest_sha256"] != MANIFEST_SHA or predictions["truth_read"] is not False:
        raise ValueError("truth or prediction provenance differs")
    image_info = {row["id"]: row for row in manifest["rows"]}
    cases = {row["case_id"]: row for row in manifest["cases"]}
    predictions_by_image: dict[str, list[dict]] = {}
    for row in predictions["rows"]:
        predictions_by_image.setdefault(row["id"], []).append(row)
    if len(cases) != 16 or len(truth["rows"]) != 16 or len(image_info) != 16:
        raise ValueError("development case inventory differs")
    rows = []
    for item in truth["rows"]:
        case = cases[item["case_id"]]
        if item["image_id"] != case["image_id"]:
            raise ValueError("case/image identity differs")
        anchors = item["anchors"]
        row = {"case_id": item["case_id"], "image_id": item["image_id"], "study_id": case["study_id"],
               "train_overlap": image_info[item["image_id"]]["train_overlap"]}
        if len(anchors) != 3:
            rows.append({**row, "status": "unsupported_anchor_count"})
            continue
        matrix = np.array([[point["GraphX"], point["GraphY"], 1] for point in anchors], dtype=np.float64)
        try:
            screen_x = np.linalg.solve(matrix, np.array([point["ScreenX"] for point in anchors]))
            screen_y = np.linalg.solve(matrix, np.array([point["ScreenY"] for point in anchors]))
        except np.linalg.LinAlgError:
            rows.append({**row, "status": "degenerate_calibration"})
            continue
        if not np.isfinite(screen_x).all() or not np.isfinite(screen_y).all() or abs(screen_y[1]) < 1e-9:
            rows.append({**row, "status": "degenerate_calibration"})
            continue
        matching = []
        for panel in predictions_by_image.get(item["image_id"], []):
            if not panel["success"]:
                continue
            crop = panel["crop"]
            contained = sum(crop["X"] - 1 <= point["ScreenX"] <= crop["X"] + crop["Width"] + 1 and
                            crop["Y"] - 1 <= point["ScreenY"] <= crop["Y"] + crop["Height"] + 1
                            for point in anchors)
            if contained >= 2:
                matching.append(panel)
        if len(matching) != 1:
            rows.append({**row, "status": "ambiguous_or_missing_panel"})
            continue
        panel = matching[0]
        def error(point: dict) -> tuple[float, float]:
            x = point["X"] + panel["crop"]["X"]
            y = point["Y"] + panel["crop"]["Y"]
            graph_y = (y - screen_y[2]) / screen_y[1]
            expected_x = float(screen_x[1] * graph_y + screen_x[2])
            return abs(x - expected_x), expected_x
        baseline, baseline_x = error(panel["baseline"])
        selected, selected_x = error(panel["selected"])
        left, right = panel["crop"]["X"], panel["crop"]["X"] + panel["crop"]["Width"]
        if not all(math.isfinite(value) for value in (baseline, selected, baseline_x, selected_x)) or \
                not (left - 5 <= baseline_x <= right + 5 and left - 5 <= selected_x <= right + 5):
            rows.append({**row, "status": "calibrated_axis_outside_panel"})
            continue
        rows.append({**row, "status": "scored", "baseline_error_pixels": baseline,
                     "selected_error_pixels": selected, "selected_rank": panel["selected_rank"]})
    scored = [row for row in rows if row["status"] == "scored"]
    result = {"schema": "graphreader.axis-tick-dev-score.v1", "scope": "exposed-development-not-acceptance",
              "manifest_sha256": MANIFEST_SHA, "truth_sha256": truth_sha, "prediction_sha256": prediction_sha,
              "tolerance_pixels": 5, "rows": rows,
              "limitations": ["Manual calibration is imperfect and graph X=0 may not equal a visible axis.",
                              "Only vertical axis position at one Y coordinate is measured.",
                              "Study groups and historical exposure limit independence."]}
    path = RUN / "tick-rank-dev-score-v1.json"
    write_new(path, result)
    print(json.dumps({"cases": len(rows), "scored": len(scored),
                      "baseline_within_5px": sum(row["baseline_error_pixels"] <= 5 for row in scored),
                      "selected_within_5px": sum(row["selected_error_pixels"] <= 5 for row in scored),
                      "changed_scored": sum(row["selected_rank"] != 0 for row in scored),
                      "unavailable": len(rows) - len(scored), "sha256": digest(path)}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("predict", "score"))
    parser.add_argument("--report-sha256")
    parser.add_argument("--truth-sha256")
    parser.add_argument("--prediction-sha256")
    args = parser.parse_args()
    valid = lambda value: bool(value and re.fullmatch(r"[0-9a-f]{64}", value))
    if args.mode == "predict" and valid(args.report_sha256):
        predict(args.report_sha256)
    elif args.mode == "score" and valid(args.truth_sha256) and valid(args.prediction_sha256):
        score(args.truth_sha256, args.prediction_sha256)
    else:
        parser.error("the selected mode requires lowercase SHA-256 input identities")


if __name__ == "__main__":
    main()
