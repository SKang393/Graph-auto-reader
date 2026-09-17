# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare bounded component-dev error/control patches with selected train support."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import heapq
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
from ml.markers.gate_seal import canonical_json_bytes  # noqa: E402


SCHEMA = "graphreader.marker-v26-component-cross-split-support.v1"
THRESHOLD = 0.25
QUERY_LIMIT = 32
EXPECTED_COMPONENT_TRAIN_PROPOSALS = 236_056
EXPECTED_COMPONENT_DEV_PROPOSALS = 224_840
EXPECTED_DEV_POSITIVES = 3_632
EXPECTED_DEV_POSITIVE_ERRORS = 683
EXPECTED_DEV_NEGATIVE_ERRORS = 2_375
EXPECTED_TRAIN_SELECTED = 35_838
EXPECTED_TRAIN_POSITIVES = 3_258
EXPECTED_TRAIN_NEGATIVES = 32_580
EXPECTED_SELECTION_SHA256 = "c184a97ae7ad0292361e62a4aaed5a54d8c1ae1bdb0ab3daab9ddeb8d46b1af0"
RANK_DOMAIN = "graphreader.marker-v26-component-cross-split-query.v1"

BOUNDARY_SOURCE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/diagnose_marker_v26_confidence_boundary.py"
)
BOUNDARY_SOURCE_SHA256 = "b79793b2027407247a90584cae26ce1009ca13446b3e2af806a5134bf5a84225"
BOUNDARY_RESULT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-confidence-boundary/diagnosis-v1.json"
)
BOUNDARY_RESULT_SHA256 = "b1661bff1963f7fc94b83985a181f892d37ba918386bdf051061518096e2a60c"
DEV_REPORT_PATH = Path("artifacts/goal22-runs/marker-v26-annulus/diagnosis-v1/diagnosis.json")
DEV_REPORT_SHA256 = "936a5ae1bca09bd431d94ad7c6acfc6cf521af78e8e45afea3498305a09250f8"
DEV_OUTPUT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-annulus/diagnosis-v1/fixed-v26-dev-outputs.npz"
)
DEV_OUTPUT_SHA256 = "ff0d332ef0c7b7c8b3084e2ae2357cb551d5ce698d320e67077bcd2b63cec90d"
V25_CACHE_REPORT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/"
    "suppressor-anchor-diagnosis-v1/diagnosis.json"
)
V25_CACHE_REPORT_SHA256 = "ad923c8f9c658e4a51b4babf728f30f68e9bf33fb3d4528587b6f3e5998290db"
V25_CACHE_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/"
    "suppressor-anchor-diagnosis-v1/fixed-v25-dev-outputs.npz"
)
V25_CACHE_SHA256 = "d9247c03dded13b00c582a64152976a9b89f1b01704670cfdc5f226c9d09eb72"

STRATA = (
    "positive_le3",
    "negative_gt3_le5",
    "negative_gt5_le8",
    "negative_gt8_le12",
    "negative_outside_annuli",
)
CHANNELS = ("ink", "text_mask", "artifact_mask")


class DiagnosticError(RuntimeError):
    """An authenticated input, population, or bounded search contract changed."""


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise DiagnosticError(message)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _read_json(path: Path, expected: str) -> dict[str, Any]:
    _require(_sha256(path) == expected, f"authenticated JSON changed: {path}")
    value = json.loads(path.read_bytes())
    _require(isinstance(value, dict), f"authenticated JSON is not an object: {path}")
    return value


def _load_authenticated_npz(
    path: Path, expected_sha256: str, names: set[str]
) -> dict[str, np.ndarray]:
    _require(_sha256(path) == expected_sha256, f"authenticated NPZ changed: {path}")
    return boundary._load_npz(path, names)


def _array_sha(value: np.ndarray) -> str:
    array = np.ascontiguousarray(value)
    metadata = canonical_json_bytes({"dtype": str(array.dtype), "shape": list(array.shape)})
    digest = hashlib.sha256()
    digest.update(len(metadata).to_bytes(8, "little"))
    digest.update(metadata)
    digest.update(array.tobytes(order="C"))
    return digest.hexdigest()


