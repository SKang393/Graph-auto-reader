# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score localized dev recognition only after the frozen truth-blind reader pass."""
from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import re
import stat
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[6]
ARTIFACT_REL = "artifacts/stroke-seeded-xtick-v1-seed-crop-localization-dev-v1"
PROBE_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json"
PROBE_MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
P5_EVAL_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/evaluate_p5_probe.py"
P5_EVAL_SHA = "1dd3ada4bd9fdd9de33b71c72c604c05b8fe637c8561b822e82834c413f455b4"
HOUGH_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py"
HOUGH_SHA = "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979"
MATCH_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py"
MATCH_SHA = "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de"
RELAXED_EVAL_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/evaluate_relaxed_width_probe.py"
RELAXED_EVAL_SHA = "04b183c38966eb5ce5dfb84b87361f205a877135715bf48fc3b4709df83c4788"
NUMBER = re.compile(r"^-?\d+(?:\.\d+)?%?$", re.ASCII)
EXPECTED_VISIBLE = 181


class DevFinalizationError(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise DevFinalizationError("invalid project-relative path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        try:
            info = current.lstat()
        except OSError as exc:
            raise DevFinalizationError("missing/unreadable input path") from exc
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise DevFinalizationError("symlink/reparse input rejected")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise DevFinalizationError("non-directory path ancestor")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise DevFinalizationError("input missing or over size bound")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise DevFinalizationError("input exceeded size bound")
    return raw


def load_exact(relative: str, expected: str, name: str, maximum: int = 5_000_000):
    raw = read_project(relative, maximum)
    if digest(raw) != expected:
        raise DevFinalizationError(f"pinned dependency hash mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise DevFinalizationError(f"could not load pinned dependency: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, maximum)) != expected:
        raise DevFinalizationError(f"dependency changed during import: {relative}")
    return module


def recognition_decision(row: dict[str, Any], reader: str, crop_mode: str) -> tuple[bool, str | None]:
    text_key = ("NumericText" if reader == "numeric" else "OfficialText") if crop_mode == "localized" else (
        "FullNumericText" if reader == "numeric" else "FullOfficialText")
    confidence_key = ("NumericConfidence" if reader == "numeric" else "OfficialConfidence") if crop_mode == "localized" else (
        "FullNumericConfidence" if reader == "numeric" else "FullOfficialConfidence")
    text = row[text_key]
    if text is None or not NUMBER.fullmatch(text.strip()):
        return False, text
    if reader == "numeric":
        confidence = row[confidence_key]
        return confidence is not None and confidence >= 0.65, text
    return True, text


def enrich_recognition_rows(rec_rows: list[dict[str, Any]], input_rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Join only truth-blind proposal geometry and classifier scores by stable proposal ID."""
    inputs = {row["proposal_id"]: row for row in input_rows}
    if len(inputs) != len(input_rows) or {row["CaseId"] for row in rec_rows} != set(inputs):
        raise DevFinalizationError("input and reader proposal IDs differ")
    enriched = []
    for result in rec_rows:
        source = inputs[result["CaseId"]]
        if result["ImageHash"] != source["image_sha256"]:
            raise DevFinalizationError("reader result crop hash differs from input row")
        enriched.append({**result, "ProposalId": result["CaseId"], "Proposal": source["proposal"], "PanelId": source["panel_id"],
            "SourceId": source["source_id"], "P3Score": source["p3_score"], "P5Score": source["p5_score"],
            "P3Selected": source["p3_selected"], "P5Selected": source["p5_selected"]})
    return enriched


def evaluate_proposals(rows: list[dict[str, Any]], truth_by_panel: dict[str, list[dict[str, Any]]],
                       expected_by_case: dict[str, str | None], matcher: Any, *,
                       reader: str | None = None, crop_mode: str = "localized") -> dict[str, Any]:
    detections_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
    ids_by_panel: dict[str, list[str]] = defaultdict(list)
    recognitions = {row["ProposalId"]: row for row in rows}
    candidates = rows
    if reader is not None:
        candidates = [row for row in rows if recognition_decision(row, reader, crop_mode)[0]]
    for row in candidates:
        detections_by_panel[row["PanelId"]].append(row["Proposal"])
        ids_by_panel[row["PanelId"]].append(row["ProposalId"])
    matched = false = no_visible_fp = no_visible_panels = 0
    exact = wrong = empty = 0
    no_visible_nonempty = no_visible_numeric = unmatched_nonempty = unmatched_numeric = 0
    visible = sum(len(panel_rows) for panel_rows in truth_by_panel.values())
    per_source: dict[str, Counter] = defaultdict(Counter)
    for panel_id, truths in truth_by_panel.items():
        detections = detections_by_panel.get(panel_id, [])
        ids = ids_by_panel.get(panel_id, [])
        result = matcher.match_panel(truths, detections)
        matched += result["matched_count"]
        false += result["unmatched_detected_count"]
        if not truths:
            no_visible_panels += 1
            no_visible_fp += len(detections)
            for proposal_id in ids:
                prediction = recognitions[proposal_id]
                _, text = recognition_decision(prediction, reader or "official", crop_mode) if reader is not None else (False, prediction["OfficialText"])
                no_visible_nonempty += bool(text)
                no_visible_numeric += bool(text and NUMBER.fullmatch(text.strip()))
        matched_detection_indices = {pair["detection_index"] for pair in result["matched_pairs"]}
        for index, proposal_id in enumerate(ids):
            if index not in matched_detection_indices:
                prediction = recognitions[proposal_id]
                text_key = ("NumericText" if reader == "numeric" else "OfficialText") if crop_mode == "localized" else (
                    "FullNumericText" if reader == "numeric" else "FullOfficialText")
                text = prediction[text_key]
                unmatched_nonempty += bool(text)
                unmatched_numeric += bool(text and NUMBER.fullmatch(text.strip()))
        for pair in result["matched_pairs"]:
            proposal_id = ids[pair["detection_index"]]
            row = recognitions[proposal_id]
            if reader is None:
                continue
            text_key = ("NumericText" if reader == "numeric" else "OfficialText") if crop_mode == "localized" else (
                "FullNumericText" if reader == "numeric" else "FullOfficialText")
            text = row[text_key]
            expected = expected_by_case.get(pair["case_id"])
            if not text:
                empty += 1
            elif expected is not None and text.strip() == expected:
                exact += 1
            else:
                wrong += 1
            per_source[row["SourceId"]]["matched"] += 1
            per_source[row["SourceId"]]["exact"] += int(bool(text) and expected is not None and text.strip() == expected)
            per_source[row["SourceId"]]["wrong"] += int(bool(text) and (expected is None or text.strip() != expected))
            per_source[row["SourceId"]]["empty"] += int(not bool(text))
    return {
        "proposals": len(candidates), "matched_ticks": matched, "unmatched_false_proposals": false,
        "precision": matched / len(candidates) if candidates else 0.0,
        "recall_of_all_visible_truth": matched / visible if visible else 0.0,
        "visible_truth_denominator": visible, "no_visible_panels": no_visible_panels,
        "false_proposals_on_no_visible_panels": no_visible_fp,
        "recognized_on_matched_ticks": {"exact": exact, "wrong": wrong, "empty": empty,
            "exact_rate_of_full_visible_truth": exact / visible if visible else 0.0},
        "false_text_on_no_visible_panels": {"nonempty": no_visible_nonempty, "numeric_grammar": no_visible_numeric},
        "false_text_on_all_unmatched_proposals": {"nonempty": unmatched_nonempty, "numeric_grammar": unmatched_numeric},
        "per_source_recognition": {key: dict(sorted(value.items())) for key, value in sorted(per_source.items())},
    }


def main() -> int:
    artifact = ROOT / ARTIFACT_REL
    input_bytes = (artifact / "input.json").read_bytes()
    protocol_bytes = (artifact / "protocol.json").read_bytes()
    preflight_bytes = (artifact / "overlap_preflight.json").read_bytes()
    recognition_bytes = (artifact / "recognition_v1.json").read_bytes()
    input_doc, protocol, overlap, recognition = map(json.loads, (input_bytes, protocol_bytes, preflight_bytes, recognition_bytes))
    if recognition.get("completed") is not True or recognition.get("truth_read") is not False:
        raise DevFinalizationError("recognizer pass incomplete or has already read truth")
    if recognition.get("input_sha256") != digest(input_bytes) or protocol.get("input_sha256") != digest(input_bytes):
        raise DevFinalizationError("recognition/input byte binding mismatch")
    if overlap.get("truth_read") is not False or overlap.get("dev_sealed_private_inputs_read") is not False:
        raise DevFinalizationError("preflight already accessed labels or restricted inputs")
    rec_rows = recognition.get("recognitions")
    if not isinstance(rec_rows, list) or len(rec_rows) != len(input_doc["rows"]):
        raise DevFinalizationError("selected row count differs from truth-blind recognition output")
    if {r["CaseId"] for r in rec_rows} != {r["proposal_id"] for r in input_doc["rows"]}:
        raise DevFinalizationError("candidate IDs differ across input and recognition")
    rec_rows = enrich_recognition_rows(rec_rows, input_doc["rows"])

    # The gate above is the first point at which this script may read the consumed
    # development truth helpers, source manifest, or annotation payloads.
    relaxed = load_exact(RELAXED_EVAL_REL, RELAXED_EVAL_SHA, "localization_pinned_relaxed_evaluator")
    p5_eval = relaxed.load_exact(P5_EVAL_REL, P5_EVAL_SHA, "localization_pinned_p5_context")
    _bound, _audit, context, _common = p5_eval.load_context()
    truth_helper = relaxed.load_exact(HOUGH_REL, HOUGH_SHA, "localization_pinned_probe_truth")
    matcher = relaxed.load_exact(MATCH_REL, MATCH_SHA, "localization_pinned_tick_matcher")
    truth_by_panel: dict[str, list[dict[str, Any]]] = {}
    for source_id, rendered in context["rendered_sources"].items():
        truth_by_panel.update(truth_helper.source_truths(source_id, rendered["panels"]))
    visible = sum(row["source_stroke_visible"] for panel in truth_by_panel.values() for row in panel)
    if visible != EXPECTED_VISIBLE:
        raise DevFinalizationError(f"visible-tick denominator changed: {visible}")
    truth_by_panel = {panel: [row for row in values if row["source_stroke_visible"]] for panel, values in truth_by_panel.items()}

    manifest_raw = read_project(PROBE_MANIFEST_REL, 5_000_000)
    if digest(manifest_raw) != PROBE_MANIFEST_SHA:
        raise DevFinalizationError("consumed probe source manifest identity changed")
    manifest = json.loads(manifest_raw)
    if manifest.get("split") != "exploratory-synthetic-diagnostic":
        raise DevFinalizationError("source manifest split identity changed")
    expected_by_case = {}
    for record in manifest["records"]:
        annotation_rel = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/" + record["annotation_path"]
        annotation_raw = read_project(annotation_rel, 100_000)
        if digest(annotation_raw) != record["annotation_sha256"]:
            raise DevFinalizationError("probe annotation bytes changed")
        annotation = json.loads(annotation_raw)
        expected_by_case[record["case_id"]] = annotation.get("expected_tick_label")

    all_rows = rec_rows
    report = {
        "schema": "graphreader.seed-crop-localization-dev-result.v1",
        "acceptance_status": "already-consumed synthetic development diagnostic only; not formal acceptance",
        "train_localization_rule": json.loads(read_project("artifacts/stroke-seeded-xtick-v1-seed-crop-localization-train-v1/protocol.json", 100_000))["roi_seed_relative_xyxy"],
        "overlap_preflight": {k: overlap[k] for k in ("rows", "eligible_rows", "quarantined_rows", "unique_crop_hashes",
            "crop_hash_overlap_localizer_train", "crop_hash_overlap_localizer_holdout", "source_id_overlap", "source_identity_overlap",
            "full_panel_hash_overlap", "p1_holdout_source_id_overlap", "p1_holdout_source_identity_overlap")},
        "threshold": 0.5, "visible_truth_denominator": visible,
        "no_visible_panel_count": sum(not panel for panel in truth_by_panel.values()),
        "metrics": {},
        "input_sha256": digest(input_bytes), "recognition_output_sha256": digest(recognition_bytes),
        "truth_manifest_sha256": digest(manifest_raw), "truth_read_after_recognizer_complete": True,
        "private_or_sealed_inputs_read": False, "training_performed": False, "production_activation": False,
    }
    for model_key in ("p3", "p5"):
        classifier_rows = [row for row in all_rows if row["P3Selected" if model_key == "p3" else "P5Selected"]]
        report["metrics"][model_key] = {
            "classifier_only_detection": evaluate_proposals(classifier_rows, truth_by_panel, expected_by_case, matcher),
            "numeric_localized": evaluate_proposals(classifier_rows, truth_by_panel, expected_by_case, matcher, reader="numeric", crop_mode="localized"),
            "numeric_whole_seed": evaluate_proposals(classifier_rows, truth_by_panel, expected_by_case, matcher, reader="numeric", crop_mode="whole"),
            "official_localized": evaluate_proposals(classifier_rows, truth_by_panel, expected_by_case, matcher, reader="official", crop_mode="localized"),
            "official_whole_seed": evaluate_proposals(classifier_rows, truth_by_panel, expected_by_case, matcher, reader="official", crop_mode="whole"),
        }
    old_metrics = json.loads(read_project("artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/paired_report.json", 3_000_000))["metrics"]
    for key in ("p3", "p5"):
        metric = report["metrics"][key]["classifier_only_detection"]
        old = old_metrics[key]
        if (metric["proposals"], metric["matched_ticks"], metric["unmatched_false_proposals"], metric["false_proposals_on_no_visible_panels"]) != (
            old["proposals"], old["true_matches"], old["unmatched_proposals"], old["false_proposals_on_no_visible_panels"]):
            raise DevFinalizationError(f"frozen classifier geometry parity failed for {key}")
    report_path = artifact / "dev_report_v1.json"
    if report_path.exists():
        raise FileExistsError("refusing to overwrite dev report")
    report_path.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": report_path.as_posix(), "visible": visible,
                      "p3": report["metrics"]["p3"], "p5": report["metrics"]["p5"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
