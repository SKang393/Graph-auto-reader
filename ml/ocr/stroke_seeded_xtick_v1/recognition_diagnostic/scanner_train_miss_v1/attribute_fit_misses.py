# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Attribute fit-only positive label misses to frozen pixel-scan eligibility stages."""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
import json
import math
from pathlib import Path
from typing import Any

from PIL import Image

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan

ROOT = scan.ROOT
RUN_ROOT = ROOT / "artifacts/stroke-seeded-xtick-v1-scan-train-miss-v2"
RUN_REPORT_SHA = "a08da3e040112e4476777c7c7d4e21b4882e1f32e1f2a37209bebd5b84984e83"
FIT_PROPOSALS_SHA = "e3d1476b8264e61e35c1187e5d7851cd5abf2f5ff30e889043a8aa0d9eee99ca"
V6_SHA = scan.V6_MANIFEST_SHA
CURATION_SHA = scan.CURATION_MANIFEST_SHA
OUTPUT = RUN_ROOT / "fit_miss_attribution.json"


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def point_line_distance(point: list[float], line: list[float]) -> float:
    x0, y0, x1, y1 = (float(v) for v in line)
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length == 0:
        return math.inf
    return abs(dx * (float(point[1]) - y0) - dy * (float(point[0]) - x0)) / length


def local_hit_metrics(gray: Image.Image, axis_full: list[float], panel_bounds: list[int], along: float) -> dict[str, int]:
    left, top, right, bottom = panel_bounds
    axis = [axis_full[0] - left, axis_full[1] - top, axis_full[2] - left, axis_full[3] - top]
    dx, dy = axis[2] - axis[0], axis[3] - axis[1]
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 8:
        return {"dark_sample_count": 0, "longest_contiguous_run": 0}
    ux, uy = dx / length, dy / length
    if ux < 0:
        ux, uy = -ux, -uy
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    panel = gray.crop((left, top, right, bottom)).convert("L")
    px, hit_count, longest, run, previous = panel.load(), 0, 0, 0, False
    along_i = int(round(along))
    for offset in range(-12, 13):
        if abs(offset) <= 2:
            run, previous = 0, False
            continue
        x = int(round(axis[0] + ux * along_i + nx * offset))
        y = int(round(axis[1] + uy * along_i + ny * offset))
        dark = 0 <= x < panel.width and 0 <= y < panel.height and px[x, y] < 200
        if dark:
            hit_count += 1
            run = run + 1 if previous else 1
            longest = max(longest, run)
        else:
            run = 0
        previous = dark
    return {"dark_sample_count": hit_count, "longest_contiguous_run": longest}