def _summary(values: Sequence[float] | np.ndarray) -> dict[str, float | int]:
    array = np.asarray(values, dtype=np.float64)
    if not len(array):
        return {"count": 0}
    return {
        "count": int(len(array)),
        "minimum": float(array.min()),
        "p05": float(np.quantile(array, 0.05)),
        "median": float(np.quantile(array, 0.5)),
        "p95": float(np.quantile(array, 0.95)),
        "maximum": float(array.max()),
    }


def _stratum(distance: float) -> str:
    if distance <= 3.0:
        return "positive_le3"
    if distance <= 5.0:
        return "negative_gt3_le5"
    if distance <= 8.0:
        return "negative_gt5_le8"
    if distance <= 12.0:
        return "negative_gt8_le12"
    return "negative_outside_annuli"


def _query_rank(cohort: str, scene_identity: str, proposal_index: int) -> int:
    material = (
        f"{RANK_DOMAIN}\n{cohort}\n{scene_identity}\n{proposal_index}\n"
    ).encode("ascii")
    return int.from_bytes(hashlib.sha256(material).digest(), "big")


def _offer_ranked(
    heap: list[tuple[int, str, int, torch.Tensor, float]],
    *,
    rank: int,
    scene_identity: str,
    proposal_index: int,
    patch: torch.Tensor,
    score: float,
    limit: int,
) -> None:
    key = (-rank, scene_identity, proposal_index)
    if len(heap) < limit or key > heap[0][:3]:
        entry = (*key, patch.detach().clone(), score)
        if len(heap) < limit:
            heapq.heappush(heap, entry)
        else:
            heapq.heapreplace(heap, entry)


def _ordered_sample(
    heap: Sequence[tuple[int, str, int, torch.Tensor, float]],
) -> list[tuple[int, str, int, torch.Tensor, float]]:
    return sorted(heap, key=lambda item: (-item[0], item[1], item[2]))


def _deadline(deadline_at: float | None, stage: str) -> None:
    if deadline_at is not None and time.perf_counter() > deadline_at:
        raise DiagnosticError(f"elapsed-time bound exceeded during {stage}")


