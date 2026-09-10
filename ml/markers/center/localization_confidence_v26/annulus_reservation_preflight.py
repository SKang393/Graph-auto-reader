# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cache-only preflight for fixed-band negative-anchor reservations."""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import time
from typing import Mapping, Sequence

import numpy as np

from ml.markers.gate_seal import canonical_json_bytes, sha256_file


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
COVERAGE_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1"
)
COVERAGE_REPORT_NAME = "coverage.json"
COVERAGE_REPORT_SHA256 = (
    "921e6020831055cde224729954a863c23b6804bee232a9431c773bce660ed1d5"
)
COVERAGE_CACHE_NAME = "frozen-v25-train-proposal-metadata.npz"
COVERAGE_CACHE_SHA256 = (
    "5b8afc209e1c2a6f849ef044056b46903c79692d7264a724651a3a158369576b"
)
OUTPUT_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/annulus-reservation-v1"
)
REPORT_NAME = "preflight.json"
CACHE_NAME = "proposed-selection-metadata.npz"
SCHEMA = "graphreader.marker-center-annulus-reservation-preflight.v1"
REMOVAL_RANK_DOMAIN = "graphreader.marker-center-annulus-reservation-removal.v1"
BANDS = (
    (3.0, 5.0, "negative_gt3_le5"),
    (5.0, 8.0, "negative_gt5_le8"),
    (8.0, 12.0, "negative_gt8_le12"),
)


@dataclass(frozen=True)
class ReservationSelection:
    selected: np.ndarray
    reserved: np.ndarray
    added: np.ndarray
    displaced: np.ndarray
    fallback_additions: tuple[int, ...]
    replacement_tiers: tuple[str, ...]


def annulus_band(distance: float) -> str | None:
    if not np.isfinite(distance):
        return None
    for lower, upper, name in BANDS:
        if lower < distance <= upper:
            return name
    return None


def choose_reservations(
    scene_indices: np.ndarray,
    proposal_indices: np.ndarray,
    coordinates: np.ndarray,
    nearest_truth_indices: np.ndarray,
    distances: np.ndarray,
) -> np.ndarray:
    """Choose one closest cached negative per scene/truth/fixed band."""

    count = _validate_row_arrays(
        scene_indices,
        proposal_indices,
        nearest_truth_indices,
        distances,
    )
    if coordinates.shape != (count, 2) or coordinates.dtype != np.float32:
        raise ValueError("coordinates must be float32 [N,2]")
    chosen: dict[tuple[int, int, str], tuple[tuple[float, float, float, int], int]] = {}
    for row in range(count):
        truth_index = int(nearest_truth_indices[row])
        band = annulus_band(float(distances[row]))
        if truth_index < 0 or band is None:
            continue
        key = (int(scene_indices[row]), truth_index, band)
        rank = (
            float(distances[row]),
            float(coordinates[row, 0]),
            float(coordinates[row, 1]),
            int(proposal_indices[row]),
        )
        prior = chosen.get(key)
        if prior is None or rank < prior[0]:
            chosen[key] = (rank, row)
    result = np.zeros(count, dtype=np.bool_)
    for _, row in chosen.values():
        result[row] = True
    return result


