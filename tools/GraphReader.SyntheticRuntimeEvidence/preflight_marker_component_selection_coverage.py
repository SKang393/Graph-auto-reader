# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare a bounded train-only component-negative coverage selection."""

from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence


REPO_ROOT = Path(__file__).resolve().parents[2]
TOOLS_ROOT = Path(__file__).resolve().parent
for search_root in (REPO_ROOT, TOOLS_ROOT):
    if str(search_root) not in sys.path:
        sys.path.insert(0, str(search_root))

import numpy as np
import torch

import diagnose_marker_v26_confidence_boundary as boundary  # noqa: E402
from ml.markers.center.localization_confidence_v26 import (  # noqa: E402
    measure_negative_coverage,
)
from ml.markers.center.mask_preserving_v24 import stratified_background  # noqa: E402
from ml.markers.center.real_range_generator_v1 import negative_sampler  # noqa: E402
from ml.markers.gate_seal import canonical_json_bytes  # noqa: E402


SCHEMA = "graphreader.marker-component-selection-coverage-preflight.v1"
BOUNDARY_SOURCE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/diagnose_marker_v26_confidence_boundary.py"
)
BOUNDARY_SOURCE_SHA256 = "b79793b2027407247a90584cae26ce1009ca13446b3e2af806a5134bf5a84225"
REPORT_NAME = "preflight.json"
CACHE_NAME = "proposed-selection-metadata.npz"
EXPECTED_COMPONENT_ROWS = 236_056
EXPECTED_COMPONENT_POSITIVES = 3_258
EXPECTED_COMPONENT_NEGATIVES = 32_580
EXPECTED_COMPONENT_SELECTED = 35_838
EXPECTED_COMPONENT_PROTECTED = 18_284
EXPECTED_COMPONENT_RESERVED = 5_760
EXPECTED_COMPONENT_CELL_COUNT = 50
EXPECTED_COMPONENT_SELECTION_SHA256 = (
    "c184a97ae7ad0292361e62a4aaed5a54d8c1ae1bdb0ab3daab9ddeb8d46b1af0"
)
EXPECTED_FAMILY_ROWS = 122_900
EXPECTED_FAMILY_POSITIVES = 823
EXPECTED_FAMILY_NEGATIVES = 8_230
EXPECTED_FAMILY_SELECTED = 9_053
EXPECTED_FAMILY_CELL_COUNT = 179
EXPECTED_FAMILY_SELECTION_SHA256 = (
    "239b3f2aa14eb9636964c22083e722aba76bd70623a2bbd75362e1db96c527e3"
)
BIN_COUNT = stratified_background.BIN_COUNT
COMPONENT_BAND_COUNTS = {
    "negative_gt3_le5": 2_044,
    "negative_gt5_le8": 4_256,
    "negative_gt8_le12": 4_223,
    "negative_gt12": 22_057,
}


class PreflightError(RuntimeError):
    """An authenticated input or frozen selection invariant changed."""


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise PreflightError(message)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _read_json(path: Path, expected_sha256: str) -> dict[str, Any]:
    _require(_sha256(path) == expected_sha256, f"authenticated JSON changed: {path}")
    value = json.loads(path.read_bytes())
    _require(isinstance(value, dict), f"authenticated JSON is not an object: {path}")
    return value


def _load_authenticated_npz(
    path: Path, expected_sha256: str, names: set[str]
) -> dict[str, np.ndarray]:
    _require(_sha256(path) == expected_sha256, f"authenticated NPZ changed: {path}")
    return boundary._load_npz(path, names)


def _sparse_counts(values: np.ndarray) -> dict[str, int]:
    return {
        str(index): int(count)
        for index, count in enumerate(values.tolist())
        if count
    }


