# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Paired P3/P4 classifier evaluation on the consumed truth-blind probe rows."""

from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import importlib.util
import json
import math
from pathlib import Path
import stat
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[7]
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4_probe_eval_v2.json"
P4_RUNNER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/train_classifier_p4.py"
P4_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4.json"
P4_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-preflight-v4.json"
P4_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/report.json"
P4_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/checkpoint.pt"
P4_PRETRAIN_HASHES_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
P4_ADDITIONS_REL = "artifacts/stroke-seeded-xtick-v1-wideproposal-train-candidates-p3/manifest.json"
P3_EVALUATOR_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/evaluate_p3_probe.py"
P3_EVALUATOR_SHA = "263506da81caaa40f75cf6cb8c8b06e55c77b9a19e7a2b88af420a311e87337f"
P3_PRIOR_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1/report.json"
P3_PRIOR_REPORT_SHA = "0cfe59debc97b64a20a2b2a1b53e4394e72c25f50d3b58c50581864a52750ac0"
P3_PRIOR_SCORES_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1/proposal_scores.json"
P3_PRIOR_SCORES_SHA = "c8f3adfb1c090ec57257064ba315c2b59d50bba49a93d23145f5071e6cfc50fc"
P3_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1-preflight-v2.json"
P3_PREFLIGHT_SHA = "7fffab0c37e4747f351f086abfef955bcf0afc55092778feb6f25abaa e4c35ae".replace(" ", "")
PROBE_MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
SCANNER_SHA = "0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081"
P3_CHECKPOINT_SHA = "8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92"
P3_CONFIG_SHA = "927b93c88d7e61dc505516995b72f2235aa7fc8824e5fc54b45ae6741c3c5717"
P3_TRAIN_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
AXIS_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py"
AXIS_HELPER_SHA = "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de"
TRUTH_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py"
TRUTH_HELPER_SHA = "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979"
MATERIALIZER_REL = "ml/ocr/stroke_seeded_xtick_v1/materialize.py"
MATERIALIZER_SHA = "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-p4-paired-consumed-probe-v2"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-p4-paired-consumed-probe-v2-preflight.json"
OVERLAP_REL = OUTPUT_REL + "/overlap_audit.json"
THRESHOLD = 0.5
P4_RUNNER_SHA = "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde"


class P4EvaluationError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum <= 0:
        raise P4EvaluationError("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise P4EvaluationError(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P4EvaluationError(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise P4EvaluationError(f"input is not bounded regular file: {relative}")
    with current.open("rb") as stream:
        data = stream.read(maximum + 1)
    if len(data) > maximum:
        raise P4EvaluationError(f"input grew beyond bound: {relative}")
    return data


def load_exact(relative: str, expected_sha: str, name: str):
    raw = read_project(relative, 4_000_000)
    if digest(raw) != expected_sha:
        raise P4EvaluationError(f"pinned source hash mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise P4EvaluationError(f"cannot construct pinned source module: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, 4_000_000)) != expected_sha:
        raise P4EvaluationError(f"pinned source changed during execution: {relative}")
    return module


def validate_probe_config() -> tuple[dict[str, Any], str]:
    raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(raw)
    runner_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    expected = {"schema": "graphreader.stroke-seeded-p4-p3-paired-consumed-probe-config.v1",
        "recipe_id": "stroke-seeded-xtick-p4-vs-p3-same-eligible-proposals-v2",
        "p4_runner_sha256": "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde",
        "p4_config_sha256": "e137dc7e6fde4bec1a5ed33135f5f0269325236d48ebe2a6ce2c2607cb4670bd",
        "p4_report_sha256": "F7AD0713FF10866C5EA5F093059AA8E3CAD3A932FBA6682C7D22B212319FFD35".lower(),
        "p4_checkpoint_sha256": "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9",
        "p4_preflight_sha256": "0f2d943793bd084658b42021d717ab4f90d0cb4526682ef6c22589153d6376db",
        "p3_evaluator_sha256": P3_EVALUATOR_SHA, "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
        "p3_prior_report_sha256": P3_PRIOR_REPORT_SHA, "p3_prior_score_rows_sha256": P3_PRIOR_SCORES_SHA,
        "p3_preflight_sha256": P3_PREFLIGHT_SHA, "probe_manifest_sha256": PROBE_MANIFEST_SHA,
        "scanner_proposals_sha256": SCANNER_SHA, "p3_config_sha256": P3_CONFIG_SHA,
        "p3_train_manifest_sha256": P3_TRAIN_MANIFEST_SHA,
        "axis_matcher_sha256": AXIS_HELPER_SHA, "truth_geometry_sha256": TRUTH_HELPER_SHA,
        "crop_materializer_sha256": MATERIALIZER_SHA, "score_threshold": THRESHOLD,
        "crop_geometry": "pinned evaluator P1 crop materializer, proposal center x and center y minus 2 pixels",
        "eligible_rows": "intersection of P3 and P4 hash-clean eligibility; identical rows scored by both models",
        "visible_truth_denominator": 181, "truth_read_after_both_model_inferences": True,
        "p1_holdout_payload_bytes_read": False, "probe_private_or_sealed_inputs_read": False,
        "training_performed": False, "production_activation": False,
        "output_relative": OUTPUT_REL, "preflight_relative": PREFLIGHT_REL,
        "expected_evaluator_sha256": runner_sha}
    if config != expected:
        raise P4EvaluationError("P4 paired evaluation config/source identity mismatch")
    return config, digest(raw)


