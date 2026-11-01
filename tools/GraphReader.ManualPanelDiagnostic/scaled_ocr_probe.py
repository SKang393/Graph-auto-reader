# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Apply the unchanged Y-label rule to already exposed panelized images."""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import re
import sys

import cv2
import numpy as np

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.manual_y_axis_candidate_v1.candidate import propose  # noqa: E402

AXIS_REPORT_SHA = "74ed5eaf7b446c3fbce0a08e9fdf2f859c1f173f94cb566fa041de2e760a6ed6"
SOURCE_INPUT_SHA = "c49f7693ce9401c8e90b7b196fc280d528c2a21f012c1e4a41d410a1ae4199fc"
CANDIDATE_SHA = "b4ffb2712c0eac839916f920d22245faec222ffd37aaf9cb85bec6fbae91a823"
LABELS_SHA = "0d80b0663457f25a89e7d39a0e60f5e156604a0d382dcb9e8f132f7c29331713"
OCR_INPUT_SHA = "4f64d264b97443118cfcf21d40325ac62a380596da1bde6d012a661b580377c7"
JOIN_SHA = "1e8e2804586f217b8145a6b32e0524805374da2f0ee6d8c84bf4fe7ce0efd4f7"
PATTERN = re.compile(r"^-?\d+(?:\.\d+)?%?$")


def read_checked(path: Path, expected: str) -> bytes:
    data = path.read_bytes()
    if sha256(data).hexdigest() != expected:
        raise ValueError("input hash differs")
    return data


