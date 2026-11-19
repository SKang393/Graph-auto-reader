# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Render the project-owned narrow-margin Y-tick controls using Pillow on Windows."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def render(output: Path, kind: str, font: ImageFont.FreeTypeFont) -> None:
    image = Image.new("RGB", (612, 328), "white")
    draw = ImageDraw.Draw(image)
    left, right, top, bottom = 31, 580, 30, 270
    draw.line((left, top, left, bottom, right, bottom), fill="black", width=2)
    for index in range(6):
        y = bottom - 48 * index
        draw.line((left - 6, y, left, y), fill="black", width=2)
        if kind == "labeled":
            draw.text((left - 10, y), str(index), anchor="rm", font=font, fill="black")
        if kind == "nearby" and index in (1, 3):
            draw.text((left - 10, y - 19), "8", anchor="rm", font=font, fill="black")
    if kind == "nearby":
        draw.text((2, 4), "ID", font=font, fill="black")

    for index in range(6):
        x = left + 100 * index
        draw.line((x, bottom, x, bottom + 6), fill="black", width=2)
        draw.text((x, bottom + 9), str(index + 1), anchor="mt", font=font, fill="black")
    points = [(left + 100 * i, bottom - 17 - 29 * i - (i % 2) * 27) for i in range(6)]
    draw.line(points, fill="black", width=2)
    for x, y in points:
        draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill="black")
    with (output / f"{kind}.png").open("xb") as target:
        image.save(target, format="PNG")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    label_font = ImageFont.truetype("arial.ttf", 12)
    for variant in ("labeled", "blank", "nearby"):
        render(args.output, variant, label_font)
