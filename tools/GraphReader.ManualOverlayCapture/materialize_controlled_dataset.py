# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Materialize only controlled proposals verified by the native patch sampler."""

from __future__ import annotations

from collections import Counter
import hashlib
import json
from pathlib import Path

import numpy as np

from patches import proposal_patch, read_frame
from score import RECEIPT, RECEIPT_SHA, RUN, checked_json, digest
from score_proposal_domain import INPUT_SHA

CAPTURE_SHA = "52feaf7b9f5b6625d80446ecd847366e3885bd29d3eed87d8abc74929be62383"
SELECTION_SHA = "4015f68ad77f6be5268b85670f8758cb814e988fea7afdd1d81ae50bfb644437"
PARITY_SHA = "cf8ef8312d793ed48a7c802ccbb75d6208da202e59e782bb645bb47f707f01df"
DOMAIN_SHA = "28dd02d5f5b34ffa509e5e6e40ba78c70c38187abb129882eb5fb090be424213"
PARTITIONS = ("internal_train_build", "internal_train_holdout")
LABELS = {"injected_positive_only": 1, "injected_distractor_only": 0}


def validate_rows(rows: list[dict]) -> None:
    seen, groups = set(), {name: set() for name in PARTITIONS}
    for row in rows:
        key = (row["image"], row["x"], row["y"])
        patch = row["patch"]
        if key in seen or row["partition"] not in groups or row["label"] not in (0, 1):
            raise ValueError("duplicate proposal or invalid controlled label")
        if (not isinstance(row["group"], str) or len(row["group"]) != 64 or
                patch.shape != (3, 33, 33) or patch.dtype != np.float32 or
                not np.isfinite(patch).all() or patch.min() < 0 or patch.max() > 1 or
                hashlib.sha256(patch.tobytes(order="C")).hexdigest() != row["sha256"]):
            raise ValueError("controlled proposal tensor or group identity differs")
        seen.add(key)
        groups[row["partition"]].add(row["group"])
    if not rows or groups[PARTITIONS[0]] & groups[PARTITIONS[1]]:
        raise ValueError("empty dataset or source groups cross partitions")
    for partition in PARTITIONS:
        if {row["label"] for row in rows if row["partition"] == partition} != {0, 1}:
            raise ValueError("each partition requires both controlled classes")


def save_array(path: Path, values: np.ndarray) -> str:
    with path.open("xb") as stream:
        np.save(stream, values, allow_pickle=False)
    return digest(path)


