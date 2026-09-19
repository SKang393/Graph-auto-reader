# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Attribute cached V26-to-V27 component truth transitions to fixed cohorts.

Only authenticated synthetic development metadata and saved proposal/model-output
caches are replayed.  The module performs no proposal or model inference, no
optimizer step, and no threshold or candidate selection.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from hashlib import sha256
import json
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import numpy as np
import torch

from ml.markers.center.component_diversity_v27 import diagnose_head_swap as head_swap
from ml.markers.center.component_diversity_v27 import diagnose_score_localization as source
from ml.markers.center.mask_preserving_v24 import stratified_background
from ml.markers.center.plot_domain_v25 import diagnose_dev as postprocess
from ml.markers.center.real_range_generator_v1 import generator
from ml.markers.center.localization_confidence_v26 import measure_negative_coverage
from ml.markers.gate_seal import canonical_json_bytes


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_DIRECTORY = Path("artifacts/goal22-runs/marker-v27-cohort-attribution/diagnosis-v2")
REPORT_NAME = "diagnosis.json"
SCHEMA = "graphreader.marker-center-v27-cached-cohort-attribution.v1"

HEAD_SWAP_REPORT = Path("artifacts/goal22-runs/marker-v27-head-swap/diagnosis-v1/diagnosis.json")
HEAD_SWAP_REPORT_SHA256 = "613b9bd9217bcc5218e0cdfbc199ca7c73ea835da21fea5d5f1bfa690627810d"
SELECTION_REPORT = Path(
    "artifacts/goal22-runs/marker-component-selection-coverage-preflight/v3/preflight.json"
)
SELECTION_REPORT_SHA256 = "8830c983cade4b3caaff1ced2b60274c932e8af8a7a27525ca82d96e676dff43"
SELECTION_CACHE = SELECTION_REPORT.parent / "proposed-selection-metadata.npz"
SELECTION_CACHE_SHA256 = "d31a25f48170a375fae22f196344d1c8fc85d2297ddc3394c8303d08d34a246b"
GENERATOR_SOURCE = Path("ml/markers/center/real_range_generator_v1/generator.py")
GENERATOR_SOURCE_SHA256 = "2244094e8d8e3a8cacb83e11c5e7189ded46b25b1ce9ecdad1bd80e5dec78b41"
BIN_SOURCE = Path("ml/markers/center/mask_preserving_v24/stratified_background.py")
BIN_SOURCE_SHA256 = "b3d922272c83908ffdfeb3fa0f02bfa24cc83b95d3194d4a105110005e00caaa"
HEAD_SWAP_SOURCE = Path("ml/markers/center/component_diversity_v27/diagnose_head_swap.py")
HEAD_SWAP_SOURCE_SHA256 = "568f7be7ef1750a196c925abe3843f44a95b89ec32a85393c01065143936d826"
RADIUS_CATEGORY_SOURCE = Path(
    "ml/markers/center/localization_confidence_v26/measure_negative_coverage.py"
)
RADIUS_CATEGORY_SOURCE_SHA256 = "82a16abae6df6c0caf3733f07604f1add7c92a948aa6eb58b116937598156985"
HELPER_SOURCES = {
    GENERATOR_SOURCE: GENERATOR_SOURCE_SHA256,
    BIN_SOURCE: BIN_SOURCE_SHA256,
    HEAD_SWAP_SOURCE: HEAD_SWAP_SOURCE_SHA256,
    RADIUS_CATEGORY_SOURCE: RADIUS_CATEGORY_SOURCE_SHA256,
}

TRANSITIONS = (
    "false_negative_to_false_negative",
    "false_negative_to_true_positive",
    "true_positive_to_false_negative",
    "true_positive_to_true_positive",
)
EXPECTED_TRANSITIONS = {
    "false_negative_to_false_negative": 117,
    "false_negative_to_true_positive": 50,
    "true_positive_to_false_negative": 91,
    "true_positive_to_true_positive": 1746,
}
MORPHOLOGIES = (
    "point",
    "filled_circle",
    "hollow_circle",
    "filled_square",
    "hollow_square",
    "filled_circle_with_bar",
    "horizontal_cross_rectangle",
    "vertical_cross_rectangle",
)
FIXED_MODE_MORPHOLOGIES = {
    2: "filled_circle",
    3: "filled_square",
    4: "filled_circle_with_bar",
    5: "horizontal_cross_rectangle",
    6: "vertical_cross_rectangle",
}


