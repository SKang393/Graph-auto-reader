# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare stress fragments with authenticated V44 cross-truth merge geometry."""

from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path
import statistics
import sys
from typing import Any, Iterable, Mapping, Sequence


ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
for location in (ROOT, TOOLS):
    if str(location) not in sys.path:
        sys.path.insert(0, str(location))

import diagnose_ocr_line_assembly_v44 as assembly  # noqa: E402
from ml.ocr.official_bakeoff import word_fragment_stress_v1 as stress  # noqa: E402
from ml.ocr.component_context_detector_v7.dataset import box_iou  # noqa: E402


SCHEMA = "graphreader.word-fragment-collision-comparison-v1"
FEATURES = (
    "horizontal_gap_pixels",
    "horizontal_gap_height_ratio",
    "vertical_overlap_ratio",
    "height_ratio",
    "merged_height_growth_ratio",
    "bottom_edge_offset_height_ratio",
    "top_edge_offset_height_ratio",
    "vertical_center_offset_height_ratio",
)


@dataclass(frozen=True)
class TracedGroup:
    group: assembly.RegionGroup
    truth_ids: frozenset[str]
    roles: frozenset[str]
    first_crossing: Mapping[str, Any] | None


def _sha(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _inside_root(value: str | Path) -> Path:
    path = (ROOT / value).resolve() if not Path(value).is_absolute() else Path(value).resolve()
    if path != ROOT and ROOT not in path.parents:
        raise RuntimeError(f"Path escaped repository: {value}")
    return path


def _inside_artifacts(value: str | Path) -> Path:
    path = _inside_root(value)
    artifacts = (ROOT / "artifacts").resolve()
    if path == artifacts or artifacts not in path.parents:
        raise RuntimeError(f"Path must be below artifacts: {value}")
    return path


def _verify(path: Path, expected: str, label: str) -> None:
    if len(expected) != 64 or not path.is_file() or _sha(path) != expected:
        raise RuntimeError(f"{label} identity changed: {path}")


def _repository_path(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def _box_features(left: Sequence[float], right: Sequence[float]) -> dict[str, float]:
    basic = stress._pair_geometry(left, right)
    left_height = float(left[3]) - float(left[1])
    right_height = float(right[3]) - float(right[1])
    maximum_height = max(left_height, right_height)
    left_center = (float(left[1]) + float(left[3])) / 2.0
    right_center = (float(right[1]) + float(right[3])) / 2.0
    basic.update({
        "bottom_edge_offset_height_ratio": abs(float(left[3]) - float(right[3])) / maximum_height,
        "top_edge_offset_height_ratio": abs(float(left[1]) - float(right[1])) / maximum_height,
        "vertical_center_offset_height_ratio": abs(left_center - right_center) / maximum_height,
    })
    return basic


def _metric_box_tuple(box: Any) -> tuple[float, float, float, float]:
    return box.left, box.top, box.right, box.bottom


def _nearest_truth(
    box: Any,
    truths: Sequence[Any],
) -> Any | None:
    overlapping = [truth for truth in truths if assembly._intersects(box, truth.box)]
    return None if not overlapping else max(overlapping, key=lambda truth: box_iou(box, truth.box))


def _assemble_with_first_crossing(
    panel: dict[str, Any],
    truths: Sequence[Any],
) -> tuple[TracedGroup, ...]:
    remaining = []
    for group in assembly._raw_groups(panel):
        truth = _nearest_truth(group.source_box, truths)
        remaining.append(TracedGroup(
            group,
            frozenset(() if truth is None else (truth.truth_id,)),
            frozenset(() if truth is None else (truth.generator_role,)),
            None,
        ))
    remaining.sort(key=lambda row: (
        row.group.panel_box.top, row.group.panel_box.left,
        row.group.panel_box.bottom, row.group.panel_box.right,
    ))
    completed = []
    while remaining:
        line = remaining.pop(0)
        changed = True
        while changed:
            changed = False
            for index in range(len(remaining) - 1, -1, -1):
                candidate = remaining[index]
                if not assembly._can_merge(line.group.panel_box, candidate.group.panel_box):
                    continue
                truth_ids = line.truth_ids | candidate.truth_ids
                roles = line.roles | candidate.roles
                first_crossing = line.first_crossing
                if first_crossing is None and len(truth_ids) > 1:
                    first_crossing = {
                        **_box_features(
                            _metric_box_tuple(line.group.source_box),
                            _metric_box_tuple(candidate.group.source_box),
                        ),
                        "role_set": "+".join(sorted(roles)),
                    }
                panel_box = assembly._union(line.group.panel_box, candidate.group.panel_box)
                member_ids = tuple(sorted((*line.group.member_ids, *candidate.group.member_ids)))
                member_source_boxes = (
                    *line.group.member_source_boxes,
                    *candidate.group.member_source_boxes,
                )
                line = TracedGroup(
                    assembly.RegionGroup(
                        f'{panel["panel_id"]}\nassembled:{sha256(chr(10).join(member_ids).encode()).hexdigest()}',
                        panel["source_sha256"],
                        panel["panel_id"],
                        panel_box,
                        assembly._map_box(panel_box, panel["panel_to_source_matrix"]),
                        member_ids,
                        member_source_boxes,
                    ),
                    truth_ids,
                    roles,
                    first_crossing,
                )
                remaining.pop(index)
                changed = True
        completed.append(line)
    return tuple(completed)


def _summary(values: Iterable[float]) -> dict[str, float | int | None]:
    rows = sorted(values)
    return {
        "count": len(rows),
        "minimum": rows[0] if rows else None,
        "median": statistics.median(rows) if rows else None,
        "maximum": rows[-1] if rows else None,
    }


def _authenticate_stress(
    preflight_path: Path,
    preflight_sha: str,
    capture_path: Path,
    capture_sha: str,
    score_path: Path,
    score_sha: str,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], dict[str, Any]]:
    _verify(preflight_path, preflight_sha, "stress preflight")
    _verify(capture_path, capture_sha, "stress capture")
    _verify(score_path, score_sha, "stress pair score")
    preflight = _read(preflight_path)
    capture = _read(capture_path)
    score = _read(score_path)
    truth_descriptor = preflight["truth"]
    truth_path = _inside_artifacts(truth_descriptor["path"])
    _verify(truth_path, truth_descriptor["sha256"], "stress truth")
    truth = _read(truth_path)
    expected_score_inputs = score["inputs"]
    if (preflight.get("schema") != "graphreader.word-fragment-stress-v1.preflight.v1"
            or capture.get("schema") != "graphreader.word-fragment-stress-v1.raw-capture.v1"
            or score.get("schema") != "graphreader.word-fragment-stress-v1.separability.v1"
            or score.get("status") != "train_only_raw_pair_geometry_measured"
            or expected_score_inputs["preflight"] != {
                "path": _repository_path(preflight_path), "sha256": preflight_sha}
            or expected_score_inputs["capture"] != {
                "path": _repository_path(capture_path), "sha256": capture_sha}
            or expected_score_inputs["truth"] != truth_descriptor
            or capture.get("candidate_sha256") != preflight["diagnostic_candidate"]["sha256"]):
        raise RuntimeError("Stress comparison input binding changed")
    for row in preflight["generator_sources"]:
        _verify(_inside_root(row["path"]), row["sha256"], "stress generator source")
    diagnostic = preflight["diagnostic_candidate"]
    diagnostic_path = _inside_artifacts(diagnostic["path"])
    _verify(diagnostic_path, diagnostic["sha256"], "stress diagnostic candidate")
    diagnostic_candidate = _read(diagnostic_path)
    assemblies = diagnostic_candidate["execution_assemblies"]
    names = {row["name"] for row in assemblies}
    if len(assemblies) != 4 or len(names) != 4:
        raise RuntimeError("Stress diagnostic assembly inventory changed")
    for row in assemblies:
        _verify(_inside_root(row["path"]), row["sha256"], "stress diagnostic assembly")
    return preflight, capture, truth, score


def _authenticate_historical_v44(
    manifest_path: Path,
    manifest_sha: str,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], Path]:
    """Run the frozen V44 validator against preserved exact assembly bytes.

    The original candidate, capture, evaluation, models, native runtime, licenses,
    source bindings, and truth binding retain their normal validators. Only the
    assembly byte lookup is redirected from the rebuilt live path to its recorded
    immutable backup with the same original path and SHA-256.
    """

    _verify(manifest_path, manifest_sha, "historical runtime backup manifest")
    manifest = _read(manifest_path)
    if not isinstance(manifest, list) or len(manifest) != 8:
        raise RuntimeError("Historical runtime backup manifest inventory changed")
    by_original: dict[Path, dict[str, Any]] = {}
    backup_paths: set[Path] = set()
    for row in manifest:
        if not isinstance(row, dict) or set(row) != {"sha256", "backup_path", "original_path"}:
            raise RuntimeError("Historical runtime backup manifest shape changed")
        original = _inside_root(row["original_path"])
        backup = _inside_artifacts(row["backup_path"])
        if original in by_original or backup in backup_paths:
            raise RuntimeError("Historical runtime backup manifest repeats a path")
        _verify(backup, row["sha256"], "historical runtime backup")
        by_original[original] = row
        backup_paths.add(backup)

    geometry = assembly.metric.geometry
    expected_names = geometry.EXPECTED_ASSEMBLIES

    def validate_archived_assemblies(
        root: Path,
        value: Any,
        label: str,
    ) -> tuple[dict[str, str], ...]:
        records = geometry._array(value, label)
        if len(records) != 4:
            raise geometry.EvidenceError(f"{label} must contain four assemblies")
        result = []
        seen = set()
        roots = set()
        for raw in records:
            record = geometry._object(raw, f"{label} item")
            if set(record) != {"name", "path", "sha256"}:
                raise geometry.EvidenceError(f"{label} item has unknown or missing fields")
            name = record.get("name")
            if not isinstance(name, str) or name not in expected_names or name in seen:
                raise geometry.EvidenceError(f"{label} names are incomplete or duplicated")
            original = geometry._inside(root, record.get("path"), f"{label} {name}")
            expected_sha = geometry._sha(record.get("sha256"), f"{label} {name}")
            preserved = by_original.get(original)
            if preserved is None or preserved["sha256"] != expected_sha:
                raise geometry.EvidenceError(
                    f"{label} {name} has no exact preserved historical assembly")
            backup = _inside_artifacts(preserved["backup_path"])
            _verify(backup, expected_sha, f"{label} {name} historical backup")
            roots.add(original.parent)
            seen.add(name)
            result.append({
                "name": name,
                "path": original.relative_to(root).as_posix(),
                "sha256": expected_sha,
            })
        if seen != expected_names or len(roots) != 1:
            raise geometry.EvidenceError(
                f"{label} does not identify one complete immutable executable snapshot")
        return tuple(sorted(result, key=lambda item: item["name"]))

    live_validator = geometry._validate_assemblies
    geometry._validate_assemblies = validate_archived_assemblies
    try:
        return assembly._authenticate()
    finally:
        geometry._validate_assemblies = live_validator


