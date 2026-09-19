# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replay fixed V26/V27 dev caches with confidence and geometry heads swapped.

This diagnostic authenticates the V25 proposal coordinates, V26 outputs, V27
outputs, their reports, and the fixed V27 synthetic development metadata. It
replays the registered 0.25 postprocessor for both originals and two mechanical
head swaps. It performs no model or proposal inference, optimizer step,
threshold selection, private read, or sealed read.
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

from ml.markers.center.component_diversity_v27 import (
    diagnose_score_localization as source_diagnostic,
)
from ml.markers.center.line_aware_v1.pipeline import ProposalBatch
from ml.markers.center.localization_confidence_v26 import (
    diagnose_suppressor_anchors as cache_helper,
)
from ml.markers.center.plot_domain_v25 import diagnose_dev as postprocess
from ml.markers.gate_seal import canonical_json_bytes


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v27-head-swap/diagnosis-v1"
)
REPORT_NAME = "diagnosis.json"
SCHEMA = "graphreader.marker-center-v27-cached-head-swap-attribution.v1"
THRESHOLD = 0.25
POSITIVE_PROPOSAL_RADIUS_PX = 3.0

SOURCE_DIAGNOSTIC_SHA256 = (
    "3dda4482fdd5df1067718615a72e4acc02ed964d00031af2c59a3f37211b6061"
)
V27_REPORT = Path(
    "artifacts/goal22-runs/marker-v27-score-localization/diagnosis-v1/diagnosis.json"
)
V27_REPORT_SHA256 = (
    "732c8b9f058c314e5927823a4fe0a45502452c755b12667aaf6566605d166dfc"
)
V27_CACHE = V27_REPORT.parent / "fixed-v27-dev-outputs.npz"
V27_CACHE_SHA256 = (
    "ea2089262c1b2d0b57a4b8eeb7e576956af462b2d67166e3e3fd05f82e87493a"
)

MODEL_IDENTITIES = (
    "v26",
    "v27",
    "v27_confidence_v26_geometry",
    "v26_confidence_v27_geometry",
)
PAIRED_COMPARISONS = (
    ("v26", "v27"),
    ("v26", "v27_confidence_v26_geometry"),
    ("v26", "v26_confidence_v27_geometry"),
    ("v27", "v27_confidence_v26_geometry"),
    ("v27", "v26_confidence_v27_geometry"),
)
EXPECTED = source_diagnostic.EXPECTED


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def _json(path: Path) -> Mapping[str, Any]:
    value = json.loads(path.read_bytes())
    _require(isinstance(value, Mapping), f"expected JSON object: {path}")
    return value


def _quantiles(values: Sequence[float]) -> dict[str, float | int]:
    array = np.asarray(values, dtype=np.float64)
    if not len(array):
        return {"count": 0}
    points = np.quantile(array, (0.0, 0.05, 0.5, 0.95, 1.0))
    return {
        "count": int(len(array)),
        **dict(zip(
            ("minimum", "p05", "median", "p95", "maximum"),
            (float(value) for value in points),
            strict=True,
        )),
    }


def _hybrid_outputs(
    v26_output: np.ndarray,
    v27_output: np.ndarray,
) -> dict[str, np.ndarray]:
    _require(
        v26_output.dtype == v27_output.dtype == np.float32
        and v26_output.ndim == v27_output.ndim == 2
        and v26_output.shape == v27_output.shape
        and v26_output.shape[1] == 4
        and np.isfinite(v26_output).all()
        and np.isfinite(v27_output).all(),
        "cached output arrays are invalid",
    )
    v27_confidence_v26_geometry = np.array(v26_output, copy=True)
    v27_confidence_v26_geometry[:, 0] = v27_output[:, 0]
    v26_confidence_v27_geometry = np.array(v27_output, copy=True)
    v26_confidence_v27_geometry[:, 0] = v26_output[:, 0]
    return {
        "v26": v26_output,
        "v27": v27_output,
        "v27_confidence_v26_geometry": v27_confidence_v26_geometry,
        "v26_confidence_v27_geometry": v26_confidence_v27_geometry,
    }


