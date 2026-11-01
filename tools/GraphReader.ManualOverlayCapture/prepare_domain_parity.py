# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Prepare private, controlled-position Python hashes for native sampler parity."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from patches import PATCH_RADIUS, lattice_near, proposal_patch, read_frame
from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest
from score_proposal_domain import INPUT_SHA, emitted_centers, read_stage


def selected_patches(planes: np.ndarray, target_x: float, target_y: float,
                     bounds: tuple[int, int, int, int], emitted: set[tuple[int, int]]) -> list[dict]:
    if not np.little_endian:
        raise ValueError("patch parity requires little-endian float32")
    _, height, width = planes.shape
    left, top, right, bottom = bounds
    result = []
    for x, y in lattice_near(target_x, target_y, width, height):
        if not (left <= x - PATCH_RADIUS and x + PATCH_RADIUS < right and
                top <= y - PATCH_RADIUS and y + PATCH_RADIUS < bottom):
            raise ValueError("proposal patch escapes controlled region")
        if (x, y) not in emitted:
            continue
        patch = proposal_patch(planes, x, y)
        if patch is None:
            raise ValueError("emitted patch lacks mirrored ink support")
        result.append({"x": x, "y": y, "python_patch_sha256": hashlib.sha256(patch.tobytes(order="C")).hexdigest()})
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    manifest = checked_json(RUN / "full-raster-input-v1.json", INPUT_SHA)
    capture_dir = RUN / "full-stage-capture-v2"
    capture = checked_json(capture_dir / "report.json", args.report_sha256)
    receipt = checked_json(RECEIPT, RECEIPT_SHA)
    if (manifest["schema"] != "graphreader.manual-marker-raster-input.v1" or
            manifest["scope"] != "train-only-raster-no-truth" or manifest["training_approved"] is not False or
            manifest["selection"] != "full" or manifest["receipt_sha256"] != RECEIPT_SHA or
            capture["schema"] != "graphreader.manual-marker-input-capture.v1" or
            capture["scope"] != "train-only-unapproved-candidate-not-acceptance" or
            capture["input_sha256"] != INPUT_SHA or capture["runtime_description_sha256"] != args.runtime_sha256 or
            capture["truth_read"] is not False or capture["optimizer_steps"] != 0 or
            capture["training_approved"] is not False or capture["classification_executed"] is not False or
            capture["sealed_payload_reads"] != 0 or
            len(manifest["rows"]) != 200 or len(capture["rows"]) != 200 or len(receipt["rows"]) != 200):
        raise ValueError("capture scope or denominator differs")
    sources = {row["output_sha256"]: row for row in receipt["rows"]}
    captured = {row["id"]: row for row in capture["rows"]}
    if (len(sources) != 200 or len(captured) != 200 or len({row["id"] for row in manifest["rows"]}) != 200 or
            set(captured) != {row["id"] for row in manifest["rows"]}):
        raise ValueError("capture inventory differs")
    rows = []
    unavailable = no_emitted = 0
    for item in manifest["rows"]:
        truth, prediction = sources[item["image_sha256"]], captured[item["id"]]
        if prediction["image_sha256"] != item["image_sha256"]:
            raise ValueError("captured image identity differs")
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
        frame_file, stage_file = panel["frame_file"], panel["stage_file"]
        if (Path(frame_file).name != frame_file or not frame_file.endswith(".bin.gz") or
                Path(stage_file).name != stage_file or not stage_file.endswith("-stages.json.gz")):
            raise ValueError("captured file path differs")
        planes = read_frame(capture_dir / frame_file, panel["frame_sha256"], panel["frame_width"], panel["frame_height"])
        stage = read_stage(capture_dir / stage_file, panel["stage_sha256"])
        emitted = emitted_centers(stage, panel["frame_width"], panel["frame_height"])
        bounds = (left - crop["x"], top - crop["y"], right - crop["x"], bottom - crop["y"])
        targets = selected_patches(planes, (left + right) / 2 - crop["x"], (top + bottom) / 2 - crop["y"], bounds, emitted)
        if not targets:
            no_emitted += 1
            continue
        rows.append({"id": item["id"], "panel_id": panel["panel_id"], "frame_file": frame_file,
                     "frame_sha256": panel["frame_sha256"], "stage_file": stage_file,
                     "stage_sha256": panel["stage_sha256"], "width": panel["frame_width"],
                     "height": panel["frame_height"], "targets": targets})
    if not rows or len(rows) + unavailable + no_emitted != 200:
        raise ValueError("selected parity inventory differs")
    result = {"schema": "graphreader.manual-overlay-patch-parity-input.v1",
              "scope": "private-train-only-controlled-positions", "input_sha256": INPUT_SHA,
              "capture_sha256": args.report_sha256, "capture_runtime_sha256": args.runtime_sha256,
              "receipt_sha256": RECEIPT_SHA, "source_sha256": digest(Path(__file__)),
              "selection_uses_controlled_coordinates": True, "labels_emitted": False, "optimizer_steps": 0,
              "training_approved": False, "sealed_payload_reads": 0, "unavailable_images": unavailable,
              "images_without_emitted_target": no_emitted, "rows": rows}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "full-stage-parity-input-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"selected_images": len(rows), "selected_patches": sum(len(row["targets"]) for row in rows),
                      "unavailable_images": unavailable, "images_without_emitted_target": no_emitted,
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
