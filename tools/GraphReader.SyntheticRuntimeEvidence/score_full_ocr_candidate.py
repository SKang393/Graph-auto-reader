# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score full OCR text and roles on authenticated V3 synthetic evidence.

The frozen geometry scorer authenticates every annotation-free runtime input
before this module regenerates project-owned truth. Geometry alone fixes one
maximum-cardinality pairing. Text and role values never influence that pairing.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from ml.markers.center.mask_preserving_v24 import family_scenes  # noqa: E402
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3  # noqa: E402
from ml.ocr import production_tiled_inputs  # noqa: E402
from ml.ocr.component_context_detector_v7.dataset import box_iou  # noqa: E402
from ml.ocr.component_region_detector_v6.dataset import Box  # noqa: E402

import score_official_head_candidate as geometry  # noqa: E402


class EvidenceError(ValueError):
    """Full OCR evidence is incomplete, inconsistent, or out of scope."""


OUTPUT_SCHEMA = "graphreader.full-ocr-candidate-score.v1"
MATCH_IOU_MINIMUM = 0.5
EXPECTED_COUNTS = {
    "train": {"sources": 20, "panels": 28, "truths": 709},
    "validation": {"sources": 3, "panels": 9, "truths": 183},
}
SOURCE_BINDINGS = {
    "tools/GraphReader.SyntheticRuntimeEvidence/score_official_head_candidate.py":
        "719bb18c30821cdd44b65fc9ede38f6d111fb3631e07c7c69d98d1116aa575a2",
    "ml/ocr/production_tiled_inputs.py":
        "b2ee5dba050790b504f7d35b129d882d4d5bcf011d87a3b21a4431202d3fdc35",
    "ml/markers/center/plot_domain_v25/runtime_domain_binding_v3.py":
        "2e8ad74fe44720b96323a3e927ae0e611b1ef993a565c7abd02bfabdd6446f93",
    "ml/markers/center/mask_preserving_v24/runtime_family_scenes.py":
        "59233739c4eae55865d16ad8367f4c013c1b372b7feee2715fe3be57493673a0",
    "src/GraphReader.Domain/DomainModels.cs":
        "90b17c5f5d23a84e7a5982b2e08d1be048cb8a2e026439e43db738bd9d31163c",
    "src/GraphReader.App/Integration/Workflow/ProductionAutomaticDetectionAdapter.cs":
        "d46d619cdf475e1adf24fe8090d732db8c198dd7e2f1e82739557b435523377a",
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs":
        "6eaad336d7d81f579f773b9f8ef4e96cbf697b7a3b528279c874575bbdd9740b",
}
TRUTH_ROLE_TO_RUNTIME_ROLE = {
    "x_tick": "xtick",
    "y_tick": "ytick",
    "axis_title": "axistitle",
    "phase_heading": "phaseheading",
    "legend_text": "legendtext",
    "participant": "participant",
    "annotation": "annotation",
    # OcrRole has no ConditionLabel member. The production adapter's exhaustive
    # mapping sends this generator role to OcrRole.Other.
    "condition_label": "other",
}
RUNTIME_ROLES = frozenset(TRUTH_ROLE_TO_RUNTIME_ROLE.values())


@dataclass(frozen=True)
class FullTextTruth:
    truth_id: str
    source_sha256: str
    box: Box
    text: str
    generator_role: str
    expected_runtime_role: str

    @property
    def source_box(self) -> Box:
        """Expose the frozen geometry scorer's source-truth shape."""
        return self.box


@dataclass(frozen=True)
class FullTextPrediction:
    prediction_id: str
    source_sha256: str
    box: Box
    text: str
    role: str


def _validate_sources(root: Path) -> dict[str, str]:
    observed: dict[str, str] = {}
    for relative, expected in SOURCE_BINDINGS.items():
        path = (root / relative).resolve()
        if root not in path.parents or not path.is_file():
            raise EvidenceError(f"scoring source is missing or outside repository: {relative}")
        digest = sha256(path.read_bytes()).hexdigest()
        if digest != expected:
            raise EvidenceError(f"scoring source identity changed: {relative}")
        observed[relative] = digest
    try:
        geometry._validate_source_bindings(root)
    except geometry.EvidenceError as error:
        raise EvidenceError(str(error)) from error
    return observed