def apply_reservations(
    scope: str,
    selected: np.ndarray,
    positive: np.ndarray,
    protected: np.ndarray,
    reserved: np.ndarray,
    stratum_ids: np.ndarray,
    scene_indices: np.ndarray,
    proposal_indices: np.ndarray,
) -> ReservationSelection:
    """Add reservations and deterministically replace unprotected negatives."""

    count = _validate_row_arrays(
        selected,
        positive,
        protected,
        reserved,
        stratum_ids,
        scene_indices,
        proposal_indices,
    )
    for name, values in (
        ("selected", selected),
        ("positive", positive),
        ("protected", protected),
        ("reserved", reserved),
    ):
        if values.dtype != np.bool_:
            raise ValueError(f"{name} must be a boolean array")
    if np.any(protected & ~selected):
        raise ValueError("protected rows must belong to the frozen selection")
    if np.any(positive & ~selected):
        raise ValueError("all frozen positive rows must remain selected")
    if not scope or not scope.isascii():
        raise ValueError("replacement scope must be non-empty ASCII")

    proposed = selected.copy()
    added = reserved & ~selected
    displaced = np.zeros(count, dtype=np.bool_)
    available = set(np.flatnonzero(selected & ~positive & ~protected & ~reserved).tolist())
    by_scene_stratum: dict[tuple[int, int], list[int]] = {}
    by_stratum: dict[int, list[int]] = {}
    for row in available:
        by_scene_stratum.setdefault(
            (int(scene_indices[row]), int(stratum_ids[row])), []
        ).append(row)
        by_stratum.setdefault(int(stratum_ids[row]), []).append(row)
    rank = lambda row: _removal_rank(
        scope, int(scene_indices[row]), int(proposal_indices[row])
    )
    for rows in (*by_scene_stratum.values(), *by_stratum.values()):
        rows.sort(key=rank)
    fallback_order = sorted(
        available,
        key=rank,
    )
    fallback_additions: list[int] = []
    replacement_tiers: list[str] = []
    additions = sorted(
        np.flatnonzero(added).tolist(),
        key=rank,
    )
    for added_row in additions:
        same_scene = by_scene_stratum.get(
            (int(scene_indices[added_row]), int(stratum_ids[added_row])), []
        )
        while same_scene and same_scene[0] not in available:
            same_scene.pop(0)
        if same_scene:
            removed_row = same_scene.pop(0)
            replacement_tiers.append("same_scene_and_stratum")
        else:
            same_stratum = by_stratum.get(int(stratum_ids[added_row]), [])
            while same_stratum and same_stratum[0] not in available:
                same_stratum.pop(0)
            if same_stratum:
                removed_row = same_stratum.pop(0)
                replacement_tiers.append("same_stratum_global")
            else:
                while fallback_order and fallback_order[0] not in available:
                    fallback_order.pop(0)
                if not fallback_order:
                    raise ValueError("no unprotected selected negative remains for replacement")
                removed_row = fallback_order.pop(0)
                fallback_additions.append(added_row)
                replacement_tiers.append("cross_stratum_global_fallback")
        available.remove(removed_row)
        displaced[removed_row] = True
        proposed[removed_row] = False
        proposed[added_row] = True

    if int(proposed.sum()) != int(selected.sum()):
        raise RuntimeError("annulus reservation changed the selected-row budget")
    if np.any(selected & (positive | protected) & ~proposed):
        raise RuntimeError("annulus reservation displaced a preserved row")
    if np.any(reserved & ~proposed):
        raise RuntimeError("annulus reservation was not retained")
    return ReservationSelection(
        proposed,
        reserved.copy(),
        added,
        displaced,
        tuple(fallback_additions),
        tuple(replacement_tiers),
    )


def _removal_rank(scope: str, scene_index: int, proposal_index: int) -> str:
    material = (
        f"{REMOVAL_RANK_DOMAIN}\n{scope}\n{scene_index}:{proposal_index}\n"
    ).encode("ascii")
    return hashlib.sha256(material).hexdigest()


def _validate_row_arrays(*arrays: np.ndarray) -> int:
    if not arrays or any(value.ndim != 1 for value in arrays):
        raise ValueError("proposal metadata arrays must be one-dimensional")
    count = len(arrays[0])
    if any(len(value) != count for value in arrays[1:]):
        raise ValueError("proposal metadata arrays must have equal lengths")
    return count


def _selection_sha256(
    selected: np.ndarray, scene_indices: np.ndarray, proposal_indices: np.ndarray
) -> str:
    digest = hashlib.sha256()
    for row in np.flatnonzero(selected):
        digest.update(
            f"{int(scene_indices[row])}:{int(proposal_indices[row])}\n".encode("ascii")
        )
    return digest.hexdigest()


def _identity_sha256(
    mask: np.ndarray, scene_indices: np.ndarray, proposal_indices: np.ndarray
) -> str:
    return _selection_sha256(mask, scene_indices, proposal_indices)


def _counts_by_ids(mask: np.ndarray, ids: np.ndarray, names: Mapping[int, str]) -> dict[str, int]:
    counts = Counter(int(value) for value in ids[mask].tolist())
    return {names[index]: counts[index] for index in sorted(counts)}


