# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Partition V44 synthetic dev OCR errors without inference or optimizer work."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from hashlib import sha256
import json
from pathlib import Path
import statistics
import sys
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import score_full_ocr_candidate_v2 as metric  # noqa: E402
import score_text_extent_candidate as adapter  # noqa: E402
from ml.ocr.component_context_detector_v7.dataset import box_iou  # noqa: E402


SCORE_PATH = ROOT / "artifacts/goal22-runs/ocr-v44-text-extent/full-score-run1.json"
REPORT_PATH = ROOT / "artifacts/goal22-runs/ocr-v44-text-extent/application-evaluation-run1/report.json"
PREFLIGHT_PATH = ROOT / "artifacts/goal22-runs/ocr-text-extent-preflight/preflight-report.json"
RESULT_PATH = ROOT / "ml/ocr/text_extent_db_head_v44/P1_RESULT.json"
EXPECTED_RESULT_SHA256 = "0fc4420747c3b7abf3259ffb9233676d6bc8c291dc54342b8571f335461d9024"
V43_DOCUMENT_PATH = ROOT / "docs/GOAL-22-V43-REPRESENTATION-DIAGNOSIS.json"
V43_GEOMETRY_PATH = ROOT / "artifacts/goal22-runs/ocr-v43-representation-diagnosis/geometry-attribution.json"
ROLE_CLASSIFIER_PATH = ROOT / "src/GraphReader.Ocr/GraphTextRoleClassifier.cs"
ALLOWED_OUTPUT_PARENT = ROOT / "artifacts/goal22-runs/ocr-v44-text-extent"
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


def _predictions(
    report: dict[str, Any], split: str
) -> tuple[
    dict[str, tuple[metric.FullTextPrediction, ...]],
    dict[str, dict[str, Any]],
]:
    by_source: dict[str, list[metric.FullTextPrediction]] = defaultdict(list)
    metadata: dict[str, dict[str, Any]] = {}
    identifiers: set[str] = set()
    for panel in report["panels"]:
        if panel["split"] != split:
            continue
        if panel["status"] != "completed":
            raise RuntimeError("V44 diagnosis requires every selected panel to be completed")
        raw_by_id = {row["region_id"]: row for row in panel["raw_detector_regions"]}
        warning_by_id: dict[str, list[str]] = defaultdict(list)
        for warning in panel["ocr"]["warnings"]:
            if not warning.startswith("ocr_role_needs_review:"):
                continue
            parts = warning.split(":", 2)
            if len(parts) == 3:
                warning_by_id[parts[1]].append(parts[2])
        for region in panel["recognized_regions"]:
            identity = f'{panel["panel_id"]}\n{region["region_id"]}'
            if identity in identifiers:
                raise RuntimeError("recognized prediction identity repeats")
            identifiers.add(identity)
            by_source[panel["source_sha256"]].append(metric.FullTextPrediction(
                identity,
                panel["source_sha256"],
                _box_from_polygon(region["source_polygon"]),
                region["text"],
                region["role"],
            ))
            raw = raw_by_id[region["region_id"]]
            text = region["text"].strip()
            metadata[identity] = {
                "raw_context_is_null": raw["context"] is None,
                "warning_reasons": tuple(sorted(warning_by_id.get(region["region_id"], ()))),
                "text_is_exact_participant_token": text.casefold() == "participant",
                "text_has_strict_participant_cue": (
                    len(text) > len("Participant ") and text.casefold().startswith("participant ")
                ),
                "box_width": float(region["source_polygon"]["bounds"]["width"]),
                "box_height": float(region["source_polygon"]["bounds"]["height"]),
            }
    return ({source: tuple(rows) for source, rows in by_source.items()}, metadata)


def _summary(values: Iterable[float]) -> dict[str, float | None]:
    rows = list(values)
    return {
        "minimum": min(rows) if rows else None,
        "median": statistics.median(rows) if rows else None,
        "maximum": max(rows) if rows else None,
    }


def _iou_bin(value: float) -> str:
    if value == 0.0:
        return "zero"
    if value < 0.25:
        return "above_zero_below_0_25"
    if value < 0.40:
        return "0_25_to_below_0_40"
    if value < 0.50:
        return "0_40_to_below_0_50"
    return "at_or_above_0_50"