def _canonical_role(generator_role: str) -> str:
    try:
        return TRUTH_ROLE_TO_RUNTIME_ROLE[generator_role]
    except KeyError as error:
        raise EvidenceError(f"generator text role has no reviewed runtime mapping: {generator_role}") from error


def _regenerate_truths(
    root: Path,
    binding_path: Path,
    binding_sha256: str,
) -> dict[str, tuple[FullTextTruth, ...]]:
    binding = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
        binding_path, binding_sha256, repository_root=root
    )
    if binding.failures:
        raise EvidenceError("V3 runtime binding retains failed panels")
    split_inputs = {
        "train": (binding.train, binding.profile.train),
        "validation": (binding.dev, binding.profile.dev),
    }
    result: dict[str, tuple[FullTextTruth, ...]] = {}
    all_truth_ids: set[str] = set()
    for split, (domains, profile) in split_inputs.items():
        expected = EXPECTED_COUNTS[split]
        if len(domains) != expected["panels"]:
            raise EvidenceError(f"{split} runtime panel denominator changed")
        by_dataset: dict[int, list[Any]] = {}
        for domain in domains:
            runtime = domain.runtime_input
            if runtime.split != split:
                raise EvidenceError(f"{split} runtime panel carries a foreign split")
            by_dataset.setdefault(runtime.dataset_seed, []).append(domain)
        if set(by_dataset) != set(profile.dataset_seeds):
            raise EvidenceError(f"{split} dataset seed inventory changed")

        regenerated: dict[str, Any] = {}
        for dataset_seed in profile.dataset_seeds:
            runtime_panels = tuple(item.runtime_input for item in by_dataset[dataset_seed])
            current = runtime_domain_binding_v3._regenerate_v3(
                split, dataset_seed, runtime_panels, profile
            )
            if set(regenerated) & set(current):
                raise EvidenceError(f"{split} regenerated duplicate source identities")
            regenerated.update(current)
        if len(regenerated) != expected["sources"]:
            raise EvidenceError(f"{split} source denominator changed")

        panels_by_source: dict[str, list[Any]] = {}
        for domain in domains:
            panels_by_source.setdefault(domain.runtime_input.source_sha256, []).append(domain)
        if set(panels_by_source) != set(regenerated):
            raise EvidenceError(f"{split} runtime and regenerated source inventories differ")

        truths: list[FullTextTruth] = []
        for source_sha in sorted(regenerated):
            rendered = regenerated[source_sha]
            source_panels = sorted(
                panels_by_source[source_sha], key=lambda item: item.runtime_input.panel_id
            )
            base_truths = production_tiled_inputs._source_truth_records(
                split, rendered.annotation, source_sha, source_panels,
                rendered.width, rendered.height
            )
            records: dict[str, Mapping[str, Any]] = {}
            for record in family_scenes._records(rendered.annotation, "texts"):
                raw_text = record.get("text")
                if record.get("visible", True) is False or not isinstance(raw_text, str) or not raw_text.strip():
                    continue
                if record.get("rendered_pixel_box") is None:
                    continue
                text_id = str(record.get("text_id", "")).strip()
                if not text_id or text_id in records:
                    raise EvidenceError("regenerated source text identity is missing or duplicated")
                records[text_id] = record
            if {truth.source_text_id for truth in base_truths} != set(records):
                raise EvidenceError("full-text adapter differs from the frozen source truth inventory")
            for truth in base_truths:
                record = records[truth.source_text_id]
                generator_role = str(record.get("role", ""))
                if truth.role != generator_role:
                    raise EvidenceError("full-text adapter role differs from the frozen source truth")
                box = Box(*(float(value) for value in truth.source_box))
                text = record["text"]
                current = FullTextTruth(
                    truth.truth_id, source_sha, box, text, generator_role,
                    _canonical_role(generator_role)
                )
                if current.truth_id in all_truth_ids:
                    raise EvidenceError("full source truth identity repeats")
                all_truth_ids.add(current.truth_id)
                truths.append(current)
        if len(truths) != expected["truths"]:
            raise EvidenceError(f"{split} full source truth denominator changed")
        result[split] = tuple(truths)
    return result


