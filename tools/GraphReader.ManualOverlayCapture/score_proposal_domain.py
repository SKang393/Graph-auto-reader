# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare controlled overlay positions with actual emitted proposal centers."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import gzip
import hashlib
import json
from pathlib import Path

from patches import PATCH_RADIUS, lattice_near, proposal_patch, read_frame
from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest

INPUT_SHA = "f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608"


def emitted_centers(stage: dict, width: int, height: int) -> set[tuple[int, int]]:
    if stage.get("schema") != "graphreader.manual-marker-stage-evidence.v1":
        raise ValueError("stage schema differs")
    points = stage["emitted_proposals"]
    emitted = set()
    for point in points:
        x, y = point["x"], point["y"]
        if (type(x) is not int or type(y) is not int or point.get("is_finite") is not True or
                not (0 <= x < width and 0 <= y < height) or x % 4 or y % 4 or (x, y) in emitted):
            raise ValueError("invalid or duplicate emitted proposal")
        emitted.add((x, y))
    if stage["counters"]["emitted_proposals"] != len(emitted):
        raise ValueError("emitted proposal count differs")
    return emitted


def summarize_patch(planes, target_x: float, target_y: float, bounds: tuple[int, int, int, int],
                    emitted: set[tuple[int, int]]) -> dict:
    _, height, width = planes.shape
    left, top, right, bottom = bounds
    result = Counter()
    for x, y in lattice_near(target_x, target_y, width, height):
        if not (left <= x - PATCH_RADIUS and x + PATCH_RADIUS < right and
                top <= y - PATCH_RADIUS and y + PATCH_RADIUS < bottom):
            raise ValueError("proposal patch escapes controlled region")
        supported = proposal_patch(planes, x, y) is not None
        was_emitted = (x, y) in emitted
        if was_emitted and not supported:
            raise ValueError("runtime emitted a proposal without mirrored ink support")
        result["nearby_lattice_positions"] += 1
        result["ink_supported_positions"] += supported
        result["emitted_positions"] += was_emitted
        result["supported_not_emitted"] += supported and not was_emitted
    return dict(result)


def read_stage(path: Path, expected: str) -> dict:
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 100_000_000 or digest(path) != expected:
        raise ValueError("stage file identity differs")
    with gzip.open(path, "rb") as stream:
        data = stream.read(300_000_001)
    if len(data) > 300_000_000:
        raise ValueError("stage output exceeds size limit")
    return json.loads(data)


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
    counts = defaultdict(Counter)
    groups = defaultdict(set)
    for item in manifest["rows"]:
        truth = sources[item["image_sha256"]]
        partition, label = truth["assigned_partition"], truth["label"]
        if partition not in ("internal_train_build", "internal_train_holdout") or label not in (
                "injected_positive_only", "injected_distractor_only"):
            raise ValueError("unexpected partition or controlled label")
        groups[partition].add(truth["component_sha256"])
        bucket = counts[(partition, label)]
        bucket["images"] += 1
        prediction = captured[item["id"]]
        if prediction["image_sha256"] != item["image_sha256"]:
            raise ValueError("captured image identity differs")
        if prediction["status"] != "completed":
            bucket["capture_failed"] += 1
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
            bucket["missing_or_ambiguous_panel"] += 1
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
        local_bounds = (left - crop["x"], top - crop["y"], right - crop["x"], bottom - crop["y"])
        result = summarize_patch(planes, (left + right) / 2 - crop["x"], (top + bottom) / 2 - crop["y"],
                                 local_bounds, emitted)
        bucket["evaluated"] += 1
        bucket.update(result)
        bucket["images_with_emitted_proposal"] += result.get("emitted_positions", 0) > 0
    if groups["internal_train_build"] & groups["internal_train_holdout"]:
        raise ValueError("source groups overlap")
    summary = {f"{partition}:{label}": dict(value) for (partition, label), value in sorted(counts.items())}
    result = {"schema": "graphreader.manual-overlay-domain-score.v1", "scope": "train-only-diagnostic-not-acceptance",
              "input_sha256": INPUT_SHA, "capture_sha256": args.report_sha256, "runtime_sha256": args.runtime_sha256,
              "receipt_sha256": RECEIPT_SHA, "source_sha256": digest(Path(__file__)), "optimizer_steps": 0,
              "training_approved": False, "sealed_payload_reads": 0, "groups": {k: len(v) for k, v in groups.items()},
              "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "full-stage-domain-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "groups": result["groups"], "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
