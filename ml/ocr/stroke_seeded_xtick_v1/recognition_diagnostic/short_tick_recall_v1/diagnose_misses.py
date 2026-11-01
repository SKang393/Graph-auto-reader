# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reproduce the truth-blind short-ink scan and attribute consumed-probe misses."""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
from pathlib import Path
import json
import math
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
PROBE_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json"
SCANNER_PROPOSALS_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/scanner_proposals.json"
PAIRED_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/report.corrected.json"
BASELINE_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-filter-v1/report.json"
MATERIALIZER_REL = "ml/ocr/stroke_seeded_xtick_v1/materialize.py"
AXIS_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py"
TRUTH_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py"
PROBE_MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
SCANNER_PROPOSALS_SHA = "0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081"
PAIRED_REPORT_SHA = "51d716225552ec7e3412c373c858117e13a7e79666dbd061a24af0ecbe724b59"
BASELINE_REPORT_SHA = "3a81674fa9655e96c298f4094105b8c6d41d5f0991428f26e6bdd4ded3b7a5ef"
MATERIALIZER_SHA = "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d"
AXIS_HELPER_SHA = "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de"
TRUTH_HELPER_SHA = "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979"
SCANNER_SOURCE_SHA = "40b104c23f0b60ca029dbe596dbd46117211d4f32b13837679380e4da47a8780"
SCANNER_PROJECT_SHA = "7a6f7599b67f6e09425cc8168c894e0f2263f66e91bb2c254f593409a5ec3632"
SCAN_RADIUS = 12
AXIS_CORE_EXCLUSION = 2
EDGE_GUARD = 4
MAX_CLUSTER_WIDTH = 8
MAX_INK_GRAY = 200
OUTPUT_REL = "artifacts/stroke-seeded-xtick-short-tick-recall-v1"
COMPARISON_OUTPUT_REL = "artifacts/stroke-seeded-xtick-short-tick-recall-v5"
VARIANT_CORE_EXCLUSION = 1
WIDE_CLUSTER_LIMIT = 1_000_000


class RecallDiagnosticError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def read_pinned(relative: str, expected: str, limit: int) -> bytes:
    path = ROOT / relative
    if path.is_symlink() or not path.resolve(strict=True).is_relative_to(ROOT.resolve()):
        raise RecallDiagnosticError(f"unsafe or external input path: {relative}")
    data = path.read_bytes()
    if len(data) > limit or digest(data) != expected:
        raise RecallDiagnosticError(f"pinned input hash/size mismatch: {relative}")
    return data


def _round(value: float) -> int:
    # C# Math.Round(double) and Python round both use ties-to-even.
    return int(round(value))


