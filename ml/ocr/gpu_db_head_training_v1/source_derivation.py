# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate four train-only feature/target inputs and localized loss masks.

This exploratory source is not approved training data. Historical benchmark
independence is unproven, the source has only four scenes, and target ambiguity
remains. No held-out, private or sealed raster or label is opened here.
"""
from collections import Counter, defaultdict
from dataclasses import dataclass
from hashlib import sha256
from pathlib import Path

import numpy as np

from ml.ocr.gpu_db_head_probe_v1 import (conflict_pixel_counterfactual as conflict,
    feature_preflight_960 as preflight, parity, tensor_parity_identity as identity)

REPORT = parity.ROOT / "artifacts/synthetic-tick-feature-preflight-960-v1-20261030/report.json"
REPORT_SHA = "7409758605004e060d0f6b27072c47e1d2a2eda99f26e523ebb2254b2334ba96"
MASK_SHA = (
    "dc395d51123c93eee1bfa9e8ec428d1cab03bd1312baf85a5c4b18a91e201171",
    "8f4553faa22fa8f67666d55d3bd5ba5f16b844190b2fe551a4b560f47b6e384e",
    "1a4f5c22696e4d10f9980da4997e12595641623b7dacd99b7452469de76b2b91",
    "7e55a4234f8074fa4e98e59ae04d605f0a2d4a195b8451478418f50710d7a382",
)


@dataclass(frozen=True)
class Sample:
    feature: np.ndarray
    target: np.ndarray
    mask: np.ndarray
    feature_sha256: str
    target_sha256: str
    mask_sha256: str


@dataclass(frozen=True)
class BoundSource:
    samples: tuple[Sample, ...]
    preflight_sha256: str
    model_sha256: str
    mask_policy_sha256: str


def _array(path: Path, descriptor: dict, shape: tuple[int, ...], *, binary: bool) -> np.ndarray:
    if (descriptor.get("dtype") != "float32-le" or descriptor.get("shape") != list(shape) or
            not isinstance(descriptor.get("sha256"), str)):
        raise ValueError("Bound cache descriptor differs")
    raw = parity._read_locked(path, descriptor["sha256"])
    if len(raw) != int(np.prod(shape)) * 4:
        raise ValueError("Bound cache byte count differs")
    array = np.frombuffer(raw, dtype="<f4").reshape(shape)
    if not np.isfinite(array).all() or (binary and not np.isin(array, [0, 1]).all()):
        raise ValueError("Bound cache contains invalid values")
    return array


def _aggregate(rows):
    maps = ("original_contacts_by_kind", "unresolved_original_direct_contacts_by_kind")
    result = {key: sum(item[key] for item in rows) for key in rows[0] if key not in (*maps, "roles")}
    for key in maps:
        result[key] = dict(sorted(sum((Counter(item[key]) for item in rows), Counter()).items()))
    roles = defaultdict(Counter)
    for item in rows:
        for role, counts in item["roles"].items():
            roles[role].update(counts)
    result["roles"] = {role: dict(sorted(counts.items())) for role, counts in sorted(roles.items())}
    return result


def _verify_localized_mask(built, base_mask, localized):
    """Independently rebuild the fixed overlap-only exclusion policy."""
    if base_mask.shape != built.target.shape or localized.shape != base_mask.shape:
        raise ValueError("Localized mask shape differs")
    expected = np.asarray(base_mask, dtype=np.uint8).copy()
    height, width = expected.shape[1:]
    for contact in built.contacts:
        if contact.kind != "overlap":
            continue
        a = built.regions[contact.first_index].raster_contour_box
        b = built.regions[contact.second_index].raster_contour_box
        l, t = max(a[0], b[0]), max(a[1], b[1])
        r, bottom = min(a[2], b[2]) + 1, min(a[3], b[3]) + 1
        if r <= l or bottom <= t:
            raise ValueError("Invalid overlap contact")
        expected[0, max(0, t - 2):min(height, bottom + 2), max(0, l - 2):min(width, r + 2)] = 0
    if not np.array_equal(expected, localized):
        raise ValueError("Localized mask changed intended supervision")


def load_bound_source() -> BoundSource:
    """Read only authenticated synthetic train inputs; no model optimization."""
    report = preflight.preflight._json(parity._read_locked(REPORT, REPORT_SHA))
    required = {"schema": "graphreader.synthetic-tick-production-size-feature-preflight.v1",
                "status": "completed_diagnostic", "train_scene_count": 4, "training_input_approved": False,
                "production_approved": False, "full_production_workflow_replayed": False,
                "private_reads": 0, "sealed_reads": 0, "holdout_raster_or_label_reads": 0,
                "optimizer_steps": 0, "model_sha256": parity.MODEL_SHA, "trunk_sha256": preflight.TRUNK_SHA,
                "tensor_parity_report_sha256": preflight.PARITY_SHA, "tensor_parity_identity_sha256": preflight.IDENTITY_SHA}
    if any(type(report.get(key)) is not type(value) or report.get(key) != value for key, value in required.items()):
        raise ValueError("Bound preflight scope differs")
    expected_sources = {"script": Path(preflight.__file__), "target_builder": Path(preflight.targets.__file__),
                        "geometry_audit": Path(preflight.geometry.__file__), "conservative_mask": Path(preflight.conservative.__file__),
                        "conflict_mask": Path(conflict.__file__)}
    source_hashes = report.get("source_sha256", {})
    for key, path in expected_sources.items():
        parity._read_locked(path, source_hashes.get(key))
    bound = identity.load(preflight.IDENTITY, preflight.IDENTITY_SHA)
    compared = preflight.preflight._json(parity._read_locked(preflight.PARITY, preflight.PARITY_SHA))
    parity_rows = preflight.validate_parity(compared, bound)
    parity._read_locked(parity.MODEL, parity.MODEL_SHA)
    inputs = preflight.preflight._train_inputs()
    rows = report.get("cases")
    if not isinstance(rows, list) or len(rows) != 4 or len(inputs) != 4:
        raise ValueError("Bound input population differs")
    samples, summaries = [], []
    for index, ((source, image, annotation), row, compared_row) in enumerate(zip(inputs, rows, parity_rows, strict=True)):
        if (source["image"]["sha256"] != row.get("source_image_sha256") or
                source["annotation"]["sha256"] != row.get("source_annotation_sha256") or
                source["image"]["sha256"] != compared_row.get("source_image_sha256")):
            raise ValueError("Train source or parity binding differs")
        tensor, built, base_mask, ids = preflight.prepare(image, annotation)
        if (list(tensor.shape) != row.get("shape") or sha256(tensor.tobytes()).hexdigest() != row.get("tensor_sha256") or
                row["tensor_sha256"] != compared_row.get("candidate_tensor_sha256")):
            raise ValueError("Production-size tensor binding differs")
        h, w = tensor.shape[2:]
        arrays = row.get("arrays", {})
        expected_files = {"features": f"train-{index}.features.f32", "target": f"train-{index}.target.f32",
                          "mask": f"train-{index}.mask.f32"}
        if set(arrays) != set(expected_files) or any(arrays[k].get("file") != v for k, v in expected_files.items()):
            raise ValueError("Cache inventory differs")
        feature = _array(REPORT.parent / expected_files["features"], arrays["features"], (1, 96, h // 4, w // 4), binary=False)
        target = _array(REPORT.parent / expected_files["target"], arrays["target"], (1, h, w), binary=True)
        saved_mask = _array(REPORT.parent / expected_files["mask"], arrays["mask"], (1, h, w), binary=True)
        if not np.array_equal(target, built.target) or not np.array_equal(saved_mask, base_mask):
            raise ValueError("Cached target or mask differs from regenerated source")
        localized, summary = conflict.evaluate(built, base_mask, ids)
        _verify_localized_mask(built, base_mask, localized)
        localized = np.ascontiguousarray(localized, dtype="<f4")
        if sha256(localized.tobytes()).hexdigest() != MASK_SHA[index]:
            raise ValueError("Localized mask differs from pinned policy output")
        if not np.any((localized == 1) & (target == 1)) or not np.any((localized == 1) & (target == 0)):
            raise ValueError("Localized mask needs supervised positives and negatives")
        localized.setflags(write=False)
        samples.append(Sample(feature, target, localized, arrays["features"]["sha256"], arrays["target"]["sha256"],
                              sha256(localized.tobytes()).hexdigest()))
        summaries.append(summary)
    if _aggregate(summaries) != report.get("candidate_conflict_pixel_mask"):
        raise ValueError("Localized mask aggregate differs from bound preflight")
    identity.load(preflight.IDENTITY, preflight.IDENTITY_SHA)
    return BoundSource(tuple(samples), REPORT_SHA, parity.MODEL_SHA, source_hashes["conflict_mask"])
