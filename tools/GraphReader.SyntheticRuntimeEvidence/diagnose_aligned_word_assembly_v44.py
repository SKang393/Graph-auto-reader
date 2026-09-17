# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate one strong-alignment extension of generic-v1 OCR assembly."""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path
import sys
from typing import Any, Sequence


ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
for location in (ROOT, TOOLS):
    if str(location) not in sys.path:
        sys.path.insert(0, str(location))

import compare_word_fragment_collision_geometry_v1 as comparison  # noqa: E402
import diagnose_ocr_line_assembly_v44 as assembly  # noqa: E402
from ml.ocr.official_bakeoff import word_fragment_stress_v1 as stress  # noqa: E402


SCHEMA = "graphreader.goal22.ocr-v44-aligned-word-assembly-diagnostic.v1"
MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO = 0.10


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _verify(path: Path, expected: str, label: str) -> None:
    if len(expected) != 64 or not path.is_file() or _sha(path) != expected:
        raise RuntimeError(f"{label} identity changed: {path}")


def _repository_path(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def _center_offset_height_ratio(left: Any, right: Any) -> float:
    left_height = left.bottom - left.top
    right_height = right.bottom - right.top
    maximum_height = max(left_height, right_height)
    left_center = (left.top + left.bottom) / 2.0
    right_center = (right.top + right.bottom) / 2.0
    return abs(left_center - right_center) / max(1e-12, maximum_height)


def _can_merge(left: Any, right: Any) -> bool:
    return (
        assembly._can_merge(left, right)
        and _center_offset_height_ratio(left, right)
        <= MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO
    )


def _assemble_panel(panel: dict[str, Any]) -> tuple[assembly.RegionGroup, ...]:
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
                if not _can_merge(line.panel_box, candidate.panel_box):
                    continue
                panel_box = assembly._union(line.panel_box, candidate.panel_box)
                member_ids = tuple(sorted((*line.member_ids, *candidate.member_ids)))
                member_source_boxes = (*line.member_source_boxes, *candidate.member_source_boxes)
                line = assembly.RegionGroup(
                    f'{panel["panel_id"]}\naligned:{sha256(chr(10).join(member_ids).encode()).hexdigest()}',
                    panel["source_sha256"],
                    panel["panel_id"],
                    panel_box,
                    assembly._map_box(panel_box, panel["panel_to_source_matrix"]),
                    member_ids,
                    member_source_boxes,
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


def _delta(left: dict[str, Any], right: dict[str, Any]) -> dict[str, int]:
    keys = (
        "predicted_region_count", "true_positives", "false_positives", "false_negatives",
    )
    return {key: int(left[key]) - int(right[key]) for key in keys}


def _stress_positive_trial(
    preflight: dict[str, Any],
    capture: dict[str, Any],
    truth: dict[str, Any],
    score: dict[str, Any],
) -> dict[str, Any]:
    captured = {row["source_sha256"]: row for row in capture["sources"]}
    truths = {row["source_sha256"]: row for row in truth["truths"]}
    expected = {row["sha256"] for row in preflight["sources"]}
    if (len(captured) != len(capture["sources"])
            or len(truths) != len(truth["truths"])
            or set(captured) != expected or set(truths) != expected):
        raise RuntimeError("Stress positive inventory changed")
    assigned = 0
    accepted_generic = 0
    accepted_aligned = 0
    geometry_recoveries = 0
    rejected_by_alignment = 0
    for source_sha in sorted(expected):
        raw = tuple(stress._box(value) for value in captured[source_sha]["raw_boxes_ltrb"])
        truth_row = truths[source_sha]["positive_truth"]
        token_truths = tuple(stress._box(value) for value in truth_row["token_boxes_ltrb"])
        pair = stress._best_assigned_pair(raw, token_truths)
        if pair is None:
            continue
        assigned += 1
        left = assembly.metric.Box(*raw[pair[0]])
        right = assembly.metric.Box(*raw[pair[1]])
        generic = assembly._can_merge(left, right)
        aligned = _can_merge(left, right)
        accepted_generic += int(generic)
        accepted_aligned += int(aligned)
        rejected_by_alignment += int(generic and not aligned)
        logical_truth = stress._box(truth_row["box_ltrb"])
        union = stress._union((raw[pair[0]], raw[pair[1]]))
        geometry_recoveries += int(aligned and stress._iou(union, logical_truth) >= 0.5)
    if assigned != score["positive_pair_count"]:
        raise RuntimeError("Stress assigned-positive count changed")
    return {
        "source_count": len(expected),
        "assigned_positive_pair_count": assigned,
        "positive_without_assigned_pair_count": len(expected) - assigned,
        "accepted_by_generic_v1_count": accepted_generic,
        "accepted_by_aligned_rule_count": accepted_aligned,
        "generic_pairs_rejected_only_by_alignment_count": rejected_by_alignment,
        "accepted_pairs_with_union_iou_at_least_0_5_count": geometry_recoveries,
        "recovery_definition": (
            "An assigned positive pair is a geometry recovery only when the aligned rule accepts it "
            "and its source-pixel union reaches IoU 0.5 with the local logical truth."
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stress-preflight", required=True)
    parser.add_argument("--stress-preflight-sha256", required=True)
    parser.add_argument("--stress-capture", required=True)
    parser.add_argument("--stress-capture-sha256", required=True)
    parser.add_argument("--stress-score", required=True)
    parser.add_argument("--stress-score-sha256", required=True)
    parser.add_argument("--comparison", required=True)
    parser.add_argument("--comparison-sha256", required=True)
    parser.add_argument("--line-assembly", required=True)
    parser.add_argument("--line-assembly-sha256", required=True)
    parser.add_argument("--historical-runtime-manifest", required=True)
    parser.add_argument("--historical-runtime-manifest-sha256", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()

    preflight_path = comparison._inside_artifacts(arguments.stress_preflight)
    capture_path = comparison._inside_artifacts(arguments.stress_capture)
    stress_score_path = comparison._inside_artifacts(arguments.stress_score)
    comparison_path = comparison._inside_artifacts(arguments.comparison)
    line_path = comparison._inside_artifacts(arguments.line_assembly)
    historical_manifest_path = comparison._inside_artifacts(
        arguments.historical_runtime_manifest
    )
    output_path = comparison._inside_artifacts(arguments.output)
    if output_path.exists():
        raise RuntimeError("Use a new aligned-rule output path")

    preflight, capture, stress_truth, stress_score = comparison._authenticate_stress(
        preflight_path, arguments.stress_preflight_sha256,
        capture_path, arguments.stress_capture_sha256,
        stress_score_path, arguments.stress_score_sha256,
    )
    _verify(comparison_path, arguments.comparison_sha256, "fragment collision comparison")
    comparison_result = _read(comparison_path)
    expected_comparison_inputs = {
        "stress_preflight": {"path": _repository_path(preflight_path),
                              "sha256": arguments.stress_preflight_sha256},
        "stress_capture": {"path": _repository_path(capture_path),
                            "sha256": arguments.stress_capture_sha256},
        "stress_score": {"path": _repository_path(stress_score_path),
                          "sha256": arguments.stress_score_sha256},
        "line_assembly": {"path": _repository_path(line_path),
                          "sha256": arguments.line_assembly_sha256},
        "historical_runtime_manifest": {
            "path": _repository_path(historical_manifest_path),
            "sha256": arguments.historical_runtime_manifest_sha256,
        },
    }
    if (comparison_result.get("schema")
            != "graphreader.word-fragment-collision-comparison-v1"
            or comparison_result.get("status") != "train_only_source_pixel_geometry_compared"
            or comparison_result.get("coordinate_space")
            != "original_source_pixels_for_both_populations"
            or comparison_result.get("inputs") != expected_comparison_inputs
            or comparison_result.get("stress_positive_pair_count") != 9
            or comparison_result.get("v44_actual_cross_truth_first_crossing_count") != 21):
        raise RuntimeError("Fragment collision comparison binding changed")
    for relative, digest in comparison_result["source_bindings"].items():
        _verify(comparison._inside_root(relative), digest,
                "fragment collision comparison source")

    center_distributions = comparison_result["feature_distributions"][
        "vertical_center_offset_height_ratio"
    ]
    expected_center_distributions = {
        "stress_positive_fragments": {
            "count": 9,
            "minimum": 0.0,
            "median": 0.038461538461538464,
            "maximum": 0.16666666666666666,
        },
        "v44_actual_cross_truth_first_crossings": {
            "count": 21,
            "minimum": 0.13157894736842105,
            "median": 0.13636363636363635,
            "maximum": 0.5384615384615384,
        },
    }
    if center_distributions != expected_center_distributions:
        raise RuntimeError("Train-informed vertical-center evidence changed")

    _verify(line_path, arguments.line_assembly_sha256, "generic-v1 line assembly result")
    line_result = _read(line_path)
    if (line_result.get("schema")
            != "graphreader.goal22.ocr-v44-line-assembly-feasibility.v1"
            or line_result.get("status") != "geometry_only_experiment_complete"):
        raise RuntimeError("Generic-v1 line assembly result binding changed")
    for descriptor in line_result["inputs"]:
        _verify(comparison._inside_root(descriptor["path"]), descriptor["sha256"],
                "generic-v1 input")
    score, report, v44_preflight, _ = comparison._authenticate_historical_v44(
        historical_manifest_path, arguments.historical_runtime_manifest_sha256
    )
    truths_by_split = assembly._load_truths(v44_preflight, score)
    raw_by_split = {"train": [], "validation": []}
    aligned_by_split = {"train": [], "validation": []}
    for panel in report["panels"]:
        if panel["status"] != "completed":
            raise RuntimeError("Aligned trial requires every V44 panel to be completed")
        split = panel["split"]
        raw_by_split[split].extend(assembly._raw_groups(panel))
        aligned_by_split[split].extend(_assemble_panel(panel))

    split_results = {}
    for split in ("train", "validation"):
        aligned = assembly._score_geometry(
            split,
            truths_by_split[split],
            tuple(raw_by_split[split]),
            tuple(aligned_by_split[split]),
        )
        generic = line_result["geometry_results"][split]
        if aligned["baseline"] != generic["baseline"]:
            raise RuntimeError(f"{split} raw baseline changed")
        split_results[split] = {
            "full_truth_denominator": len(truths_by_split[split]),
            "raw_baseline": aligned["baseline"],
            "generic_v1": generic["assembled"],
            "aligned_rule": aligned["assembled"],
            "aligned_delta_from_raw": aligned["delta"],
            "aligned_delta_from_generic_v1": _delta(
                aligned["assembled"], generic["assembled"]
            ),
            "aligned_recovered_truth_count": aligned["recovered_truth_count"],
            "aligned_lost_truth_count": aligned["lost_truth_count"],
            "generic_v1_cross_truth_merger_count": generic[
                "accidental_merger_audit"
            ]["groups_whose_members_map_to_different_truths"],
            "aligned_cross_truth_merger_count": aligned[
                "accidental_merger_audit"
            ]["groups_whose_members_map_to_different_truths"],
            "aligned_cross_truth_role_sets": aligned[
                "accidental_merger_audit"
            ]["cross_truth_generator_role_sets"],
            "by_generator_role": aligned["by_generator_role"],
        }

    stress_trial = _stress_positive_trial(
        preflight, capture, stress_truth, stress_score
    )
    result = {
        "schema": SCHEMA,
        "status": "cached_train_dev_geometry_trial_complete",
        "scope": (
            "Authenticated project-owned synthetic train/dev cached geometry plus the bounded "
            "train-only word-fragment stress capture."
        ),
        "inputs": expected_comparison_inputs | {
            "comparison": {"path": _repository_path(comparison_path),
                           "sha256": arguments.comparison_sha256},
        },
        "source_bindings": {
            _repository_path(Path(__file__)): _sha(Path(__file__)),
            _repository_path(Path(comparison.__file__)): _sha(Path(comparison.__file__)),
            _repository_path(Path(assembly.__file__)): _sha(Path(assembly.__file__)),
            _repository_path(Path(stress.__file__)): _sha(Path(stress.__file__)),
        },
        "rule": {
            "basis": "generic-v1 criteria plus one strong vertical-center alignment condition",
            "generic_v1_criteria_retained_unchanged": True,
            "maximum_vertical_center_offset_height_ratio":
                MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO,
            "formula": "abs(center_y_left-center_y_right)/max(height_left,height_right) <= 0.10",
            "selection": (
                "One train-informed engineering trial. Threshold 0.10 was fixed from the "
                f"authenticated comparison where stress positives had median "
                f"{center_distributions['stress_positive_fragments']['median']:.5f} and actual "
                f"generic-v1 cross-truth train crossings had minimum "
                f"{center_distributions['v44_actual_cross_truth_first_crossings']['minimum']:.5f}. "
                "No threshold sweep was performed; dev was evaluated once after fixing the rule."
            ),
            "uses_text_or_truth_to_form_groups": False,
            "coordinate_space": "panel pixels before source projection and source-space scoring",
            "stress_coordinate_space": (
                "original source pixels; the normalized alignment ratio is translation invariant"
            ),
        },
        "stress_positive_geometry": stress_trial,
        "geometry_results": split_results,
        "interpretation_constraints": [
            "A recovered truth means only that the assembled source rectangle reaches IoU 0.5.",
            "The trial does not concatenate recognized strings, synthesize recognition, infer roles, or change detector operating thresholds.",
            "The controlled stress evidence and cached train/dev geometry do not approve production behavior or change an acceptance gate.",
        ],
        "recognition_synthesis": False,
        "role_inference": False,
        "detector_operating_thresholds_changed": False,
        "acceptance_gate_changed": False,
        "model_inference": False,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "new_model_revision": False,
        "production_runtime_changed": False,
        "production_approval": False,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True, allow_nan=False)
        stream.write("\n")
    print(json.dumps({
        "output": _repository_path(output_path),
        "sha256": _sha(output_path),
        "stress": stress_trial,
        "train": {
            "delta_from_generic_v1": split_results["train"]["aligned_delta_from_generic_v1"],
            "cross_truth_mergers": split_results["train"]["aligned_cross_truth_merger_count"],
        },
        "validation": {
            "delta_from_generic_v1": split_results["validation"]["aligned_delta_from_generic_v1"],
            "cross_truth_mergers": split_results["validation"]["aligned_cross_truth_merger_count"],
        },
    }, sort_keys=True))


if __name__ == "__main__":
    main()
