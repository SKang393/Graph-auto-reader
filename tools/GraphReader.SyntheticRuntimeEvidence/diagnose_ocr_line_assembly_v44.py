# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate one frozen geometry-only OCR line-assembly rule on V44 raw boxes."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
from hashlib import sha256
import itertools
import json
import math
from pathlib import Path
import statistics
import sys
from typing import Any, Iterable, Sequence


ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import score_full_ocr_candidate_v2 as metric  # noqa: E402
from ml.ocr.component_context_detector_v7.dataset import box_iou  # noqa: E402


RUN_DIR = ROOT / "artifacts/goal22-runs/ocr-v44-text-extent"
SCORE_PATH = RUN_DIR / "full-score-run1.json"
REPORT_PATH = RUN_DIR / "application-evaluation-run1/report.json"
PREFLIGHT_PATH = ROOT / "artifacts/goal22-runs/ocr-text-extent-preflight/preflight-report.json"
RESULT_PATH = ROOT / "ml/ocr/text_extent_db_head_v44/P1_RESULT.json"
EXPECTED_RESULT_SHA256 = "0fc4420747c3b7abf3259ffb9233676d6bc8c291dc54342b8571f335461d9024"
V43_DOCUMENT_PATH = ROOT / "docs/GOAL-22-V43-REPRESENTATION-DIAGNOSIS.json"
PROPOSAL_SOURCE_PATH = ROOT / "src/GraphReader.Ocr/LocalOnnxProposalTextRegionDetector.cs"
CONNECTED_COMPONENT_SOURCE_PATH = ROOT / "src/GraphReader.Ocr/ConnectedComponentTextRegionDetector.cs"
EXPECTED_RULE_SOURCE_SHA256 = {
    PROPOSAL_SOURCE_PATH: "b82246c047fa01ab88908aac97dc7db8f3b34994e6556ba3f3dfb054aa0b5eca",
    CONNECTED_COMPONENT_SOURCE_PATH: "c77e65eb53bb9d15bc94b2956a96b87cfdcdd79509cbf87d2beada3b29afc3c4",
}
RELEVANT_SNAPSHOT_SOURCES = frozenset({
    "ml/markers/center/mask_preserving_v24/runtime_inputs.py",
    "ml/markers/center/plot_domain_v25/runtime_domain_binding_v3.py",
    "ml/ocr/official_bakeoff/text_extent_head_inputs.py",
    "ml/ocr/production_tiled_inputs.py",
    "ml/synthetic/dataset.py",
    "ml/synthetic/io.py",
    "ml/synthetic/runtime_graph_visible_content_v3.py",
    "ml/synthetic/templates.py",
    "tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py",
    "tools/GraphReader.SyntheticRuntimeEvidence/score_text_extent_candidate.py",
})

# Frozen before train or dev scoring. These are the reviewed generic defaults in
# LocalOnnxProposalTextRegionDetector; ConnectedComponentTextRegionDetector uses
# the same vertical-overlap and horizontal-gap defaults. The experiment applies
# their continuous rectangle analogue to complete DB regions in panel space.
MINIMUM_VERTICAL_OVERLAP_RATIO = 0.35
MAXIMUM_HORIZONTAL_GAP_HEIGHT_RATIO = 2.5
MAXIMUM_COMPONENT_HEIGHT_RATIO_WITHIN_LINE = 2.0
MAXIMUM_MERGED_HEIGHT_GROWTH_RATIO = 1.6


@dataclass(frozen=True)
class RegionGroup:
    prediction_id: str
    source_sha256: str
    panel_id: str
    panel_box: metric.Box
    source_box: metric.Box
    member_ids: tuple[str, ...]
    member_source_boxes: tuple[metric.Box, ...]


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _verify(path: Path, expected_sha256: str, label: str) -> None:
    if not path.is_file() or _sha(path) != expected_sha256:
        raise RuntimeError(f"{label} identity changed: {path}")


