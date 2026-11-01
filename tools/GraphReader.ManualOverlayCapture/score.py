# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score only injected train-overlay shapes after a capture report is frozen."""

from __future__ import annotations

import argparse
import gzip
from hashlib import sha256
import json
import math
from pathlib import Path
import struct

import numpy as np

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-marker-input-diagnostic-v1"
RECEIPT = ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-render-v7/receipt.json"
RECEIPT_SHA = "b7631113c9425cd06c62e4198dd173e8423013d973040e4a05ef5448bcd7a02f"


def digest(path: Path) -> str:
    value = sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def checked_json(path: Path, expected: str) -> dict:
    if len(expected) != 64 or digest(path) != expected:
        raise ValueError("input identity differs")
    return json.loads(path.read_bytes())


def assess(panels: list[dict], patch: list[int]) -> dict:
    left, top, right, bottom = patch
    if not (left < right and top < bottom):
        raise ValueError("invalid controlled patch")
    matching = []
    for panel in panels:
        crop = panel["provenance"]["encoded_crop_source_pixels"]
        if (crop["x"] <= left and crop["y"] <= top and
                crop["x"] + crop["width"] >= right and crop["y"] + crop["height"] >= bottom):
            matching.append(panel)
    if len(matching) != 1:
        return {"status": "ambiguous_or_clipped_patch"}
    panel = matching[0]
    crop = panel["provenance"]["encoded_crop_source_pixels"]
    matrix = panel["provenance"]["panel_to_source_matrix"]
    if matrix != [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1]:
        raise ValueError("panel-to-source transform differs")
    target_x, target_y = (left + right) / 2, (top + bottom) / 2
    nearby = 0
    within_patch = 0
    for marker in panel["markers"]:
        x = marker["center"]["x"] + crop["x"]
        y = marker["center"]["y"] + crop["y"]
        if not math.isfinite(x) or not math.isfinite(y):
            raise ValueError("nonfinite marker coordinate")
        nearby += math.hypot(x - target_x, y - target_y) <= 5
        within_patch += left <= x < right and top <= y < bottom
    return {"status": "scored", "nearby_centers": nearby, "centers_in_patch": within_patch,
            "axis_needs_review": panel["axis_needs_review"]}


def frame_statistics(path: Path, expected_sha: str, width: int, height: int) -> dict:
    if digest(path) != expected_sha or not (0 < width * height <= 40_000_000):
        raise ValueError("frame identity or dimensions differ")
    expected_size = 12 + 3 * width * height * 4
    with gzip.open(path, "rb") as stream:
        data = stream.read(expected_size + 1)
    if len(data) != expected_size or struct.unpack("<III", data[:12]) != (0x31465247, width, height):
        raise ValueError("marker frame format differs")
    planes = np.frombuffer(data, dtype="<f4", offset=12).reshape(3, height, width)
    if not np.isfinite(planes).all() or np.min(planes) < 0 or np.max(planes) > 1:
        raise ValueError("marker frame contains invalid plane values")
    return {"ocr_nonzero_pixels": int(np.count_nonzero(planes[1])),
            "artifact_nonzero_pixels": int(np.count_nonzero(planes[2]))}


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
    source_by_sha = {row["output_sha256"]: row for row in receipt["rows"]}
    capture_by_id = {row["id"]: row for row in capture["rows"]}
    input_ids = {row["id"] for row in manifest["rows"]}
    if (len(source_by_sha) != 200 or len(capture_by_id) != len(capture["rows"]) or
            len(input_ids) != len(manifest["rows"]) or input_ids != set(capture_by_id)):
        raise ValueError("capture inventory differs")
    results = []
    frames = []
    for item in manifest["rows"]:
        prediction = capture_by_id[item["id"]]
        source = source_by_sha[item["image_sha256"]]
        if prediction["image_sha256"] != item["image_sha256"]:
            raise ValueError("capture image identity differs")
        for panel in prediction["panels"]:
            provenance = panel["provenance"]
            crop = provenance["encoded_crop_source_pixels"]
            if (provenance["source_sha256"] != item["image_sha256"] or
                    provenance["panel_image_sha256"] != panel["panel_sha256"] or
                    crop["width"] != panel["frame_width"] or crop["height"] != panel["frame_height"] or
                    not (0 <= crop["x"] < crop["x"] + crop["width"] <= provenance["source_width"] and
                         0 <= crop["y"] < crop["y"] + crop["height"] <= provenance["source_height"])):
                raise ValueError("frame and source provenance differ")
            name = panel["frame_file"]
            if Path(name).name != name or not name.endswith(".bin.gz"):
                raise ValueError("invalid frame file name")
            frames.append(frame_statistics(capture_dir / name, panel["frame_sha256"], panel["frame_width"], panel["frame_height"]))
        base = {"id": item["id"], "partition": source["assigned_partition"], "label": source["label"],
                "shape": source["shape"]}
        if source["label"] not in ("injected_positive_only", "injected_distractor_only"):
            raise ValueError("unknown injected label")
        if prediction["status"] != "completed":
            results.append({**base, "status": "capture_failed"})
        else:
            results.append({**base, **assess(prediction["panels"], source["patch_ltrb_half_open"])})
    scored = [row for row in results if row["status"] == "scored"]
    positives = [row for row in scored if row["label"] == "injected_positive_only"]
    distractors = [row for row in scored if row["label"] == "injected_distractor_only"]
    result = {"schema": "graphreader.manual-marker-overlay-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": args.input_sha256, "report_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256, "receipt_sha256": RECEIPT_SHA,
              "scorer_source_sha256": digest(Path(__file__)),
              "center_tolerance_pixels": 5, "rows": results,
              "limitations": ["Only injected shapes in controlled patches have labels.",
                              "Unlabeled native content is never scored as negative.",
                              "White erased patches and simple shapes are not representative of complete real graphs.",
                              "The OCR and artifact inputs use unapproved candidates and do not establish release readiness."]}
    output = RUN / f"{args.mode}-score-v2.json"
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({"images": len(results), "scored": len(scored), "unavailable": len(results) - len(scored),
                      "positive_images": len(positives), "positive_with_center_within_5px": sum(row["nearby_centers"] > 0 for row in positives),
                      "positive_with_multiple_nearby": sum(row["nearby_centers"] > 1 for row in positives),
                      "distractor_images": len(distractors), "distractor_with_center_within_5px": sum(row["nearby_centers"] > 0 for row in distractors),
                      "distractor_with_any_center_in_patch": sum(row["centers_in_patch"] > 0 for row in distractors),
                      "frames": len(frames), "frames_with_ocr_mask": sum(row["ocr_nonzero_pixels"] > 0 for row in frames),
                      "frames_with_artifact_mask": sum(row["artifact_nonzero_pixels"] > 0 for row in frames),
                      "sha256": digest(output)}))


if __name__ == "__main__":
    main()
