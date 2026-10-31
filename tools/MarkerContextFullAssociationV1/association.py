# SPDX-License-Identifier: Apache-2.0
"""Pure, review-only 2 px association for authenticated v5 full-train captures."""
from collections import Counter
import math

TOLERANCE_PX = 2.0


def _point(point, label):
    if not isinstance(point, (list, tuple)) or len(point) != 2:
        raise ValueError(f"{label} must be a 2D point")
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in point):
        raise ValueError(f"{label} must contain finite coordinates")
    return float(point[0]), float(point[1])


def transform_panel_to_source(point, matrix, source_width, source_height):
    """Apply a validated row-major affine panel->source 3x3 matrix."""
    x, y = _point(point, "candidate center")
    if not isinstance(matrix, (list, tuple)) or len(matrix) != 9:
        raise ValueError("panel_to_source_matrix must have nine values")
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in matrix):
        raise ValueError("panel_to_source_matrix must be finite")
    m = [float(v) for v in matrix]
    if abs(m[6]) > 1e-12 or abs(m[7]) > 1e-12 or abs(m[8] - 1.0) > 1e-12:
        raise ValueError("Only affine matrices are supported")
    sx, sy = m[0] * x + m[1] * y + m[2], m[3] * x + m[4] * y + m[5]
    if not (0 <= sx < source_width and 0 <= sy < source_height):
        raise ValueError("transformed candidate center falls outside source pixels")
    return sx, sy


def _distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def associate_source(source_id, capture_status, panel_status_counts, candidates, truth_markers, overlays,
                    failure_type_counts=None):
    """Return aggregate counts only. No coordinates/labels leave this function."""
    if capture_status not in ("completed", "failed"):
        raise ValueError("invalid capture source status")
    if any(type(panel_status_counts.get(k)) is not int or panel_status_counts[k] < 0
           for k in ("completed", "failed", "not_processed")):
        raise ValueError("invalid panel accounting")
    if sum(panel_status_counts.values()) == 0 and capture_status == "completed":
        raise ValueError("completed source has no accounted panels")
    if capture_status == "completed" and (panel_status_counts["failed"] or panel_status_counts["not_processed"]):
        raise ValueError("source status conflicts with incomplete panels")
    if capture_status == "failed" and sum(panel_status_counts.values()) > 0 and not (panel_status_counts["failed"] or panel_status_counts["not_processed"]):
        raise ValueError("failed source has no failed or unprocessed panel")

    candidate_ids = [c.get("candidate_id") for c in candidates]
    if any(not isinstance(v, str) or not v for v in candidate_ids) or len(set(candidate_ids)) != len(candidate_ids):
        raise ValueError("candidate identities must be unique")
    truth_ids = [m.get("truth_id") for m in truth_markers]
    if any(not isinstance(v, str) or not v for v in truth_ids) or len(set(truth_ids)) != len(truth_ids):
        raise ValueError("truth identities must be unique")
    truth_points = []
    for marker in truth_markers:
        if marker.get("connection") not in ("connected", "isolated"):
            raise ValueError("unsupported truth marker class")
        truth_points.append(_point(marker.get("center"), "truth center"))
    overlay_points = [_point(overlay.get("center"), "overlay center") for overlay in overlays]

    candidate_points = []
    for candidate in candidates:
        candidate_points.append(_point(candidate.get("center"), "candidate center"))
        if type(candidate.get("symbol_input")) is not bool:
            raise ValueError("symbol_input flag must be explicit")
        if type(candidate.get("detector_candidate")) is not bool:
            raise ValueError("detector_candidate flag must be explicit")

    overlay_candidates = set()
    for i, candidate in enumerate(candidates):
        if candidate["symbol_input"] or any(_distance(candidate_points[i], p) <= TOLERANCE_PX for p in overlay_points):
            overlay_candidates.add(i)

    duplicates = set()
    by_point = {}
    for i, point in enumerate(candidate_points):
        by_point.setdefault(point, []).append(i)
    for indices in by_point.values():
        if len(indices) > 1:
            duplicates.update(indices)

    eligible = [i for i in range(len(candidates)) if i not in overlay_candidates and i not in duplicates]
    links_by_candidate, links_by_truth = {}, {}
    for i in eligible:
        for j, point in enumerate(truth_points):
            if _distance(candidate_points[i], point) <= TOLERANCE_PX:
                links_by_candidate.setdefault(i, []).append(j)
                links_by_truth.setdefault(j, []).append(i)

    candidate_status = {}
    for i, candidate in enumerate(candidates):
        if i in overlay_candidates:
            status = "review_overlay"
        elif i in duplicates:
            status = "review_duplicate"
        elif len(links_by_candidate.get(i, [])) > 1 or any(len(links_by_truth[j]) > 1 for j in links_by_candidate.get(i, [])):
            status = "review_ambiguous"
        elif len(links_by_candidate.get(i, [])) == 1:
            truth = truth_markers[links_by_candidate[i][0]]
            status = "review_connected_diagnostic" if truth["connection"] == "connected" else "review_isolated"
        else:
            status = "review_unmatched"
        candidate_status[i] = status

    truth_status = {}
    for j, truth in enumerate(truth_markers):
        linked = links_by_truth.get(j, [])
        if len(linked) > 1 or (linked and len(links_by_candidate.get(linked[0], [])) > 1):
            status = "review_ambiguous"
        elif len(linked) == 1:
            candidate_i = linked[0]
            if candidate_status[candidate_i] == "review_connected_diagnostic":
                status = "matched_connected_review_only"
            elif candidate_status[candidate_i] == "review_isolated":
                status = "matched_isolated_review_only"
            else:
                status = "review_ambiguous"
        elif any(_distance(truth_points[j], p) <= TOLERANCE_PX for p in overlay_points):
            status = "review_overlay_conflict"
        elif capture_status != "completed":
            status = "not_observed_capture_incomplete"
        else:
            status = "unmatched_review_only"
        truth_status[j] = status

    candidate_counts = Counter(candidate_status.values())
    truth_counts = Counter(truth_status.values())
    failures = failure_type_counts or {}
    if not isinstance(failures, dict) or any(not isinstance(k, str) or type(v) is not int or v < 0
                                               for k, v in failures.items()):
        raise ValueError("invalid failure type counts")
    truth_by_connection = {kind: Counter() for kind in ("connected", "isolated")}
    for index, marker in enumerate(truth_markers):
        truth_by_connection[marker["connection"]][truth_status[index]] += 1
    return {
        "source_id": source_id,
        "capture_status": capture_status,
        "panel_status_counts": dict(panel_status_counts),
        "failure_type_counts": {key: failures[key] for key in sorted(failures)},
        "candidate_count": len(candidates),
        "detector_candidate_count": sum(c["detector_candidate"] for c in candidates),
        "symbol_input_candidate_count": sum(c["symbol_input"] for c in candidates),
        "truth_marker_count": len(truth_markers),
        "overlay_count": len(overlays),
        "candidate_status_counts": {key: candidate_counts[key] for key in sorted(candidate_counts)},
        "truth_status_counts": {key: truth_counts[key] for key in sorted(truth_counts)},
        "truth_status_counts_by_connection": {
            kind: {key: counts[key] for key in sorted(counts)}
            for kind, counts in truth_by_connection.items()
        },
        "negative_training_rows": 0,
        "trainable_rows": 0,
    }


