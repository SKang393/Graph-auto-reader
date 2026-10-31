# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Pure-Python reference for the production MarkerPatchExtractor sampling contract."""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path


def _required(value: dict, key: str):
    if key not in value:
        raise ValueError(f"missing required field: {key}")
    return value[key]


def _sample_brightness(planes, width, height, channels, x, y, bounds):
    if x < 0 or y < 0 or x > width - 1 or y > height - 1:
        return None
    x0 = math.floor(x)
    y0 = math.floor(y)
    x1 = min(x0 + 1, width - 1)
    y1 = min(y0 + 1, height - 1)
    fx = x - x0
    fy = y - y0

    def pixel(channel, px, py):
        if bounds is not None:
            left, top = bounds["x"], bounds["y"]
            right, bottom = left + bounds["width"], top + bounds["height"]
            if px < left or px >= right or py < top or py >= bottom:
                return 1.0
        return planes[(channel * width * height) + (py * width) + px]

    brightness = 0.0
    for channel in range(channels):
        tl = pixel(channel, x0, y0)
        tr = pixel(channel, x1, y0)
        bl = pixel(channel, x0, y1)
        br = pixel(channel, x1, y1)
        top = tl + ((tr - tl) * fx)
        bottom = bl + ((br - bl) * fx)
        brightness += top + ((bottom - top) * fy)
    return brightness / channels


def build_reference(payload):
    if payload.get("input", {}).get("schema") != "graphreader.marker-context-crop-evidence.v1":
        raise ValueError("unsupported evidence schema")
    if payload["input"].get("scope") != "synthetic_fixture":
        raise ValueError("only synthetic fixtures are supported")
    input_data = payload["input"]
    options = input_data["options"]
    width, height, channels = input_data["width"], input_data["height"], input_data["channelCount"]
    patch_width, patch_height = options["width"], options["height"]
    if options["outputChannels"] != 1 or channels <= 0 or patch_width <= 0 or patch_height <= 0:
        raise ValueError("unsupported plane or output shape")
    planes = input_data["channelsFirstPixels"]
    if len(planes) != width * height * channels:
        raise ValueError("source plane length does not match dimensions")
    transform = input_data["originalToFrame"]
    m11, m12, m13 = transform["m11"], transform["m12"], transform["m13"]
    m21, m22, m23 = transform["m21"], transform["m22"], transform["m23"]
    determinant = m11 * m22 - m12 * m21
    if not math.isfinite(determinant) or abs(determinant) <= 1e-12:
        raise ValueError("original-to-frame transform is not invertible")
    marker = input_data["markerCenterOriginalPixels"]
    cx = m11 * marker["x"] + m12 * marker["y"] + m13
    cy = m21 * marker["x"] + m22 * marker["y"] + m23
    scale_x = math.sqrt(m11 * m11 + m21 * m21)
    scale_y = math.sqrt(m12 * m12 + m22 * m22)
    frame_radius = input_data["markerRadiusOriginalPixels"] * math.sqrt(scale_x * scale_y)
    half = max(options["minimumHalfExtentFramePixels"], frame_radius * options["radiusScale"])
    patch_scale_x = 2.0 * half / patch_width
    patch_scale_y = 2.0 * half / patch_height
    patch_offset_x = cx - half + (half / patch_width)
    patch_offset_y = cy - half + (half / patch_height)
    patch_to_frame = [patch_scale_x, 0.0, patch_offset_x,
                      0.0, patch_scale_y, patch_offset_y,
                      0.0, 0.0, 1.0]
    inverse = [m22 / determinant, -m12 / determinant, ((m12 * m23) - (m22 * m13)) / determinant,
               -m21 / determinant, m11 / determinant, ((m21 * m13) - (m11 * m23)) / determinant,
               0.0, 0.0, 1.0]

    def multiply(left, right):
        return [sum(left[row * 3 + inner] * right[inner * 3 + column] for inner in range(3))
                for row in range(3) for column in range(3)]

    patch_to_original = multiply(inverse, patch_to_frame)
    coordinate_errors = [abs(expected - observed) for expected, observed in zip(
        patch_to_frame + patch_to_original,
        payload["patchToFrameMatrix3x3"] + payload["patchToOriginalMatrix3x3"], strict=True)]
    bounds = options["originalPixelContentBounds"]
    tensor = []
    padding_samples = 0
    for py in range(patch_height):
        source_y = cy + ((((py + 0.5) / patch_height) * 2.0) - 1.0) * half
        for px in range(patch_width):
            source_x = cx + ((((px + 0.5) / patch_width) * 2.0) - 1.0) * half
            brightness = _sample_brightness(planes, width, height, channels, source_x, source_y, bounds)
            if brightness is None:
                tensor.append(float(options["paddingValue"]))
                padding_samples += 1
            else:
                tensor.append(1.0 - brightness)
    actual = payload["productionTensor"]
    if len(tensor) != len(actual):
        raise ValueError("production tensor length differs from Python reference")
    tolerance = 2e-6
    absolute_errors = [abs(expected - observed) for expected, observed in zip(tensor, actual, strict=True)]
    maximum_error = max(absolute_errors, default=0.0)
    mismatches = sum(error > tolerance for error in absolute_errors)
    return {
        "schema": "graphreader.marker-context-crop-parity.v1",
        "scope": "synthetic_fixture",
        "case_id": input_data["caseId"],
        "input_shape_nchw": [1, channels, height, width],
        "output_shape_nchw": payload["outputShapeNchw"],
        "sample_count": len(tensor),
        "padding_samples": padding_samples,
        "absolute_tolerance": tolerance,
        "maximum_absolute_error": maximum_error,
        "mismatched_values": mismatches,
        "maximum_transform_absolute_error": max(coordinate_errors, default=0.0),
        "parity": mismatches == 0 and max(coordinate_errors, default=0.0) <= 1e-9,
        "optimizer_steps": 0,
        "annotations_read": False,
        "private_or_sealed_data_read": False,
    }


def main(argv):
    if len(argv) != 3 or argv[1] != "--compare":
        raise SystemExit("usage: python marker_context_reference.py --compare evidence.json")
    payload = json.loads(Path(argv[2]).read_text(encoding="utf-8"))
    result = build_reference(payload)
    print(json.dumps(result, sort_keys=True))
    return 0 if result["parity"] else 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