def _split_preflight(
    split: str,
    arrays: Mapping[str, np.ndarray],
    report: Mapping[str, object],
    truth_denominator: int,
    expected_positive: int,
    expected_negative: int,
    stratum_names: Mapping[int, str],
    role_names: Mapping[int, str],
) -> tuple[dict[str, object], dict[str, np.ndarray]]:
    prefix = f"{split}_"
    scene = arrays[prefix + "scene_index"]
    proposal = arrays[prefix + "proposal_index"]
    coordinates = arrays[prefix + "coordinates"]
    truth = arrays[prefix + "nearest_truth_index"]
    distances = arrays[prefix + "nearest_truth_distance_px"]
    selected = arrays[prefix + "selected"]
    strata = arrays[prefix + "stratum_id"]
    roles = arrays[prefix + "retention_role_id"]
    positive = distances <= np.float32(3.0)
    protected_role_names = (
        {"topology_reserved", "connector_anchor_reserved", "sparse_fragment_reserved"}
        if split == "component"
        else {"hard_negative_retained"}
    )
    protected_role_ids = {
        index for index, name in role_names.items() if name in protected_role_names
    }
    protected = selected & np.isin(roles, tuple(sorted(protected_role_ids)))
    if split == "component":
        hard_ids = {index for index, name in stratum_names.items() if name == "hard_existing"}
        protected |= selected & np.isin(strata, tuple(sorted(hard_ids)))

    if int((selected & positive).sum()) != expected_positive:
        raise RuntimeError(f"{split} frozen positive selection changed")
    if int((selected & ~positive).sum()) != expected_negative:
        raise RuntimeError(f"{split} frozen negative selection changed")
    reserved = choose_reservations(scene, proposal, coordinates, truth, distances)
    outcome = apply_reservations(
        split,
        selected,
        positive,
        protected,
        reserved,
        strata,
        scene,
        proposal,
    )

    band_rows: dict[str, dict[str, int]] = {}
    for _, _, name in BANDS:
        in_band = np.asarray([annulus_band(float(value)) == name for value in distances])
        band_reserved = reserved & in_band
        band_rows[name] = {
            "eligible_negative_rows": int(in_band.sum()),
            "reserved_truth_band_rows": int(band_reserved.sum()),
            "already_selected_reserved_rows": int((band_reserved & selected).sum()),
            "added_reserved_rows": int((band_reserved & ~selected).sum()),
            "selected_before": int((in_band & selected).sum()),
            "selected_after": int((in_band & outcome.selected).sum()),
        }
    represented_truths = {
        (int(scene[row]), int(truth[row])) for row in np.flatnonzero(reserved)
    }
    old_strata = _counts_by_ids(selected & ~positive, strata, stratum_names)
    new_strata = _counts_by_ids(outcome.selected & ~positive, strata, stratum_names)
    all_names = sorted(set(old_strata) | set(new_strata))
    stratum_delta = {
        name: new_strata.get(name, 0) - old_strata.get(name, 0)
        for name in all_names
        if new_strata.get(name, 0) != old_strata.get(name, 0)
    }
    old_scenes = Counter(int(value) for value in scene[selected & ~positive].tolist())
    new_scenes = Counter(
        int(value) for value in scene[outcome.selected & ~positive].tolist()
    )
    scene_delta = {
        str(index): new_scenes[index] - old_scenes[index]
        for index in sorted(set(old_scenes) | set(new_scenes))
        if new_scenes[index] != old_scenes[index]
    }
    split_report = {
        "truth_denominator": truth_denominator,
        "truths_with_at_least_one_annulus_reservation": len(represented_truths),
        "truths_without_annulus_reservation": truth_denominator - len(represented_truths),
        "proposal_row_count": len(selected),
        "positive_row_count": int(positive.sum()),
        "selected_positive_row_count": int((selected & positive).sum()),
        "selected_negative_count_before": int((selected & ~positive).sum()),
        "selected_negative_count_after": int((outcome.selected & ~positive).sum()),
        "protected_selected_negative_count": int(protected.sum()),
        "reserved_row_count": int(reserved.sum()),
        "already_selected_reserved_row_count": int((reserved & selected).sum()),
        "added_reserved_row_count": int(outcome.added.sum()),
        "displaced_row_count": int(outcome.displaced.sum()),
        "fallback_replacement_count": len(outcome.fallback_additions),
        "replacement_tier_counts": dict(sorted(Counter(outcome.replacement_tiers).items())),
        "bands": band_rows,
        "old_selected_negative_strata": old_strata,
        "new_selected_negative_strata": new_strata,
        "stratum_count_delta": stratum_delta,
        "scene_selected_negative_count_delta": scene_delta,
        "frozen_selection_sha256": _selection_sha256(selected, scene, proposal),
        "proposed_selection_sha256": _selection_sha256(outcome.selected, scene, proposal),
        "reservation_identity_sha256": _identity_sha256(reserved, scene, proposal),
        "displacement_identity_sha256": _identity_sha256(outcome.displaced, scene, proposal),
    }
    cache = {
        f"{split}_proposed_selected": outcome.selected,
        f"{split}_reserved": outcome.reserved,
        f"{split}_added": outcome.added,
        f"{split}_displaced": outcome.displaced,
    }
    return split_report, cache


