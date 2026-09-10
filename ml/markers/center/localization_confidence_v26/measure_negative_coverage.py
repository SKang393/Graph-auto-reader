# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Measure frozen V25 train-negative coverage without model execution."""

from __future__ import annotations

from collections import Counter
import hashlib
import json
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import numpy as np
import torch

from ml.markers.center.mask_preserving_v24 import train_p1 as v24
from ml.markers.center.mask_preserving_v24 import stratified_background
from ml.markers.center.plot_domain_v25 import train_p1 as v25
from ml.markers.center.plot_domain_v25.proposal_domain import extract_proposals_in_domain
from ml.markers.center.real_range_generator_v1 import negative_sampler
from ml.markers.gate_seal import canonical_json_bytes, sha256_file


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1"
)
REPORT_NAME = "coverage.json"
CACHE_NAME = "frozen-v25-train-proposal-metadata.npz"
SCHEMA = "graphreader.marker-center-v25-train-negative-coverage.v1"
TARGET_PREFLIGHT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/preflight-v1.json"
)
TARGET_PREFLIGHT_SHA256 = (
    "20bbd7c0c2f8cf999a4dbff68a9fa60a9494acae8b22a04d5b9f45cba7790b70"
)


def distance_band(distance: float) -> str:
    if not np.isfinite(distance):
        return "no_truth"
    if distance <= 3.0:
        return "positive_le3"
    if distance <= 5.0:
        return "negative_gt3_le5"
    if distance <= 8.0:
        return "negative_gt5_le8"
    if distance <= 12.0:
        return "negative_gt8_le12"
    return "negative_gt12"


def radius_category(radius: float) -> str:
    if not np.isfinite(radius):
        return "no_truth"
    if radius < 2.5:
        return "below_2_5px"
    if radius <= 8.0:
        return "within_2_5_to_8px"
    return "above_8px"


def component_original_strata(scene: Any, proposals: Any, labels: torch.Tensor) -> tuple[str, ...]:
    """Replay the exact precedence used by the frozen component sampler."""

    features = negative_sampler._features(proposals.patches)
    legacy_hard = set(
        negative_sampler._hard_indices(scene, proposals.coordinates, labels).tolist()
    )
    topology = negative_sampler._topology_indices(
        scene,
        proposals.coordinates,
        labels,
        radius_px=negative_sampler.TOPOLOGY_SAMPLER_RADIUS_PX,
    )
    topology_by_index = {
        index for kind in negative_sampler.TOPOLOGY_KINDS for index in topology[kind]
    }
    connector = negative_sampler._connector_anchor_indices(
        scene, proposals.coordinates, labels
    )
    connector_band = negative_sampler._generic_connector_band_indices(
        scene,
        proposals.coordinates,
        labels,
        features,
        legacy_hard,
        {index: "topology" for index in topology_by_index},
        connector,
    )
    result: list[str] = []
    for index in range(len(labels)):
        if labels[index].item() > 0.5:
            result.append("positive")
        elif index in legacy_hard:
            result.append("hard_existing")
        elif index in topology_by_index or index in connector:
            result.append("generic")
        elif bool(features["faint_low"][index]):
            result.append("faint_low")
        elif bool(features["faint_p05"][index]):
            result.append("faint_p05")
        elif bool(features["ocr_heavy"][index]):
            result.append("ocr_heavy")
        elif bool(features["artifact"][index]):
            result.append("artifact")
        elif index in connector_band:
            result.append("generic_connector_band")
        else:
            result.append("generic")
    return tuple(result)


def component_retention_roles(
    scene: Any, proposals: Any, labels: torch.Tensor
) -> tuple[str, ...]:
    """Describe fixed sampler retention without changing original quota strata."""

    features = negative_sampler._features(proposals.patches)
    legacy_hard = set(
        negative_sampler._hard_indices(scene, proposals.coordinates, labels).tolist()
    )
    topology = negative_sampler._topology_indices(
        scene,
        proposals.coordinates,
        labels,
        radius_px=negative_sampler.TOPOLOGY_SAMPLER_RADIUS_PX,
    )
    topology_by_index = {
        index for kind in negative_sampler.TOPOLOGY_KINDS for index in topology[kind]
    }
    connector = negative_sampler._connector_anchor_indices(
        scene, proposals.coordinates, labels
    )
    sparse = negative_sampler._sparse_fragment_indices(
        scene, proposals.coordinates, labels
    )
    connector_band = negative_sampler._generic_connector_band_indices(
        scene,
        proposals.coordinates,
        labels,
        features,
        legacy_hard,
        {index: "topology" for index in topology_by_index},
        connector,
    )
    result: list[str] = []
    for index in range(len(labels)):
        if labels[index].item() > 0.5:
            result.append("positive")
        elif index in legacy_hard:
            result.append("quota_ranked")
        elif index in topology_by_index:
            result.append("topology_reserved")
        elif index in connector:
            result.append("connector_anchor_reserved")
        elif index in sparse:
            result.append("sparse_fragment_reserved")
        elif index in connector_band:
            result.append("quota_ranked_connector_band")
        else:
            result.append("quota_ranked")
    return tuple(result)


