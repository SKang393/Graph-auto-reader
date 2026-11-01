# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Materialize controlled, source-grouped proposal patches for diagnostic review."""

from __future__ import annotations

import argparse
import gzip
from hashlib import sha256
import json
import math
from pathlib import Path
import struct

import numpy as np

from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest

PATCH_SIZE = 33
PATCH_RADIUS = 16
STRIDE = 4
SUPPORT_RADIUS = 8
SUPPORT_THRESHOLD = np.float32(0.11)


def read_frame(path: Path, expected_sha: str, width: int, height: int) -> np.ndarray:
    if digest(path) != expected_sha or not 0 < width * height <= 40_000_000:
        raise ValueError("frame identity or dimensions differ")
    size = 12 + 3 * width * height * 4
    with gzip.open(path, "rb") as stream:
        payload = stream.read(size + 1)
    if len(payload) != size or struct.unpack("<III", payload[:12]) != (0x31465247, width, height):
        raise ValueError("frame format differs")
    planes = np.frombuffer(payload, dtype="<f4", offset=12).reshape(3, height, width)
    if not np.isfinite(planes).all() or planes.min() < 0 or planes.max() > 1:
        raise ValueError("frame plane values differ")
    return planes


def proposal_patch(planes: np.ndarray, x: int, y: int) -> np.ndarray | None:
    """Mirror the mask-preserving candidate's four-pixel lattice and 33px patch."""
    if planes.ndim != 3 or planes.shape[0] != 3 or planes.dtype != np.float32:
        raise ValueError("three float32 frame planes required")
    _, height, width = planes.shape
    if x % STRIDE or y % STRIDE or not (0 <= x < width and 0 <= y < height):
        raise ValueError("point is outside the proposal lattice")
    x0, x1 = max(0, x - SUPPORT_RADIUS), min(width, x + SUPPORT_RADIUS + 1)
    y0, y1 = max(0, y - SUPPORT_RADIUS), min(height, y + SUPPORT_RADIUS + 1)
    if np.max(np.float32(1) - planes[0, y0:y1, x0:x1]) < SUPPORT_THRESHOLD:
        return None
    patch = np.zeros((3, PATCH_SIZE, PATCH_SIZE), dtype=np.float32)
    left, right = max(0, x - PATCH_RADIUS), min(width, x + PATCH_RADIUS + 1)
    top, bottom = max(0, y - PATCH_RADIUS), min(height, y + PATCH_RADIUS + 1)
    px, py = left - (x - PATCH_RADIUS), top - (y - PATCH_RADIUS)
    target = patch[:, py:py + bottom - top, px:px + right - left]
    target[0] = np.float32(1) - planes[0, top:bottom, left:right]
    target[1:] = planes[1:, top:bottom, left:right]
    return patch


