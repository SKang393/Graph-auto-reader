#!/usr/bin/env python3
"""Aggregate fixed-output geometry and confidence attribution for synthetic OCR.

The reviewed source-global scorer authenticates the saved replay and translates
each panel crop into source pixels.  This diagnostic only partitions its final
matched controls and unmatched detections; it neither reruns inference nor
selects a runtime threshold.  Output contains aggregates only.
"""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
import math
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
SCORER_DIRECTORY = ROOT / "tools" / "GraphReader.SyntheticRuntimeEvidence"
if str(SCORER_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SCORER_DIRECTORY))

import score_native_workflow_ocr as source_global  # noqa: E402


PINNED_MATCHER_MODULE_HASHES = {
    "score_full_ocr_candidate_v2.py": "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656",
    "score_official_head_candidate.py": "719bb18c30821cdd44b65fc9ede38f6d111fb3631e07c7c69d98d1116aa575a2",
}
FIXED_MATCH_IOU_MINIMUM = 0.5


class DiagnosticError(ValueError):
    """Saved evidence cannot support the fixed-output attribution."""


def digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise DiagnosticError(message)


def validate_matcher_configuration(metric_hashes, iou_minimum: float) -> None:
    require(metric_hashes == PINNED_MATCHER_MODULE_HASHES,
            "source-global scorer matcher-module pins differ from reviewed values")
    require(iou_minimum == FIXED_MATCH_IOU_MINIMUM,
            "source-global scorer IoU threshold differs from fixed value")


def validate_matcher_binding() -> None:
    """Prevent direct matcher use from bypassing the scorer's pinned identities."""
    validate_matcher_configuration(source_global.METRIC_HASHES, source_global.metric.MATCH_IOU_MINIMUM)
    for filename, expected_hash in PINNED_MATCHER_MODULE_HASHES.items():
        require(digest(SCORER_DIRECTORY / filename) == expected_hash,
                f"pinned matcher module changed: {filename}")


def prediction_key(prediction) -> tuple[str, str, str]:
    panel_id, separator, region_id = prediction.prediction_id.partition("\n")
    require(bool(panel_id and separator and region_id), "malformed scorer prediction identity")
    require(isinstance(prediction.source_sha256, str) and prediction.source_sha256,
            "malformed scorer prediction source identity")
    return prediction.source_sha256, panel_id, region_id


def iou(left, right) -> float:
    x0, y0 = max(left.left, right.left), max(left.top, right.top)
    x1, y1 = min(left.right, right.right), min(left.bottom, right.bottom)
    intersection = max(0.0, x1 - x0) * max(0.0, y1 - y0)
    union = left.width * left.height + right.width * right.height - intersection
    return intersection / union if union > 0 else 0.0


def overlap_band(value: float) -> str:
    require(isinstance(value, (int, float)) and math.isfinite(value) and 0 <= value <= 1,
            "invalid IoU")
    if value == 0:
        return "0"
    if value < 0.25:
        return "(0,0.25)"
    if value < 0.5:
        return "[0.25,0.5)"
    if value < 0.75:
        return "[0.5,0.75)"
    return "[0.75,1]"


def confidence_band(value: float) -> str:
    require(isinstance(value, (int, float)) and not isinstance(value, bool)
            and math.isfinite(value) and 0 <= value <= 1, "invalid saved OCR confidence")
    if value < 0.5:
        return "[0,0.5)"
    if value < 0.75:
        return "[0.5,0.75)"
    if value < 0.9:
        return "[0.75,0.9)"
    return "[0.9,1]"


def aspect_band(box) -> str:
    require(box.width > 0 and box.height > 0, "empty scorer prediction box")
    ratio = box.width / box.height
    if ratio <= 1:
        return "tall_or_square_(0,1]"
    if ratio <= 3:
        return "landscape_(1,3]"
    return "wide_(3,infinity)"