def _nearest_support(
    queries: torch.Tensor,
    candidates: torch.Tensor,
    *,
    block_size: int,
    deadline_at: float | None = None,
) -> dict[str, np.ndarray | float]:
    """Search every candidate with FP64 norm/dot, then directly resolve near ties."""
    _require(queries.ndim == 4 and candidates.ndim == 4, "patch tensors must be NCHW")
    _require(
        tuple(queries.shape[1:]) == tuple(candidates.shape[1:]) and queries.shape[1] == 3,
        "query and candidate patch shapes differ",
    )
    _require(
        len(queries) > 0 and len(candidates) > 0 and block_size > 0,
        "nearest support requires patches and a positive block size",
    )
    flat_queries = queries.reshape(len(queries), -1).to(dtype=torch.float64)
    _require(
        bool(torch.isfinite(flat_queries).all().item())
        and float(flat_queries.min().item()) >= 0.0
        and float(flat_queries.max().item()) <= 1.0,
        "query patch values must be finite and within [0, 1]",
    )
    query_norms = (flat_queries * flat_queries).sum(dim=1)
    dimension = flat_queries.shape[1]
    epsilon = np.finfo(np.float64).eps
    gamma = (dimension * epsilon) / (1.0 - dimension * epsilon)
    roundoff_band = float(32.0 * gamma * dimension)
    best = np.full(len(queries), np.inf, dtype=np.float64)
    near: list[list[tuple[float, int]]] = [[] for _ in range(len(queries))]

    for start in range(0, len(candidates), block_size):
        _deadline(deadline_at, "full-candidate norm/dot search")
        stop = min(start + block_size, len(candidates))
        candidate_block = candidates[start:stop].reshape(stop - start, -1).to(torch.float64)
        _require(
            bool(torch.isfinite(candidate_block).all().item())
            and float(candidate_block.min().item()) >= 0.0
            and float(candidate_block.max().item()) <= 1.0,
            "candidate patch values must be finite and within [0, 1]",
        )
        candidate_norms = (candidate_block * candidate_block).sum(dim=1)
        raw_squared = (
            query_norms[:, None]
            + candidate_norms[None, :]
            - 2.0 * (flat_queries @ candidate_block.T)
        )
        _require(
            float(raw_squared.min().item()) >= -roundoff_band,
            "norm/dot squared distance exceeded the negative roundoff band",
        )
        squared = raw_squared.clamp_min_(0.0)
        squared_np = squared.cpu().numpy()
        block_best = squared_np.min(axis=1)
        for query_index in range(len(queries)):
            new_best = min(best[query_index], block_best[query_index])
            cutoff = new_best + roundoff_band
            if new_best < best[query_index]:
                near[query_index] = [
                    item for item in near[query_index] if item[0] <= cutoff
                ]
            local = np.flatnonzero(squared_np[query_index] <= cutoff)
            near[query_index].extend(
                (float(squared_np[query_index, index]), start + int(index))
                for index in local
            )
            best[query_index] = new_best

    nearest_indices = np.empty(len(queries), dtype=np.int64)
    direct_squared = np.empty(len(queries), dtype=np.float64)
    channel_max = np.empty((len(queries), 3), dtype=np.float64)
    near_counts = np.empty(len(queries), dtype=np.int64)
    for query_index, approximate in enumerate(near):
        _require(bool(approximate), "roundoff candidate set is empty")
        indices = sorted({index for _, index in approximate})
        query = queries[query_index].to(torch.float64)
        direct = (
            (candidates[indices].to(torch.float64) - query[None, :]).square()
            .reshape(len(indices), -1)
            .sum(dim=1)
            .cpu()
            .numpy()
        )
        chosen_offset = min(range(len(indices)), key=lambda index: (direct[index], indices[index]))
        chosen = indices[chosen_offset]
        difference = (candidates[chosen].to(torch.float64) - query).abs()
        nearest_indices[query_index] = chosen
        direct_squared[query_index] = direct[chosen_offset]
        channel_max[query_index] = difference.reshape(3, -1).max(dim=1).values.cpu().numpy()
        near_counts[query_index] = len(indices)
    return {
        "nearest_indices": nearest_indices,
        "direct_squared": direct_squared,
        "normalized_rmse": np.sqrt(direct_squared / dimension),
        "channel_max": channel_max,
        "roundoff_candidate_counts": near_counts,
        "roundoff_band_squared_distance": roundoff_band,
    }


