# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate audit of fixed train-only inverse-unclip targets, without training."""
from collections import Counter
from hashlib import sha256
import json
import math
from pathlib import Path

import cv2
import numpy as np

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight, parity, tensor_parity
from ml.ocr.official_bakeoff import db_inverse_targets as targets


def _unclamped_box(region, width, height):
    """Counterfactual only: retain rounding/fit but remove minimum extent."""
    left, top, right, bottom = region.continuous_contour_box
    extent_x = math.floor(right - left + 0.5)
    extent_y = math.floor(bottom - top + 0.5)
    x0, y0, x1, y1 = region.tensor_box
    raster_x = math.floor((x0 + x1 - extent_x) / 2 + 0.5)
    raster_y = math.floor((y0 + y1 - extent_y) / 2 + 0.5)
    raster_x = min(max(0, raster_x), width - 1 - extent_x)
    raster_y = min(max(0, raster_y), height - 1 - extent_y)
    return raster_x, raster_y, raster_x + extent_x, raster_y + extent_y


def _touch(a, b):
    return (max(0, max(a[0], b[0]) - min(a[2], b[2])) <= 1 and
            max(0, max(a[1], b[1]) - min(a[3], b[3])) <= 1)


def analyze(built, identities):
    if len(identities) != len(built.regions) or len({item[0] for item in identities}) != len(identities):
        raise ValueError("Text instance identities must be distinct")
    height, width = built.target.shape[1:]
    _, labels = cv2.connectedComponents(built.target[0].astype(np.uint8), connectivity=8)
    component_regions = Counter()
    for region in built.regions:
        left, top, right, bottom = region.raster_contour_box
        component = int(labels[top, left])
        if component == 0 or not np.all(labels[top:bottom + 1, left:right + 1] == component):
            raise ValueError("Target raster does not match target region")
        component_regions[component] += 1
    pair_kinds = Counter()
    role_pairs = Counter()
    contacts_with_clamps = 0
    contacts_without_minimum = 0
    for contact in built.contacts:
        first, second = built.regions[contact.first_index], built.regions[contact.second_index]
        if identities[first.index][0] == identities[second.index][0]:
            raise ValueError("Contact does not identify separate text instances")
        pair_kinds[contact.kind] += 1
        roles = sorted((identities[first.index][1], identities[second.index][1]))
        role_pairs[" + ".join(roles)] += 1
        if (first.minimum_extent_clamped_x or first.minimum_extent_clamped_y or
                second.minimum_extent_clamped_x or second.minimum_extent_clamped_y):
            contacts_with_clamps += 1
        contacts_without_minimum += _touch(_unclamped_box(first, width, height), _unclamped_box(second, width, height))
    clamped = [r for r in built.regions if r.minimum_extent_clamped_x or r.minimum_extent_clamped_y]
    roles = Counter(identities[r.index][1] for r in clamped)
    factors = []
    larger_than_source = 0
    for region in clamped:
        left, top, right, bottom = region.continuous_contour_box
        factors.append((region.raster_extent[0] * region.raster_extent[1]) / ((right - left) * (bottom - top)))
        x0, y0, x1, y1 = region.tensor_box
        larger_than_source += region.raster_extent[0] > x1 - x0 or region.raster_extent[1] > y1 - y0
    return {"text_instances": len(identities), "direct_contacts": len(built.contacts),
            "contact_kinds": dict(sorted(pair_kinds.items())), "contact_role_pairs": dict(sorted(role_pairs.items())),
            "contacts_involving_clamp": contacts_with_clamps,
            "contacts_remaining_without_minimum_extent": contacts_without_minimum,
            "components_with_multiple_instances": sum(count > 1 for count in component_regions.values()),
            "instances_in_multi_instance_components": sum(count for count in component_regions.values() if count > 1),
            "clamped_instances": len(clamped), "clamped_x": sum(r.minimum_extent_clamped_x for r in clamped),
            "clamped_y": sum(r.minimum_extent_clamped_y for r in clamped),
            "clamped_both": sum(r.minimum_extent_clamped_x and r.minimum_extent_clamped_y for r in clamped),
            "clamped_roles": dict(sorted(roles.items())), "clamped_extent_larger_than_source_box": larger_than_source,
            "maximum_clamped_extent_area_to_continuous_inverse_area": max(factors, default=0.0)}


