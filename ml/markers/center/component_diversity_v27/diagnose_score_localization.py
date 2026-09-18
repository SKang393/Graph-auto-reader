# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cache one V27 synthetic-dev pass and attribute score, offset, and NMS errors.

This diagnostic authenticates the failed, unconsumed V27 outcome and the fixed
V25/V26 development caches. It runs the V27 ONNX model once on the same frozen
proposal patches, then compares V26 and V27 at the registered 0.25 operating
threshold. It does not train, select a threshold, read private or sealed data,
or authorize a production candidate.
"""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.center.component_diversity_v27 import train_p1 as v27
from ml.markers.center.localization_confidence_v26 import (
    diagnose_suppressor_anchors as cache_helper,
)
from ml.markers.center.plot_domain_v25 import diagnose_dev as postprocess
from ml.markers.center.plot_domain_v25.proposal_domain import (
    extract_proposals_in_domain,
)
from ml.markers.gate_seal import canonical_json_bytes, source_bundle_sha256


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v27-score-localization/diagnosis-v1"
)
CACHE_NAME = "fixed-v27-dev-outputs.npz"
REPORT_NAME = "diagnosis.json"
SCHEMA = "graphreader.marker-center-v27-fixed-dev-attribution.v1"
THRESHOLD = 0.25
MODEL_IDENTITIES = ("v26", "v27")

V27_PATHS = {
    "result": Path("ml/markers/center/component_diversity_v27/P1_RESULT.json"),
    "config": Path("ml/markers/center/component_diversity_v27/training/p1.json"),
    "protocol": Path("ml/markers/center/component_diversity_v27/protocol.json"),
    "candidate_report": Path(
        "artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/"
        "candidate-report.json"
    ),
    "onnx": Path(
        "artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/"
        "marker-center-component-diversity-v27-p1.onnx"
    ),
}
V27_HASHES = {
    "result": "a13d74f5cac9bd745759873932d63c81de2ce2a385526969202f0dd8d0fc63ac",
    "config": "e0c0545ce0182be73c7c1f64a86587b5b849cc1cca348da4014f0905b3c8608a",
    "protocol": "d17a97fb294bcc465d8b38554c0ec6a6321e48dca06f051c91a3586b23226697",
    "candidate_report": "876bc719324e7a4137182c23ffe1f32c97789794c255a5ae0b1d1da3125c8c47",
    "onnx": "4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04",
}
V27_RUNNER_BUNDLE = "217b58ccc92cbaf0ff6bc8d3d2f87a4b0b644aac92ca84bfac18c86ad3d460ac"

V26_REPORT = Path(
    "artifacts/goal22-runs/marker-v26-annulus/diagnosis-v1/diagnosis.json"
)
V26_REPORT_SHA256 = "936a5ae1bca09bd431d94ad7c6acfc6cf521af78e8e45afea3498305a09250f8"
V26_CACHE = V26_REPORT.parent / "fixed-v26-dev-outputs.npz"
V26_CACHE_SHA256 = "ff0d332ef0c7b7c8b3084e2ae2357cb551d5ce698d320e67077bcd2b63cec90d"

V25_REPORT = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/"
    "suppressor-anchor-diagnosis-v1/diagnosis.json"
)
V25_REPORT_SHA256 = "ad923c8f9c658e4a51b4babf728f30f68e9bf33fb3d4528587b6f3e5998290db"
V25_CACHE = V25_REPORT.parent / "fixed-v25-dev-outputs.npz"
V25_CACHE_SHA256 = "d9247c03dded13b00c582a64152976a9b89f1b01704670cfdc5f226c9d09eb72"

BINDING_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/"
    "visible-content-v3-binding/binding-v3.json"
)
BINDING_SHA256 = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"

SOURCE_HASHES = {
    "ml/markers/center/plot_domain_v25/diagnose_dev.py":
        "7d138c9cb7d29bb97f2d88d7b76f15e90f9bcda32817850d54e263e8203a0bb2",
    "ml/markers/center/localization_confidence_v26/diagnose_suppressor_anchors.py":
        "ad19a8c9a77781cf0a925728e51aadd26577da11f917847a2d328e104ae71051",
    "ml/markers/center/plot_domain_v25/runtime_domain_binding_v3.py":
        "2e8ad74fe44720b96323a3e927ae0e611b1ef993a565c7abd02bfabdd6446f93",
    "ml/markers/center/real_range_generator_v1/generator.py":
        "2244094e8d8e3a8cacb83e11c5e7189ded46b25b1ce9ecdad1bd80e5dec78b41",
}

EXPECTED = {
    "component": {
        "scene_count": 167,
        "truth_count": 2004,
        "proposal_count": 224840,
        "v26": {"true_positive": 1837, "false_positive": 224, "false_negative": 167},
        "v27": {"true_positive": 1796, "false_positive": 238, "false_negative": 208},
    },
    "family": {
        "scene_count": 9,
        "truth_count": 206,
        "proposal_count": 45396,
        "v26": {"true_positive": 194, "false_positive": 47, "false_negative": 12},
        "v27": {"true_positive": 190, "false_positive": 39, "false_negative": 16},
    },
}


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


def _scene_identity(scene: Any) -> str:
    return (
        scene.sampling_identity
        if hasattr(scene, "sampling_identity")
        else f"{scene.split}:{scene.family}:{scene.seed}"
    )


def _prediction_key(model_identity: str, split: str, scene_number: int) -> str:
    _require(model_identity in MODEL_IDENTITIES, "unknown model identity")
    _require(split in EXPECTED, "unknown split identity")
    _require(scene_number >= 0, "scene number is negative")
    return f"{split}_{scene_number:03d}_{model_identity}_candidate_predictions"


def _require_decomposition(total: int, parts: Mapping[str, int], label: str) -> None:
    _require(total >= 0 and all(value >= 0 for value in parts.values()), f"{label} is negative")
    _require(sum(parts.values()) == total, f"{label} does not conserve its total")


def _require_exact_metrics(name: str, model_identity: str, counts: Mapping[str, int]) -> None:
    _require(model_identity in MODEL_IDENTITIES, "unknown model identity")
    expected = EXPECTED[name]
    exact = {
        "scene_count": expected["scene_count"],
        "truth_count": expected["truth_count"],
        "proposal_count": expected["proposal_count"],
        **expected[model_identity],
    }
    for key, value in exact.items():
        _require(counts.get(key, 0) == value, f"{name} {model_identity} {key} changed")


def _score_offset_counts(
    coordinates: np.ndarray,
    centers: np.ndarray,
    output: np.ndarray,
) -> tuple[Counter[str], np.ndarray, np.ndarray, np.ndarray]:
    _require(
        coordinates.dtype == np.float32
        and centers.dtype == np.float32
        and output.dtype == np.float32
        and coordinates.ndim == centers.ndim == output.ndim == 2
        and coordinates.shape[1] == centers.shape[1] == 2
        and output.shape == (len(coordinates), 4)
        and len(centers) > 0
        and np.isfinite(coordinates).all()
        and np.isfinite(centers).all()
        and np.isfinite(output).all(),
        "score/offset arrays are invalid",
    )
    distance = np.linalg.norm(
        coordinates[:, None, :] - centers[None, :, :], axis=2
    )
    assignment = distance.argmin(axis=1)
    positive = distance.min(axis=1) <= 3.0
    scores = output[:, 0]
    decoded = coordinates + output[:, 1:3] * postprocess.v24.STRIDE
    decoded_error = np.linalg.norm(decoded - centers[assignment], axis=1)
    counts: Counter[str] = Counter({
        "truths": len(centers),
        "proposals": len(coordinates),
        "positive_anchors": int(positive.sum()),
        "negative_anchors": int((~positive).sum()),
        "positive_anchors_below_threshold": int(
            np.count_nonzero(positive & (scores < THRESHOLD))
        ),
        "negative_anchors_above_threshold": int(
            np.count_nonzero(~positive & (scores >= THRESHOLD))
        ),
        "positive_anchors_decoded_beyond_five_pixels": int(
            np.count_nonzero(positive & (decoded_error > 5.0))
        ),
    })
    for truth_index in range(len(centers)):
        owned = positive & (assignment == truth_index)
        if not owned.any():
            counts["truths_without_positive_anchor"] += 1
        elif not (scores[owned] >= THRESHOLD).any():
            counts["truths_without_above_threshold_positive_anchor"] += 1
        elif not ((scores[owned] >= THRESHOLD) & (decoded_error[owned] <= 5.0)).any():
            counts["truths_without_localized_above_threshold_positive_anchor"] += 1
        else:
            counts["truths_with_localized_above_threshold_positive_anchor"] += 1
    return counts, positive, scores, decoded_error


def _require_score_offset_partition(counts: Mapping[str, int], label: str) -> None:
    _require_decomposition(
        counts["proposals"],
        {
            "positive": counts["positive_anchors"],
            "negative": counts["negative_anchors"],
        },
        f"{label} proposal partition",
    )
    _require_decomposition(
        counts["truths"],
        {
            "without_positive": counts.get("truths_without_positive_anchor", 0),
            "without_score": counts.get(
                "truths_without_above_threshold_positive_anchor", 0
            ),
            "without_localization": counts.get(
                "truths_without_localized_above_threshold_positive_anchor", 0
            ),
            "localized": counts.get(
                "truths_with_localized_above_threshold_positive_anchor", 0
            ),
        },
        f"{label} truth partition",
    )


def _one_truth_cause(
    scene: Any,
    proposals: Any,
    output: np.ndarray,
    trace: postprocess.PostprocessTrace,
    truth_index: int,
    domain: Any,
) -> str:
    other_truths = set(range(len(scene.centers))) - {truth_index}
    causes, _, _, _ = postprocess.categorize_missed_truths(
        scene, proposals, output, trace.accepted, other_truths, domain
    )
    _require(sum(causes.values()) == 1, "one missed truth did not receive one cause")
    return next(iter(causes))


def _authenticate(
    root: Path,
    expected_source_sha256: str,
) -> tuple[dict[str, Path], Mapping[str, Any], Mapping[str, Any], Mapping[str, Any]]:
    _require(len(expected_source_sha256) == 64, "expected diagnostic source SHA256 is invalid")
    _require(_sha(Path(__file__)) == expected_source_sha256, "diagnostic source changed")

    paths = {key: root / path for key, path in V27_PATHS.items()}
    paths.update({
        "diagnostic_source": Path(__file__),
        "v26_report": root / V26_REPORT,
        "v26_cache": root / V26_CACHE,
        "v25_report": root / V25_REPORT,
        "v25_cache": root / V25_CACHE,
        "binding": root / BINDING_PATH,
    })
    expected_hashes = {
        **{key: value for key, value in V27_HASHES.items()},
        "diagnostic_source": expected_source_sha256,
        "v26_report": V26_REPORT_SHA256,
        "v26_cache": V26_CACHE_SHA256,
        "v25_report": V25_REPORT_SHA256,
        "v25_cache": V25_CACHE_SHA256,
        "binding": BINDING_SHA256,
    }
    actual_hashes = {key: _sha(path) for key, path in paths.items()}
    _require(actual_hashes == expected_hashes, "frozen diagnostic input changed")
    for relative, expected in SOURCE_HASHES.items():
        _require(_sha(root / relative) == expected, f"diagnostic dependency changed: {relative}")
    _require(
        source_bundle_sha256(root, v27.RUNNER_SOURCE_PATHS) == V27_RUNNER_BUNDLE,
        "V27 runner source bundle changed",
    )

    config = _json(paths["config"])
    candidate = _json(paths["candidate_report"])
    result = _json(paths["result"])
    recipe = config.get("recipe")
    report_descriptor = result.get("candidate_report")
    authorization = candidate.get("training_authorization")
    _require(
        config.get("expected_runner_source_bundle_sha256") == V27_RUNNER_BUNDLE
        and isinstance(recipe, Mapping)
        and recipe.get("confidence_threshold") == THRESHOLD
        and isinstance(report_descriptor, Mapping)
        and report_descriptor.get("sha256") == V27_HASHES["candidate_report"]
        and result.get("candidate_config_sha256") == V27_HASHES["config"]
        and result.get("protocol_sha256") == V27_HASHES["protocol"]
        and result.get("onnx_sha256") == V27_HASHES["onnx"]
        and result.get("runner_source_bundle_sha256") == V27_RUNNER_BUNDLE
        and result.get("operating_threshold") == THRESHOLD
        and result.get("status") == "failed_dev_unconsumed"
        and candidate.get("status") == "failed_dev"
        and candidate.get("onnx_sha256") == V27_HASHES["onnx"]
        and candidate.get("candidate_config_sha256") == V27_HASHES["config"]
        and isinstance(authorization, Mapping)
        and authorization.get("runner_source_bundle_sha256") == V27_RUNNER_BUNDLE
        and result.get("component_selected") == candidate.get("component_dev_comparisons", [None])[0]
        and result.get("family_selected") == candidate.get("family_dev_comparisons", [None])[0],
        "V27 outcome ancestry changed",
    )

    v26_report = _json(paths["v26_report"])
    v25_report = _json(paths["v25_report"])
    v26_cache = v26_report.get("cache")
    v25_cache = v25_report.get("cache")
    _require(
        v26_report.get("operating_threshold") == THRESHOLD
        and v26_report.get("optimizer_steps") == 0
        and v26_report.get("private_reads") == 0
        and v26_report.get("sealed_runs") == 0
        and isinstance(v26_cache, Mapping)
        and v26_cache.get("sha256") == V26_CACHE_SHA256
        and v26_cache.get("array_count") == 176,
        "V26 diagnostic cache ancestry changed",
    )
    _require(
        isinstance(v25_cache, Mapping)
        and v25_cache.get("sha256") == V25_CACHE_SHA256
        and v25_cache.get("array_count") == 352,
        "V25 diagnostic cache ancestry changed",
    )
    return paths, v25_report, v26_report, actual_hashes


def _load_caches(
    paths: Mapping[str, Path],
    v25_report: Mapping[str, Any],
    v26_report: Mapping[str, Any],
) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray]]:
    with np.load(paths["v25_cache"], allow_pickle=False) as archive:
        v25_arrays = {key: np.array(archive[key], copy=True) for key in archive.files}
    with np.load(paths["v26_cache"], allow_pickle=False) as archive:
        v26_arrays = {key: np.array(archive[key], copy=True) for key in archive.files}

    expected_v25: set[str] = set()
    expected_v26: set[str] = set()
    for name in EXPECTED:
        old_section = v25_report.get(f"{name}_dev")
        new_section = v26_report.get(f"{name}_dev")
        _require(isinstance(old_section, Mapping), f"V25 report omitted {name}")
        _require(isinstance(new_section, Mapping), f"V26 report omitted {name}")
        old_manifest = old_section.get("cache_manifest")
        new_manifest = new_section.get("cache_manifest")
        _require(
            isinstance(old_manifest, list)
            and isinstance(new_manifest, list)
            and len(old_manifest) == len(new_manifest) == EXPECTED[name]["scene_count"],
            f"{name} cache manifest changed",
        )
        for old_row, new_row in zip(old_manifest, new_manifest, strict=True):
            _require(
                isinstance(old_row, Mapping)
                and isinstance(new_row, Mapping)
                and old_row.get("scene_identity") == new_row.get("scene_identity")
                and old_row.get("proposal_count") == new_row.get("proposal_count")
                and old_row.get("proposal_coordinates_sha256")
                == new_row.get("proposal_coordinates_sha256"),
                f"{name} V25/V26 cache join changed",
            )
            for key_name, hash_name, arrays, expected_keys, width in (
                ("proposal_coordinates_key", "proposal_coordinates_sha256", v25_arrays, expected_v25, 2),
                ("candidate_predictions_key", "candidate_predictions_sha256", v25_arrays, expected_v25, 4),
                ("v26_candidate_predictions_key", "v26_candidate_predictions_sha256", v26_arrays, expected_v26, 4),
            ):
                source = old_row if not key_name.startswith("v26") else new_row
                key = source.get(key_name)
                _require(
                    isinstance(key, str) and key in arrays and key not in expected_keys,
                    f"{name} cache key is missing or duplicated",
                )
                expected_keys.add(key)
                value = arrays[key]
                _require(
                    value.dtype == np.float32
                    and value.ndim == 2
                    and value.shape[1] == width
                    and np.isfinite(value).all()
                    and cache_helper._array_sha(value) == source.get(hash_name),
                    f"{name} cached array identity changed",
                )
    _require(
        set(v25_arrays) == expected_v25 and len(v25_arrays) == 352,
        "V25 cache contains an unexpected array inventory",
    )
    _require(
        set(v26_arrays) == expected_v26 and len(v26_arrays) == 176,
        "V26 cache contains an unexpected array inventory",
    )
    return v25_arrays, v26_arrays


def _load_authenticated_dev_scenes(root: Path) -> dict[str, tuple[Any, ...]]:
    """Reuse V27's reviewed historical-source handling for both dev splits."""

    base = v27._prepare_base_with_authenticated_current_sources(root)
    scenes = {
        "component": tuple(base.component_dev),
        "family": tuple(base.family_dev),
    }
    for name, values in scenes.items():
        _require(
            len(values) == EXPECTED[name]["scene_count"],
            f"{name} authenticated dev scene count changed",
        )
    return scenes