def scan_with_rejections(gray: Image.Image, axis_full: list[float], panel_bounds: list[int],
                         max_cluster_width: int = 8) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Mirror the frozen scan while returning post-proposal rejection telemetry."""
    left, top, right, bottom = panel_bounds
    axis = [axis_full[0] - left, axis_full[1] - top, axis_full[2] - left, axis_full[3] - top]
    dx, dy = axis[2] - axis[0], axis[3] - axis[1]
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 8:
        return [], [{"reason": "axis_too_short_or_invalid", "along_start": 0, "along_end": 0, "width": 0}]
    ux, uy = dx / length, dy / length
    if ux < 0:
        ux, uy = -ux, -uy
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    panel = gray.crop((left, top, right, bottom)).convert("L")
    px = panel.load()
    hits: list[tuple[int, int, int]] = []
    along = 4
    while along <= length - 4:
        dark_count = longest_run = run = 0
        previous_dark = False
        for offset in range(-12, 13):
            if abs(offset) <= 2:
                run = 0
                previous_dark = False
                continue
            x = int(round(axis[0] + ux * along + nx * offset))
            y = int(round(axis[1] + uy * along + ny * offset))
            dark = 0 <= x < panel.width and 0 <= y < panel.height and px[x, y] < 200
            if dark:
                dark_count += 1
                run = run + 1 if previous_dark else 1
                longest_run = max(longest_run, run)
            else:
                run = 0
            previous_dark = dark
        if dark_count >= 3 and longest_run >= 2:
            hits.append((along, dark_count, longest_run))
        along += 1
    clusters: list[list[tuple[int, int, int]]] = []
    for hit in hits:
        if not clusters or hit[0] - clusters[-1][-1][0] > 1:
            clusters.append([])
        clusters[-1].append(hit)
    proposals, cluster_rows = [], []
    for cluster in clusters:
        width = cluster[-1][0] - cluster[0][0] + 1
        chosen_along = cluster[len(cluster) // 2][0]
        row = {"along_start": cluster[0][0], "along_end": cluster[-1][0],
               "along_center": chosen_along, "width": width,
               "qualifying_positions": len(cluster), "reason": "accepted"}
        if width > max_cluster_width:
            row["reason"] = "cluster_width_exceeded"
            cluster_rows.append(row)
            continue
        dark_offsets = []
        for offset in range(-12, 13):
            if abs(offset) <= 2:
                continue
            x = int(round(axis[0] + ux * chosen_along + nx * offset))
            y = int(round(axis[1] + uy * chosen_along + ny * offset))
            if 0 <= x < panel.width and 0 <= y < panel.height and px[x, y] < 200:
                dark_offsets.append(offset)
        row["peak_dark_offsets"] = len(dark_offsets)
        if len(dark_offsets) < 3:
            row["reason"] = "insufficient_peak_dark_offsets"
            cluster_rows.append(row)
            continue
        start_offset, end_offset = min(0, min(dark_offsets)), max(0, max(dark_offsets))
        row["off_axis_span"] = end_offset - start_offset
        if end_offset - start_offset < 4:
            row["reason"] = "off_axis_span_below_4px"
            cluster_rows.append(row)
            continue
        start = [axis[0] + ux * chosen_along + nx * start_offset + left,
                 axis[1] + uy * chosen_along + ny * start_offset + top]
        end = [axis[0] + ux * chosen_along + nx * end_offset + left,
               axis[1] + uy * chosen_along + ny * end_offset + top]
        proposals.append({"center": [(start[0] + end[0]) / 2, (start[1] + end[1]) / 2],
                          "line": [start[0], start[1], end[0], end[1]],
                          "cluster_width_pixels": width,
                          "dark_pixel_count": sum(hit[1] for hit in cluster),
                          "longest_dark_run": max(hit[2] for hit in cluster)})
        cluster_rows.append(row)
    return proposals, cluster_rows


def attribute_misses(truths: list[dict[str, Any]], proposals: list[dict[str, Any]],
                     clusters: list[dict[str, Any]], axis: list[float], needs_review: bool,
                     gray: Image.Image, panel_bounds: list[int]) -> list[dict[str, Any]]:
    assignments = scan.curation.assign_proposals(proposals, truths)
    matched_truths = {row["truth_index"] for row in assignments
                      if row["state"] == "unique_visible_tick" and row["truth_index"] is not None}
    x0, y0, x1, y1 = (float(v) for v in axis)
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    ux, uy = (dx / length, dy / length) if length else (0.0, 0.0)
    if ux < 0:
        ux, uy = -ux, -uy
    result = []
    for index, truth in enumerate(truths):
        if index in matched_truths:
            continue
        x, y = (float(v) for v in truth["stroke_center_original_xy"])
        relative_along = (x - x0) * ux + (y - y0) * uy
        axis_distance = point_line_distance([x, y], axis)
        tolerance = scan.curation._center_tolerance(truth)
        nearby = [row for row in clusters if row["along_start"] - tolerance <= relative_along <= row["along_end"] + tolerance]
        hit = None
        if length <= 8:
            reason = "axis_too_short_or_invalid"
        elif relative_along < 4 or relative_along > length - 4:
            reason = "endpoint_guard"
        elif nearby:
            nearest = min(nearby, key=lambda row: abs(relative_along - row["along_center"]))
            reason = nearest["reason"] if nearest["reason"] != "accepted" else "accepted_cluster_but_no_matched_proposal"
        else:
            hit = local_hit_metrics(gray, axis, panel_bounds, relative_along)
            if hit["dark_sample_count"] < 3:
                reason = "dark_sample_count_below_3"
            elif hit["longest_contiguous_run"] < 2:
                reason = "contiguous_dark_run_below_2"
            else:
                reason = "qualifying_local_ink_not_in_nearby_cluster"
        result.append({"tick_id": truth["tick_id"], "expected_label": truth.get("expected_tick_label"),
            "axis_review_flag": bool(needs_review), "axis_centerline_distance_px": axis_distance,
            "axis_relative_along_px": relative_along, "match_tolerance_px": tolerance,
            "closest_stage_reason": reason,
            "local_scan_hit_metrics": hit if not nearby and length > 8 else None,
            "nearby_cluster_widths": [row["width"] for row in nearby]})
    return result


def _quantile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[round((len(ordered) - 1) * q)]


def run() -> dict[str, Any]:
    run_bytes = scan.read_pinned(RUN_ROOT / "report.json", RUN_REPORT_SHA, 2_000_000)
    run_report = json.loads(run_bytes)
    proposal_bytes = scan.read_pinned(RUN_ROOT / "fit_truth_blind_proposals.json", FIT_PROPOSALS_SHA, 2_000_000)
    fit_outputs = json.loads(proposal_bytes)
    v6_bytes = scan.read_pinned(ROOT / scan.V6_MANIFEST_REL, V6_SHA, 5_000_000)
    v6 = json.loads(v6_bytes)
    curation_bytes = scan.read_pinned(ROOT / scan.CURATION_REL / "manifest.json", CURATION_SHA, 2_000_000)
    curation_manifest = json.loads(curation_bytes)
    if run_report.get("frozen_rule", {}).get("validation_truth_opened") is not False:
        raise RuntimeError("the selected scanner rule must be frozen before validation labels are accessed")
    fit_ids = list(run_report["fit_sources_scored"])
    if not set(fit_ids).issubset(set(run_report["split"]["fit_source_ids"])):
        raise RuntimeError("fit proposal source rows escape the frozen train fit groups")
    source_map = {row["source_id"]: row for row in v6["sources"]}
    raster_hashes = {row["source_id"]: row for row in curation_manifest["source_groups"]}
    # Reconstruct only fit panel pixels. No axis detector or model rerun.
    _, fit_data = scan._truth_blind_inputs(fit_ids, source_map, raster_hashes)
    refs_by_source_panel: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for ref in v6["records"]:
        if ref["source_id"] not in fit_ids or ref["split"] != "train" or ref["kind"] != "positive":
            continue
        payload = scan.read_pinned(ROOT / scan.V6_REL / ref["annotation_path"], ref["annotation_sha256"], 65_536)
        annotation = json.loads(payload)
        if annotation.get("source_id") != ref["source_id"] or annotation.get("split") != "train":
            raise RuntimeError("fit label escaped pinned train source identity")
        refs_by_source_panel[(ref["source_id"], annotation["source_panel_id"])].append(annotation)
    source_outputs = {row["source_id"]: row for row in fit_outputs}
    all_misses = []
    totals = Counter()
    by_source = defaultdict(Counter)
    positive_truth_count = 0
    for source_id in fit_ids:
        source_output = source_outputs[source_id]
        image = fit_data[source_id]["image"]
        rendered = fit_data[source_id]["rendered"]
        for panel_output in source_output["panels"]:
            panel_id = panel_output["panel_id"]
            panel = rendered["panels"][int(panel_output["panel_index"])]
            if panel["panel_id"] != panel_id:
                raise RuntimeError("fit panel id mismatch during post-run attribution")
            gray = image.convert("L")
            rendered_ticks = {tick["tick_id"]: tick for tick in panel["ticks"]
                              if tick.get("axis", "x") == "x" and tick.get("role") == "x_tick"}
            truths = []
            for row in refs_by_source_panel[(source_id, panel_id)]:
                tick = rendered_ticks.get(row["tick_id"])
                if tick is None or not tick.get("visible"):
                    raise RuntimeError("positive fit label is not a visible XTick in the reconstructed renderer")
                if row["source_sha256"] != fit_data[source_id]["source_pixel_sha256"]:
                    raise RuntimeError("positive fit label raster identity mismatch")
                if not scan.curation._sample_tick_ink(gray, row["stroke_line_original_xyxy"]):
                    raise RuntimeError("positive fit label stroke is not present in the exact raster")
                truths.append({"tick_id": row["tick_id"],
                    "stroke_center_original_xy": row["stroke_center_original_xy"],
                    "stroke_line_original_xyxy": row["stroke_line_original_xyxy"],
                    "expected_tick_label": row["expected_tick_label"], "visible": True,
                    "ground_truth_target_unambiguous": True})
            positive_truth_count += len(truths)
            axis = panel_output["selected_x_axis_line"]
            bounds = scan.common.transformed_panel_bounds(panel, image.size)
            proposals, clusters = scan_with_rejections(image, axis, bounds, 8)
            frozen = panel_output["ink_proposals"]
            if len(proposals) != len(frozen):
                raise RuntimeError(f"fit pixel scan proposal parity failed: {source_id}/{panel_id}")
            for a, b in zip(proposals, frozen):
                for key in ("center", "line"):
                    if max(abs(float(x)-float(y)) for x,y in zip(a[key], b[key])) > 1e-6:
                        raise RuntimeError(f"fit pixel scan {key} parity failed: {source_id}/{panel_id}")
                for key in ("cluster_width_pixels", "dark_pixel_count", "longest_dark_run"):
                    if a[key] != b[key]:
                        raise RuntimeError(f"fit pixel scan {key} parity failed: {source_id}/{panel_id}")
            misses = attribute_misses(truths, proposals, clusters, axis,
                bool(panel_output["needs_review"]), image, bounds)
            for miss in misses:
                miss.update({"source_id": source_id, "panel_id": panel_id,
                    "uncertainty_reasons": panel_output["uncertainty_reasons"]})
                all_misses.append(miss)
                key = "axis_uncertain" if miss["axis_review_flag"] else "axis_clear"
                totals[(key, "misses")] += 1
                totals[(key, miss["closest_stage_reason"])] += 1
                by_source[source_id]["misses"] += 1
                by_source[source_id][miss["closest_stage_reason"]] += 1
    distances = {key: [row["axis_centerline_distance_px"] for row in all_misses if row["axis_review_flag"] == flag]
                 for key, flag in (("axis_uncertain", True), ("axis_clear", False))}
    reasons = Counter(row["closest_stage_reason"] for row in all_misses)
    if positive_truth_count != 179 or positive_truth_count - len(all_misses) != 82:
        raise RuntimeError("fit positive-label/match denominator does not reproduce the frozen main report")
    report = {"schema": "graphreader.scan-train-miss-fit-attribution.v1",
        "recipe_id": "fit-only-positive-target-eligibility-stage-attribution-v1",
        "acceptance_status": "exploratory train-fit attribution only",
        "input_hashes": {"scanner_run_report_sha256": digest(run_bytes),
            "fit_truth_blind_proposals_sha256": digest(proposal_bytes), "v6_train_manifest_sha256": digest(v6_bytes),
            "curation_manifest_sha256": digest(curation_bytes),
            "scan_train_miss_runner_sha256": digest(Path(scan.__file__).read_bytes()),
            "attribution_source_sha256": digest(Path(__file__).read_bytes())},
        "fit_source_ids": fit_ids,
        "positive_target_truths": positive_truth_count,
        "matched_positive_targets_at_width_8": positive_truth_count - len(all_misses),
        "missed_positive_targets": len(all_misses),
        "miss_stage_reason_counts": dict(sorted(reasons.items())),
        "by_axis_review_flag": {key: {"misses": totals[(key, "misses")],
            "stage_reasons": {reason: totals[(key, reason)] for reason in sorted(reasons)},
            "axis_centerline_distance_mean_px": sum(distances[key]) / len(distances[key]) if distances[key] else None,
            "axis_centerline_distance_p50_px": _quantile(distances[key], .5),
            "axis_centerline_distance_p90_px": _quantile(distances[key], .9),
            "misses_within_2px_of_selected_axis": sum(x <= 2 for x in distances[key])}
            for key in ("axis_uncertain", "axis_clear")},
        "per_source_group": {key: dict(value) for key, value in sorted(by_source.items())},
        "misses": all_misses,
        "truth_handling": "Read only positive V6 fit annotations after saved truth-blind proposals; no validation, consumed dev, sealed or private labels read.",
        "scanner_rerun": False, "training_performed": False, "gpu_used": False,
        "production_behavior_changed": False}
    if OUTPUT.exists() or OUTPUT.is_symlink():
        raise RuntimeError("attribution output already exists")
    OUTPUT.write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))
