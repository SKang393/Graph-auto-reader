# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unapproved train-only loss-mask counterfactual; no training or inference."""
from collections import Counter, defaultdict, deque
from hashlib import sha256
import json
import math
from pathlib import Path

import cv2
import numpy as np

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight, parity, target_geometry_audit, tensor_parity
from ml.ocr.official_bakeoff import db_inverse_targets as targets

AUDIT = parity.ROOT / "artifacts/synthetic-tick-target-geometry-audit-v2-20261030.json"
AUDIT_SHA = "2ec1e0813300845114a385b09d4ac26d6a7f30d54e0dc3d7317a4ee46ff0f8a0"
MARGIN = 2


def _overlap(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def _footprints(region, width, height):
    left, top, right, bottom = region.tensor_box
    source = (max(0, math.floor(left) - MARGIN), max(0, math.floor(top) - MARGIN),
              min(width, math.ceil(right) + MARGIN), min(height, math.ceil(bottom) + MARGIN))
    x0, y0, x1, y1 = region.raster_contour_box
    return source, (x0, y0, x1 + 1, y1 + 1)


def evaluate(built, base_mask, identities):
    target = built.target
    if (target.ndim != 3 or target.shape[0] != 1 or base_mask.shape != target.shape or
            not np.isin(base_mask, [0, 1]).all() or len(identities) != len(built.regions) or
            len({item[0] for item in identities}) != len(identities)):
        raise ValueError("Target, mask or text instance identity differs")
    height, width = target.shape[1:]
    _, components = cv2.connectedComponents(target[0].astype(np.uint8), connectivity=8)
    members = defaultdict(list)
    footprints = []
    outgrown = set()
    for region in built.regions:
        x0, y0, x1, y1 = region.raster_contour_box
        component = int(components[y0, x0])
        if component == 0 or not np.all(components[y0:y1 + 1, x0:x1 + 1] == component):
            raise ValueError("Invalid target component")
        members[component].append(region.index)
        footprints.append(_footprints(region, width, height))
        l, t, r, b = region.tensor_box
        if ((region.minimum_extent_clamped_x and region.raster_extent[0] > r - l) or
                (region.minimum_extent_clamped_y and region.raster_extent[1] > b - t)):
            outgrown.add(region.index)
    merged = {index for group in members.values() if len(group) > 1 for index in group}
    omitted = merged | outgrown
    pending = deque(omitted)
    # A retained source or target footprint may not overlap any ignored footprint.
    while pending:
        current = pending.popleft()
        for other, other_rectangles in enumerate(footprints):
            if other in omitted:
                continue
            if any(_overlap(a, b) for a in footprints[current] for b in other_rectangles):
                omitted.add(other)
                pending.append(other)
    final_mask = np.asarray(base_mask, dtype=np.uint8).copy()
    for index in omitted:
        for left, top, right, bottom in footprints[index]:
            final_mask[0, top:bottom, left:right] = 0
    for index, rectangles in enumerate(footprints):
        if index not in omitted and any(not final_mask[0, t:b, l:r].all() for l, t, r, b in rectangles):
            # Existing artifact exclusions may intersect a retained footprint;
            # only the newly introduced omissions must be disjoint.
            if any(_overlap(a, b) for a in rectangles for dropped in omitted for b in footprints[dropped]):
                raise ValueError("Retained footprint intersects a new omission")
    positive = target == 1
    before = int(np.count_nonzero((base_mask == 1) & positive))
    after = int(np.count_nonzero((final_mask == 1) & positive))
    total_roles = Counter(role for _, role in identities)
    omitted_roles = Counter(identities[index][1] for index in omitted)
    result = {"text_instances": len(identities), "merged_component_seed_instances": len(merged),
              "outgrown_clamp_seed_instances": len(outgrown), "seed_union_instances": len(merged | outgrown),
              "cascade_instances": len(omitted - merged - outgrown), "omitted_instances": len(omitted),
              "retained_instances": len(identities) - len(omitted), "roles_total": dict(sorted(total_roles.items())),
              "roles_omitted": dict(sorted(omitted_roles.items())), "positive_pixels_before": before,
              "positive_pixels_after": after, "supervised_pixels_before": int(np.count_nonzero(base_mask)),
              "supervised_pixels_after": int(np.count_nonzero(final_mask))}
    return final_mask, result


def run(output: Path) -> dict:
    output = output.resolve()
    if not output.is_relative_to((parity.ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Report must be a new project artifacts file")
    prior = preflight._json(parity._read_locked(tensor_parity.PREFLIGHT, tensor_parity.PREFLIGHT_SHA))
    audit = preflight._json(parity._read_locked(AUDIT, AUDIT_SHA))
    if audit.get("script_sha256") != sha256(Path(target_geometry_audit.__file__).read_bytes()).hexdigest():
        raise ValueError("Target geometry audit source differs")
    rows = preflight._train_inputs()
    if len(rows) != 4 or len(prior.get("cases", [])) != 4:
        raise ValueError("Expected four authenticated train scenes")
    results, warnings = [], Counter()
    for (source, image, annotation), saved in zip(rows, prior["cases"], strict=True):
        if source["image"]["sha256"] != saved["source_image_sha256"] or source["annotation"]["sha256"] != saved["source_annotation_sha256"]:
            raise ValueError("Preflight source binding differs")
        tensor, target, mask, counts = preflight._prepare(image, annotation)
        if sha256(tensor.tobytes()).hexdigest() != saved["tensor_sha256"] or counts != saved["counts"]:
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
        audit_row = target_geometry_audit.analyze(built, identities)
        warnings["direct_contacts"] += audit_row["direct_contacts"]
        warnings["clamped_instances"] += audit_row["clamped_instances"]
        _counterfactual_mask, result = evaluate(built, mask, identities)
        results.append(result)
    if any(warnings[key] != audit["aggregate"][key] for key in warnings):
        raise ValueError("Target geometry audit warning counts differ")
    dictionary_keys = ("roles_total", "roles_omitted")
    aggregate = {key: sum(row[key] for row in results) for key in results[0] if key not in dictionary_keys}
    for key in dictionary_keys:
        aggregate[key] = dict(sorted(sum((Counter(row[key]) for row in results), Counter()).items()))
    aggregate["roles_retained"] = {key: count - aggregate["roles_omitted"].get(key, 0)
                                   for key, count in aggregate["roles_total"].items()}
    report = {"schema": "graphreader.synthetic-tick-loss-mask-counterfactual.v1", "aggregate": aggregate,
              "policy": "omit merged target components and outgrown clamped contours; ignore target footprints and source boxes plus 2-pixel margin; cascade overlapping source or target footprints",
              "preflight_report_sha256": tensor_parity.PREFLIGHT_SHA, "geometry_audit_sha256": AUDIT_SHA,
              "script_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "binding": "descriptive script hash; authenticated synthetic train inputs and frozen reports",
              "train_scene_count": 4, "holdout_raster_or_label_reads": 0, "private_reads": 0,
              "sealed_reads": 0, "optimizer_steps": 0, "training_input_approved": False,
              "production_approved": False, "saved_training_arrays": False}
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    return report


if __name__ == "__main__":
    import sys
    if len(sys.argv) != 2:
        raise SystemExit("Expected new project artifacts report path")
    print(json.dumps(run(Path(sys.argv[1]))["aggregate"], sort_keys=True))