def _select_seeded_rows(
    rows: np.ndarray,
    bin_ids: np.ndarray,
    budget: int,
    *,
    seed: int,
) -> tuple[np.ndarray, tuple[int, ...], tuple[int, ...]]:
    """Apply the frozen per-bin quota and seeded randperm semantics to row IDs."""
    _require(rows.ndim == 1 and bin_ids.ndim == 1, "row and bin arrays must be flat")
    _require(
        len(rows) == len(bin_ids)
        and np.all((bin_ids >= 0) & (bin_ids < BIN_COUNT)),
        "row and bin arrays are invalid",
    )
    entries = [rows[bin_ids == bin_id] for bin_id in range(BIN_COUNT)]
    capacities = tuple(int(len(values)) for values in entries)
    quotas = stratified_background.allocate_quotas(capacities, budget)
    generator = torch.Generator(device="cpu").manual_seed(seed)
    selected: list[int] = []
    for values, quota in zip(entries, quotas, strict=True):
        if quota:
            offsets = torch.randperm(len(values), generator=generator)[:quota].tolist()
            selected.extend(int(values[offset]) for offset in offsets)
    result = np.asarray(sorted(selected), dtype=np.int64)
    _require(len(result) == budget, "seeded stratified fill changed its budget")
    return result, capacities, quotas


def _select_component_mask(
    source_selected: np.ndarray,
    positive: np.ndarray,
    protected: np.ndarray,
    reserved: np.ndarray,
    cell_ids: Sequence[str | None],
    bin_ids: np.ndarray,
    *,
    seed: int,
) -> tuple[np.ndarray, dict[str, dict[str, Any]]]:
    """Keep mandatory rows and refill each frozen negative cell independently."""
    count = len(source_selected)
    _require(
        all(len(value) == count for value in (positive, protected, reserved, bin_ids))
        and len(cell_ids) == count,
        "component selection arrays differ in length",
    )
    _require(
        all(value.dtype == np.bool_ for value in (source_selected, positive, protected, reserved)),
        "component selection masks must be boolean",
    )
    _require(not np.any(positive & np.asarray([key is not None for key in cell_ids])),
             "positive row entered a negative allocation cell")
    mandatory = positive | protected | reserved
    _require(not np.any(mandatory & ~source_selected), "mandatory row is absent from V26 selection")
    proposed = mandatory.copy()
    cell_rows: dict[str, list[int]] = defaultdict(list)
    for row, key in enumerate(cell_ids):
        if key is not None:
            cell_rows[key].append(row)
    reports: dict[str, dict[str, Any]] = {}
    for key in sorted(cell_rows):
        rows = np.asarray(cell_rows[key], dtype=np.int64)
        source_rows = rows[source_selected[rows]]
        mandatory_rows = rows[mandatory[rows]]
        quota = len(source_rows)
        fill_budget = quota - len(mandatory_rows)
        _require(fill_budget >= 0, f"mandatory rows exceed source quota: {key}")
        candidates = rows[~mandatory[rows]]
        _require(fill_budget <= len(candidates), f"cell lacks fill capacity: {key}")
        chosen, capacities, quotas = _select_seeded_rows(
            candidates,
            bin_ids[candidates],
            fill_budget,
            seed=seed,
        )
        proposed[chosen] = True
        after_rows = rows[proposed[rows]]
        _require(len(after_rows) == quota, f"cell selected count changed: {key}")
        eligible_bins = np.bincount(bin_ids[rows], minlength=BIN_COUNT)
        before_bins = np.bincount(bin_ids[source_rows], minlength=BIN_COUNT)
        after_bins = np.bincount(bin_ids[after_rows], minlength=BIN_COUNT)
        regression = np.flatnonzero((before_bins > 0) & (after_bins == 0))
        reports[key] = {
            "eligible_rows": len(rows),
            "source_selected_rows": quota,
            "mandatory_rows": len(mandatory_rows),
            "available_discretionary_rows": len(candidates),
            "fill_budget": fill_budget,
            "nonempty_eligible_bins": int((eligible_bins > 0).sum()),
            "selected_bins_before": int((before_bins > 0).sum()),
            "selected_bins_after": int((after_bins > 0).sum()),
            "eligible_zero_quota_bins": [
                index
                for index, (capacity, allocated) in enumerate(zip(capacities, quotas, strict=True))
                if capacity and not allocated
            ],
            "coverage_regression_bin_ids": regression.tolist(),
            "eligible_bin_counts": _sparse_counts(eligible_bins),
            "selected_bin_counts_before": _sparse_counts(before_bins),
            "selected_bin_counts_after": _sparse_counts(after_bins),
            "allocator_capacities": {
                str(index): value for index, value in enumerate(capacities) if value
            },
            "allocator_quotas": {
                str(index): value for index, value in enumerate(quotas) if value
            },
        }
    _require(not np.any(proposed & positive & ~source_selected),
             "selection introduced a non-source positive")
    return proposed, reports