def load_input_context():
    evaluator = load_exact(P3_EVALUATOR_REL, P3_EVALUATOR_SHA, "p4_pair_pinned_p3_evaluator")
    p3_preflight, p3_eligible, context = evaluator.preflight()
    saved_preflight_raw = read_project(P3_PREFLIGHT_REL, 1_000_000)
    if digest(saved_preflight_raw) != P3_PREFLIGHT_SHA or json.loads(saved_preflight_raw) != p3_preflight:
        raise P4EvaluationError("frozen P3 preflight does not match its current inputs")
    p4 = load_exact(P4_RUNNER_REL, P4_RUNNER_SHA, "p4_pair_pinned_p4_trainer")
    p4_config_raw = read_project(P4_CONFIG_REL, 65_536)
    p4_config = json.loads(p4_config_raw)
    if digest(p4_config_raw) != "e137dc7e6fde4bec1a5ed33135f5f0269325236d48ebe2a6ce2c2607cb4670bd":
        raise P4EvaluationError("P4 training config changed")
    p4_data = p4.load_dataset(p4_config)
    p4_report_raw = read_project(P4_REPORT_REL, 1_000_000)
    p4_checkpoint_raw = read_project(P4_CHECKPOINT_REL, 100_000_000)
    p4_preflight_raw = read_project(P4_PREFLIGHT_REL, 1_000_000)
    if (digest(p4_report_raw) != "f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35"
            or digest(p4_checkpoint_raw) != "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9"
            or digest(p4_preflight_raw) != "0f2d943793bd084658b42021d717ab4f90d0cb4526682ef6c22589153d6376db"):
        raise P4EvaluationError("P4 report/checkpoint/preflight bytes differ from frozen run")
    p4_report = json.loads(p4_report_raw)
    p4_preflight = json.loads(p4_preflight_raw)
    if (p4_report.get("status") != "completed-train-only-p4-finetune"
            or p4_report.get("checkpoint", {}).get("sha256") != digest(p4_checkpoint_raw)
            or p4_report.get("p4_runner_sha256") != "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde"
            or p4_report.get("p4_config_sha256") != digest(p4_config_raw)
            or p4_report.get("data", {}).get("holdout_used_for_training_or_metrics") is not False
            or p4_preflight.get("status") != "preflight-passed-no-optimizer"
            or p4_preflight.get("gpu_preflight", {}).get("optimizer_created") is not False):
        raise P4EvaluationError("P4 report/checkpoint identity or no-holdout status mismatch")
    return evaluator, p4, p3_preflight, p3_eligible, context, p4_data, p4_report, p4_checkpoint_raw