def _evaluate_split(
    name: str,
    bound_scenes: Sequence[Any],
    session: ort.InferenceSession,
    v25_arrays: Mapping[str, np.ndarray],
    v26_arrays: Mapping[str, np.ndarray],
    v25_manifest: Sequence[Mapping[str, Any]],
    v26_manifest: Sequence[Mapping[str, Any]],
    output_cache: dict[str, np.ndarray],
) -> dict[str, Any]:
    totals = {identity: Counter() for identity in MODEL_IDENTITIES}
    score_offset = {identity: Counter() for identity in MODEL_IDENTITIES}
    radius_nms = {
        identity: postprocess._new_radius_nms_accumulator()
        for identity in MODEL_IDENTITIES
    }
    v27_fn_causes: Counter[str] = Counter()
    threshold_crossings: Counter[str] = Counter()
    confidence_delta: list[float] = []
    positive_confidence_delta: list[float] = []
    offset_delta_px: list[float] = []
    radius_delta: list[float] = []
    cache_manifest: list[dict[str, Any]] = []
    forward_ms = 0.0

    expected_scenes = EXPECTED[name]["scene_count"]
    _require(
        len(bound_scenes) == len(v25_manifest) == len(v26_manifest) == expected_scenes,
        f"{name} scene manifest length changed",
    )
    for scene_number, (bound, old_row, prior_row) in enumerate(
        zip(bound_scenes, v25_manifest, v26_manifest, strict=True)
    ):
        scene = bound.scene if hasattr(bound, "scene") else bound
        domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
        proposals = (
            extract_proposals_in_domain(scene.tensor, domain).proposals
            if domain is not None
            else postprocess.v24.extract_proposals(scene.tensor)
        )
        identity = _scene_identity(scene)
        coordinate_key = str(old_row.get("proposal_coordinates_key"))
        v26_output_key = str(prior_row.get("v26_candidate_predictions_key"))
        coordinates = proposals.coordinates.detach().cpu().numpy().astype(np.float32, copy=False)
        patches = proposals.patches.detach().cpu().numpy().astype(np.float32, copy=False)
        v26_output = v26_arrays[v26_output_key]
        _require(
            old_row.get("scene_identity") == prior_row.get("scene_identity") == identity
            and old_row.get("proposal_count") == prior_row.get("proposal_count") == len(coordinates)
            and np.array_equal(coordinates, v25_arrays[coordinate_key])
            and cache_helper._array_sha(coordinates) == prior_row.get("proposal_coordinates_sha256")
            and cache_helper._array_sha(patches) == prior_row.get("proposal_patches_sha256")
            and cache_helper._array_sha(v26_output) == prior_row.get("v26_candidate_predictions_sha256"),
            f"{name} frozen proposal or V26 output changed: {identity}",
        )

        started = time.perf_counter()
        v27_output = postprocess._infer(session, proposals.patches)
        forward_ms += (time.perf_counter() - started) * 1000.0
        _require(
            v27_output.dtype == np.float32
            and v27_output.shape == v26_output.shape
            and np.isfinite(v27_output).all(),
            f"{name} V27 output shape or values changed: {identity}",
        )
        v27_key = _prediction_key("v27", name, scene_number)
        output_cache[v27_key] = v27_output
        cache_manifest.append({
            "scene_identity": identity,
            "proposal_count": len(coordinates),
            "proposal_coordinates_sha256": cache_helper._array_sha(coordinates),
            "proposal_patches_sha256": cache_helper._array_sha(patches),
            "v26_candidate_predictions_key": v26_output_key,
            "v26_candidate_predictions_sha256": cache_helper._array_sha(v26_output),
            "v27_candidate_predictions_key": v27_key,
            "v27_candidate_predictions_sha256": cache_helper._array_sha(v27_output),
        })

        outputs = {"v26": v26_output, "v27": v27_output}
        traces = {
            model_identity: postprocess._postprocess_trace(scene, proposals, output, domain)
            for model_identity, output in outputs.items()
        }
        matches = {
            model_identity: postprocess.greedy_match_pairs(trace.accepted, scene.centers)
            for model_identity, trace in traces.items()
        }
        centers = np.asarray(scene.centers, dtype=np.float32)
        for model_identity in MODEL_IDENTITIES:
            output = outputs[model_identity]
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
            scene_counts, positive, _, _ = _score_offset_counts(
                coordinates, centers, output
            )
            score_offset[model_identity].update(scene_counts)
            if model_identity == "v27":
                for truth_index in set(range(len(scene.centers))) - {
                    truth for _, truth in pairs
                }:
                    v27_fn_causes[
                        _one_truth_cause(
                            scene, proposals, output, trace, truth_index, domain
                        )
                    ] += 1

        v26_above = v26_output[:, 0] >= THRESHOLD
        v27_above = v27_output[:, 0] >= THRESHOLD
        threshold_crossings.update({
            "below_to_above": int(np.count_nonzero(~v26_above & v27_above)),
            "above_to_below": int(np.count_nonzero(v26_above & ~v27_above)),
            "above_in_both": int(np.count_nonzero(v26_above & v27_above)),
            "below_in_both": int(np.count_nonzero(~v26_above & ~v27_above)),
        })
        deltas = v27_output - v26_output
        confidence_delta.extend(float(value) for value in deltas[:, 0])
        positive_confidence_delta.extend(float(value) for value in deltas[positive, 0])
        offset_delta_px.extend(
            float(value)
            for value in np.hypot(deltas[:, 1], deltas[:, 2]) * postprocess.v24.STRIDE
        )
        radius_delta.extend(float(value) for value in deltas[:, 3])

    for model_identity in MODEL_IDENTITIES:
        _require_exact_metrics(name, model_identity, totals[model_identity])
        _require_score_offset_partition(
            score_offset[model_identity], f"{name} {model_identity}"
        )
    _require_decomposition(
        totals["v27"]["false_negative"], v27_fn_causes,
        f"{name} V27 false-negative causes",
    )
    _require_decomposition(
        totals["v27"]["proposal_count"], threshold_crossings,
        f"{name} threshold crossings",
    )
    _require(
        radius_nms["v27"]["nms_counts"]["false_negative_truths"]
        == v27_fn_causes["nms_suppression"],
        f"{name} NMS cause does not match actual suppressor trace",
    )
    return {
        "counts": {
            identity: dict(sorted(totals[identity].items()))
            for identity in MODEL_IDENTITIES
        },
        "score_offset_attribution": {
            identity: dict(sorted(score_offset[identity].items()))
            for identity in MODEL_IDENTITIES
        },
        "v27_false_negative_cause": dict(sorted(v27_fn_causes.items())),
        "actual_radius_and_nms_attribution": {
            identity: postprocess._radius_nms_report(radius_nms[identity])
            for identity in MODEL_IDENTITIES
        },
        "same_proposal_threshold_crossings": dict(sorted(threshold_crossings.items())),
        "same_proposal_v27_minus_v26": {
            "confidence": _quantiles(confidence_delta),
            "positive_anchor_confidence": _quantiles(positive_confidence_delta),
            "decoded_offset_vector_change_px": _quantiles(offset_delta_px),
            "raw_radius": _quantiles(radius_delta),
        },
        "cache_manifest": cache_manifest,
        "model_forward_ms": round(forward_ms, 3),
    }