def scan(frame: np.ndarray, axis: list[float], *, radius: int = SCAN_RADIUS,
         core_exclusion: int = AXIS_CORE_EXCLUSION, edge_guard: int = EDGE_GUARD,
         max_cluster_width: int = MAX_CLUSTER_WIDTH, maximum_ink_gray: int = MAX_INK_GRAY) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Pixel-level translation of frozen C# Scan, returning proposals and diagnostic clusters."""
    sx, sy, ex, ey = (float(v) for v in axis)
    dx, dy = ex - sx, ey - sy
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 2 * edge_guard:
        return [], []
    ux, uy = dx / length, dy / length
    if ux < 0:
        ux, uy = -ux, -uy
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    height, width = frame.shape
    hits = []
    for along in range(edge_guard, math.floor(length - edge_guard) + 1):
        dark_count = longest_run = run = 0
        previous_dark = False
        for offset in range(-radius, radius + 1):
            if abs(offset) <= core_exclusion:
                run, previous_dark = 0, False
                continue
            x = _round(sx + ux * along + nx * offset)
            y = _round(sy + uy * along + ny * offset)
            dark = 0 <= x < width and 0 <= y < height and int(frame[y, x]) < maximum_ink_gray
            if dark:
                dark_count += 1
                run = run + 1 if previous_dark else 1
                longest_run = max(longest_run, run)
            else:
                run = 0
            previous_dark = dark
        if dark_count >= 3 and longest_run >= 2:
            hits.append({"along": along, "dark_count": dark_count, "longest_run": longest_run})

    clusters: list[list[dict[str, int]]] = []
    for hit in hits:
        if not clusters or hit["along"] - clusters[-1][-1]["along"] > 1:
            clusters.append([])
        clusters[-1].append(hit)
    proposals = []
    cluster_diagnostics = []
    for cluster in clusters:
        cluster_width = cluster[-1]["along"] - cluster[0]["along"] + 1
        along = cluster[len(cluster) // 2]["along"]
        offsets = []
        for offset in range(-radius, radius + 1):
            if abs(offset) <= core_exclusion:
                continue
            x = _round(sx + ux * along + nx * offset)
            y = _round(sy + uy * along + ny * offset)
            if 0 <= x < width and 0 <= y < height and int(frame[y, x]) < maximum_ink_gray:
                offsets.append(offset)
        reason = None
        if cluster_width > max_cluster_width:
            reason = "cluster_width_over_limit"
        elif len(offsets) < 3:
            reason = "dark_offset_count_below_3"
        else:
            start_offset = min(0, min(offsets))
            end_offset = max(0, max(offsets))
            if end_offset - start_offset < 4:
                reason = "dark_offset_span_below_4"
            else:
                start = (sx + ux * along + nx * start_offset, sy + uy * along + ny * start_offset)
                end = (sx + ux * along + nx * end_offset, sy + uy * along + ny * end_offset)
                proposals.append({"center": [(start[0] + end[0]) / 2, (start[1] + end[1]) / 2],
                    "line": [*start, *end], "cluster_width_pixels": cluster_width,
                    "dark_pixel_count": sum(h["dark_count"] for h in cluster),
                    "longest_dark_run": max(h["longest_run"] for h in cluster)})
        cluster_diagnostics.append({"start_along": cluster[0]["along"], "end_along": cluster[-1]["along"],
            "cluster_width": cluster_width, "hit_count": len(cluster), "dark_offsets": offsets,
            "rejection": reason, "along": along})
    return proposals, {"hits": hits, "clusters": cluster_diagnostics}


def _line_projection(center: list[float], axis: list[float]) -> tuple[float, float, float, float]:
    sx, sy, ex, ey = (float(v) for v in axis)
    dx, dy = ex - sx, ey - sy
    length = math.hypot(dx, dy)
    ux, uy = dx / length, dy / length
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    vx, vy = center[0] - sx, center[1] - sy
    return vx * ux + vy * uy, vx * nx + vy * ny, length, math.hypot(dx, dy)


def metric_summary(panels: list[dict[str, Any]], key: str) -> dict[str, Any]:
    visible = sum(p["visible_count"] for p in panels)
    proposals = sum(p[key]["usable_detected_x_stroke_count"] for p in panels)
    matched = sum(p[key]["matched_count"] for p in panels)
    no_visible = [p for p in panels if p["visible_count"] == 0]
    no_visible_fp = sum(p[key]["usable_detected_x_stroke_count"] for p in no_visible)
    return {"visible_ticks": visible, "proposals": proposals, "matches": matched,
        "false_proposals": proposals - matched, "precision": matched / proposals if proposals else 0.0,
        "recall": matched / visible if visible else 0.0, "no_visible_panels": len(no_visible),
        "false_proposals_on_no_visible_panels": no_visible_fp,
        "no_visible_panels_with_proposal": sum(p[key]["usable_detected_x_stroke_count"] > 0 for p in no_visible)}


def run() -> dict[str, Any]:
    from ml.ocr.stroke_seeded_xtick_v1 import materialize
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as truth_helper

    probe = json.loads(read_pinned(PROBE_MANIFEST_REL, PROBE_MANIFEST_SHA, 10_000_000))
    raw = json.loads(read_pinned(SCANNER_PROPOSALS_REL, SCANNER_PROPOSALS_SHA, 5_000_000))
    paired = json.loads(read_pinned(PAIRED_REPORT_REL, PAIRED_REPORT_SHA, 2_000_000))
    prior = json.loads(read_pinned(BASELINE_REPORT_REL, BASELINE_REPORT_SHA, 2_000_000))
    source_hashes = {
        MATERIALIZER_REL: MATERIALIZER_SHA,
        AXIS_HELPER_REL: AXIS_HELPER_SHA,
        TRUTH_HELPER_REL: TRUTH_HELPER_SHA,
    }
    for relative, expected in source_hashes.items():
        if digest(read_pinned(relative, expected, 5_000_000)) != expected:
            raise RecallDiagnosticError(f"source identity changed: {relative}")
    if raw["probe_manifest_sha256"] != PROBE_MANIFEST_SHA:
        raise RecallDiagnosticError("raw scanner artifact targets another probe")
    if paired["scanner_identity"]["raw_proposal_sha256"] != SCANNER_PROPOSALS_SHA:
        raise RecallDiagnosticError("paired report does not bind retained raw scanner proposals")
    if paired["scanner_identity"]["binary_sha256"] != raw["scanner_binary_sha256"]:
        raise RecallDiagnosticError("scanner binary identity mismatch")

    raw_by_panel = {p["panel_id"]: p for s in raw["source_results"] for p in s["panels"]}
    baseline_rows: list[dict[str, Any]] = []
    panel_records = []
    miss_categories: Counter[str] = Counter()
    miss_cluster_attribution: Counter[str] = Counter()
    miss_examples = []
    panel_proposals: dict[str, list[dict[str, Any]]] = {}
    variant_proposals: dict[str, list[dict[str, Any]]] = {}
    wide_cluster_proposals: dict[str, list[dict[str, Any]]] = {}

    # Proposal generation is truth-blind. Re-render full panels and reproduce the frozen C# scan exactly.
    rendered_sources = {}
    for source in probe["sources"]:
        scene = materialize.build_train_scene(source["renderer_recipe"])
        image, rendered, _ = materialize.render_train_scene(scene)
        if digest(image.tobytes()) != source["source_pixel_sha256"]:
            raise RecallDiagnosticError(f"pinned probe source pixels differ: {source['source_id']}")
        rendered_sources[source["source_id"]] = rendered
        for panel in raw_by_panel.values():
            if panel["panel_id"] not in {p["panel_id"] for p in rendered["panels"]}:
                continue
            x0, y0, x1, y1 = (int(v) for v in panel["bounds"])
            crop = image.crop((x0, y0, x1, y1))
            # ProductionRasterFrameDecoder computes the project-defined BT.601
            # grayscale frame before cropping. Pillow's L conversion is also
            # used by the V24 synthetic input path; do not scan the RGB triplet.
            gray = np.asarray(crop.convert("L"), dtype=np.uint8)
            global_axis = panel["selected_x_axis_line"]
            local_axis = [global_axis[0] - x0, global_axis[1] - y0,
                          global_axis[2] - x0, global_axis[3] - y0]
            local_props, trace = scan(gray, local_axis)
            variant_local_props, variant_trace = scan(gray, local_axis,
                core_exclusion=VARIANT_CORE_EXCLUSION)
            wide_local_props, wide_trace = scan(gray, local_axis,
                max_cluster_width=WIDE_CLUSTER_LIMIT)
            global_props = [{**p,
                "center": [p["center"][0] + x0, p["center"][1] + y0],
                "line": [p["line"][0] + x0, p["line"][1] + y0,
                         p["line"][2] + x0, p["line"][3] + y0]} for p in local_props]
            global_variant_props = [{**p,
                "center": [p["center"][0] + x0, p["center"][1] + y0],
                "line": [p["line"][0] + x0, p["line"][1] + y0,
                         p["line"][2] + x0, p["line"][3] + y0]} for p in variant_local_props]
            global_wide_props = [{**p,
                "center": [p["center"][0] + x0, p["center"][1] + y0],
                "line": [p["line"][0] + x0, p["line"][1] + y0,
                         p["line"][2] + x0, p["line"][3] + y0]} for p in wide_local_props]
            frozen = panel["ink_proposals"]
            if len(global_props) != len(frozen):
                raise RecallDiagnosticError(f"C# baseline parity count mismatch for {panel['panel_id']}: {len(global_props)} != {len(frozen)}")
            max_geometry_error = max((abs(a - b) for g, f in zip(global_props, frozen)
                for a, b in zip(g["center"] + g["line"], f["center"] + f["line"])), default=0.0)
            if max_geometry_error > 1e-6 or any(g["dark_pixel_count"] != f["dark_pixel_count"] or
                    g["cluster_width_pixels"] != f["cluster_width_pixels"] or
                    g["longest_dark_run"] != f["longest_dark_run"] for g, f in zip(global_props, frozen)):
                raise RecallDiagnosticError(f"C# baseline parity geometry/stats mismatch for {panel['panel_id']}")
            panel_proposals[panel["panel_id"]] = global_props
            variant_proposals[panel["panel_id"]] = global_variant_props
            wide_cluster_proposals[panel["panel_id"]] = global_wide_props
            baseline_rows.extend(global_props)
            panel_records.append({"panel_id": panel["panel_id"],
                "source_id": next(s["source_id"] for s in raw["source_results"]
                    if panel in s["panels"]),
                "bounds": panel["bounds"], "selected_axis": global_axis,
                "needs_review": panel["needs_review"], "uncertainty_reasons": panel["uncertainty_reasons"],
                "baseline_proposal_count": len(frozen), "baseline_reproduction_count": len(global_props),
                "max_geometry_error_px": max_geometry_error,
                "trace": trace, "variant_trace": variant_trace, "wide_trace": wide_trace})

    if len(baseline_rows) != 365:
        raise RecallDiagnosticError(f"baseline reproduced {len(baseline_rows)} proposals, expected 365")
    # Ground truth is reconstructed only after the truth-blind baseline scan has completed and passed parity.
    all_truths = {}
    for source_id, rendered in rendered_sources.items():
        all_truths.update(truth_helper.source_truths(source_id, rendered["panels"]))
    if sum(t["source_stroke_visible"] for truths in all_truths.values() for t in truths) != 181:
        raise RecallDiagnosticError("consumed probe visible tick inventory differs from the pinned 181")
    for panel_record in panel_records:
        visible = [t for t in all_truths[panel_record["panel_id"]] if t["source_stroke_visible"]]
        panel_record["visible_count"] = len(visible)
        panel_record["visible_truth"] = visible
        panel_record["baseline"] = common.match_panel(visible, panel_proposals[panel_record["panel_id"]])
        panel_record["variant"] = common.match_panel(visible, variant_proposals[panel_record["panel_id"]])
        panel_record["wide_cluster"] = common.match_panel(visible,
            wide_cluster_proposals[panel_record["panel_id"]])

    matched_case_ids = {pair["case_id"] for panel in panel_records for pair in panel["baseline"]["matched_pairs"]}
    for panel in panel_records:
        axis = panel["selected_axis"]
        proposals = panel_proposals[panel["panel_id"]]
        local_bounds = panel["bounds"]
        x0, y0, _, _ = local_bounds
        local_axis = [axis[0] - x0, axis[1] - y0, axis[2] - x0, axis[3] - y0]
        for truth in panel["visible_truth"]:
            if truth["case_id"] in matched_case_ids:
                continue
            along, signed_offset, axis_length, _ = _line_projection(truth["stroke_center_original_xy"], axis)
            tx0, ty0, tx1, ty1 = truth["stroke_line_original_xyxy"]
            offsets = [_line_projection([x, y], axis)[1] for x, y in ((tx0, ty0), (tx1, ty1))]
            along_ends = [_line_projection([x, y], axis)[0] for x, y in ((tx0, ty0), (tx1, ty1))]
            tolerance = max(3.0, 0.5 * abs(along_ends[1] - along_ends[0]) + 2.0)
            nearby_clusters = []
            for cluster in panel["trace"]["clusters"]:
                distance = max(cluster["start_along"] - along, 0.0,
                    along - cluster["end_along"])
                if distance <= tolerance:
                    nearby_clusters.append({**cluster, "distance_to_truth_along_px": distance})
            if not nearby_clusters:
                cluster_cause = "no_qualifying_hit_cluster_near_tick"
            elif any(c["rejection"] is None for c in nearby_clusters):
                cluster_cause = "accepted_cluster_not_matched_by_geometry"
            else:
                cluster_cause = "+".join(sorted({c["rejection"] for c in nearby_clusters}))
            miss_cluster_attribution[cluster_cause] += 1
            intersects_scan_band = ((max(offsets) >= 3 and min(offsets) <= 12) or
                (min(offsets) <= -3 and max(offsets) >= -12))
            if along < EDGE_GUARD or along > axis_length - EDGE_GUARD:
                category = "edge_guard"
            elif not intersects_scan_band:
                category = "outside_scan_radius_or_axis_core"
            else:
                category = "within_scan_band_but_no_proposal_match"
            miss_categories[category] += 1
            miss_examples.append({"case_id": truth["case_id"], "panel_id": panel["panel_id"],
                "source_id": panel["source_id"], "projected_along_px": along,
                "signed_center_offset_px": signed_offset, "line_endpoint_offsets_px": offsets,
                "category": category, "cluster_cause": cluster_cause,
                "nearby_clusters": nearby_clusters, "nearby_proposals": [p for p in proposals
                    if abs(_line_projection(p["center"], axis)[0] - along) <= max(3.0,
                        0.5 * math.hypot(tx1 - tx0, ty1 - ty0))]})

    source_metrics = {}
    for source_id in sorted({p["source_id"] for p in panel_records}):
        subset = [p for p in panel_records if p["source_id"] == source_id]
        source_metrics[source_id] = metric_summary(subset, "baseline")
    variant_source_metrics = {}
    for source_id in sorted({p["source_id"] for p in panel_records}):
        subset = [p for p in panel_records if p["source_id"] == source_id]
        variant_source_metrics[source_id] = metric_summary(subset, "variant")
    wide_source_metrics = {}
    for source_id in sorted({p["source_id"] for p in panel_records}):
        subset = [p for p in panel_records if p["source_id"] == source_id]
        wide_source_metrics[source_id] = metric_summary(subset, "wide_cluster")
    baseline_metrics = metric_summary(panel_records, "baseline")
    variant_metrics = metric_summary(panel_records, "variant")
    wide_metrics = metric_summary(panel_records, "wide_cluster")
    expected_metrics = prior["metrics"]["scan_alone"]
    if (baseline_metrics["proposals"] != expected_metrics["proposals"] or
            baseline_metrics["matches"] != expected_metrics["matched_ticks"] or
            baseline_metrics["false_proposals"] != expected_metrics["unmatched_proposals"]):
        raise RecallDiagnosticError("reproduced baseline does not match the retained scan-alone report")
    report = {"schema": "graphreader.short-tick-ink-scan-miss-diagnosis.v3",
        "status": "completed-baseline-parity-miss-attribution-and-two-one-parameter-comparisons",
        "acceptance_status": "consumed synthetic development probe; not acceptance",
        "inputs": {"probe_manifest_sha256": PROBE_MANIFEST_SHA,
            "scanner_proposals_sha256": SCANNER_PROPOSALS_SHA,
            "paired_report_sha256": PAIRED_REPORT_SHA,
            "prior_filter_report_sha256": BASELINE_REPORT_SHA,
            "scanner_source_sha256": SCANNER_SOURCE_SHA, "scanner_project_sha256": SCANNER_PROJECT_SHA,
            "helper_sha256": source_hashes},
        "baseline_reproduction": {"parameters": {"maximum_ink_gray": MAX_INK_GRAY,
            "scan_radius_px": SCAN_RADIUS, "axis_core_exclusion_px": AXIS_CORE_EXCLUSION,
            "edge_guard_px": EDGE_GUARD, "maximum_cluster_width_px": MAX_CLUSTER_WIDTH,
            "minimum_dark_pixels_per_along_sample": 3, "minimum_longest_run_px": 2,
            "minimum_dark_offsets_at_cluster_center": 3, "minimum_offset_span_px": 4},
            "proposals": len(baseline_rows), "proposal_geometry_max_abs_error_px": max(
                (p["max_geometry_error_px"] for p in panel_records), default=0.0),
            "metrics": baseline_metrics, "retained_report_metrics": expected_metrics,
            "exact_report_parity": True},
        "corrected_scanner_comparison": {
            "hypothesis": "A 2px axis-core exclusion suppresses much of an 8px tick shifted toward one side of the selected axis; reducing exclusion to 1px may expose enough connected ink for the scan.",
            "parameters": {"all_baseline_parameters_held": True,
                "axis_core_exclusion_px": VARIANT_CORE_EXCLUSION},
            "metrics": variant_metrics, "per_source_metrics": variant_source_metrics,
            "production_default_changed": False},
        "wide_cluster_rule_comparison": {
            "hypothesis": "Some truth-blind clusters wider than 8px contain a visible tick and are discarded. Removing only this rejection may recover ticks, at a possible false-positive cost.",
            "parameters": {"all_baseline_parameters_held": True,
                "maximum_cluster_width_px": WIDE_CLUSTER_LIMIT,
                "only_changed_rule": "maximum_cluster_width_px increased from 8 to 1000000"},
            "metrics": wide_metrics, "per_source_metrics": wide_source_metrics,
            "production_default_changed": False},
        "visible_tick_miss_attribution": {"visible_ticks": 181,
            "matched_ticks": baseline_metrics["matches"],
            "missed_visible_ticks": 181 - baseline_metrics["matches"],
            "categories": dict(sorted(miss_categories.items())),
            "scan_cluster_causes": dict(sorted(miss_cluster_attribution.items())),
            "examples": miss_examples},
        "per_source_metrics": source_metrics,
        "panel_diagnostics": [{k: v for k, v in p.items() if k not in ("visible_truth", "trace")}
            for p in panel_records],
        "training_performed": False, "production_behavior_changed": False,
        "dev_sealed_private_inputs_read": False,
        "limitations": ["Only the already-consumed procedural synthetic probe was scored.",
            "This step diagnoses the exact frozen scan behavior; it does not claim generalization to real charts."]}
    return report


if __name__ == "__main__":
    report = run()
    output = ROOT / COMPARISON_OUTPUT_REL
    if output.exists():
        raise SystemExit("diagnostic output already exists; refusing overwrite")
    output.mkdir(parents=False)
    path = output / "miss_diagnosis.json"
    path.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"baseline": report["baseline_reproduction"]["metrics"],
        "variant": report["corrected_scanner_comparison"]["metrics"],
        "wide_cluster_variant": report["wide_cluster_rule_comparison"]["metrics"],
        "miss_categories": report["visible_tick_miss_attribution"]["categories"],
        "scan_cluster_causes": report["visible_tick_miss_attribution"]["scan_cluster_causes"],
        "output": path.relative_to(ROOT).as_posix()}, indent=2))