def family_original_strata(
    scene: Any, proposals: Any, labels: torch.Tensor
) -> tuple[str, ...]:
    hard = torch.zeros(len(labels), dtype=torch.bool)
    for kind, x, y in scene.hard_negatives:
        radius = v24._hard_negative_radius(scene, kind)
        if radius is not None and len(proposals.coordinates):
            hard |= torch.cdist(
                proposals.coordinates,
                torch.tensor(((x, y),), dtype=proposals.coordinates.dtype),
            ).squeeze(1).le(radius)
    bins = stratified_background._bin_ids(proposals.patches)
    return tuple(
        "positive"
        if labels[index].item() > 0.5
        else "hard_negative"
        if hard[index].item()
        else f"stratified_bin_{int(bins[index]):02d}"
        for index in range(len(labels))
    )


def summarize_negative_rows(rows: Sequence[Mapping[str, object]]) -> dict[str, object]:
    grouped: Counter[tuple[str, str, str]] = Counter()
    selected: Counter[tuple[str, str, str]] = Counter()
    for row in rows:
        if row["distance_band"] == "positive_le3":
            continue
        key = (
            str(row["distance_band"]),
            str(row["stratum"]),
            str(row["radius_category"]),
        )
        grouped[key] += 1
        if bool(row["selected"]):
            selected[key] += 1
    cells = [
        {
            "distance_band": key[0],
            "stratum": key[1],
            "truth_radius_category": key[2],
            "eligible": grouped[key],
            "selected": selected[key],
            "selection_fraction": selected[key] / grouped[key],
        }
        for key in sorted(grouped)
    ]
    eligible_by_distance: Counter[str] = Counter()
    selected_by_distance: Counter[str] = Counter()
    for key, count in grouped.items():
        eligible_by_distance[key[0]] += count
        selected_by_distance[key[0]] += selected[key]
    return {
        "eligible_negative_count": sum(grouped.values()),
        "selected_negative_count": sum(selected.values()),
        "by_distance": {
            name: {
                "eligible": eligible_by_distance[name],
                "selected": selected_by_distance[name],
                "selection_fraction": selected_by_distance[name] / eligible_by_distance[name],
            }
            for name in sorted(eligible_by_distance)
        },
        "cells": cells,
    }


def _selection_prefix(sampling: Any, scene_count: int) -> tuple[tuple[int, ...], ...]:
    selections = sampling.selections
    if len(selections) < scene_count or any(selections[scene_count:]):
        raise RuntimeError("component sampler selection shape changed")
    return tuple(selections[:scene_count])


def _scene_rows(
    scene_index: int,
    scene: Any,
    proposals: Any,
    selected_indices: Sequence[int],
    strata: Sequence[str],
    retention_roles: Sequence[str],
) -> list[dict[str, object]]:
    centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
    if len(centers):
        distances = torch.cdist(proposals.coordinates, centers)
        nearest_distance, nearest_truth = distances.min(dim=1)
        raw_radii = getattr(scene, "diameters", None)
        if raw_radii is None:
            raw_radii = tuple(float(value) * 2.0 for value in scene.radii)
        radii = torch.tensor(raw_radii, dtype=torch.float32) / 2.0
    else:
        nearest_distance = torch.full((len(proposals.coordinates),), float("inf"))
        nearest_truth = torch.full((len(proposals.coordinates),), -1, dtype=torch.int64)
        radii = torch.empty(0, dtype=torch.float32)
    selected = set(int(index) for index in selected_indices)
    result = []
    for proposal_index, (x, y) in enumerate(proposals.coordinates.tolist()):
        truth_index = int(nearest_truth[proposal_index])
        distance = float(nearest_distance[proposal_index])
        radius = float(radii[truth_index]) if truth_index >= 0 else float("nan")
        result.append({
            "scene_index": scene_index,
            "proposal_index": proposal_index,
            "x": float(x),
            "y": float(y),
            "nearest_truth_index": truth_index,
            "nearest_truth_distance_px": distance,
            "nearest_truth_radius_px": radius,
            "distance_band": distance_band(distance),
            "radius_category": radius_category(radius),
            "selected": proposal_index in selected,
            "stratum": strata[proposal_index],
            "retention_role": retention_roles[proposal_index],
        })
    return result


