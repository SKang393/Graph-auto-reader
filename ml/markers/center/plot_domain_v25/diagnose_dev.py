# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate synthetic-dev diagnosis for the frozen V25 P1 candidate.

The model sees only authenticated component tensors and actual runtime family
planes. Synthetic truth and descriptive geometry are joined after those inputs
validate and are used only to attribute errors at the fixed operating point.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
import time
from typing import Any, Iterable, Mapping, Sequence

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.gate_seal import canonical_json_bytes, source_bundle_sha256
from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction, ProposalBatch
from ml.markers.center.mask_preserving_v24.family_scenes import _records
from ml.markers.center.mask_preserving_v24.mask_preserving import _consensus
from ml.markers.center.mask_preserving_v24 import mask_preserving as v24
from ml.markers.center.real_range_generator_v1.generator import build_split
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import png_bytes

from . import protocol, runtime_domain_binding_v3, train_p1
from .proposal_domain import PlotDomain, extract_proposals_in_domain, postprocess_in_domain


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
RESULT_PATH = Path("ml/markers/center/plot_domain_v25/P1_RESULT.json")
CONFIG_PATH = Path("ml/markers/center/plot_domain_v25/training/p1.json")
CANDIDATE_REPORT_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/P1-run/candidate-report.json"
)
ONNX_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/P1-run/"
    "marker-center-plot-domain-v25-p1.onnx"
)
OUTPUT_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/diagnosis-v1/"
    "nms-radius-attribution-v2/diagnosis.json"
)
PRIOR_DIAGNOSIS_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/diagnosis-v1/diagnosis.json"
)
EXPECTED_PRIOR_DIAGNOSIS_SHA256 = "c0911d7d730098eabb223431c36fad16f5659918f19635e33e6865bab27a7cf6"
EXPECTED_RESULT_SHA256 = "fb64443005b6ca4c26d4e5f1d8b37d8882a93a186d9b2b92fffc476a8c823a63"
EXPECTED_CONFIG_SHA256 = "e84acb31ca4df3204d3894e893b31ba7fede55aa91a6c885f791eee05b7fe9b2"
EXPECTED_REPORT_SHA256 = "e5347d33313147d57c4d6ce80bcf06a020a1a70ddd52ef4bb8a2e0c623b7a64b"
EXPECTED_ONNX_SHA256 = "77293a9fef2656aba4955e3e8d0676e2fac811666061484527af31f7a00349b9"
THRESHOLD = 0.25
MATCH_TOLERANCE_PX = 5.0
POSITIVE_PROPOSAL_RADIUS_PX = 3.0
MASK_PRESENT_MINIMUM = 0.35
INFERENCE_BATCH = 4096
EXPECTED = {
    "component": {"scenes": 167, "truth": 2004, "tp": 1863, "fp": 152, "fn": 141},
    "family": {"scenes": 9, "truth": 206, "tp": 197, "fp": 59, "fn": 9},
}


@dataclass(frozen=True)
class Candidate:
    source_proposal_index: int
    prediction: MarkerPrediction
    raw_radius: float | None = None


@dataclass(frozen=True)
class Suppression:
    suppressed: Candidate
    suppressor: Candidate
    center_distance: float
    exclusion_distance: float


@dataclass(frozen=True)
class PostprocessTrace:
    candidates_before_nms: tuple[Candidate, ...]
    accepted: tuple[Candidate, ...]
    suppressions: tuple[Suppression, ...]


@dataclass(frozen=True)
class Primitive:
    category: str
    kind: str
    points: tuple[tuple[float, float], ...]
    closed: bool


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def _json(path: Path) -> Mapping[str, Any]:
    value = json.loads(path.read_bytes())
    if not isinstance(value, Mapping):
        raise RuntimeError(f"expected JSON object: {path}")
    return value


def _quantiles(values: Iterable[float]) -> dict[str, float | int]:
    items = np.asarray(tuple(values), dtype=np.float64)
    if not len(items):
        return {"count": 0}
    points = np.quantile(items, (0.0, 0.05, 0.5, 0.9, 0.95, 1.0))
    return {
        "count": int(len(items)),
        **dict(zip(
            ("minimum", "p05", "median", "p90", "p95", "maximum"),
            (float(value) for value in points),
            strict=True,
        )),
    }


def distance_bin(distance: float) -> str:
    if distance <= 3:
        return "le3"
    if distance <= 5:
        return "gt3_le5"
    if distance <= 8:
        return "gt5_le8"
    if distance <= 16:
        return "gt8_le16"
    return "gt16_or_unavailable"


def _segment_distance(
    point: tuple[float, float], start: tuple[float, float], end: tuple[float, float]
) -> float:
    px, py = point
    ax, ay = start
    bx, by = end
    dx, dy = bx - ax, by - ay
    denominator = dx * dx + dy * dy
    if denominator <= 0:
        return math.hypot(px - ax, py - ay)
    ratio = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / denominator))
    return math.hypot(px - (ax + ratio * dx), py - (ay + ratio * dy))


