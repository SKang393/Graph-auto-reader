# SPDX-License-Identifier: Apache-2.0
"""P5 source-bound train-only hard-negative weighting experiment."""

from __future__ import annotations

from collections import Counter
from datetime import datetime, timezone
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import random
import stat
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[7]
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5_train.json"
P4_RUNNER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/train_classifier_p4.py"
P4_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4.json"
P4_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/report.json"
P4_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt/checkpoint.pt"
P4_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-preflight-v4.json"
P5_TEACHER_RECEIPT_REL = "artifacts/stroke-seeded-xtick-v1-p5-hard-negative-train-preflight-v1.json"
P5_TEACHER_RUNNER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5_train_only_hard_negative_preflight.py"
P5_TEACHER_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/p5.json"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p5-hard-negative-weighted-rx6600xt"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-p5-hard-negative-training-preflight-v1.json"
P4_RUNNER_SHA = "48489355ba5ea31005cbd223ad0fc541e9ba81759659dfeeec985af476151fde"
P4_CONFIG_SHA = "e137dc7e6fde4bec1a5ed33135f5f0269325236d48ebe2a6ce2c2607cb4670bd"
P4_REPORT_SHA = "f7ad0713ff10866c5ea5f093059aa8e3cad3a932fba6682c7d22b212319ffd35"
P4_CHECKPOINT_SHA = "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9"
P4_PREFLIGHT_SHA = "0f2d943793bd084658b42021d717ab4f90d0cb4526682ef6c22589153d6376db"
P5_TEACHER_RUNNER_SHA = "72dfd36e257ba6f5033a31e9166e55eec424eb2acc957ef56e64ec53655c1239"
P5_TEACHER_CONFIG_SHA = "5f2e4293e86ac483f513226153659fa99939c3f51082b8e6fd21e1829c582a62"
P5_TEACHER_RECEIPT_SHA = "cd5e51f4634a60a08e8fb2357c4723209346996cc1b42ab9e2ced8c077cc1f91"
P3_CHECKPOINT_SHA = "8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92"
EXPECTED_HARD_NEGATIVES = 14


