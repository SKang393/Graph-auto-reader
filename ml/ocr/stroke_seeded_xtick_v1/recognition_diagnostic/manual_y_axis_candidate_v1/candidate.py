# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Train-only Y-axis label proposals using supplied calibration geometry."""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(r"E:\Graph-auto-reader")
REVIEW = ROOT / "artifacts/manual-dig-train-anchor-review-v1"
OUTPUT = ROOT / "artifacts/manual-y-axis-candidate-v1"
MIN_COMPONENT_HEIGHT = 5
MIN_COMPONENT_AREA = 4
MIN_VERTICAL_OVERLAP = 0.6
MAX_AXIS_GAP_HEIGHT_RATIO = 4.0
MATCH_IOU = 0.5


def group_components(boxes: list[tuple[int, int, int, int]]) -> list[tuple[int, int, int, int]]:
    """Join nearby glyph components on the same line, without using text labels."""
    parent = list(range(len(boxes)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    for i, a in enumerate(boxes):
        for j in range(i):
            b = boxes[j]
            overlap = max(0, min(a[3], b[3]) - max(a[1], b[1]))
            height = min(a[3] - a[1], b[3] - b[1])
            gap = max(0, max(a[0], b[0]) - min(a[2], b[2]))
            if overlap >= MIN_VERTICAL_OVERLAP * height and gap <= max(3, 0.8 * height):
                left, right = find(i), find(j)
                if left != right:
                    parent[right] = left
    groups: dict[int, list[tuple[int, int, int, int]]] = {}
    for i, box in enumerate(boxes):
        groups.setdefault(find(i), []).append(box)
    return sorted((min(b[0] for b in group), min(b[1] for b in group),
                   max(b[2] for b in group), max(b[3] for b in group))
                  for group in groups.values())


def propose(image: np.ndarray, anchors: list[dict]) -> list[list[int]]:
    matrix = np.array([[a["GraphX"], a["GraphY"], 1] for a in anchors], dtype=np.float64)
    screen_x = np.linalg.solve(matrix, np.array([a["ScreenX"] for a in anchors]))
    screen_y = np.linalg.solve(matrix, np.array([a["ScreenY"] for a in anchors]))
    low = min(a["GraphY"] for a in anchors)
    high = max(a["GraphY"] for a in anchors)
    endpoints = [(float(np.array([0, y, 1]) @ screen_x), float(np.array([0, y, 1]) @ screen_y))
                 for y in (low, high)]
    if high <= low or abs(endpoints[1][1] - endpoints[0][1]) < 10:
        raise ValueError("calibration does not bound a usable vertical axis")
    height, width = image.shape
    x_max = max(0, min(width, int(round(min(p[0] for p in endpoints))) - 4))
    x_min = max(0, x_max - min(120, int(width * 0.12)))
    y_min = max(0, int(min(p[1] for p in endpoints)) - 15)
    y_max = min(height, int(max(p[1] for p in endpoints)) + 16)
    if x_max <= x_min or y_max <= y_min:
        return []
    roi = image[y_min:y_max, x_min:x_max]
    _, foreground = cv2.threshold(roi, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    _, _, stats, _ = cv2.connectedComponentsWithStats(foreground, 8)
    components = []
    for x, y, w, h, area in stats[1:]:
        if area >= MIN_COMPONENT_AREA and MIN_COMPONENT_HEIGHT <= h <= max(40, height * 0.1):
            components.append((x_min + int(x), y_min + int(y), x_min + int(x + w), y_min + int(y + h)))
    proposals = []
    for x1, y1, x2, y2 in group_components(components):
        center_y = (y1 + y2) / 2
        fraction = (center_y - endpoints[0][1]) / (endpoints[1][1] - endpoints[0][1])
        axis_x = endpoints[0][0] + fraction * (endpoints[1][0] - endpoints[0][0])
        gap = axis_x - x2
        if 0 <= gap <= MAX_AXIS_GAP_HEIGHT_RATIO * (y2 - y1):
            proposals.append([x1, y1, x2, y2])
    return sorted(proposals, key=lambda box: (box[1], box[0]))


def iou(a: list[int], b: list[int]) -> float:
    intersection = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - intersection
    return intersection / union if union else 0.0


def run_proposals() -> None:
    if OUTPUT.exists():
        raise ValueError("output already exists")
    review_bytes = (REVIEW / "anchor-samples.json").read_bytes()
    review = json.loads(review_bytes)
    if review["selected_train_examples"] != 8 or review["development_payloads_opened"] != 0 or review["sealed_payloads_opened"] != 0:
        raise ValueError("review input is not the eight-source train-only sample")
    rows = []
    for sample in review["samples"]:
        path = REVIEW / sample["ImageFile"]
        data = path.read_bytes()
        if sha256(data).hexdigest() != sample["ImageSha256"]:
            raise ValueError("source image hash mismatch")
        image = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if image is None or image.shape != (sample["Height"], sample["Width"]):
            raise ValueError("source image decode mismatch")
        rows.append({"sample_id": sample["SampleId"], "boxes": propose(image, sample["Anchors"])})
    OUTPUT.mkdir(parents=True)
    result = {"schema": "graphreader.manual-y-axis-proposals.v1", "scope": "train-only-oracle-calibration-diagnostic",
              "review_sha256": sha256(review_bytes).hexdigest(), "source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "opencv_version": cv2.__version__, "truth_read": False, "optimizer_steps": 0, "rows": rows}
    path = OUTPUT / "proposals.json"
    path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": "proposals-created", "images": len(rows), "proposals": sum(len(r["boxes"]) for r in rows),
                      "sha256": sha256(path.read_bytes()).hexdigest()}))


def score() -> None:
    proposal_bytes = (OUTPUT / "proposals.json").read_bytes()
    proposal = json.loads(proposal_bytes)
    labels_bytes = (REVIEW / "verified-axis-labels-v2/labels.json").read_bytes()
    labels = json.loads(labels_bytes)
    if proposal["truth_read"] is not False or proposal["optimizer_steps"] != 0 or labels["label_count"] != 76:
        raise ValueError("input scope differs")
    counts = {"fit": {"labels": 0, "matched": 0, "proposals": 0, "unmatched": 0},
              "comparison": {"labels": 0, "matched": 0, "proposals": 0, "unmatched": 0}}
    for row in proposal["rows"]:
        group = "fit" if int(row["sample_id"][-3:]) <= 4 else "comparison"
        truth = [item["ink_box_xyxy_half_open"] for item in labels["rows"] if item["sample_id"] == row["sample_id"]]
        remaining = set(range(len(truth)))
        matched = 0
        for box in row["boxes"]:
            options = [(iou(box, truth[index]), index) for index in remaining]
            if options:
                overlap, index = max(options)
                if overlap >= MATCH_IOU:
                    remaining.remove(index)
                    matched += 1
        item = counts[group]
        item["labels"] += len(truth)
        item["matched"] += matched
        item["proposals"] += len(row["boxes"])
        item["unmatched"] += len(row["boxes"]) - matched
    result = {"schema": "graphreader.manual-y-axis-score.v1", "scope": "exploratory-train-only-not-acceptance",
              "proposals_sha256": sha256(proposal_bytes).hexdigest(), "labels_sha256": sha256(labels_bytes).hexdigest(),
              "match_iou": MATCH_IOU, "counts": counts,
              "limitations": ["Axis geometry is supplied from manual calibration.", "Annotations are incomplete and assistant-reviewed.",
                              "Both source groups were visually inspected and aggregate label geometry was observed before this run.",
                              "Unmatched proposals are not verified false positives.", "Historical and sealed overlap is not fully established."]}
    path = OUTPUT / "score.json"
    if path.exists():
        raise ValueError("score already exists")
    path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": "scored", "counts": counts, "sha256": sha256(path.read_bytes()).hexdigest()}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("propose", "score"))
    args = parser.parse_args()
    if args.mode == "propose":
        run_proposals()
    else:
        score()


if __name__ == "__main__":
    main()