def _verify_inputs_unchanged(
    root: Path,
    paths: Mapping[str, Path],
    expected_hashes: Mapping[str, str],
) -> None:
    _require(
        {key: _sha(path) for key, path in paths.items()} == expected_hashes,
        "diagnostic input changed during execution",
    )
    for relative, expected in SOURCE_HASHES.items():
        _require(_sha(root / relative) == expected, f"diagnostic dependency changed: {relative}")
    _require(
        source_bundle_sha256(root, v27.RUNNER_SOURCE_PATHS) == V27_RUNNER_BUNDLE,
        "V27 runner source bundle changed during execution",
    )


def run(
    expected_source_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    destination = (
        output_directory if output_directory.is_absolute() else root / output_directory
    ).resolve()
    _require(
        destination == (root / OUTPUT_DIRECTORY).resolve(),
        "diagnostic output path changed",
    )
    _require(not destination.exists(), "diagnostic output already exists")
    paths, v25_report, v26_report, input_hashes = _authenticate(
        root, expected_source_sha256
    )
    v25_arrays, v26_arrays = _load_caches(paths, v25_report, v26_report)

    torch.set_num_threads(int(v27.EXECUTION["torch_intraop_threads"]))
    scenes = _load_authenticated_dev_scenes(root)

    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    options.intra_op_num_threads = int(v27.EXECUTION["onnx_intraop_threads"])
    options.inter_op_num_threads = int(v27.EXECUTION["onnx_interop_threads"])
    session = ort.InferenceSession(
        str(paths["onnx"]),
        sess_options=options,
        providers=["CPUExecutionProvider"],
    )
    _require(
        session.get_providers()[0] == "CPUExecutionProvider",
        "diagnostic did not select CPUExecutionProvider",
    )

    output_cache: dict[str, np.ndarray] = {}
    results = {}
    for name in EXPECTED:
        results[name] = _evaluate_split(
            name,
            scenes[name],
            session,
            v25_arrays,
            v26_arrays,
            v25_report[f"{name}_dev"]["cache_manifest"],
            v26_report[f"{name}_dev"]["cache_manifest"],
            output_cache,
        )
    _require(len(output_cache) == 176, "V27 output cache array count changed")
    _verify_inputs_unchanged(root, paths, input_hashes)

    destination.mkdir(parents=True, exist_ok=False)
    cache_path = destination / CACHE_NAME
    report_path = destination / REPORT_NAME
    with cache_path.open("xb") as stream:
        np.savez_compressed(stream, **output_cache)
    report = {
        "schema": SCHEMA,
        "scope": "fixed-v25-proposals-v26-v27-synthetic-dev-attribution",
        "candidate_id": "P1",
        "operating_threshold": THRESHOLD,
        "inputs": {
            **{
                f"v27_{key}_path": path.relative_to(root).as_posix()
                for key, path in paths.items()
                if key in V27_PATHS
            },
            **{f"v27_{key}_sha256": value for key, value in V27_HASHES.items()},
            "v27_runner_source_bundle_sha256": V27_RUNNER_BUNDLE,
            "v26_report_path": V26_REPORT.as_posix(),
            "v26_report_sha256": V26_REPORT_SHA256,
            "v26_cache_path": V26_CACHE.as_posix(),
            "v26_cache_sha256": V26_CACHE_SHA256,
            "v25_report_path": V25_REPORT.as_posix(),
            "v25_report_sha256": V25_REPORT_SHA256,
            "v25_cache_path": V25_CACHE.as_posix(),
            "v25_cache_sha256": V25_CACHE_SHA256,
            "binding_path": BINDING_PATH.as_posix(),
            "binding_sha256": BINDING_SHA256,
            "diagnostic_source_sha256": expected_source_sha256,
            "diagnostic_dependency_sha256": dict(SOURCE_HASHES),
        },
        "cache": {
            "path": cache_path.relative_to(destination).as_posix(),
            "sha256": _sha(cache_path),
            "array_count": len(output_cache),
            "contains": "synthetic dev V27 candidate outputs only; proposal coordinates remain in the authenticated V25 cache",
        },
        "component_dev": results["component"],
        "family_dev": results["family"],
        "interpretation_limits": [
            "This is one fixed-threshold synthetic-dev diagnostic, not threshold selection or candidate evaluation.",
            "Proposal support, score, decoded offset, and NMS attribution are observational and do not establish a causal training repair.",
            "The report replays the frozen Python development postprocessor and does not establish C# runtime parity.",
            "No private, sealed, optimizer, production, or release decision is made by this report.",
        ],
        "synthetic_only": True,
        "model_inference": True,
        "scene_inference_count": 176,
        "torch_intraop_threads": int(v27.EXECUTION["torch_intraop_threads"]),
        "onnx_intraop_threads": int(v27.EXECUTION["onnx_intraop_threads"]),
        "onnx_interop_threads": int(v27.EXECUTION["onnx_interop_threads"]),
        "optimizer_steps": 0,
        "thresholds_selected": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
    with report_path.open("xb") as stream:
        stream.write(canonical_json_bytes(report))
    return report


def self_test() -> None:
    valid = {
        "scene_count": 167,
        "truth_count": 2004,
        "proposal_count": 224840,
        "true_positive": 1796,
        "false_positive": 238,
        "false_negative": 208,
    }
    _require_exact_metrics("component", "v27", valid)
    _require_decomposition(6, {"score": 2, "offset": 1, "nms": 3}, "synthetic")
    try:
        _require_exact_metrics("component", "v27", {**valid, "false_negative": 207})
    except RuntimeError:
        pass
    else:
        raise AssertionError("metric mutation was accepted")
    try:
        _require_decomposition(6, {"score": 2, "offset": 1, "nms": 2}, "synthetic")
    except RuntimeError:
        pass
    else:
        raise AssertionError("decomposition mutation was accepted")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-source-sha256")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        print(json.dumps({"status": "pass", "scope": "pure-decomposition-only"}))
        return 0
    if not args.expected_source_sha256:
        parser.error("--expected-source-sha256 is required for diagnostic execution")
    report = run(args.expected_source_sha256)
    report_path = REPOSITORY_ROOT / OUTPUT_DIRECTORY / REPORT_NAME
    print(json.dumps({
        "status": "complete",
        "report_sha256": _sha(report_path),
        "component_v27": report["component_dev"]["counts"]["v27"],
        "family_v27": report["family_dev"]["counts"]["v27"],
        "elapsed_ms": report["elapsed_ms"],
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