def _descriptor(document: dict[str, Any], key: str) -> tuple[Path, str]:
    value = document[key]
    path = (ROOT / value["path"]).resolve()
    if ROOT not in path.parents or len(value["sha256"]) != 64:
        raise RuntimeError(f"{key} descriptor escaped the repository or has an invalid digest")
    _verify(path, value["sha256"], key)
    return path, value["sha256"]


def _box_from_polygon(value: dict[str, Any]) -> metric.Box:
    points = value["points"]
    return metric.Box(
        min(float(point["x"]) for point in points),
        min(float(point["y"]) for point in points),
        max(float(point["x"]) for point in points),
        max(float(point["y"]) for point in points),
    )


def _union(left: metric.Box, right: metric.Box) -> metric.Box:
    return metric.Box(
        min(left.left, right.left), min(left.top, right.top),
        max(left.right, right.right), max(left.bottom, right.bottom),
    )


def _height(box: metric.Box) -> float:
    return box.bottom - box.top


def _intersects(left: metric.Box, right: metric.Box) -> bool:
    return (min(left.right, right.right) > max(left.left, right.left)
            and min(left.bottom, right.bottom) > max(left.top, right.top))


def _pair_geometry(left: metric.Box, right: metric.Box) -> dict[str, float]:
    minimum_height = max(1e-12, min(_height(left), _height(right)))
    maximum_height = max(_height(left), _height(right))
    overlap = max(0.0, min(left.bottom, right.bottom) - max(left.top, right.top))
    gap = (left.left - right.right if left.left > right.right
           else right.left - left.right if right.left > left.right else 0.0)
    merged = _union(left, right)
    return {
        "minimum_height": minimum_height,
        "maximum_height": maximum_height,
        "height_ratio": maximum_height / minimum_height,
        "vertical_overlap_ratio": overlap / minimum_height,
        "horizontal_gap_pixels": gap,
        "horizontal_gap_height_ratio": gap / max(1e-12, maximum_height),
        "merged_height_growth_ratio": _height(merged) / max(1e-12, maximum_height),
    }


def _can_merge(left: metric.Box, right: metric.Box) -> bool:
    values = _pair_geometry(left, right)
    return (
        values["height_ratio"] <= MAXIMUM_COMPONENT_HEIGHT_RATIO_WITHIN_LINE
        and values["vertical_overlap_ratio"] >= MINIMUM_VERTICAL_OVERLAP_RATIO
        and values["horizontal_gap_height_ratio"] <= MAXIMUM_HORIZONTAL_GAP_HEIGHT_RATIO
        and values["merged_height_growth_ratio"] <= MAXIMUM_MERGED_HEIGHT_GROWTH_RATIO
    )