def _recognized_predictions(evidence: Any) -> dict[str, tuple[FullTextPrediction, ...]]:
    output: dict[str, list[FullTextPrediction]] = {}
    identifiers: set[str] = set()
    for raw_panel in evidence.report["panels"]:
        panel = geometry._object(raw_panel, "evaluation panel")
        panel_id = panel.get("panel_id")
        expected = evidence.panels.get(panel_id) if isinstance(panel_id, str) else None
        if expected is None:
            raise EvidenceError("validated evaluation panel disappeared")
        for raw_region in geometry._array(panel.get("recognized_regions"), "recognized regions"):
            region = geometry._object(raw_region, "recognized region")
            region_id = region.get("region_id")
            prediction_id = f"{panel_id}\n{region_id}"
            if not isinstance(region_id, str) or not region_id or prediction_id in identifiers:
                raise EvidenceError("recognized full-OCR prediction identity is missing or duplicated")
            identifiers.add(prediction_id)
            text = region.get("text")
            role = region.get("role")
            if not isinstance(text, str):
                raise EvidenceError("recognized region primary text is not a string")
            if role not in RUNTIME_ROLES:
                raise EvidenceError("recognized region carries an unknown runtime role")
            points = geometry._polygon(
                region.get("source_polygon"), "recognized source polygon",
                expected.source_width, expected.source_height
            )
            box = Box(
                min(point[0] for point in points), min(point[1] for point in points),
                max(point[0] for point in points), max(point[1] for point in points)
            )
            output.setdefault(expected.source_sha256, []).append(
                FullTextPrediction(prediction_id, expected.source_sha256, box, text, role)
            )
    return {source: tuple(items) for source, items in output.items()}


def _maximum_cardinality_pairs(
    predictions: Sequence[FullTextPrediction],
    truths: Sequence[FullTextTruth],
) -> tuple[tuple[int, int], ...]:
    """Return the frozen matcher assignment without inspecting text or role."""
    edges = [
        [index for index, truth in enumerate(truths)
         if box_iou(prediction.box, truth.box) >= MATCH_IOU_MINIMUM]
        for prediction in predictions
    ]
    owners = [-1] * len(truths)

    def visit(prediction_index: int, seen: set[int]) -> bool:
        for truth_index in edges[prediction_index]:
            if truth_index in seen:
                continue
            seen.add(truth_index)
            if owners[truth_index] == -1 or visit(owners[truth_index], seen):
                owners[truth_index] = prediction_index
                return True
        return False

    for index in range(len(predictions)):
        visit(index, set())
    return tuple((prediction_index, truth_index)
                 for truth_index, prediction_index in enumerate(owners)
                 if prediction_index != -1)


def _levenshtein(left: str, right: str) -> int:
    previous = list(range(len(right) + 1))
    for row, left_character in enumerate(left, start=1):
        current = [row]
        for column, right_character in enumerate(right, start=1):
            current.append(min(
                current[-1] + 1,
                previous[column] + 1,
                previous[column - 1] + int(left_character != right_character),
            ))
        previous = current
    return previous[-1]