def _paired_transitions(
    left_true_positives: set[int],
    right_true_positives: set[int],
    truth_count: int,
) -> dict[str, int]:
    _require(
        truth_count >= 0
        and all(0 <= value < truth_count for value in left_true_positives)
        and all(0 <= value < truth_count for value in right_true_positives),
        "paired truth outcome index is invalid",
    )
    all_truths = set(range(truth_count))
    values = {
        "true_positive_to_true_positive": len(
            left_true_positives & right_true_positives
        ),
        "true_positive_to_false_negative": len(
            left_true_positives - right_true_positives
        ),
        "false_negative_to_true_positive": len(
            right_true_positives - left_true_positives
        ),
        "false_negative_to_false_negative": len(
            all_truths - left_true_positives - right_true_positives
        ),
    }
    _require(sum(values.values()) == truth_count, "paired truth outcomes do not conserve")
    return values


def _transition_label(
    left_true_positives: set[int],
    right_true_positives: set[int],
    truth_index: int,
) -> str:
    left = "true_positive" if truth_index in left_true_positives else "false_negative"
    right = "true_positive" if truth_index in right_true_positives else "false_negative"
    return f"{left}_to_{right}"


def _max_positive_margins(
    coordinates: np.ndarray,
    centers: np.ndarray,
    output: np.ndarray,
) -> np.ndarray:
    """Return one max positive-anchor score margin per truth, NaN if unsupported."""

    _require(
        coordinates.dtype == centers.dtype == output.dtype == np.float32
        and coordinates.ndim == centers.ndim == output.ndim == 2
        and coordinates.shape[1] == centers.shape[1] == 2
        and output.shape == (len(coordinates), 4)
        and len(centers) > 0
        and np.isfinite(coordinates).all()
        and np.isfinite(centers).all()
        and np.isfinite(output).all(),
        "margin input arrays are invalid",
    )
    distance = np.linalg.norm(
        coordinates[:, None, :] - centers[None, :, :], axis=2
    )
    assignment = distance.argmin(axis=1)
    positive = distance.min(axis=1) <= POSITIVE_PROPOSAL_RADIUS_PX
    margins = np.full(len(centers), np.nan, dtype=np.float64)
    for truth_index in range(len(centers)):
        owned = positive & (assignment == truth_index)
        if owned.any():
            margins[truth_index] = (
                float(np.max(output[owned, 0])) - THRESHOLD
            )
    return margins


def _margin_report(values: Sequence[float]) -> dict[str, Any]:
    array = np.asarray(values, dtype=np.float64)
    finite = array[np.isfinite(array)]
    return {
        "truth_count": int(len(array)),
        "truths_without_positive_anchor": int(np.count_nonzero(~np.isfinite(array))),
        "truths_at_or_above_threshold": int(np.count_nonzero(finite >= 0.0)),
        "truths_below_threshold": int(np.count_nonzero(finite < 0.0)),
        "max_positive_confidence_minus_0_25": _quantiles(finite.tolist()),
    }


