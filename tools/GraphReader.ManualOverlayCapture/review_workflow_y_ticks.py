# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Create private prediction-blind sheets for all emitted Y-tick regions."""

from __future__ import annotations

import hashlib
import io
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw

from propose_dense_tick_windows import ROOT, RUN, checked_json
from score_workflow_tick_details import INPUT_SHA

REPORT_SHA = "20437621a2de87625273f2ca3e6148e2b06bf56f374bd6236d509873b73226a3"


def source_box(panel: dict, region: dict) -> tuple[int, int, int, int]:
    crop, box = panel["provenance"]["encoded_crop_source_pixels"], region["bounds"]
    left, top = math.floor(crop["x"] + box["left"]), math.floor(crop["y"] + box["top"])
    right, bottom = math.ceil(crop["x"] + box["right"]), math.ceil(crop["y"] + box["bottom"])
    if not (crop["x"] <= left < right <= crop["right"] and crop["y"] <= top < bottom <= crop["bottom"]):
        raise ValueError("workflow region escaped its source panel")
    return left, top, right, bottom


def main() -> None:
    inputs = checked_json(RUN / "original-raster-input-v1.json", INPUT_SHA)
    capture = checked_json(RUN / "original-workflow-full-v5/report.json", REPORT_SHA)
    if (capture["input_sha256"] != INPUT_SHA or capture["detailed_capture"] is not True or
            capture["truth_read"] is not False or capture["optimizer_steps"] != 0 or
            capture["sealed_payload_reads"] != 0):
        raise ValueError("workflow review scope differs")
    sources = {row["id"]: row for row in inputs["rows"]}
    selected = [(row, panel, region) for row in capture["rows"] for panel in row["panels"]
                for region in (panel["ocr_tick_regions"] or []) if region["role"] == "YTick"]
    if len(sources) != 20 or len(selected) != 115:
        raise ValueError("workflow Y-tick inventory differs")
    output = RUN / "workflow-y-review-v1"
    if output.exists():
        raise ValueError("workflow review output already exists")
    output.mkdir()
    cache: dict[str, Image.Image] = {}
    entries, sheets = [], []
    for offset in range(0, len(selected), 16):
        canvas = Image.new("RGB", (1200, 1120), "white")
        draw = ImageDraw.Draw(canvas)
        sheet_ids = []
        for index, (row, panel, region) in enumerate(selected[offset:offset + 16]):
            source = sources[row["id"]]
            provenance = panel["provenance"]
            if (source["image_sha256"] != provenance["source_sha256"] or not source["image_path"].startswith(
                    "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/")):
                raise ValueError("workflow review source differs")
            if source["image_sha256"] not in cache:
                path = (ROOT / source["image_path"]).resolve(strict=True)
                if not path.is_relative_to(ROOT.resolve()):
                    raise ValueError("workflow review source escaped project")
                data = path.read_bytes()
                if hashlib.sha256(data).hexdigest() != source["image_sha256"]:
                    raise ValueError("workflow review source hash differs")
                image = Image.open(io.BytesIO(data)).convert("RGB")
                if image.size != (provenance["source_width"], provenance["source_height"]):
                    raise ValueError("workflow review source dimensions differ")
                cache[source["image_sha256"]] = image
            image = cache[source["image_sha256"]]
            left, top, right, bottom = source_box(panel, region)
            identifier = f"workflow-y-{offset + index:04d}"
            cell_x, cell_y = (index % 2) * 600, (index // 2) * 140
            draw.text((cell_x + 4, cell_y + 3), identifier, fill="black")
            detail = image.crop((left, top, right, bottom))
            factor = min(8, 155 / detail.width, 108 / detail.height)
            detail = detail.resize((max(1, round(detail.width * factor)), max(1, round(detail.height * factor))),
                                   Image.Resampling.NEAREST)
            canvas.paste(detail, (cell_x + 5, cell_y + 26))
            bounds = (max(0, left - 45), max(0, top - 20), min(image.width, right + 70), min(image.height, bottom + 20))
            context = image.crop(bounds)
            scale = min(5, 420 / context.width, 108 / context.height)
            context = context.resize((max(1, round(context.width * scale)), max(1, round(context.height * scale))),
                                     Image.Resampling.NEAREST)
            x, y = cell_x + 175, cell_y + 26
            canvas.paste(context, (x, y))
            draw.rectangle((x + (left-bounds[0])*scale, y + (top-bounds[1])*scale,
                            x + (right-bounds[0])*scale, y + (bottom-bounds[1])*scale), outline="red", width=2)
            entries.append({"id": identifier, "source_id": row["id"], "panel_id": panel["panel_id"],
                            "region_id": region["region_id"], "image_sha256": source["image_sha256"],
                            "box_xyxy": [left, top, right, bottom]})
            sheet_ids.append(identifier)
        path = output / f"sheet-{offset // 16 + 1:02d}.png"
        with path.open("xb") as stream:
            canvas.save(stream, format="PNG")
        sheets.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "region_ids": sheet_ids})
    value = {"schema": "graphreader.workflow-y-review.v1", "scope": "train-private-review-no-predicted-text",
             "report_sha256": REPORT_SHA, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
             "sheets": sheets, "rows": entries}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (output / "manifest.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"regions": len(entries), "sheets": len(sheets), "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