def metadata_by_prediction(root: Path, report, workflow_report: Path) -> dict:
    """Read only authenticated final OCR metadata required for aggregate bins."""
    records = {}
    folder = workflow_report.parent
    for entry in report["calibration_diagnostic_files"]:
        record = source_global.read(root, entry, base=folder)
        source_global.validate_envelope(record, report)
        observation, panel_source = record["observation"], record["panel_source"]
        panel_id = observation["panel_id"]
        source_hash = panel_source["source"]["image"]["sha256"]
        ocr = observation["ocr"]
        require(ocr["panel_id"] == panel_id, "OCR panel identity mismatch")
        for region in ocr["regions"]:
            key = source_hash, panel_id, region["region_id"]
            require(key not in records, "duplicate final OCR metadata identity")
            confidence = region.get("confidence")
            require(isinstance(confidence, (int, float)) and not isinstance(confidence, bool)
                    and math.isfinite(confidence) and 0 <= confidence <= 1,
                    "invalid final OCR confidence")
            points = region.get("polygon", {}).get("points")
            require(isinstance(points, list) and len(points) >= 3, "invalid final OCR polygon")
            records[key] = {"confidence": confidence, "polygon_vertex_count": len(points)}
    return records


def partition(root: Path, workflow_report: Path, generator_report: Path) -> dict:
    workflow_report, generator_report = workflow_report.resolve(), generator_report.resolve()
    require(workflow_report.is_relative_to(root), "workflow report must remain in repository")
    require(generator_report.is_relative_to(root), "generator report must remain in repository")
    report_ref = {"path": str(workflow_report), "sha256": digest(workflow_report)}
    generator_ref = {"path": str(generator_report), "sha256": digest(generator_report)}
    validate_matcher_binding()
    report, sources, panels, observations = source_global.authenticate_runtime(root, report_ref)
    truths, source_splits = source_global.load_truths(root, generator_ref, sources, panels)
    metadata = metadata_by_prediction(root, report, workflow_report)
    all_final_predictions = {
        prediction_key(prediction)
        for values in observations["assembled_ocr"].values() for prediction in values
    }
    require(set(metadata) == all_final_predictions,
            "final OCR metadata does not exactly bind the source-global predictions")

    results = {}
    for split, split_truths in sorted(truths.items()):
        source_ids = {source_id for source_id, source_split in source_splits.items() if source_split == split}
        aggregates = {
            "nearest_truth_iou_band": {"unmatched": Counter(), "matched_control": Counter()},
            "same_source_nearest_prediction_iou_band": {"unmatched": Counter(), "matched_control": Counter()},
            "saved_ocr_confidence_band": {"unmatched": Counter(), "matched_control": Counter()},
            "source_box_aspect_band": {"unmatched": Counter(), "matched_control": Counter()},
            "polygon_vertex_count": {"unmatched": Counter(), "matched_control": Counter()},
        }
        counts = Counter()
        for source_id in sorted(source_ids):
            source_truths = [truth for truth in split_truths if truth.source_sha256 == source_id]
            predictions = tuple(observations["assembled_ocr"].get(source_id, ()))
            pairs = source_global.metric._maximum_cardinality_pairs(predictions, source_truths)
            matched_indexes = {prediction_index for prediction_index, _ in pairs}
            for prediction_index, prediction in enumerate(predictions):
                group = "matched_control" if prediction_index in matched_indexes else "unmatched"
                counts[group] += 1
                nearest_truth = max((iou(prediction.box, truth.box) for truth in source_truths), default=0.0)
                nearest_prediction = max(
                    (iou(prediction.box, other.box) for other_index, other in enumerate(predictions)
                     if other_index != prediction_index), default=0.0
                )
                saved = metadata[prediction_key(prediction)]
                aggregates["nearest_truth_iou_band"][group][overlap_band(nearest_truth)] += 1
                aggregates["same_source_nearest_prediction_iou_band"][group][overlap_band(nearest_prediction)] += 1
                aggregates["saved_ocr_confidence_band"][group][confidence_band(saved["confidence"])] += 1
                aggregates["source_box_aspect_band"][group][aspect_band(prediction.box)] += 1
                aggregates["polygon_vertex_count"][group][str(saved["polygon_vertex_count"])] += 1
        results[split] = {
            "source_count": len(source_ids),
            "truth_count": len(split_truths),
            "assembled_unmatched_count": counts["unmatched"],
            "assembled_matched_control_count": counts["matched_control"],
            **{name: {group: dict(sorted(values.items())) for group, values in groups.items()}
               for name, groups in aggregates.items()},
        }

    return {
        "schema": "graphreader.source-global-ocr-fixed-output-geometry-attribution.v1",
        "status": "synthetic_development_diagnostic_only",
        "workflow_report": report_ref,
        "generator_report": generator_ref,
        "source_global_scorer": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/score_native_workflow_ocr.py",
            "sha256": digest(SCORER_DIRECTORY / "score_native_workflow_ocr.py"),
        },
        "diagnostic_script": {
            "path": "tools/Diagnose-SourceGlobalOcrFalsePositiveGeometry.py",
            "sha256": digest(Path(__file__).resolve()),
        },
        "binding": {
            "candidate_binding_sha256": report["candidate_binding_sha256"],
            "input_manifest_sha256": report["input_manifest_sha256"],
            "source_count": len(sources),
            "panel_count": len(panels),
            "coordinate_mapping": "reviewed source-global scorer applies authenticated panel crop translation once",
            "truth_denominator": "each source truth is counted once in its source split",
        },
        "matcher_binding": {
            "modules": PINNED_MATCHER_MODULE_HASHES,
            "intersection_over_union_minimum": FIXED_MATCH_IOU_MINIMUM,
            "controls": "geometry-matched under frozen source-global IoU pairing; text and role do not determine this control population",
        },
        "partitions": results,
        "limitations": [
            "Only aggregate counts are emitted; no source, panel, text, geometry, or region-ID rows are retained.",
            "The confidence is saved final OCR output confidence, not an independently calibrated probability.",
            "The fixed bands describe saved output only and are not a threshold sweep or runtime selection recommendation.",
            "A high same-source prediction overlap is geometric proximity only; it does not establish runtime lineage or a safe duplicate-suppression rule.",
            "Inherited source-global scorer limitation: recorded panel-image hashes and crop translations are checked, but crop bytes are not recomputed from source pixels.",
            "Inherited source-global scorer limitation: sources failing before OCR have no panel observations and cannot be scored.",
            "Controls are geometry-matched only. Text and role are not used to create the control population.",
            "No truth-free runtime correction is asserted without a deterministic condition that separates unmatched detections from geometry-matched controls.",
            "This consumes no private or sealed corpus data, does not rerun inference, and does not establish release acceptance.",
        ],
        "private_reads": 0,
        "sealed_reads": 0,
        "inference_rerun": False,
        "optimizer_steps": 0,
        "production_approved": False,
    }