def _stress_positive_features(
    preflight: dict[str, Any],
    capture: dict[str, Any],
    truth: dict[str, Any],
    score: dict[str, Any],
) -> list[dict[str, float]]:
    captured = {row["source_sha256"]: row for row in capture["sources"]}
    truths = {row["source_sha256"]: row for row in truth["truths"]}
    expected = {row["sha256"] for row in preflight["sources"]}
    if (len(captured) != len(capture["sources"])
            or len(truths) != len(truth["truths"])
            or set(captured) != expected or set(truths) != expected):
        raise RuntimeError("Stress comparison source inventory changed")
    rows = []
    for source_sha in sorted(expected):
        raw = tuple(stress._box(value) for value in captured[source_sha]["raw_boxes_ltrb"])
        token_truths = tuple(
            stress._box(value)
            for value in truths[source_sha]["positive_truth"]["token_boxes_ltrb"]
        )
        pair = stress._best_assigned_pair(raw, token_truths)
        if pair is not None:
            rows.append(_box_features(raw[pair[0]], raw[pair[1]]))
    if len(rows) != score["positive_pair_count"]:
        raise RuntimeError("Stress positive pair reconstruction differs from frozen score")
    for feature in FEATURES[:5]:
        observed = stress._distribution(float(row[feature]) for row in rows)
        if observed != score["feature_distributions"][feature]["positive"]:
            raise RuntimeError(f"Stress positive {feature} distribution changed")
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stress-preflight", required=True)
    parser.add_argument("--stress-preflight-sha256", required=True)
    parser.add_argument("--stress-capture", required=True)
    parser.add_argument("--stress-capture-sha256", required=True)
    parser.add_argument("--stress-score", required=True)
    parser.add_argument("--stress-score-sha256", required=True)
    parser.add_argument("--line-assembly", required=True)
    parser.add_argument("--line-assembly-sha256", required=True)
    parser.add_argument("--historical-runtime-manifest", required=True)
    parser.add_argument("--historical-runtime-manifest-sha256", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()

    preflight_path = _inside_artifacts(arguments.stress_preflight)
    capture_path = _inside_artifacts(arguments.stress_capture)
    stress_score_path = _inside_artifacts(arguments.stress_score)
    line_path = _inside_artifacts(arguments.line_assembly)
    historical_manifest_path = _inside_artifacts(arguments.historical_runtime_manifest)
    output_path = _inside_artifacts(arguments.output)
    if output_path.exists():
        raise RuntimeError("Use a new comparison output path")
    preflight, capture, stress_truth, stress_score = _authenticate_stress(
        preflight_path, arguments.stress_preflight_sha256,
        capture_path, arguments.stress_capture_sha256,
        stress_score_path, arguments.stress_score_sha256,
    )
    _verify(line_path, arguments.line_assembly_sha256, "V44 line-assembly result")
    line_result = _read(line_path)
    if line_result.get("schema") != "graphreader.goal22.ocr-v44-line-assembly-feasibility.v1":
        raise RuntimeError("V44 line-assembly result schema changed")
    for descriptor in line_result["inputs"]:
        _verify(_inside_root(descriptor["path"]), descriptor["sha256"], "V44 line input")

    score, report, v44_preflight, _ = _authenticate_historical_v44(
        historical_manifest_path, arguments.historical_runtime_manifest_sha256
    )
    truths_by_split = assembly._load_truths(v44_preflight, score)
    panel_truths = assembly._panel_truths(report, truths_by_split)
    actual_negative_rows = []
    role_sets: Counter[str] = Counter()
    for panel in report["panels"]:
        if panel["split"] != "train":
            continue
        for group in _assemble_with_first_crossing(
            panel, panel_truths.get(panel["panel_id"], ())
        ):
            if len(group.truth_ids) <= 1:
                continue
            if group.first_crossing is None:
                raise RuntimeError("Cross-truth group lacks a recorded crossing step")
            row = dict(group.first_crossing)
            role_sets[str(row.pop("role_set"))] += 1
            actual_negative_rows.append(row)
    expected_crossings = line_result["geometry_results"]["train"][
        "accidental_merger_audit"
    ]["groups_whose_members_map_to_different_truths"]
    if len(actual_negative_rows) != expected_crossings:
        raise RuntimeError("V44 cross-truth group reconstruction changed")

    stress_positive_rows = _stress_positive_features(
        preflight, capture, stress_truth, stress_score
    )
    distributions = {
        feature: {
            "stress_positive_fragments": _summary(
                float(row[feature]) for row in stress_positive_rows
            ),
            "v44_actual_cross_truth_first_crossings": _summary(
                float(row[feature]) for row in actual_negative_rows
            ),
        }
        for feature in FEATURES
    }
    result = {
        "schema": SCHEMA,
        "status": "train_only_source_pixel_geometry_compared",
        "scope": (
            "Aggregate comparison of authenticated project-owned synthetic train evidence. "
            "No inference, training, private reads, sealed reads, or production changes."
        ),
        "inputs": {
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
        },
        "source_bindings": {
            _repository_path(Path(__file__)): _sha(Path(__file__)),
            _repository_path(Path(assembly.__file__)): _sha(Path(assembly.__file__)),
            _repository_path(Path(stress.__file__)): _sha(Path(stress.__file__)),
            _repository_path(Path(box_iou.__code__.co_filename)):
                _sha(Path(box_iou.__code__.co_filename)),
        },
        "coordinate_space": "original_source_pixels_for_both_populations",
        "historical_v44_runtime_binding": (
            "The full V44 evidence validator read each execution assembly from the exact preserved "
            "backup whose original path and SHA-256 match the candidate, capture request, and "
            "evaluation report. No live file was replaced or skipped."
        ),
        "stress_positive_pair_count": len(stress_positive_rows),
        "v44_actual_cross_truth_first_crossing_count": len(actual_negative_rows),
        "v44_actual_cross_truth_role_sets": dict(sorted(role_sets.items())),
        "feature_distributions": distributions,
        "selection": (
            "Stress positives use the frozen one-to-one token assignment. Each V44 negative is the "
            "first accepted generic-v1 merge step that makes one final assembled group span more "
            "than one nearest-overlapping truth. One aggregate negative is retained per final "
            "cross-truth group."
        ),
        "interpretation": (
            "The controlled stress negatives are intentionally ambiguous and are excluded here. "
            "This comparison describes whether alignment features distinguish observed V44 "
            "fragment positives from the actual train collisions caused by generic-v1 assembly; "
            "it does not choose or approve a new joining rule."
        ),
        "model_inference": False,
        "optimizer_steps": 0,
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
        "output": _repository_path(output_path),
        "sha256": _sha(output_path),
        "stress_positive_pairs": len(stress_positive_rows),
        "v44_cross_truth_first_crossings": len(actual_negative_rows),
    }, sort_keys=True))


if __name__ == "__main__":
    main()
