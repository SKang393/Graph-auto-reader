# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Create private, flag-blind crop sheets from frozen development proposals."""

from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path

from PIL import Image, ImageDraw

from axis_connectivity_probe import ROOT, RUN, read_checked
from dev_connectivity_probe import MANIFEST_SHA

PROPOSAL_SHA = "49efae065135c157de683458d751d16e0d6f3c24efbec0300d410ef5fc13197b"


def scaled(image: Image.Image, width: int, height: int, maximum: int) -> tuple[Image.Image, float]:
    factor = min(maximum, width / image.width, height / image.height)
    return image.resize((max(1, round(image.width * factor)), max(1, round(image.height * factor))),
                        Image.Resampling.NEAREST), factor


def main() -> None:
    manifest = json.loads(read_checked(RUN / "dev-axis-capture-v1/manifest.json", MANIFEST_SHA))
    capture = json.loads(read_checked(RUN / "dev-connectivity-proposals-v1.json", PROPOSAL_SHA))
    if (capture["scope"] != "dev-diagnostic-not-acceptance" or capture["manifest_sha256"] != MANIFEST_SHA or
            capture["truth_read"] is not False or capture["optimizer_steps"] != 0 or
            capture["sealed_payload_reads"] != 0 or len(capture["rows"]) != 16):
        raise ValueError("development capture scope differs")
    source_by_id = {row["id"]: row for row in manifest["rows"]}
    items = [(row["id"], proposal) for row in capture["rows"] for panel in row["panels"]
             for proposal in panel["proposals"]]
    if (len(source_by_id) != 16 or len(items) != 134 or
            len({proposal["id"] for _, proposal in items}) != len(items)):
        raise ValueError("development proposal inventory differs")
    directory = RUN / "dev-connectivity-review-v1"
    if directory.exists():
        raise ValueError("review output already exists")
    directory.mkdir()
    cache: dict[str, Image.Image] = {}
    sheets = []
    for offset in range(0, len(items), 16):
        canvas = Image.new("RGB", (1200, 1120), "white")
        draw = ImageDraw.Draw(canvas)
        selected = items[offset:offset + 16]
        for index, (image_id, proposal) in enumerate(selected):
            source = source_by_id[image_id]
            if image_id not in cache:
                relative = source["image_path"]
                if not relative.startswith("artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/"):
                    raise ValueError("review image path differs")
                path = (ROOT / relative).resolve(strict=True)
                if not path.is_relative_to(ROOT.resolve()):
                    raise ValueError("review image escaped the project")
                image = Image.open(io.BytesIO(read_checked(path, source["image_sha256"]))).convert("RGB")
                if image.size != (source["width"], source["height"]):
                    raise ValueError("review image dimensions differ")
                cache[image_id] = image
            image = cache[image_id]
            left, top, right, bottom = proposal["box"]
            if not (0 <= left < right <= image.width and 0 <= top < bottom <= image.height):
                raise ValueError("review crop bounds differ")
            cell_x, cell_y = (index % 2) * 600, (index // 2) * 140
            draw.text((cell_x + 4, cell_y + 3), proposal["id"], fill="black")
            detail, _ = scaled(image.crop((left, top, right, bottom)), 155, 108, 8)
            canvas.paste(detail, (cell_x + 5, cell_y + 26))
            bounds = (max(0, left - 45), max(0, top - 20), min(image.width, right + 70), min(image.height, bottom + 20))
            context, factor = scaled(image.crop(bounds), 420, 108, 5)
            context_x, context_y = cell_x + 175, cell_y + 26
            canvas.paste(context, (context_x, context_y))
            draw.rectangle((context_x + (left - bounds[0]) * factor, context_y + (top - bounds[1]) * factor,
                            context_x + (right - bounds[0]) * factor, context_y + (bottom - bounds[1]) * factor),
                           outline="red", width=2)
        path = directory / f"sheet-{offset // 16 + 1:02d}.png"
        with path.open("xb") as stream:
            canvas.save(stream, format="PNG")
        sheets.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                       "proposal_ids": [proposal["id"] for _, proposal in selected]})
    value = {"schema": "graphreader.dev-connectivity-review-sheets.v1", "scope": "private-dev-review-no-flags",
             "manifest_sha256": MANIFEST_SHA, "proposal_sha256": PROPOSAL_SHA,
             "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "sheets": sheets}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (directory / "manifest.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"sheets": len(sheets), "proposals": len(items), "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
