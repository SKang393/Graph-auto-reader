# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Measure the approved classifier's response to controlled changes in patch context.

Only the model is an input. The drawings, proposal radii and proposal offsets
are fixed. The four views are diagnostic interventions on the same proposal.
"""
import argparse
import hashlib
import json
import sys
from collections import Counter
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ml/markers/classifier"))
from runtime_patches import extract_original_patch, original_luminance

MODEL_SHA256 = "26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a"
OFFSETS = ((0, 0), (2, 0), (0, 2), (2, -2))
RADIUS = 5
ORIGIN = 32
VIEWS = ("original", "centered", "bounded", "bounded_centered")


def draw_patch(symbol, context, stroke_width=2):
    scale = 4
    image = Image.new("L", (65 * scale, 65 * scale), 255)
    draw = ImageDraw.Draw(image)

    def coords(points):
        return [((ORIGIN + x) * scale, (ORIGIN + y) * scale) for x, y in points]

    def line(*points):
        draw.line(coords(points), fill=74, width=stroke_width * scale)

    if context == "flat":
        line((-30, 0), (30, 0))
    elif context == "slope":
        line((-16, 24), (0, 0), (24, 12))
    elif context == "steep":
        draw.line(coords([(-12, -30), (0, 0), (14, 29)]), fill=74, width=3 * scale)

    if symbol == "diamond":
        draw.polygon(coords([(-5, 0), (0, -5), (5, 0), (0, 5)]), fill=55)
    elif symbol == "circle":
        draw.ellipse(coords([(-5, -5), (5, 5)]), fill=55)
    elif symbol == "crossing":
        line((-25, -25), (25, 25))
        line((-25, 25), (25, -25))
    elif symbol == "divider":
        for top, bottom in ((-24, -12), (-4, 4), (12, 24)):
            line((0, top), (0, bottom))
    elif symbol == "corner":
        line((0, -22), (0, 0), (22, 0))
    elif symbol == "eight":
        for top, bottom in ((-6, 0), (0, 6)):
            draw.ellipse(coords([(-3, top), (3, bottom)]), outline=55, width=2 * scale)
    elif symbol == "straight":
        line((-20, 0), (20, 0))
    else:
        structures = {
            "bend": [[(-18, -30), (0, 0), (18, -30)]],
            "arrow": [[(-24, 0), (0, 0)], [(-9, 6), (0, 0), (-9, -6)]],
            "tee": [[(-26, 0), (26, 0)], [(0, 0), (0, 25)]],
            "bracket": [[(-26, 0), (0, 0), (0, 9), (-26, 9)]],
            "dash": [[(-1, -5), (0, 5)]],
            "diagonal_crossing": [[(-22, -22), (22, 22)], [(22, -22), (-22, 22)]],
        }
        for path in structures[symbol]:
            draw.line(coords(path), fill=50, width=stroke_width * scale)
    return original_luminance(image.resize((65, 65), Image.Resampling.LANCZOS))


def drawings():
    for symbol in ("diamond", "circle"):
        for context in ("none", "flat", "slope", "steep"):
            yield True, symbol, context, 2
    for symbol in ("crossing", "divider", "corner", "eight", "straight"):
        yield False, symbol, "none", 2
    for symbol in ("bend", "arrow", "tee", "bracket", "dash", "diagonal_crossing"):
        for width in (2, 3, 4):
            yield False, symbol, "none", width


def thickest_center(image, x, y):
    """Center of one eight-connected plateau at maximum distance from paper."""
    rows, columns = np.indices(image.shape)
    candidates = np.argwhere((image < 0.5) & ((columns - x)**2 + (rows - y)**2 <= RADIUS**2))
    if not len(candidates):
        return None
    paper = np.argwhere(image >= 0.5)
    distances = np.square(candidates[:, None, :] - paper).sum(axis=2).min(axis=1)
    peak = candidates[distances == distances.max()]
    remaining = set(map(tuple, peak))
    pending = [remaining.pop()]
    while pending:
        row, column = pending.pop()
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                neighbor = (row + dy, column + dx)
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    pending.append(neighbor)
    if remaining:
        return None
    cy, cx = peak.mean(axis=0)
    return cx, cy


def views(image, x, y):
    raw = extract_original_patch(image, (x, y), RADIUS)
    center = thickest_center(image, x, y)
    if center is None:
        return [raw] * len(VIEWS), False
    cx, cy = center
    rows, columns = np.indices(image.shape)
    inside = (abs(columns - cx) <= RADIUS) & (abs(rows - cy) <= RADIUS)
    bounded = np.where(inside, image, np.float32(1))
    return [
        raw, extract_original_patch(image, center, RADIUS),
        extract_original_patch(bounded, (x, y), RADIUS),
        extract_original_patch(bounded, center, RADIUS),
    ], True


def evaluate(model_path):
    if hashlib.sha256(model_path.read_bytes()).hexdigest() != MODEL_SHA256:
        raise ValueError("Unexpected marker classifier payload.")
    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(model_path), options, providers=["CPUExecutionProvider"])
    totals = Counter()
    flips = {view: Counter() for view in VIEWS[1:]}
    by_shape = {}
    for is_marker, symbol, context, width in drawings():
        image = draw_patch(symbol, context, width)
        group = "markers" if is_marker else "structures"
        scores = []
        for dx, dy in OFFSETS:
            patches, measured = views(image, ORIGIN + dx, ORIGIN + dy)
            # Column 12 in the approved packed output is the artifact probability.
            probabilities = session.run(None, {"marker_patch": np.stack(patches)})[0][:, 12]
            scores.append(probabilities.tolist())
            totals[group, "cases"] += 1
            totals[group, "center_available"] += int(measured)
            for view, probability in zip(VIEWS, probabilities, strict=True):
                if probability < 0.5:
                    totals[group, view] += 1
                    if probabilities[0] >= 0.5:
                        flips[view][symbol] += 1
        by_shape[f"{symbol}/{context}/{width}"] = scores
    return {
        "model_sha256": MODEL_SHA256, "offsets": OFFSETS, "view_order": VIEWS,
        "counts": {group: {key: totals[group, key] for key in ("cases", "center_available", *VIEWS)}
                   for group in ("markers", "structures")},
        "raw_rejections_overturned": flips, "artifact_probabilities": by_shape,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path, help="Local approved packed classifier ONNX")
    print(json.dumps(evaluate(parser.parse_args().model), indent=2))