def write_new(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def make_input() -> None:
    report = json.loads(read_checked(RUN / "panel-diagnostic-scale-v1/report.json", AXIS_REPORT_SHA))
    sources = json.loads(read_checked(RUN / "holdout-axis-input.json", SOURCE_INPUT_SHA))
    code = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/manual_y_axis_candidate_v1/candidate.py"
    read_checked(code, CANDIDATE_SHA)
    if report["source_split"] != "internal_train_holdout" or sources["source_split"] != "internal_train_holdout":
        raise ValueError("source split differs")
    by_id = {row["id"]: row for row in sources["rows"]}
    if len(by_id) != 5 or {row["id"] for row in report["rows"]} != set(by_id):
        raise ValueError("source inventory differs")
    ocr_rows, joins = [], []
    for image in report["rows"]:
        source = by_id[image["id"]]
        relative = source["image_path"]
        if not relative.startswith("artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/"):
            raise ValueError("image is outside the pinned capture")
        path = (ROOT / relative).resolve(strict=True)
        if not path.is_relative_to(ROOT.resolve()):
            raise ValueError("image escaped project root")
        data = read_checked(path, source["image_sha256"])
        pixels = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if pixels is None or pixels.shape != (source["height"], source["width"]):
            raise ValueError("image dimensions differ")
        for panel in image["panels"]:
            if not panel["axis"]["success"]:
                continue
            crop = panel["crop"]
            x, y, width, height = (int(crop[key]) for key in ("X", "Y", "Width", "Height"))
            if (x, y, width, height) != (crop["X"], crop["Y"], crop["Width"], crop["Height"]) or \
                    x < 0 or y < 0 or x + width > source["width"] or y + height > source["height"]:
                raise ValueError("panel crop differs")
            axis = panel["axis"]["y_axis"]
            top, bottom = sorted((axis["Start"], axis["End"]), key=lambda point: point["Y"])
            if bottom["Y"] - top["Y"] < 10:
                continue
            geometry = [
                {"GraphX": 0, "GraphY": 0, "ScreenX": bottom["X"], "ScreenY": bottom["Y"]},
                {"GraphX": 1, "GraphY": 0, "ScreenX": bottom["X"] + 1, "ScreenY": bottom["Y"]},
                {"GraphX": 0, "GraphY": 1, "ScreenX": top["X"], "ScreenY": top["Y"]},
            ]
            for box in propose(pixels[y:y + height, x:x + width], geometry):
                source_box = [box[0] + x, box[1] + y, box[2] + x, box[3] + y]
                identity = f"scaled-proposal-{len(ocr_rows):03d}"
                ocr_rows.append({"id": identity, "image_path": relative, "image_sha256": source["image_sha256"],
                                 "box_xyxy": source_box})
                joins.append({"id": identity, "sample_id": image["id"], "box": source_box})
    output, join = RUN / "scaled-ocr-input-v1.json", RUN / "scaled-ocr-join-v1.json"
    if output.exists() or join.exists():
        raise ValueError("new output paths required")
    write_new(output, {"schema": "graphreader.manual-axis-ocr-input.v1", "scope": "exposed-train-only",
                       "source_split": "internal_train_holdout", "rows": ocr_rows})
    write_new(join, joins)
    print(json.dumps({"proposals": len(ocr_rows), "input_sha256": sha256(output.read_bytes()).hexdigest(),
                      "join_sha256": sha256(join.read_bytes()).hexdigest(), "optimizer_steps": 0}))


def iou(a: list[int], b: list[int]) -> float:
    intersection = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - intersection
    return intersection / union if union else 0.0


def score(prediction_sha: str) -> None:
    labels = json.loads(read_checked(RUN / "holdout-labels.json", LABELS_SHA))["rows"]
    predictions = json.loads(read_checked(RUN / "scaled-ocr-predictions-v1.json", prediction_sha))
    joins = json.loads(read_checked(RUN / "scaled-ocr-join-v1.json", JOIN_SHA))
    read_checked(RUN / "scaled-ocr-input-v1.json", OCR_INPUT_SHA)
    if predictions["input_sha256"] != OCR_INPUT_SHA or predictions["source_split"] != "internal_train_holdout" or \
            predictions["optimizer_steps"] != 0:
        raise ValueError("OCR prediction provenance differs")
    outputs = {row["id"]: row for row in predictions["rows"]}
    if len(labels) != 52 or len(joins) != len(outputs) or {row["id"] for row in joins} != set(outputs):
        raise ValueError("label or prediction inventory differs")
    remaining = set(range(len(labels)))
    counts: Counter[str] = Counter()
    for row in joins:
        choices = [(iou(row["box"], labels[index]["box_xyxy"]), index) for index in remaining
                   if labels[index]["sample_id"] == row["sample_id"]]
        overlap, index = max(choices) if choices else (0, None)
        text = (outputs[row["id"]]["official"]["Text"] or "").strip()
        if overlap < 0.5:
            counts["unmatched"] += 1
            if PATTERN.fullmatch(text):
                counts["unmatched_numeric"] += 1
            continue
        remaining.remove(index)
        counts["matched"] += 1
        if not text:
            counts["empty"] += 1
        elif text == labels[index]["label"]:
            counts["exact"] += 1
        else:
            counts["wrong"] += 1
    counts["missed"] = len(remaining)
    path = RUN / "scaled-ocr-score-v2.json"
    write_new(path, {"schema": "graphreader.scaled-axis-ocr-score.v1", "scope": "exposed-training-not-acceptance",
                     "labels": 52, "proposals": len(joins), "match_iou": 0.5, "counts": dict(counts),
                     "labels_sha256": LABELS_SHA, "predictions_sha256": prediction_sha,
                     "limitations": ["The images and labels were already reviewed during development.",
                                     "Assistant-reviewed labels are not independently adjudicated.",
                                     "Unmatched numeric proposals are not automatically false positives.",
                                     "Historical and sealed source overlap is not fully established."]})
    print(json.dumps({"labels": 52, "proposals": len(joins), "counts": dict(counts),
                      "sha256": sha256(path.read_bytes()).hexdigest()}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("propose", "score"))
    parser.add_argument("--prediction-sha256")
    args = parser.parse_args()
    if args.mode == "propose":
        make_input()
    elif args.prediction_sha256 and re.fullmatch(r"[0-9a-f]{64}", args.prediction_sha256):
        score(args.prediction_sha256)
    else:
        parser.error("score requires a lowercase prediction SHA-256")


if __name__ == "__main__":
    main()