def run(
    *,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
) -> dict[str, object]:
    started = time.perf_counter()
    root = repository_root.resolve()
    source_directory = root / COVERAGE_DIRECTORY
    coverage_report_path = source_directory / COVERAGE_REPORT_NAME
    coverage_cache_path = source_directory / COVERAGE_CACHE_NAME
    if sha256_file(coverage_report_path) != COVERAGE_REPORT_SHA256:
        raise RuntimeError("frozen negative-coverage report bytes changed")
    if sha256_file(coverage_cache_path) != COVERAGE_CACHE_SHA256:
        raise RuntimeError("frozen negative-coverage cache bytes changed")
    coverage = json.loads(coverage_report_path.read_text(encoding="utf-8"))
    if (
        coverage.get("status") != "model_free_complete"
        or coverage.get("model_inference_runs") != 0
        or coverage.get("optimizer_steps_run") != 0
        or coverage.get("private_reads") != 0
        or coverage.get("sealed_runs") != 0
    ):
        raise RuntimeError("frozen negative-coverage evidence is not eligible")
    destination = output_directory if output_directory.is_absolute() else root / output_directory
    if destination.exists():
        raise FileExistsError(f"annulus preflight output already exists: {destination}")

    with np.load(coverage_cache_path, allow_pickle=False) as loaded:
        arrays = {name: loaded[name] for name in loaded.files}
    stratum_names = {
        int(index): name for name, index in coverage["cache"]["stratum_ids"].items()
    }
    role_names = {
        int(index): name
        for name, index in coverage["cache"]["retention_role_ids"].items()
    }
    component, component_cache = _split_preflight(
        "component", arrays, coverage, 2004, 3258, 32580, stratum_names, role_names
    )
    family, family_cache = _split_preflight(
        "family", arrays, coverage, 500, 823, 8230, stratum_names, role_names
    )
    if component["selected_negative_count_after"] != 32580:
        raise RuntimeError("component negative budget changed")
    if family["selected_negative_count_after"] != 8230:
        raise RuntimeError("family negative budget changed")

    destination.mkdir(parents=True, exist_ok=False)
    cache_path = destination / CACHE_NAME
    np.savez_compressed(cache_path, **component_cache, **family_cache)
    report: dict[str, object] = {
        "schema": SCHEMA,
        "status": "model_free_preflight_only",
        "scope": "fixed-v25-synthetic-train-annulus-reservation-design",
        "inputs": {
            "coverage_report_path": (COVERAGE_DIRECTORY / COVERAGE_REPORT_NAME).as_posix(),
            "coverage_report_sha256": COVERAGE_REPORT_SHA256,
            "coverage_cache_path": (COVERAGE_DIRECTORY / COVERAGE_CACHE_NAME).as_posix(),
            "coverage_cache_sha256": COVERAGE_CACHE_SHA256,
            "v25_config_sha256": coverage["inputs"]["v25_config_sha256"],
            "v25_result_sha256": coverage["inputs"]["v25_result_sha256"],
            "v25_component_selected_index_sha256": coverage["inputs"]["component_selected_index_sha256"],
            "v25_family_selected_index_sha256": coverage["inputs"]["family_selected_index_sha256"],
            "v25_selected_row_identity_sha256": coverage["inputs"]["selected_row_identity_sha256"],
            "v25_selected_tensor_inventory_sha256": coverage["inputs"]["selected_tensor_inventory_sha256"],
            "source_sha256": sha256_file(Path(__file__)),
        },
        "algorithm": {
            "bands": [
                {"lower_exclusive": lower, "upper_inclusive": upper, "name": name}
                for lower, upper, name in BANDS
            ],
            "reservation": "one closest negative per cached nearest-truth assignment and band",
            "tie_order": ["nearest_truth_distance_px", "x", "y", "proposal_index"],
            "replacement": "same original stratum first; deterministic cross-stratum fallback only if exhausted",
            "replacement_preference": [
                "same_scene_and_original_stratum",
                "same_original_stratum_global",
                "cross_stratum_global_fallback",
            ],
            "replacement_rank_domain": REMOVAL_RANK_DOMAIN,
            "replacement_rank": "ascending_sha256(domain_lf_scope_lf_scene_colon_proposal_lf)",
        },
        "component_train": component,
        "family_train": family,
        "combined": {
            "truth_denominator": 2504,
            "truths_with_at_least_one_annulus_reservation": component["truths_with_at_least_one_annulus_reservation"] + family["truths_with_at_least_one_annulus_reservation"],
            "reserved_row_count": component["reserved_row_count"] + family["reserved_row_count"],
            "added_reserved_row_count": component["added_reserved_row_count"] + family["added_reserved_row_count"],
            "displaced_row_count": component["displaced_row_count"] + family["displaced_row_count"],
        },
        "cache": {
            "path": CACHE_NAME,
            "sha256": sha256_file(cache_path),
            "arrays": {
                name: {"dtype": str(value.dtype), "shape": list(value.shape)}
                for name, value in sorted({**component_cache, **family_cache}.items())
            },
        },
        "interpretation_limits": [
            "This train-only preflight prepares a testable sampler hypothesis; it does not establish causality or choose a candidate.",
            "The proposal uses cached nearest-truth assignments only and does not add anchors outside the frozen runtime proposal domain.",
            "Stratum effects are descriptive and are not an acceptance gate.",
        ],
        "dev_truth_used_for_selection": False,
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
        "component_added": report["component_train"]["added_reserved_row_count"],
        "family_added": report["family_train"]["added_reserved_row_count"],
        "output": str(OUTPUT_DIRECTORY / REPORT_NAME),
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