def run(output: Path) -> dict:
    root = (parity.ROOT / "artifacts").resolve()
    output = output.resolve()
    if not output.is_relative_to(root) or output.exists():
        raise ValueError("Report must be a new project artifacts file")
    previous = preflight._json(parity._read_locked(tensor_parity.PREFLIGHT, tensor_parity.PREFLIGHT_SHA))
    records = preflight._train_inputs()
    if len(records) != 4 or len(previous.get("cases", [])) != 4:
        raise ValueError("Expected the bound four train scenes")
    results = []
    for (row, image, annotation), saved in zip(records, previous["cases"], strict=True):
        if row["image"]["sha256"] != saved["source_image_sha256"] or row["annotation"]["sha256"] != saved["source_annotation_sha256"]:
            raise ValueError("Preflight source binding differs")
        tensor, target, _mask, metrics = preflight._prepare(image, annotation)
        if sha256(tensor.tobytes()).hexdigest() != saved["tensor_sha256"] or metrics != saved["counts"]:
            raise ValueError("Preflight tensor or target counts differ")
        height, width = tensor.shape[2:]
        canvas = annotation["canvas"]
        boxes, identities = [], []
        for panel in annotation["panels"]:
            for text in panel["texts"]:
                x, y, w, h = preflight._box(text["rendered_pixel_box"], canvas["width"], canvas["height"])
                boxes.append((x * width / canvas["width"], y * height / canvas["height"],
                              (x + w) * width / canvas["width"], (y + h) * height / canvas["height"]))
                identities.append((text["text_id"], text["role"]))
        built = targets.build_inverse_targets(width, height, boxes)
        if not np.array_equal(built.target, target):
            raise ValueError("Reconstructed target raster differs")
        results.append(analyze(built, identities))
    counters = ("contact_kinds", "contact_role_pairs", "clamped_roles")
    aggregate = {key: sum(row[key] for row in results) for key in results[0] if key not in counters and not key.startswith("maximum_")}
    for key in counters:
        aggregate[key] = dict(sorted(sum((Counter(row[key]) for row in results), Counter()).items()))
    aggregate["maximum_clamped_extent_area_to_continuous_inverse_area"] = max(
        row["maximum_clamped_extent_area_to_continuous_inverse_area"] for row in results)
    report = {"schema": "graphreader.synthetic-tick-inverse-target-geometry-audit.v1", "aggregate": aggregate,
              "preflight_report_sha256": tensor_parity.PREFLIGHT_SHA,
              "script_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "inverse_targets_source_sha256": sha256(Path(targets.__file__).read_bytes()).hexdigest(),
              "opencv_version": cv2.__version__,
              "binding": "descriptive script hash; authenticated synthetic train inputs and preflight report",
              "component_connectivity": 8,
              "counterfactual": "same inverse geometry and rounding with no minimum extent; diagnostic only",
              "area_ratio": "clamped raster contour extents product / ideal continuous inverse contour area; excludes inclusive pixel count",
              "train_scene_count": 4, "holdout_raster_or_label_reads": 0, "private_reads": 0,
              "sealed_reads": 0, "optimizer_steps": 0, "training_input_approved": False}
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    return report


if __name__ == "__main__":
    import sys
    if len(sys.argv) != 2:
        raise SystemExit("Expected new project artifacts report path")
    result = run(Path(sys.argv[1]))
    print(json.dumps(result["aggregate"], sort_keys=True))