def main() -> None:
    manifest = checked_json(RUN / "full-raster-input-v1.json", INPUT_SHA)
    capture_dir = RUN / "full-stage-capture-v2"
    capture = checked_json(capture_dir / "report.json", CAPTURE_SHA)
    selection = checked_json(RUN / "full-stage-parity-input-v1.json", SELECTION_SHA)
    parity = checked_json(RUN / "full-stage-parity-result-v1.json", PARITY_SHA)
    domain = checked_json(RUN / "full-stage-domain-score-v1.json", DOMAIN_SHA)
    receipt = checked_json(RECEIPT, RECEIPT_SHA)
    if (manifest["selection"] != "full" or manifest["receipt_sha256"] != RECEIPT_SHA or
            capture["input_sha256"] != INPUT_SHA or capture["truth_read"] is not False or
            selection["capture_sha256"] != CAPTURE_SHA or selection["labels_emitted"] is not False or
            parity["input_sha256"] != SELECTION_SHA or parity["capture_sha256"] != CAPTURE_SHA or
            domain["capture_sha256"] != CAPTURE_SHA or len(manifest["rows"]) != 200 or
            (parity["selected_panels"], parity["selected_patches"], parity["matching_patches"]) != (148, 334, 334) or
            (selection["unavailable_images"], selection["images_without_emitted_target"]) != (0, 52) or
            any(value["optimizer_steps"] != 0 or value["training_approved"] is not False or
                value["sealed_payload_reads"] != 0 for value in (capture, selection, parity, domain))):
        raise ValueError("controlled dataset evidence scope differs")
    inputs = {row["id"]: row for row in manifest["rows"]}
    captures = {row["id"]: row for row in capture["rows"]}
    truths = {row["output_sha256"]: row for row in receipt["rows"]}
    parity_rows = {(row["id"], row["panel_id"]): row for row in parity["rows"]}
    if (len(inputs) != 200 or len(captures) != 200 or len(truths) != 200 or len(parity_rows) != 148 or
            set(inputs) != set(captures) or len(selection["rows"]) != 148):
        raise ValueError("controlled dataset inventory differs")
    rows = []
    selected_ids = set()
    for item in selection["rows"]:
        identifier = item["id"]
        if identifier in selected_ids:
            raise ValueError("duplicate selected image")
        selected_ids.add(identifier)
        source, captured = inputs[identifier], captures[identifier]
        truth = truths[source["image_sha256"]]
        partition, label = truth["assigned_partition"], truth["label"]
        if partition not in PARTITIONS or label not in LABELS or captured["image_sha256"] != source["image_sha256"]:
            raise ValueError("controlled image binding differs")
        panels = [panel for panel in captured["panels"] if panel["panel_id"] == item["panel_id"]]
        review = parity_rows[(identifier, item["panel_id"])]
        if (len(panels) != 1 or review["selected"] != len(item["targets"]) or
                review["matched"] != review["selected"] or review["selected"] == 0):
            raise ValueError("native per-panel parity differs")
        panel = panels[0]
        frame_file = item["frame_file"]
        if (Path(frame_file).name != frame_file or panel["frame_file"] != frame_file or
                panel["frame_sha256"] != item["frame_sha256"] or panel["frame_width"] != item["width"] or
                panel["frame_height"] != item["height"]):
            raise ValueError("captured frame identity differs")
        planes = read_frame(capture_dir / frame_file, item["frame_sha256"], item["width"], item["height"])
        for target in item["targets"]:
            x, y = target["x"], target["y"]
            patch = proposal_patch(planes, x, y)
            if patch is None:
                raise ValueError("verified emitted proposal lacks ink support")
            rows.append({"partition": partition, "label": LABELS[label], "group": truth["component_sha256"],
                         "image": identifier, "x": x, "y": y, "patch": patch,
                         "sha256": target["python_patch_sha256"]})
    if len(rows) != 334 or selected_ids != {identifier for identifier, _ in parity_rows}:
        raise ValueError("controlled dataset dropped or added parity proposals")
    validate_rows(rows)
    output = RUN / "controlled-domain-dataset-v1"
    output.mkdir(exist_ok=False)
    summary = []
    for partition in PARTITIONS:
        selected = [row for row in rows if row["partition"] == partition]
        directory = output / partition
        directory.mkdir()
        arrays = {"patches": np.stack([row["patch"] for row in selected]),
                  "labels": np.array([row["label"] for row in selected], dtype=np.uint8),
                  "groups": np.array([row["group"] for row in selected], dtype="<U64"),
                  "images": np.array([row["image"] for row in selected], dtype="<U11"),
                  "coordinates": np.array([(row["x"], row["y"]) for row in selected], dtype=np.int32),
                  "patch_sha256": np.array([row["sha256"] for row in selected], dtype="<U64")}
        files = {name: save_array(directory / f"{name}.npy", value) for name, value in arrays.items()}
        counts = Counter(row["label"] for row in selected)
        summary.append({"partition": partition, "patches": len(selected), "positive": counts[1], "distractor": counts[0],
                        "groups": len({row["group"] for row in selected}), "images": len({row["image"] for row in selected}),
                        "files": files})
    result = {"schema": "graphreader.controlled-marker-domain-dataset.v1", "scope": "train-only-controlled-overlays",
              "capture_sha256": CAPTURE_SHA, "selection_sha256": SELECTION_SHA, "parity_sha256": PARITY_SHA,
              "domain_sha256": DOMAIN_SHA, "receipt_sha256": RECEIPT_SHA, "source_sha256": digest(Path(__file__)),
              "unavailable_images": 0, "images_without_emitted_target": 52, "optimizer_steps": 0,
              "training_approved": False, "sealed_payload_reads": 0, "native_unmarked_content_labeled_negative": False,
              "partitions": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (output / "report.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"partitions": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