def _intersects(left: metric.Box, right: metric.Box) -> bool:
    return min(left.right, right.right) > max(left.left, right.left) and \
        min(left.bottom, right.bottom) > max(left.top, right.top)


def _partition_dev(
    truths: tuple[metric.FullTextTruth, ...],
    predictions_by_source: dict[str, tuple[metric.FullTextPrediction, ...]],
    prediction_metadata: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    truths_by_source: dict[str, list[metric.FullTextTruth]] = defaultdict(list)
    for truth in truths:
        truths_by_source[truth.source_sha256].append(truth)

    role_rows: dict[str, dict[str, Any]] = {}
    confusion: dict[str, Counter[str]] = defaultdict(Counter)
    prediction_role_counts: Counter[str] = Counter()
    unmatched_prediction_roles: Counter[str] = Counter()
    unmatched_nearest_truth_roles: dict[str, Counter[str]] = defaultdict(Counter)
    missed_iou_bins: dict[str, Counter[str]] = defaultdict(Counter)
    extra_iou_bins: Counter[str] = Counter()
    per_source: list[dict[str, Any]] = []
    totals = Counter()
    missed_merge_like = Counter()
    participant_fragment_gaps: list[float] = []
    participant_fragment_union_ious: list[float] = []

    for source in sorted(set(truths_by_source) | set(predictions_by_source)):
        source_truths = tuple(truths_by_source.get(source, ()))
        predictions = tuple(predictions_by_source.get(source, ()))
        pairs = metric._maximum_cardinality_pairs(predictions, source_truths)
        matched_predictions = {prediction for prediction, _ in pairs}
        matched_truths = {truth for _, truth in pairs}
        source_counts = {
            "source_sha256": source,
            "truth_count": len(source_truths),
            "prediction_count": len(predictions),
            "matched_count": len(pairs),
            "false_positive_count": len(predictions) - len(pairs),
            "false_negative_count": len(source_truths) - len(pairs),
        }
        source_counts["precision"] = source_counts["matched_count"] / max(1, source_counts["prediction_count"])
        source_counts["recall"] = source_counts["matched_count"] / max(1, source_counts["truth_count"])
        per_source.append(source_counts)
        totals.update({key: value for key, value in source_counts.items() if key.endswith("_count")})
        prediction_role_counts.update(prediction.role for prediction in predictions)

        by_truth_index = {truth_index: prediction_index for prediction_index, truth_index in pairs}
        for truth_index, prediction_index in by_truth_index.items():
            truth = source_truths[truth_index]
            if truth.generator_role != "participant":
                continue
            main = predictions[prediction_index]
            suffixes = [
                prediction for index, prediction in enumerate(predictions)
                if index not in matched_predictions and prediction.text.strip().isdigit()
                and _intersects(prediction.box, truth.box)
            ]
            if len(suffixes) != 1:
                continue
            suffix = suffixes[0]
            combined = metric.Box(
                min(main.box.left, suffix.box.left), min(main.box.top, suffix.box.top),
                max(main.box.right, suffix.box.right), max(main.box.bottom, suffix.box.bottom),
            )
            participant_fragment_gaps.append(suffix.box.left - main.box.right)
            participant_fragment_union_ious.append(box_iou(combined, truth.box))
        for truth_index, truth in enumerate(source_truths):
            row = role_rows.setdefault(truth.generator_role, {
                "expected_runtime_role": truth.expected_runtime_role,
                "truth_count": 0,
                "matched_count": 0,
                "missed_count": 0,
                "recognition_exact_count": 0,
                "matched_character_count": 0,
                "matched_edit_count": 0,
                "role_correct_count": 0,
                "matched_raw_context_null_count": 0,
                "matched_exact_participant_token_count": 0,
                "matched_strict_participant_cue_count": 0,
                "matched_warning_counts": Counter(),
                "matched_prediction_widths": [],
                "matched_prediction_heights": [],
            })
            row["truth_count"] += 1
            prediction_index = by_truth_index.get(truth_index)
            if prediction_index is None:
                row["missed_count"] += 1
                nearest = max((box_iou(prediction.box, truth.box) for prediction in predictions), default=0.0)
                missed_iou_bins[truth.generator_role][_iou_bin(nearest)] += 1
                if predictions:
                    best = max(predictions, key=lambda prediction: box_iou(prediction.box, truth.box))
                    intersected_truths = sum(_intersects(best.box, other.box) for other in source_truths)
                    if intersected_truths > 1:
                        missed_merge_like[truth.generator_role] += 1
                continue
            prediction = predictions[prediction_index]
            row["matched_count"] += 1
            row["recognition_exact_count"] += int(prediction.text == truth.text)
            row["matched_character_count"] += len(truth.text)
            row["matched_edit_count"] += metric._levenshtein(truth.text, prediction.text)
            row["role_correct_count"] += int(prediction.role == truth.expected_runtime_role)
            metadata = prediction_metadata[prediction.prediction_id]
            row["matched_raw_context_null_count"] += int(metadata["raw_context_is_null"])
            row["matched_exact_participant_token_count"] += int(metadata["text_is_exact_participant_token"])
            row["matched_strict_participant_cue_count"] += int(metadata["text_has_strict_participant_cue"])
            row["matched_warning_counts"].update(metadata["warning_reasons"])
            row["matched_prediction_widths"].append(metadata["box_width"])
            row["matched_prediction_heights"].append(metadata["box_height"])
            confusion[truth.expected_runtime_role][prediction.role] += 1

        for prediction_index, prediction in enumerate(predictions):
            if prediction_index in matched_predictions:
                continue
            unmatched_prediction_roles[prediction.role] += 1
            if source_truths:
                nearest = max(source_truths, key=lambda truth: box_iou(prediction.box, truth.box))
                nearest_iou = box_iou(prediction.box, nearest.box)
                unmatched_nearest_truth_roles[nearest.generator_role][_iou_bin(nearest_iou)] += 1
                extra_iou_bins[_iou_bin(nearest_iou)] += 1

    for row in role_rows.values():
        row["geometry_recall"] = row["matched_count"] / max(1, row["truth_count"])
        row["recognition_exact_accuracy_full_denominator"] = (
            row["recognition_exact_count"] / max(1, row["truth_count"])
        )
        row["recognition_exact_accuracy_matched_only"] = (
            row["recognition_exact_count"] / max(1, row["matched_count"])
        )
        row["matched_character_error_rate"] = row["matched_edit_count"] / max(1, row["matched_character_count"])
        row["role_accuracy_full_denominator"] = row["role_correct_count"] / max(1, row["truth_count"])
        row["role_accuracy_matched_only"] = row["role_correct_count"] / max(1, row["matched_count"])
        row["matched_warning_counts"] = dict(sorted(row["matched_warning_counts"].items()))
        row["matched_prediction_width"] = _summary(row.pop("matched_prediction_widths"))
        row["matched_prediction_height"] = _summary(row.pop("matched_prediction_heights"))

    return {
        "totals": dict(totals),
        "per_source": per_source,
        "by_generator_role": dict(sorted(role_rows.items())),
        "matched_role_confusion": {
            expected: dict(sorted(counts.items())) for expected, counts in sorted(confusion.items())
        },
        "all_prediction_role_counts": dict(sorted(prediction_role_counts.items())),
        "unmatched_prediction_role_counts": dict(sorted(unmatched_prediction_roles.items())),
        "missed_nearest_iou_bins_by_generator_role": {
            role: dict(sorted(counts.items())) for role, counts in sorted(missed_iou_bins.items())
        },
        "missed_whose_best_prediction_intersects_multiple_truths_by_role": dict(sorted(missed_merge_like.items())),
        "unmatched_prediction_nearest_truth_role_and_iou_bin": {
            role: dict(sorted(counts.items())) for role, counts in sorted(unmatched_nearest_truth_roles.items())
        },
        "unmatched_prediction_nearest_iou_bins": dict(sorted(extra_iou_bins.items())),
        "participant_fragmentation": {
            "matched_participant_truth_count": role_rows["participant"]["matched_count"],
            "main_word_plus_numeric_suffix_pair_count": len(participant_fragment_gaps),
            "horizontal_gap_pixels": _summary(participant_fragment_gaps),
            "combined_box_iou_with_truth": _summary(participant_fragment_union_ious),
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args()
    output_path = Path(arguments.output).resolve()
    if output_path.parent != ALLOWED_OUTPUT_PARENT.resolve() or output_path.exists():
        raise RuntimeError("Output must be a new JSON artifact in the V44 run directory")

    for path in (RESULT_PATH, SCORE_PATH, REPORT_PATH, PREFLIGHT_PATH,
                 V43_DOCUMENT_PATH, V43_GEOMETRY_PATH, ROLE_CLASSIFIER_PATH):
        if not path.is_file():
            raise FileNotFoundError(path)

    _verify(RESULT_PATH, EXPECTED_RESULT_SHA256, "tracked V44 outcome")
    outcome = _read(RESULT_PATH)
    if (outcome.get("schema") != "graphreader.ocr-text-extent-v44-outcome.v1"
            or outcome.get("revision") != "graph-text-extent-db-head-v44"
            or outcome.get("status") != "failed_dev_unconsumed"):
        raise RuntimeError("tracked V44 outcome identity or status changed")
    outcome_evidence = outcome["evidence"]
    for key in ("full_score", "application_evaluation", "candidate", "protocol",
                "source_snapshot", "acceptance_bars"):
        _descriptor(outcome_evidence, key)
    if ((ROOT / outcome_evidence["full_score"]["path"]).resolve() != SCORE_PATH
            or (ROOT / outcome_evidence["application_evaluation"]["path"]).resolve() != REPORT_PATH):
        raise RuntimeError("tracked outcome references a different score or application report")

    score = _read(SCORE_PATH)
    report = _read(REPORT_PATH)
    preflight = _read(PREFLIGHT_PATH)
    if (score["inputs"]["evaluation_report"] != outcome_evidence["application_evaluation"]
            or score["inputs"]["candidate"] != outcome_evidence["candidate"]):
        raise RuntimeError("score evidence differs from the tracked outcome")
    score_preflight = score["inputs"]["binding"]
    _verify(PREFLIGHT_PATH, score_preflight["sha256"], "V44 preflight binding")
    if (ROOT / score_preflight["path"]).resolve() != PREFLIGHT_PATH:
        raise RuntimeError("score binds a different preflight")
    for relative, digest in score["inputs"]["source_bindings"].items():
        _verify((ROOT / relative).resolve(), digest, f"full-score source {relative}")

    snapshot_path = (ROOT / outcome_evidence["source_snapshot"]["path"]).resolve()
    snapshot = _read(snapshot_path)
    snapshot_sources = {row["path"]: row["sha256"] for row in snapshot["sources"]}
    if not RELEVANT_SNAPSHOT_SOURCES.issubset(snapshot_sources):
        raise RuntimeError("V44 source snapshot omits a diagnostic generator or metric source")
    for relative in sorted(RELEVANT_SNAPSHOT_SOURCES):
        _verify((ROOT / relative).resolve(), snapshot_sources[relative], f"V44 snapshot source {relative}")

    v43_document_digest = snapshot_sources.get(V43_DOCUMENT_PATH.relative_to(ROOT).as_posix())
    if v43_document_digest is None:
        raise RuntimeError("V44 snapshot omits the V43 diagnosis")
    _verify(V43_DOCUMENT_PATH, v43_document_digest, "V43 representation diagnosis")
    v43_document = _read(V43_DOCUMENT_PATH)
    v43_descriptor = next((row for row in v43_document["reproducibility_artifacts"]
                           if row["path"] == V43_GEOMETRY_PATH.relative_to(ROOT).as_posix()), None)
    if v43_descriptor is None:
        raise RuntimeError("V43 diagnosis does not bind its geometry attribution")
    _verify(V43_GEOMETRY_PATH, v43_descriptor["sha256"], "V43 geometry attribution")
    v43 = _read(V43_GEOMETRY_PATH)
    truths = adapter._fixed_dev_truth(ROOT, preflight)
    predictions, prediction_metadata = _predictions(report, "validation")
    partition = _partition_dev(truths, predictions, prediction_metadata)

    frozen = score["metrics"]["validation"]
    observed = partition["totals"]
    expected = {
        "truth_count": frozen["truth_region_count"],
        "prediction_count": frozen["predicted_region_count"],
        "matched_count": frozen["geometry_matched_region_count"],
        "false_positive_count": frozen["geometry_false_positive_count"],
        "false_negative_count": frozen["geometry_false_negative_count"],
    }
    if any(observed[key] != value for key, value in expected.items()):
        raise RuntimeError(f"diagnostic geometry totals differ from frozen score: {observed!r}")

    v43_by_role = v43["by_truth_role"]
    v44_by_role = partition["by_generator_role"]
    geometry_delta = {
        role: {
            "truth_count": v44_by_role[role]["truth_count"],
            "v43_matched_count": v43_by_role[role]["matched"],
            "v44_matched_count": v44_by_role[role]["matched_count"],
            "matched_delta": v44_by_role[role]["matched_count"] - v43_by_role[role]["matched"],
            "v43_missed_count": v43_by_role[role]["missed"],
            "v44_missed_count": v44_by_role[role]["missed_count"],
        }
        for role in sorted(v44_by_role)
    }

    dev = score["metrics"]["validation"]
    unmatched_edits = (dev["unmatched_truth_deletion_edit_count"]
                       + dev["unmatched_prediction_insertion_edit_count"])
    participant = partition["by_generator_role"]["participant"]
    legend = partition["by_generator_role"]["legend_text"]
    all_raw_contexts = [row["raw_context_is_null"] for row in prediction_metadata.values()]
    result = {
        "schema": "graphreader.goal22.ocr-v44-text-extent-diagnosis.v1",
        "status": "synthetic_dev_diagnosis_complete",
        "scope": "authenticated project-owned synthetic train/dev evidence; no inference, training, private, or sealed reads",
        "inputs": [
            {"path": SCORE_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(SCORE_PATH)},
            {"path": REPORT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(REPORT_PATH)},
            {"path": PREFLIGHT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(PREFLIGHT_PATH)},
            {"path": V43_GEOMETRY_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(V43_GEOMETRY_PATH)},
            {"path": ROLE_CLASSIFIER_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(ROLE_CLASSIFIER_PATH)},
            {"path": RESULT_PATH.relative_to(ROOT).as_posix(), "sha256": _sha(RESULT_PATH)},
            {"path": snapshot_path.relative_to(ROOT).as_posix(), "sha256": _sha(snapshot_path)},
        ],
        "authentication": {
            "tracked_outcome_sha256": EXPECTED_RESULT_SHA256,
            "outcome_references_verified": [
                "full_score", "application_evaluation", "candidate", "protocol",
                "source_snapshot", "acceptance_bars",
            ],
            "score_binding_chain_verified": True,
            "full_score_source_bindings_verified": len(score["inputs"]["source_bindings"]),
            "v44_snapshot_sources_verified": sorted(RELEVANT_SNAPSHOT_SOURCES),
            "v43_document_and_geometry_binding_verified": True,
        },
        "runtime_identity": {
            "detector": report["candidate"]["detector"],
            "recognizer": report["candidate"]["recognizer"],
            "native_sha256": report["candidate"]["native_sha256"],
            "execution_assemblies": report["execution_assemblies"],
            "input_mode": report["input_mode"],
            "detector_postprocess": report["detector_postprocess"],
            "axis_bounds_used_for_role_classification": report["axis_bounds_used_for_role_classification"],
            "graph_structure_consensus_applied": report["graph_structure_consensus_applied"],
            "axis_mask_applied_to_detector": report["axis_mask_applied_to_detector"],
            "role_classifier_version": "graph-text-role-classifier-v2",
        },
        "frozen_complete_ocr_metrics": {
            "train": score["metrics"]["train"],
            "validation": frozen,
            "raw_detector_geometry": score["raw_detector_geometry"],
            "recognition_failures": score["recognition_failures"],
        },
        "v43_to_v44_dev_geometry_by_generator_role": geometry_delta,
        "v44_dev_partition": partition,
        "observed_findings": [
            f"V44 changes 135/183 V43 dev matches to {observed['matched_count']}/183: +4 matches, -2 false positives, and -4 false negatives. The intended legend subset gains 0/11 and condition labels gain 1/30; x ticks account for +4 while phase headings and y ticks each lose one.",
            f"Geometry-driven deletion and insertion edits contribute {unmatched_edits}/{dev['character_error_count']} ({unmatched_edits / dev['character_error_count']:.4%}) dev character errors. Matched crops contribute only {dev['matched_pair_edit_count']} edits.",
            f"Participant geometry matches all {participant['matched_count']}/9 truths but every matched prediction is role Other. Seven primary recognitions are exactly the standalone token Participant, two are Partidopant-like corruptions, none satisfy the classifier's stricter Participant-plus-suffix cue, and all nine carry the ambiguous-peripheral warning.",
            f"Every participant truth is fragmented into a main word proposal and a separate numeric suffix proposal: nine unmatched predictions have participant as their nearest truth. The matched main-word boxes are {participant['matched_prediction_width']['minimum']:.0f}-{participant['matched_prediction_width']['maximum']:.0f} pixels wide versus the authenticated V43 dev participant truth range 110-111 pixels.",
            f"Legend role 0/11 consists of ten geometry misses plus one matched crop classified Annotation. The extent treatment leaves legend geometry unchanged at 1/11; eight of ten misses retain a nonzero overlapping proposal, including seven with nearest IoU from 0.25 through 0.50.",
            f"All {sum(all_raw_contexts)}/{len(all_raw_contexts)} dev raw detector regions carry null semantic context. The current role classifier has branches for participant bands, legend glyph proximity, phase dividers, and annotation arrows, but OcrPipeline.EnrichGeometry populates only axis-title context.",
        ],
        "causal_interpretation": [
            "The participant 0/9 result is a detector-fragmentation and runtime-role cascade, not evidence of a standalone learned role classifier failure. The DB detector splits each label at whitespace, recognition sees only the main word, and the context-free classifier intentionally rejects standalone Participant as ambiguous peripheral text.",
            "The sole geometrically matched legend fails role classification because compact legends are inside the plot and no NearLegendGlyph context is populated. This establishes a runtime context defect for that matched region; it does not explain the ten legend detector misses.",
            "The V44 long-text extent treatment does not generalize to the target legend failure mode. The concentration of overlapping sub-gate proposals and participant word/suffix splits points to line assembly and compact-layout localization rather than additional duration or another global width-only treatment.",
        ],
        "next_bounded_action": {
            "priority": "runtime diagnostic before another trained candidate",
            "hypothesis": "A deterministic line-assembly pass that merges horizontally adjacent, vertically aligned DB regions before recognition will repair whitespace-fragmented multi-token text without changing detector weights or thresholds.",
            "minimum_scope": [
                "Replay existing synthetic train/dev regions only; no inference is required for the first diagnostic.",
                "Construct merge proposals from raw boxes using vertical overlap, height compatibility, and a gap normalized by text height; never use truth text or roles to form proposals.",
                "Report participant 9-pair reconstruction, legend reconstruction, new accidental merges, raw geometry, complete-OCR metrics, and per-role confusion under the unchanged scorer.",
                "If a runtime implementation follows, populate or refine semantic context from non-truth layout evidence and add an end-to-end test proving that those context fields are actually produced, rather than only unit tests that inject them.",
            ],
            "do_not": [
                "Do not extend training duration or open another width-only candidate from this evidence.",
                "Do not change the IoU gate, detector thresholds, or role strings to score this candidate.",
                "Do not add participant IDs or dev text as whitelists.",
            ],
        },
        "limitations": [
            "The three dev sources remain one coupled held-out family, so renderer, font, degradation, template, and marker effects cannot be separated.",
            "Geometry matching is source-level at IoU 0.5. A matched role or recognition error can still include an imperfect crop.",
            "Static role-classifier rules are linked to aggregate matched confusion. This diagnosis does not replay alternate contexts or modify runtime behavior.",
        ],
        "optimizer_steps": 0,
        "model_inference": False,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approval": False,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": output_path.relative_to(ROOT).as_posix(),
        "sha256": _sha(output_path),
        "totals": observed,
        "roles": partition["matched_role_confusion"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
