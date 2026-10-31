#!/usr/bin/env python3
"""Aggregate source-global attribution for a bound synthetic OCR replay.

This wrapper reuses the reviewed native-workflow scorer to authenticate the
replay, map panel-local boxes into source pixels exactly once, and retain every
source truth exactly once.  It emits aggregates only.  An ID equality with a
raw-detector ID is a surviving-ID classification, not proof of runtime lineage.
"""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
SCORER_DIRECTORY = ROOT / "tools" / "GraphReader.SyntheticRuntimeEvidence"
if str(SCORER_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SCORER_DIRECTORY))

import score_native_workflow_ocr as source_global  # noqa: E402


class DiagnosticError(ValueError):
    """The requested diagnostic cannot establish its reviewed binding."""


def digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise DiagnosticError(message)


def prediction_region_key(prediction) -> tuple[str, str, str]:
    """Return the authenticated source/panel/region identity, not a bare ID."""
    panel_id, separator, region_id = prediction.prediction_id.partition("\n")
    require(bool(panel_id and separator and region_id), "malformed scorer prediction identity")
    require(isinstance(prediction.source_sha256, str) and prediction.source_sha256,
            "malformed scorer prediction source identity")
    return prediction.source_sha256, panel_id, region_id


def unmatched_predictions_by_source(truths, predictions_by_source, source_ids):
    """Use the frozen scorer's deterministic maximum-cardinality geometry pairing."""
    unmatched = []
    for source_id in sorted(source_ids):
        source_truths = [truth for truth in truths if truth.source_sha256 == source_id]
        predictions = tuple(predictions_by_source.get(source_id, ()))
        matched_prediction_indexes = {
            prediction_index
            for prediction_index, _ in source_global.metric._maximum_cardinality_pairs(
                predictions, source_truths
            )
        }
        unmatched.extend(
            prediction for prediction_index, prediction in enumerate(predictions)
            if prediction_index not in matched_prediction_indexes
        )
    return tuple(unmatched)


def classify_surviving_identifier(region_key: tuple[str, str, str], raw_region_keys: set[tuple[str, str, str]]) -> str:
    """Describe only equality in saved evidence, without asserting causality."""
    return (
        "same_identifier_present_in_raw_detector"
        if region_key in raw_region_keys
        else "final_only_identifier"
    )


def attribute(root: Path, workflow_report: Path, generator_report: Path) -> dict:
    workflow_report = workflow_report.resolve()
    generator_report = generator_report.resolve()
    require(workflow_report.is_relative_to(root), "workflow report must remain in repository")
    require(generator_report.is_relative_to(root), "generator report must remain in repository")
    report_ref = {"path": str(workflow_report), "sha256": digest(workflow_report)}
    generator_ref = {"path": str(generator_report), "sha256": digest(generator_report)}
    report, sources, panels, observations = source_global.authenticate_runtime(root, report_ref)
    truths, source_splits = source_global.load_truths(root, generator_ref, sources, panels)

    raw_region_keys = {
        prediction_region_key(prediction)
        for source_predictions in observations["raw_detector"].values()
        for prediction in source_predictions
    }
    by_split = {}
    for split, split_truths in sorted(truths.items()):
        source_ids = {
            source_id for source_id, source_split in source_splits.items()
            if source_split == split
        }
        final_unmatched = unmatched_predictions_by_source(
            split_truths, observations["assembled_ocr"], source_ids
        )
        classifications = Counter(
            classify_surviving_identifier(prediction_region_key(prediction), raw_region_keys)
            for prediction in final_unmatched
        )
        final_roles = Counter(prediction.role for prediction in final_unmatched)
        by_split[split] = {
            "source_count": len(source_ids),
            "truth_count": len(split_truths),
            "assembled_false_positive_count": len(final_unmatched),
            "by_final_runtime_role": dict(sorted(final_roles.items())),
            "by_surviving_identifier_classification": dict(sorted(classifications.items())),
        }

    return {
        "schema": "graphreader.source-global-ocr-false-positive-attribution.v1",
        "status": "synthetic_development_diagnostic_only",
        "workflow_report": report_ref,
        "generator_report": generator_ref,
        "source_global_scorer": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/score_native_workflow_ocr.py",
            "sha256": digest(SCORER_DIRECTORY / "score_native_workflow_ocr.py"),
        },
        "diagnostic_script": {
            "path": "tools/Diagnose-SourceGlobalOcrFalsePositives.py",
            "sha256": digest(Path(__file__).resolve()),
        },
        "binding": {
            "candidate_binding_sha256": report["candidate_binding_sha256"],
            "input_manifest_sha256": report["input_manifest_sha256"],
            "panel_count": len(panels),
            "source_count": len(sources),
            "coordinate_mapping": "reviewed source-global scorer applies authenticated panel crop translation once",
            "truth_denominator": "each source truth is counted once in its source split",
        },
        "matching": {
            "method": "frozen maximum-cardinality source-global IoU pairing",
            "iou_minimum": source_global.metric.MATCH_IOU_MINIMUM,
            "final_prediction_basis": "assembled OCR observations",
        },
        "attribution": by_split,
        "limitations": [
            "Only aggregate counts are emitted; no per-source, panel, text, box, or region-ID rows are retained.",
            "A same source/panel/identifier tuple in raw and final observations is a surviving-ID classification only, not actual runtime lineage.",
            "Inherited source-global scorer limitation: it validates recorded panel-image hashes and crop translations, but does not recompute crop bytes from the source image.",
            "Inherited source-global scorer limitation: every source must retain an OCR panel; a source that fails before OCR cannot be scored by this diagnostic.",
            "The diagnostic identifies no truth-free runtime correction: every observed final role can represent application text, and this evidence does not establish a safe generic suppression rule.",
            "This consumes no private or sealed corpus data, does not rerun inference, and does not establish release acceptance.",
        ],
        "private_reads": 0,
        "sealed_reads": 0,
        "inference_rerun": False,
        "optimizer_steps": 0,
        "production_approved": False,
    }


def self_test() -> None:
    raw = {("source-a", "panel-a", "raw-id")}
    require(
        classify_surviving_identifier(("source-a", "panel-a", "raw-id"), raw)
        == "same_identifier_present_in_raw_detector",
        "same saved ID should be classified as surviving",
    )
    require(
        classify_surviving_identifier(("source-a", "panel-a", "final-id"), raw) == "final_only_identifier",
        "nonmatching saved ID should be final-only",
    )
    require(
        classify_surviving_identifier(("source-a", "panel-b", "raw-id"), raw)
        == "final_only_identifier",
        "bare region IDs must not collide across panels in the same source",
    )
    require(
        classify_surviving_identifier(("source-b", "panel-a", "raw-id"), raw)
        == "final_only_identifier",
        "bare region IDs must not collide across sources for the same panel",
    )
    print(json.dumps({"status": "pass", "checks": 4}))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workflow-report", type=Path)
    parser.add_argument("--generator-report", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        require(not any((args.workflow_report, args.generator_report, args.output)),
                "--self-test cannot be combined with report or output arguments")
        self_test()
        return
    require(all((args.workflow_report, args.generator_report, args.output)),
            "--workflow-report, --generator-report, and --output are required")
    output = args.output.resolve()
    require(output.is_relative_to(ROOT), "output must remain in repository")
    require(not output.exists(), "output already exists")
    result = attribute(ROOT, args.workflow_report, args.generator_report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "attribution": result["attribution"]}))


if __name__ == "__main__":
    main()