def self_test() -> None:
    require(overlap_band(0) == "0" and overlap_band(0.5) == "[0.5,0.75)", "overlap bands")
    require(confidence_band(0.9) == "[0.9,1]" and confidence_band(0.5) == "[0.5,0.75)", "confidence bands")

    class Box:
        left, top, right, bottom = 0.0, 0.0, 2.0, 1.0
        width, height = 2.0, 1.0

    require(aspect_band(Box()) == "landscape_(1,3]", "aspect band")
    validate_matcher_configuration(PINNED_MATCHER_MODULE_HASHES, FIXED_MATCH_IOU_MINIMUM)
    try:
        validate_matcher_configuration({**PINNED_MATCHER_MODULE_HASHES, "unexpected.py": "0" * 64}, FIXED_MATCH_IOU_MINIMUM)
    except DiagnosticError:
        pass
    else:
        raise AssertionError("altered matcher pins must fail closed")
    try:
        validate_matcher_configuration(PINNED_MATCHER_MODULE_HASHES, 0.49)
    except DiagnosticError:
        pass
    else:
        raise AssertionError("altered IoU threshold must fail closed")
    print(json.dumps({"status": "pass", "checks": 6}))


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
    result = partition(ROOT, args.workflow_report, args.generator_report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "partitions": result["partitions"]}))


if __name__ == "__main__":
    main()