def _load_v27_cache(
    root: Path,
) -> tuple[Mapping[str, Any], dict[str, np.ndarray]]:
    report_path = root / V27_REPORT
    cache_path = root / V27_CACHE
    _require(_sha(report_path) == V27_REPORT_SHA256, "V27 diagnostic report changed")
    _require(_sha(cache_path) == V27_CACHE_SHA256, "V27 output cache changed")
    report = _json(report_path)
    cache = report.get("cache")
    _require(
        report.get("schema")
        == "graphreader.marker-center-v27-fixed-dev-attribution.v1"
        and report.get("operating_threshold") == THRESHOLD
        and report.get("synthetic_only") is True
        and report.get("model_inference") is True
        and report.get("scene_inference_count") == 176
        and report.get("optimizer_steps") == 0
        and report.get("thresholds_selected") == 0
        and report.get("private_reads") == 0
        and report.get("sealed_reads") == 0
        and report.get("sealed_runs") == 0
        and isinstance(cache, Mapping)
        and cache.get("sha256") == V27_CACHE_SHA256
        and cache.get("array_count") == 176,
        "V27 diagnostic ancestry changed",
    )
    with np.load(cache_path, allow_pickle=False) as archive:
        arrays = {key: np.array(archive[key], copy=True) for key in archive.files}
    expected_keys: set[str] = set()
    for name in EXPECTED:
        section = report.get(f"{name}_dev")
        _require(isinstance(section, Mapping), f"V27 report omitted {name}")
        manifest = section.get("cache_manifest")
        _require(
            isinstance(manifest, list)
            and len(manifest) == EXPECTED[name]["scene_count"],
            f"V27 {name} cache manifest changed",
        )
        for row in manifest:
            _require(isinstance(row, Mapping), f"V27 {name} manifest row changed")
            key = row.get("v27_candidate_predictions_key")
            _require(
                isinstance(key, str)
                and key in arrays
                and key not in expected_keys,
                f"V27 {name} cache key is missing or duplicated",
            )
            expected_keys.add(key)
            value = arrays[key]
            _require(
                value.dtype == np.float32
                and value.ndim == 2
                and value.shape[1] == 4
                and np.isfinite(value).all()
                and cache_helper._array_sha(value)
                == row.get("v27_candidate_predictions_sha256"),
                f"V27 {name} cached array identity changed",
            )
    _require(
        set(arrays) == expected_keys and len(arrays) == 176,
        "V27 cache contains an unexpected array inventory",
    )
    return report, arrays


def _cached_proposals(coordinates: np.ndarray) -> ProposalBatch:
    _require(
        coordinates.dtype == np.float32
        and coordinates.ndim == 2
        and coordinates.shape[1] == 2
        and np.isfinite(coordinates).all(),
        "cached proposal coordinates are invalid",
    )
    return ProposalBatch(
        torch.empty((len(coordinates), 0), dtype=torch.float32),
        torch.from_numpy(coordinates),
    )


