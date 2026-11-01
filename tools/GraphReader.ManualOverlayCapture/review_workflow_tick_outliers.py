# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Create a private, prediction-blind sheet for frozen workflow position outliers."""

from __future__ import annotations

import hashlib
import io
import json
import math
from pathlib import Path
import statistics

from PIL import Image, ImageDraw

from propose_dense_tick_windows import ROOT, RUN, checked_json
from score_workflow_tick_details import INPUT_SHA

REPORT_SHA = "20437621a2de87625273f2ca3e6148e2b06bf56f374bd6236d509873b73226a3"


def outlier_regions(panel: dict) -> list[dict]:
    regions = [region for region in (panel["ocr_tick_regions"] or []) if region["role"] == "YTick" and
               isinstance(region["numeric_value"], (int, float)) and math.isfinite(region["numeric_value"])]
    if len(regions) < 3 or not panel["y_axis_line"]:
        return []
    start, end = panel["y_axis_line"]["start"], panel["y_axis_line"]["end"]
    if end["y"] == start["y"]:
        raise ValueError("invalid vertical axis")
    gaps = []
    for region in regions:
        box = region["bounds"]
        if box["height"] <= 0:
            raise ValueError("invalid OCR box")
        y = box["top"] + box["height"] / 2
        x = start["x"] + (y - start["y"]) * (end["x"] - start["x"]) / (end["y"] - start["y"])
        gaps.append((x - box["right"]) / box["height"])
    median = statistics.median(gaps)
    return [region for region, gap in zip(regions, gaps) if gap > median + 1.25]


def main() -> None:
    inputs = checked_json(RUN / "original-raster-input-v1.json", INPUT_SHA)
    capture = checked_json(RUN / "original-workflow-full-v5/report.json", REPORT_SHA)
    if (capture["input_sha256"] != INPUT_SHA or capture["detailed_capture"] is not True or
            capture["truth_read"] is not False or capture["optimizer_steps"] != 0 or
            capture["sealed_payload_reads"] != 0):
        raise ValueError("workflow outlier scope differs")
    sources = {row["id"]: row for row in inputs["rows"]}
    selected = [(row, panel, region) for row in capture["rows"] for panel in row["panels"]
                for region in outlier_regions(panel)]
    if len(selected) != 3:
        raise ValueError("workflow outlier inventory differs")
    output = RUN / "workflow-gap-review-v1"
    if output.exists():
        raise ValueError("review output already exists")
    output.mkdir()
    canvas = Image.new("RGB", (1200, 420), "white")
    draw = ImageDraw.Draw(canvas)
    entries = []
    for index, (row, panel, region) in enumerate(selected):
        source = sources[row["id"]]
        provenance = panel["provenance"]
        if source["image_sha256"] != provenance["source_sha256"] or not source["image_path"].startswith(
                "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/"):
            raise ValueError("workflow outlier source differs")
        path = (ROOT / source["image_path"]).resolve(strict=True)
        if not path.is_relative_to(ROOT.resolve()):
            raise ValueError("workflow outlier source escaped project")
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != source["image_sha256"]:
            raise ValueError("workflow outlier image hash differs")
        image = Image.open(io.BytesIO(data)).convert("RGB")
        if image.size != (provenance["source_width"], provenance["source_height"]):
            raise ValueError("workflow outlier image dimensions differ")
        crop, box = provenance["encoded_crop_source_pixels"], region["bounds"]
        left = math.floor(crop["x"] + box["left"])
        top = math.floor(crop["y"] + box["top"])
        right = math.ceil(crop["x"] + box["right"])
        bottom = math.ceil(crop["y"] + box["bottom"])
        if not (crop["x"] <= left < right <= crop["right"] and crop["y"] <= top < bottom <= crop["bottom"]):
            raise ValueError("workflow outlier crop escaped the panel")
        cell_y = index * 140
        draw.text((4, cell_y + 3), f"workflow-outlier-{index:03d}", fill="black")
        detail = image.crop((left, top, right, bottom))
        factor = min(8, 170 / detail.width, 105 / detail.height)
        detail = detail.resize((max(1, round(detail.width * factor)), max(1, round(detail.height * factor))),
                               Image.Resampling.NEAREST)
        canvas.paste(detail, (5, cell_y + 25))
        bounds = (max(0, left - 55), max(0, top - 25), min(image.width, right + 120), min(image.height, bottom + 25))
        context = image.crop(bounds)
        scale = min(5, 900 / context.width, 105 / context.height)
        context = context.resize((max(1, round(context.width * scale)), max(1, round(context.height * scale))),
                                 Image.Resampling.NEAREST)
        x, y = 190, cell_y + 25
        canvas.paste(context, (x, y))
        draw.rectangle((x + (left-bounds[0])*scale, y + (top-bounds[1])*scale,
                        x + (right-bounds[0])*scale, y + (bottom-bounds[1])*scale), outline="red", width=2)
        entries.append({"id": f"workflow-outlier-{index:03d}", "source_id": row["id"],
                        "panel_id": panel["panel_id"], "region_id": region["region_id"],
                        "image_sha256": source["image_sha256"], "box_xyxy": [left, top, right, bottom]})
    sheet = output / "sheet.png"
    with sheet.open("xb") as stream:
        canvas.save(stream, format="PNG")
    manifest = {"schema": "graphreader.workflow-gap-review.v1", "scope": "train-private-review-no-predicted-text",
                "report_sha256": REPORT_SHA, "sheet_sha256": hashlib.sha256(sheet.read_bytes()).hexdigest(),
                "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "rows": entries}
    encoded = (json.dumps(manifest, sort_keys=True, indent=2) + "\n").encode()
    with (output / "manifest.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"outliers": len(entries), "manifest_sha256": hashlib.sha256(encoded).hexdigest(),
                      "sheet_sha256": manifest["sheet_sha256"]}))


if __name__ == "__main__":
    main()