class CohortDiagnosisError(RuntimeError):
    """An authenticated cache or cohort invariant changed."""


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise CohortDiagnosisError(message)


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _read_json(path: Path, digest: str) -> Mapping[str, Any]:
    _require(_sha(path) == digest, f"authenticated JSON changed: {path}")
    value = json.loads(path.read_bytes())
    _require(isinstance(value, Mapping), f"authenticated JSON is not an object: {path}")
    return value


def _verify_helper_sources(
    root: Path, expected: Mapping[Path, str] = HELPER_SOURCES
) -> None:
    for relative, digest in expected.items():
        _require(_sha(root / relative) == digest, f"cohort helper source changed: {relative}")


def _layout_band(x: float, y: float) -> str:
    horizontal = "left" if x < generator.WIDTH / 3 else "right" if x >= 2 * generator.WIDTH / 3 else "center"
    vertical = "upper" if y < generator.HEIGHT / 3 else "lower" if y >= 2 * generator.HEIGHT / 3 else "middle"
    return f"{horizontal}_{vertical}"


def _morphology(scene_number: int, truth_index: int, diameter: float) -> str:
    if diameter == 1.0:
        return "point"
    global_index = scene_number * generator.MARKERS_PER_SCENE + truth_index
    mode = global_index % 7
    if mode > 1:
        return FIXED_MODE_MORPHOLOGIES[mode]
    source_index = scene_number + truth_index
    if source_index % 3 == 0:
        return "filled_circle"
    if source_index % 3 == 1:
        return "hollow_circle"
    return "filled_square" if source_index % 2 else "hollow_square"


def _cohort_row(scene_number: int, truth_index: int, scene: Any) -> dict[str, Any]:
    diameter = float(scene.diameters[truth_index])
    radius = diameter / 2.0
    x, y = (float(value) for value in scene.centers[truth_index])
    return {
        "morphology": _morphology(scene_number, truth_index, diameter),
        "diameter_px": f"{diameter:g}",
        "radius_category": measure_negative_coverage.radius_category(radius),
        "layout_band": _layout_band(x, y),
        "anti_alias_radius_px": f"{generator.ANTI_ALIAS_BLUR_RADII[scene_number % len(generator.ANTI_ALIAS_BLUR_RADII)]:g}",
    }


def _changed_selection_bins(report: Mapping[str, Any]) -> dict[str, set[int]]:
    cells = report.get("allocations", {}).get("component_cells")
    _require(isinstance(cells, Mapping) and len(cells) == 50, "selection cell inventory changed")
    result: dict[str, set[int]] = defaultdict(set)
    for cell_name, raw in cells.items():
        _require(isinstance(cell_name, str) and isinstance(raw, Mapping), "selection cell is invalid")
        parts = cell_name.split("|")
        _require(len(parts) == 3, "selection cell identity changed")
        before = raw.get("selected_bin_counts_before")
        after = raw.get("selected_bin_counts_after")
        _require(isinstance(before, Mapping) and isinstance(after, Mapping), "selection bin counts are absent")
        for key in set(before) | set(after):
            _require(str(key).isdigit(), "selection bin ID is invalid")
            bin_id = int(key)
            _require(0 <= bin_id < 64, "selection bin ID is out of range")
            if int(before.get(key, 0)) != int(after.get(key, 0)):
                result[parts[2]].add(bin_id)
    return dict(result)


def _summarize(rows: Sequence[Mapping[str, Any]]) -> dict[str, Any]:
    counts = Counter(str(row["transition"]) for row in rows)
    _require(set(counts) == set(TRANSITIONS), "truth transition inventory changed")
    _require(dict(counts) == EXPECTED_TRANSITIONS, "truth transition counts changed")
    dimensions: dict[str, Any] = {}
    for dimension in (
        "morphology", "diameter_px", "radius_category", "layout_band", "anti_alias_radius_px"
    ):
        values: dict[str, Counter[str]] = defaultdict(Counter)
        for row in rows:
            values[str(row[dimension])][str(row["transition"])] += 1
        dimensions[dimension] = {
            key: {transition: int(counter.get(transition, 0)) for transition in TRANSITIONS}
            for key, counter in sorted(values.items())
        }
    overlap = {}
    for transition in TRANSITIONS:
        selected = [row for row in rows if row["transition"] == transition]
        hit = sum(bool(row["positive_bins"] & row["changed_negative_bins"]) for row in selected)
        overlap[transition] = {
            "truth_count": len(selected),
            "truths_with_changed_negative_bin_overlap": hit,
            "fraction": hit / len(selected) if selected else 0.0,
        }
    return {
        "transition_counts": {key: int(counts[key]) for key in TRANSITIONS},
        "cohorts": dimensions,
        "changed_negative_selection_overlap": overlap,
    }