def _evaluate_split(
    name: str,
    bound_scenes: Sequence[Any],
    v25_arrays: Mapping[str, np.ndarray],
    v26_arrays: Mapping[str, np.ndarray],
    v27_arrays: Mapping[str, np.ndarray],
    v25_manifest: Sequence[Mapping[str, Any]],
    v26_manifest: Sequence[Mapping[str, Any]],
    v27_manifest: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    totals = {identity: Counter() for identity in MODEL_IDENTITIES}
    radius_nms = {
        identity: postprocess._new_radius_nms_accumulator()
        for identity in MODEL_IDENTITIES
    }
    paired_totals = {
        f"{left}_to_{right}": Counter()
        for left, right in PAIRED_COMPARISONS
    }
    margins = {identity: [] for identity in MODEL_IDENTITIES}
    margins_by_original_transition = {
        identity: defaultdict(list) for identity in MODEL_IDENTITIES
    }
    replay_manifest: list[dict[str, Any]] = []

    expected_scenes = EXPECTED[name]["scene_count"]
    _require(
        len(bound_scenes)
        == len(v25_manifest)
        == len(v26_manifest)
        == len(v27_manifest)
        == expected_scenes,
        f"{name} scene manifest length changed",
    )
    for scene_number, (bound, old_row, prior_row, current_row) in enumerate(
        zip(
            bound_scenes,
            v25_manifest,
            v26_manifest,
            v27_manifest,
            strict=True,
        )
    ):
        scene = bound.scene if hasattr(bound, "scene") else bound
        domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
        identity = source_diagnostic._scene_identity(scene)
        coordinate_key = old_row.get("proposal_coordinates_key")
        v26_key = prior_row.get("v26_candidate_predictions_key")
        v27_key = current_row.get("v27_candidate_predictions_key")
        _require(
            isinstance(coordinate_key, str)
            and isinstance(v26_key, str)
            and isinstance(v27_key, str),
            f"{name} cache manifest key changed: {identity}",
        )
        coordinates = v25_arrays[coordinate_key]
        v26_output = v26_arrays[v26_key]
        v27_output = v27_arrays[v27_key]
        _require(
            old_row.get("scene_identity")
            == prior_row.get("scene_identity")
            == current_row.get("scene_identity")
            == identity
            and old_row.get("proposal_count")
            == prior_row.get("proposal_count")
            == current_row.get("proposal_count")
            == len(coordinates)
            and old_row.get("proposal_coordinates_sha256")
            == prior_row.get("proposal_coordinates_sha256")
            == current_row.get("proposal_coordinates_sha256")
            == cache_helper._array_sha(coordinates)
            and prior_row.get("v26_candidate_predictions_sha256")
            == current_row.get("v26_candidate_predictions_sha256")
            == cache_helper._array_sha(v26_output)
            and current_row.get("v27_candidate_predictions_sha256")
            == cache_helper._array_sha(v27_output),
            f"{name} authenticated cache join changed: {identity}",
        )
        proposals = _cached_proposals(coordinates)
        outputs = _hybrid_outputs(v26_output, v27_output)
        traces = {
            model_identity: postprocess._postprocess_trace(
                scene, proposals, output, domain
            )
            for model_identity, output in outputs.items()
        }
        matches = {
            model_identity: postprocess.greedy_match_pairs(
                trace.accepted, scene.centers
            )
            for model_identity, trace in traces.items()
        }
        true_positive_truths = {
            model_identity: {truth for _, truth in pairs}
            for model_identity, pairs in matches.items()
        }
        centers = np.asarray(scene.centers, dtype=np.float32)
        scene_margins = {
            model_identity: _max_positive_margins(
                coordinates, centers, output
            )
            for model_identity, output in outputs.items()
        }

        for model_identity in MODEL_IDENTITIES:
            trace = traces[model_identity]
            pairs = matches[model_identity]
            postprocess.summarize_radius_and_nms(
                scene, trace, pairs, radius_nms[model_identity]
            )
            totals[model_identity].update({
                "scene_count": 1,
                "truth_count": len(scene.centers),
                "proposal_count": len(coordinates),
                "true_positive": len(pairs),
                "false_positive": len(trace.accepted) - len(pairs),
                "false_negative": len(scene.centers) - len(pairs),
            })
            margins[model_identity].extend(scene_margins[model_identity].tolist())
            for truth_index, margin in enumerate(scene_margins[model_identity]):
                label = _transition_label(
                    true_positive_truths["v26"],
                    true_positive_truths["v27"],
                    truth_index,
                )
                margins_by_original_transition[model_identity][label].append(
                    float(margin)
                )

        for left, right in PAIRED_COMPARISONS:
            paired_totals[f"{left}_to_{right}"].update(
                _paired_transitions(
                    true_positive_truths[left],
                    true_positive_truths[right],
                    len(scene.centers),
                )
            )
        replay_manifest.append({
            "scene_number": scene_number,
            "scene_identity": identity,
            "truth_count": len(scene.centers),
            "proposal_count": len(coordinates),
            "proposal_coordinates_sha256": cache_helper._array_sha(coordinates),
            "v26_candidate_predictions_sha256": cache_helper._array_sha(v26_output),
            "v27_candidate_predictions_sha256": cache_helper._array_sha(v27_output),
        })

    for original in ("v26", "v27"):
        source_diagnostic._require_exact_metrics(name, original, totals[original])
    for model_identity in MODEL_IDENTITIES:
        counts = totals[model_identity]
        _require(
            counts["scene_count"] == EXPECTED[name]["scene_count"]
            and counts["truth_count"] == EXPECTED[name]["truth_count"]
            and counts["proposal_count"] == EXPECTED[name]["proposal_count"]
            and counts["true_positive"] + counts["false_negative"]
            == counts["truth_count"],
            f"{name} {model_identity} replay denominators changed",
        )
    for comparison, values in paired_totals.items():
        _require(
            sum(values.values()) == EXPECTED[name]["truth_count"],
            f"{name} {comparison} paired outcomes do not conserve",
        )
    for model_identity in MODEL_IDENTITIES:
        _require(
            len(margins[model_identity]) == EXPECTED[name]["truth_count"],
            f"{name} {model_identity} margin denominator changed",
        )

    return {
        "counts": {
            identity: dict(sorted(totals[identity].items()))
            for identity in MODEL_IDENTITIES
        },
        "original_exact_count_checks": {
            identity: "pass" for identity in ("v26", "v27")
        },
        "paired_truth_transitions": {
            comparison: dict(sorted(values.items()))
            for comparison, values in paired_totals.items()
        },
        "max_positive_confidence_margins": {
            identity: {
                "all_truths": _margin_report(margins[identity]),
                "by_original_v26_to_v27_truth_transition": {
                    transition: _margin_report(values)
                    for transition, values in sorted(
                        margins_by_original_transition[identity].items()
                    )
                },
            }
            for identity in MODEL_IDENTITIES
        },
        "actual_radius_and_nms_attribution": {
            identity: postprocess._radius_nms_report(radius_nms[identity])
            for identity in MODEL_IDENTITIES
        },
        "replay_manifest": replay_manifest,
    }


def _verify_inputs_unchanged(
    root: Path,
    paths: Mapping[str, Path],
    hashes: Mapping[str, str],
    diagnostic_source: Path,
    expected_source_sha256: str,
) -> None:
    source_diagnostic._verify_inputs_unchanged(root, paths, hashes)
    _require(
        _sha(root / V27_REPORT) == V27_REPORT_SHA256
        and _sha(root / V27_CACHE) == V27_CACHE_SHA256
        and _sha(diagnostic_source) == expected_source_sha256,
        "head-swap diagnostic input changed during execution",
    )


def run(
    expected_source_sha256: str,
    *,
    torch_threads: int = 2,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    destination = (
        output_directory if output_directory.is_absolute() else root / output_directory
    ).resolve()
    source_path = Path(__file__).resolve()
    _require(len(expected_source_sha256) == 64, "expected source SHA256 is invalid")
    _require(_sha(source_path) == expected_source_sha256, "diagnostic source changed")
    _require(1 <= torch_threads <= 12, "torch thread count must be between 1 and 12")
    _require(
        destination == (root / OUTPUT_DIRECTORY).resolve(),
        "diagnostic output path changed",
    )
    _require(not destination.exists(), "diagnostic output already exists")

    paths, v25_report, v26_report, authenticated_hashes = (
        source_diagnostic._authenticate(root, SOURCE_DIAGNOSTIC_SHA256)
    )
    v25_arrays, v26_arrays = source_diagnostic._load_caches(
        paths, v25_report, v26_report
    )
    v27_report, v27_arrays = _load_v27_cache(root)

    torch.set_num_threads(torch_threads)
    scenes = source_diagnostic._load_authenticated_dev_scenes(root)
    results = {}
    for name in EXPECTED:
        results[name] = _evaluate_split(
            name,
            scenes[name],
            v25_arrays,
            v26_arrays,
            v27_arrays,
            v25_report[f"{name}_dev"]["cache_manifest"],
            v26_report[f"{name}_dev"]["cache_manifest"],
            v27_report[f"{name}_dev"]["cache_manifest"],
        )
    _verify_inputs_unchanged(
        root,
        paths,
        authenticated_hashes,
        source_path,
        expected_source_sha256,
    )

    report = {
        "schema": SCHEMA,
        "scope": "fixed-v25-proposals-v26-v27-cached-head-swap-synthetic-dev",
        "operating_threshold": THRESHOLD,
        "models": {
            "v26": "authenticated cached V26 output",
            "v27": "authenticated cached V27 output",
            "v27_confidence_v26_geometry": (
                "V27 confidence with V26 decoded offsets and raw radius"
            ),
            "v26_confidence_v27_geometry": (
                "V26 confidence with V27 decoded offsets and raw radius"
            ),
        },
        "inputs": {
            "v25_report_path": source_diagnostic.V25_REPORT.as_posix(),
            "v25_report_sha256": source_diagnostic.V25_REPORT_SHA256,
            "v25_cache_path": source_diagnostic.V25_CACHE.as_posix(),
            "v25_cache_sha256": source_diagnostic.V25_CACHE_SHA256,
            "v26_report_path": source_diagnostic.V26_REPORT.as_posix(),
            "v26_report_sha256": source_diagnostic.V26_REPORT_SHA256,
            "v26_cache_path": source_diagnostic.V26_CACHE.as_posix(),
            "v26_cache_sha256": source_diagnostic.V26_CACHE_SHA256,
            "v27_report_path": V27_REPORT.as_posix(),
            "v27_report_sha256": V27_REPORT_SHA256,
            "v27_cache_path": V27_CACHE.as_posix(),
            "v27_cache_sha256": V27_CACHE_SHA256,
            "v25_binding_path": source_diagnostic.BINDING_PATH.as_posix(),
            "v25_binding_sha256": source_diagnostic.BINDING_SHA256,
            "source_diagnostic_path": (
                "ml/markers/center/component_diversity_v27/"
                "diagnose_score_localization.py"
            ),
            "source_diagnostic_sha256": SOURCE_DIAGNOSTIC_SHA256,
            "diagnostic_source_sha256": expected_source_sha256,
        },
        "metadata_reconstruction": {
            "method": (
                "existing authenticated V27 historical-source preparation; only fixed "
                "component/family dev truth, tensors, and panel domains are consumed"
            ),
            "new_dataset_sampling_choices": 0,
            "proposal_coordinates_source": "authenticated V25 cache",
            "model_outputs_source": "authenticated V26 and V27 caches",
        },
        "component_dev": results["component"],
        "family_dev": results["family"],
        "interpretation_limits": [
            "The hybrids are mechanical counterfactuals that break the learned joint dependence among confidence, offset, and radius heads; they are diagnostics, not candidates.",
            "The fixed synthetic dev truth, scene tensor, and panel domain are deterministically reconstructed only to replay matching, consensus, and domain containment.",
            "This is one registered-threshold replay and does not select a threshold, architecture, candidate, or training revision.",
            "The report replays the frozen Python development postprocessor and does not establish C# runtime parity.",
            "No private, sealed, optimizer, production, or release decision is made by this report.",
        ],
        "synthetic_only": True,
        "cache_only_model_outputs": True,
        "model_inference": False,
        "proposal_inference": False,
        "scene_inference_count": 0,
        "optimizer_steps": 0,
        "thresholds_selected": 0,
        "new_dataset_sampling_choices": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "torch_threads": torch_threads,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
    destination.mkdir(parents=True, exist_ok=False)
    report_path = destination / REPORT_NAME
    with report_path.open("xb") as stream:
        stream.write(canonical_json_bytes(report))
    return report


def self_test() -> None:
    left = {0, 1}
    right = {1, 2}
    _require(
        _paired_transitions(left, right, 4)
        == {
            "true_positive_to_true_positive": 1,
            "true_positive_to_false_negative": 1,
            "false_negative_to_true_positive": 1,
            "false_negative_to_false_negative": 1,
        },
        "paired transition self-test failed",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-source-sha256")
    parser.add_argument("--torch-threads", type=int, default=2)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        print(json.dumps({"status": "pass", "scope": "pure-head-swap-helpers"}))
        return 0
    if not args.expected_source_sha256:
        parser.error("--expected-source-sha256 is required for diagnostic execution")
    report = run(
        args.expected_source_sha256,
        torch_threads=args.torch_threads,
    )
    report_path = REPOSITORY_ROOT / OUTPUT_DIRECTORY / REPORT_NAME
    print(json.dumps({
        "status": "complete",
        "report_sha256": _sha(report_path),
        "component_counts": report["component_dev"]["counts"],
        "family_counts": report["family_dev"]["counts"],
        "elapsed_ms": report["elapsed_ms"],
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