def _point_in_polygon(
    point: tuple[float, float], polygon: Sequence[tuple[float, float]]
) -> bool:
    x, y = point
    inside = False
    previous = polygon[-1]
    for current in polygon:
        if _segment_distance(point, previous, current) <= 1e-9:
            return True
        x1, y1 = previous
        x2, y2 = current
        if (y1 > y) != (y2 > y):
            crossing = (x2 - x1) * (y - y1) / (y2 - y1) + x1
            if x < crossing:
                inside = not inside
        previous = current
    return inside


def _primitive_distance(point: tuple[float, float], primitive: Primitive) -> float:
    if len(primitive.points) == 1:
        return math.hypot(
            point[0] - primitive.points[0][0], point[1] - primitive.points[0][1]
        )
    if primitive.closed and _point_in_polygon(point, primitive.points):
        return 0.0
    count = len(primitive.points) if primitive.closed else len(primitive.points) - 1
    return min(
        _segment_distance(point, primitive.points[index], primitive.points[(index + 1) % len(primitive.points)])
        for index in range(count)
    )


def nearest_primitive(
    point: tuple[float, float], primitives: Sequence[Primitive]
) -> tuple[str, str, float] | None:
    if not primitives:
        return None
    values = (
        (primitive.category, primitive.kind, _primitive_distance(point, primitive))
        for primitive in primitives
    )
    return min(values, key=lambda item: (item[2], item[0], item[1]))


def greedy_matches(
    predictions: Sequence[Candidate], truths: Sequence[tuple[float, float]]
) -> tuple[set[int], set[int]]:
    pairs = greedy_match_pairs(predictions, truths)
    return {prediction for prediction, _ in pairs}, {truth for _, truth in pairs}


def greedy_match_pairs(
    predictions: Sequence[Candidate], truths: Sequence[tuple[float, float]]
) -> tuple[tuple[int, int], ...]:
    edges = sorted(
        (math.hypot(item.prediction.x - x, item.prediction.y - y), i, j)
        for i, item in enumerate(predictions)
        for j, (x, y) in enumerate(truths)
        if math.hypot(item.prediction.x - x, item.prediction.y - y) <= MATCH_TOLERANCE_PX
    )
    used_predictions: set[int] = set()
    used_truths: set[int] = set()
    pairs: list[tuple[int, int]] = []
    for _, prediction_index, truth_index in edges:
        if prediction_index not in used_predictions and truth_index not in used_truths:
            used_predictions.add(prediction_index)
            used_truths.add(truth_index)
            pairs.append((prediction_index, truth_index))
    return tuple(pairs)


def radius_bin(radius: float) -> str:
    if not math.isfinite(radius) or radius <= 0:
        raise ValueError("ground-truth radius must be finite and positive")
    if radius < 2.5:
        return "below_decoder_minimum"
    if radius <= 8.0:
        return "within_decoder_range"
    return "above_decoder_maximum"


def decoded_radius_bin(radius: float | None) -> str:
    if radius is None:
        return "unavailable"
    if not math.isfinite(radius):
        raise ValueError("decoded raw radius must be finite")
    if radius < 2.5:
        return "clamped_to_minimum"
    if radius <= 8.0:
        return "not_clamped"
    return "clamped_to_maximum"


def _truth_radii(scene: Any) -> tuple[float, ...]:
    raw = getattr(scene, "diameters", None)
    if raw is None:
        raw = tuple(2.0 * float(value) for value in getattr(scene, "radii", ()))
    values = tuple(float(value) / 2.0 for value in raw)
    if len(values) != len(scene.centers) or any(not math.isfinite(value) or value <= 0 for value in values):
        raise RuntimeError("diagnostic ground-truth radius inventory is invalid")
    return values


def _final_candidates(
    scene: Any,
    proposals: ProposalBatch,
    output: np.ndarray,
    domain: PlotDomain | None,
) -> tuple[Candidate, ...]:
    return _postprocess_trace(scene, proposals, output, domain).accepted