def _positive_bins_by_truth(scene: Any, proposals: Any) -> list[set[int]]:
    coordinates = proposals.coordinates
    centers = torch.tensor(scene.centers, dtype=torch.float32)
    distances = torch.cdist(coordinates, centers)
    nearest_distance, nearest_truth = distances.min(dim=1)
    bin_ids = stratified_background._bin_ids(proposals.patches)
    result = [set() for _ in scene.centers]
    for row in torch.nonzero(nearest_distance <= 3.0, as_tuple=False).flatten().tolist():
        result[int(nearest_truth[row])].add(int(bin_ids[row]))
    _require(all(result), "a component truth has no positive proposal bin")
    return result


def _matched_truths(scene: Any, proposals: Any, output: np.ndarray) -> set[int]:
    trace = postprocess._postprocess_trace(scene, proposals, output, None)
    return {truth for _, truth in postprocess.greedy_match_pairs(trace.accepted, scene.centers)}


def _align_cached_proposals(extracted: Any, cached_coordinates: np.ndarray) -> Any:
    """Select extracted patches in the exact authenticated cached-coordinate order."""

    _require(
        cached_coordinates.dtype == np.float32
        and cached_coordinates.ndim == 2
        and cached_coordinates.shape[1] == 2
        and np.isfinite(cached_coordinates).all(),
        "cached proposal coordinates are invalid",
    )
    coordinates = extracted.coordinates.numpy()
    lookup = {tuple(row.tolist()): index for index, row in enumerate(coordinates)}
    _require(len(lookup) == len(coordinates), "extracted proposal coordinates are duplicated")
    try:
        indices = [lookup[tuple(row.tolist())] for row in cached_coordinates]
    except KeyError as error:
        raise CohortDiagnosisError("cached proposal coordinate is absent from reconstruction") from error
    index = torch.tensor(indices, dtype=torch.int64)
    result = postprocess.ProposalBatch(
        patches=extracted.patches[index],
        coordinates=extracted.coordinates[index],
    )
    _require(np.array_equal(result.coordinates.numpy(), cached_coordinates), "cached proposal order changed")
    return result