class P5Error(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise P5Error("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise P5Error(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P5Error(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise P5Error(f"unbounded/non-regular input: {relative}")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise P5Error(f"input grew beyond bound: {relative}")
    return raw


def load_exact(relative: str, expected_sha: str, name: str):
    raw = read_project(relative, 4_000_000)
    if digest(raw) != expected_sha:
        raise P5Error(f"pinned source mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise P5Error(f"cannot load pinned source: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, 4_000_000)) != expected_sha:
        raise P5Error(f"source changed during import: {relative}")
    return module


def weighted_example_values(labels: list[int], hashes: list[str], hard_hashes: set[str], weight: float) -> list[float]:
    if len(labels) != len(hashes) or weight <= 1.0:
        raise P5Error("invalid weighted-loss inputs")
    label_by_hash: dict[str, set[int]] = {}
    for label, image_hash in zip(labels, hashes):
        if label not in (0, 1):
            raise P5Error("invalid binary training label")
        label_by_hash.setdefault(image_hash, set()).add(label)
    if any(len(values) > 1 for values in label_by_hash.values()):
        raise P5Error("semantic image-label conflict in weighted-loss rows")
    if not hard_hashes <= set(label_by_hash):
        raise P5Error("hard-negative hash missing from current training input")
    if any(label_by_hash[image_hash] != {0} for image_hash in hard_hashes):
        raise P5Error("hard-negative selection includes non-negative training image")
    return [float(weight) if image_hash in hard_hashes else 1.0 for image_hash in hashes]


def load_context() -> tuple[Any, Any, dict[str, Any], dict[str, Any], dict[str, Any], dict[str, Any], bytes]:
    config_raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(config_raw)
    runner_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    expected_config = {"schema": "graphreader.p5-hard-negative-trainer-config.v1",
        "recipe_id": "ink-stroke-validity-p5-train-only-hard-negative-weighting",
        "p5_runner_sha256": runner_sha, "p4_runner_sha256": P4_RUNNER_SHA,
        "p4_config_sha256": P4_CONFIG_SHA, "p4_report_sha256": P4_REPORT_SHA,
        "p4_checkpoint_sha256": P4_CHECKPOINT_SHA, "p4_preflight_sha256": P4_PREFLIGHT_SHA,
        "p5_teacher_runner_sha256": P5_TEACHER_RUNNER_SHA,
        "p5_teacher_config_sha256": P5_TEACHER_CONFIG_SHA,
        "p5_teacher_receipt_sha256": P5_TEACHER_RECEIPT_SHA,
        "epochs": 20, "steps_per_epoch": 23, "optimizer_steps": 460, "batch_size": 32,
        "class_draws_per_batch": 16, "per_class_draws_per_epoch": 368,
        "learning_rate": 0.001, "weight_decay": 0.01, "optimizer": "AdamW",
        "capturable": True, "torch_rng_seed": 27104152, "sampler_seed": 27104153,
        "hard_negative_score_threshold": 0.5, "hard_negative_weight": 3.0,
        "hard_negative_count": EXPECTED_HARD_NEGATIVES,
        "changed_variable": "per-example BCE weight on P4-teacher-scored label-0 P4 train rows only",
        "training_inputs": {"rows": 465, "positive": 89, "negative": 376,
            "source_groups": sorted(__import__("json").loads(read_project(P4_CONFIG_REL, 65_536))["training_inputs"]["train_source_groups"])},
        "probe_dev_sealed_private_payloads_read": False, "production_activation": False,
        "output_relative": OUTPUT_REL, "preflight_relative": PREFLIGHT_REL}
    if config != expected_config:
        raise P5Error("P5 trainer/config source or recipe identity mismatch")
    teacher_raw = read_project(P5_TEACHER_RECEIPT_REL, 1_000_000)
    if digest(teacher_raw) != P5_TEACHER_RECEIPT_SHA:
        raise P5Error("frozen P5 train-only hard-negative inference receipt changed")
    teacher_config_raw = read_project(P5_TEACHER_CONFIG_REL, 65_536)
    if digest(teacher_config_raw) != P5_TEACHER_CONFIG_SHA:
        raise P5Error("frozen P5 teacher config changed")
    teacher = json.loads(teacher_raw)
    if (teacher.get("status") != "p5-protocol-preflight-passed-no-optimizer"
            or teacher.get("hard_negative_count") != EXPECTED_HARD_NEGATIVES
            or teacher.get("inference", {}).get("optimizer_created") is not False
            or teacher.get("probe_dev_sealed_private_payloads_read") is not False):
        raise P5Error("teacher receipt is not the expected train-only inference preflight")
    p4 = load_exact(P4_RUNNER_REL, P4_RUNNER_SHA, "p5_fixed_p4_train_helpers")
    p3, p2, _p3_config, stack_hashes = p4.prepare_stack()
    p4_config, p4_config_sha, _raw, _overlap = p4.validate_config(p2, stack_hashes)
    if p4_config_sha != P4_CONFIG_SHA:
        raise P5Error("P4 dataset/training config changed")
    data = p4.load_dataset(p4_config)
    if len(data["rows"]) != 465 or data["class_counts"] != {1: 89, 0: 376}:
        raise P5Error("P5 training rows/classes changed")
    if teacher.get("p4_training_row_bindings_sha256") != data["row_bindings_sha256"]:
        raise P5Error("P5 teacher inference used different P4 train rows")
    return p4, p2, p4_config, data, teacher, stack_hashes, config_raw


def validate_hard_negatives(data: dict[str, Any], teacher: dict[str, Any]) -> list[dict[str, Any]]:
    rows = data["rows"]
    by_hash: dict[str, dict[str, Any]] = {}
    for row in rows:
        if row["image_sha256"] in by_hash and by_hash[row["image_sha256"]]["label"] != row["label"]:
            raise P5Error("current P5 train set contains a conflicting image label")
        by_hash[row["image_sha256"]] = row
    selected = teacher["hard_negative_rows"]
    if len(selected) != EXPECTED_HARD_NEGATIVES:
        raise P5Error("teacher hard-negative count changed")
    selected_hashes = {row["image_sha256"] for row in selected}
    if len(selected_hashes) != EXPECTED_HARD_NEGATIVES:
        raise P5Error("teacher hard-negative crop hashes are not unique")
    for item in selected:
        row = by_hash.get(item["image_sha256"])
        if (row is None or row["label"] != 0
                or sorted(row["source_group_ids"]) != item["source_group_ids"]
                or not 0.5 <= float(item["score"]) <= 1.0):
            raise P5Error("teacher hard-negative row does not bind to a label-0 P4 train image")
    return sorted(selected, key=lambda item: item["image_sha256"])


def assert_rx(runtime: dict[str, Any], p2: Any) -> Any:
    import torch
    if (runtime.get("device_name") != "AMD Radeon RX 6600 XT"
            or runtime.get("cpu_fallback") is not False
            or Path(sys.executable).resolve() != p2.helpers.GPU_PYTHON.resolve()):
        raise P5Error("P5 requires project RX 6600 XT runtime with CPU fallback disabled")
    device = torch.device(str(runtime["device"]))
    if not device.type == "cuda":
        raise P5Error("P5 device must be a CUDA/HIP device")
    return device


def p5_preflight(p4: Any, p2: Any, cfg: dict[str, Any], data: dict[str, Any], teacher: dict[str, Any],
                 stack_hashes: dict[str, str], config_raw: bytes) -> dict[str, Any]:
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    device = assert_rx(runtime, p2)
    p4.audit_capturable_adamw_runtime(torch)
    p4.seed_torch_rng(torch, int(cfg["torch_rng_seed"]))
    p3_checkpoint_raw = p4.read_project(p4.P3_CHECKPOINT_REL, 100_000_000)
    if digest(p3_checkpoint_raw) != P3_CHECKPOINT_SHA:
        raise P5Error("P5 frozen P3 initialization checkpoint changed")
    checkpoint = torch.load(__import__("io").BytesIO(p3_checkpoint_raw), map_location=device, weights_only=False)
    if (checkpoint.get("model_identity") != cfg["initialization"]["model_identity"]
            or checkpoint.get("p3_config_sha256") != p4.P3_CONFIG_SHA
            or checkpoint.get("p3_runner_sha256") != "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d"):
        raise P5Error("P3 initialization checkpoint metadata changed")
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(cfg["learning_rate"]),
        weight_decay=float(cfg["weight_decay"]), capturable=bool(cfg["capturable"]))
    residency = p4.assert_optimizer_gpu_residency(torch, model, optimizer, device)
    x = torch.from_numpy(np.stack([row["pixels"] for row in data["rows"][:2]]).astype(np.float32)).unsqueeze(1).to(device)
    p2.p1._assert_gpu_binding(runtime, model, [x], device)
    with torch.inference_mode():
        fmap = tuple(model.features(x).shape[-2:])
        logits = model(x)
    if fmap != (12, 32) or tuple(logits.shape) != (2,) or not torch.isfinite(logits).all():
        raise P5Error("P5 model input/output shape preflight failed")
    torch.cuda.synchronize(device)
    hard_negatives = validate_hard_negatives(data, teacher)
    return {"schema": "graphreader.p5-hard-negative-training-preflight.v1",
        "status": "preflight-passed-no-optimizer-step",
        "p5_trainer_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
        "p5_training_config_sha256": digest(config_raw),
        "p5_teacher_receipt_sha256": P5_TEACHER_RECEIPT_SHA,
        "p5_teacher_runner_sha256": P5_TEACHER_RUNNER_SHA,
        "p4_runner_sha256": P4_RUNNER_SHA, "p4_config_sha256": P4_CONFIG_SHA,
        "p4_checkpoint_sha256": P4_CHECKPOINT_SHA, "p4_report_sha256": P4_REPORT_SHA,
        "p4_preflight_sha256": P4_PREFLIGHT_SHA,
        "p4_training_row_bindings_sha256": data["row_bindings_sha256"],
        "p4_input_manifests_sha256": data["input_manifest_hashes"],
        "pinned_dependency_hashes": {"p4_runner": P4_RUNNER_SHA, **stack_hashes},
        "data": {"row_count": len(data["rows"]), "unique_hash_count": len({r["image_sha256"] for r in data["rows"]}),
            "class_counts": {"positive": 89, "negative": 376}, "source_groups": sorted(data["groups"]),
            "hard_negative_count": len(hard_negatives),
            "hard_negative_hashes_sha256": digest(json.dumps(sorted(r["image_sha256"] for r in hard_negatives), separators=(",", ":")).encode()),
            "hard_negative_source_draw_counts": dict(sorted(Counter(g for row in hard_negatives for g in row["source_group_ids"]).items()))},
        "optimizer_preflight": {"optimizer_created": True, "optimizer_steps": 0,
            "capturable": True, "gpu_residency_after_init": residency,
            "gpu_residency_every_parameter_and_state_on_device": True,
            "runtime": p2._versions(runtime), "device": str(device), "cpu_fallback": False,
            "forward_shape": {"feature_map": list(fmap), "logits": list(logits.shape)},
            "backward_performed": False},
        "fixed_schedule": {"epochs": 20, "steps_per_epoch": 23, "optimizer_steps": 460,
            "batch_size": 32, "learning_rate": float(cfg["learning_rate"]),
            "weight_decay": float(cfg["weight_decay"]), "torch_rng_seed": cfg["torch_rng_seed"],
            "sampler_seed": cfg["sampler_seed"], "loss_weight": 3.0},
        "holdout_probe_dev_sealed_private_payloads_read": False,
        "production_activation": False}


