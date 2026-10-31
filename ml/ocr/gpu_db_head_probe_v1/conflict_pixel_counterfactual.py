# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Diagnostic: ignore shared target pixels and a fixed margin; no training."""
from collections import Counter, defaultdict
from hashlib import sha256
import json
from pathlib import Path

import cv2
import numpy as np

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight, parity, target_geometry_audit, tensor_parity
from ml.ocr.official_bakeoff import db_inverse_targets as targets

AUDIT = parity.ROOT / "artifacts/synthetic-tick-target-geometry-audit-v2-20261030.json"
AUDIT_SHA = "2ec1e0813300845114a385b09d4ac26d6a7f30d54e0dc3d7317a4ee46ff0f8a0"
MARGIN = 2  # Pixels in the 512 preflight tensor, not original-image pixels.


def _validate_dependencies(prior, audit):
    target_sha = sha256(Path(targets.__file__).read_bytes()).hexdigest()
    if audit.get("preflight_report_sha256") != tensor_parity.PREFLIGHT_SHA:
        raise ValueError("Target geometry audit preflight identity differs")
    if audit.get("inverse_targets_source_sha256") != target_sha:
        raise ValueError("Target geometry audit target source differs")
    if prior.get("dependency_source_sha256", {}).get("inverse_targets") != target_sha:
        raise ValueError("Preflight target source differs")
    if audit.get("opencv_version") != cv2.__version__ or prior.get("opencv_version") != cv2.__version__:
        raise ValueError("OpenCV version differs from bound evidence")
    if audit.get("script_sha256") != sha256(Path(target_geometry_audit.__file__).read_bytes()).hexdigest():
        raise ValueError("Target geometry audit source differs")
    return target_sha


def evaluate(built, base_mask, identities):
    target = built.target
    if (target.ndim != 3 or target.shape[0] != 1 or base_mask.shape != target.shape or
            not np.isin(base_mask, [0, 1]).all() or len(identities) != len(built.regions) or
            len({item[0] for item in identities}) != len(identities)):
        raise ValueError("Target, mask or text instance identity differs")
    height, width = target.shape[1:]
    exact = np.zeros((height, width), dtype=bool)
    excluded = np.zeros((height, width), dtype=bool)
    pair_kinds = Counter()
    for contact in built.contacts:
        pair_kinds[contact.kind] += 1
        if contact.kind != "overlap":
            continue
        a = built.regions[contact.first_index].raster_contour_box
        b = built.regions[contact.second_index].raster_contour_box
        left, top = max(a[0], b[0]), max(a[1], b[1])
        right, bottom = min(a[2], b[2]) + 1, min(a[3], b[3]) + 1
        if right <= left or bottom <= top:
            raise ValueError("Overlap contact has no shared target pixels")
        exact[top:bottom, left:right] = True
        excluded[max(0, top - MARGIN):min(height, bottom + MARGIN),
                 max(0, left - MARGIN):min(width, right + MARGIN)] = True
    mask = np.asarray(base_mask, dtype=np.uint8).copy()
    mask[0, excluded] = 0
    supervised_positive = (target[0] == 1) & (mask[0] == 1)
    _, labels = cv2.connectedComponents(supervised_positive.astype(np.uint8), connectivity=8)
    label_sets = []
    role_counts = defaultdict(Counter)
    for region, (_, role) in zip(built.regions, identities, strict=True):
        l, t, r, b = region.raster_contour_box
        before = int(np.count_nonzero(base_mask[0, t:b + 1, l:r + 1]))
        after = int(np.count_nonzero(mask[0, t:b + 1, l:r + 1]))
        role_counts[role]["instances"] += 1
        role_counts[role]["per_instance_positive_pixels_before"] += before
        role_counts[role]["per_instance_positive_pixels_after"] += after
        role_counts[role]["instances_with_positive_before"] += before > 0
        role_counts[role]["instances_with_positive_after"] += after > 0
        role_counts[role]["instances_partially_masked"] += 0 < after < before
        role_counts[role]["instances_lost_all_positive"] += before > 0 and after == 0
        label_sets.append(set(np.unique(labels[t:b + 1, l:r + 1])) - {0})
    unresolved = Counter()
    for contact in built.contacts:
        first = built.regions[contact.first_index].raster_contour_box
        second = built.regions[contact.second_index].raster_contour_box
        a = np.zeros((height, width), dtype=np.uint8)
        b = np.zeros_like(a)
        for array, rect in ((a, first), (b, second)):
            l, t, r, bottom = rect
            array[t:bottom + 1, l:r + 1] = mask[0, t:bottom + 1, l:r + 1]
        if np.any(cv2.dilate(a, np.ones((3, 3), dtype=np.uint8)) & b):
            unresolved[contact.kind] += 1
    shared_pairs = sum(bool(label_sets[i] & label_sets[j])
                       for i in range(len(label_sets)) for j in range(i + 1, len(label_sets)))
    positive = target == 1
    result = {"text_instances": len(identities), "original_contacts_by_kind": dict(sorted(pair_kinds.items())),
              "unresolved_original_direct_contacts_by_kind": dict(sorted(unresolved.items())),
              "instance_pairs_sharing_retained_connected_component": shared_pairs,
              "shared_target_pixels_before_margin": int(np.count_nonzero(exact)),
              "excluded_tensor_pixels_including_margin": int(np.count_nonzero(excluded)),
              "positive_pixels_before": int(np.count_nonzero((base_mask == 1) & positive)),
              "positive_pixels_after": int(np.count_nonzero((mask == 1) & positive)),
              "supervised_pixels_before": int(np.count_nonzero(base_mask)),
              "supervised_pixels_after": int(np.count_nonzero(mask)),
              "outgrown_clamped_instances_not_targeted": sum(
                  (r.minimum_extent_clamped_x and r.raster_extent[0] > r.tensor_box[2] - r.tensor_box[0]) or
                  (r.minimum_extent_clamped_y and r.raster_extent[1] > r.tensor_box[3] - r.tensor_box[1])
                  for r in built.regions),
              "roles": {role: dict(sorted(counts.items())) for role, counts in sorted(role_counts.items())}}
    return mask, result