def _load_train_patches(
    coverage: Mapping[str, np.ndarray],
    selected: np.ndarray,
    *,
    deadline_at: float,
) -> tuple[torch.Tensor, torch.Tensor]:
    scene_indices = coverage["component_scene_index"]
    proposal_indices = coverage["component_proposal_index"]
    coordinates = coverage["component_coordinates"]
    distances = coverage["component_nearest_truth_distance_px"]
    _require(len(selected) == EXPECTED_COMPONENT_TRAIN_PROPOSALS, "train selection size changed")
    _require(
        boundary.v26.annulus_reservation_preflight._selection_sha256(
            selected, scene_indices, proposal_indices
        )
        == EXPECTED_SELECTION_SHA256,
        "component selected-row identity changed",
    )
    positive_patches = torch.empty(
        (EXPECTED_TRAIN_POSITIVES, 3, 33, 33), dtype=torch.float32
    )
    negative_patches = torch.empty(
        (EXPECTED_TRAIN_NEGATIVES, 3, 33, 33), dtype=torch.float32
    )
    positive_offset = 0
    negative_offset = 0
    offset = 0
    scenes = boundary.v25.build_split("train")
    _require(len(scenes) == 167, "component train scene count changed")
    for scene_index, scene in enumerate(scenes):
        _deadline(deadline_at, "component train patch reconstruction")
        proposals = boundary.v25.v24.extract_proposals(scene.tensor)
        count = len(proposals.coordinates)
        row = slice(offset, offset + count)
        actual_coordinates = proposals.coordinates.detach().cpu().numpy().astype(np.float32, copy=False)
        _require(
            np.all(scene_indices[row] == scene_index)
            and np.array_equal(proposal_indices[row], np.arange(count, dtype=proposal_indices.dtype))
            and np.array_equal(coordinates[row], actual_coordinates),
            f"component train proposal inventory changed at scene {scene_index}",
        )
        local_selected = selected[row]
        selected_mask = torch.from_numpy(
            np.asarray(local_selected, dtype=np.bool_)
        )
        selected_patches = proposals.patches[selected_mask]
        _require(
            selected_patches.dtype == torch.float32
            and tuple(selected_patches.shape[1:]) == (3, 33, 33),
            f"component train patch contract changed at scene {scene_index}",
        )
        positive_mask = torch.from_numpy(
            np.asarray(distances[row][local_selected] <= 3.0, dtype=np.bool_)
        )
        positive_count = int(positive_mask.sum().item())
        negative_count = len(selected_patches) - positive_count
        positive_patches[
            positive_offset : positive_offset + positive_count
        ].copy_(selected_patches[positive_mask])
        negative_patches[
            negative_offset : negative_offset + negative_count
        ].copy_(selected_patches[~positive_mask])
        positive_offset += positive_count
        negative_offset += negative_count
        offset += count
    _require(offset == EXPECTED_COMPONENT_TRAIN_PROPOSALS, "component train rows changed")
    _require(
        positive_offset == EXPECTED_TRAIN_POSITIVES
        and negative_offset == EXPECTED_TRAIN_NEGATIVES
        and positive_offset + negative_offset == EXPECTED_TRAIN_SELECTED,
        "component selected train labels changed",
    )
    return positive_patches, negative_patches


def _sample_dev_queries(
    old_report: Mapping[str, Any],
    new_report: Mapping[str, Any],
    old_cache_path: Path,
    new_cache_path: Path,
    *,
    deadline_at: float,
) -> tuple[
    dict[tuple[str, str], list[tuple[int, str, int, torch.Tensor, float]]],
    Counter[tuple[str, str]],
]:
    old_manifest = old_report["component_dev"]["cache_manifest"]
    new_manifest = new_report["component_dev"]["cache_manifest"]
    scenes = boundary.v25.build_split("dev", independent_layout=True)
    _require(len(scenes) == len(old_manifest) == len(new_manifest) == 167, "dev manifest changed")
    samples: dict[
        tuple[str, str], list[tuple[int, str, int, torch.Tensor, float]]
    ] = defaultdict(list)
    populations: Counter[tuple[str, str]] = Counter()
    total = positives = positive_errors = negative_errors = 0
    with np.load(old_cache_path, allow_pickle=False) as old_archive, np.load(
        new_cache_path, allow_pickle=False
    ) as new_archive:
        for scene_index, (scene, old_row, new_row) in enumerate(
            zip(scenes, old_manifest, new_manifest, strict=True)
        ):
            _deadline(deadline_at, "component dev authenticated query sampling")
            proposals = boundary.v25.v24.extract_proposals(scene.tensor)
            coordinates = proposals.coordinates.detach().cpu().numpy().astype(np.float32, copy=False)
            patches = proposals.patches
            cached_coordinates = np.asarray(old_archive[old_row["proposal_coordinates_key"]])
            outputs = np.asarray(new_archive[new_row["v26_candidate_predictions_key"]])
            identity = f"{scene.split}:{scene.family}:{scene.seed}"
            _require(
                identity == old_row["scene_identity"] == new_row["scene_identity"]
                and len(coordinates) == old_row["proposal_count"] == new_row["proposal_count"]
                and np.array_equal(coordinates, cached_coordinates)
                and _array_sha(coordinates) == old_row["proposal_coordinates_sha256"]
                and _array_sha(coordinates) == new_row["proposal_coordinates_sha256"]
                and _array_sha(outputs) == new_row["v26_candidate_predictions_sha256"]
                and _array_sha(patches.detach().cpu().numpy().astype(np.float32, copy=False))
                == new_row["proposal_patches_sha256"],
                f"component dev cached proposal identity changed at scene {scene_index}",
            )
            centers = np.asarray(scene.centers, dtype=np.float32)
            nearest = np.linalg.norm(coordinates[:, None, :] - centers[None, :, :], axis=2).min(axis=1)
            scores = outputs[:, 0]
            for proposal_index, (distance, score) in enumerate(zip(nearest, scores, strict=True)):
                stratum = _stratum(float(distance))
                is_positive = stratum == "positive_le3"
                is_error = float(score) < THRESHOLD if is_positive else float(score) >= THRESHOLD
                outcome = "error" if is_error else "control"
                cohort = (stratum, outcome)
                populations[cohort] += 1
                rank = _query_rank(f"{stratum}:{outcome}", identity, proposal_index)
                _offer_ranked(
                    samples[cohort],
                    rank=rank,
                    scene_identity=identity,
                    proposal_index=proposal_index,
                    patch=patches[proposal_index],
                    score=float(score),
                    limit=QUERY_LIMIT,
                )
                total += 1
                positives += int(is_positive)
                positive_errors += int(is_positive and is_error)
                negative_errors += int(not is_positive and is_error)
    _require(
        total == EXPECTED_COMPONENT_DEV_PROPOSALS
        and positives == EXPECTED_DEV_POSITIVES
        and positive_errors == EXPECTED_DEV_POSITIVE_ERRORS
        and negative_errors == EXPECTED_DEV_NEGATIVE_ERRORS,
        "component dev fixed-threshold populations changed",
    )
    return samples, populations