def lattice_near(x: float, y: float, width: int, height: int) -> list[tuple[int, int]]:
    xs = range(max(0, math.ceil((x - 3) / STRIDE)), min((width - 1) // STRIDE, math.floor((x + 3) / STRIDE)) + 1)
    ys = range(max(0, math.ceil((y - 3) / STRIDE)), min((height - 1) // STRIDE, math.floor((y + 3) / STRIDE)) + 1)
    return [(gx * STRIDE, gy * STRIDE) for gy in ys for gx in xs if math.hypot(gx * STRIDE - x, gy * STRIDE - y) <= 3]


def save_array(path: Path, value: np.ndarray) -> str:
    with path.open("xb") as stream:
        np.save(stream, value, allow_pickle=False)
    return digest(path)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("smoke", "full"))
    parser.add_argument("--input-sha256", required=True)
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    manifest = checked_json(RUN / f"{args.mode}-raster-input-v1.json", args.input_sha256)
    capture_dir = RUN / f"{args.mode}-capture-v1"
    capture = checked_json(capture_dir / "report.json", args.report_sha256)
    receipt = checked_json(RECEIPT, RECEIPT_SHA)
    if (manifest["schema"] != "graphreader.manual-marker-raster-input.v1" or
            manifest["scope"] != "train-only-raster-no-truth" or manifest["training_approved"] is not False or
            manifest["receipt_sha256"] != RECEIPT_SHA or manifest["selection"] != args.mode or
            capture["schema"] != "graphreader.manual-marker-input-capture.v1" or
            capture["input_sha256"] != args.input_sha256 or capture["runtime_description_sha256"] != args.runtime_sha256 or
            capture["truth_read"] is not False or capture["optimizer_steps"] != 0 or capture["training_approved"] is not False):
        raise ValueError("capture scope differs")
    source = {row["output_sha256"]: row for row in receipt["rows"]}
    predictions = {row["id"]: row for row in capture["rows"]}
    if (len(source) != 200 or len(predictions) != len(capture["rows"]) or
            len({row["id"] for row in manifest["rows"]}) != len(manifest["rows"]) or
            set(predictions) != {row["id"] for row in manifest["rows"]}):
        raise ValueError("input inventory differs")
    output = RUN / f"{args.mode}-patches-v1"
    output.mkdir(exist_ok=False)
    partitions = {"internal_train_build": [], "internal_train_holdout": []}
    unavailable = 0
    for item in manifest["rows"]:
        prediction = predictions[item["id"]]
        truth = source[item["image_sha256"]]
        if prediction["image_sha256"] != item["image_sha256"] or truth["assigned_partition"] not in partitions:
            raise ValueError("image or partition identity differs")
        if prediction["status"] != "completed":
            unavailable += 1
            continue
        left, top, right, bottom = truth["patch_ltrb_half_open"]
        matches = []
        for panel in prediction["panels"]:
            provenance = panel["provenance"]
            crop = provenance["encoded_crop_source_pixels"]
            if (provenance["source_sha256"] != item["image_sha256"] or
                    provenance["panel_image_sha256"] != panel["panel_sha256"] or
                    provenance["panel_to_source_matrix"] != [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1] or
                    crop["width"] != panel["frame_width"] or crop["height"] != panel["frame_height"]):
                raise ValueError("panel provenance differs")
            if (crop["x"] <= left and crop["y"] <= top and
                    crop["x"] + crop["width"] >= right and crop["y"] + crop["height"] >= bottom):
                matches.append(panel)
        if len(matches) != 1:
            unavailable += 1
            continue
        panel = matches[0]
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        frame_name = panel["frame_file"]
        if Path(frame_name).name != frame_name or not frame_name.endswith(".bin.gz"):
            raise ValueError("frame file name differs")
        planes = read_frame(capture_dir / frame_name, panel["frame_sha256"], panel["frame_width"], panel["frame_height"])
        target_x = (left + right) / 2 - crop["x"]
        target_y = (top + bottom) / 2 - crop["y"]
        if truth["label"] == "injected_positive_only":
            label = 1
        elif truth["label"] == "injected_distractor_only":
            label = 0
        else:
            raise ValueError("unknown controlled label")
        for x, y in lattice_near(target_x, target_y, panel["frame_width"], panel["frame_height"]):
            source_x, source_y = x + crop["x"], y + crop["y"]
            if not (left <= source_x - PATCH_RADIUS and source_x + PATCH_RADIUS < right and
                    top <= source_y - PATCH_RADIUS and source_y + PATCH_RADIUS < bottom):
                raise ValueError("proposal input extends beyond the controlled patch")
            patch = proposal_patch(planes, x, y)
            if patch is not None:
                partitions[truth["assigned_partition"]].append((patch, label, truth["component_sha256"], item["id"]))
    groups = [{row[2] for row in rows} for rows in partitions.values()]
    if groups[0] & groups[1]:
        raise ValueError("source groups cross build and holdout")
    output_rows = []
    for name, rows in partitions.items():
        if not rows:
            raise ValueError("partition has no proposal patches")
        directory = output / name
        directory.mkdir()
        patches = np.stack([row[0] for row in rows])
        labels = np.array([row[1] for row in rows], dtype=np.uint8)
        group_ids = np.array([row[2] for row in rows], dtype="<U64")
        image_ids = np.array([row[3] for row in rows], dtype="<U11")
        files = {key: save_array(directory / f"{key}.npy", value) for key, value in
                 (("patches", patches), ("labels", labels), ("groups", group_ids), ("images", image_ids))}
        output_rows.append({"partition": name, "patches": len(rows), "positive": int(labels.sum()),
                            "negative": int(len(labels) - labels.sum()), "groups": len(set(group_ids.tolist())),
                            "unique_ink": len({sha256(row[0][0].tobytes()).digest() for row in rows}),
                            "unique_all_channels": len({sha256(row[0].tobytes()).digest() for row in rows}), "files": files})
    result = {"schema": "graphreader.manual-marker-controlled-patches.v1", "scope": "diagnostic-not-admitted-for-training",
              "input_sha256": args.input_sha256, "report_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256, "receipt_sha256": RECEIPT_SHA,
              "source_sha256": digest(Path(__file__)), "optimizer_steps": 0, "training_approved": False,
              "plot_domain_membership_verified": False, "unavailable_images": unavailable, "partitions": output_rows,
              "limitations": ["Only the controlled white patch is labeled; all native content is unknown.",
                              "Grid positions use runtime patch layout but their plot-domain membership is not captured.",
                              "Repeated simple shapes are not independent real marker examples."]}
    with (output / "report.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"unavailable_images": unavailable, "partitions": output_rows, "report_sha256": digest(output / "report.json")}))


if __name__ == "__main__":
    main()