def _map_box(box: metric.Box, matrix: Sequence[float]) -> metric.Box:
    points = (
        (box.left, box.top), (box.right, box.top),
        (box.right, box.bottom), (box.left, box.bottom),
    )
    mapped = []
    for x, y in points:
        denominator = matrix[6] * x + matrix[7] * y + matrix[8]
        if not math.isfinite(denominator) or abs(denominator) < 1e-12:
            raise RuntimeError("panel-to-source transform is singular")
        mapped.append((
            (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
            (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator,
        ))
    return metric.Box(
        min(point[0] for point in mapped), min(point[1] for point in mapped),
        max(point[0] for point in mapped), max(point[1] for point in mapped),
    )


def _raw_groups(panel: dict[str, Any]) -> tuple[RegionGroup, ...]:
    result = []
    for row in panel["raw_detector_regions"]:
        identity = f'{panel["panel_id"]}\n{row["region_id"]}'
        result.append(RegionGroup(
            identity,
            panel["source_sha256"],
            panel["panel_id"],
            _box_from_polygon(row["panel_polygon"]),
            _box_from_polygon(row["source_polygon"]),
            (identity,),
            (_box_from_polygon(row["source_polygon"]),),
        ))
    return tuple(result)


def _assemble_panel(panel: dict[str, Any]) -> tuple[RegionGroup, ...]:
    remaining = sorted(
        _raw_groups(panel),
        key=lambda row: (row.panel_box.top, row.panel_box.left,
                         row.panel_box.bottom, row.panel_box.right),
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
                panel_box = _union(line.panel_box, candidate.panel_box)
                member_ids = tuple(sorted((*line.member_ids, *candidate.member_ids)))
                member_source_boxes = (*line.member_source_boxes, *candidate.member_source_boxes)
                line = RegionGroup(
                    f'{panel["panel_id"]}\nassembled:{sha256(chr(10).join(member_ids).encode()).hexdigest()}',
                    panel["source_sha256"],
                    panel["panel_id"],
                    panel_box,
                    _map_box(panel_box, panel["panel_to_source_matrix"]),
                    member_ids,
                    member_source_boxes,
                )
                remaining.pop(index)
                changed = True
        assembled.append(line)
    return tuple(sorted(
        assembled,
        key=lambda row: (row.panel_box.top, row.panel_box.left,
                         row.panel_box.bottom, row.panel_box.right),
    ))


def _load_truths(
    preflight: dict[str, Any],
    score: dict[str, Any],
) -> dict[str, tuple[metric.FullTextTruth, ...]]:
    train_descriptor = score["inputs"]["saved_train_truth"]
    if train_descriptor != preflight["train_text_truth"]:
        raise RuntimeError("score and preflight bind different saved train truth")
    train_path, _ = _descriptor({"truth": train_descriptor}, "truth")
    train_document = _read(train_path)
    train = tuple(
        metric.FullTextTruth(
            row["truth_id"], row["source_sha256"], metric.Box(*row["source_box_ltrb"]),
            "", row["role"], metric._canonical_role(row["role"]),
        )
        for row in train_document["truths"]
    )
    dev_descriptor = preflight["historical_dev"]["synthetic_truth"]
    dev_path, _ = _descriptor({"truth": dev_descriptor}, "truth")
    dev_document = _read(dev_path)
    dev = tuple(
        metric.FullTextTruth(
            row["truth_id"], row["source_sha256"], metric.Box(*row["box"]),
            "", row["role"], metric._canonical_role(row["role"]),
        )
        for row in dev_document["truths"] if row["split"] == "validation"
    )
    if len(train) != 709 or len(dev) != 183:
        raise RuntimeError("line-assembly experiment did not retain the full truth denominator")
    return {"train": train, "validation": dev}


def _panel_truths(
    report: dict[str, Any],
    truths_by_split: dict[str, tuple[metric.FullTextTruth, ...]],
) -> dict[str, tuple[metric.FullTextTruth, ...]]:
    result: dict[str, list[metric.FullTextTruth]] = defaultdict(list)
    panels = report["panels"]
    for split, truths in truths_by_split.items():
        for truth in truths:
            center_x = (truth.box.left + truth.box.right) / 2.0
            center_y = (truth.box.top + truth.box.bottom) / 2.0
            owners = []
            for panel in panels:
                if panel["split"] != split or panel["source_sha256"] != truth.source_sha256:
                    continue
                crop = panel["crop"]
                if (crop["x"] <= center_x <= crop["x"] + crop["width"]
                        and crop["y"] <= center_y <= crop["y"] + crop["height"]):
                    owners.append(panel["panel_id"])
            if len(owners) != 1:
                raise RuntimeError(
                    f"truth must have exactly one panel owner, observed {len(owners)}"
                )
            result[owners[0]].append(truth)
    return {panel_id: tuple(rows) for panel_id, rows in result.items()}


def _summary(values: Iterable[float]) -> dict[str, float | None]:
    rows = list(values)
    return {
        "minimum": min(rows) if rows else None,
        "median": statistics.median(rows) if rows else None,
        "maximum": max(rows) if rows else None,
    }


def _participant_feasibility(
    report: dict[str, Any],
    panel_truths: dict[str, tuple[metric.FullTextTruth, ...]],
) -> dict[str, Any]:
    measurements = []
    participants = 0
    with_two_regions = 0
    accepted = 0
    for panel in report["panels"]:
        if panel["split"] != "train":
            continue
        raw = _raw_groups(panel)
        for truth in panel_truths.get(panel["panel_id"], ()):
            if truth.generator_role != "participant":
                continue
            participants += 1
            intersecting = [row for row in raw if _intersects(row.source_box, truth.box)]
            if len(intersecting) < 2:
                continue
            with_two_regions += 1
            candidates = []
            for left, right in itertools.combinations(intersecting, 2):
                combined = _union(left.source_box, right.source_box)
                candidates.append((box_iou(combined, truth.box), left, right))
            _, left, right = max(candidates, key=lambda row: row[0])
            geometry = _pair_geometry(left.panel_box, right.panel_box)
            geometry["combined_source_iou_with_truth"] = box_iou(
                _union(left.source_box, right.source_box), truth.box
            )
            geometry["accepted_by_frozen_rule"] = _can_merge(left.panel_box, right.panel_box)
            accepted += int(geometry["accepted_by_frozen_rule"])
            measurements.append(geometry)
    numeric_keys = (
        "horizontal_gap_pixels", "horizontal_gap_height_ratio", "height_ratio",
        "vertical_overlap_ratio", "merged_height_growth_ratio",
        "combined_source_iou_with_truth",
    )
    return {
        "selection": (
            "For aggregate train-only feasibility, choose the two intersecting raw regions whose "
            "source-space union has highest IoU with each participant truth. Truth is not used by "
            "the assembly rule."
        ),
        "participant_truth_count": participants,
        "truths_with_at_least_two_intersecting_raw_regions": with_two_regions,
        "best_pairs_measured": len(measurements),
        "best_pairs_accepted_by_frozen_rule": accepted,
        "geometry": {
            key: _summary(float(row[key]) for row in measurements) for key in numeric_keys
        },
    }


def _as_prediction(group: RegionGroup) -> metric.FullTextPrediction:
    return metric.FullTextPrediction(
        group.prediction_id, group.source_sha256, group.source_box, "", "other"
    )


def _group_accidentals(
    groups: Sequence[RegionGroup],
    truths: Sequence[metric.FullTextTruth],
) -> dict[str, Any]:
    truths_by_source: dict[str, list[metric.FullTextTruth]] = defaultdict(list)
    for truth in truths:
        truths_by_source[truth.source_sha256].append(truth)
    merged = [group for group in groups if len(group.member_ids) > 1]
    cross_truth_groups = 0
    spans_multiple_truths = 0
    cross_role_sets: Counter[str] = Counter()
    for group in merged:
        source_truths = truths_by_source[group.source_sha256]
        member_truths = set()
        member_roles = set()
        for member_box in group.member_source_boxes:
            overlapping = [truth for truth in source_truths if _intersects(member_box, truth.box)]
            if not overlapping:
                continue
            nearest = max(overlapping, key=lambda truth: box_iou(member_box, truth.box))
            member_truths.add(nearest.truth_id)
            member_roles.add(nearest.generator_role)
        if len(member_truths) > 1:
            cross_truth_groups += 1
            cross_role_sets["+".join(sorted(member_roles))] += 1
        if sum(_intersects(group.source_box, truth.box) for truth in source_truths) > 1:
            spans_multiple_truths += 1
    return {
        "merged_group_count": len(merged),
        "raw_regions_consumed_by_merged_groups": sum(len(group.member_ids) for group in merged),
        "groups_whose_members_map_to_different_truths": cross_truth_groups,
        "groups_whose_union_intersects_multiple_truths": spans_multiple_truths,
        "cross_truth_generator_role_sets": dict(sorted(cross_role_sets.items())),
    }


def _score_geometry(
    split: str,
    truths: tuple[metric.FullTextTruth, ...],
    raw_groups: tuple[RegionGroup, ...],
    assembled_groups: tuple[RegionGroup, ...],
) -> dict[str, Any]:
    truths_by_source: dict[str, list[metric.FullTextTruth]] = defaultdict(list)
    raw_by_source: dict[str, list[RegionGroup]] = defaultdict(list)
    assembled_by_source: dict[str, list[RegionGroup]] = defaultdict(list)
    for truth in truths:
        truths_by_source[truth.source_sha256].append(truth)
    for group in raw_groups:
        raw_by_source[group.source_sha256].append(group)
    for group in assembled_groups:
        assembled_by_source[group.source_sha256].append(group)

    baseline_matches = set()
    assembled_matches = set()
    assembled_match_groups: dict[str, RegionGroup] = {}
    for source in sorted(truths_by_source):
        source_truths = tuple(truths_by_source[source])
        baseline = tuple(_as_prediction(row) for row in raw_by_source.get(source, ()))
        assembled = tuple(_as_prediction(row) for row in assembled_by_source.get(source, ()))
        for _, truth_index in metric._maximum_cardinality_pairs(baseline, source_truths):
            baseline_matches.add(source_truths[truth_index].truth_id)
        for prediction_index, truth_index in metric._maximum_cardinality_pairs(assembled, source_truths):
            truth = source_truths[truth_index]
            assembled_matches.add(truth.truth_id)
            assembled_match_groups[truth.truth_id] = assembled_by_source[source][prediction_index]

    recovered = assembled_matches - baseline_matches
    lost = baseline_matches - assembled_matches
    by_role = {}
    for role in sorted({truth.generator_role for truth in truths}):
        role_truths = [truth for truth in truths if truth.generator_role == role]
        ids = {truth.truth_id for truth in role_truths}
        by_role[role] = {
            "truth_count": len(ids),
            "baseline_matched_count": len(ids & baseline_matches),
            "assembled_matched_count": len(ids & assembled_matches),
            "recovered_count": len(ids & recovered),
            "lost_count": len(ids & lost),
            "recovered_by_multi_region_group_count": sum(
                len(assembled_match_groups[truth_id].member_ids) > 1
                for truth_id in ids & recovered
            ),
        }

    def totals(prediction_count: int, matched: int) -> dict[str, Any]:
        return {
            "truth_region_count": len(truths),
            "predicted_region_count": prediction_count,
            "true_positives": matched,
            "false_positives": prediction_count - matched,
            "false_negatives": len(truths) - matched,
            "precision": matched / max(1, prediction_count),
            "recall": matched / max(1, len(truths)),
            "intersection_over_union_minimum": metric.MATCH_IOU_MINIMUM,
        }

    return {
        "split": split,
        "baseline": totals(len(raw_groups), len(baseline_matches)),
        "assembled": totals(len(assembled_groups), len(assembled_matches)),
        "delta": {
            "predicted_region_count": len(assembled_groups) - len(raw_groups),
            "true_positives": len(assembled_matches) - len(baseline_matches),
            "false_positives": (
                len(assembled_groups) - len(assembled_matches)
                - (len(raw_groups) - len(baseline_matches))
            ),
            "false_negatives": (
                len(truths) - len(assembled_matches)
                - (len(truths) - len(baseline_matches))
            ),
        },
        "recovered_truth_count": len(recovered),
        "lost_truth_count": len(lost),
        "by_generator_role": by_role,
        "accidental_merger_audit": _group_accidentals(assembled_groups, truths),
    }


def _authenticate() -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], Path]:
    _verify(RESULT_PATH, EXPECTED_RESULT_SHA256, "tracked V44 outcome")
    outcome = _read(RESULT_PATH)
    if (outcome.get("schema") != "graphreader.ocr-text-extent-v44-outcome.v1"
            or outcome.get("revision") != "graph-text-extent-db-head-v44"
            or outcome.get("status") != "failed_dev_unconsumed"):
        raise RuntimeError("tracked V44 outcome identity or status changed")
    evidence = outcome["evidence"]
    for key in ("full_score", "application_evaluation", "candidate", "protocol",
                "source_snapshot", "acceptance_bars"):
        _descriptor(evidence, key)
    if ((ROOT / evidence["full_score"]["path"]).resolve() != SCORE_PATH
            or (ROOT / evidence["application_evaluation"]["path"]).resolve() != REPORT_PATH):
        raise RuntimeError("tracked outcome references a different score or application report")

    score = _read(SCORE_PATH)
    report = _read(REPORT_PATH)
    if (score["inputs"]["evaluation_report"] != evidence["application_evaluation"]
            or score["inputs"]["candidate"] != evidence["candidate"]):
        raise RuntimeError("score evidence differs from the tracked outcome")
    binding_path, binding_sha = _descriptor(score["inputs"], "binding")
    if binding_path != PREFLIGHT_PATH.resolve():
        raise RuntimeError("score binds a different preflight")
    preflight = _read(PREFLIGHT_PATH)
    for relative, digest in score["inputs"]["source_bindings"].items():
        _verify((ROOT / relative).resolve(), digest, f"full-score source {relative}")

    snapshot_path = (ROOT / evidence["source_snapshot"]["path"]).resolve()
    snapshot = _read(snapshot_path)
    snapshot_sources = {row["path"]: row["sha256"] for row in snapshot["sources"]}
    if not RELEVANT_SNAPSHOT_SOURCES.issubset(snapshot_sources):
        raise RuntimeError("V44 source snapshot omits a generator or metric dependency")
    for relative in sorted(RELEVANT_SNAPSHOT_SOURCES):
        _verify((ROOT / relative).resolve(), snapshot_sources[relative], f"snapshot source {relative}")
    v43_digest = snapshot_sources.get(V43_DOCUMENT_PATH.relative_to(ROOT).as_posix())
    if v43_digest is None:
        raise RuntimeError("V44 source snapshot omits the V43 diagnosis")
    _verify(V43_DOCUMENT_PATH, v43_digest, "V43 representation diagnosis")
    for path, digest in EXPECTED_RULE_SOURCE_SHA256.items():
        _verify(path, digest, f"frozen line-grouping precedent {path.name}")

    inputs = score["inputs"]
    validated = metric.geometry._validate_before_truth(
        ROOT,
        binding_path,
        binding_sha,
        (ROOT / inputs["capture_report"]["path"]).resolve(),
        inputs["capture_report"]["sha256"],
        (ROOT / inputs["capture_request"]["path"]).resolve(),
        inputs["capture_request"]["sha256"],
        (ROOT / inputs["candidate"]["path"]).resolve(),
        inputs["candidate"]["sha256"],
        REPORT_PATH,
        inputs["evaluation_report"]["sha256"],
    )
    if validated.report != report:
        raise RuntimeError("authenticated report parse differs from direct report parse")
    return score, report, preflight, snapshot_path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()
    output_path = Path(arguments.output).resolve()
    if output_path.parent != RUN_DIR.resolve() or output_path.exists():
        raise RuntimeError("Output must be a new JSON artifact in the V44 run directory")

    score, report, preflight, snapshot_path = _authenticate()
    truths_by_split = _load_truths(preflight, score)
    panel_truths = _panel_truths(report, truths_by_split)
    raw_by_split: dict[str, list[RegionGroup]] = defaultdict(list)
    assembled_by_split: dict[str, list[RegionGroup]] = defaultdict(list)
    for panel in report["panels"]:
        if panel["status"] != "completed":
            raise RuntimeError("line-assembly experiment requires every V44 panel to be completed")
        raw_by_split[panel["split"]].extend(_raw_groups(panel))
        assembled_by_split[panel["split"]].extend(_assemble_panel(panel))

    train_feasibility = _participant_feasibility(report, panel_truths)
    split_results = {}
    for split in ("train", "validation"):
        split_results[split] = _score_geometry(
            split,
            truths_by_split[split],
            tuple(raw_by_split[split]),
            tuple(assembled_by_split[split]),
        )
        frozen = score["raw_detector_geometry"][split]
        observed = split_results[split]["baseline"]
        comparable = {
            "truth_region_count": frozen["truth_region_count"],
            "predicted_region_count": frozen["predicted_region_count"],
            "true_positives": frozen["true_positives"],
            "false_positives": frozen["false_positives"],
            "false_negatives": frozen["false_negatives"],
        }
        if any(observed[key] != value for key, value in comparable.items()):
            raise RuntimeError(f"{split} raw geometry differs from the frozen V44 score")

    result = {
        "schema": "graphreader.goal22.ocr-v44-line-assembly-feasibility.v1",
        "status": "geometry_only_experiment_complete",
        "scope": (
            "Authenticated project-owned synthetic train/dev raw boxes. Zero inference, optimizer "
            "work, recognition synthesis, private reads, sealed reads, threshold selection, or model selection."
        ),
        "inputs": [
            {"path": SCORE_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(SCORE_PATH)},
            {"path": REPORT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(REPORT_PATH)},
            {"path": PREFLIGHT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(PREFLIGHT_PATH)},
            {"path": RESULT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(RESULT_PATH)},
            {"path": snapshot_path.relative_to(ROOT).as_posix(), "sha256": _sha(snapshot_path)},
            {"path": V43_DOCUMENT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(V43_DOCUMENT_PATH)},
            {"path": PROPOSAL_SOURCE_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(PROPOSAL_SOURCE_PATH)},
            {"path": CONNECTED_COMPONENT_SOURCE_PATH.relative_to(ROOT).as_posix(),
             "sha256": _sha(CONNECTED_COMPONENT_SOURCE_PATH)},
            score["inputs"]["saved_train_truth"],
            preflight["historical_dev"]["synthetic_truth"],
        ],
        "authentication": {
            "tracked_outcome_sha256": EXPECTED_RESULT_SHA256,
            "outcome_reference_chain_verified": True,
            "score_preflight_candidate_capture_evaluation_chain_verified": True,
            "full_score_source_bindings_verified": len(score["inputs"]["source_bindings"]),
            "v44_snapshot_sources_verified": sorted(RELEVANT_SNAPSHOT_SOURCES),
            "v43_diagnosis_binding_verified": True,
            "full_truth_denominators": {"train": 709, "validation": 183},
        },
        "frozen_rule": {
            "selection_time": "module constants fixed before authenticated truth is loaded or either split is scored",
            "basis": (
                "Continuous rectangle analogue of the generic line grouping defaults already used by "
                "LocalOnnxProposalTextRegionDetector and ConnectedComponentTextRegionDetector."
            ),
            "uses_text_or_truth_to_form_groups": False,
            "coordinate_space": "panel pixels before source projection and source-space matching",
            "minimum_vertical_overlap_ratio": MINIMUM_VERTICAL_OVERLAP_RATIO,
            "maximum_horizontal_gap_height_ratio": MAXIMUM_HORIZONTAL_GAP_HEIGHT_RATIO,
            "maximum_component_height_ratio_within_line": MAXIMUM_COMPONENT_HEIGHT_RATIO_WITHIN_LINE,
            "maximum_merged_height_growth_ratio": MAXIMUM_MERGED_HEIGHT_GROWTH_RATIO,
            "application_order": "train and validation independently; every raw DB region in every panel",
        },
        "train_participant_pair_feasibility_before_dev_review": train_feasibility,
        "geometry_results": split_results,
        "interpretation_constraints": [
            "A recovered geometry pair means only that the assembled rectangle reaches IoU 0.5.",
            "The experiment does not concatenate recognized strings, invent recognition output, classify a role, or estimate complete-OCR accuracy.",
            "Cross-truth member mappings and multi-truth union intersections are aggregate collision indicators; a runtime implementation must reject or resolve them generically before adoption.",
            "Validation is applied once with the frozen train-independent rule. No rule sweep or threshold selection is performed.",
        ],
        "optimizer_steps": 0,
        "model_inference": False,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_runtime_changed": False,
        "production_approval": False,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True, allow_nan=False)
        stream.write("\n")
    print(json.dumps({
        "output": output_path.relative_to(ROOT).as_posix(),
        "sha256": _sha(output_path),
        "train": split_results["train"]["delta"],
        "validation": split_results["validation"]["delta"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
