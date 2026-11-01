# SPDX-License-Identifier: Apache-2.0
"""Paired P3/P4/P5 inference on the same consumed synthetic proposal rows."""

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
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5_probe_eval_v3.json"
P4_EVALUATOR_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/evaluate_p4_probe.py"
P5_TRAINER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/train_classifier_p5.py"
P5_TRAIN_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5_train.json"
P5_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p5-hard-negative-weighted-rx6600xt/report.json"
P5_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p5-hard-negative-weighted-rx6600xt/checkpoint.pt"
P5_TRAIN_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-p5-hard-negative-training-preflight-v1.json"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-p5-paired-consumed-probe-v3"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-p5-paired-consumed-probe-v3-preflight.json"
THRESHOLD = 0.5
P4_EVALUATOR_SHA = "ea2e04d5440f8b0a8d3abfcf5fa34e13ad0be5ea1ac50ab0b1858f9518c27786"
P4_PAIR_REPORT_SHA = "daf4e6c212fbf491b3210e7e17bd9cb326f758efc9c106d035e7afedb70e90b5"
P4_PAIR_PREFLIGHT_SHA = "7dc48690de341fc85536d2fdd694eac6ec8384cd39aae772179d535b10f38de1"
P4_PAIR_SCORES_SHA = "47e92bc8488bb38beb458fb5e700f6405842230ac82946a35723bce96c9e30e7"
P5_TRAINER_SHA = "8817ee863eaf825ef6aa3e145ac257ccee195986aaafa0a016e6cc95b76c3c42"
P5_TRAIN_CONFIG_SHA = "914f5c8f6b52b317eabc0eb2a3a2814196a10214e5706a52edbedf54b368d5e7"
P5_REPORT_SHA = "f527ce75992de86bada23aeb0a9ddfced5c580e85ae64e3561d73ce5db4e85d8"
P5_CHECKPOINT_SHA = "d34c1b9ea9674f4b7d7a2dd41ca40d872bdc338b5d30092f7cc997c41d3e7445"
P5_TRAIN_PREFLIGHT_SHA = "bc563971cf4337d90c423e6964a3982650b0e5d3a8041cb7c91871edf3cf970e"
P5_TEACHER_RECEIPT_SHA = "cd5e51f4634a60a08e8fb2357c4723209346996cc1b42ab9e2ced8c077cc1f91"


