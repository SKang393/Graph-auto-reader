# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Source-bound train-only wrapper for the V39 frozen pretrained DB head."""

from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import time
from typing import Any, Callable, Mapping

import numpy as np
import torch

from ml.markers import training_budget
from ml.markers.gate_seal import (
    canonical_json_bytes,
    sha256_file,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3
from ml.ocr.official_bakeoff import (
    frozen_head_training,
    frozen_trunk_head,
    production_head_inputs,
)


REPO_ROOT = Path(__file__).resolve().parents[3]
ROOT = Path("ml/ocr/pretrained_db_head_v39")
CONFIG_PATH = ROOT / "training/p1.json"
PROTOCOL_PATH = ROOT / "protocol.json"
TASK = "ocr-detection"
REVISION = "graph-text-pretrained-db-head-v39"
CANDIDATE_ID = "P1"
CONFIG_SCHEMA = "graphreader.ocr-pretrained-db-head-v39-candidate-config.v1"
STAGE_SCHEMA = "graphreader.ocr-pretrained-db-head-v39-training-stage.v1"
PARENT_MODEL_SHA256 = frozen_trunk_head.REVIEWED_DETECTOR_SHA256
PARITY_SCHEMA = "graphreader.official-head-captured-step-zero-parity.v1"
PARITY_TOLERANCE = 1e-5
CHECKPOINT_NAME = "selected-head.pt"
ONNX_NAME = "detector-head-v39-p1.onnx"
STAGE_NAME = "training-stage.json"

RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    ROOT / "__init__.py",
    ROOT / "train_p1.py",
    PROTOCOL_PATH,
    Path("ml/ocr/official_bakeoff/production_head_inputs.py"),
    Path("ml/ocr/official_bakeoff/frozen_head_training.py"),
    Path("ml/ocr/official_bakeoff/frozen_trunk_head.py"),
    Path("ml/ocr/production_tiled_inputs.py"),
    Path("ml/markers/training_budget.py"),
    Path("ml/markers/gate_seal.py"),
    Path("ml/policy/evidence_policy.py"),
    Path("ml/markers/center/mask_preserving_v24/runtime_inputs.py"),
    Path("ml/markers/center/plot_domain_v25/runtime_domain_binding.py"),
    Path("ml/markers/center/plot_domain_v25/runtime_domain_binding_v3.py"),
    *runtime_domain_binding_v3.GENERATOR_SOURCE_PATHS,
)))

_CONFIG_KEYS = {
    "schema", "task", "revision", "candidate_id", "stage",
    "expected_runner_source_bundle_sha256", "parent_model", "v3_binding",
    "capture_report", "captured_parity_report", "captured_parity_source",
    "head_source", "feature_model", "recipe", "expected_data",
    "synthetic_only", "private_data", "sealed_data", "production_approval",
}
_RECIPE = {
    "seed": frozen_head_training.SEED,
    "epochs": frozen_head_training.EPOCHS,
    "batch_size": frozen_head_training.BATCH_SIZE,
    "learning_rate": frozen_head_training.LEARNING_RATE,
    "weight_decay": frozen_head_training.WEIGHT_DECAY,
    "dice_epsilon": frozen_head_training.DICE_EPSILON,
    "shrink_ratio": production_head_inputs.DB_SHRINK_RATIO,
    "empty_target_objective": "masked_mean_probability",
    "captured_parity_maximum_absolute_error": PARITY_TOLERANCE,
}
_EXPECTED_DATA = {
    "train": {"source_count": 20, "panel_count": 28, "full_source_truth_count": 709},
    "validation": {"source_count": 3, "panel_count": 9, "full_source_truth_count": 183},
}


class V39TrainingError(RuntimeError):
    """The V39 authorization, input, parity, or output contract is invalid."""


@dataclass(frozen=True)
class PreparedTraining:
    config: Mapping[str, Any]
    config_path: Path
    config_sha256: str
    training_panels: tuple[frozen_head_training.TrainingPanel, ...]
    train_source_count: int
    train_truth_count: int
    validation_source_count: int
    validation_panel_count: int
    validation_truth_count: int
    feature_inventory_sha256: str
    parity_maximum_absolute_error: float