def run(
    expected_source_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
    torch_threads: int = 2,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    destination = (output_directory if output_directory.is_absolute() else root / output_directory).resolve()
    source_path = Path(__file__).resolve()
    _require(_sha(source_path) == expected_source_sha256, "diagnostic source changed")
    _require(1 <= torch_threads <= 12, "torch thread count is invalid")
    _require(root / "artifacts" in destination.parents and not destination.exists(), "output must be a fresh artifacts directory")
    _verify_helper_sources(root)

    head_report = _read_json(root / HEAD_SWAP_REPORT, HEAD_SWAP_REPORT_SHA256)
    selection_report = _read_json(root / SELECTION_REPORT, SELECTION_REPORT_SHA256)
    _require(_sha(root / SELECTION_CACHE) == SELECTION_CACHE_SHA256, "selection cache changed")
    _require(
        head_report.get("cache_only_model_outputs") is True
        and head_report.get("model_inference") is False
        and head_report.get("optimizer_steps") == 0
        and head_report.get("private_reads") == 0
        and head_report.get("sealed_reads") == 0,
        "head-swap evidence scope changed",
    )
    _require(
        selection_report.get("status") == "model_free_preflight_only"
        and selection_report.get("policy", {}).get("dev_rows_or_scores_used") is False
        and selection_report.get("private_reads") == 0
        and selection_report.get("sealed_reads") == 0
        and selection_report.get("optimizer_steps") == 0,
        "selection evidence scope changed",
    )
    changed_bins = _changed_selection_bins(selection_report)

    paths, v25_report, v26_report, authenticated_hashes = source._authenticate(
        root, head_swap.SOURCE_DIAGNOSTIC_SHA256
    )
    v25_arrays, v26_arrays = source._load_caches(paths, v25_report, v26_report)
    v27_report, v27_arrays = head_swap._load_v27_cache(root)
    scenes = generator.build_split("dev", independent_layout=True)
    _require(len(scenes) == 167, "component dev scene count changed")
    torch.set_num_threads(torch_threads)
    rows: list[dict[str, Any]] = []
    replay_manifest = head_report.get("component_dev", {}).get("replay_manifest")
    _require(isinstance(replay_manifest, list) and len(replay_manifest) == len(scenes), "head-swap replay manifest changed")
    for scene_number, scene in enumerate(scenes):
        prior = v26_report["component_dev"]["cache_manifest"][scene_number]
        current = v27_report["component_dev"]["cache_manifest"][scene_number]
        old = v25_report["component_dev"]["cache_manifest"][scene_number]
        replay = replay_manifest[scene_number]
        extracted = postprocess.v24.extract_proposals(scene.tensor)
        cached_coordinates = v25_arrays[str(old["proposal_coordinates_key"])]
        proposals = _align_cached_proposals(extracted, cached_coordinates)
        coordinates = proposals.coordinates.numpy().astype(np.float32, copy=False)
        v26_output = v26_arrays[str(prior["v26_candidate_predictions_key"])]
        v27_output = v27_arrays[str(current["v27_candidate_predictions_key"])]
        identity = f"dev:{scene.family}:{scene.seed}"
        _require(
            replay.get("scene_identity") == old.get("scene_identity") == prior.get("scene_identity") == identity
            and np.array_equal(coordinates, cached_coordinates)
            and source.cache_helper._array_sha(coordinates) == replay.get("proposal_coordinates_sha256")
            and source.cache_helper._array_sha(v26_output) == replay.get("v26_candidate_predictions_sha256")
            and source.cache_helper._array_sha(v27_output) == replay.get("v27_candidate_predictions_sha256"),
            f"component cache identity changed at scene {scene_number}",
        )
        v26_hits = _matched_truths(scene, proposals, v26_output)
        v27_hits = _matched_truths(scene, proposals, v27_output)
        positive_bins = _positive_bins_by_truth(scene, proposals)
        for truth_index in range(len(scene.centers)):
            row = _cohort_row(scene_number, truth_index, scene)
            row["transition"] = head_swap._transition_label(v26_hits, v27_hits, truth_index)
            row["positive_bins"] = positive_bins[truth_index]
            row["changed_negative_bins"] = changed_bins.get(row["radius_category"], set())
            rows.append(row)

    summary = _summarize(rows)
    source._verify_inputs_unchanged(root, paths, authenticated_hashes)
    _verify_helper_sources(root)
    _require(
        _sha(source_path) == expected_source_sha256
        and _sha(root / HEAD_SWAP_REPORT) == HEAD_SWAP_REPORT_SHA256
        and _sha(root / SELECTION_REPORT) == SELECTION_REPORT_SHA256
        and _sha(root / SELECTION_CACHE) == SELECTION_CACHE_SHA256,
        "diagnostic input changed during execution",
    )
    report = {
        "schema": SCHEMA,
        "scope": "fixed-v26-v27-synthetic-component-dev-cache-cohorts",
        "inputs": [
            {"path": path.as_posix(), "sha256": digest}
            for path, digest in (
                (HEAD_SWAP_REPORT, HEAD_SWAP_REPORT_SHA256),
                (source.V25_REPORT, source.V25_REPORT_SHA256),
                (source.V25_CACHE, source.V25_CACHE_SHA256),
                (source.V26_REPORT, source.V26_REPORT_SHA256),
                (source.V26_CACHE, source.V26_CACHE_SHA256),
                (head_swap.V27_REPORT, head_swap.V27_REPORT_SHA256),
                (head_swap.V27_CACHE, head_swap.V27_CACHE_SHA256),
                (SELECTION_REPORT, SELECTION_REPORT_SHA256),
                (SELECTION_CACHE, SELECTION_CACHE_SHA256),
                (GENERATOR_SOURCE, GENERATOR_SOURCE_SHA256),
                (BIN_SOURCE, BIN_SOURCE_SHA256),
                (HEAD_SWAP_SOURCE, HEAD_SWAP_SOURCE_SHA256),
                (RADIUS_CATEGORY_SOURCE, RADIUS_CATEGORY_SOURCE_SHA256),
            )
        ],
        "component_dev": summary,
        "selection_overlap_definition": (
            "A truth overlaps changed negative selection when at least one frozen positive-proposal "
            "64-bin patch signature appears in a V27 allocation cell whose selected count changed "
            "for the truth's fixed radius category. This is descriptive overlap, not causality."
        ),
        "truth_count": len(rows),
        "proposal_inference": False,
        "model_inference": False,
        "optimizer_steps": 0,
        "thresholds_selected": 0,
        "new_revision": False,
        "synthetic_only": True,
        "private_reads": 0,
        "sealed_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "torch_threads": torch_threads,
        "elapsed_seconds": time.perf_counter() - started,
    }
    destination.mkdir(parents=True, exist_ok=False)
    (destination / REPORT_NAME).write_bytes(canonical_json_bytes(report))
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-sha256", required=True)
    parser.add_argument("--torch-threads", type=int, default=2)
    parser.add_argument("--output-directory", type=Path, default=OUTPUT_DIRECTORY)
    args = parser.parse_args()
    report = run(
        args.source_sha256,
        torch_threads=args.torch_threads,
        output_directory=args.output_directory,
    )
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