def run_training(p4: Any, p2: Any, cfg: dict[str, Any], data: dict[str, Any], teacher: dict[str, Any],
                 hard_negatives: list[dict[str, Any]], config_sha: str, output: Path) -> dict[str, Any]:
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    device = assert_rx(runtime, p2)
    p4.audit_capturable_adamw_runtime(torch)
    p4.seed_torch_rng(torch, int(cfg["torch_rng_seed"]))
    p3_checkpoint_raw = p4.read_project(p4.P3_CHECKPOINT_REL, 100_000_000)
    checkpoint = torch.load(__import__("io").BytesIO(p3_checkpoint_raw), map_location=device, weights_only=False)
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(cfg["learning_rate"]),
        weight_decay=float(cfg["weight_decay"]), capturable=bool(cfg["capturable"]))
    residency_init = p4.assert_optimizer_gpu_residency(torch, model, optimizer, device)

    rows = data["rows"]
    labels = [int(row["label"]) for row in rows]
    hashes = [str(row["image_sha256"]) for row in rows]
    hard_hashes = {row["image_sha256"] for row in hard_negatives}
    weight_values = weighted_example_values(labels, hashes, hard_hashes, 3.0)
    pixels = torch.from_numpy(np.stack([row["pixels"] for row in rows]).astype(np.float32)).unsqueeze(1)
    train_groups = set(cfg["training_inputs"]["train_source_groups"])
    pos_indices = [i for i, label in enumerate(labels) if label == 1]
    neg_indices = [i for i, label in enumerate(labels) if label == 0]
    pos_groups, _ = p2._owner_group_indices([{"source_group_ids": rows[i]["source_group_ids"]} for i in pos_indices], train_groups)
    neg_groups, _ = p2._owner_group_indices([{"source_group_ids": rows[i]["source_group_ids"]} for i in neg_indices], train_groups)
    pos_state = p2.make_group_state(pos_groups, int(cfg["sampler_seed"]))
    neg_state = p2.make_group_state(neg_groups, int(cfg["sampler_seed"]) + 1)
    pos_global = {local: index for local, index in enumerate(pos_indices)}
    neg_global = {local: index for local, index in enumerate(neg_indices)}
    steps = 0
    loss_first = loss_last = None
    draws = {"positive": Counter(), "negative": Counter()}
    residency_last = residency_init
    for epoch in range(int(cfg["epochs"])):
        po, pd = p2.source_uniform_order(pos_groups, int(cfg["per_class_draws_per_epoch"]), epoch, pos_state)
        no, nd = p2.source_uniform_order(neg_groups, int(cfg["per_class_draws_per_epoch"]), epoch, neg_state)
        if max(pd.values()) - min(pd.values()) > 1 or max(nd.values()) - min(nd.values()) > 1:
            raise P5Error("P5 source-uniform sampler draw imbalance")
        draws["positive"].update(pd)
        draws["negative"].update(nd)
        half = int(cfg["class_draws_per_batch"])
        for batch_index in range(int(cfg["steps_per_epoch"])):
            start = batch_index * half
            chosen = [pos_global[i] for i in po[start:start + half]] + [neg_global[i] for i in no[start:start + half]]
            random.Random(int(cfg["sampler_seed"]) + epoch * 10007 + batch_index).shuffle(chosen)
            x = pixels[chosen].to(device)
            y = torch.tensor([labels[i] for i in chosen], dtype=torch.float32, device=device)
            weights = torch.tensor([weight_values[i] for i in chosen], dtype=torch.float32, device=device)
            p2.p1._assert_gpu_binding(runtime, model, [x, y, weights], device)
            optimizer.zero_grad(set_to_none=True)
            per_item = torch.nn.functional.binary_cross_entropy_with_logits(model(x), y, reduction="none")
            loss = (per_item * weights).mean()
            if not torch.isfinite(loss):
                raise P5Error("non-finite P5 weighted BCE loss")
            loss.backward()
            optimizer.step()
            residency_last = p4.assert_optimizer_gpu_residency(torch, model, optimizer, device)
            loss_value = float(loss.detach().cpu())
            loss_first = loss_value if loss_first is None else loss_first
            loss_last = loss_value
            steps += 1
    if steps != int(cfg["optimizer_steps"]):
        raise P5Error(f"P5 optimizer step count mismatch: {steps}")
    metrics, by_source, scores = p2._evaluate_train(model, rows, device, int(cfg["batch_size"]))
    model_identity = "stroke-seeded-ink-validity-rf31-p5-train-hard-negative-weighted"
    checkpoint_path = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_identity": model_identity,
        "recipe_id": cfg["recipe_id"], "architecture": cfg["architecture"],
        "initialization_model_identity": checkpoint.get("model_identity"),
        "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p4_teacher_checkpoint_sha256": P4_CHECKPOINT_SHA,
        "p5_config_sha256": config_sha, "p5_teacher_receipt_sha256": P5_TEACHER_RECEIPT_SHA,
        "p4_train_row_bindings_sha256": data["row_bindings_sha256"],
        "hard_negative_hashes_sha256": digest(json.dumps(sorted(hard_hashes), separators=(",", ":")).encode()),
        "source_groups": sorted(train_groups), "runtime": runtime}, checkpoint_path)
    return {"schema": "graphreader.stroke-seeded-p5-hard-negative-train-report.v1",
        "status": "completed-train-only-p5-hard-negative-weighting",
        "acceptance_status": "train-only exploratory; not acceptance", "production_approval": False,
        "p5_runner_sha256": digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)),
        "p5_config_sha256": config_sha, "p5_teacher_receipt_sha256": P5_TEACHER_RECEIPT_SHA,
        "p4_teacher_checkpoint_sha256": P4_CHECKPOINT_SHA, "p3_initialization_checkpoint_sha256": P3_CHECKPOINT_SHA,
        "p4_input_manifest_sha256s": data["input_manifest_hashes"],
        "training_row_bindings_sha256": data["row_bindings_sha256"],
        "data": {"rows": len(rows), "positive": sum(labels), "negative": len(labels)-sum(labels),
            "source_groups": sorted(train_groups), "hard_negative_count": len(hard_hashes),
            "hard_negative_weight": 3.0, "hard_negative_hashes_sha256": digest(json.dumps(sorted(hard_hashes), separators=(",", ":")).encode()),
            "probe_dev_sealed_private_holdout_payloads_read": False},
        "recipe": {"epochs": int(cfg["epochs"]), "steps_per_epoch": int(cfg["steps_per_epoch"]),
            "optimizer_steps": steps, "batch_size": int(cfg["batch_size"]), "optimizer": "AdamW",
            "capturable": True, "learning_rate": float(cfg["learning_rate"]),
            "weight_decay": float(cfg["weight_decay"]), "loss": "per-example BCE-with-logits; hard-negative rows weighted 3x",
            "threshold_for_hard_negative_selection": 0.5, "torch_rng_seed": int(cfg["torch_rng_seed"]),
            "sampler_seed": int(cfg["sampler_seed"]), "gpu_residency_after_optimizer_init": residency_init,
            "gpu_residency_after_last_step": residency_last,
            "source_draws": {key: dict(sorted(value.items())) for key, value in draws.items()}},
        "training": {"loss_first": loss_first, "loss_last": loss_last,
            "runtime": p2._versions(runtime), "cpu_fallback": False},
        "train_only_metrics": {"threshold": 0.5, "overall": metrics, "by_source_group": by_source,
            "score_count": len(scores)},
        "checkpoint": {"path": f"{OUTPUT_REL}/checkpoint.pt", "sha256": digest(checkpoint_path.read_bytes()),
            "model_identity": model_identity},
        "limitations": ["Train-only synthetic experiment; no probe generalization claim.",
            "The P5 teacher scores select examples from the same training dataset only.",
            "This experiment is not promoted or connected to production."]}