class P5EvaluationError(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise P5EvaluationError("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise P5EvaluationError(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P5EvaluationError(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise P5EvaluationError(f"input is not bounded regular file: {relative}")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise P5EvaluationError(f"input grew beyond bound: {relative}")
    return raw


def load_exact(relative: str, expected_sha: str, name: str):
    raw = read_project(relative, 4_000_000)
    if digest(raw) != expected_sha:
        raise P5EvaluationError(f"pinned source hash mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise P5EvaluationError(f"cannot load pinned source: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, 4_000_000)) != expected_sha:
        raise P5EvaluationError(f"pinned source changed during execution: {relative}")
    return module


def validate_config() -> tuple[dict[str, Any], str]:
    raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(raw)
    runner_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    expected = {"schema": "graphreader.p5-paired-consumed-probe-config.v1",
        "recipe_id": "p5-vs-p4-vs-p3-same-eligible-consumed-probe-v3",
        "p5_evaluator_sha256": runner_sha, "p4_evaluator_sha256": P4_EVALUATOR_SHA,
        "p4_pair_report_sha256": P4_PAIR_REPORT_SHA,
        "p4_pair_preflight_sha256": P4_PAIR_PREFLIGHT_SHA,
        "p4_pair_score_rows_sha256": P4_PAIR_SCORES_SHA,
        "p5_trainer_sha256": P5_TRAINER_SHA,
        "p5_training_config_sha256": P5_TRAIN_CONFIG_SHA,
        "p5_training_report_sha256": P5_REPORT_SHA,
        "p5_checkpoint_sha256": P5_CHECKPOINT_SHA,
        "p5_training_preflight_sha256": P5_TRAIN_PREFLIGHT_SHA,
        "threshold": THRESHOLD, "visible_truth_denominator": 181,
        "proposal_rows": 365, "common_eligible_rows": 357,
        "duplicate_proposal_rows_preserved": True,
        "truth_read_after_all_three_models_inference": True,
        "holdout_probe_dev_sealed_private_payloads_read": False,
        "training_performed": False, "production_activation": False,
        "output_relative": OUTPUT_REL, "preflight_relative": PREFLIGHT_REL}
    if config != expected:
        raise P5EvaluationError("P5 paired evaluation config/source identity mismatch")
    return config, digest(raw)


def load_context():
    pair_eval = load_exact(P4_EVALUATOR_REL, P4_EVALUATOR_SHA, "p5_pinned_p4_paired_evaluator")
    p4_receipt, audit, p3_evaluator, p4_trainer, context, p4_data, p4_report, p4_checkpoint, common = pair_eval.make_preflight()
    saved_p4_receipt = read_project(pair_eval.PREFLIGHT_REL, 1_000_000)
    saved_p4_audit = read_project(pair_eval.OVERLAP_REL, 500_000)
    p4_receipt["overlap_audit_sha256"] = digest(saved_p4_audit)
    if digest(saved_p4_receipt) != P4_PAIR_PREFLIGHT_SHA or json.loads(saved_p4_receipt) != p4_receipt:
        raise P5EvaluationError("P4 frozen paired preflight changed")
    if digest(saved_p4_audit) != p4_receipt["overlap_audit_sha256"] or json.loads(saved_p4_audit) != audit:
        raise P5EvaluationError("P4 frozen overlap audit changed")
    if digest(read_project(pair_eval.OUTPUT_REL + "/report.json", 3_000_000)) != P4_PAIR_REPORT_SHA:
        raise P5EvaluationError("P4 paired report changed")
    if digest(read_project(pair_eval.OUTPUT_REL + "/proposal_scores.json", 3_000_000)) != P4_PAIR_SCORES_SHA:
        raise P5EvaluationError("P4 paired score rows changed")
    trainer = load_exact(P5_TRAINER_REL, P5_TRAINER_SHA, "p5_pinned_trainer_for_probe_eval")
    train_config_raw = read_project(P5_TRAIN_CONFIG_REL, 65_536)
    if digest(train_config_raw) != P5_TRAIN_CONFIG_SHA:
        raise P5EvaluationError("P5 train config changed")
    report_raw = read_project(P5_REPORT_REL, 1_000_000)
    checkpoint_raw = read_project(P5_CHECKPOINT_REL, 100_000_000)
    train_preflight_raw = read_project(P5_TRAIN_PREFLIGHT_REL, 1_000_000)
    if (digest(report_raw) != P5_REPORT_SHA or digest(checkpoint_raw) != P5_CHECKPOINT_SHA
            or digest(train_preflight_raw) != P5_TRAIN_PREFLIGHT_SHA):
        raise P5EvaluationError("P5 report/checkpoint/training preflight changed")
    p5_report = json.loads(report_raw)
    p5_config = json.loads(train_config_raw)
    p5_preflight = json.loads(train_preflight_raw)
    if (p5_report.get("status") != "completed-train-only-p5-hard-negative-weighting"
            or p5_report.get("checkpoint", {}).get("sha256") != P5_CHECKPOINT_SHA
            or p5_report.get("training_row_bindings_sha256") != p4_data["row_bindings_sha256"]
            or p5_preflight.get("status") != "preflight-passed-no-optimizer-step"
            or p5_preflight.get("p4_training_row_bindings_sha256") != p4_data["row_bindings_sha256"]
            or p5_report.get("data", {}).get("probe_dev_sealed_private_holdout_payloads_read") is not False):
        raise P5EvaluationError("P5 checkpoint/report does not bind exact train rows and preflight")
    bound = {"pair_eval": pair_eval, "p3_evaluator": p3_evaluator, "p4_trainer": p4_trainer,
        "p4_data": p4_data, "p4_checkpoint": p4_checkpoint,
        "p5_trainer": trainer, "p5_report": p5_report, "p5_checkpoint": checkpoint_raw,
        "p5_config": p5_config, "p5_preflight": p5_preflight}
    return bound, audit, context, common


def p5_scores(trainer: Any, p5_config: dict[str, Any], p5_report: dict[str, Any], checkpoint_raw: bytes,
              p4_data: dict[str, Any], rows: list[dict[str, Any]]) -> tuple[list[float], dict[str, Any]]:
    import torch
    _p3, p2, _cfg, source_hashes = trainer.prepare_stack()
    runtime = p2.helpers.require_rx_6600_xt(torch)
    if (runtime.get("device_name") != "AMD Radeon RX 6600 XT" or runtime.get("cpu_fallback") is not False
            or Path(sys.executable).resolve() != p2.helpers.GPU_PYTHON.resolve()):
        raise P5EvaluationError("P5 inference requires project RX 6600 XT launcher")
    device = torch.device(str(runtime["device"]))
    payload = torch.load(BytesIO(checkpoint_raw), map_location=device, weights_only=True)
    if (payload.get("model_identity") != "stroke-seeded-ink-validity-rf31-p5-train-hard-negative-weighted"
            or payload.get("p4_teacher_checkpoint_sha256") != "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9"
            or payload.get("p5_config_sha256") != digest(read_project(P5_TRAIN_CONFIG_REL, 65_536))
            or payload.get("p5_teacher_receipt_sha256") != P5_TEACHER_RECEIPT_SHA
            or payload.get("p4_train_row_bindings_sha256") != p4_data["row_bindings_sha256"]):
        raise P5EvaluationError("P5 checkpoint metadata differs from verified run")
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(payload["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise P5EvaluationError("P5 model parameters are not on RX 6600 XT")
    model.eval()
    scores = []
    with torch.inference_mode():
        for start in range(0, len(rows), 32):
            arrays = np.stack([r["pixels"] for r in rows[start:start+32]]).astype(np.float32, copy=False)
            batch = torch.from_numpy(arrays).unsqueeze(1).to(device)
            if batch.device != device:
                raise P5EvaluationError("P5 inference batch left RX 6600 XT")
            values = torch.sigmoid(model(batch)).reshape(-1).detach().cpu().tolist()
            if len(values) != len(arrays) or not all(math.isfinite(float(v)) for v in values):
                raise P5EvaluationError("P5 returned invalid score rows")
            scores.extend(float(v) for v in values)
    torch.cuda.synchronize(device)
    return scores, {"runtime": p2._versions(runtime), "device": str(device), "cpu_fallback": False,
        "checkpoint_sha256": digest(checkpoint_raw), "source_hashes": source_hashes,
        "inference_rows": len(scores), "inference_batches": math.ceil(len(scores)/32)}


def score_panels(pair, context: dict[str, Any], score_rows: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], int]:
    truth_helper = load_exact(pair.TRUTH_HELPER_REL, pair.TRUTH_HELPER_SHA, "p5_pair_truth_geometry")
    matcher = load_exact(pair.AXIS_HELPER_REL, pair.AXIS_HELPER_SHA, "p5_pair_axis_matcher")
    truths_by_panel: dict[str, list[dict[str, Any]]] = {}
    for source_id, rendered in context["rendered_sources"].items():
        truths_by_panel.update(truth_helper.source_truths(source_id, rendered["panels"]))
    visible_total = sum(t["source_stroke_visible"] for ts in truths_by_panel.values() for t in ts)
    if visible_total != 181:
        raise P5EvaluationError(f"visible tick denominator changed: {visible_total}")
    scores = {r["proposal_id"]: r for r in score_rows}
    scan_by_panel = {p["panel_id"]: p for result in context["scanner"]["source_results"] for p in result["panels"]}
    rows_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in context["all_rows"]:
        rows_by_panel[row["panel_id"]].append(row)
    panels = []
    for source in context["probe_manifest"]["sources"]:
        for rendered in context["rendered_sources"][source["source_id"]]["panels"]:
            panel_id = rendered["panel_id"]
            raw_panel = scan_by_panel[panel_id]
            visible = [t for t in truths_by_panel[panel_id] if t["source_stroke_visible"]]
            panel_rows = rows_by_panel[panel_id]
            panel = {"panel_id": panel_id, "source_id": source["source_id"],
                "visible_truth_count": len(visible), "needs_review": bool(raw_panel["needs_review"])}
            choices = {"raw_scan": panel_rows}
            for model_name in ("p3", "p4", "p5"):
                choices[model_name] = [r for r in panel_rows if r["proposal_id"] in scores
                    and scores[r["proposal_id"]][f"{model_name}_score"] >= THRESHOLD]
            for model_name, selected in choices.items():
                detections = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in selected]
                panel[model_name] = matcher.match_panel(visible, detections)
            panels.append(panel)
    return panels, visible_total


def metrics(panels: list[dict[str, Any]], key: str, matcher) -> dict[str, Any]:
    return matcher._metrics(panels, key)


def make_preflight():
    config, cfg_sha = validate_config()
    bound, audit, context, common = load_context()
    p4_data = bound["p4_data"]
    if len(common) != 357:
        raise P5EvaluationError("common eligible proposal count changed")
    receipt = {"schema": "graphreader.p5-paired-probe-preflight.v1",
        "status": "verified-before-paired-inference", "config_sha256": cfg_sha,
        "evaluator_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
        "p4_pair_report_sha256": P4_PAIR_REPORT_SHA, "p4_pair_preflight_sha256": P4_PAIR_PREFLIGHT_SHA,
        "p4_pair_scores_sha256": P4_PAIR_SCORES_SHA, "p4_training_hash_count": len({r["image_sha256"] for r in p4_data["rows"]}),
        "training_probe_hash_overlap_rows": audit["p4_train_probe_hash_overlap_rows"],
        "holdout_probe_hash_overlap_rows": audit["p1_holdout_probe_hash_overlap_rows"],
        "training_probe_source_id_overlap": audit["p4_train_probe_source_id_overlap"],
        "training_probe_source_identity_overlap": audit["p4_train_probe_source_identity_overlap"],
        "training_probe_full_panel_pixel_overlap": audit["p4_train_probe_full_panel_pixel_sha256_overlap"],
        "proposal_admission": {"total": len(context["all_rows"]), "common_eligible": len(common),
            "duplicates_preserved": True, "truth_used": False},
        "p5_training_report_sha256": P5_REPORT_SHA, "p5_checkpoint_sha256": P5_CHECKPOINT_SHA,
        "p5_training_preflight_sha256": P5_TRAIN_PREFLIGHT_SHA,
        "p5_row_bindings_sha256": bound["p5_report"]["training_row_bindings_sha256"],
        "visible_truth_denominator_expected_after_all_inference": 181,
        "optimizer_or_training_performed": False, "production_activation": False,
        "holdout_probe_dev_sealed_private_payloads_read": False}
    return receipt, bound, audit, context, common


def write_preflight() -> dict[str, Any]:
    if (ROOT / PREFLIGHT_REL).exists() or (ROOT / OUTPUT_REL).exists():
        raise P5EvaluationError("P5 paired evaluation outputs exist; refusing overwrite")
    receipt, bound, audit, context, common = make_preflight()
    out = ROOT / OUTPUT_REL
    out.mkdir(parents=False, exist_ok=False)
    audit_raw = json.dumps(audit, sort_keys=True, indent=2).encode() + b"\n"
    (out / "overlap_audit.json").write_bytes(audit_raw)
    receipt["p4_overlap_audit_sha256"] = digest(audit_raw)
    preflight_raw = json.dumps(receipt, sort_keys=True, indent=2).encode() + b"\n"
    (ROOT / PREFLIGHT_REL).write_bytes(preflight_raw)
    return receipt


def run() -> dict[str, Any]:
    saved_preflight_raw = read_project(PREFLIGHT_REL, 1_000_000)
    receipt, bound, audit, context, common = make_preflight()
    expected_audit_raw = json.dumps(audit, sort_keys=True, indent=2).encode() + b"\n"
    receipt["p4_overlap_audit_sha256"] = digest(expected_audit_raw)
    if json.loads(saved_preflight_raw) != receipt:
        raise P5EvaluationError("P5 paired preflight receipt changed before inference")
    audit_raw = read_project(OUTPUT_REL + "/overlap_audit.json", 500_000)
    if digest(audit_raw) != receipt["p4_overlap_audit_sha256"] or json.loads(audit_raw) != audit:
        raise P5EvaluationError("P5 paired overlap audit changed before inference")
    pair_eval = bound["pair_eval"]
    p5_train_cfg = bound["p5_config"]
    p5_report = bound["p5_report"]
    p5_checkpoint = bound["p5_checkpoint"]
    # Run all three model score passes on the same common eligible rows before target geometry is opened.
    p3_scores, p3_runtime = bound["p3_evaluator"]._infer_p3(common)
    p4_score_values, p4_runtime = pair_eval.p4_scores(bound["p4_trainer"], bound["p4_data"], bound["p4_checkpoint"], common)
    p5_values, p5_runtime = p5_scores(bound["p4_trainer"], p5_train_cfg, p5_report, p5_checkpoint, bound["p4_data"], common)
    if not (len(p3_scores) == len(p4_score_values) == len(p5_values) == len(common) == 357):
        raise P5EvaluationError("P3/P4/P5 were not scored on identical 357 rows")
    prior_p4_scores = json.loads(read_project(pair_eval.OUTPUT_REL + "/proposal_scores.json", 3_000_000))
    prior = {r["proposal_id"]: r for r in prior_p4_scores}
    combined_rows = []
    for row, s3, s4, s5 in zip(common, p3_scores, p4_score_values, p5_values):
        old = prior.get(row["proposal_id"])
        if old is None or abs(float(old["p3_score"]) - s3) > 1e-7 or abs(float(old["p4_score"]) - s4) > 1e-7:
            raise P5EvaluationError("P3/P4 re-inference does not match the saved paired scores")
        combined_rows.append({"proposal_id": row["proposal_id"], "source_id": row["source_id"],
            "panel_id": row["panel_id"], "proposal_index": row["proposal_index"],
            "crop_sha256": row["crop_sha256"], "p3_score": float(s3), "p4_score": float(s4), "p5_score": float(s5)})
    panels, visible_count = score_panels(pair_eval, context, combined_rows)
    panel_metrics = {key: {"aggregate": metrics(panels, key, pair_eval),
        "by_source": {source: metrics([p for p in panels if p["source_id"] == source], key, pair_eval)
            for source in sorted({p["source_id"] for p in panels})}}
        for key in ("raw_scan", "p3", "p4", "p5")}
    p4_saved = json.loads(read_project(pair_eval.OUTPUT_REL + "/report.json", 3_000_000))
    for name, oldkey in (("p3", "p3_at_05"), ("p4", "p4_at_05")):
        now = panel_metrics[name]["aggregate"]
        old = p4_saved["metrics"][oldkey]["aggregate"]
        for key in ("proposals", "true_matches", "unmatched_proposals", "precision", "recall"):
            if abs(float(now[key]) - float(old[key])) > 1e-12:
                raise P5EvaluationError(f"saved P4 paired result parity failed for {name}/{key}")
    report = {"schema": "graphreader.p5-paired-consumed-probe-report.v1",
        "status": "completed-paired-exploratory-consumed-synthetic-probe",
        "acceptance_status": "consumed synthetic development evidence only; not formal acceptance",
        "production_activation": False, "training_performed": False,
        "truth_read_after_all_three_model_inferences": True, "visible_truth_denominator": visible_count,
        "threshold": THRESHOLD, "proposal_admission": receipt["proposal_admission"],
        "runtime": {"p3": p3_runtime, "p4": p4_runtime, "p5": p5_runtime},
        "overlap_audit": {key: audit[key] for key in ("p4_training_hash_count", "p4_added_training_hash_count",
            "p4_train_probe_hash_overlap_rows", "p1_holdout_probe_hash_overlap_rows",
            "p4_train_probe_source_id_overlap", "p4_train_probe_source_identity_overlap",
            "p4_train_probe_full_panel_pixel_sha256_overlap")},
        "metrics": panel_metrics,
        "p3_p4_saved_parity_passed": True,
        "inputs": {"preflight_sha256": digest(saved_preflight_raw),
            "overlap_audit_sha256": receipt["p4_overlap_audit_sha256"],
            "p4_pair_report_sha256": P4_PAIR_REPORT_SHA, "p4_pair_score_rows_sha256": P4_PAIR_SCORES_SHA,
            "p5_checkpoint_sha256": P5_CHECKPOINT_SHA, "p5_training_report_sha256": P5_REPORT_SHA,
            "p5_training_preflight_sha256": P5_TRAIN_PREFLIGHT_SHA,
            "p5_trainer_sha256": P5_TRAINER_SHA, "p5_training_config_sha256": P5_TRAIN_CONFIG_SHA,
            "p5_eval_runner_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
            "p5_eval_config_sha256": digest(read_project(CONFIG_REL, 65_536))},
        "training_run": {"steps": p5_report["recipe"]["optimizer_steps"], "epochs": p5_report["recipe"]["epochs"],
            "loss_first": p5_report["training"]["loss_first"], "loss_last": p5_report["training"]["loss_last"],
            "gpu_residency_last_step": p5_report["recipe"]["gpu_residency_after_last_step"]},
        "score_rows": combined_rows,
        "score_rows_sha256": digest(json.dumps(combined_rows, sort_keys=True, separators=(",", ":")).encode()),
        "limitations": ["Consumed procedural synthetic development evidence, not acceptance.",
            "P5 was trained on the P4 train set only; this comparison does not establish broad generalization.",
            "All models use the exact common eligible crop rows and fixed 0.5 score threshold."]}
    output = ROOT / OUTPUT_REL
    if {item.name for item in output.iterdir()} != {"overlap_audit.json"}:
        raise P5EvaluationError("P5 evaluation output is not the preflight-only directory")
    (output / "proposal_scores.json").write_text(json.dumps(combined_rows, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    report["finished_utc"] = datetime.now(timezone.utc).isoformat()
    (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> None:
    if "--preflight" in sys.argv:
        result = write_preflight()
        print(json.dumps({"status": result["status"], "common_eligible": result["proposal_admission"]["common_eligible"]}, sort_keys=True))
    else:
        result = run()
        print(json.dumps({key: value["aggregate"] for key, value in result["metrics"].items()}, sort_keys=True))


if __name__ == "__main__":
    main()
