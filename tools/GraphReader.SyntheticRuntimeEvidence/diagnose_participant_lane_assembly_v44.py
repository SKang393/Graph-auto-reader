# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate one cached plot-derived participant-lane assembly trial for V44."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from hashlib import sha256
import itertools
import json
import math
from pathlib import Path
import sys
from typing import Any, Iterable, Sequence


ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
for location in (ROOT, TOOLS):
    if str(location) not in sys.path:
        sys.path.insert(0, str(location))

import compare_word_fragment_collision_geometry_v1 as comparison  # noqa: E402
import diagnose_aligned_word_assembly_v44 as aligned  # noqa: E402
import diagnose_ocr_line_assembly_v44 as assembly  # noqa: E402


SCHEMA = "graphreader.goal22.ocr-v44-participant-lane-assembly-diagnostic.v1"
ALIGNED_SCHEMA = "graphreader.goal22.ocr-v44-aligned-word-assembly-diagnostic.v1"


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _verify(path: Path, expected: str, label: str) -> None:
    if len(expected) != 64 or not path.is_file() or _sha(path) != expected:
        raise RuntimeError(f"{label} identity changed: {path}")


def _repository_path(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def _point(value: dict[str, Any], label: str) -> tuple[float, float]:
    if set(value) != {"x", "y", "is_finite"} or value["is_finite"] is not True:
        raise RuntimeError(f"{label} point shape changed")
    x = float(value["x"])
    y = float(value["y"])
    if not math.isfinite(x) or not math.isfinite(y):
        raise RuntimeError(f"{label} point is nonfinite")
    return x, y


def _map_point(x: float, y: float, matrix: Sequence[float]) -> tuple[float, float]:
    if len(matrix) != 9 or any(not math.isfinite(float(value)) for value in matrix):
        raise RuntimeError("Panel-to-source matrix changed")
    denominator = matrix[6] * x + matrix[7] * y + matrix[8]
    if abs(denominator) <= 1e-12:
        raise RuntimeError("Panel-to-source matrix mapped a point to infinity")
    return (
        (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
        (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator,
    )


def _plot_box(axis: dict[str, Any], width: int, height: int) -> assembly.metric.Box:
    geometry = axis["geometry"]
    if geometry.get("coordinate_space") != "original_pixels":
        raise RuntimeError("Axis plot geometry coordinate space changed")
    points = geometry["plot_polygon"]["points"]
    if not isinstance(points, list) or len(points) != 4:
        raise RuntimeError("Axis plot polygon shape changed")
    coordinates = [_point(value, "plot polygon") for value in points]
    result = assembly.metric.Box(
        min(value[0] for value in coordinates),
        min(value[1] for value in coordinates),
        max(value[0] for value in coordinates),
        max(value[1] for value in coordinates),
    )
    if (result.left < 0 or result.top < 0
            or result.right > width or result.bottom > height):
        raise RuntimeError("Axis plot polygon escaped panel bounds")
    return result


def _divider_midpoints(axis: dict[str, Any]) -> tuple[tuple[float, float], ...]:
    result = []
    for divider in axis["geometry"]["phase_dividers"]:
        result.append(_point(divider["line"]["midpoint"], "phase divider midpoint"))
    return tuple(result)


def _load_panel_geometry(
    report: dict[str, Any],
) -> tuple[
    dict[tuple[str, str], assembly.metric.Box],
    dict[tuple[str, str], tuple[tuple[float, float], ...]],
    list[dict[str, str]],
]:
    request_descriptor = report["request"]
    if set(request_descriptor) != {"path", "sha256"}:
        raise RuntimeError("V44 capture request descriptor shape changed")
    request_path = comparison._inside_artifacts(request_descriptor["path"])
    _verify(request_path, request_descriptor["sha256"], "V44 capture request")
    request = _read(request_path)
    if (request.get("schema") != "graphreader.official-head-tensor-capture-request.v1"
            or request.get("model_inference") is not False
            or request.get("private_data") is not False
            or request.get("sealed_data") is not False
            or request.get("truth_included") is not False):
        raise RuntimeError("V44 capture request scope changed")

    report_descriptors = request["reports"]
    by_report_path = {}
    verified_reports = []
    for descriptor in report_descriptors:
        required = {"manifest_path", "manifest_sha256", "report_path", "report_sha256", "split"}
        if not isinstance(descriptor, dict) or set(descriptor) != required:
            raise RuntimeError("V44 runtime report descriptor shape changed")
        report_path = comparison._inside_artifacts(descriptor["report_path"])
        manifest_path = comparison._inside_artifacts(descriptor["manifest_path"])
        if report_path in by_report_path:
            raise RuntimeError("V44 capture request repeats a runtime report")
        _verify(report_path, descriptor["report_sha256"], "V44 runtime report")
        _verify(manifest_path, descriptor["manifest_sha256"], "V44 runtime manifest")
        document = _read(report_path)
        if (document.get("schema") != "graphreader.synthetic-runtime-seed-evidence.v2"
                or document.get("failed") != 0
                or document.get("completed") != document.get("count")
                or document.get("failed_panels") != 0
                or document.get("completed_panels") != document.get("panel_count")):
            raise RuntimeError("V44 runtime report completion changed")
        by_report_path[report_path] = (descriptor, document)
        verified_reports.append({
            "path": _repository_path(report_path),
            "sha256": descriptor["report_sha256"],
        })
    if len(by_report_path) != 6:
        raise RuntimeError("V44 runtime report inventory changed")

    request_panels = {}
    for row in request["panels"]:
        key = (row["source_sha256"], row["panel_id"])
        if key in request_panels:
            raise RuntimeError("V44 capture request repeats a panel")
        runtime_path = comparison._inside_artifacts(row["report_path"])
        descriptor, _ = by_report_path[runtime_path]
        if (row["report_sha256"] != descriptor["report_sha256"]
                or row["split"] != descriptor["split"]):
            raise RuntimeError("V44 request panel runtime-report binding changed")
        request_panels[key] = row

    app_panels = {(row["source_sha256"], row["panel_id"]): row for row in report["panels"]}
    if len(app_panels) != len(report["panels"]) or set(app_panels) != set(request_panels):
        raise RuntimeError("V44 application and capture panel inventories differ")

    plot_by_panel = {}
    dividers_by_panel = {}
    observed_runtime_panels = set()
    for runtime_path, (descriptor, document) in by_report_path.items():
        for case in document["cases"]:
            source_sha = case["image_sha256"]
            for runtime_panel in case["panels"]:
                key = (source_sha, runtime_panel["panel_id"])
                if key in observed_runtime_panels:
                    raise RuntimeError("V44 runtime evidence repeats a panel")
                observed_runtime_panels.add(key)
                request_panel = request_panels.get(key)
                app_panel = app_panels.get(key)
                if request_panel is None or app_panel is None:
                    raise RuntimeError("V44 runtime panel is absent from capture inventory")
                if (request_panel["report_path"] != _repository_path(runtime_path)
                        or request_panel["split"] != descriptor["split"]
                        or runtime_panel["status"] != "seed-completed"
                        or runtime_panel["width"] != app_panel["width"]
                        or runtime_panel["height"] != app_panel["height"]
                        or runtime_panel["crop"] != app_panel["crop"]
                        or runtime_panel["panel_to_source_matrix"]
                        != app_panel["panel_to_source_matrix"]):
                    raise RuntimeError("V44 runtime panel geometry binding changed")
                plot_by_panel[key] = _plot_box(
                    runtime_panel["axis"], runtime_panel["width"], runtime_panel["height"]
                )
                dividers_by_panel[key] = _divider_midpoints(runtime_panel["axis"])
    if observed_runtime_panels != set(request_panels):
        raise RuntimeError("V44 runtime reports omit a requested panel")
    return plot_by_panel, dividers_by_panel, verified_reports


def _inside_participant_lane(box: assembly.metric.Box, plot: assembly.metric.Box) -> bool:
    center_x = (box.left + box.right) / 2.0
    center_y = (box.top + box.bottom) / 2.0
    return center_x < plot.left and plot.top <= center_y <= plot.bottom


def _assemble_panel(
    panel: dict[str, Any],
    plot: assembly.metric.Box,
) -> tuple[assembly.RegionGroup, ...]:
    remaining = sorted(
        assembly._raw_groups(panel),
        key=lambda row: (
            row.panel_box.top, row.panel_box.left,
            row.panel_box.bottom, row.panel_box.right,
        ),
    )
    assembled = []
    while remaining:
        line = remaining.pop(0)
        changed = True
        while changed:
            changed = False
            for index in range(len(remaining) - 1, -1, -1):
                candidate = remaining[index]
                merged = assembly._union(line.panel_box, candidate.panel_box)
                if not aligned._can_merge(line.panel_box, candidate.panel_box):
                    continue
                if not _inside_participant_lane(merged, plot):
                    continue
                member_ids = tuple(sorted((*line.member_ids, *candidate.member_ids)))
                line = assembly.RegionGroup(
                    f'{panel["panel_id"]}\nparticipant-lane:{sha256(chr(10).join(member_ids).encode()).hexdigest()}',
                    panel["source_sha256"],
                    panel["panel_id"],
                    merged,
                    assembly._map_box(merged, panel["panel_to_source_matrix"]),
                    member_ids,
                    (*line.member_source_boxes, *candidate.member_source_boxes),
                )
                remaining.pop(index)
                changed = True
        assembled.append(line)
    return tuple(sorted(
        assembled,
        key=lambda row: (
            row.panel_box.top, row.panel_box.left,
            row.panel_box.bottom, row.panel_box.right,
        ),
    ))


def _nearest_truth(
    box: assembly.metric.Box,
    truths: Sequence[Any],
) -> Any | None:
    overlapping = [truth for truth in truths if assembly._intersects(box, truth.box)]
    return None if not overlapping else max(
        overlapping, key=lambda truth: assembly.box_iou(box, truth.box)
    )


def _role_set(roles: Iterable[str]) -> str:
    rows = sorted(set(roles))
    return "+".join(rows) if rows else "unmatched"


def _merged_group_audit(
    groups: Sequence[assembly.RegionGroup],
    truths: Sequence[Any],
) -> dict[str, Any]:
    truths_by_source = defaultdict(list)
    for truth in truths:
        truths_by_source[truth.source_sha256].append(truth)
    role_sets = Counter()
    same_truth = 0
    cross_truth = 0
    groups_with_unmatched_members = 0
    contamination = Counter()
    merged = [group for group in groups if len(group.member_ids) > 1]
    for group in merged:
        member_truths = []
        unmatched = False
        for box in group.member_source_boxes:
            truth = _nearest_truth(box, truths_by_source[group.source_sha256])
            if truth is None:
                unmatched = True
            else:
                member_truths.append(truth)
        truth_ids = {truth.truth_id for truth in member_truths}
        roles = {truth.generator_role for truth in member_truths}
        role_sets[_role_set(roles)] += 1
        same_truth += int(len(truth_ids) == 1 and not unmatched)
        cross_truth += int(len(truth_ids) > 1)
        groups_with_unmatched_members += int(unmatched)
        for role in ("x_tick", "y_tick", "axis_title"):
            contamination[role] += int(role in roles)
    return {
        "merged_group_count": len(merged),
        "raw_regions_consumed_by_merged_groups": sum(len(group.member_ids) for group in merged),
        "groups_by_member_nearest_truth_role_set": dict(sorted(role_sets.items())),
        "groups_whose_members_map_to_one_truth": same_truth,
        "groups_whose_members_map_to_different_truths": cross_truth,
        "groups_with_at_least_one_unmatched_member": groups_with_unmatched_members,
        "contamination_group_counts": {
            "axis_title": contamination["axis_title"],
            "x_tick": contamination["x_tick"],
            "y_tick": contamination["y_tick"],
        },
        "truth_is_diagnostic_only": True,
    }


def _dev_participant_fragment_pairs_grouped(
    report: dict[str, Any],
    truths: Sequence[Any],
    assembled_by_panel: dict[tuple[str, str], tuple[assembly.RegionGroup, ...]],
) -> dict[str, int | str]:
    truths_by_source = defaultdict(list)
    for truth in truths:
        truths_by_source[truth.source_sha256].append(truth)
    pair_count = 0
    grouped_count = 0
    for panel in report["panels"]:
        if panel["split"] != "validation":
            continue
        raw = assembly._raw_groups(panel)
        groups = assembled_by_panel[(panel["source_sha256"], panel["panel_id"])]
        for truth in truths_by_source[panel["source_sha256"]]:
            if truth.generator_role != "participant":
                continue
            intersecting = [row for row in raw if assembly._intersects(row.source_box, truth.box)]
            if len(intersecting) < 2:
                continue
            candidates = []
            for left, right in itertools.combinations(intersecting, 2):
                combined = assembly._union(left.source_box, right.source_box)
                candidates.append((assembly.box_iou(combined, truth.box), left, right))
            _, left, right = max(candidates, key=lambda row: row[0])
            pair_count += 1
            expected_ids = set((*left.member_ids, *right.member_ids))
            grouped_count += int(any(expected_ids <= set(group.member_ids) for group in groups))
    return {
        "observed_pair_count": pair_count,
        "grouped_pair_count": grouped_count,
        "measurement": (
            "Truth selects the previously diagnosed best-overlap participant pair only for "
            "measurement after geometry-only grouping; truth and roles do not form groups."
        ),
    }


def _mechanism_summary(
    report: dict[str, Any],
    truths_by_split: dict[str, tuple[Any, ...]],
    plot_by_panel: dict[tuple[str, str], assembly.metric.Box],
    dividers_by_panel: dict[tuple[str, str], tuple[tuple[float, float], ...]],
) -> dict[str, Any]:
    truths_by_source = defaultdict(list)
    for split in ("train", "validation"):
        for truth in truths_by_split[split]:
            truths_by_source[(split, truth.source_sha256)].append(truth)
    crossing_count = 0
    crossings_above_plot = 0
    crossings_in_participant_lane = 0
    crossings_with_divider_midpoint_in_gap = 0
    crossing_roles = Counter()
    participant_pairs = 0
    participant_pairs_in_lane = 0
    for panel in report["panels"]:
        panel_key = (panel["source_sha256"], panel["panel_id"])
        plot = plot_by_panel[panel_key]
        truths = truths_by_source[(panel["split"], panel["source_sha256"])]
        for group in aligned._assemble_panel(panel):
            member_truths = [
                _nearest_truth(box, truths) for box in group.member_source_boxes
            ]
            truth_ids = {truth.truth_id for truth in member_truths if truth is not None}
            if len(truth_ids) <= 1:
                continue
            crossing_count += 1
            crossing_roles[_role_set(
                truth.generator_role for truth in member_truths if truth is not None
            )] += 1
            crossings_above_plot += int(group.panel_box.bottom <= plot.top)
            crossings_in_participant_lane += int(
                _inside_participant_lane(group.panel_box, plot)
            )
            members = sorted(group.member_source_boxes, key=lambda box: box.left)
            gaps = tuple(
                (left.right, right.left)
                for left, right in zip(members, members[1:])
            )
            matrix = panel["panel_to_source_matrix"]
            source_divider_x = [
                _map_point(x, y, matrix)[0]
                for x, y in dividers_by_panel[panel_key]
            ]
            crossings_with_divider_midpoint_in_gap += int(any(
                gap_left <= x <= gap_right
                for gap_left, gap_right in gaps
                for x in source_divider_x
            ))
        if panel["split"] != "validation":
            continue
        raw = assembly._raw_groups(panel)
        for truth in truths:
            if truth.generator_role != "participant":
                continue
            intersecting = [row for row in raw if assembly._intersects(row.source_box, truth.box)]
            if len(intersecting) < 2:
                continue
            _, left, right = max(
                (
                    (
                        assembly.box_iou(
                            assembly._union(a.source_box, b.source_box), truth.box
                        ),
                        a,
                        b,
                    )
                    for a, b in itertools.combinations(intersecting, 2)
                ),
                key=lambda row: row[0],
            )
            participant_pairs += 1
            union_panel = assembly._union(left.panel_box, right.panel_box)
            participant_pairs_in_lane += int(_inside_participant_lane(union_panel, plot))
    return {
        "global_aligned_cross_truth_group_count": crossing_count,
        "global_aligned_cross_truth_role_sets": dict(sorted(crossing_roles.items())),
        "cross_truth_groups_above_plot_count": crossings_above_plot,
        "cross_truth_groups_in_participant_lane_count": crossings_in_participant_lane,
        "cross_truth_groups_with_detected_divider_midpoint_in_member_gap_count":
            crossings_with_divider_midpoint_in_gap,
        "dev_participant_fragment_pair_count": participant_pairs,
        "dev_participant_fragment_pairs_in_participant_lane_count":
            participant_pairs_in_lane,
        "interpretation": (
            "The observed crossing groups are above-plot semantic headers, while the diagnosed "
            "participant word/suffix pairs occupy the plot-derived left participant lane. A "
            "detected divider midpoint is not present inside any crossing member gap."
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--aligned-result", required=True)
    parser.add_argument("--aligned-result-sha256", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()

    aligned_path = comparison._inside_artifacts(arguments.aligned_result)
    output_path = comparison._inside_artifacts(arguments.output)
    if output_path.exists():
        raise RuntimeError("Use a new participant-lane output path")
    _verify(aligned_path, arguments.aligned_result_sha256, "aligned assembly result")
    aligned_result = _read(aligned_path)
    if (aligned_result.get("schema") != ALIGNED_SCHEMA
            or aligned_result.get("status") != "cached_train_dev_geometry_trial_complete"
            or aligned_result.get("model_inference") is not False
            or aligned_result.get("private_reads") != 0
            or aligned_result.get("sealed_reads") != 0):
        raise RuntimeError("Aligned assembly result scope changed")
    for relative, digest in aligned_result["source_bindings"].items():
        _verify(comparison._inside_root(relative), digest, "aligned result source")
    for descriptor in aligned_result["inputs"].values():
        _verify(comparison._inside_root(descriptor["path"]), descriptor["sha256"],
                "aligned result input")

    historical = aligned_result["inputs"]["historical_runtime_manifest"]
    score, report, preflight, _ = comparison._authenticate_historical_v44(
        comparison._inside_artifacts(historical["path"]), historical["sha256"]
    )
    truths_by_split = assembly._load_truths(preflight, score)
    plot_by_panel, dividers_by_panel, runtime_reports = _load_panel_geometry(report)

    raw_by_split = {"train": [], "validation": []}
    lane_by_split = {"train": [], "validation": []}
    assembled_by_panel = {}
    for panel in report["panels"]:
        if panel["status"] != "completed":
            raise RuntimeError("Participant-lane trial requires completed V44 panels")
        key = (panel["source_sha256"], panel["panel_id"])
        groups = _assemble_panel(panel, plot_by_panel[key])
        assembled_by_panel[key] = groups
        raw_by_split[panel["split"]].extend(assembly._raw_groups(panel))
        lane_by_split[panel["split"]].extend(groups)

    geometry_results = {}
    for split in ("train", "validation"):
        scored = assembly._score_geometry(
            split,
            truths_by_split[split],
            tuple(raw_by_split[split]),
            tuple(lane_by_split[split]),
        )
        expected_baseline = aligned_result["geometry_results"][split]["raw_baseline"]
        if scored["baseline"] != expected_baseline:
            raise RuntimeError(f"{split} raw baseline changed")
        geometry_results[split] = {
            "full_truth_denominator": len(truths_by_split[split]),
            "raw_baseline": scored["baseline"],
            "participant_lane_assembled": scored["assembled"],
            "delta_from_raw": scored["delta"],
            "recovered_truth_count": scored["recovered_truth_count"],
            "lost_truth_count": scored["lost_truth_count"],
            "by_generator_role": scored["by_generator_role"],
            "merged_group_audit": _merged_group_audit(
                tuple(lane_by_split[split]), truths_by_split[split]
            ),
        }

    participant_measurement = _dev_participant_fragment_pairs_grouped(
        report,
        truths_by_split["validation"],
        assembled_by_panel,
    )
    if participant_measurement["observed_pair_count"] != 9:
        raise RuntimeError("Diagnosed dev participant pair inventory changed")
    mechanism = _mechanism_summary(
        report, truths_by_split, plot_by_panel, dividers_by_panel
    )
    if (mechanism["global_aligned_cross_truth_group_count"] != 9
            or mechanism["cross_truth_groups_with_detected_divider_midpoint_in_member_gap_count"] != 0
            or mechanism["dev_participant_fragment_pair_count"] != 9):
        raise RuntimeError("Preceding assembly mechanism evidence changed")

    result = {
        "schema": SCHEMA,
        "status": "cached_train_dev_participant_lane_geometry_trial_complete",
        "scope": (
            "Authenticated project-owned synthetic train/dev cached rectangles and detected "
            "plot/divider geometry only."
        ),
        "inputs": {
            "aligned_result": {
                "path": _repository_path(aligned_path),
                "sha256": arguments.aligned_result_sha256,
            },
            "historical_runtime_manifest": historical,
            "capture_request": report["request"],
            "runtime_reports": runtime_reports,
        },
        "source_bindings": {
            _repository_path(Path(__file__)): _sha(Path(__file__)),
            _repository_path(Path(aligned.__file__)): _sha(Path(aligned.__file__)),
            _repository_path(Path(comparison.__file__)): _sha(Path(comparison.__file__)),
            _repository_path(Path(assembly.__file__)): _sha(Path(assembly.__file__)),
        },
        "rule": {
            "fixed_alignment_rule_retained": True,
            "maximum_vertical_center_offset_height_ratio":
                aligned.MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO,
            "additional_categorical_scope": (
                "The proposed union center X is strictly left of detected PlotBounds.Left, and "
                "its center Y is within inclusive detected PlotBounds.Top/Bottom."
            ),
            "grouping_inputs": ["raw detector rectangles", "detected plot bounds"],
            "uses_truth_or_semantic_role_to_form_groups": False,
            "parameter_sweeps": 0,
        },
        "preceding_mechanism": mechanism,
        "dev_participant_fragment_measurement": participant_measurement,
        "geometry_results": geometry_results,
        "interpretation_constraints": [
            "Participant pair grouping is geometry evidence only; recognition and role recovery were not synthesized or claimed.",
            "Truth and generator roles are used after grouping only for aggregate measurement and contamination audit.",
            "The trial does not change detector probabilities, the IoU matcher, acceptance gates, or production runtime.",
        ],
        "recognition_synthesis": False,
        "role_inference": False,
        "model_inference": False,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "new_model_revision": False,
        "production_runtime_changed": False,
        "acceptance_gate_changed": False,
        "production_approval": False,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True, allow_nan=False)
        stream.write("\n")
    print(json.dumps({
        "output": _repository_path(output_path),
        "sha256": _sha(output_path),
        "participant_pairs": participant_measurement,
        "train": {
            "delta_from_raw": geometry_results["train"]["delta_from_raw"],
            "merged_group_audit": geometry_results["train"]["merged_group_audit"],
        },
        "validation": {
            "delta_from_raw": geometry_results["validation"]["delta_from_raw"],
            "merged_group_audit": geometry_results["validation"]["merged_group_audit"],
        },
    }, sort_keys=True))


if __name__ == "__main__":
    main()