def _validate_final_masks(
    source_component: np.ndarray,
    proposed_component: np.ndarray,
    positive_component: np.ndarray,
    protected_component: np.ndarray,
    reserved_component: np.ndarray,
    cell_ids: Sequence[str | None],
    source_family: np.ndarray,
    proposed_family: np.ndarray,
) -> None:
    _require(np.array_equal(source_family, proposed_family), "family selection bytes changed")
    _require(np.all(proposed_component[positive_component]), "component positive was removed")
    _require(np.all(proposed_component[protected_component]), "component protected row was removed")
    _require(np.all(proposed_component[reserved_component]), "component reservation was removed")
    _require(
        int(proposed_component.sum()) == int(source_component.sum()),
        "component selected-row total changed",
    )
    keys = sorted({key for key in cell_ids if key is not None})
    for key in keys:
        cell = np.asarray([value == key for value in cell_ids], dtype=np.bool_)
        _require(
            int((source_component & cell).sum()) == int((proposed_component & cell).sum()),
            f"component cell quota changed: {key}",
        )


def _component_masks(
    coverage: Mapping[str, np.ndarray],
    annulus: Mapping[str, np.ndarray],
    coverage_report: Mapping[str, Any],
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    distances = coverage["component_nearest_truth_distance_px"]
    selected = annulus["component_proposed_selected"]
    positive = distances <= np.float32(3.0)
    roles = coverage["component_retention_role_id"]
    strata = coverage["component_stratum_id"]
    role_ids = coverage_report["cache"]["retention_role_ids"]
    stratum_ids = coverage_report["cache"]["stratum_ids"]
    protected_names = {
        "topology_reserved", "connector_anchor_reserved", "sparse_fragment_reserved"
    }
    resolved_protected_names = set(role_ids) & protected_names
    _require(
        resolved_protected_names == {"topology_reserved", "connector_anchor_reserved"},
        "component protected-role inventory changed",
    )
    protected_ids = tuple(
        value for name, value in role_ids.items() if name in protected_names
    )
    protected = selected & np.isin(roles, protected_ids)
    hard_id = stratum_ids.get("hard_existing")
    _require(type(hard_id) is int, "component hard-existing stratum changed")
    protected |= selected & (strata == hard_id)
    reserved = annulus["component_reserved"]
    _require(not np.any(reserved & positive), "component reservation contains a positive row")
    return selected, positive, protected, reserved


def _component_cell_ids(
    coverage: Mapping[str, np.ndarray], coverage_report: Mapping[str, Any]
) -> list[str | None]:
    distances = coverage["component_nearest_truth_distance_px"]
    radii = coverage["component_nearest_truth_radius_px"]
    strata = coverage["component_stratum_id"]
    names = {
        int(index): name
        for name, index in coverage_report["cache"]["stratum_ids"].items()
    }
    result: list[str | None] = []
    for distance, radius, stratum in zip(distances, radii, strata, strict=True):
        band = measure_negative_coverage.distance_band(float(distance))
        if band == "positive_le3":
            result.append(None)
        else:
            result.append(
                "|".join((
                    band,
                    names[int(stratum)],
                    measure_negative_coverage.radius_category(float(radius)),
                ))
            )
    return result


def _regenerate_component_bins(
    coverage: Mapping[str, np.ndarray], *, deadline_at: float
) -> tuple[np.ndarray, int]:
    bin_ids = np.empty(EXPECTED_COMPONENT_ROWS, dtype=np.uint8)
    offset = 0
    maximum_scene_proposals = 0
    scenes = boundary.v25.build_split("train")
    _require(len(scenes) == 167, "component train scene count changed")
    for scene_index, scene in enumerate(scenes):
        _require(time.perf_counter() <= deadline_at, "elapsed-time bound exceeded during binning")
        proposals = boundary.v25.v24.extract_proposals(scene.tensor)
        values, nearest_index, nearest_distance = boundary.v26._proposal_values(scene, proposals)
        count = len(proposals.coordinates)
        rows = slice(offset, offset + count)
        try:
            boundary.v26._validate_regenerated_rows(
                "component", scene_index, proposals.coordinates,
                nearest_index, nearest_distance, coverage, rows,
            )
        except Exception as error:
            raise PreflightError(str(error)) from error
        radii = np.asarray(scene.diameters, dtype=np.float32) / np.float32(2.0)
        regenerated_radii = radii[nearest_index]
        _require(
            np.array_equal(
                coverage["component_nearest_truth_radius_px"][rows], regenerated_radii
            ),
            f"component regenerated truth radius changed at scene {scene_index}",
        )
        scene_bins = stratified_background._bin_ids(values[0]).cpu().numpy()
        _require(
            scene_bins.shape == (count,) and np.all((scene_bins >= 0) & (scene_bins < BIN_COUNT)),
            f"component patch-bin contract changed at scene {scene_index}",
        )
        bin_ids[rows] = scene_bins.astype(np.uint8, copy=False)
        maximum_scene_proposals = max(maximum_scene_proposals, count)
        offset += count
    _require(offset == EXPECTED_COMPONENT_ROWS, "component regenerated row count changed")
    return bin_ids, maximum_scene_proposals


def _aggregate_counts(
    labels: Sequence[str | None],
    eligible: np.ndarray,
    before: np.ndarray,
    after: np.ndarray,
) -> dict[str, dict[str, int]]:
    result: dict[str, dict[str, int]] = {}
    for name in sorted({value for value in labels if value is not None}):
        mask = np.asarray([value == name for value in labels], dtype=np.bool_)
        result[str(name)] = {
            "eligible": int((eligible & mask).sum()),
            "selected_before": int((eligible & before & mask).sum()),
            "selected_after": int((eligible & after & mask).sum()),
        }
    return result


def run(
    *,
    output_directory: Path,
    source_sha256: str,
    root: Path = REPO_ROOT,
    max_elapsed_seconds: float = 180.0,
    torch_threads: int = 1,
) -> dict[str, Any]:
    started = time.perf_counter()
    deadline_at = started + max_elapsed_seconds
    root = root.resolve()
    output = (
        output_directory if output_directory.is_absolute() else root / output_directory
    ).resolve()
    _require(root / "artifacts" in output.parents and not output.exists(),
             "output directory must be new under artifacts")
    _require(max_elapsed_seconds > 0 and torch_threads > 0, "runtime bounds are invalid")
    _require(_sha256(Path(__file__)) == source_sha256, "preflight source changed")
    _require(
        _sha256(root / BOUNDARY_SOURCE_PATH) == BOUNDARY_SOURCE_SHA256,
        "authenticated boundary helper changed",
    )
    authentication_started = time.perf_counter()
    authenticated_sources, _ = boundary._authenticate_sources(root)
    required_sources = {
        "ml/markers/center/mask_preserving_v24/stratified_background.py",
        "ml/markers/center/localization_confidence_v26/measure_negative_coverage.py",
        "ml/markers/center/localization_confidence_v26/annulus_reservation_preflight.py",
    }
    authenticated_paths = {str(row["path"]) for row in authenticated_sources}
    _require(required_sources <= authenticated_paths, "selection helper sources were not authenticated")
    coverage_report = _read_json(
        root / boundary.COVERAGE_REPORT_PATH, boundary.COVERAGE_REPORT_SHA256
    )
    annulus_report = _read_json(
        root / boundary.ANNULUS_REPORT_PATH, boundary.ANNULUS_REPORT_SHA256
    )
    _require(
        coverage_report.get("status") == "model_free_complete"
        and coverage_report.get("private_reads") == 0
        and coverage_report.get("sealed_runs") == 0
        and annulus_report.get("status") == "model_free_preflight_only"
        and annulus_report.get("dev_truth_used_for_selection") is False
        and annulus_report.get("private_reads") == 0
        and annulus_report.get("sealed_runs") == 0,
        "frozen train-only evidence scope changed",
    )
    coverage_names = {
        f"{scope}_{name}"
        for scope in ("component", "family")
        for name in (
            "scene_index", "proposal_index", "coordinates", "nearest_truth_index",
            "nearest_truth_distance_px", "nearest_truth_radius_px", "selected",
            "stratum_id", "retention_role_id",
        )
    }
    annulus_names = {
        f"{scope}_{name}"
        for scope in ("component", "family")
        for name in ("proposed_selected", "reserved", "added", "displaced")
    }
    coverage = _load_authenticated_npz(
        root / boundary.COVERAGE_CACHE_PATH,
        boundary.COVERAGE_CACHE_SHA256,
        coverage_names,
    )
    annulus = _load_authenticated_npz(
        root / boundary.ANNULUS_CACHE_PATH,
        boundary.ANNULUS_CACHE_SHA256,
        annulus_names,
    )
    authentication_ms = (time.perf_counter() - authentication_started) * 1000.0

    source_component, positive, protected, reserved = _component_masks(
        coverage, annulus, coverage_report
    )
    source_family = annulus["family_proposed_selected"]
    family_positive = coverage["family_nearest_truth_distance_px"] <= np.float32(3.0)
    _require(
        len(source_component) == EXPECTED_COMPONENT_ROWS
        and int((source_component & positive).sum()) == EXPECTED_COMPONENT_POSITIVES
        and int((source_component & ~positive).sum()) == EXPECTED_COMPONENT_NEGATIVES
        and int(source_component.sum()) == EXPECTED_COMPONENT_SELECTED
        and int(protected.sum()) == EXPECTED_COMPONENT_PROTECTED
        and int(reserved.sum()) == EXPECTED_COMPONENT_RESERVED,
        "component frozen population changed",
    )
    _require(
        len(source_family) == EXPECTED_FAMILY_ROWS
        and int((source_family & family_positive).sum()) == EXPECTED_FAMILY_POSITIVES
        and int((source_family & ~family_positive).sum()) == EXPECTED_FAMILY_NEGATIVES
        and int(source_family.sum()) == EXPECTED_FAMILY_SELECTED,
        "family frozen population changed",
    )
    component_source_sha = boundary.v26.annulus_reservation_preflight._selection_sha256(
        source_component,
        coverage["component_scene_index"],
        coverage["component_proposal_index"],
    )
    family_source_sha = boundary.v26.annulus_reservation_preflight._selection_sha256(
        source_family,
        coverage["family_scene_index"],
        coverage["family_proposal_index"],
    )
    _require(component_source_sha == EXPECTED_COMPONENT_SELECTION_SHA256,
             "component V26 selection identity changed")
    _require(family_source_sha == EXPECTED_FAMILY_SELECTION_SHA256,
             "family V26 selection identity changed")

    torch.set_num_threads(torch_threads)
    regeneration_started = time.perf_counter()
    bin_ids, maximum_scene_proposals = _regenerate_component_bins(
        coverage, deadline_at=deadline_at
    )
    regeneration_ms = (time.perf_counter() - regeneration_started) * 1000.0
    cell_ids = _component_cell_ids(coverage, coverage_report)
    selection_started = time.perf_counter()
    proposed_component, cell_report = _select_component_mask(
        source_component,
        positive,
        protected,
        reserved,
        cell_ids,
        bin_ids,
        seed=negative_sampler.SAMPLER_SEED,
    )
    proposed_family = source_family.copy()
    _validate_final_masks(
        source_component, proposed_component, positive, protected, reserved,
        cell_ids, source_family, proposed_family,
    )
    selection_ms = (time.perf_counter() - selection_started) * 1000.0
    _require(time.perf_counter() <= deadline_at, "elapsed-time bound exceeded after selection")

    distance_labels = [
        measure_negative_coverage.distance_band(float(value))
        for value in coverage["component_nearest_truth_distance_px"]
    ]
    radius_labels = [
        measure_negative_coverage.radius_category(float(value))
        for value in coverage["component_nearest_truth_radius_px"]
    ]
    stratum_names = {
        int(index): name
        for name, index in coverage_report["cache"]["stratum_ids"].items()
    }
    stratum_labels = [
        stratum_names[int(value)] for value in coverage["component_stratum_id"]
    ]
    negative = ~positive
    by_distance = _aggregate_counts(
        distance_labels, negative, source_component, proposed_component
    )
    by_stratum = _aggregate_counts(
        stratum_labels, negative, source_component, proposed_component
    )
    by_radius = _aggregate_counts(
        radius_labels, negative, source_component, proposed_component
    )
    family_distance_labels = [
        measure_negative_coverage.distance_band(float(value))
        for value in coverage["family_nearest_truth_distance_px"]
    ]
    family_radius_labels = [
        measure_negative_coverage.radius_category(float(value))
        for value in coverage["family_nearest_truth_radius_px"]
    ]
    family_stratum_labels = [
        stratum_names[int(value)] for value in coverage["family_stratum_id"]
    ]
    family_cell_labels = [
        None if band == "positive_le3" else "|".join((band, stratum, radius))
        for band, stratum, radius in zip(
            family_distance_labels,
            family_stratum_labels,
            family_radius_labels,
            strict=True,
        )
    ]
    family_negative = ~family_positive
    family_by_distance = _aggregate_counts(
        family_distance_labels, family_negative, source_family, proposed_family
    )
    family_by_stratum = _aggregate_counts(
        family_stratum_labels, family_negative, source_family, proposed_family
    )
    family_by_radius = _aggregate_counts(
        family_radius_labels, family_negative, source_family, proposed_family
    )
    family_by_cell = _aggregate_counts(
        family_cell_labels, family_negative, source_family, proposed_family
    )
    _require(len(cell_report) == EXPECTED_COMPONENT_CELL_COUNT,
             "component negative allocation-cell inventory changed")
    _require(len(family_by_cell) == EXPECTED_FAMILY_CELL_COUNT,
             "family negative allocation-cell inventory changed")
    _require(
        {name: by_distance[name]["selected_after"] for name in COMPONENT_BAND_COUNTS}
        == COMPONENT_BAND_COUNTS,
        "component distance-band allocations changed",
    )
    expected_strata = annulus_report["component_train"]["new_selected_negative_strata"]
    _require(
        {name: row["selected_after"] for name, row in by_stratum.items() if row["selected_after"]}
        == expected_strata,
        "component semantic-stratum quotas changed",
    )
    added = proposed_component & ~source_component
    displaced = source_component & ~proposed_component
    _require(int(added.sum()) == int(displaced.sum()), "replacement count is unbalanced")
    proposed_component_sha = boundary.v26.annulus_reservation_preflight._selection_sha256(
        proposed_component,
        coverage["component_scene_index"],
        coverage["component_proposal_index"],
    )
    proposed_family_sha = boundary.v26.annulus_reservation_preflight._selection_sha256(
        proposed_family,
        coverage["family_scene_index"],
        coverage["family_proposal_index"],
    )
    coverage_regressions = {
        key: row["coverage_regression_bin_ids"]
        for key, row in cell_report.items()
        if row["coverage_regression_bin_ids"]
    }
    zero_quota_cells = {
        key: row["eligible_zero_quota_bins"]
        for key, row in cell_report.items()
        if row["eligible_zero_quota_bins"]
    }

    _require(_sha256(Path(__file__)) == source_sha256, "preflight source changed during run")
    output.mkdir(parents=True, exist_ok=False)
    cache_path = output / CACHE_NAME
    np.savez_compressed(
        cache_path,
        component_proposed_selected=proposed_component,
        component_added=added,
        component_displaced=displaced,
        component_bin_id=bin_ids,
        family_proposed_selected=proposed_family,
    )
    report = {
        "schema": SCHEMA,
        "status": "model_free_preflight_only",
        "scope": "frozen-v26-synthetic-train-component-selection-coverage",
        "diagnostic_source_sha256": source_sha256,
        "authenticated_sources": authenticated_sources,
        "inputs": [
            {"path": path.as_posix(), "sha256": digest}
            for path, digest in (
                (BOUNDARY_SOURCE_PATH, BOUNDARY_SOURCE_SHA256),
                (boundary.COVERAGE_REPORT_PATH, boundary.COVERAGE_REPORT_SHA256),
                (boundary.COVERAGE_CACHE_PATH, boundary.COVERAGE_CACHE_SHA256),
                (boundary.ANNULUS_REPORT_PATH, boundary.ANNULUS_REPORT_SHA256),
                (boundary.ANNULUS_CACHE_PATH, boundary.ANNULUS_CACHE_SHA256),
            )
        ],
        "policy": {
            "scope_changed": "component discretionary negatives only",
            "family_selection": "byte-identical copy of frozen V26 proposed mask",
            "mandatory_component_rows": "union of positives, protected rows, and reservations",
            "cell": ["distance_band", "original_semantic_stratum", "truth_radius_category"],
            "cell_quota": "exact selected count in frozen V26 proposed component mask",
            "bin_definition": "frozen V24 64-bin patch signature",
            "allocator": "frozen allocate_quotas semantics per cell",
            "within_bin_selection": "frozen seeded torch.randperm semantics per cell",
            "seed": negative_sampler.SAMPLER_SEED,
            "seed_reset_per_cell": True,
            "dev_rows_or_scores_used": False,
        },
        "populations": {
            "component": {
                "proposal_rows": len(source_component),
                "positive_selected": int((source_component & positive).sum()),
                "negative_selected": int((source_component & negative).sum()),
                "protected_selected_negative": int(protected.sum()),
                "reserved_selected_negative": int(reserved.sum()),
                "mandatory_union_rows": int((positive | protected | reserved).sum()),
                "selected_before": int(source_component.sum()),
                "selected_after": int(proposed_component.sum()),
                "added": int(added.sum()),
                "displaced": int(displaced.sum()),
            },
            "family": {
                "proposal_rows": len(source_family),
                "positive_selected": int((source_family & family_positive).sum()),
                "negative_selected": int((source_family & ~family_positive).sum()),
                "selected_before": int(source_family.sum()),
                "selected_after": int(proposed_family.sum()),
                "byte_identical": bool(np.array_equal(source_family, proposed_family)),
            },
        },
        "allocations": {
            "component_by_distance_band": by_distance,
            "component_by_semantic_stratum": by_stratum,
            "component_by_truth_radius_category": by_radius,
            "component_cells": cell_report,
            "family_by_distance_band": family_by_distance,
            "family_by_semantic_stratum": family_by_stratum,
            "family_by_truth_radius_category": family_by_radius,
            "family_cells": family_by_cell,
        },
        "bin_coverage": {
            "cell_count": len(cell_report),
            "cells_with_coverage_regression": coverage_regressions,
            "coverage_regression_count": sum(map(len, coverage_regressions.values())),
            "cells_with_eligible_zero_quota_bins": zero_quota_cells,
            "eligible_zero_quota_bin_count": sum(map(len, zero_quota_cells.values())),
        },
        "selection_identity": {
            "component_source_sha256": component_source_sha,
            "component_proposed_sha256": proposed_component_sha,
            "family_source_sha256": family_source_sha,
            "family_proposed_sha256": proposed_family_sha,
        },
        "cache": {
            "path": CACHE_NAME,
            "sha256": _sha256(cache_path),
            "arrays": {
                "component_proposed_selected": {"dtype": "bool", "shape": [len(proposed_component)]},
                "component_added": {"dtype": "bool", "shape": [len(added)]},
                "component_displaced": {"dtype": "bool", "shape": [len(displaced)]},
                "component_bin_id": {"dtype": "uint8", "shape": [len(bin_ids)]},
                "family_proposed_selected": {"dtype": "bool", "shape": [len(proposed_family)]},
            },
        },
        "resource": {
            "elapsed_time_bound_seconds": max_elapsed_seconds,
            "authentication_ms": round(authentication_ms, 3),
            "component_regeneration_and_binning_ms": round(regeneration_ms, 3),
            "selection_ms": round(selection_ms, 3),
            "elapsed_ms_before_report_write": round((time.perf_counter() - started) * 1000.0, 3),
            "torch_threads": torch_threads,
            "maximum_scene_proposal_rows_materialized": maximum_scene_proposals,
            "retained_component_bin_id_bytes": int(bin_ids.nbytes),
            "full_patch_pool_materialized": False,
            "peak_memory_measured": False,
        },
        "limitations": [
            "This is train-only feasibility evidence and does not establish that coverage or model accuracy improved.",
            "The frozen allocator favors lower bin IDs when a cell fill budget is smaller than its nonempty-bin count.",
            "Mandatory rows remain outside the quota allocator and may already occupy bins selected for discretionary fill.",
            "Resetting the same frozen seed independently per cell is deterministic and can correlate within-bin offsets across cells.",
            "Family selection is unchanged because the cross-split support diagnosis covered component scenes only.",
            "No model revision, training, checkpoint inference, threshold selection, private read, or sealed read occurred.",
        ],
        "optimizer_steps": 0,
        "checkpoint_forward_passes": 0,
        "thresholds_selected": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
    }
    (output / REPORT_NAME).write_bytes(canonical_json_bytes(report))
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--max-elapsed-seconds", type=float, default=180.0)
    parser.add_argument("--torch-threads", type=int, default=1)
    args = parser.parse_args()
    report = run(
        output_directory=args.output_directory,
        source_sha256=args.source_sha,
        max_elapsed_seconds=args.max_elapsed_seconds,
        torch_threads=args.torch_threads,
    )
    print(json.dumps({
        "status": report["status"],
        "component_proposed_sha256": report["selection_identity"]["component_proposed_sha256"],
        "cache_sha256": report["cache"]["sha256"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