def _score_split(
    truths: Sequence[FullTextTruth],
    predictions_by_source: Mapping[str, tuple[FullTextPrediction, ...]],
) -> dict[str, Any]:
    truths_by_source: dict[str, list[FullTextTruth]] = {}
    for truth in truths:
        truths_by_source.setdefault(truth.source_sha256, []).append(truth)
    sources = sorted(set(truths_by_source) | set(predictions_by_source))
    matched_count = exact = role_correct = matched_edits = deletion_edits = insertion_edits = 0
    truth_characters = 0
    predicted_count = 0
    role_counts: dict[str, list[int]] = {}
    for source in sources:
        source_truths = tuple(truths_by_source.get(source, ()))
        predictions = tuple(predictions_by_source.get(source, ()))
        pairs = _maximum_cardinality_pairs(predictions, source_truths)
        frozen_count = geometry.maximum_cardinality_matches(
            tuple(predictions), tuple(truth.box for truth in source_truths)
        )
        if len(pairs) != frozen_count:
            raise EvidenceError("full OCR pairing differs from the frozen geometry matcher")
        matched_predictions = {prediction for prediction, _ in pairs}
        matched_truths = {truth for _, truth in pairs}
        matched_count += len(pairs)
        predicted_count += len(predictions)
        truth_characters += sum(len(truth.text) for truth in source_truths)
        for prediction_index, truth_index in pairs:
            prediction = predictions[prediction_index]
            truth = source_truths[truth_index]
            exact += int(prediction.text == truth.text)
            matched_edits += _levenshtein(truth.text, prediction.text)
            role_correct += int(prediction.role == truth.expected_runtime_role)
        deletion_edits += sum(len(truth.text) for index, truth in enumerate(source_truths)
                              if index not in matched_truths)
        insertion_edits += sum(len(prediction.text) for index, prediction in enumerate(predictions)
                               if index not in matched_predictions)
        for index, truth in enumerate(source_truths):
            row = role_counts.setdefault(truth.expected_runtime_role, [0, 0])
            row[0] += 1
            matching = next((prediction for prediction, owner in pairs if owner == index), None)
            if matching is not None:
                row[1] += int(predictions[matching].role == truth.expected_runtime_role)
    truth_count = len(truths)
    total_edits = matched_edits + deletion_edits + insertion_edits
    return {
        "truth_region_count": truth_count,
        "predicted_region_count": predicted_count,
        "geometry_matched_region_count": matched_count,
        "geometry_false_positive_count": predicted_count - matched_count,
        "geometry_false_negative_count": truth_count - matched_count,
        "recognition_exact_count": exact,
        "recognition_exact_accuracy": exact / max(1, truth_count),
        "truth_character_count": truth_characters,
        "matched_pair_edit_count": matched_edits,
        "unmatched_truth_deletion_edit_count": deletion_edits,
        "unmatched_prediction_insertion_edit_count": insertion_edits,
        "character_error_count": total_edits,
        "character_error_rate": total_edits / max(1, truth_characters),
        "role_correct_count": role_correct,
        "role_accuracy": role_correct / max(1, truth_count),
        "by_expected_runtime_role": {
            role: {"truth_count": counts[0], "correct_count": counts[1],
                   "accuracy": counts[1] / max(1, counts[0])}
            for role, counts in sorted(role_counts.items())
        },
        "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
    }