def _cache_arrays(
    rows: Sequence[Mapping[str, object]],
    stratum_ids: Mapping[str, int],
    retention_role_ids: Mapping[str, int],
) -> dict[str, np.ndarray]:
    return {
        "scene_index": np.asarray([row["scene_index"] for row in rows], dtype=np.int32),
        "proposal_index": np.asarray([row["proposal_index"] for row in rows], dtype=np.int32),
        "coordinates": np.asarray([(row["x"], row["y"]) for row in rows], dtype=np.float32),
        "nearest_truth_index": np.asarray([row["nearest_truth_index"] for row in rows], dtype=np.int32),
        "nearest_truth_distance_px": np.asarray([row["nearest_truth_distance_px"] for row in rows], dtype=np.float32),
        "nearest_truth_radius_px": np.asarray([row["nearest_truth_radius_px"] for row in rows], dtype=np.float32),
        "selected": np.asarray([row["selected"] for row in rows], dtype=np.bool_),
        "stratum_id": np.asarray([stratum_ids[str(row["stratum"])] for row in rows], dtype=np.uint8),
        "retention_role_id": np.asarray(
            [retention_role_ids[str(row["retention_role"])] for row in rows],
            dtype=np.uint8,
        ),
    }


def _array_sha(value: np.ndarray) -> str:
    array = np.ascontiguousarray(value)
    digest = hashlib.sha256(canonical_json_bytes({
        "dtype": str(array.dtype), "shape": list(array.shape)
    }))
    digest.update(array.tobytes(order="C"))
    return digest.hexdigest()