def _postprocess_trace(
    scene: Any,
    proposals: ProposalBatch,
    output: np.ndarray,
    domain: PlotDomain | None,
) -> PostprocessTrace:
    candidates: list[Candidate] = []
    for index in np.flatnonzero(output[:, 0] >= THRESHOLD):
        base_x, base_y = proposals.coordinates[index].tolist()
        x = float(base_x + output[index, 1] * v24.STRIDE)
        y = float(base_y + output[index, 2] * v24.STRIDE)
        if domain is not None and not domain.contains(x, y):
            continue
        raw_radius = float(output[index, 3])
        radius = float(np.clip(raw_radius, 2.5, 8.0))
        if _consensus(scene, x, y):
            candidates.append(
                Candidate(
                    int(index),
                    MarkerPrediction(x, y, radius, float(output[index, 0])),
                    raw_radius,
                )
            )
    accepted: list[Candidate] = []
    suppressions: list[Suppression] = []
    for item in sorted(
        candidates,
        key=lambda value: (
            -value.prediction.confidence,
            value.prediction.y,
            value.prediction.x,
        ),
    ):
        prediction = item.prediction
        suppressor: Candidate | None = None
        center_distance = math.inf
        exclusion_distance = math.inf
        for previous in accepted:
            distance = math.hypot(
                prediction.x - previous.prediction.x,
                prediction.y - previous.prediction.y,
            )
            exclusion = max(5.0, 1.25 * max(prediction.radius, previous.prediction.radius))
            if distance < exclusion:
                suppressor = previous
                center_distance = distance
                exclusion_distance = exclusion
                break
        if suppressor is not None:
            suppressions.append(Suppression(item, suppressor, center_distance, exclusion_distance))
            continue
        accepted.append(item)
    accepted.sort(key=lambda item: (
        item.prediction.y, item.prediction.x, -item.prediction.confidence
    ))
    reference = (
        v24.postprocess(scene, proposals, output)
        if domain is None
        else postprocess_in_domain(scene, proposals, output, domain).predictions
    )
    _require(len(reference) == len(accepted), "diagnostic postprocess count drifted")
    _require(all(
        abs(expected.x - actual.prediction.x) <= 1e-6
        and abs(expected.y - actual.prediction.y) <= 1e-6
        and abs(expected.radius - actual.prediction.radius) <= 1e-6
        and abs(expected.confidence - actual.prediction.confidence) <= 1e-6
        for expected, actual in zip(reference, accepted, strict=True)
    ), "diagnostic postprocess values drifted")
    return PostprocessTrace(tuple(candidates), tuple(accepted), tuple(suppressions))


def categorize_missed_truths(
    scene: Any,
    proposals: ProposalBatch,
    output: np.ndarray,
    predictions: Sequence[Candidate],
    used_truths: set[int],
    domain: PlotDomain | None = None,
) -> tuple[Counter[str], list[float], list[float], list[float]]:
    causes: Counter[str] = Counter()
    nearest_proposals: list[float] = []
    positive_scores: list[float] = []
    nearest_decoded: list[float] = []
    coordinates = proposals.coordinates.detach().cpu().numpy()
    above = np.flatnonzero(output[:, 0] >= THRESHOLD)
    decoded = {
        int(index): (
            float(coordinates[index, 0] + output[index, 1] * v24.STRIDE),
            float(coordinates[index, 1] + output[index, 2] * v24.STRIDE),
        )
        for index in above
    }
    for truth_index, (truth_x, truth_y) in enumerate(scene.centers):
        if truth_index in used_truths:
            continue
        distances = np.hypot(coordinates[:, 0] - truth_x, coordinates[:, 1] - truth_y)
        nearest_proposals.append(float(distances.min(initial=math.inf)))
        positives = np.flatnonzero(distances <= POSITIVE_PROPOSAL_RADIUS_PX)
        if len(positives):
            positive_scores.append(float(output[positives, 0].max()))
        decoded_distances = {
            index: math.hypot(x - truth_x, y - truth_y)
            for index, (x, y) in decoded.items()
        }
        if decoded_distances:
            nearest_decoded.append(min(decoded_distances.values()))
        raw_near = [index for index, distance in decoded_distances.items() if distance <= 5]
        eligible_near = [
            index for index in raw_near
            if domain is None or domain.contains(*decoded[index])
        ]
        consensus_near = [
            index for index in eligible_near if _consensus(scene, *decoded[index])
        ]
        final_near = [
            item for item in predictions
            if math.hypot(item.prediction.x - truth_x, item.prediction.y - truth_y) <= 5
        ]
        if consensus_near:
            causes["greedy_assignment_competition" if final_near else "nms_suppression"] += 1
        elif eligible_near:
            causes["consensus_rejection"] += 1
        elif raw_near:
            causes["decoded_outside_plot_domain"] += 1
        elif not np.any(distances <= MATCH_TOLERANCE_PX):
            causes["proposal_unavailable_within_5px"] += 1
        elif not len(positives):
            causes["proposal_available_only_3_to_5px"] += 1
        elif not np.any(output[positives, 0] >= THRESHOLD):
            causes["classification_below_threshold"] += 1
        else:
            causes["offset_error"] += 1
    return causes, nearest_proposals, positive_scores, nearest_decoded