@dataclass(frozen=True)
class TrainingDependencies:
    load_inputs: Callable[..., production_head_inputs.ProductionHeadInputs]
    extract_head: Callable[..., frozen_trunk_head.FrozenTrunkHeadBundle]
    train_head: Callable[..., frozen_head_training.TrainingResult]
    patch_head: Callable[..., frozen_trunk_head.PatchResult]
    acquire: Callable[..., training_budget.TrainingAuthorization]
    verify_snapshot: Callable[[Path, Path, object], None]
    void: Callable[[training_budget.TrainingAuthorization, BaseException], Path]


def _default_dependencies() -> TrainingDependencies:
    return TrainingDependencies(
        production_head_inputs.load_production_head_inputs,
        frozen_trunk_head.extract_frozen_trunk_head,
        frozen_head_training.train_frozen_head,
        frozen_trunk_head.patch_head_constants,
        training_budget.acquire_training_candidate,
        verify_bound_source_snapshot,
        training_budget.void_candidate,
    )


def prepare_training(
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: TrainingDependencies | None = None,
) -> PreparedTraining:
    """Authenticate cached features and full train/dev truth without optimizing."""

    root = repository_root.resolve()
    relative_config = _relative(root, config_path, "configuration")
    config_file = root / relative_config
    config, config_sha = _load_config(config_file, root)
    expected_bundle = _sha(config["expected_runner_source_bundle_sha256"], "runner source bundle")
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != expected_bundle:
        raise V39TrainingError("V39 runner source bundle differs from the authorized configuration")

    parent_path = _bound_path(root, config["parent_model"], "parent model")
    _read_exact(parent_path, PARENT_MODEL_SHA256, "parent model")
    head_source = _bound_path(root, config["head_source"], "frozen-head source")
    if head_source != (root / "ml/ocr/official_bakeoff/frozen_trunk_head.py").resolve():
        raise V39TrainingError("V39 frozen-head source path changed")
    head_source_sha = _sha(config["head_source"]["sha256"], "frozen-head source")
    _read_exact(head_source, head_source_sha, "frozen-head source")
    parity_source = _bound_path(root, config["captured_parity_source"], "captured parity source")
    parity_source_sha = _sha(config["captured_parity_source"]["sha256"], "captured parity source")
    _read_exact(parity_source, parity_source_sha, "captured parity source")
    feature_model = _bound_path(root, config["feature_model"], "feature model")
    feature_model_sha = _sha(config["feature_model"]["sha256"], "feature model")
    _read_exact(feature_model, feature_model_sha, "feature model")
    capture_report = _bound_path(root, config["capture_report"], "capture report")
    capture_report_sha = _sha(config["capture_report"]["sha256"], "capture report")
    capture = _read_json_exact(capture_report, capture_report_sha, "capture report")
    parity_report = _bound_path(root, config["captured_parity_report"], "captured parity report")
    parity_report_sha = _sha(config["captured_parity_report"]["sha256"], "captured parity report")
    parity = _read_json_exact(parity_report, parity_report_sha, "captured parity report")
    features, feature_hash, parity_max = _validate_parity_and_features(
        parity,
        parity_report.parent,
        capture,
        capture_report_sha,
        parity_source_sha,
        head_source_sha,
        feature_model_sha,
    )

    binding_path = _bound_path(root, config["v3_binding"], "V3 binding")
    binding_sha = _sha(config["v3_binding"]["sha256"], "V3 binding")
    if binding_sha != production_head_inputs.V3_BINDING_SHA256:
        raise V39TrainingError("V39 requires the frozen V3 runtime binding")
    deps = dependencies or _default_dependencies()
    inputs = deps.load_inputs(
        binding_path,
        binding_sha,
        capture_report,
        capture_report_sha,
        repository_root=root,
    )
    _validate_loaded_counts(inputs)
    all_panels = {panel.panel_id: panel for panel in (*inputs.train.panels, *inputs.dev.panels)}
    if set(all_panels) != set(features):
        raise V39TrainingError("Captured features and authenticated production panels differ")
    training_panels: list[frozen_head_training.TrainingPanel] = []
    for panel in inputs.train.panels:
        feature, input_sha = features[panel.panel_id]
        if input_sha != panel.tensor_sha256:
            raise V39TrainingError(f"Feature input identity changed: {panel.panel_id}")
        expected_shape = (1, frozen_trunk_head.FEATURE_CHANNELS,
                          panel.tensor_shape[2] // 4, panel.tensor_shape[3] // 4)
        if feature.shape != expected_shape:
            raise V39TrainingError(f"Feature shape changed: {panel.panel_id}")
        training_panels.append(frozen_head_training.TrainingPanel(
            panel.panel_id, "train", feature, panel.shrink_target, panel.supervision_mask
        ))
    for panel in inputs.dev.panels:
        feature, input_sha = features[panel.panel_id]
        if input_sha != panel.tensor_sha256:
            raise V39TrainingError(f"Validation feature input identity changed: {panel.panel_id}")
        expected_shape = (1, frozen_trunk_head.FEATURE_CHANNELS,
                          panel.tensor_shape[2] // 4, panel.tensor_shape[3] // 4)
        if feature.shape != expected_shape:
            raise V39TrainingError(f"Validation feature shape changed: {panel.panel_id}")
    return PreparedTraining(
        config,
        relative_config,
        config_sha,
        tuple(training_panels),
        inputs.train.source_count,
        inputs.train.full_source_truth_count,
        inputs.dev.source_count,
        inputs.dev.panel_count,
        inputs.dev.full_source_truth_count,
        feature_hash,
        parity_max,
    )


def train_candidate(
    output_dir: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: TrainingDependencies | None = None,
) -> dict[str, Any]:
    """Run the authorized train-only stage and leave C# dev evaluation pending."""

    root = repository_root.resolve()
    output = output_dir if output_dir.is_absolute() else root / output_dir
    output = output.resolve()
    _require_inside(root, output, "output")
    if output.exists():
        raise V39TrainingError(f"V39 training output already exists: {output}")
    relative_config = _relative(root, config_path, "configuration")
    pre_config, _ = _load_config(root / relative_config, root)
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != _sha(
        pre_config["expected_runner_source_bundle_sha256"], "runner source bundle"
    ):
        raise V39TrainingError("V39 runner source bundle differs from the authorized configuration")
    deps = dependencies or _default_dependencies()
    authorization = deps.acquire(
        root,
        task=TASK,
        revision=REVISION,
        candidate_id=CANDIDATE_ID,
        config_path=relative_config,
        runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    started = time.perf_counter()
    phase = "preflight"
    result: frozen_head_training.TrainingResult | None = None
    try:
        output.mkdir(parents=True)
        prepared = prepare_training(
            relative_config,
            repository_root=root,
            dependencies=deps,
        )
        if authorization.snapshot_path is None:
            raise V39TrainingError("Training authorization omitted its source snapshot")
        deps.verify_snapshot(root, authorization.snapshot_path, authorization.binding.get("source_snapshot_sha256"))
        parent_path = _bound_path(root, prepared.config["parent_model"], "parent model")
        bundle = deps.extract_head(parent_path, PARENT_MODEL_SHA256)
        phase = "optimization"
        result = deps.train_head(bundle.head, prepared.training_panels)
        _validate_training_result(result, len(prepared.training_panels))
        phase = "artifacts"
        checkpoint_path = output / CHECKPOINT_NAME
        _write_checkpoint(checkpoint_path, bundle.head)
        onnx_path = output / ONNX_NAME
        patch = deps.patch_head(parent_path, onnx_path, bundle.head, PARENT_MODEL_SHA256)
        if tuple(patch.changed_constants) != tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES):
            raise V39TrainingError("V39 ONNX must replace exactly the nine reviewed head constants")
        if tuple(patch.unchanged_trainable_constants):
            raise V39TrainingError("V39 ONNX retained an unchanged trainable head constant")
        if sha256_file(onnx_path) != patch.output_sha256 or patch.source_sha256 != PARENT_MODEL_SHA256:
            raise V39TrainingError("V39 patched ONNX identity changed after creation")
        deps.verify_snapshot(root, authorization.snapshot_path, authorization.binding.get("source_snapshot_sha256"))
        report = _success_report(
            root,
            prepared,
            authorization,
            result,
            patch,
            checkpoint_path,
            onnx_path,
            (time.perf_counter() - started) * 1000.0,
        )
        _write_json(output / STAGE_NAME, report)
        return report
    except Exception as error:
        failure = {
            "schema": STAGE_SCHEMA,
            "task": TASK,
            "revision": REVISION,
            "candidate_id": CANDIDATE_ID,
            "stage": "P1",
            "status": "failed_presealed",
            "phase": phase,
            "optimizer_steps": result.optimizer_steps if result is not None else None,
            "optimizer_steps_known": result is not None,
            "exception_type": type(error).__name__,
            "exception_message": str(error),
            "completed_utc": datetime.now(timezone.utc).isoformat(),
            "private_data": False,
            "sealed_data": False,
            "sealed_runs": 0,
            "production_approval": False,
            "training_authorization": authorization.binding,
        }
        stage_path = output / STAGE_NAME
        if output.is_dir() and not stage_path.exists():
            _write_json(stage_path, failure)
        deps.void(authorization, error)
        raise


def _load_config(path: Path, root: Path) -> tuple[dict[str, Any], str]:
    config = _read_json_exact(path, None, "V39 configuration")
    if set(config) != _CONFIG_KEYS:
        raise V39TrainingError("V39 configuration fields changed")
    expected = {
        "schema": CONFIG_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "production_approval": False,
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise V39TrainingError(f"V39 configuration {key} changed")
    if config.get("recipe") != _RECIPE:
        raise V39TrainingError("V39 frozen training recipe changed")
    if config.get("expected_data") != _EXPECTED_DATA:
        raise V39TrainingError("V39 full train/dev inventory changed")
    _sha(config.get("expected_runner_source_bundle_sha256"), "runner source bundle")
    for key in (
        "parent_model", "v3_binding", "capture_report", "captured_parity_report",
        "captured_parity_source", "head_source", "feature_model",
    ):
        descriptor = config.get(key)
        if not isinstance(descriptor, dict) or set(descriptor) != {"path", "sha256"}:
            raise V39TrainingError(f"V39 {key} descriptor changed")
        _bound_path(root, descriptor, key)
        _sha(descriptor.get("sha256"), key)
    if _sha(config["parent_model"]["sha256"], "parent model") != PARENT_MODEL_SHA256:
        raise V39TrainingError("V39 parent model identity changed")
    return config, sha256_file(path)


def _validate_parity_and_features(
    report: Mapping[str, Any],
    report_directory: Path,
    capture: Mapping[str, Any],
    capture_sha: str,
    helper_sha: str,
    head_source_sha: str,
    feature_model_sha: str,
) -> tuple[dict[str, tuple[np.ndarray, str]], str, float]:
    exact = {
        "schema": PARITY_SCHEMA,
        "source_model_sha256": PARENT_MODEL_SHA256,
        "source_code_sha256": head_source_sha,
        "helper_sha256": helper_sha,
        "capture_report_sha256": capture_sha,
        "feature_model_sha256": feature_model_sha,
        "no_op_model_sha256": PARENT_MODEL_SHA256,
        "provider": "CPUExecutionProvider",
        "cpu_threads": 1,
        "torch_mkldnn_enabled": False,
        "tolerance": PARITY_TOLERANCE,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
    }
    for key, value in exact.items():
        if report.get(key) != value:
            raise V39TrainingError(f"Captured parity {key} changed")
    capture_rows = capture.get("panels")
    rows = report.get("panels")
    if not isinstance(capture_rows, list) or not isinstance(rows, list) or len(capture_rows) != 37 or len(rows) != 37:
        raise V39TrainingError("Captured parity must retain all 37 panels")
    expected: dict[str, tuple[str, tuple[int, ...], str]] = {}
    for raw in capture_rows:
        if not isinstance(raw, dict) or not isinstance(raw.get("tensor"), dict):
            raise V39TrainingError("Capture report panel is malformed")
        panel_id = _text(raw.get("panel_id"), "capture panel identity")
        if panel_id in expected:
            raise V39TrainingError("Capture report repeats a panel identity")
        tensor = raw["tensor"]
        shape = _shape(tensor.get("shape"), "capture tensor shape", expected_length=4)
        expected[panel_id] = (_sha(tensor.get("sha256"), "capture tensor"), shape,
                              _text(raw.get("split"), "capture split"))
    loaded: dict[str, tuple[np.ndarray, str]] = {}
    inventory = sha256()
    errors: list[float] = []
    for raw in rows:
        if not isinstance(raw, dict):
            raise V39TrainingError("Captured parity panel is malformed")
        panel_id = _text(raw.get("panel_id"), "parity panel identity")
        if panel_id in loaded or panel_id not in expected:
            raise V39TrainingError("Captured parity panel identity changed or repeated")
        input_sha, input_shape, split = expected[panel_id]
        if raw.get("split") != split or _sha(raw.get("input_sha256"), "feature input") != input_sha:
            raise V39TrainingError(f"Captured parity input identity changed: {panel_id}")
        if _shape(raw.get("shape"), "parity input shape", 4) != input_shape:
            raise V39TrainingError(f"Captured parity input shape changed: {panel_id}")
        feature_shape = _shape(raw.get("feature_shape"), "feature shape", 4)
        expected_feature_shape = (1, frozen_trunk_head.FEATURE_CHANNELS,
                                  input_shape[2] // 4, input_shape[3] // 4)
        if feature_shape != expected_feature_shape:
            raise V39TrainingError(f"Captured feature shape changed: {panel_id}")
        name = _safe_leaf(raw.get("feature_file"), "feature file")
        feature_path = report_directory / name
        payload = _read_exact(feature_path, _sha(raw.get("feature_sha256"), "feature"),
                              f"feature {panel_id}")
        if len(payload) != math.prod(feature_shape) * 4:
            raise V39TrainingError(f"Captured feature byte count changed: {panel_id}")
        feature = np.frombuffer(payload, dtype="<f4").reshape(feature_shape)
        if not np.isfinite(feature).all():
            raise V39TrainingError(f"Captured feature contains non-finite values: {panel_id}")
        error = raw.get("maximum_absolute_error")
        if isinstance(error, bool) or not isinstance(error, (int, float)) or not math.isfinite(error) or error < 0:
            raise V39TrainingError(f"Captured parity error is invalid: {panel_id}")
        errors.append(float(error))
        loaded[panel_id] = (feature, input_sha)
        inventory.update(panel_id.encode("utf-8") + b"\0")
        inventory.update(input_sha.encode("ascii") + b"\0")
        inventory.update(raw["feature_sha256"].encode("ascii") + b"\n")
    if set(loaded) != set(expected):
        raise V39TrainingError("Captured parity omitted production panels")
    maximum = max(errors)
    declared = report.get("maximum_absolute_error")
    if isinstance(declared, bool) or not isinstance(declared, (int, float)) or float(declared) != maximum:
        raise V39TrainingError("Captured parity aggregate error was not recomputed from panel rows")
    recomputed_pass = maximum <= PARITY_TOLERANCE
    if report.get("passed") is not recomputed_pass or not recomputed_pass:
        raise V39TrainingError(
            f"Captured parity fails the fixed {PARITY_TOLERANCE:g} maximum absolute error"
        )
    if report.get("panel_count") != 37:
        raise V39TrainingError("Captured parity panel count changed")
    return loaded, inventory.hexdigest(), maximum


def _validate_loaded_counts(inputs: production_head_inputs.ProductionHeadInputs) -> None:
    observed = {
        "train": {
            "source_count": inputs.train.source_count,
            "panel_count": inputs.train.panel_count,
            "full_source_truth_count": inputs.train.full_source_truth_count,
        },
        "validation": {
            "source_count": inputs.dev.source_count,
            "panel_count": inputs.dev.panel_count,
            "full_source_truth_count": inputs.dev.full_source_truth_count,
        },
    }
    if observed != _EXPECTED_DATA:
        raise V39TrainingError("V39 authenticated full train/dev truth inventory changed")
    if (inputs.train.projected_source_truth_count, inputs.dev.projected_source_truth_count) != (709, 183):
        raise V39TrainingError("V39 runtime panels do not retain every full-source truth")
    if any((split.outside_runtime_crop_truth_count or split.partial_source_truth_count or
            split.overlapping_source_truth_count) for split in (inputs.train, inputs.dev)):
        raise V39TrainingError("V39 runtime crop truth coverage changed")


def _success_report(
    root: Path,
    prepared: PreparedTraining,
    authorization: training_budget.TrainingAuthorization,
    result: frozen_head_training.TrainingResult,
    patch: frozen_trunk_head.PatchResult,
    checkpoint_path: Path,
    onnx_path: Path,
    elapsed_ms: float,
) -> dict[str, Any]:
    return {
        "schema": STAGE_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "status": "training_complete_pending_actual_csharp_dev_evaluation",
        "config_path": prepared.config_path.as_posix(),
        "config_sha256": prepared.config_sha256,
        "parent_model_sha256": PARENT_MODEL_SHA256,
        "feature_inventory_sha256": prepared.feature_inventory_sha256,
        "captured_parity_maximum_absolute_error": prepared.parity_maximum_absolute_error,
        "captured_parity_tolerance": PARITY_TOLERANCE,
        "train_source_count": prepared.train_source_count,
        "train_panel_count": len(prepared.training_panels),
        "train_full_source_truth_count": prepared.train_truth_count,
        "validation_source_count": prepared.validation_source_count,
        "validation_panel_count": prepared.validation_panel_count,
        "validation_full_source_truth_count": prepared.validation_truth_count,
        "training_result": asdict(result),
        "checkpoint": {
            "path": checkpoint_path.relative_to(root).as_posix(),
            "sha256": sha256_file(checkpoint_path),
            "content": "torch_state_dict_only",
        },
        "onnx": {
            "path": onnx_path.relative_to(root).as_posix(),
            "sha256": sha256_file(onnx_path),
            "source_sha256": patch.source_sha256,
            "changed_constants": list(patch.changed_constants),
            "unchanged_trainable_constants": list(patch.unchanged_trainable_constants),
        },
        "actual_csharp_dev_evaluation": "pending",
        "result_json_written": False,
        "canonical_ledger_updated": False,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "production_approval": False,
        "training_authorization": authorization.binding,
        "elapsed_ms": round(elapsed_ms, 3),
        "completed_utc": datetime.now(timezone.utc).isoformat(),
    }


def _validate_training_result(
    result: frozen_head_training.TrainingResult,
    panel_count: int,
) -> None:
    expected_steps = frozen_head_training.EPOCHS * panel_count
    fixed = (
        isinstance(result, frozen_head_training.TrainingResult)
        and result.optimizer_steps == expected_steps
        and result.epochs == frozen_head_training.EPOCHS
        and result.panel_count == panel_count
        and result.seed == frozen_head_training.SEED
        and result.batch_size == frozen_head_training.BATCH_SIZE
        and result.learning_rate == frozen_head_training.LEARNING_RATE
        and result.weight_decay == frozen_head_training.WEIGHT_DECAY
        and result.dice_epsilon == frozen_head_training.DICE_EPSILON
        and result.torch_backend == frozen_head_training.TORCH_BACKEND
        and result.trainable_parameter_names == frozen_head_training.TRAINABLE_PARAMETER_NAMES
        and result.frozen_batch_norm_sha256_before == result.frozen_batch_norm_sha256_after
        and 1 <= result.selected_epoch <= result.epochs
        and len(result.epoch_losses) == result.epochs
    )
    if not fixed:
        raise V39TrainingError("V39 training result differs from the frozen recipe")
    for index, row in enumerate(result.epoch_losses, start=1):
        values = (
            row.training_step_mean_loss,
            row.full_train_loss,
            row.full_positive_dice_loss,
            row.full_empty_negative_loss,
        )
        if (
            row.epoch != index
            or row.optimizer_steps != index * panel_count
            or any(value is not None and not math.isfinite(value) for value in values)
            or len(row.draw_order_sha256) != 64
            or any(character not in "0123456789abcdef" for character in row.draw_order_sha256)
        ):
            raise V39TrainingError("V39 epoch evidence differs from the frozen recipe")


def _write_checkpoint(path: Path, head: frozen_trunk_head.FrozenDbHead) -> None:
    state = {name: value.detach().cpu() for name, value in head.state_dict().items()}
    buffer = BytesIO()
    torch.save(state, buffer)
    with path.open("xb") as stream:
        stream.write(buffer.getvalue())


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    with path.open("xb") as stream:
        stream.write(canonical_json_bytes(dict(value)))


def _read_json_exact(path: Path, expected_sha: str | None, label: str) -> dict[str, Any]:
    payload = _read_exact(path, expected_sha, label)
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise V39TrainingError(f"{label} is not valid JSON") from error
    if not isinstance(value, dict):
        raise V39TrainingError(f"{label} must be a JSON object")
    return value


def _read_exact(path: Path, expected_sha: str | None, label: str) -> bytes:
    if not path.is_file():
        raise V39TrainingError(f"{label} is missing: {path}")
    payload = path.read_bytes()
    if expected_sha is not None and sha256(payload).hexdigest() != expected_sha:
        raise V39TrainingError(f"{label} SHA-256 changed")
    return payload


def _bound_path(root: Path, descriptor: Any, label: str) -> Path:
    if not isinstance(descriptor, dict):
        raise V39TrainingError(f"{label} descriptor must be an object")
    raw = descriptor.get("path")
    if not isinstance(raw, str) or not raw or Path(raw).is_absolute():
        raise V39TrainingError(f"{label} path must be repository-relative")
    path = (root / raw).resolve()
    _require_inside(root, path, label)
    return path


def _relative(root: Path, path: Path, label: str) -> Path:
    resolved = path.resolve() if path.is_absolute() else (root / path).resolve()
    _require_inside(root, resolved, label)
    return resolved.relative_to(root)


def _require_inside(root: Path, path: Path, label: str) -> None:
    try:
        path.relative_to(root)
    except ValueError as error:
        raise V39TrainingError(f"{label} path is outside the repository") from error


def _safe_leaf(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value or Path(value).name != value or value in {".", ".."}:
        raise V39TrainingError(f"{label} must be a safe file name")
    return value


def _sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or len(value) != 64 or value != value.lower() or any(
        character not in "0123456789abcdef" for character in value
    ):
        raise V39TrainingError(f"{label} must be a lowercase SHA-256")
    return value


def _text(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise V39TrainingError(f"{label} must be a nonempty string")
    return value


def _shape(value: Any, label: str, expected_length: int) -> tuple[int, ...]:
    if not isinstance(value, list) or len(value) != expected_length or any(
        isinstance(item, bool) or not isinstance(item, int) or item <= 0 for item in value
    ):
        raise V39TrainingError(f"{label} is invalid")
    return tuple(value)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    args = parser.parse_args()
    report = train_candidate(args.output, args.config)
    print(json.dumps({
        "status": report["status"],
        "optimizer_steps": report["training_result"]["optimizer_steps"],
        "actual_csharp_dev_evaluation": report["actual_csharp_dev_evaluation"],
    }, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "CANDIDATE_ID", "CONFIG_PATH", "CONFIG_SCHEMA", "ONNX_NAME",
    "PARENT_MODEL_SHA256", "PARITY_TOLERANCE", "PreparedTraining", "REVISION",
    "RUNNER_SOURCE_PATHS", "STAGE_NAME", "STAGE_SCHEMA", "TASK",
    "TrainingDependencies", "V39TrainingError", "prepare_training", "train_candidate",
]