def run(
    *,
    output: Path,
    source_sha256: str,
    root: Path = REPO_ROOT,
    max_elapsed_seconds: float = 360.0,
    candidate_block_size: int = 1024,
    torch_threads: int = 1,
) -> dict[str, Any]:
    started = time.perf_counter()
    deadline_at = started + max_elapsed_seconds
    root = root.resolve()
    output = (output if output.is_absolute() else root / output).resolve()
    _require(root / "artifacts" in output.parents and not output.exists(), "output must be new under artifacts")
    _require(
        0 < candidate_block_size <= 4096 and torch_threads > 0 and max_elapsed_seconds > 0,
        "runtime bounds are invalid",
    )
    _require(_sha256(Path(__file__)) == source_sha256, "diagnostic source changed")
    _require(
        _sha256(root / BOUNDARY_SOURCE_PATH) == BOUNDARY_SOURCE_SHA256,
        "completed boundary diagnostic source changed",
    )
    authenticated_sources, _ = boundary._authenticate_sources(root)
    boundary_result = _read_json(root / BOUNDARY_RESULT_PATH, BOUNDARY_RESULT_SHA256)
    old_report = _read_json(root / V25_CACHE_REPORT_PATH, V25_CACHE_REPORT_SHA256)
    new_report = _read_json(root / DEV_REPORT_PATH, DEV_REPORT_SHA256)
    _require(_sha256(root / V25_CACHE_PATH) == V25_CACHE_SHA256, "V25 dev cache changed")
    _require(_sha256(root / DEV_OUTPUT_PATH) == DEV_OUTPUT_SHA256, "V26 dev cache changed")
    _require(
        boundary_result["row_inventory"]["component"] == {
            "negative_rows": EXPECTED_TRAIN_NEGATIVES,
            "positive_rows": EXPECTED_TRAIN_POSITIVES,
            "selected_rows": EXPECTED_TRAIN_SELECTED,
            "selection_evidence": boundary_result["row_inventory"]["component"]["selection_evidence"],
        }
        and boundary_result["row_inventory"]["component"]["selection_evidence"][
            "proposed_selection_sha256"
        ]
        == EXPECTED_SELECTION_SHA256,
        "completed component boundary inventory changed",
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
        for name in ("reserved", "proposed_selected", "added", "displaced")
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
    torch.set_num_threads(torch_threads)
    train_positive_patches, train_negative_patches = _load_train_patches(
        coverage,
        annulus["component_proposed_selected"],
        deadline_at=deadline_at,
    )
    samples, populations = _sample_dev_queries(
        old_report,
        new_report,
        root / V25_CACHE_PATH,
        root / DEV_OUTPUT_PATH,
        deadline_at=deadline_at,
    )
    cohorts: dict[tuple[str, str], list[tuple[int, str, int, torch.Tensor, float]]] = {}
    population_report: dict[str, Any] = {}
    for stratum in STRATA:
        errors = _ordered_sample(samples[(stratum, "error")])
        controls = _ordered_sample(samples[(stratum, "control")])[: len(errors)]
        _require(len(errors) == len(controls), f"unbalanced sampled cohort: {stratum}")
        cohorts[(stratum, "error")] = errors
        cohorts[(stratum, "control")] = controls
        population_report[stratum] = {
            outcome: {
                "population": int(populations[(stratum, outcome)]),
                "sampled": len(cohorts[(stratum, outcome)]),
                "excluded": int(populations[(stratum, outcome)])
                - len(cohorts[(stratum, outcome)]),
            }
            for outcome in ("error", "control")
        }

    support: dict[str, Any] = {stratum: {} for stratum in STRATA}
    search_reports: dict[str, dict[str, np.ndarray | float]] = {}
    positions: dict[tuple[str, str], np.ndarray] = {}
    for label, relevant, candidates in (
        ("positive", ("positive_le3",), train_positive_patches),
        ("negative", STRATA[1:], train_negative_patches),
    ):
        ordered_keys = [(stratum, outcome) for stratum in relevant for outcome in ("error", "control")]
        patch_parts: list[torch.Tensor] = []
        offset = 0
        for key in ordered_keys:
            values = cohorts[key]
            positions[key] = np.arange(offset, offset + len(values), dtype=np.int64)
            patch_parts.extend(item[3] for item in values)
            offset += len(values)
        _require(bool(patch_parts), f"no sampled {label} queries")
        query_tensor = torch.stack(patch_parts)
        search_reports[label] = _nearest_support(
            query_tensor,
            candidates,
            block_size=candidate_block_size,
            deadline_at=deadline_at,
        )
        for key in ordered_keys:
            indices = positions[key]
            result = search_reports[label]
            scores = [item[4] for item in cohorts[key]]
            channels = np.asarray(result["channel_max"])[indices]
            support[key[0]][key[1]] = {
                "query_count": len(indices),
                "score": _summary(scores),
                "nearest_selected_train_normalized_rmse": _summary(
                    np.asarray(result["normalized_rmse"])[indices]
                ),
                "nearest_selected_train_channel_max_absolute_difference": {
                    channel: _summary(channels[:, channel_index])
                    for channel_index, channel in enumerate(CHANNELS)
                },
                "roundoff_candidate_count": _summary(
                    np.asarray(result["roundoff_candidate_counts"])[indices]
                ),
            }

    _deadline(deadline_at, "report assembly")
    sampled_positive = sum(len(cohorts[(STRATA[0], outcome)]) for outcome in ("error", "control"))
    sampled_negative = sum(
        len(cohorts[(stratum, outcome)])
        for stratum in STRATA[1:]
        for outcome in ("error", "control")
    )
    report = {
        "schema": SCHEMA,
        "scope": "frozen-v26-component-synthetic-dev-cross-split-support",
        "diagnostic_source_sha256": source_sha256,
        "authenticated_sources": authenticated_sources,
        "inputs": [
            {"path": path.as_posix(), "sha256": digest}
            for path, digest in (
                (BOUNDARY_SOURCE_PATH, BOUNDARY_SOURCE_SHA256),
                (BOUNDARY_RESULT_PATH, BOUNDARY_RESULT_SHA256),
                (DEV_REPORT_PATH, DEV_REPORT_SHA256),
                (DEV_OUTPUT_PATH, DEV_OUTPUT_SHA256),
                (V25_CACHE_REPORT_PATH, V25_CACHE_REPORT_SHA256),
                (V25_CACHE_PATH, V25_CACHE_SHA256),
                (boundary.COVERAGE_CACHE_PATH, boundary.COVERAGE_CACHE_SHA256),
                (boundary.ANNULUS_CACHE_PATH, boundary.ANNULUS_CACHE_SHA256),
            )
        ],
        "reuse": {
            "dev_scores": "authenticated fixed V26 output cache; no checkpoint inference",
            "dev_coordinates": "authenticated fixed V25 proposal cache",
            "dev_patches": "component scenes regenerated and matched to frozen per-scene patch SHA-256",
            "train_selection": "authenticated 236056-row coverage and V26 proposed-selection caches",
            "family_renderer_replay": False,
        },
        "sampling": {
            "rank_domain": RANK_DOMAIN,
            "per_error_stratum_limit": QUERY_LIMIT,
            "control_rule": "equal count within stratum; independent smallest SHA-256 ranks",
            "strata": list(STRATA),
            "populations": population_report,
            "matching_limits": "controls are matched by component scope, label, and distance stratum only",
        },
        "train_support": {
            "selected_rows": EXPECTED_TRAIN_SELECTED,
            "positive_candidates": EXPECTED_TRAIN_POSITIVES,
            "negative_candidates": EXPECTED_TRAIN_NEGATIVES,
            "selection_sha256": EXPECTED_SELECTION_SHA256,
        },
        "nearest_search": {
            "candidate_pool": "all same-label selected component-train patches",
            "approximate_index_used": False,
            "distance": "FP64 squared Euclidean over float32 3x33x33 patches",
            "normalized_rmse_denominator": 3 * 33 * 33,
            "norm_dot_candidate_block_size": candidate_block_size,
            "tiny_negative_squared_distance_clamped_to_zero": True,
            "near_tie_resolution": (
                "retain every norm/dot candidate within a conservative FP64 roundoff band; "
                "choose by direct FP64 squared-difference recomputation then train-row order"
            ),
            "positive_roundoff_band_squared_distance": float(
                search_reports["positive"]["roundoff_band_squared_distance"]
            ),
            "negative_roundoff_band_squared_distance": float(
                search_reports["negative"]["roundoff_band_squared_distance"]
            ),
            "candidate_pairs_evaluated": (
                sampled_positive * EXPECTED_TRAIN_POSITIVES
                + sampled_negative * EXPECTED_TRAIN_NEGATIVES
            ),
        },
        "support_distance": support,
        "execution_budget": {
            "elapsed_time_bound_seconds": max_elapsed_seconds,
            "estimated_runtime_seconds": [90, 240],
            "estimated_peak_memory_mib_maximum": 700,
            "estimates_not_measurements": True,
            "torch_threads": torch_threads,
        },
        "optimizer_steps": 0,
        "checkpoint_forward_passes": 0,
        "thresholds_selected": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
        "limitations": [
            "Component synthetic dev only; family panels are excluded to avoid V3 renderer replay.",
            "At most 32 error anchors and equal-size controls are measured per fixed stratum; all other anchors are excluded and counted.",
            "Controls are not matched by scene, diameter, blur radius, or truth identity.",
            "The FP64 roundoff band is conservative engineering protection, not a formal proof for every floating-point implementation.",
            "Distances are observational and do not establish causation, select a repair, or grade a gate.",
            "No patches, per-case identities, or per-case outputs are emitted.",
        ],
    }
    _require(_sha256(Path(__file__)) == source_sha256, "diagnostic source changed during run")
    output.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(report, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    with output.open("xb") as stream:
        stream.write(payload)
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--max-elapsed-seconds", type=float, default=360.0)
    parser.add_argument("--candidate-block-size", type=int, default=1024)
    parser.add_argument("--torch-threads", type=int, default=1)
    args = parser.parse_args()
    report = run(
        output=args.output,
        source_sha256=args.source_sha,
        max_elapsed_seconds=args.max_elapsed_seconds,
        candidate_block_size=args.candidate_block_size,
        torch_threads=args.torch_threads,
    )
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
