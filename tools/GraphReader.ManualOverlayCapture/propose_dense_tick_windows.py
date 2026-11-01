# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze measured tick-aligned crop proposals for two train-only diagnostics."""

from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
import statistics

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-marker-input-diagnostic-v1"
OUTPUT = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
INPUT_SHA = "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6"
RECEIPT_SHA = "23964f24a72fa7c34344f252f08d17582e6855111945ffd4c360fdeb7977bd21"
SOURCES = (
    (3, "internal_train_build", "tick-probe-a-v1", "9640618b667a2542946e6869f66f273f6b168af17a4f408f290d53ef4f2ce85b"),
    (17, "internal_train_holdout", "tick-probe-b-v1", "0170554a0ed976dd597dca2020dd2d292d86ca5be45e9410800efe72212e0ef0"),
)


def checked_json(path: Path, expected: str) -> dict:
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError("input hash differs")
    return json.loads(data)


def clustered_positions(values: list[float], tolerance: float = 5) -> list[float]:
    if not all(math.isfinite(value) for value in values):
        raise ValueError("tick position is nonfinite")
    groups: list[list[float]] = []
    for value in sorted(values):
        if groups and value - groups[-1][0] <= tolerance:
            groups[-1].append(value)
        else:
            groups.append([value])
    return [statistics.mean(group) for group in groups]


def measured_pitch(positions: list[float]) -> float | None:
    gaps = [right - left for left, right in zip(positions, positions[1:]) if 8 <= right - left <= 80]
    if len(gaps) < 3:
        return None
    candidates = [(sum(abs(gap - candidate) <= max(2, candidate * 0.12) for gap in gaps), candidate)
                  for candidate in gaps]
    count, selected = min(candidates, key=lambda value: (-value[0], value[1]))
    if count < 3:
        return None
    return statistics.median(gap for gap in gaps if abs(gap - selected) <= max(2, selected * 0.12))


def windows(positions: list[float], bounds: dict) -> list[list[int]]:
    pitch = measured_pitch(positions)
    if pitch is None:
        return []
    left, top, right, bottom = (bounds[key] for key in ("left", "top", "right", "bottom"))
    if not all(math.isfinite(value) for value in (left, top, right, bottom)) or right <= left or bottom <= top:
        raise ValueError("invalid OCR lane bounds")
    result = []
    for center in positions:
        if not left <= center <= right:
            continue
        box = [math.floor(max(left, center - pitch * 0.45)), math.floor(top),
               math.ceil(min(right, center + pitch * 0.45)), math.ceil(bottom)]
        if box[2] - box[0] >= pitch * 0.65 and box[3] - box[1] >= 3:
            result.append(box)
    return result


def write_new(path: Path, data: dict) -> str:
    encoded = (json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n").encode()
    with path.open("xb") as stream:
        stream.write(encoded)
    return hashlib.sha256(encoded).hexdigest()


def main() -> None:
    manifest = checked_json(RUN / "original-raster-input-v1.json", INPUT_SHA)
    receipt = checked_json(ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/source-receipt.json", RECEIPT_SHA)
    if (manifest["scope"] != "train-only-original-raster-no-truth" or manifest["selection"] != "original" or
            receipt["sealed_payload_reads"] != 0 or receipt["development_payload_reads"] != 0 or
            len(manifest["rows"]) != 20 or len(receipt["sources"]) != 20):
        raise ValueError("train source scope differs")
    for index, partition, report_name, report_sha in SOURCES:
        report = checked_json(RUN / report_name / "report.json", report_sha)
        item = manifest["rows"][index]
        source = next(row for row in receipt["sources"] if row["source_index"] == index)
        if (source["assigned_partition"] != partition or item["id"] != f"source-{index:03d}" or
                source["extracted_png_sha256"] != item["image_sha256"] or
                report["input_sha256"] != INPUT_SHA or report["truth_read"] is not False or
                report["optimizer_steps"] != 0 or report["sealed_payload_reads"] != 0 or
                len(report["rows"]) != 1 or report["rows"][0]["id"] != item["id"] or
                report["rows"][0]["image_sha256"] != item["image_sha256"]):
            raise ValueError("source identity differs")
        panels = report["rows"][0]["panels"]
        if len(panels) != 1 or not panels[0]["marker_stage_reached"]:
            raise ValueError("expected one observed panel")
        panel = panels[0]
        if not panel["provenance"]["identity"]:
            raise ValueError("source-to-panel identity required")
        wide = [region for region in panel["ocr_tick_regions"] if region["role"] == "XTick" and
                region["text"].isdecimal() and len(region["text"]) >= 10 and
                region["polygon"]["bounds"]["width"] > region["polygon"]["bounds"]["height"] * 4]
        if len(wide) != 1:
            raise ValueError("expected one wide numeric lane")
        strokes = panel["tick_lane"]["ocr"]["x_axis_stroke_diagnostics"]
        positions = clustered_positions([stroke["center_x"] for stroke in strokes if stroke["usable"]])
        boxes = windows(positions, wide[0]["polygon"]["bounds"])
        rows = [{"id": f"item-{n:03d}", "image_path": item["image_path"],
                 "image_sha256": item["image_sha256"], "box_xyxy": box} for n, box in enumerate(boxes)]
        if not rows:
            raise ValueError("no measured tick-aligned windows")
        output = OUTPUT / f"dense-window-input-{partition}-v1.json"
        result = {"schema": "graphreader.manual-axis-ocr-input.v1", "scope": "train-only-no-truth",
                  "source_split": partition, "rows": rows}
        print(json.dumps({"partition": partition, "windows": len(rows), "pitch": measured_pitch(positions),
                          "sha256": write_new(output, result)}))


if __name__ == "__main__":
    main()