def associate_full(capture_sources, truth_by_source):
    """Aggregate exactly the fixed 64-source train inventory, preserving failures."""
    if len(capture_sources) != 64 or len(truth_by_source) != 64:
        raise ValueError("expected exactly 64 full-train sources")
    source_ids = [s.get("source_id") for s in capture_sources]
    if len(set(source_ids)) != 64 or set(source_ids) != set(truth_by_source):
        raise ValueError("capture/truth source identities differ")
    results = []
    for source in capture_sources:
        truth = truth_by_source[source["source_id"]]
        results.append(associate_source(source["source_id"], source["capture_status"],
            source["panel_status_counts"], source["candidates"], truth["markers"], truth["overlays"],
            source.get("failure_type_counts", {})))
    candidate_totals, truth_totals, panel_totals, failure_totals = Counter(), Counter(), Counter(), Counter()
    truth_by_connection = {kind: Counter() for kind in ("connected", "isolated")}
    for row in results:
        candidate_totals.update(row["candidate_status_counts"])
        truth_totals.update(row["truth_status_counts"])
        for kind in truth_by_connection:
            truth_by_connection[kind].update(row["truth_status_counts_by_connection"][kind])
        panel_totals.update(row["panel_status_counts"])
        failure_totals.update(row["failure_type_counts"])
    failed_sources = sum(row["capture_status"] != "completed" for row in results)
    return {
        "schema": "graphreader.marker-context-full-train-association-diagnostic.v1",
        "source_count": 64,
        "completed_source_count": 64 - failed_sources,
        "failed_source_count": failed_sources,
        "panel_status_counts": {key: panel_totals[key] for key in ("completed", "failed", "not_processed")},
        "failure_type_counts": {key: failure_totals[key] for key in sorted(failure_totals)},
        "candidate_status_counts": {key: candidate_totals[key] for key in sorted(candidate_totals)},
        "truth_status_counts": {key: truth_totals[key] for key in sorted(truth_totals)},
        "truth_status_counts_by_connection": {
            kind: {key: counts[key] for key in sorted(counts)}
            for kind, counts in truth_by_connection.items()
        },
        "candidate_count": sum(row["candidate_count"] for row in results),
        "truth_marker_count": sum(row["truth_marker_count"] for row in results),
        "overlay_count": sum(row["overlay_count"] for row in results),
        "sources": results,
        "match_tolerance_px": TOLERANCE_PX,
        "match_metric": "euclidean_original_source_pixels_inclusive",
        "negative_training_rows": 0,
        "trainable_rows": 0,
        "training_approved": False,
        "optimizer_steps": 0,
        "truth_match_accuracy_guaranteed": False,
    }
