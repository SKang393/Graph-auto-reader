# SPDX-License-Identifier: Apache-2.0
"""Train-only GPU inference preflight for a proposed weighted-loss P5 recipe."""

from __future__ import annotations

from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import stat
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[7]
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5.json"
P4_RUNNER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/train_classifier_p4.py"
P4_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4.json"
P4_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/report.json"
P4_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/checkpoint.pt"
P4_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-preflight-v4.json"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-p5-hard-negative-train-preflight-v1.json"
P4_RUNNER_SHA = "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde"
P4_CONFIG_SHA = "e137dc7e6fde4bec1a5ed33135f5f0269325236d48ebe2a6ce2c2607cb4670bd"
P4_REPORT_SHA = "f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35"
P4_CHECKPOINT_SHA = "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9"
P4_PREFLIGHT_SHA = "0f2d943793bd084658b42021d717ab4f90d0cb4526682ef6c22589153d6376db"
EXPECTED_HARD_NEGATIVES = 14
SCORE_THRESHOLD = 0.5
HARD_NEGATIVE_WEIGHT = 3.0


class P5PreflightError(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise P5PreflightError("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise P5PreflightError(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P5PreflightError(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise P5PreflightError(f"unbounded/non-regular input: {relative}")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise P5PreflightError(f"input grew beyond bound: {relative}")
    return raw


def load_exact(relative: str, expected_sha: str, name: str):
    raw = read_project(relative, 4_000_000)
    if digest(raw) != expected_sha:
        raise P5PreflightError(f"pinned source mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise P5PreflightError(f"cannot load pinned source: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, 4_000_000)) != expected_sha:
        raise P5PreflightError(f"source changed during execution: {relative}")
    return module


def check_rx6600xt(runtime: dict[str, Any], python_executable: str, expected_python: str) -> None:
    if (runtime.get("device_name") != "AMD Radeon RX 6600 XT"
            or runtime.get("cpu_fallback") is not False
            or not str(runtime.get("device", "")).startswith("cuda:")
            or Path(python_executable).resolve() != Path(expected_python).resolve()):
        raise P5PreflightError("preflight requires project RX 6600 XT runtime with no CPU fallback")


def select_hard_negative_rows(rows: list[dict[str, Any]], scores: list[float], expected_count: int,
                              threshold: float = SCORE_THRESHOLD) -> list[dict[str, Any]]:
    if len(rows) != len(scores):
        raise P5PreflightError("score/row count mismatch")
    selected = []
    for row, score in zip(rows, scores):
        if row.get("label") not in (0, 1) or not isinstance(score, (int, float)):
            raise P5PreflightError("invalid training label or score")
        if not 0.0 <= float(score) <= 1.0:
            raise P5PreflightError("non-probability hard-negative score")
        if row["label"] == 0 and float(score) >= threshold:
            selected.append({"image_sha256": row["image_sha256"],
                "source_group_ids": sorted(row["source_group_ids"]), "score": float(score),
                "label": 0})
    if len(selected) != expected_count:
        raise P5PreflightError(f"expected {expected_count} training hard negatives, found {len(selected)}")
    return sorted(selected, key=lambda row: row["image_sha256"])


def validate_config() -> tuple[dict[str, Any], str]:
    raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(raw)
    self_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    expected = {"schema": "graphreader.p5-train-only-hard-negative-preflight.v1",
        "recipe_id": "ink-stroke-validity-p5-train-only-hard-negative-weighting",
        "preflight_runner_sha256": self_sha, "p4_runner_sha256": P4_RUNNER_SHA,
        "p4_config_sha256": P4_CONFIG_SHA, "p4_report_sha256": P4_REPORT_SHA,
        "p4_checkpoint_sha256": P4_CHECKPOINT_SHA, "p4_preflight_sha256": P4_PREFLIGHT_SHA,
        "training_rows": 465, "training_class_counts": {"positive": 89, "negative": 376},
        "hard_negative_selection": "frozen P4 checkpoint score >= 0.5 on label-0 P4 training rows only",
        "expected_hard_negative_rows": EXPECTED_HARD_NEGATIVES,
        "hard_negative_weight": HARD_NEGATIVE_WEIGHT,
        "unchanged_recipe": {"initialization": "frozen P3 checkpoint; fresh AdamW state",
            "architecture": "P4 RF31 classifier", "sampler": "P4 source-uniform round-robin sampler",
            "epochs": 20, "optimizer_steps": 460, "batch_size": 32,
            "optimizer": "AdamW", "learning_rate": 0.001, "weight_decay": 0.01,
            "torch_rng_seed": 27104152, "sampler_seed": 27104153},
        "changed_variable": "per-example BCE loss weight only; selected training hard negatives weighted 3x",
        "threshold_tuning": False, "probe_dev_sealed_private_inputs_read": False,
        "optimizer_created": False, "training_performed": False, "production_activation": False,
        "output_relative": OUTPUT_REL}
    if config != expected:
        raise P5PreflightError("P5 fixed preflight protocol/config identity mismatch")
    return config, digest(raw)


def run() -> dict[str, Any]:
    cfg, cfg_sha = validate_config()
    if (ROOT / OUTPUT_REL).exists() or (ROOT / OUTPUT_REL).is_symlink():
        raise P5PreflightError("P5 preflight output exists; refusing overwrite")
    p4 = load_exact(P4_RUNNER_REL, P4_RUNNER_SHA, "p5_pinned_p4_trainer")
    stack = p4.prepare_stack()
    p3, p2, _p3_config, stack_sources = stack
    p4_cfg, p4_cfg_sha, _raw, _validated = p4.validate_config(p2, stack_sources)
    if p4_cfg_sha != P4_CONFIG_SHA:
        raise P5PreflightError("P4 training config changed")
    dataset = p4.load_dataset(p4_cfg)
    if len(dataset["rows"]) != 465 or dataset["class_counts"] != {1: 89, 0: 376}:
        raise P5PreflightError("P4 training inventory changed")
    report_raw = read_project(P4_REPORT_REL, 1_000_000)
    checkpoint_raw = read_project(P4_CHECKPOINT_REL, 100_000_000)
    preflight_raw = read_project(P4_PREFLIGHT_REL, 1_000_000)
    if (digest(report_raw) != P4_REPORT_SHA or digest(checkpoint_raw) != P4_CHECKPOINT_SHA
            or digest(preflight_raw) != P4_PREFLIGHT_SHA):
        raise P5PreflightError("P4 training receipt/checkpoint/preflight hash changed")
    p4_report = json.loads(report_raw)
    if (p4_report.get("status") != "completed-train-only-p4-finetune"
            or p4_report.get("checkpoint", {}).get("sha256") != P4_CHECKPOINT_SHA
            or p4_report.get("data", {}).get("holdout_used_for_training_or_metrics") is not False):
        raise P5PreflightError("P4 checkpoint/report identity or training boundary changed")

    import numpy as np
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    check_rx6600xt(runtime, sys.executable, str(p2.helpers.GPU_PYTHON))
    device = torch.device(str(runtime["device"]))
    payload = torch.load(__import__("io").BytesIO(checkpoint_raw), map_location=device, weights_only=True)
    if (payload.get("model_identity") != "stroke-seeded-ink-validity-rf31-p4-wideproposal-finetune"
            or payload.get("p3_checkpoint_sha256") != p4_cfg["initialization"]["checkpoint_sha256"]
            or payload.get("p4_config_sha256") != P4_CONFIG_SHA
            or payload.get("train_row_bindings_sha256") != dataset["row_bindings_sha256"]):
        raise P5PreflightError("P4 checkpoint metadata does not bind exact training inputs")
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(payload["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise P5PreflightError("P4 inference model is not resident on RX 6600 XT")
    model.eval()
    rows = dataset["rows"]
    scores = []
    with torch.inference_mode():
        for start in range(0, len(rows), 32):
            batch_np = np.stack([row["pixels"] for row in rows[start:start + 32]]).astype(np.float32, copy=False)
            batch = torch.from_numpy(batch_np).unsqueeze(1).to(device)
            if batch.device != device:
                raise P5PreflightError("training inference batch left RX 6600 XT")
            batch_scores = torch.sigmoid(model(batch)).reshape(-1).detach().cpu().tolist()
            if len(batch_scores) != len(batch_np):
                raise P5PreflightError("P4 training inference returned an incomplete batch")
            scores.extend(float(score) for score in batch_scores)
    torch.cuda.synchronize(device)
    hard_negatives = select_hard_negative_rows(rows, scores, EXPECTED_HARD_NEGATIVES)
    if any(row["label"] != 0 for row in hard_negatives):
        raise P5PreflightError("positive row leaked into hard-negative selection")
    report = {"schema": "graphreader.p5-train-only-hard-negative-preflight-receipt.v1",
        "status": "p5-protocol-preflight-passed-no-optimizer",
        "p5_config_sha256": cfg_sha,
        "p5_preflight_runner_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
        "p4_runner_sha256": P4_RUNNER_SHA, "p4_config_sha256": P4_CONFIG_SHA,
        "p4_report_sha256": P4_REPORT_SHA, "p4_checkpoint_sha256": P4_CHECKPOINT_SHA,
        "p4_preflight_sha256": P4_PREFLIGHT_SHA,
        "p4_training_row_bindings_sha256": dataset["row_bindings_sha256"],
        "p4_training_input_manifests_sha256": dataset["input_manifest_hashes"],
        "training_rows": len(rows), "class_counts": {"positive": 89, "negative": 376},
        "training_source_group_count": len(dataset["groups"]),
        "hard_negative_rule": cfg["hard_negative_selection"],
        "hard_negative_count": len(hard_negatives), "hard_negative_weight": HARD_NEGATIVE_WEIGHT,
        "hard_negative_rows": hard_negatives,
        "inference": {"row_count": len(scores), "batch_count": (len(scores) + 31) // 32,
            "score_threshold": SCORE_THRESHOLD, "runtime": p2._versions(runtime),
            "device": str(device), "cpu_fallback": False, "optimizer_created": False,
            "training_or_backward_performed": False},
        "proposed_training_schedule": {"epochs": 20, "optimizer_steps": 460,
            "steps_per_epoch": 23, "batch_size": 32, "same_as_p4_except_loss_weight": True},
        "probe_dev_sealed_private_payloads_read": False,
        "holdout_or_probe_payload_bytes_read": False,
        "limitations": ["Hard negatives are selected only from the P4 training rows and frozen P4 scores.",
            "This is a recipe preflight only; no optimizer was created and no P5 checkpoint exists.",
            "The consumed synthetic development probe was not used in selection or admission."]}
    output = ROOT / OUTPUT_REL
    output.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    result = run()
    print(json.dumps({"status": result["status"], "hard_negative_count": result["hard_negative_count"],
                      "training_rows": result["training_rows"], "runtime": result["inference"]["runtime"]}, sort_keys=True))
