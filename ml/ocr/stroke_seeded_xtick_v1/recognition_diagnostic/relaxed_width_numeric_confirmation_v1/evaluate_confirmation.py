# SPDX-License-Identifier: Apache-2.0
"""Source-bound scoring, recognizer handoff, and post-recognition probe metrics."""

from __future__ import annotations

from collections import defaultdict
from hashlib import sha256
from pathlib import Path
import importlib.util
import json
import re
import stat
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[5]
HERE = Path(__file__).resolve().parent
RELAXED = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/evaluate_relaxed_width_probe.py"
RELAXED_SHA = "04b183c38966eb5ce5dfb84b87361f205a877135715bf48fc3b4709df83c4788"
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/relaxed_width_probe_v2.json"
RUN_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-numeric-confirmation-v1"
SCORE_REL = RUN_REL + "/score_rows.json"
RECOGNITION_REL = RUN_REL + "/recognition.json"
RESULT_REL = RUN_REL + "/result.json"
RULE_REL = "ml/ocr/component_ensemble_v5/artifacts/P1-run/candidate-report.json"
RULE_SHA = "e5095b63221b7a745e48ead61011a7794e5da54e0fc24fd641d785bc09257260"
GRAMMAR = re.compile(r"^-?\d+(?:\.\d+)?%?$", re.ASCII)
THRESHOLD = 0.5
NUMERIC_THRESHOLD = 0.65
EXPECTED_ROWS = 433
PROTOCOL_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/protocol.json"


