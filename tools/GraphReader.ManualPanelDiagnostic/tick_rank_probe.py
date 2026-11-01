# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Explore repeated Y-tick evidence on already exposed training images only."""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path

import numpy as np

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
INPUTS = {
    "build": ("panel-diagnostic-build-v1/report.json", "2b8e8c5d64d5cdfe9419ab2f66e5535780ae800a332bc3aadd3e5f307c5b7e9c"),
    "holdout": ("panel-diagnostic-v4/report.json", "ed9d593561370483efe5c74bb64843989784130b9035760b9b742f97e1b8a4b2"),
    "fresh": ("panel-diagnostic-fresh-v1/report.json", None),
}
ANCHORS = ROOT / "artifacts/manual-dig-train-anchor-review-v1/anchor-samples.json"


def file_hash(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def regular_run(positions: list[float]) -> int:
    """Count a consecutive near-regular run without assuming tick values."""
    if len(positions) < 2:
        return len(positions)
    best = 1
    for index, start in enumerate(positions):
        for second in range(index + 1, len(positions)):
            step = positions[second] - start
            if step < 6:
                continue
            tolerance = max(3.0, step * 0.15)
            count, last = 2, positions[second]
            for current in positions[second + 1:]:
                if abs(current - last - step) <= tolerance:
                    count += 1
                    last = current
                elif current > last + step + tolerance:
                    break
            best = max(best, count)
    return best


def tick_evidence(candidate: dict, lines: list[dict], width: int, height: int) -> tuple[int, int]:
    axis = candidate["YAxisLine"]
    start, end = axis["Start"], axis["End"]
    delta_y = end["Y"] - start["Y"]
    bottom, top = candidate["Intersection"]["Y"], candidate["TopLeft"]["Y"]
    if abs(delta_y) < 1:
        return 0, 0
    positions = []
    for line in lines:
        first, second = line["Segment"]["Start"], line["Segment"]["End"]
        dx, dy = abs(first["X"] - second["X"]), abs(first["Y"] - second["Y"])
        y = (first["Y"] + second["Y"]) / 2
        if not (4 <= dx <= min(100, width * 0.08) and dy <= max(2, dx * 0.2) and top - 8 <= y <= bottom + 8):
            continue
        x = start["X"] + (y - start["Y"]) * (end["X"] - start["X"]) / delta_y
        left, right = min(first["X"], second["X"]), max(first["X"], second["X"])
        if left < x - 2 and x - 6 <= right <= x + max(6, dx * 0.5):
            positions.append(y)
    positions.sort()
    clusters: list[list[float]] = []
    for y in positions:
        if not clusters or y - clusters[-1][-1] > max(5, height * 0.004):
            clusters.append([y])
        else:
            clusters[-1].append(y)
    centers = [sum(group) / len(group) for group in clusters]
    return regular_run(centers), len(centers)


def choose(panel: dict) -> tuple[dict, int, int]:
    axis = panel["axis"]
    candidates, lines = axis["ranked_candidates"], axis["line_candidates"]
    baseline = candidates[0]
    width, height = panel["Width"], panel["Height"]
    groups: list[tuple[float, float, float]] = []
    ranked = []
    for candidate in candidates:
        if candidate["Score"] < baseline["Score"] - 0.12:
            break
        x, bottom = candidate["Intersection"]["X"], candidate["Intersection"]["Y"]
        top = candidate["TopLeft"]["Y"]
        if candidate["BottomRight"]["X"] - x < width * 0.15 or bottom - top < height * 0.15:
            continue
        if any(abs(x - px) < 7 and abs(bottom - py) < 7 and abs(top - pt) < 10 for px, py, pt in groups):
            continue
        groups.append((x, bottom, top))
        regular, count = tick_evidence(candidate, lines, width, height)
        ranked.append((regular, candidate["Score"], -candidate["Rank"], candidate, count))
        if len(groups) >= 60:
            break
    if not ranked:
        return baseline, 0, 0
    regular, _, _, selected, count = max(ranked, key=lambda item: item[:3])
    return (selected, regular, count) if regular >= 4 else (baseline, regular, count)


def write_new(path: Path, value: dict) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def predict(split: str, expected_fresh: str | None = None) -> None:
    relative, expected = INPUTS[split]
    if split == "fresh":
        expected = expected_fresh
        if expected is None or len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
            raise ValueError("fresh report requires its SHA-256")
    source = RUN / relative
    if file_hash(source) != expected:
        raise ValueError("pinned diagnostic report differs")
    output = RUN / f"tick-rank-{split}-v1.json"
    if output.exists():
        raise ValueError("output already exists")
    with source.open(encoding="utf-8") as stream:
        report = json.load(stream)
    if report["source_split"] != f"internal_train_{split}" or report["optimizer_steps"] != 0:
        raise ValueError("training split differs")
    rows = []
    for image in report["rows"]:
        for panel in image["panels"]:
            if not panel["axis"]["success"]:
                rows.append({"id": image["id"], "panel": panel["Order"], "success": False})
                continue
            selected, regular, count = choose(panel)
            baseline = panel["axis"]["ranked_candidates"][0]
            rows.append({"id": image["id"], "panel": panel["Order"], "success": True,
                         "crop": panel["crop"], "baseline": baseline["Intersection"],
                         "selected": selected["Intersection"], "selected_rank": selected["Rank"],
                         "regular_tick_run": regular, "tick_clusters": count})
    result = {"schema": "graphreader.axis-tick-rank-probe.v1", "split": split, "input_sha256": expected,
              "source_sha256": file_hash(Path(__file__)), "truth_read": False, "optimizer_steps": 0, "rows": rows}
    write_new(output, result)
    print(json.dumps({"panels": len(rows), "changed": sum(row.get("selected_rank", 0) != 0 for row in rows),
                      "sha256": file_hash(output)}))


def score_build() -> None:
    predictions = RUN / "tick-rank-build-v1.json"
    output = RUN / "tick-rank-build-score-v2.json"
    if output.exists():
        raise ValueError("output already exists")
    predicted = json.loads(predictions.read_bytes())
    anchors_bytes = ANCHORS.read_bytes()
    anchors = json.loads(anchors_bytes)
    if predicted["split"] != "build" or predicted["truth_read"] is not False or len(predicted["rows"]) != 8:
        raise ValueError("prediction scope differs")
    by_id = {row["SampleId"]: row for row in anchors["samples"]}
    rows = []
    for row in predicted["rows"]:
        if not row["success"]:
            rows.append({"id": row["id"], "success": False})
            continue
        points = by_id[row["id"]]["Anchors"]
        matrix = np.array([[point["GraphX"], point["GraphY"], 1] for point in points], dtype=np.float64)
        screen_x = np.linalg.solve(matrix, np.array([point["ScreenX"] for point in points]))
        screen_y = np.linalg.solve(matrix, np.array([point["ScreenY"] for point in points]))
        if abs(screen_y[1]) < 1e-9:
            raise ValueError("calibration Y axis is singular")
        def error(point: dict) -> float:
            source_y = point["Y"] + row["crop"]["Y"]
            graph_y = (source_y - screen_y[2]) / screen_y[1]
            expected_x = screen_x[1] * graph_y + screen_x[2]
            return float(abs(point["X"] + row["crop"]["X"] - expected_x))
        rows.append({"id": row["id"], "success": True, "baseline_error_pixels": error(row["baseline"]),
                     "selected_error_pixels": error(row["selected"]), "selected_rank": row["selected_rank"]})
    result = {"schema": "graphreader.axis-tick-rank-build-score.v1", "predictions_sha256": file_hash(predictions),
              "anchors_sha256": sha256(anchors_bytes).hexdigest(), "tolerance_pixels": 5, "rows": rows,
              "prediction_source_sha256": predicted["source_sha256"], "scoring_source_sha256": file_hash(Path(__file__)),
              "scope": "exploratory-known-training-sources-not-acceptance"}
    write_new(output, result)
    print(json.dumps({"rows": len(rows), "baseline_within_5px": sum(row.get("baseline_error_pixels", 1e9) <= 5 for row in rows),
                      "selected_within_5px": sum(row.get("selected_error_pixels", 1e9) <= 5 for row in rows),
                      "sha256": file_hash(output)}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "holdout", "fresh", "score-build"))
    parser.add_argument("--fresh-report-sha256")
    args = parser.parse_args()
    if args.mode == "score-build":
        score_build()
    else:
        predict(args.mode, args.fresh_report_sha256)


if __name__ == "__main__":
    main()