def p4_overlap(context: dict[str, Any], p3_eligible: list[dict[str, Any]],
               p4_data: dict[str, Any]) -> tuple[dict[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
    rows = context["all_rows"]
    if len(rows) != 365:
        raise P4EvaluationError("consumed probe proposal row count changed")
    p3_hashes = set(context["train_hash_labels"])
    p4_hashes = {str(row["image_sha256"]) for row in p4_data["rows"]}
    if len(p4_hashes) != 465 or len(p3_hashes) != 446 or not p3_hashes <= p4_hashes:
        raise P4EvaluationError("P4 training hash inventory does not include the full 446+19 training set")
    holdout_groups = set(context["holdout_groups"])
    holdout_hashes = {str(row["image_sha256"]) for row in context["train_manifest"]["records"]
                      if row.get("source_group_id") in holdout_groups}
    probe_hashes: dict[str, list[str]] = defaultdict(list)
    for row in rows:
        probe_hashes[row["crop_sha256"]].append(row["proposal_id"])
    p4_overlap_rows = [{"crop_sha256": h, "proposal_ids": ids} for h, ids in sorted(probe_hashes.items()) if h in p4_hashes]
    holdout_overlap_rows = [{"crop_sha256": h, "proposal_ids": ids} for h, ids in sorted(probe_hashes.items()) if h in holdout_hashes]
    # Preserve the exact eligible rows returned by the pinned P3 preflight.
    p4_eligible, p4_quarantine = [], []
    for row in rows:
        reasons = []
        if row["crop_sha256"] in p4_hashes:
            reasons.append("exact_P4_training_crop_hash_overlap")
        if row["crop_sha256"] in holdout_hashes:
            reasons.append("exact_P1_holdout_crop_hash_metadata_overlap")
        if reasons:
            p4_quarantine.append({"proposal_id": row["proposal_id"], "crop_sha256": row["crop_sha256"], "reasons": reasons})
        else:
            p4_eligible.append(row)
    p3_ids = {row["proposal_id"] for row in p3_eligible}
    p4_ids = {row["proposal_id"] for row in p4_eligible}
    common_rows = common_eligible_rows(rows, p3_ids, p4_ids)

    train_groups = set(p4_data["groups"])
    source_rows = {row["source_id"]: row for row in context["train_manifest"]["source_groups"]}
    train_identity = {source_rows[g]["source_identity_sha256"] for g in train_groups}
    train_panel = {source_rows[g]["reconstructed_full_panel_pixel_sha256"] for g in train_groups}
    probe_sources = context["probe_manifest"]["sources"]
    probe_ids = {row["source_id"] for row in probe_sources}
    probe_identity = {row["source_identity_sha256"] for row in probe_sources}
    probe_panel = {row["source_pixel_sha256"] for row in probe_sources}
    p4_membership: dict[str, set[str]] = defaultdict(set)
    for row in p4_data["rows"]:
        p4_membership[row["image_sha256"]].update(row["source_group_ids"])
    audit = {"schema": "graphreader.p4-probe-training-overlap-audit.v1",
        "scope": "all P4 train crop hashes checked against every retained consumed-probe scanner proposal row and P1 holdout crop hash metadata; probe proposal rows are not deduplicated",
        "p4_checkpoint_sha256": "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9",
        "p4_report_sha256": "f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35",
        "p4_training_manifest_sha256s": p4_data["input_manifest_hashes"],
        "p4_training_hash_count": len(p4_hashes), "p4_training_crop_hashes": sorted(p4_hashes),
        "p4_training_hash_set_sha256": digest(json.dumps(sorted(p4_hashes), separators=(",", ":")).encode()),
        "p4_training_group_membership_sha256": digest(json.dumps({k: sorted(v) for k, v in sorted(p4_membership.items())}, sort_keys=True, separators=(",", ":")).encode()),
        "p3_baseline_training_hash_count": len(p3_hashes), "p4_added_training_hash_count": len(p4_hashes - p3_hashes),
        "p1_holdout_crop_hash_count": len(holdout_hashes),
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "probe_scanner_proposal_rows": len(rows),
        "probe_unique_crop_hashes": len(probe_hashes), "probe_duplicate_rows_preserved": True,
        "p4_train_probe_hash_overlap_hashes": p4_overlap_rows,
        "p4_train_probe_hash_overlap_rows": sum(len(item["proposal_ids"]) for item in p4_overlap_rows),
        "p1_holdout_probe_hash_overlap_hashes": holdout_overlap_rows,
        "p1_holdout_probe_hash_overlap_rows": sum(len(item["proposal_ids"]) for item in holdout_overlap_rows),
        "p4_source_group_count": len(train_groups), "probe_source_group_count": len(probe_ids),
        "p4_train_probe_source_id_overlap": sorted(train_groups & probe_ids),
        "p4_train_probe_source_identity_overlap": sorted(train_identity & probe_identity),
        "p4_train_probe_full_panel_pixel_sha256_overlap": sorted(train_panel & probe_panel),
        "p1_holdout_or_probe_payload_bytes_read": False, "private_or_sealed_inputs_read": False}
    return audit, common_rows, p4_quarantine


def _metrics(panels: list[dict[str, Any]], key: str) -> dict[str, Any]:
    visible = sum(panel["visible_truth_count"] for panel in panels)
    proposals = sum(panel[key]["usable_detected_x_stroke_count"] for panel in panels)
    matched = sum(panel[key]["matched_count"] for panel in panels)
    unmatched = sum(panel[key]["unmatched_detected_count"] for panel in panels)
    no_visible = [panel for panel in panels if panel["visible_truth_count"] == 0]
    return {"visible_truth_ticks": visible, "proposals": proposals, "true_matches": matched,
        "unmatched_proposals": unmatched, "precision": matched / proposals if proposals else 0.0,
        "recall": matched / visible if visible else 0.0,
        "no_visible_tick_panels": len(no_visible),
        "false_proposals_on_no_visible_tick_panels": sum(p[key]["usable_detected_x_stroke_count"] for p in no_visible),
        "no_visible_panels_with_proposals": sum(p[key]["usable_detected_x_stroke_count"] > 0 for p in no_visible),
        "axis_uncertain_panels": sum(bool(p["needs_review"]) for p in panels)}


def common_eligible_rows(rows: list[dict[str, Any]], p3_ids: set[str], p4_ids: set[str]) -> list[dict[str, Any]]:
    row_ids = [str(row["proposal_id"]) for row in rows]
    if len(row_ids) != len(set(row_ids)):
        raise P4EvaluationError("duplicate proposal row identity")
    common_ids = p3_ids & p4_ids
    if not common_ids <= set(row_ids):
        raise P4EvaluationError("eligibility references a missing proposal row")
    return [row for row in rows if row["proposal_id"] in common_ids]


def p4_scores(p4, p4_data: dict[str, Any], checkpoint_raw: bytes, rows: list[dict[str, Any]]) -> tuple[list[float], dict[str, Any]]:
    import torch
    p3, p2, _config, stack = p4.prepare_stack()
    runtime = p2.helpers.require_rx_6600_xt(torch)
    if (runtime.get("device_name") != "AMD Radeon RX 6600 XT" or runtime.get("cpu_fallback") is not False
            or Path(sys.executable).resolve() != p2.helpers.GPU_PYTHON.resolve()):
        raise P4EvaluationError("P4 inference requires project RX 6600 XT launcher")
    device = torch.device(str(runtime["device"]))
    payload = torch.load(BytesIO(checkpoint_raw), map_location=device, weights_only=True)
    if (payload.get("model_identity") != "stroke-seeded-ink-validity-rf31-p4-wideproposal-finetune"
            or payload.get("p3_checkpoint_sha256") != P3_CHECKPOINT_SHA
            or payload.get("p4_config_sha256") != digest(read_project(P4_CONFIG_REL, 65_536))
            or payload.get("train_row_bindings_sha256") != p4_data["row_bindings_sha256"]):
        raise P4EvaluationError("P4 checkpoint metadata differs from the verified training run")
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(payload["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise P4EvaluationError("P4 model parameters are not on the RX 6600 XT")
    model.eval()
    scores: list[float] = []
    with torch.inference_mode():
        for start in range(0, len(rows), 32):
            arrays = np.stack([row["pixels"] for row in rows[start:start + 32]]).astype(np.float32, copy=False)
            batch = torch.from_numpy(arrays).unsqueeze(1).to(device)
            if batch.device != device:
                raise P4EvaluationError("P4 inference input left the RX 6600 XT")
            values = torch.sigmoid(model(batch)).reshape(-1).detach().cpu().tolist()
            if len(values) != len(arrays) or not all(math.isfinite(float(value)) for value in values):
                raise P4EvaluationError("P4 produced missing or non-finite scores")
            scores.extend(float(value) for value in values)
    torch.cuda.synchronize(device)
    return scores, {"runtime": runtime, "device": str(device), "source_hashes": stack,
                    "checkpoint_sha256": digest(checkpoint_raw), "inference_rows": len(scores),
                    "inference_batches": math.ceil(len(scores) / 32), "cpu_fallback": False}


def make_preflight() -> tuple[dict[str, Any], dict[str, Any], Any, Any, dict[str, Any], Any, dict[str, Any], bytes, list[dict[str, Any]]]:
    config, config_sha = validate_probe_config()
    evaluator, p4, p3_preflight, p3_eligible, context, p4_data, p4_report, p4_checkpoint = load_input_context()
    audit, common_rows, p4_quarantine = p4_overlap(context, p3_eligible, p4_data)
    p3_ids = {row["proposal_id"] for row in p3_eligible}
    common_ids = {row["proposal_id"] for row in common_rows}
    if len(common_rows) != len(common_ids):
        raise P4EvaluationError("proposal row identity duplicated")
    receipt = {"schema": "graphreader.p4-paired-probe-preflight.v1",
        "status": "verified-before-paired-inference", "p4_eval_config_sha256": config_sha,
        "p4_eval_runner_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
        "p4_runner_sha256": "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde",
        "p4_config_sha256": digest(read_project(P4_CONFIG_REL, 65_536)),
        "p4_checkpoint_sha256": digest(p4_checkpoint), "p4_report_sha256": digest(read_project(P4_REPORT_REL, 1_000_000)),
        "p4_training_data_bindings_sha256": p4_data["row_bindings_sha256"],
        "p3_evaluator_sha256": P3_EVALUATOR_SHA, "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "scanner_proposals_sha256": SCANNER_SHA,
        "source_hashes": {"axis_matcher": AXIS_HELPER_SHA, "truth_geometry": TRUTH_HELPER_SHA,
                          "crop_materializer": MATERIALIZER_SHA,
                          "p3_evaluator_stack": context["source_receipts"] and p3_preflight["evaluator_source_sha256"]},
        "p4_hash_overlap_audit": audit,
        "proposal_admission": {"total_rows": len(context["all_rows"]),
            "p3_eligible": len(p3_ids), "p4_quarantined": len(p4_quarantine),
            "common_eligible_for_both": len(common_rows),
            "p3_and_p4_receive_identical_proposal_ids": True,
            "duplicate_proposal_rows_preserved": True,
            "proposal_truth_used": False},
        "visible_truth_denominator_expected_after_inference": 181,
        "holdout_or_probe_payload_bytes_opened_before_inference": False,
        "optimizer_execution": False, "production_activation": False,
        "p3_preflight_receipt_sha256": digest(read_project(P3_PREFLIGHT_REL, 1_000_000))}
    return receipt, audit, evaluator, p4, context, p4_data, p4_report, p4_checkpoint, common_rows


def write_preflight() -> dict[str, Any]:
    if (ROOT / PREFLIGHT_REL).exists() or (ROOT / OVERLAP_REL).exists():
        raise P4EvaluationError("P4 evaluation preflight/audit output exists; refusing overwrite")
    receipt, audit, *_ = make_preflight()
    (ROOT / OUTPUT_REL).mkdir(parents=True, exist_ok=False)
    audit_raw = json.dumps(audit, sort_keys=True, indent=2).encode() + b"\n"
    receipt["overlap_audit_sha256"] = digest(audit_raw)
    (ROOT / OVERLAP_REL).write_bytes(audit_raw)
    preflight_raw = json.dumps(receipt, sort_keys=True, indent=2).encode() + b"\n"
    (ROOT / PREFLIGHT_REL).write_bytes(preflight_raw)
    return receipt


def run() -> dict[str, Any]:
    if not (ROOT / PREFLIGHT_REL).is_file() or not (ROOT / OVERLAP_REL).is_file():
        raise P4EvaluationError("run preflight first; overlap audit is missing")
    receipt, audit, evaluator, p4, context, p4_data, p4_report, p4_checkpoint, common_rows = make_preflight()
    audit_raw = read_project(OVERLAP_REL, 500_000)
    saved_receipt_raw = read_project(PREFLIGHT_REL, 1_000_000)
    receipt["overlap_audit_sha256"] = digest(json.dumps(audit, sort_keys=True, indent=2).encode() + b"\n")
    if json.loads(audit_raw) != audit or json.loads(saved_receipt_raw) != receipt:
        raise P4EvaluationError("fresh overlap/preflight receipt changed before inference")
    p3_scores, p3_runtime = evaluator._infer_p3(common_rows)
    p4_values, p4_runtime = p4_scores(p4, p4_data, p4_checkpoint, common_rows)
    if len(p3_scores) != len(common_rows) or len(p4_values) != len(common_rows):
        raise P4EvaluationError("P3/P4 score rows differ from identical eligible proposal set")
    prior_rows = json.loads(read_project(P3_PRIOR_SCORES_REL, 3_000_000))["rows"]
    prior_by_id = {row["proposal_id"]: row for row in prior_rows}
    score_by_id = {}
    for row, score3, score4 in zip(common_rows, p3_scores, p4_values):
        prior = prior_by_id.get(row["proposal_id"])
        if prior is None or prior.get("p3_score") is None or abs(float(prior["p3_score"]) - float(score3)) > 1e-7:
            raise P4EvaluationError("frozen P3 inference does not match retained score rows")
        score_by_id[row["proposal_id"]] = {"p3_score": float(score3), "p4_score": float(score4)}

    # Only now inspect synthetic target tick annotations, after both model score passes.
    truth_helper = load_exact(TRUTH_HELPER_REL, TRUTH_HELPER_SHA, "p4_pair_truth_geometry")
    common = load_exact(AXIS_HELPER_REL, AXIS_HELPER_SHA, "p4_pair_axis_matcher")
    truths_by_panel: dict[str, list[dict[str, Any]]] = {}
    for source_id, rendered in context["rendered_sources"].items():
        truths_by_panel.update(truth_helper.source_truths(source_id, rendered["panels"]))
    visible_total = sum(t["source_stroke_visible"] for truths in truths_by_panel.values() for t in truths)
    if visible_total != 181:
        raise P4EvaluationError(f"full visible truth denominator changed: {visible_total}")
    scan_by_panel = {panel["panel_id"]: panel for result in context["scanner"]["source_results"]
                     for panel in result["panels"]}
    rows_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in context["all_rows"]:
        rows_by_panel[row["panel_id"]].append(row)
    panels = []
    for source in context["probe_manifest"]["sources"]:
        source_id = source["source_id"]
        for rendered_panel in context["rendered_sources"][source_id]["panels"]:
            panel_id = rendered_panel["panel_id"]
            raw_panel = scan_by_panel[panel_id]
            visible = [row for row in truths_by_panel[panel_id] if row["source_stroke_visible"]]
            proposals = rows_by_panel[panel_id]
            p3_selected = [r for r in proposals if r["proposal_id"] in score_by_id
                           and score_by_id[r["proposal_id"]]["p3_score"] >= THRESHOLD]
            p4_selected = [r for r in proposals if r["proposal_id"] in score_by_id
                           and score_by_id[r["proposal_id"]]["p4_score"] >= THRESHOLD]
            record = {"panel_id": panel_id, "source_id": source_id,
                "visible_truth_count": len(visible), "needs_review": bool(raw_panel["needs_review"])}
            for key, selected in (("raw_scan", proposals), ("p3_at_05", p3_selected), ("p4_at_05", p4_selected)):
                detections = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in selected]
                record[key] = common.match_panel(visible, detections)
            panels.append(record)

    metrics = {}
    for key in ("raw_scan", "p3_at_05", "p4_at_05"):
        all_metric = _metrics(panels, key)
        by_source = {source_id: _metrics([p for p in panels if p["source_id"] == source_id], key)
                     for source_id in sorted({p["source_id"] for p in panels})}
        metrics[key] = {"aggregate": all_metric, "by_source": by_source}
    old_report = json.loads(read_project(P3_PRIOR_REPORT_REL, 3_000_000))
    old_p3 = old_report["raw_scan_p2_and_p3_metrics"]["p3_filter_eligible"]["aggregate"]
    p3_actual = metrics["p3_at_05"]["aggregate"]
    p3_parity = {"prior_report_sha256": P3_PRIOR_REPORT_SHA,
        "prior_eligible_metric": old_p3, "paired_eligible_metric": p3_actual,
        "p3_parity_passed": len(score_by_id) == 357 and all(
            abs(float(p3_actual[k]) - float(old_p3[old]) ) < 1e-12
            for k, old in (("proposals", "proposals"), ("true_matches", "matched_ticks"),
                           ("unmatched_proposals", "unmatched_proposals"),
                           ("precision", "precision"), ("recall", "recall")))}
    score_rows = [{"proposal_id": row["proposal_id"], "source_id": row["source_id"],
        "panel_id": row["panel_id"], "proposal_index": row["proposal_index"],
        "crop_sha256": row["crop_sha256"], "eligible_for_both": row["proposal_id"] in score_by_id,
        "p3_score": score_by_id.get(row["proposal_id"], {}).get("p3_score"),
        "p4_score": score_by_id.get(row["proposal_id"], {}).get("p4_score")} for row in context["all_rows"]]
    report = {"schema": "graphreader.p4-paired-consumed-probe-report.v1",
        "status": "completed-paired-exploratory-consumed-synthetic-probe",
        "acceptance_status": "consumed development evidence only; not formal acceptance",
        "production_activation": False, "training_performed": False,
        "truth_used_after_both_model_inferences": True, "visible_truth_denominator": 181,
        "inputs": {"preflight_sha256": digest(saved_receipt_raw), "overlap_audit_sha256": digest(audit_raw),
            "p4_checkpoint_sha256": "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9",
            "p4_training_report_sha256": "f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35",
            "p4_runner_sha256": "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde",
            "p4_config_sha256": digest(read_project(P4_CONFIG_REL, 65_536)),
            "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p3_evaluator_sha256": P3_EVALUATOR_SHA,
            "probe_manifest_sha256": PROBE_MANIFEST_SHA, "scanner_proposals_sha256": SCANNER_SHA,
            "axis_matcher_sha256": AXIS_HELPER_SHA, "truth_geometry_sha256": TRUTH_HELPER_SHA,
            "crop_materializer_sha256": MATERIALIZER_SHA},
        "runtime": {"p3": p3_runtime, "p4": p4_runtime},
        "threshold": THRESHOLD,
        "proposal_admission": receipt["proposal_admission"],
        "overlap_audit": {k: audit[k] for k in ("p4_training_hash_count", "p4_added_training_hash_count",
            "p4_train_probe_hash_overlap_rows", "p1_holdout_probe_hash_overlap_rows",
            "p4_train_probe_source_id_overlap", "p4_train_probe_source_identity_overlap",
            "p4_train_probe_full_panel_pixel_sha256_overlap")},
        "metrics": metrics, "frozen_p3_parity": p3_parity,
        "score_rows_sha256": digest(json.dumps(score_rows, sort_keys=True, separators=(",", ":")).encode()),
        "proposal_scores": score_rows,
        "training_run": {"optimizer_steps": 460, "epochs": 20, "loss_first": p4_report["training"]["loss_first"],
            "loss_last": p4_report["training"]["loss_last"],
            "duration_proxy": "report write timestamp minus started receipt time; exact in-process training duration was not recorded",
            "started_utc": json.loads(read_project(P4_REPORT_REL.replace("report.json", "started.json"), 65_536))["started_utc"]},
        "limitations": ["This is the already-consumed procedural synthetic development probe, not formal acceptance.",
            "P3 and P4 were scored on the same eligible proposal IDs at the fixed 0.5 threshold; duplicate scanner rows were preserved.",
            "The P3 training lineage retains the historical P2 imported-helper provenance caveat.",
            "The P4 run is exploratory only and has not been promoted or integrated into production."]}
    out = ROOT / OUTPUT_REL
    if (not out.is_dir() or out.is_symlink()
            or {item.name for item in out.iterdir()} != {"overlap_audit.json"}):
        raise P4EvaluationError("paired evaluation output is not the fresh preflight-only directory")
    (out / "started.json").write_text(json.dumps({"status": "completed-inference-starting-report-write",
        "p4_checkpoint_sha256": report["inputs"]["p4_checkpoint_sha256"]}, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    (out / "proposal_scores.json").write_text(json.dumps(score_rows, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    report["finished_utc"] = datetime.now(timezone.utc).isoformat()
    (out / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> None:
    if "--preflight" in sys.argv:
        receipt = write_preflight()
        print(json.dumps({"status": receipt["status"], "preflight": PREFLIGHT_REL,
                          "overlap_audit": OVERLAP_REL,
                          "common_eligible": receipt["proposal_admission"]["common_eligible_for_both"]}, sort_keys=True))
        return
    report = run()
    print(json.dumps({key: value["aggregate"] for key, value in report["metrics"].items()}, sort_keys=True))


if __name__ == "__main__":
    main()