def _geometry_and_failure_metrics(
    evidence: Any,
    truths_by_split: Mapping[str, Sequence[FullTextTruth]],
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    raw_metrics: dict[str, Any] = {}
    recognized_metrics: dict[str, Any] = {}
    failures: dict[str, Any] = {}
    for split, truths in truths_by_split.items():
        allowed_sources = {truth.source_sha256 for truth in truths}
        raw = {
            source: predictions for source, predictions in evidence.raw_by_source.items()
            if source in allowed_sources
        }
        recognized = {
            source: predictions for source, predictions in evidence.recognized_by_source.items()
            if source in allowed_sources
        }
        raw_metrics[split] = geometry._score_predictions(truths, raw)
        recognized_metrics[split] = geometry._score_predictions(truths, recognized)
        failures[split] = {
            "failed_panel_count": sum(
                panel.split == split
                for panel_id, panel in evidence.panels.items()
                if next(item for item in evidence.report["panels"]
                        if item["panel_id"] == panel_id)["status"] == "failed"
            ),
            "explicit_region_failure_count": evidence.explicit_region_failures[split],
            "raw_regions_on_failed_panels": evidence.failed_panel_raw_regions[split],
            "raw_regions_without_successful_recognition": (
                raw_metrics[split]["predicted_region_count"]
                - recognized_metrics[split]["predicted_region_count"]
            ),
        }
    return raw_metrics, recognized_metrics, failures


def score(
    binding_path: Path,
    binding_sha256: str,
    capture_report_path: Path,
    capture_report_sha256: str,
    request_path: Path,
    request_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    evaluation_report_path: Path,
    evaluation_report_sha256: str,
    output_path: Path,
    *,
    evaluator_sha256: str,
    repository_root: Path = REPOSITORY_ROOT,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    evaluator = geometry._sha(evaluator_sha256, "evaluator SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != evaluator:
        raise EvidenceError("current evaluator bytes differ from the reviewed identity")
    output = output_path.resolve()
    artifacts = (root / "artifacts").resolve()
    if output == artifacts or artifacts not in output.parents or output.exists():
        raise EvidenceError("score output must be a new file under repository artifacts")
    source_bindings = _validate_sources(root)
    try:
        bar_sha, detection_precision_bar, detection_recall_bar = geometry._load_acceptance_bar(root)
        evidence = geometry._validate_before_truth(
            root, binding_path.resolve(), geometry._sha(binding_sha256, "V3 binding"),
            capture_report_path.resolve(), geometry._sha(capture_report_sha256, "capture report"),
            request_path.resolve(), geometry._sha(request_sha256, "capture request"),
            candidate_path.resolve(), geometry._sha(candidate_sha256, "candidate"),
            evaluation_report_path.resolve(), geometry._sha(
                evaluation_report_sha256, "evaluation report"))
    except geometry.EvidenceError as error:
        raise EvidenceError(str(error)) from error

    # No truth regeneration occurs before every annotation-free input above has
    # authenticated. This is the only regeneration pass used by this scorer.
    truths_by_split = _regenerate_truths(root, binding_path.resolve(), binding_sha256)
    predictions = _recognized_predictions(evidence)
    raw_geometry, recognized_geometry, recognition_failures = _geometry_and_failure_metrics(
        evidence, truths_by_split
    )
    metrics: dict[str, Any] = {}
    for split, truths in truths_by_split.items():
        allowed_sources = {truth.source_sha256 for truth in truths}
        split_predictions = {
            source: values for source, values in predictions.items() if source in allowed_sources
        }
        metrics[split] = _score_split(truths, split_predictions)
    if sum(item["truth_region_count"] for item in metrics.values()) != 892:
        raise EvidenceError("full OCR scorer did not retain all 892 source truths")

    _, bar_payload = geometry._read_exact(
        root, geometry.ACCEPTANCE_BARS_PATH.as_posix(), bar_sha, "acceptance bars")
    bars = geometry._object(json.loads(bar_payload), "acceptance bars")
    tier = geometry._object(bars.get("tier1_reviewable_error"), "tier 1 acceptance bars")
    exact_bar = geometry._finite(tier.get("recognition_exact_match_minimum"), "exact bar")
    cer_bar = geometry._finite(tier.get("character_error_rate_maximum"), "CER bar")
    role_bar = geometry._finite(tier.get("role_accuracy_minimum"), "role bar")
    validation = metrics["validation"]
    comparison = {
        "recognition_exact_match_minimum": exact_bar,
        "character_error_rate_maximum": cer_bar,
        "role_accuracy_minimum": role_bar,
        "text_region_detection_precision_minimum": detection_precision_bar,
        "text_region_detection_recall_minimum": detection_recall_bar,
        "validation_recognition_exact_meets_bar": validation["recognition_exact_accuracy"] >= exact_bar,
        "validation_character_error_rate_meets_bar": validation["character_error_rate"] <= cer_bar,
        "validation_role_accuracy_meets_bar": validation["role_accuracy"] >= role_bar,
        "validation_raw_detection_precision_meets_bar": (
            raw_geometry["validation"]["precision"] >= detection_precision_bar
        ),
        "validation_raw_detection_recall_meets_bar": (
            raw_geometry["validation"]["recall"] >= detection_recall_bar
        ),
        "descriptive_only_until_metric_policy_and_candidate_outcome_are_closed": True,
    }
    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_received_truth": False,
            "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration": True,
            "truth_regenerated_once_only_in_python_evaluator": True,
        },
        "inputs": {
            "binding": {"path": binding_path.resolve().relative_to(root).as_posix(),
                        "sha256": binding_sha256},
            "capture_report": {"path": capture_report_path.resolve().relative_to(root).as_posix(),
                               "sha256": capture_report_sha256},
            "capture_request": {"path": request_path.resolve().relative_to(root).as_posix(),
                                "sha256": request_sha256},
            "candidate": {"path": candidate_path.resolve().relative_to(root).as_posix(),
                          "sha256": candidate_sha256},
            "evaluation_report": {
                "path": evaluation_report_path.resolve().relative_to(root).as_posix(),
                "sha256": evaluation_report_sha256,
            },
            "evaluator_sha256": evaluator,
            "source_bindings": source_bindings,
        },
        "role_mapping": {
            "generator_to_serialized_runtime_ocr_role": TRUTH_ROLE_TO_RUNTIME_ROLE,
            "condition_label_rationale": (
                "OcrRole has no condition-label member; the production mapping uses OcrRole.Other "
                "for generator condition_label truth. Unknown generator or runtime roles fail closed."
            ),
            "domain_enum_source": "src/GraphReader.Domain/DomainModels.cs",
            "production_mapping_source": (
                "src/GraphReader.App/Integration/Workflow/ProductionAutomaticDetectionAdapter.cs"
            ),
            "serialization_source": (
                "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs"
            ),
        },
        "matching": {
            "coordinate_space": "source_original_pixels",
            "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
            "algorithm": "frozen_order_augmenting_path_maximum_cardinality",
            "text_role_and_alternatives_do_not_influence_pairing": True,
            "recognized_primary_text_used_exactly_without_normalization": True,
            "alternatives_used": False,
        },
        "metrics": metrics,
        "raw_detector_geometry": raw_geometry,
        "successfully_recognized_region_geometry": recognized_geometry,
        "recognition_failures": recognition_failures,
        "acceptance_bar_reference": {
            "path": geometry.ACCEPTANCE_BARS_PATH.as_posix(),
            "sha256": bar_sha,
            **comparison,
            "scorer_does_not_select_or_approve_a_candidate": True,
        },
        "integrity": {
            "source_count": 23,
            "panel_count": 37,
            "full_source_truth_count": 892,
            "failed_panels_remain_in_full_source_denominator": True,
            "unmatched_truths_count_as_exact_and_role_failures_and_full_text_deletions": True,
            "unmatched_predictions_count_as_character_insertions": True,
        },
        "elapsed_milliseconds": (time.perf_counter() - started) * 1000.0,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=True).encode("utf-8") + b"\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binding", type=Path, required=True)
    parser.add_argument("--binding-sha256", required=True)
    parser.add_argument("--capture-report", type=Path, required=True)
    parser.add_argument("--capture-report-sha256", required=True)
    parser.add_argument("--capture-request", type=Path, required=True)
    parser.add_argument("--capture-request-sha256", required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-sha256", required=True)
    parser.add_argument("--evaluation-report", type=Path, required=True)
    parser.add_argument("--evaluation-report-sha256", required=True)
    parser.add_argument("--evaluator-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    score(
        arguments.binding, arguments.binding_sha256,
        arguments.capture_report, arguments.capture_report_sha256,
        arguments.capture_request, arguments.capture_request_sha256,
        arguments.candidate, arguments.candidate_sha256,
        arguments.evaluation_report, arguments.evaluation_report_sha256,
        arguments.output, evaluator_sha256=arguments.evaluator_sha256,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