def summarize_radius_and_nms(
    scene: Any,
    trace: PostprocessTrace,
    match_pairs: Sequence[tuple[int, int]],
    accumulator: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Attribute radius strata and unmatched truths without changing postprocessing."""

    values = accumulator if accumulator is not None else _new_radius_nms_accumulator()
    radii = _truth_radii(scene)
    truth_outcomes: Counter[str] = values["truth_outcomes"]
    prediction_outcomes: Counter[str] = values["prediction_outcomes"]
    matched_by_prediction = {prediction: truth for prediction, truth in match_pairs}
    matched_truths = {truth for _, truth in match_pairs}
    for truth_index, radius in enumerate(radii):
        outcome = "true_positive" if truth_index in matched_truths else "false_negative"
        truth_outcomes[f"{outcome}|{radius_bin(radius)}"] += 1

    for prediction_index, candidate in enumerate(trace.accepted):
        truth_index = matched_by_prediction.get(prediction_index)
        if truth_index is None:
            truth_index = min(
                range(len(scene.centers)),
                key=lambda index: math.hypot(
                    candidate.prediction.x - scene.centers[index][0],
                    candidate.prediction.y - scene.centers[index][1],
                ),
                default=None,
            )
            outcome = "false_positive"
        else:
            outcome = "true_positive"
        truth_bin = "no_ground_truth" if truth_index is None else radius_bin(radii[truth_index])
        prediction_outcomes[
            f"{outcome}|nearest_truth_{truth_bin}|{decoded_radius_bin(candidate.raw_radius)}"
        ] += 1

    suppressed_by_index = {
        item.suppressed.source_proposal_index: item for item in trace.suppressions
    }
    accepted_index = {
        item.source_proposal_index: index for index, item in enumerate(trace.accepted)
    }
    nms_counts: Counter[str] = values["nms_counts"]
    truth_radius_counts: Counter[str] = values["truth_radius_counts"]
    suppressed_radius_counts: Counter[str] = values["suppressed_radius_counts"]
    suppressor_radius_counts: Counter[str] = values["suppressor_radius_counts"]
    suppressor_outcomes: Counter[str] = values["suppressor_outcomes"]
    lowest_errors: list[float] = values["lowest_errors"]
    highest_errors: list[float] = values["highest_errors"]
    suppressor_errors: list[float] = values["suppressor_errors"]
    confidence_advantages: list[float] = values["confidence_advantages"]
    error_penalties: list[float] = values["error_penalties"]
    suppression_center_distances: list[float] = values["suppression_center_distances"]
    suppression_exclusion_distances: list[float] = values["suppression_exclusion_distances"]

    for truth_index, truth in enumerate(scene.centers):
        if truth_index in matched_truths:
            continue
        near = [
            candidate for candidate in trace.candidates_before_nms
            if math.hypot(candidate.prediction.x - truth[0], candidate.prediction.y - truth[1])
            <= MATCH_TOLERANCE_PX
        ]
        if not near or any(candidate.source_proposal_index in accepted_index for candidate in near):
            continue
        if any(candidate.source_proposal_index not in suppressed_by_index for candidate in near):
            raise RuntimeError("NMS attribution lost an eligible unmatched candidate")
        lowest = min(
            near,
            key=lambda candidate: (
                math.hypot(candidate.prediction.x - truth[0], candidate.prediction.y - truth[1]),
                -candidate.prediction.confidence,
                candidate.source_proposal_index,
            ),
        )
        highest = min(
            near,
            key=lambda candidate: (
                -candidate.prediction.confidence,
                math.hypot(candidate.prediction.x - truth[0], candidate.prediction.y - truth[1]),
                candidate.source_proposal_index,
            ),
        )
        selected = lowest
        suppression = suppressed_by_index[selected.source_proposal_index]
        suppressor = suppression.suppressor
        lowest_error = math.hypot(lowest.prediction.x - truth[0], lowest.prediction.y - truth[1])
        highest_error = math.hypot(highest.prediction.x - truth[0], highest.prediction.y - truth[1])
        suppressor_error = math.hypot(
            suppressor.prediction.x - truth[0], suppressor.prediction.y - truth[1]
        )
        suppressor_prediction_index = accepted_index[suppressor.source_proposal_index]

        nms_counts["false_negative_truths"] += 1
        nms_counts["lowest_error_candidate_suppressed"] += 1
        if highest.source_proposal_index in suppressed_by_index:
            nms_counts["highest_confidence_candidate_suppressed"] += 1
        if highest.source_proposal_index == lowest.source_proposal_index:
            nms_counts["highest_confidence_is_lowest_error"] += 1
        elif (
            suppressed_by_index[highest.source_proposal_index].suppressor.source_proposal_index
            == suppressor.source_proposal_index
        ):
            nms_counts["highest_and_lowest_share_actual_suppressor"] += 1
        truth_radius_counts[radius_bin(radii[truth_index])] += 1
        suppressed_radius_counts[decoded_radius_bin(selected.raw_radius)] += 1
        suppressor_radius_counts[decoded_radius_bin(suppressor.raw_radius)] += 1
        suppressor_outcomes[
            "true_positive" if suppressor_prediction_index in matched_by_prediction else "false_positive"
        ] += 1
        lowest_errors.append(lowest_error)
        highest_errors.append(highest_error)
        suppressor_errors.append(suppressor_error)
        confidence_advantages.append(
            suppressor.prediction.confidence - selected.prediction.confidence
        )
        error_penalties.append(suppressor_error - lowest_error)
        suppression_center_distances.append(suppression.center_distance)
        suppression_exclusion_distances.append(suppression.exclusion_distance)

    return _radius_nms_report(values)


def _new_radius_nms_accumulator() -> dict[str, Any]:
    return {
        "truth_outcomes": Counter(),
        "prediction_outcomes": Counter(),
        "nms_counts": Counter(),
        "truth_radius_counts": Counter(),
        "suppressed_radius_counts": Counter(),
        "suppressor_radius_counts": Counter(),
        "suppressor_outcomes": Counter(),
        "lowest_errors": [],
        "highest_errors": [],
        "suppressor_errors": [],
        "confidence_advantages": [],
        "error_penalties": [],
        "suppression_center_distances": [],
        "suppression_exclusion_distances": [],
    }


def _radius_nms_report(values: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "truth_outcomes_by_ground_truth_radius": dict(sorted(values["truth_outcomes"].items())),
        "prediction_outcomes_by_nearest_truth_and_decoded_radius": dict(
            sorted(values["prediction_outcomes"].items())
        ),
        "nms_false_negatives": {
            "counts": dict(sorted(values["nms_counts"].items())),
            "ground_truth_radius_bins": dict(sorted(values["truth_radius_counts"].items())),
            "lowest_error_candidate_raw_radius_bins": dict(sorted(values["suppressed_radius_counts"].items())),
            "actual_suppressor_raw_radius_bins": dict(sorted(values["suppressor_radius_counts"].items())),
            "actual_suppressor_prediction_outcome": dict(sorted(values["suppressor_outcomes"].items())),
            "lowest_error_candidate_distance_px": _quantiles(values["lowest_errors"]),
            "highest_confidence_candidate_distance_px": _quantiles(values["highest_errors"]),
            "actual_suppressor_distance_to_truth_px": _quantiles(values["suppressor_errors"]),
            "actual_suppressor_confidence_advantage": _quantiles(values["confidence_advantages"]),
            "actual_suppressor_error_penalty_px": _quantiles(values["error_penalties"]),
            "suppressed_to_actual_suppressor_distance_px": _quantiles(
                values["suppression_center_distances"]
            ),
            "applicable_nms_exclusion_distance_px": _quantiles(
                values["suppression_exclusion_distances"]
            ),
        },
    }


def _record_primitive(
    record: Mapping[str, Any], category: str, crop_x: int, crop_y: int
) -> Primitive | None:
    if record.get("visible") is False or record.get("drawn") is False:
        return None
    kind = str(record.get("kind", record.get("role", category)))
    raw_box = record.get("rendered_pixel_box", record.get("box"))
    if isinstance(raw_box, Sequence) and not isinstance(raw_box, (str, bytes)) and len(raw_box) == 4:
        try:
            left, top, width, height = (float(value) for value in raw_box)
        except (TypeError, ValueError):
            return None
        if width < 0 or height < 0 or not all(math.isfinite(v) for v in (left, top, width, height)):
            return None
        points = ((left, top), (left + width, top), (left + width, top + height), (left, top + height))
        return Primitive(category, kind, tuple((x - crop_x, y - crop_y) for x, y in points), True)
    for key, closed in (("line", False), ("polygon", True)):
        raw = record.get(key)
        if isinstance(raw, Sequence) and not isinstance(raw, (str, bytes)):
            try:
                points = tuple((float(item[0]), float(item[1])) for item in raw)
            except (IndexError, TypeError, ValueError):
                continue
            if len(points) >= (3 if closed else 2) and all(math.isfinite(v) for p in points for v in p):
                return Primitive(category, kind, tuple((x - crop_x, y - crop_y) for x, y in points), closed)
    raw_center = record.get("center")
    if isinstance(raw_center, Sequence) and not isinstance(raw_center, (str, bytes)) and len(raw_center) == 2:
        try:
            x, y = float(raw_center[0]), float(raw_center[1])
        except (TypeError, ValueError):
            return None
        if math.isfinite(x) and math.isfinite(y):
            return Primitive(category, kind, ((x - crop_x, y - crop_y),), False)
    return None


def _annotation_primitives(
    annotation: Mapping[str, Any], crop: tuple[int, int, int, int]
) -> tuple[Primitive, ...]:
    keys = {
        "text": "texts", "axis": "axes", "tick": "ticks", "divider": "dividers",
        "connector": "edges", "marker": "markers", "legend": "legends",
        "arrow": "arrows", "bracket": "brackets", "top_bar": "top_bars",
    }
    values: list[Primitive] = []
    for category, key in keys.items():
        for record in _records(annotation, key):
            item = _record_primitive(record, category, crop[0], crop[1])
            if item is not None:
                values.append(item)
    return tuple(values)


def _hard_negative_primitives(scene: Any) -> tuple[Primitive, ...]:
    grouped = {
        "text": "text",
        "ocr_heavy": "text",
        "axis": "axis",
        "line_intersection": "connector",
        "faint_line": "connector",
        "topology_junction": "connector",
        "topology_fragment": "connector",
        "sparse_fragment": "connector",
    }
    return tuple(
        Primitive(grouped.get(kind, "other"), kind, ((float(x), float(y)),), False)
        for kind, x, y in scene.hard_negatives
    )


def _mask_points(mask: torch.Tensor) -> np.ndarray:
    values = torch.nonzero(mask >= MASK_PRESENT_MINIMUM, as_tuple=False).cpu().numpy()
    return values[:, (1, 0)].astype(np.float64, copy=False) if len(values) else np.empty((0, 2))


def _point_set_distance(points: np.ndarray, point: tuple[float, float]) -> float:
    if not len(points):
        return math.inf
    return float(np.hypot(points[:, 0] - point[0], points[:, 1] - point[1]).min())


def _infer(session: ort.InferenceSession, patches: torch.Tensor) -> np.ndarray:
    parts = []
    for start in range(0, len(patches), INFERENCE_BATCH):
        value = patches[start:start + INFERENCE_BATCH].contiguous().cpu().numpy()
        parts.append(session.run(["candidate_predictions"], {"candidate_patches": value})[0])
    return np.concatenate(parts, axis=0) if parts else np.empty((0, 4), dtype=np.float32)


def _diagnose_split(
    name: str,
    bound_scenes: Sequence[Any],
    session: ort.InferenceSession,
    annotations: Mapping[str, Mapping[str, Any]] | None,
) -> dict[str, Any]:
    totals: Counter[str] = Counter()
    fp_categories: Counter[str] = Counter()
    fp_category_bins: dict[str, Counter[str]] = defaultdict(Counter)
    fp_truth_bins: Counter[str] = Counter()
    fp_ocr_bins: Counter[str] = Counter()
    fp_artifact_bins: Counter[str] = Counter()
    fp_confidences: list[float] = []
    fn_causes: Counter[str] = Counter()
    fn_proposals: list[float] = []
    fn_scores: list[float] = []
    fn_decoded: list[float] = []
    stage_counts: Counter[str] = Counter()
    radius_nms = _new_radius_nms_accumulator()
    forward_ms = 0.0

    for bound in bound_scenes:
        scene = bound.scene if hasattr(bound, "scene") else bound
        domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
        if domain is None:
            proposals = v24.extract_proposals(scene.tensor)
            omitted = 0
        else:
            batch = extract_proposals_in_domain(scene.tensor, domain)
            proposals = batch.proposals
            omitted = batch.omitted_by_domain_count
        started = time.perf_counter()
        output = _infer(session, proposals.patches)
        forward_ms += (time.perf_counter() - started) * 1000.0
        trace = _postprocess_trace(scene, proposals, output, domain)
        predictions = trace.accepted
        match_pairs = greedy_match_pairs(predictions, scene.centers)
        used_predictions = {prediction for prediction, _ in match_pairs}
        used_truths = {truth for _, truth in match_pairs}
        summarize_radius_and_nms(scene, trace, match_pairs, radius_nms)
        totals.update({
            "scene_count": 1,
            "truth_count": len(scene.centers),
            "proposal_count": len(proposals.coordinates),
            "domain_omitted_ink_proposals": omitted,
            "prediction_count": len(predictions),
            "true_positive": len(used_predictions),
            "false_positive": len(predictions) - len(used_predictions),
            "false_negative": len(scene.centers) - len(used_truths),
        })
        above = np.flatnonzero(output[:, 0] >= THRESHOLD)
        stage_counts["outputs_above_threshold"] += len(above)
        stage_counts["nms_eligible_candidates"] += len(trace.candidates_before_nms)
        stage_counts["nms_suppressed_candidates"] += len(trace.suppressions)
        for index in above:
            base_x, base_y = proposals.coordinates[index].tolist()
            x = float(base_x + output[index, 1] * v24.STRIDE)
            y = float(base_y + output[index, 2] * v24.STRIDE)
            if domain is not None and not domain.contains(x, y):
                stage_counts["decoded_outside_plot"] += 1

        causes, nearest, scores, decoded = categorize_missed_truths(
            scene, proposals, output, predictions, used_truths, domain
        )
        fn_causes.update(causes)
        fn_proposals.extend(nearest)
        fn_scores.extend(scores)
        fn_decoded.extend(decoded)
        primitives = (
            _annotation_primitives(annotations[scene.source_sha256], scene.crop)
            if annotations is not None else _hard_negative_primitives(scene)
        )
        ocr_points = _mask_points(scene.tensor[1])
        artifact_points = _mask_points(scene.tensor[2])
        for prediction_index, candidate in enumerate(predictions):
            if prediction_index in used_predictions:
                continue
            point = (candidate.prediction.x, candidate.prediction.y)
            fp_confidences.append(candidate.prediction.confidence)
            nearest_truth = min(
                (math.hypot(point[0] - x, point[1] - y) for x, y in scene.centers),
                default=math.inf,
            )
            fp_truth_bins[distance_bin(nearest_truth)] += 1
            nearest_item = nearest_primitive(point, primitives)
            if nearest_item is not None:
                category, _, distance = nearest_item
                fp_categories[category] += 1
                fp_category_bins[category][distance_bin(distance)] += 1
            fp_ocr_bins[distance_bin(_point_set_distance(ocr_points, point))] += 1
            fp_artifact_bins[distance_bin(_point_set_distance(artifact_points, point))] += 1

    expected = EXPECTED[name]
    for actual_key, expected_key in (
        ("scene_count", "scenes"), ("truth_count", "truth"),
        ("true_positive", "tp"), ("false_positive", "fp"), ("false_negative", "fn"),
    ):
        _require(totals[actual_key] == expected[expected_key], f"{name} {actual_key} drifted")
    _require(sum(fn_causes.values()) == totals["false_negative"], f"{name} FN attribution incomplete")
    _require(sum(fp_categories.values()) == totals["false_positive"], f"{name} FP attribution incomplete")
    _require(
        sum(radius_nms["truth_outcomes"].values()) == totals["truth_count"]
        and sum(radius_nms["prediction_outcomes"].values()) == totals["prediction_count"],
        f"{name} radius attribution incomplete",
    )
    _require(
        radius_nms["nms_counts"]["false_negative_truths"]
        == fn_causes["nms_suppression"],
        f"{name} NMS suppressor attribution differs from the fixed postprocessor",
    )
    tp, fp, truth = totals["true_positive"], totals["false_positive"], totals["truth_count"]
    return {
        "counts": dict(sorted(totals.items())),
        "precision": tp / (tp + fp),
        "recall": tp / truth,
        "stage_counts": dict(sorted(stage_counts.items())),
        "false_positive_nearest_truth_distance_bins_px": dict(sorted(fp_truth_bins.items())),
        "false_positive_nearest_descriptive_category": dict(sorted(fp_categories.items())),
        "false_positive_descriptive_distance_bins_px": {
            key: dict(sorted(value.items())) for key, value in sorted(fp_category_bins.items())
        },
        "false_positive_ocr_mask_distance_bins_px": dict(sorted(fp_ocr_bins.items())),
        "false_positive_artifact_mask_distance_bins_px": dict(sorted(fp_artifact_bins.items())),
        "false_positive_confidence": _quantiles(fp_confidences),
        "false_negative_cause": dict(sorted(fn_causes.items())),
        "false_negative_nearest_proposal_distance_px": _quantiles(fn_proposals),
        "false_negative_maximum_positive_proposal_probability": _quantiles(fn_scores),
        "false_negative_nearest_above_threshold_decoded_distance_px": _quantiles(fn_decoded),
        "radius_and_nms_attribution": _radius_nms_report(radius_nms),
        "model_forward_ms": round(forward_ms, 3),
    }


def _family_annotations(domains: Any, scenes: Sequence[Any]) -> dict[str, Mapping[str, Any]]:
    selected_by_seed: dict[int, Mapping[str, Any]] = {}
    for dataset_seed in domains.profile.dev.dataset_seeds:
        selected_by_seed.update({
            int(scene["seed"]): scene
            for scene in _build_scenes(PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True)
            if _scene_split(scene) == "validation"
        })
    annotations: dict[str, Mapping[str, Any]] = {}
    for bound in scenes:
        scene = selected_by_seed.get(bound.scene.seed)
        _require(scene is not None, "family diagnostic scene seed is not in the frozen profile")
        result = runtime_domain_binding_v3.visible_content_v3.render_visible_content_source(scene)
        source_sha = sha256(png_bytes(result.image.convert("RGB"))).hexdigest()
        _require(source_sha == bound.scene.source_sha256, "family annotation regeneration changed source bytes")
        existing = annotations.setdefault(source_sha, result.annotation)
        _require(existing == result.annotation, "family source annotation regeneration is unstable")
    return annotations


def run(*, repository_root: Path = REPOSITORY_ROOT, output_path: Path = OUTPUT_PATH) -> dict[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    paths = {
        "result": root / RESULT_PATH,
        "config": root / CONFIG_PATH,
        "candidate_report": root / CANDIDATE_REPORT_PATH,
        "onnx": root / ONNX_PATH,
        "binding": root / protocol.FAMILY_BINDING_PATH,
        "prior_diagnosis": root / PRIOR_DIAGNOSIS_PATH,
    }
    expected_hashes = {
        "result": EXPECTED_RESULT_SHA256,
        "config": EXPECTED_CONFIG_SHA256,
        "candidate_report": EXPECTED_REPORT_SHA256,
        "onnx": EXPECTED_ONNX_SHA256,
        "binding": protocol.FAMILY_BINDING_SHA256,
        "prior_diagnosis": EXPECTED_PRIOR_DIAGNOSIS_SHA256,
    }
    actual_hashes = {key: _sha(path) for key, path in paths.items()}
    _require(actual_hashes == expected_hashes, "frozen V25 diagnosis input identity changed")
    result = _json(paths["result"])
    config = _json(paths["config"])
    candidate_report = _json(paths["candidate_report"])
    _require(result.get("candidate_report_sha256") == EXPECTED_REPORT_SHA256, "outcome points to another report")
    _require(candidate_report.get("candidate_config_sha256") == EXPECTED_CONFIG_SHA256, "report points to another config")
    _require(candidate_report.get("onnx_sha256") == EXPECTED_ONNX_SHA256, "report points to another ONNX")
    _require(config.get("confidence_threshold") == THRESHOLD, "operating threshold changed")
    runner_bundle = source_bundle_sha256(root, train_p1.RUNNER_SOURCE_PATHS)
    _require(runner_bundle == config.get("expected_runner_source_bundle_sha256"), "frozen runner source bundle changed")

    domains = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
        paths["binding"], expected_hashes["binding"], repository_root=root
    )
    joined = runtime_domain_binding_v3.join_runtime_domain_truth_v3(domains, repository_root=root)
    component = build_split("dev", independent_layout=True)
    _require(len(component) == 167 and sum(len(scene.centers) for scene in component) == 2004,
             "component dev denominator changed")
    _require(len(joined.dev) == 9 and sum(len(item.scene.centers) for item in joined.dev) == 206,
             "family dev denominator changed")
    annotations = _family_annotations(domains, joined.dev)
    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session = ort.InferenceSession(str(paths["onnx"]), sess_options=options, providers=["CPUExecutionProvider"])
    _require(session.get_providers()[0] == "CPUExecutionProvider", "diagnostic did not select CPUExecutionProvider")

    component_result = _diagnose_split("component", component, session, None)
    family_result = _diagnose_split("family", joined.dev, session, annotations)
    report = {
        "schema": "graphreader.marker-center-plot-domain-v25-dev-diagnosis.v2",
        "scope": "synthetic-dev-fixed-operating-point-diagnosis",
        "candidate_id": "P1",
        "operating_threshold": THRESHOLD,
        "inputs": {
            **{f"{key}_path": path.relative_to(root).as_posix() for key, path in paths.items()},
            **{f"{key}_sha256": value for key, value in actual_hashes.items()},
            "runner_source_bundle_sha256": runner_bundle,
            "diagnostic_source_sha256": _sha(Path(__file__)),
        },
        "component_dev": component_result,
        "family_dev": family_result,
        "interpretation_limits": [
            "Attribution uses synthetic annotations only after model input validation; annotations never enter inference.",
            "Nearest descriptive geometry is diagnostic proximity, not proof that a structure caused a prediction.",
            "Component fixtures expose named hard-negative centers rather than full semantic geometry.",
            "False-positive radius strata use the nearest ground-truth marker only as a descriptive reference; they do not assign that prediction to the marker.",
            "NMS attribution replays the fixed confidence order and records the first accepted prediction that actually suppresses each eligible candidate.",
            "No threshold, candidate, architecture, private, sealed, or production decision is made by this report.",
        ],
        "synthetic_only": True,
        "private_reads": 0,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
    destination = output_path if output_path.is_absolute() else root / output_path
    destination.parent.mkdir(parents=True, exist_ok=False)
    destination.write_bytes(canonical_json_bytes(report))
    return report


def main() -> None:
    report = run()
    print(json.dumps({
        "status": "complete",
        "component": report["component_dev"]["counts"],
        "family": report["family_dev"]["counts"],
        "elapsed_ms": report["elapsed_ms"],
        "report_sha256": _sha(REPOSITORY_ROOT / OUTPUT_PATH),
    }, sort_keys=True))


if __name__ == "__main__":
    main()
