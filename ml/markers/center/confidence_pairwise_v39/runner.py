# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Canonical-ledger-gated, RX 6600 XT-only synthetic confidence trainer.

The entrypoint is intentionally not runnable until the canonical marker
training ledger registers P1 with this exact config and source-bundle digest.
It trains on V37's authenticated synthetic rows only. Development evaluation
is a separate later stage and is not read here.
"""
from __future__ import annotations

import argparse
import ctypes
from contextlib import nullcontext
import json
import math
import os
from pathlib import Path
import random
import stat
import sys
import time
import traceback

import numpy as np
import torch

from ml.markers.gate_seal import (
    sha256_file,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.markers.training_budget import (
    acquire_training_candidate,
    void_candidate,
)
from ..confidence_reflection_v37.cache import load_training
from ..confidence_spatial_v35.model import SpatialCenterNet, TRAINABLE_PARAMETERS
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from .objective import synthetic_confidence_loss

TASK = "marker-center"
REVISION = "marker-center-confidence-pairwise-v39"
CANDIDATE_ID = "P1"
CONFIG_PATH = Path("ml/markers/center/confidence_pairwise_v39/p1.json")
PROTOCOL_PATH = Path("ml/markers/center/confidence_pairwise_v39/protocol.json")
EXPECTED_PROTOCOL_SHA256 = "85a2c3aca5278fb0397b6ebcb46f23c6ce90d33981dd0ce53b6745d0e332dbcb"
EXPECTED_REQUIREMENTS_SHA256 = "efc2c06c5be88c463e4fe567964b2d87c07a9c61f5118f9738654cf4be74d4aa"
EXPECTED_LAUNCHER_SHA256 = "37fbdcf0dceecc278973c9b28bf85556319a67587b3de9127a38c98e745908f5"
FEATURE_REPORT_SHA256 = "284ebfa305b7b827c59383711122b1668398460ddde2489007252335ee55f500"
PARENT_FEATURE_REPORT_SHA256 = "3a98504c7b31f16af8f4b127ab438cba3fe7918d51c9417021221f96bb6ad7cc"
INITIALIZER_SHA256 = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
EVIDENCE_POLICY_SHA256 = "4dc18136c284b0b1805d3a3b22a9197ad06e6a41f4e43b4e1d4d9245b97e0aed"
ACCEPTANCE_BARS_SHA256 = "aab9f2ab60cf166828f0928b8496f537341870fd457d0408952e22549fc53a56"
MIN_AVAILABLE_MEMORY_BYTES = 9 * 1024**3
DATA_ROOT = Path("artifacts/goal22-worktrees/resume-20260908")
FEATURE_REPORT_PATH = Path("artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json")
INITIALIZER_PATH = Path("artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/marker-center-component-diversity-v27-p1.pt")
TRAINING_ROWS = {
    "component:identity": 35838, "component:horizontal": 35838,
    "component:vertical": 35838, "component:both": 35838,
    "family:identity": 9053, "family:horizontal": 9053,
    "family:vertical": 9053, "family:both": 9053,
    "coverage:identity": 42747, "coverage:horizontal": 42747,
    "coverage:vertical": 42747, "coverage:both": 42747,
    "crowded:identity": 182614, "crowded:horizontal": 182614,
    "crowded:vertical": 182614, "crowded:both": 182614,
    "crowded_mined_negative:identity": 6366, "crowded_mined_negative:horizontal": 6366,
    "crowded_mined_negative:vertical": 6366, "crowded_mined_negative:both": 6366,
}
RECIPE = {
    "adamw_amsgrad": False,
    "adamw_betas": [0.9, 0.999],
    "adamw_capturable": True,
    "adamw_differentiable": False,
    "adamw_eps": 1e-8,
    "adamw_foreach": None,
    "adamw_fused": None,
    "adamw_maximize": False,
    "batch_size": 128,
    "checkpoint_selection": "fixed_final_epoch_no_dev_selection",
    "confidence_mapping": "adapted_copy_768x64_silu_projection",
    "confidence_objective": "synthetic_bce_plus_within_group_mean_softplus_pairwise_margin_1.0",
    "confidence_threshold": 0.1,
    "epochs": 8,
    "frozen_base_confidence": True,
    "frozen_geometry": True,
    "frozen_normalization_buffers": True,
    "frozen_spatial_features": True,
    "gradient_norm_limit": 5.0,
    "learning_rate": 0.0003,
    "seed": 20261002,
    "trainable_parameters": TRAINABLE_PARAMETERS,
    "weight_decay": 0.0001,
    "pairwise_margin_logits": 1.0,
    "pairwise_scope": "authenticated_feature_report_scope_transform_group",
}
EXPECTED_STEPS = RECIPE["epochs"] * math.ceil(sum(TRAINING_ROWS.values()) / RECIPE["batch_size"])

# Static transitive source closure used by this entrypoint and the reused V37
# cache. The preregistration must use this exact ordered tuple.
RUNNER_SOURCES = tuple(Path(value) for value in (
    "ml/markers/gate_seal.py",
    "ml/markers/training_budget.py",
    "ml/policy/evidence_policy.py",
    "ml/markers/center/__init__.py",
    "ml/markers/center/confidence_pairwise_v39/__init__.py",
    "ml/markers/center/confidence_pairwise_v39/objective.py",
    "ml/markers/center/confidence_pairwise_v39/evaluation.py",
    "ml/markers/center/confidence_pairwise_v39/protocol.json",
    "ml/markers/center/confidence_pairwise_v39/runner.py",
    "tools/gpu-training/Run-GpuPython.ps1",
    "tools/gpu-training/requirements.lock.txt",
    "tools/gpu-training/gpu-package-provenance.json",
    "ml/markers/center/confidence_reflection_v37/__init__.py",
    "ml/markers/center/confidence_reflection_v37/augmentation.py",
    "ml/markers/center/confidence_reflection_v37/cache.py",
    "ml/markers/center/confidence_spatial_v35/__init__.py",
    "ml/markers/center/confidence_spatial_v35/cache.py",
    "ml/markers/center/confidence_spatial_v35/model.py",
    "ml/markers/center/scale_classifier_v16/__init__.py",
    "ml/markers/center/scale_classifier_v16/model.py",
))


class _NoopBudget:
    """The V37 loader expects a work-block API; loading itself is CPU-only."""

    def work_block(self):
        return nullcontext()


def _read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def validate_config(repo: Path, config: dict) -> None:
    if (config.get("schema_version"), config.get("task"), config.get("revision"), config.get("candidate_id")) != (
        1, TASK, REVISION, CANDIDATE_ID
    ):
        raise ValueError("Candidate identity or config schema changed")
    if config.get("recipe") != RECIPE or config.get("training_group_rows") != TRAINING_ROWS:
        raise ValueError("Fixed recipe or training group population changed")
    if config.get("optimizer_steps_expected") != EXPECTED_STEPS:
        raise ValueError("Fixed optimizer-step count changed")
    if config.get("minimum_available_memory_bytes") != MIN_AVAILABLE_MEMORY_BYTES:
        raise ValueError("Pre-admission memory guard changed")
    if config.get("feature_report_path") != FEATURE_REPORT_PATH.as_posix() or config.get("feature_report_sha256") != FEATURE_REPORT_SHA256:
        raise ValueError("Train feature report binding changed")
    if config.get("parent_feature_report_sha256") != PARENT_FEATURE_REPORT_SHA256:
        raise ValueError("Parent feature report binding changed")
    if config.get("data_root") != DATA_ROOT.as_posix():
        raise ValueError("Train artifact root changed")
    if config.get("source_checkpoint_path") != INITIALIZER_PATH.as_posix() or config.get("source_checkpoint_sha256") != INITIALIZER_SHA256:
        raise ValueError("V27 initializer binding changed")
    if config.get("development_file") != {
        "path": "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt",
        "sha256": "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41",
    }:
        raise ValueError("Fixed development evaluator input changed")
    if config.get("training_output_path") != "artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1":
        raise ValueError("Fixed V39 checkpoint output path changed")
    if config.get("development_evaluation_output_path") != "artifacts/goal22-runs/marker-center-confidence-pairwise-v39-P1-dev-eval":
        raise ValueError("Fixed V39 evaluation output path changed")
    if config.get("evidence_policy_sha256") != EVIDENCE_POLICY_SHA256:
        raise ValueError("Shared evidence policy binding changed")
    if config.get("acceptance_bars_sha256") != ACCEPTANCE_BARS_SHA256:
        raise ValueError("Shared acceptance bars binding changed")
    if config.get("protocol_path") != PROTOCOL_PATH.as_posix() or config.get("protocol_sha256") != EXPECTED_PROTOCOL_SHA256:
        raise ValueError("Fixed protocol binding changed")
    if config.get("runner_source_paths") != [path.as_posix() for path in RUNNER_SOURCES]:
        raise ValueError("Executed source closure changed")
    if config.get("environment") != {
        "python": "3.13.7",
        "torch": "2.13.0+rocm10.1.0",
        "numpy": "2.3.5",
        "requirements_lock_sha256": EXPECTED_REQUIREMENTS_SHA256,
        "launcher_sha256": EXPECTED_LAUNCHER_SHA256,
    }:
        raise ValueError("Pinned GPU execution environment changed")
    if config.get("expected_runner_source_bundle_sha256") != source_bundle_sha256(repo, RUNNER_SOURCES):
        raise ValueError("Runner source bundle differs from its fixed configuration")
    if (config.get("private_reads") != 0 or config.get("sealed_runs_authorized") != 0 or
            config.get("production_approval") is not False or config.get("release_eligible") is not False):
        raise ValueError("Training authorization or production scope changed")


def available_physical_memory_bytes() -> int:
    if os.name != "nt":
        raise RuntimeError("The preregistered training memory guard requires Windows GlobalMemoryStatusEx")

    class MemoryStatusEx(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    status = MemoryStatusEx()
    status.dwLength = ctypes.sizeof(status)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    query = kernel32.GlobalMemoryStatusEx
    query.argtypes = [ctypes.POINTER(MemoryStatusEx)]
    query.restype = ctypes.c_int
    if not query(ctypes.byref(status)):
        raise OSError(ctypes.get_last_error(), "GlobalMemoryStatusEx failed")
    return int(status.ullAvailPhys)


def require_memory_headroom(available_bytes: int | None = None) -> dict:
    available = available_physical_memory_bytes() if available_bytes is None else available_bytes
    if isinstance(available, bool) or not isinstance(available, int) or available < MIN_AVAILABLE_MEMORY_BYTES:
        raise RuntimeError(
            f"V39 feature loading requires at least {MIN_AVAILABLE_MEMORY_BYTES} available physical bytes; "
            f"observed {available!r}. No canonical training admission was attempted."
        )
    return {"available_physical_bytes": available, "minimum_required_bytes": MIN_AVAILABLE_MEMORY_BYTES}


def acquire_after_pre_admission_checks(repo: Path):
    memory = require_memory_headroom()
    preflight_device = require_rx6600xt(repo)
    authorization = acquire_training_candidate(
        repo,
        task=TASK,
        revision=REVISION,
        candidate_id=CANDIDATE_ID,
        config_path=CONFIG_PATH,
        runner_source_paths=RUNNER_SOURCES,
    )
    return authorization, memory, preflight_device


def require_rx6600xt(repo: Path) -> torch.device:
    """Reject unsupported hardware before creating any optimizer."""
    expected_interpreter = (repo / "tools/gpu-training/venv/Scripts/python.exe").resolve(strict=True)
    try:
        actual_interpreter = Path(sys.executable).resolve(strict=True)
    except OSError as error:
        raise RuntimeError("Training must run through the project GPU venv; CPU/other-interpreter fallback is forbidden") from error
    if actual_interpreter != expected_interpreter:
        raise RuntimeError("Training must run through the project GPU venv; CPU/other-interpreter fallback is forbidden")
    if sys.version_info[:3] != (3, 13, 7) or torch.__version__ != "2.13.0+rocm10.1.0" or np.__version__ != "2.3.5":
        raise RuntimeError("Project-pinned Python, PyTorch HIP, or NumPy runtime differs")
    if not torch.cuda.is_available() or torch.version.hip is None:
        raise RuntimeError("RX 6600 XT PyTorch HIP device is unavailable; CPU fallback is forbidden")
    torch.cuda.set_device(0)
    props = torch.cuda.get_device_properties(0)
    name = str(getattr(props, "name", ""))
    architecture = str(getattr(props, "gcnArchName", ""))
    if name != "AMD Radeon RX 6600 XT" or not architecture.startswith("gfx1032"):
        raise RuntimeError(f"Expected RX 6600 XT/gfx1032 on cuda:0, found {name!r}/{architecture!r}")
    return torch.device("cuda:0")


def assert_optimizer_on_device(optimizer: torch.optim.Optimizer, device: torch.device) -> None:
    for group in optimizer.param_groups:
        if group.get("capturable") is not True:
            raise RuntimeError("AdamW must keep its step state on the selected GPU")
        for parameter in group["params"]:
            if parameter.device != device:
                raise RuntimeError("Optimizer parameter is outside the selected GPU")
            if parameter.grad is not None and parameter.grad.device != device:
                raise RuntimeError("Optimizer gradient is outside the selected GPU")
    for state in optimizer.state.values():
        for value in state.values():
            if isinstance(value, torch.Tensor) and value.device != device:
                raise RuntimeError("Optimizer state is outside the selected GPU")


def create_optimizer(head):
    """Match V37 AdamW defaults, except capturable GPU-resident step state."""
    return torch.optim.AdamW(
        head.parameters(),
        lr=RECIPE["learning_rate"],
        betas=tuple(RECIPE["adamw_betas"]),
        eps=RECIPE["adamw_eps"],
        weight_decay=RECIPE["weight_decay"],
        amsgrad=RECIPE["adamw_amsgrad"],
        maximize=RECIPE["adamw_maximize"],
        foreach=RECIPE["adamw_foreach"],
        capturable=RECIPE["adamw_capturable"],
        differentiable=RECIPE["adamw_differentiable"],
        fused=RECIPE["adamw_fused"],
    )


def make_group_ids(feature_report: dict) -> torch.Tensor:
    groups = feature_report.get("groups")
    if not isinstance(groups, list) or [item.get("scope") for item in groups] != list(TRAINING_ROWS):
        raise ValueError("Feature report group order differs from the fixed synthetic train protocol")
    counts: list[int] = []
    for item, (scope, expected_rows) in zip(groups, TRAINING_ROWS.items(), strict=True):
        if item.get("rows") != expected_rows or item.get("scope") != scope:
            raise ValueError("Feature report group identity or row count changed")
        counts.append(expected_rows)
    ids = torch.repeat_interleave(torch.arange(len(counts), dtype=torch.int64), torch.tensor(counts, dtype=torch.int64))
    if ids.numel() != sum(TRAINING_ROWS.values()) or torch.unique(ids).numel() != len(counts):
        raise ValueError("Synthetic report group ID construction failed")
    return ids


def _reject_reparse_ancestors(path: Path, root: Path) -> None:
    root = root.resolve(strict=True)
    candidate = Path(os.path.abspath(path))
    try:
        relative = candidate.relative_to(root)
    except ValueError as error:
        raise ValueError("Output must remain under the project training-runs directory") from error
    current = root
    paths = [root]
    for part in relative.parts:
        current = current / part
        paths.append(current)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for item in paths:
        if item.exists() and getattr(item.stat(), "st_file_attributes", 0) & reparse_flag:
            raise ValueError(f"Reparse-point path is forbidden: {item}")
    resolved = candidate.resolve(strict=False)
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise ValueError("Resolved output escaped the project training-runs directory") from error


def validate_output_path(repo: Path, output: Path) -> Path:
    allowed_root = repo / "artifacts" / "goal22-runs"
    candidate = output if output.is_absolute() else repo / output
    _reject_reparse_ancestors(candidate, allowed_root)
    if candidate.exists():
        raise FileExistsError(f"Training output must be fresh: {candidate}")
    return candidate


def _write_json(path: Path, payload: dict) -> None:
    data = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def _verify_data_path(root: Path, item: dict) -> None:
    root = Path(os.path.abspath(root))
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for ancestor in reversed((root, *root.parents)):
        if ancestor.exists() and getattr(ancestor.stat(), "st_file_attributes", 0) & reparse_flag:
            raise ValueError("Train data root has a reparse-point ancestor")
    resolved_root = root.resolve(strict=True)
    relative = item.get("path")
    digest = item.get("sha256")
    if not isinstance(relative, str) or not isinstance(digest, str) or len(digest) != 64:
        raise ValueError("Train evidence reference is malformed")
    relative_path = Path(relative)
    if relative_path.is_absolute() or ".." in relative_path.parts:
        raise ValueError("Train evidence reference escaped its fixed data root")
    candidate = root / relative_path
    resolved = candidate.resolve(strict=True)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as error:
        raise ValueError("Train evidence reference resolved outside its fixed data root") from error
    current = root
    for part in relative_path.parts:
        current = current / part
        if current.exists() and getattr(current.stat(), "st_file_attributes", 0) & reparse_flag:
            raise ValueError("Reparse-point train evidence is forbidden")
    if not resolved.is_file() or sha256_file(resolved) != digest:
        raise ValueError("Train evidence bytes do not match their report hash")


def _verify_report_references(data_root: Path, report: dict) -> None:
    items = [report["model"], report["parent_feature_report"], *report["evidence"]]
    for group in report["groups"]:
        items.extend(group[name] for name in ("tensors", "features", "raw", "labels", "hard_negative"))
    seen: set[str] = set()
    for item in items:
        relative = item["path"].replace("\\", "/")
        if relative not in seen:
            _verify_data_path(data_root, item)
            seen.add(relative)


def _train(head, optimizer, training, group_ids, device: torch.device) -> dict:
    features, labels, _hard_negative = training
    if features.shape != (sum(TRAINING_ROWS.values()), 768) or labels.shape != (features.shape[0],):
        raise ValueError("Loaded synthetic feature and label shapes differ from the fixed report")
    if group_ids.shape != labels.shape:
        raise ValueError("Synthetic labels and report group IDs have different row counts")
    history: list[dict] = []
    steps = 0
    for epoch in range(RECIPE["epochs"]):
        order = torch.randperm(len(labels), generator=torch.Generator().manual_seed(RECIPE["seed"] + epoch))
        losses: list[float] = []
        for start in range(0, len(order), RECIPE["batch_size"]):
            indices = order[start:start + RECIPE["batch_size"]]
            batch_features = features.index_select(0, indices).to(device, non_blocking=False)
            batch_labels = labels.index_select(0, indices).to(device, non_blocking=False)
            batch_groups = group_ids.index_select(0, indices).to(device, non_blocking=False)
            if any(value.device != device for value in (batch_features, batch_labels, batch_groups)):
                raise RuntimeError("Training batch fell back from the selected GPU")
            logits = head(batch_features)
            if logits.device != device:
                raise RuntimeError("Confidence logits fell back from the selected GPU")
            loss = synthetic_confidence_loss(logits, batch_labels, batch_groups)
            if loss.device != device:
                raise RuntimeError("Synthetic objective fell back from the selected GPU")
            if not bool(torch.isfinite(loss).item()):
                raise RuntimeError("Non-finite synthetic pairwise training loss")
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            assert_optimizer_on_device(optimizer, device)
            grad_norm = torch.nn.utils.clip_grad_norm_(head.parameters(), RECIPE["gradient_norm_limit"])
            if not bool(torch.isfinite(grad_norm).item()):
                raise RuntimeError("Non-finite confidence-head gradient norm")
            optimizer.step()
            assert_optimizer_on_device(optimizer, device)
            steps += 1
            losses.append(float(loss.detach().item()))
        history.append({"epoch": epoch + 1, "optimizer_steps": steps, "mean_loss": float(np.mean(losses))})
    if steps != EXPECTED_STEPS:
        raise RuntimeError(f"Optimizer step count mismatch: {steps} != {EXPECTED_STEPS}")
    return {"completed_epochs": RECIPE["epochs"], "optimizer_steps": steps, "history": history}


def run(repo: Path, output: Path) -> dict:
    repo = repo.resolve(strict=True)
    output = validate_output_path(repo, output)
    config = _read_json(repo / CONFIG_PATH)
    validate_config(repo, config)
    if sha256_file(repo / PROTOCOL_PATH) != EXPECTED_PROTOCOL_SHA256:
        raise ValueError("Protocol bytes differ from their fixed identity")

    # Canonical admission is intentionally before any dataset, model, optimizer,
    # or output access. A missing ledger row rejects this run here.
    authorization, memory_preflight, preflight_device = acquire_after_pre_admission_checks(repo)
    started = time.perf_counter()
    try:
        device = require_rx6600xt(repo)
        if device != preflight_device:
            raise RuntimeError("RX 6600 XT device changed after pre-admission validation")
        output.mkdir(parents=True, exist_ok=False)
        torch.manual_seed(RECIPE["seed"])
        torch.cuda.manual_seed_all(RECIPE["seed"])
        random.seed(RECIPE["seed"])
        np.random.seed(RECIPE["seed"])

        data_root = repo / config["data_root"]
        feature_path = Path(config["feature_report_path"])
        if sha256_file(data_root / feature_path) != FEATURE_REPORT_SHA256:
            raise ValueError("Authenticated V37 train report changed")
        metadata = _read_json(data_root / feature_path)
        if (metadata.get("status") != "fixed_reflection_training_features_measured" or
                metadata.get("training_rows") != sum(TRAINING_ROWS.values()) or
                metadata.get("model", {}).get("sha256") != INITIALIZER_SHA256 or
                metadata.get("parent_feature_report", {}).get("sha256") != PARENT_FEATURE_REPORT_SHA256 or
                any(metadata.get(key) != 0 for key in ("private_reads", "sealed_reads", "development_tensor_reads", "optimizer_steps"))):
            raise ValueError("V37 feature report is not the exact synthetic train-only population")
        _verify_report_references(data_root, metadata)
        if sha256_file(repo / "ml/policy/evidence-policy.json") != EVIDENCE_POLICY_SHA256 or sha256_file(repo / "ml/policy/acceptance-bars.json") != ACCEPTANCE_BARS_SHA256:
            raise ValueError("Shared evidence or acceptance policy changed")
        group_ids = make_group_ids(metadata)
        training, loaded_report = load_training(data_root, feature_path, FEATURE_REPORT_SHA256,
            TRAINING_ROWS, INITIALIZER_SHA256, _NoopBudget())
        if loaded_report != metadata or sha256_file(data_root / feature_path) != FEATURE_REPORT_SHA256:
            raise ValueError("Train report changed during authenticated loading")
        initializer = data_root / config["source_checkpoint_path"]
        if sha256_file(initializer) != INITIALIZER_SHA256:
            raise ValueError("Retained V27 initializer changed")

        base = ScaleClassifierNet(ModelConfig())
        base.load_state_dict(torch.load(initializer, map_location="cpu", weights_only=True)["state_dict"])
        model = SpatialCenterNet(base).to(device)
        frozen_base_state = {key: value.detach().clone() for key, value in model.base.state_dict().items()}
        if any(parameter.device != device for parameter in model.parameters()):
            raise RuntimeError("Model parameters were not fully placed on the selected GPU")
        head = model.confidence
        if sum(parameter.numel() for parameter in head.parameters()) != TRAINABLE_PARAMETERS:
            raise ValueError("Trainable confidence projection parameter count changed")
        if any(parameter.requires_grad for parameter in model.base.parameters()):
            raise ValueError("Frozen V27 base unexpectedly has trainable parameters")
        optimizer = create_optimizer(head)
        assert_optimizer_on_device(optimizer, device)
        training_result = _train(head, optimizer, training, group_ids, device)
        model.verify_frozen_state(frozen_base_state)
        if sha256_file(initializer) != INITIALIZER_SHA256:
            raise ValueError("V27 initializer changed during training")
        verify_bound_source_snapshot(repo, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])

        checkpoint = output / "marker-center-pairwise.pt"
        temporary = checkpoint.with_suffix(checkpoint.suffix + ".tmp")
        torch.save({"state_dict": {key: value.detach().cpu() for key, value in model.state_dict().items()},
                    "config": model.export_contract(), "candidate_binding": authorization.binding}, temporary)
        os.replace(temporary, checkpoint)
        report = {
            "schema": "graphreader.marker-center-confidence-pairwise-training-report.v1",
            "task": TASK, "revision": REVISION, "candidate_id": CANDIDATE_ID,
            "status": "training_complete_awaiting_dev_evaluation",
            "training": training_result,
            "feature_report_sha256": FEATURE_REPORT_SHA256,
            "parent_feature_report_sha256": PARENT_FEATURE_REPORT_SHA256,
            "initializer_sha256": INITIALIZER_SHA256,
            "checkpoint_sha256": sha256_file(checkpoint),
            "runner_source_bundle_sha256": authorization.binding["runner_source_bundle_sha256"],
            "canonical_opened_record": authorization.opened_path.relative_to(repo).as_posix(),
            "device": {"name": "AMD Radeon RX 6600 XT", "hip_architecture": "gfx1032", "torch_device": str(device)},
            "synthetic_train_only": True,
            "memory_preflight": memory_preflight,
            "feature_groups": len(TRAINING_ROWS),
            "training_rows": sum(TRAINING_ROWS.values()),
            "development_reads": 0, "private_reads": 0, "sealed_reads": 0,
            "production_approval": False, "release_eligible": False,
            "seconds": time.perf_counter() - started,
        }
        _write_json(output / "training-report.json", report)
        return report
    except BaseException as error:
        if output.exists():
            (output / "exception.txt").write_text(traceback.format_exc(), encoding="utf-8")
        void_candidate(authorization, error)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(Path.cwd(), args.output)
    print(json.dumps({"status": report["status"], "optimizer_steps": report["training"]["optimizer_steps"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