def main() -> None:
    config_raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(config_raw)
    p4, p2, cfg, data, teacher, stack_hashes, config_raw = load_context()
    selected = validate_hard_negatives(data, teacher)
    preflight = p5_preflight(p4, p2, cfg, data, teacher, stack_hashes, config_raw)
    preflight_sha = digest(json.dumps(preflight, sort_keys=True, indent=2).encode() + b"\n")
    if "--train" not in sys.argv:
        if (ROOT / PREFLIGHT_REL).exists() or (ROOT / PREFLIGHT_REL).is_symlink():
            raise P5Error("P5 training preflight receipt exists; refusing overwrite")
        (ROOT / PREFLIGHT_REL).write_bytes(json.dumps(preflight, sort_keys=True, indent=2).encode() + b"\n")
        print(json.dumps({"status": preflight["status"], "hard_negative_count": len(selected),
            "optimizer_residency": preflight["optimizer_preflight"], "preflight": PREFLIGHT_REL}, sort_keys=True))
        return
    saved_raw = read_project(PREFLIGHT_REL, 1_000_000)
    if json.loads(saved_raw) != preflight or preflight.get("p5_training_config_sha256") != digest(config_raw):
        raise P5Error("P5 training preflight receipt does not match fresh source/data/GPU validation")
    out = ROOT / OUTPUT_REL
    if out.exists() or out.is_symlink():
        raise P5Error("P5 output exists; refusing overwrite")
    p2.helpers._checked_chain(ROOT, out.parent, "P5 output parent")
    out.mkdir(parents=False, exist_ok=False)
    started = {"status": "started", "recipe_id": config["recipe_id"],
        "p5_runner_sha256": config["p5_runner_sha256"], "p5_config_sha256": digest(config_raw),
        "p5_preflight_sha256": preflight_sha, "started_utc": datetime.now(timezone.utc).isoformat()}
    (out / "started.json").write_text(json.dumps(started, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    try:
        result = run_training(p4, p2, cfg, data, teacher, selected, digest(config_raw), out)
        result["pinned_stack_hashes"] = stack_hashes
        (out / "report.json").write_text(json.dumps(result, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except BaseException as error:
        (out / "failure.json").write_text(json.dumps({"status": "failed",
            "error_type": type(error).__name__, "error": str(error),
            "p5_runner_sha256": config["p5_runner_sha256"], "p5_config_sha256": digest(config_raw),
            "failed_utc": datetime.now(timezone.utc).isoformat()}, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