def run(
    *,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
) -> dict[str, object]:
    started = time.perf_counter()
    root = repository_root.resolve()
    destination = output_directory if output_directory.is_absolute() else root / output_directory
    if destination.exists():
        raise FileExistsError(f"negative coverage output already exists: {destination}")

    target_preflight_path = root / TARGET_PREFLIGHT_PATH
    if sha256_file(target_preflight_path) != TARGET_PREFLIGHT_SHA256:
        raise RuntimeError("V26 target preflight bytes changed")
    target_preflight = json.loads(target_preflight_path.read_text(encoding="utf-8"))

    prepared = v25._prepare(root, v25._default_dependencies())
    _, config_sha256, config = v25._validate_candidate_config(v25.CONFIG_PATH, root, prepared.report)
    component_negative_selections = _selection_prefix(
        prepared.component_values[5], len(prepared.component_train)
    )
    component_rows: list[dict[str, object]] = []
    for scene_index, (scene, selected_negatives) in enumerate(zip(
        prepared.component_train, component_negative_selections, strict=True
    )):
        proposals = v24.extract_proposals(scene.tensor)
        centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
        labels = torch.cdist(proposals.coordinates, centers).min(dim=1).values.le(3.0).float()
        positive = torch.nonzero(labels > 0.5, as_tuple=False).flatten().tolist()
        selected = tuple(sorted(set(positive).union(selected_negatives)))
        component_rows.extend(_scene_rows(
            scene_index,
            scene,
            proposals,
            selected,
            component_original_strata(scene, proposals, labels),
            component_retention_roles(scene, proposals, labels),
        ))

    family_rows: list[dict[str, object]] = []
    family_selections = prepared.family_values[5].selections
    if len(family_selections) != len(prepared.family_train):
        raise RuntimeError("family sampler selection shape changed")
    for scene_index, (bound, selected) in enumerate(zip(
        prepared.family_train, family_selections, strict=True
    )):
        scene = bound.scene
        proposals = extract_proposals_in_domain(scene.tensor, bound.panel_domain.domain).proposals
        centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
        labels = (
            torch.cdist(proposals.coordinates, centers).min(dim=1).values.le(3.0).float()
            if len(centers)
            else torch.zeros(len(proposals.coordinates), dtype=torch.float32)
        )
        family_rows.extend(_scene_rows(
            scene_index,
            scene,
            proposals,
            selected,
            family_original_strata(scene, proposals, labels),
            tuple(
                "positive"
                if value == "positive"
                else "hard_negative_retained"
                if value == "hard_negative"
                else "stratified_quota_ranked"
                for value in family_original_strata(scene, proposals, labels)
            ),
        ))

    component = summarize_negative_rows(component_rows)
    family = summarize_negative_rows(family_rows)
    if component["selected_negative_count"] != 32580 or family["selected_negative_count"] != 8230:
        raise RuntimeError("selected negative denominator changed")
    if sum(row["distance_band"] == "positive_le3" for row in component_rows) != 3258:
        raise RuntimeError("component positive proposal denominator changed")
    if sum(row["distance_band"] == "positive_le3" for row in family_rows) != 823:
        raise RuntimeError("family positive proposal denominator changed")
    expected_v25 = target_preflight.get("v25", {})
    if (
        expected_v25.get("config_sha256") != config_sha256
        or expected_v25.get("component_selected_index_sha256")
        != prepared.component_values[5].selected_index_sha256
        or expected_v25.get("family_selected_index_sha256")
        != prepared.family_values[5].selected_index_sha256
    ):
        raise RuntimeError("V25 selected training identities changed")

    strata = tuple(sorted({str(row["stratum"]) for row in (*component_rows, *family_rows)}))
    stratum_ids = {value: index for index, value in enumerate(strata)}
    retention_roles = tuple(sorted({
        str(row["retention_role"]) for row in (*component_rows, *family_rows)
    }))
    retention_role_ids = {value: index for index, value in enumerate(retention_roles)}
    component_arrays = _cache_arrays(component_rows, stratum_ids, retention_role_ids)
    family_arrays = _cache_arrays(family_rows, stratum_ids, retention_role_ids)
    arrays = {
        **{f"component_{key}": value for key, value in component_arrays.items()},
        **{f"family_{key}": value for key, value in family_arrays.items()},
    }
    destination.mkdir(parents=True, exist_ok=False)
    cache_path = destination / CACHE_NAME
    np.savez_compressed(cache_path, **arrays)
    result_path = root / Path("ml/markers/center/plot_domain_v25/P1_RESULT.json")
    report: dict[str, object] = {
        "schema": SCHEMA,
        "status": "model_free_complete",
        "scope": "fixed-v25-synthetic-train-negative-coverage",
        "inputs": {
            "v25_config_path": v25.CONFIG_PATH.as_posix(),
            "v25_config_sha256": config_sha256,
            "v25_result_path": result_path.relative_to(root).as_posix(),
            "v25_result_sha256": sha256_file(result_path),
            "component_selected_index_sha256": prepared.component_values[5].selected_index_sha256,
            "family_selected_index_sha256": prepared.family_values[5].selected_index_sha256,
            "target_preflight_path": TARGET_PREFLIGHT_PATH.as_posix(),
            "target_preflight_sha256": TARGET_PREFLIGHT_SHA256,
            "selected_row_identity_sha256": expected_v25["selected_row_identity_sha256"],
            "selected_tensor_inventory_sha256": expected_v25["selected_tensor_inventory_sha256"],
            "measurement_source_sha256": sha256_file(Path(__file__)),
        },
        "component_train": {**component, "truth_denominator": 2004},
        "family_train": {**family, "truth_denominator": 500},
        "cache": {
            "path": CACHE_NAME,
            "sha256": sha256_file(cache_path),
            "array_count": len(arrays),
            "stratum_ids": stratum_ids,
            "retention_role_ids": retention_role_ids,
            "arrays": {
                name: {"sha256": _array_sha(value), "dtype": str(value.dtype), "shape": list(value.shape)}
                for name, value in sorted(arrays.items())
            },
        },
        "interpretation_limits": [
            "Coverage measures the fixed train sampler only and does not select a new sampler or candidate.",
            "Marker distance is diagnostic metadata derived from synthetic train truth after proposal extraction; it never enters runtime inference.",
            "Selection fractions describe representation, not model causation or an acceptance bar.",
        ],
        "model_inference_runs": 0,
        "optimizer_steps_run": 0,
        "private_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
    (destination / REPORT_NAME).write_bytes(canonical_json_bytes(report))
    return report


def main() -> int:
    report = run()
    print(json.dumps({
        "status": report["status"],
        "component_selected_negatives": report["component_train"]["selected_negative_count"],
        "family_selected_negatives": report["family_train"]["selected_negative_count"],
        "output": str(OUTPUT_DIRECTORY / REPORT_NAME),
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