def run(output: Path) -> dict:
    output = output.resolve()
    if not output.is_relative_to((parity.ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Report must be a new project artifacts file")
    prior = preflight._json(parity._read_locked(tensor_parity.PREFLIGHT, tensor_parity.PREFLIGHT_SHA))
    audit = preflight._json(parity._read_locked(AUDIT, AUDIT_SHA))
    target_sha = _validate_dependencies(prior, audit)
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
        _candidate_mask, result = evaluate(built, mask, identities)
        results.append(result)
    if any(warnings[key] != audit["aggregate"][key] for key in warnings):
        raise ValueError("Target geometry audit warning counts differ")
    dictionary_keys = ("original_contacts_by_kind", "unresolved_original_direct_contacts_by_kind", "roles")
    aggregate = {key: sum(row[key] for row in results) for key in results[0] if key not in dictionary_keys}
    for key in dictionary_keys[:2]:
        aggregate[key] = dict(sorted(sum((Counter(row[key]) for row in results), Counter()).items()))
    roles = defaultdict(Counter)
    for row in results:
        for role, counts in row["roles"].items():
            roles[role].update(counts)
    aggregate["roles"] = {role: dict(sorted(counts.items())) for role, counts in sorted(roles.items())}
    report = {"schema": "graphreader.synthetic-tick-conflict-pixel-counterfactual.v1", "aggregate": aggregate,
              "policy": "ignore shared target pixels and a fixed two-pixel square margin in 512 tensor space; retain existing artifact mask",
              "counting": "per-role target support is summed per instance; shared target pixels may count for multiple instances",
              "adjacency": "direct contacts use 8-neighbor pixel adjacency; component pairs can include indirect paths",
              "preflight_report_sha256": tensor_parity.PREFLIGHT_SHA, "geometry_audit_sha256": AUDIT_SHA,
              "inverse_targets_source_sha256": target_sha, "opencv_version": cv2.__version__,
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