class ConfirmationError(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise ConfirmationError("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ConfirmationError(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise ConfirmationError(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise ConfirmationError(f"input is not bounded regular file: {relative}")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise ConfirmationError(f"input exceeds bound: {relative}")
    return raw


def load_exact(relative: str, expected: str, module_name: str, maximum: int = 4_000_000):
    raw = read_project(relative, maximum)
    if digest(raw) != expected:
        raise ConfirmationError(f"pinned dependency changed: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None:
        raise ConfirmationError(f"cannot load pinned dependency: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, maximum)) != expected:
        raise ConfirmationError(f"dependency changed during import: {relative}")
    return module


def numeric_confirmation(text: str | None, confidence: float | None) -> bool:
    return (text is not None and confidence is not None and np.isfinite(confidence)
            and confidence >= NUMERIC_THRESHOLD and GRAMMAR.fullmatch(text.strip()) is not None)


def official_confirmation(text: str | None, confidence: float | None) -> bool:
    # The frozen V8 composition's OfficialMinimumConfidence is 0.0. This is
    # only a grammar/output comparator, not a new product confidence gate.
    return text is not None and GRAMMAR.fullmatch(text.strip()) is not None


def result_field(row: dict, snake_name: str):
    """Read C# record JSON emitted with System.Text.Json's default PascalCase."""
    pascal = "".join(part.capitalize() for part in snake_name.split("_"))
    return row[snake_name] if snake_name in row else row.get(pascal)


def score_truth_blind() -> dict:
    runner_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    relaxed = load_exact(RELAXED, RELAXED_SHA, "numeric_confirmation_pinned_relaxed_evaluator")
    config_raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(config_raw)
    saved_preflight = json.loads(read_project(
        "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/preflight.json", 1_000_000))
    saved_manifest_raw = read_project(
        "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/candidate_rows.json", 4_000_000)
    saved_audit_raw = read_project(
        "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/overlap_audit.json", 1_000_000)
    protocol_raw = read_project(PROTOCOL_REL, 65_536)
    protocol = json.loads(protocol_raw)
    if (digest(saved_manifest_raw) != saved_preflight["candidate_manifest_sha256"]
            or digest(saved_audit_raw) != saved_preflight["overlap_audit_sha256"]
            or (ROOT / RUN_REL).exists()
            or protocol.get("numeric_confidence_threshold") != NUMERIC_THRESHOLD
            or protocol.get("p3_p5_threshold") != THRESHOLD
            or protocol.get("official_confidence_threshold") != 0.0
            or protocol.get("decision_rule_evidence", {}).get("sha256") != RULE_SHA):
        raise ConfirmationError("prior probe binding mismatch or unique output already exists")
    generated, manifest_raw, audit_raw, _all_rows, eligible, quarantined, context, bound, p5_eval = relaxed.generate_preflight(
        config, digest(config_raw), RELAXED_SHA)
    if (digest(manifest_raw) != digest(saved_manifest_raw) or digest(audit_raw) != digest(saved_audit_raw)
            or digest(manifest_raw) != saved_preflight["candidate_manifest_sha256"]
            or len(eligible) != EXPECTED_ROWS or len(quarantined) != 9):
        raise ConfirmationError("P3/P5/holdout image-hash quarantine differs from frozen paired run")
    p3_scores, p3_runtime = bound["p3_evaluator"]._infer_p3(eligible)
    p5_scores, p5_runtime = p5_eval.p5_scores(
        bound["p4_trainer"], bound["p5_config"], bound["p5_report"], bound["p5_checkpoint"],
        bound["p4_data"], eligible)
    relaxed.validate_runtime(p3_runtime["runtime"])
    relaxed.validate_runtime(p5_runtime["runtime"])
    if len(p3_scores) != len(eligible) or len(p5_scores) != len(eligible):
        raise ConfirmationError("P3/P5 score inventory mismatch")

    score_rows = []
    unique_crop_paths: dict[str, str] = {}
    pending_crops: dict[str, bytes] = {}
    crop_output_rel = RUN_REL + "/crops"
    for row, p3_score, p5_score in zip(eligible, p3_scores, p5_scores):
        s3, s5 = float(p3_score), float(p5_score)
        rec = {key: row[key] for key in ("proposal_id", "source_id", "panel_id", "proposal", "needs_review", "crop_sha256")}
        rec.update({"p3_score": s3, "p5_score": s5, "p3_selected": s3 >= THRESHOLD, "p5_selected": s5 >= THRESHOLD})
        if s3 >= THRESHOLD or s5 >= THRESHOLD:
            rel_path = unique_crop_paths.get(row["crop_sha256"])
            if rel_path is None:
                source = context["source_images"][row["source_id"]]
                png, pixels = bound["p3_evaluator"].crop_bytes(source, row["proposal"]["center"])
                if digest(png) != row["crop_sha256"] or not np.array_equal(pixels, row["pixels"]):
                    raise ConfirmationError("selected OCR crop differs from the P3 classifier input crop")
                rel_path = f"{crop_output_rel}/{row['crop_sha256']}.png"
                unique_crop_paths[row["crop_sha256"]] = rel_path
                pending_crops[rel_path] = png
            rec["crop_path"] = rel_path
        score_rows.append(rec)

    rule_raw = read_project(RULE_REL, 2_000_000)
    rule = json.loads(rule_raw)
    if (rule.get("selected_threshold") != NUMERIC_THRESHOLD or rule.get("sealed_public_archive_opened") is not False
            or rule.get("synthetic_only") is not True):
        raise ConfirmationError("numeric confirmation rule is not bound to the synthetic training result")
    run_dir = ROOT / RUN_REL
    run_dir.mkdir(parents=True, exist_ok=False)
    for relative, raw in pending_crops.items():
        path = ROOT / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
    body = {"schema": "graphreader.relaxed-width-numeric-confirmation-score-rows.v1",
        "status": "truth-blind-classifier-score-pass-complete",
        "runner_sha256": runner_sha, "relaxed_evaluator_sha256": RELAXED_SHA,
        "relaxed_config_sha256": digest(config_raw), "preflight_sha256": digest(read_project(
            "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/preflight.json", 1_000_000)),
        "candidate_manifest_sha256": digest(saved_manifest_raw), "overlap_audit_sha256": digest(saved_audit_raw),
        "decision_rule_evidence_sha256": digest(rule_raw),
        "numeric_model_sha256": protocol["models"]["numeric"]["sha256"],
        "protocol_sha256": digest(protocol_raw),
        "official_model_sha256": protocol["models"]["official"]["sha256"],
        "p3_checkpoint_sha256": p3_runtime["checkpoint_sha256"], "p5_checkpoint_sha256": p5_runtime["checkpoint_sha256"],
        "p3_runtime": p3_runtime["runtime"], "p5_runtime": p5_runtime["runtime"],
        "p3_p5_threshold": THRESHOLD, "numeric_confidence_threshold": NUMERIC_THRESHOLD,
        "numeric_grammar": GRAMMAR.pattern, "eligible_rows": len(eligible), "quarantined_rows": len(quarantined),
        "selected_union_rows": sum(r["p3_selected"] or r["p5_selected"] for r in score_rows),
        "unique_selected_crop_hashes": len(unique_crop_paths), "rows": score_rows,
        "quarantined": quarantined,
        "truth_read": False, "holdout_bytes_read": False, "private_or_sealed_inputs_read": False,
        "training_performed": False, "production_activation": False}
    raw = (json.dumps(body, sort_keys=True, separators=(",", ":")) + "\n").encode()
    (ROOT / SCORE_REL).write_bytes(raw)
    return {"score_rows_sha256": digest(raw), "eligible_rows": len(eligible),
        "selected_union_rows": body["selected_union_rows"], "unique_selected_crop_hashes": len(unique_crop_paths),
        "runtime": {"p3": p3_runtime["runtime"], "p5": p5_runtime["runtime"]}, "truth_read": False}


def finalize_after_recognition() -> dict:
    scores_raw = read_project(SCORE_REL, 20_000_000)
    recognition_raw = read_project(RECOGNITION_REL, 10_000_000)
    scores, recognition = json.loads(scores_raw), json.loads(recognition_raw)
    if (scores.get("status") != "truth-blind-classifier-score-pass-complete"
            or recognition.get("score_rows_sha256") != digest(scores_raw)
            or recognition.get("truth_read") is not False
            or recognition.get("completed") is not True):
        raise ConfirmationError("truth disclosure is blocked until both bound recognizers finish")
    if (recognition.get("numeric_model_sha256") != scores["numeric_model_sha256"]
            or recognition.get("official_model_sha256") != scores["official_model_sha256"]):
        raise ConfirmationError("recognition model identities differ from scored input receipt")
    relaxed = load_exact(RELAXED, RELAXED_SHA, "numeric_confirmation_pinned_relaxed_truth")
    p5_eval = load_exact(relaxed.P5_EVALUATOR_REL, relaxed.P5_EVALUATOR_SHA,
        "numeric_confirmation_pinned_p5_truth_helper")
    bound, _old_audit, context, _common = p5_eval.load_context()
    truth_helper = relaxed.load_exact(
        "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py",
        "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979", "numeric_confirmation_truth_geometry")
    matcher = relaxed.load_exact(
        "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py",
        "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de", "numeric_confirmation_axis_matcher")
    truth_by_panel = {}
    for source_id, rendered in context["rendered_sources"].items():
        truth_by_panel.update(truth_helper.source_truths(source_id, rendered["panels"]))
    if sum(row["source_stroke_visible"] for panel in truth_by_panel.values() for row in panel) != 181:
        raise ConfirmationError("visible truth denominator changed")
    truth_by_panel = {panel_id: [row for row in rows if row["source_stroke_visible"]]
                      for panel_id, rows in truth_by_panel.items()}

    recognition_by_id = {result_field(row, "proposal_id"): row for row in recognition["rows"]}
    source_manifest_raw = read_project(
        "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json", 5_000_000)
    if digest(source_manifest_raw) != relaxed.PROBE_MANIFEST_SHA:
        raise ConfirmationError("probe source manifest differs from pinned corpus identity")
    source_manifest = json.loads(source_manifest_raw)
    expected_by_case = {}
    for record in source_manifest["records"]:
        annotation_rel = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/" + record["annotation_path"]
        annotation_raw = read_project(annotation_rel, 100_000)
        if digest(annotation_raw) != record["annotation_sha256"]:
            raise ConfirmationError("annotation changed after its hash-bound recognition pass")
        annotation = json.loads(annotation_raw)
        expected_by_case[record["case_id"]] = annotation.get("expected_tick_label")

    all_rows = scores["rows"]
    def calc(selected_rows: list[dict], truth_by_panel: dict[str, list[dict]], model_key: str) -> dict:
        detections_by_panel: dict[str, list[dict]] = defaultdict(list)
        ids_by_panel: dict[str, list[str]] = defaultdict(list)
        for row in selected_rows:
            detections_by_panel[row["panel_id"]].append(row["proposal"])
            ids_by_panel[row["panel_id"]].append(row["proposal_id"])
        matched = false = 0
        visible = sum(len(value) for value in truth_by_panel.values())
        no_visible_fp = 0
        exact = wrong = empty = 0
        for panel_id, truths in truth_by_panel.items():
            detections = detections_by_panel.get(panel_id, [])
            result = matcher.match_panel(truths, detections)
            matched += result["matched_count"]
            false += result["unmatched_detected_count"]
            if not truths:
                no_visible_fp += len(detections)
            for pair in result["matched_pairs"]:
                proposal_id = ids_by_panel[panel_id][pair["detection_index"]]
                expected = expected_by_case.get(pair["case_id"])
                rec = recognition_by_id.get(proposal_id, {})
                pred_text = result_field(rec, model_key + "_text") if rec else None
                if pred_text is None:
                    empty += 1
                elif expected is not None and pred_text == expected:
                    exact += 1
                else:
                    wrong += 1
        return {"proposals": len(selected_rows), "matched_ticks": matched, "unmatched_false_proposals": false,
            "precision": matched / len(selected_rows) if selected_rows else 0.0,
            "recall": matched / visible if visible else 0.0,
            "visible_tick_denominator": visible, "false_proposals_on_no_visible_panels": no_visible_fp,
            "recognition_on_matched_ticks": {"exact": exact, "wrong": wrong, "empty": empty}}

    summaries = {}
    for model_key in ("p3", "p5"):
        classifier_rows = [row for row in all_rows if row[f"{model_key}_selected"]]
        summaries[f"{model_key}_classifier_only_numeric_text"] = calc(classifier_rows, truth_by_panel, "numeric")
        summaries[f"{model_key}_classifier_only_official_text"] = calc(classifier_rows, truth_by_panel, "official")
        for reader in ("numeric", "official"):
            confirmed = [row for row in classifier_rows
                         if result_field(recognition_by_id[row["proposal_id"]], f"{reader}_confirmed")]
            summaries[f"{model_key}_{reader}_confirmed"] = calc(confirmed, truth_by_panel, reader)
    report = {"schema": "graphreader.relaxed-width-numeric-confirmation-result.v1",
        "acceptance_status": "exploratory consumed synthetic development diagnostic only",
        "score_rows_sha256": digest(scores_raw), "recognition_sha256": digest(recognition_raw),
        "numeric_rule": {"confidence": NUMERIC_THRESHOLD, "grammar": GRAMMAR.pattern,
            "evidence_sha256": scores["decision_rule_evidence_sha256"]},
        "metrics": summaries, "visible_truth_denominator": 181,
        "private_or_sealed_inputs_read": False, "training_performed": False, "production_activation": False,
        "limitations": ["Classifier crops are proposal-centered 256x96 crops, not independently localized text boxes.",
            "The probe is already-consumed synthetic development evidence, not acceptance.",
            "Recognizer metrics on the same crop construction do not establish real-graph performance."]}
    out = ROOT / RESULT_REL
    if out.exists():
        raise ConfirmationError("result output exists; refusing overwrite")
    out.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    if "--score" in sys.argv:
        print(json.dumps(score_truth_blind(), sort_keys=True))
        return 0
    if "--finalize" in sys.argv:
        print(json.dumps(finalize_after_recognition(), sort_keys=True))
        return 0
    raise ConfirmationError("choose --score or --finalize")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"failed: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(1)
