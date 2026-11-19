# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Render an original interval-labeled graph with two series and skipped sessions."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def render() -> Image.Image:
    image = Image.new("RGB", (572, 307), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype("arial.ttf", 15)
    left, bottom, top, step = 53, 246, 25, 34
    right = left + 14 * step
    draw.line((left, top, left, bottom, right, bottom), fill="black", width=2)

    for i in range(15):
        x = left + step * i
        draw.line((x, bottom, x, bottom + 6), fill="black", width=2)
        if i < 14:
            draw.text((x + step // 2, bottom + 10), str(i + 1), anchor="mt", font=font, fill="black")
    for i in range(6):
        y = bottom - i * 40
        draw.line((left - 6, y, left, y), fill="black", width=2)
        draw.text((left - 10, y), str(i * 2), anchor="rm", font=font, fill="black")

    divider = left + step * 7
    for y in range(top, bottom - 6, 13):
        draw.line((divider, y, divider, y + 6), fill="black")
    series = [
        ([1, 2, 3, 5, 8, 10, 13], [2, 3, 2, 4, 5, 4, 7], True),
        ([2, 4, 6, 8, 11, 14], [7, 8, 7, 6, 9, 8], False),
    ]
    for sessions, values, filled in series:
        points = [(left + (session - 0.5) * step, bottom - 20 * value)
                  for session, value in zip(sessions, values, strict=True)]
        draw.line(points, fill="black", width=2)
        for x, y in points:
            box = (x - 4, y - 4, x + 4, y + 4)
            if filled:
                draw.ellipse(box, fill="black")
            else:
                draw.rectangle(box, fill="white", outline="black", width=2)
    return image


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_png", type=Path)
    args = parser.parse_args()
    with args.output_png.open("xb") as target:
        render().save(target, format="PNG")
