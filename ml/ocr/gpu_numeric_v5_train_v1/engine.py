# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Blocked-by-default, fixed-recipe GPU V5 numeric/reject trainer.

This module defines a future training path. The checked-in registration is
blocked, so no model or optimizer can be constructed by the current package.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import io
import json
import os
from pathlib import Path
import stat
from typing import Any
from uuid import uuid4

import numpy as np

from .corpus import CorpusPair, CorpusError, load_registered_pair, project_file, sha256_file, verify_v5_sources
from .journal import JournalError, StepBudgetJournal, file_sha256
from .protocol import (
    DEVICE, EXPECTED_GPU_ARCH, EXPECTED_GPU_NAME, RECIPE_SHA256,
    REGISTRATION_RECEIPT_HASH_FIELD, ROOT, RUN_BUDGET_STEPS, V5_SOURCE_HASHES,
)


class TrainingAdmissionError(RuntimeError):
    """Training failed a fixed source, corpus, GPU, or review admission check."""


@dataclass(frozen=True)
class AdmittedRun:
    registration: dict[str, Any]
    recipe: dict[str, Any]
    device: Any
    pair: CorpusPair
    source_bindings: dict[str, str]
    output_root: Path


def _sha_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _atomic_bytes(path: Path, payload: bytes) -> None:
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def _source_bundle() -> dict[str, str]:
    names = (
        "ml/ocr/gpu_numeric_v5_train_v1/__init__.py",
        "ml/ocr/gpu_numeric_v5_train_v1/corpus.py",
        "ml/ocr/gpu_numeric_v5_train_v1/engine.py",
        "ml/ocr/gpu_numeric_v5_train_v1/journal.py",
        "ml/ocr/gpu_numeric_v5_train_v1/protocol.py",
    )
    return {name: sha256_file(project_file(name)) for name in names}


def _source_bundle_digest(bindings: dict[str, str]) -> str:
    payload = json.dumps(bindings, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return _sha_bytes(payload)


def _verify_engine_source_bundle(registration: dict[str, Any], bindings: dict[str, str]) -> str:
    digest = _source_bundle_digest(bindings)
    if registration.get("engine_source_bundle_sha256") != digest:
        raise TrainingAdmissionError("registered engine source bundle differs")
    return digest


def registration_core_bytes(registration: dict[str, Any]) -> bytes:
    """Canonical registration bytes excluding the receipt's own byte hash.

    The reviewed receipt binds this digest. The receipt's SHA-256 is then
    stored in the registration, avoiding an impossible recursive hash cycle.
    All other registration fields, including the receipt path, remain bound.
    """
    if not isinstance(registration, dict) or REGISTRATION_RECEIPT_HASH_FIELD not in registration:
        raise TrainingAdmissionError("registration core is missing its receipt-hash field")
    core = dict(registration)
    core.pop(REGISTRATION_RECEIPT_HASH_FIELD)
    return json.dumps(core, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def registration_core_sha256(registration: dict[str, Any]) -> str:
    return _sha_bytes(registration_core_bytes(registration))


def require_rx6600xt_gpu(torch_module: Any | None = None) -> Any:
    """Return HIP cuda:0 only when the expected physical GPU is visible.

    No CPU or alternate-GPU fallback is provided. The caller must additionally
    pass every model/data/gradient/optimizer tensor through
    `assert_all_tensors_on_gpu`.
    """
    if torch_module is None:
        import torch as torch_module
    if not bool(torch_module.cuda.is_available()):
        raise TrainingAdmissionError("RX 6600 XT HIP device cuda:0 is unavailable")
    hip = getattr(getattr(torch_module, "version", None), "hip", None)
    if not isinstance(hip, str) or not hip:
        raise TrainingAdmissionError("training requires the HIP PyTorch build; CPU/CUDA fallback is disabled")
    try:
        properties = torch_module.cuda.get_device_properties(0)
    except Exception as error:
        raise TrainingAdmissionError("cannot inspect HIP cuda:0 device identity") from error
    if getattr(properties, "name", None) != EXPECTED_GPU_NAME:
        raise TrainingAdmissionError("HIP cuda:0 is not the registered AMD Radeon RX 6600 XT")
    architecture = getattr(properties, "gcnArchName", None)
    if not isinstance(architecture, str) or architecture.split(":", 1)[0] != EXPECTED_GPU_ARCH:
        raise TrainingAdmissionError("HIP cuda:0 architecture is not gfx1032")
    device = torch_module.device(DEVICE)
    if str(device) != DEVICE:
        raise TrainingAdmissionError("resolved training device differs from fixed cuda:0")
    return device


def _walk_tensors(value: Any):
    if hasattr(value, "device") and hasattr(value, "numel"):
        yield value
    elif isinstance(value, dict):
        for child in value.values():
            yield from _walk_tensors(child)
    elif isinstance(value, (tuple, list)):
        for child in value:
            yield from _walk_tensors(child)


def assert_all_tensors_on_gpu(torch_module: Any, *values: Any) -> None:
    expected = torch_module.device(DEVICE)
    for value in values:
        for tensor in _walk_tensors(value):
            if tensor.device != expected:
                raise TrainingAdmissionError(f"training tensor escaped fixed HIP device: {tensor.device}")


def _verify_pin(path: Path, expected_sha256: str, description: str) -> bytes:
    try:
        payload = path.read_bytes()
    except OSError as error:
        raise TrainingAdmissionError(f"required {description} is missing") from error
    if _sha_bytes(payload) != expected_sha256:
        raise TrainingAdmissionError(f"required {description} hash differs from fixed package pin")
    return payload


def _validate_recipe(value: dict[str, Any]) -> None:
    expected = {
        "schema": "graphreader.gpu-numeric-v5-train.recipe.v1",
        "scope": "public_synthetic_train_validation_only",
        "task": "v5_numeric_and_reject_component_classification",
        "status": "implementation_only_blocked_pending_registered_corpora_and_experiment_review",
        "required_gpu": {"device": DEVICE, "name": EXPECTED_GPU_NAME, "architecture": EXPECTED_GPU_ARCH,
                         "hip_required": True, "cpu_fallback": False},
        "model": {"class": "ComponentEnsembleGlyphNet",
                  "source_sha256": V5_SOURCE_HASHES["ml/ocr/component_ensemble_v5/model.py"],
                  "class_count": 14, "glyph_shape": [1, 24, 26], "reject_class_index": 13},
        "inputs": {"positive_schema": "graphreader.synthetic-tick-corpus.v3",
                   "positive_adapter_schema": "graphreader.v5-component-crop-adapter.v1",
                   "reject_schema": "graphreader.synthetic-reject-glyph.v1",
                   "splits": ["train", "validation"], "test_split_allowed": False,
                   "positive_crop_contract": "requires separately authenticated production-compatible 128x32 grayscale V5 component raster; raw v3 RGB bbox crops are insufficient",
                   "reject_components": "must be recomputed by the exact V5 component extractor and match saved tensor hashes"},
        "bounded_recipe": {"seed": 20261031, "model_initialization_seed": 20260820,
                           "batch_size": 128, "maximum_optimizer_steps": RUN_BUDGET_STEPS,
                           "optimizer": "AdamW", "learning_rate": 0.0015, "weight_decay": 0.0001,
                           "deterministic_algorithms_required": True, "drop_last": False},
        "admission": {"registration_required": True, "optimizer_allowed_without_registered_pair": False,
                       "checkpoint_allowed_without_registered_pair": False, "training_approved": False,
                       "production_approved": False, "sealed_or_private_inputs_allowed": False,
                       "test_data_allowed": False,
                       "acceptance_policy": "unregistered; no metric gate, candidate promotion, or production approval is authorized"},
    }
    if value != expected:
        raise TrainingAdmissionError("fixed training recipe contents differ from protocol")


def _load_registration() -> dict[str, Any]:
    try:
        payload = project_file("ml/ocr/gpu_numeric_v5_train_v1/registration.json").read_bytes()
    except (OSError, CorpusError) as error:
        raise TrainingAdmissionError("training registration is unavailable") from error
    try:
        registration = json.loads(payload)
    except json.JSONDecodeError as error:
        raise TrainingAdmissionError("registration JSON is invalid") from error
    if (not isinstance(registration, dict) or registration.get("schema") != "graphreader.gpu-numeric-v5-train.registration.v1" or
            registration.get("state") != "registered_after_independent_review" or
            registration.get("training_authorized") is not True):
        raise TrainingAdmissionError("no independently registered positive/reject corpus pair; training remains blocked")
    required = {"schema", "state", "training_authorized", "positive_corpus", "positive_source_bundle",
                "positive_v5_component_corpus", "reject_corpus",
                "review_receipt_path", "review_receipt_sha256", "positive_v5_adapter_path",
                "positive_v5_adapter_sha256", "positive_v5_production_source_bindings",
                "positive_v5_crop_transform", "v5_component_source_sha256", "reject_source_bindings",
                "run_output_path", "engine_source_bundle_sha256"}
    if set(registration) != required:
        raise TrainingAdmissionError("registered admission fields differ from the fixed registration schema")
    return registration


def _verify_review_receipt(registration: dict[str, Any], recipe_sha: str,
                           source_bundle_sha: str) -> None:
    receipt_path = registration["review_receipt_path"]
    receipt_sha = registration["review_receipt_sha256"]
    if not isinstance(receipt_path, str) or not isinstance(receipt_sha, str):
        raise TrainingAdmissionError("independent training review receipt is missing")
    if not receipt_path.startswith("artifacts/") or any(part.lower() in {"private", "sealed", "test", "tests"} for part in Path(receipt_path).parts):
        raise TrainingAdmissionError("independent review receipt path is outside public project artifacts")
    try:
        payload = project_file(receipt_path).read_bytes()
    except (OSError, CorpusError) as error:
        raise TrainingAdmissionError("independent training review receipt is unavailable") from error
    if _sha_bytes(payload) != receipt_sha:
        raise TrainingAdmissionError("independent training review receipt hash differs")
    try:
        receipt = json.loads(payload)
    except json.JSONDecodeError as error:
        raise TrainingAdmissionError("independent training review receipt is invalid JSON") from error
    if not isinstance(receipt, dict):
        raise TrainingAdmissionError("independent training review receipt root is invalid")
    if (receipt.get("training_authorized") is not True or receipt.get("production_approved") is not False or
            receipt.get("recipe_sha256") != recipe_sha or receipt.get("engine_source_bundle_sha256") != source_bundle_sha or
            receipt.get("registration_core_sha256") != registration_core_sha256(registration) or
            receipt.get("positive_manifest_sha256") != registration["positive_corpus"].get("manifest_sha256") or
            receipt.get("positive_v5_component_manifest_sha256") != registration["positive_v5_component_corpus"].get("manifest_sha256") or
            receipt.get("positive_v5_adapter_sha256") != registration.get("positive_v5_adapter_sha256") or
            receipt.get("positive_v5_production_source_bindings") != registration.get("positive_v5_production_source_bindings") or
            receipt.get("positive_v5_crop_transform") != registration.get("positive_v5_crop_transform") or
            receipt.get("reject_manifest_sha256") != registration["reject_corpus"].get("manifest_sha256")):
        raise TrainingAdmissionError("independent review receipt does not bind this exact training run")


def _registered_source_path_hash(path_key: str, sha_key: str, registration: dict[str, Any]) -> None:
    path_value = registration.get(path_key)
    expected = registration.get(sha_key)
    if not isinstance(path_value, str) or not isinstance(expected, str):
        raise TrainingAdmissionError(f"registered source binding {path_key} is missing")
    actual = sha256_file(project_file(path_value))
    if actual != expected:
        raise TrainingAdmissionError(f"registered source binding differs: {path_value}")


def _validate_registered_corpus_entry(entry: Any, label: str) -> dict[str, Any]:
    if not isinstance(entry, dict) or set(entry) != {"artifact_root", "manifest_path", "manifest_sha256"}:
        raise TrainingAdmissionError(f"registered {label} corpus identity is incomplete")
    if not all(isinstance(entry.get(key), str) for key in entry):
        raise TrainingAdmissionError(f"registered {label} corpus paths/hashes are malformed")
    if len(entry["manifest_sha256"]) != 64 or any(character not in "0123456789abcdef" for character in entry["manifest_sha256"]):
        raise TrainingAdmissionError(f"registered {label} corpus manifest hash is malformed")
    artifact_root = Path(entry["artifact_root"])
    manifest_path = Path(entry["manifest_path"])
    if (artifact_root.is_absolute() or manifest_path.is_absolute() or ".." in artifact_root.parts or
            ".." in manifest_path.parts or artifact_root.parts[:1] != ("artifacts",) or
            manifest_path != artifact_root / "manifest.json"):
        raise TrainingAdmissionError(f"registered {label} corpus is not a project-local artifacts manifest")
    if any(part.lower() in {"private", "sealed", "test", "tests"} for part in artifact_root.parts):
        raise TrainingAdmissionError(f"registered {label} corpus path names a prohibited data scope")
    return entry


def preflight(torch_module: Any | None = None) -> AdmittedRun:
    """Authenticate fixed code/recipe and refuse until reviewed corpora exist."""
    try:
        actual_v5 = verify_v5_sources()
    except CorpusError as error:
        raise TrainingAdmissionError(str(error)) from error
    recipe_raw = _verify_pin(project_file("ml/ocr/gpu_numeric_v5_train_v1/recipe.json"), RECIPE_SHA256, "fixed recipe")
    try:
        recipe = json.loads(recipe_raw)
    except json.JSONDecodeError as error:
        raise TrainingAdmissionError("fixed recipe is invalid JSON") from error
    _validate_recipe(recipe)
    # Enforce GPU identity before reading any corpora. No CPU fallback exists.
    device = require_rx6600xt_gpu(torch_module)
    registration = _load_registration()
    source_bindings = _source_bundle()
    source_digest = _verify_engine_source_bundle(registration, source_bindings)
    try:
        _registered_source_path_hash("positive_v5_adapter_path", "positive_v5_adapter_sha256", registration)
    except CorpusError as error:
        raise TrainingAdmissionError(str(error)) from error
    if registration.get("v5_component_source_sha256") != actual_v5["ml/ocr/component_ensemble_v5/dataset.py"]:
        raise TrainingAdmissionError("registration does not bind exact V5 component source")
    reject_bindings = registration.get("reject_source_bindings")
    if not isinstance(reject_bindings, dict) or not reject_bindings:
        raise TrainingAdmissionError("registered reject source binding map is missing")
    for path, expected in reject_bindings.items():
        if not isinstance(path, str) or not isinstance(expected, str) or len(expected) != 64:
            raise TrainingAdmissionError("registered reject source binding entry is malformed")
        try:
            if sha256_file(project_file(path)) != expected:
                raise TrainingAdmissionError(f"reject source binding differs: {path}")
        except CorpusError as error:
            raise TrainingAdmissionError(str(error)) from error
    production_bindings = registration.get("positive_v5_production_source_bindings")
    if not isinstance(production_bindings, dict) or not production_bindings:
        raise TrainingAdmissionError("registered production crop-source bindings are missing")
    for path, expected in production_bindings.items():
        if not isinstance(path, str) or not isinstance(expected, str) or len(expected) != 64:
            raise TrainingAdmissionError("registered production crop-source entry is malformed")
        try:
            if sha256_file(project_file(path)) != expected:
                raise TrainingAdmissionError(f"production crop-source binding differs: {path}")
        except CorpusError as error:
            raise TrainingAdmissionError(str(error)) from error
    transform = registration.get("positive_v5_crop_transform")
    required_transform = {"schema", "source_mode", "output_mode", "output_size", "resize_filter",
                          "crop_origin_rule", "padding_rule"}
    if not isinstance(transform, dict) or set(transform) != required_transform:
        raise TrainingAdmissionError("registered V5 crop transform contract is incomplete")
    if (transform.get("schema") != "graphreader.v5-component-crop-adapter.v1" or
            transform.get("source_mode") != "RGB" or transform.get("output_mode") != "L" or
            transform.get("output_size") != [128, 32] or
            any(not isinstance(transform.get(key), str) or not transform[key] for key in
                ("resize_filter", "crop_origin_rule", "padding_rule"))):
        raise TrainingAdmissionError("registered V5 crop transform contract differs from the component input")
    positive = _validate_registered_corpus_entry(registration.get("positive_corpus"), "positive")
    adapter = _validate_registered_corpus_entry(registration.get("positive_v5_component_corpus"), "positive V5 component")
    reject = _validate_registered_corpus_entry(registration.get("reject_corpus"), "reject")
    registration["positive_v5_component_corpus"] = adapter
    _verify_review_receipt(registration, RECIPE_SHA256, source_digest)
    output_rel = registration.get("run_output_path")
    protected_roots = tuple(entry["artifact_root"] for entry in (positive, adapter, reject))
    output_root = _fixed_output_path(output_rel, protected_artifact_roots=protected_roots)
    if output_root.exists():
        raise TrainingAdmissionError("registered one-shot output path already exists; do not repeat this budget")
    try:
        pair = load_registered_pair(positive, reject, registration)
    except CorpusError as error:
        raise TrainingAdmissionError(str(error)) from error
    return AdmittedRun(registration, recipe, device, pair, source_bindings, output_root)


def _fixed_output_path(relative_path: Any, *, protected_artifact_roots: tuple[str, ...] = ()) -> Path:
    if not isinstance(relative_path, str) or "\\" in relative_path:
        raise TrainingAdmissionError("registered run output path is malformed")
    relative = Path(relative_path)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        raise TrainingAdmissionError("registered run output path is unsafe")
    if any(part.lower() in {"private", "sealed", "test", "tests"} for part in relative.parts):
        raise TrainingAdmissionError("registered run output path names a prohibited data scope")
    # Windows resolves case variants to the same path. Compare normalized
    # components so mixed-case spelling cannot bypass the corpus overlap guard.
    output_parts = tuple(part.casefold() for part in relative.parts)
    for protected in protected_artifact_roots:
        if not isinstance(protected, str) or "\\" in protected:
            raise TrainingAdmissionError("registered corpus root is malformed")
        protected_path = Path(protected)
        if protected_path.is_absolute() or any(part in ("", ".", "..") for part in protected_path.parts):
            raise TrainingAdmissionError("registered corpus root is malformed")
        corpus_parts = tuple(part.casefold() for part in protected_path.parts)
        if (output_parts[:len(corpus_parts)] == corpus_parts or
                corpus_parts[:len(output_parts)] == output_parts):
            raise TrainingAdmissionError("registered run output overlaps a corpus artifact root")
    path = ROOT.joinpath(*relative.parts)
    artifacts_root = (ROOT / "artifacts").resolve(strict=True)
    lexical = path.parent
    while lexical != ROOT:
        if lexical.is_symlink() or (lexical.exists() and
                (getattr(lexical.lstat(), "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))):
            raise TrainingAdmissionError("registered output path traverses a reparse point")
        lexical = lexical.parent
    resolved_parent = path.parent.resolve(strict=True)
    if artifacts_root not in resolved_parent.parents and resolved_parent != artifacts_root:
        raise TrainingAdmissionError("registered output path escaped project artifacts")
    cursor = resolved_parent
    while cursor != artifacts_root:
        if cursor.is_symlink() or (getattr(cursor.lstat(), "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)):
            raise TrainingAdmissionError("registered output path traverses a reparse point")
        cursor = cursor.parent
    if path.exists() or path.is_symlink():
        raise TrainingAdmissionError("registered output path must be fresh")
    return path


def _assert_model_and_optimizer_gpu(torch_module: Any, model: Any, optimizer: Any,
                                   train_features: Any, train_labels: Any,
                                   validation_features: Any, validation_labels: Any) -> None:
    assert_all_tensors_on_gpu(torch_module, tuple(model.parameters()), tuple(model.buffers()), optimizer.state,
                              train_features, train_labels, validation_features, validation_labels)
    for parameter in model.parameters():
        if not parameter.is_cuda or str(parameter.device) != DEVICE:
            raise TrainingAdmissionError("model parameter is not on HIP cuda:0")
        if parameter.grad is not None and (not parameter.grad.is_cuda or str(parameter.grad.device) != DEVICE):
            raise TrainingAdmissionError("model gradient is not on HIP cuda:0")


def _assert_training_outputs_gpu(torch_module: Any, logits: Any, loss: Any) -> None:
    """Keep forward outputs and scalar objectives on the selected HIP device."""
    assert_all_tensors_on_gpu(torch_module, logits, loss)


def _atomic_checkpoint(torch_module: Any, state: dict[str, Any], path: Path) -> None:
    buffer = io.BytesIO()
    torch_module.save(state, buffer)
    _atomic_bytes(path, buffer.getvalue())


def train_registered(output_path: str, *, torch_module: Any | None = None) -> Path:
    """Run only a registered fixed one-shot recipe; current registry rejects it.

    This entry point is intentionally not invoked by package tests. Construction
    of the model and optimizer occurs only after source, review, corpus, output,
    and RX 6600 XT checks have all passed.
    """
    if torch_module is None:
        import torch as torch_module
    admitted = preflight(torch_module)
    protected_roots = tuple(admitted.registration[key]["artifact_root"] for key in
                            ("positive_corpus", "positive_v5_component_corpus", "reject_corpus"))
    requested_output = _fixed_output_path(output_path, protected_artifact_roots=protected_roots)
    if requested_output != admitted.output_root:
        raise TrainingAdmissionError("requested output differs from the fixed one-shot registered path")

    recipe = admitted.recipe["bounded_recipe"]
    torch_module.use_deterministic_algorithms(True)
    torch_module.backends.cudnn.deterministic = True
    torch_module.backends.cudnn.benchmark = False
    torch_module.manual_seed(recipe["model_initialization_seed"])
    torch_module.cuda.manual_seed_all(recipe["seed"])

    run_id = str(uuid4())
    source_bundle_sha = _source_bundle_digest(admitted.source_bindings)
    journal = StepBudgetJournal(admitted.output_root, run_id=run_id,
                                maximum_steps=recipe["maximum_optimizer_steps"],
                                recipe_sha256=RECIPE_SHA256,
                                input_bundle_sha256=admitted.pair.input_bundle_sha256, create=True)

    train_features = torch_module.tensor(admitted.pair.train.features, dtype=torch_module.float32, device=admitted.device)
    train_labels = torch_module.tensor(admitted.pair.train.labels, dtype=torch_module.long, device=admitted.device)
    validation_features = torch_module.tensor(admitted.pair.validation.features, dtype=torch_module.float32, device=admitted.device)
    validation_labels = torch_module.tensor(admitted.pair.validation.labels, dtype=torch_module.long, device=admitted.device)
    assert_all_tensors_on_gpu(torch_module, train_features, train_labels, validation_features, validation_labels)

    from ml.ocr.component_ensemble_v5.model import ComponentEnsembleGlyphNet

    model = ComponentEnsembleGlyphNet(seed=recipe["model_initialization_seed"]).to(admitted.device)
    if model.classifier[-1].out_features != 14:
        raise TrainingAdmissionError("V5 model output class count differs from protocol")
    if not all(parameter.is_cuda and str(parameter.device) == DEVICE for parameter in model.parameters()):
        raise TrainingAdmissionError("V5 model initialization did not land on HIP cuda:0")
    optimizer = torch_module.optim.AdamW(model.parameters(), lr=recipe["learning_rate"],
                                         weight_decay=recipe["weight_decay"], capturable=True,
                                         foreach=False, fused=False)
    _assert_model_and_optimizer_gpu(torch_module, model, optimizer, train_features, train_labels,
                                   validation_features, validation_labels)

    generator = torch_module.Generator(device=admitted.device)
    generator.manual_seed(recipe["seed"])
    loss_fn = torch_module.nn.CrossEntropyLoss()
    losses: list[float] = []
    for _ in range(recipe["maximum_optimizer_steps"]):
        step = journal.begin_step()
        indices = torch_module.randint(train_features.shape[0], (recipe["batch_size"],),
                                       device=admitted.device, generator=generator)
        batch_features = train_features.index_select(0, indices)
        batch_labels = train_labels.index_select(0, indices)
        _assert_model_and_optimizer_gpu(torch_module, model, optimizer, train_features, train_labels,
                                       validation_features, validation_labels)
        optimizer.zero_grad(set_to_none=True)
        logits = model(batch_features)
        loss = loss_fn(logits, batch_labels)
        _assert_training_outputs_gpu(torch_module, logits, loss)
        if not bool(torch_module.isfinite(loss).item()):
            raise TrainingAdmissionError("non-finite loss stopped the fixed run")
        loss.backward()
        _assert_model_and_optimizer_gpu(torch_module, model, optimizer, train_features, train_labels,
                                       validation_features, validation_labels)
        optimizer.step()
        _assert_model_and_optimizer_gpu(torch_module, model, optimizer, train_features, train_labels,
                                       validation_features, validation_labels)
        losses.append(float(loss.detach().item()))
        _atomic_checkpoint(torch_module, {"model_state": model.state_dict(), "optimizer_state": optimizer.state_dict(),
                            "completed_steps": step,
                            "recipe_sha256": RECIPE_SHA256, "input_bundle_sha256": admitted.pair.input_bundle_sha256,
                            "source_bundle_sha256": source_bundle_sha}, admitted.output_root / "checkpoint.pt")
        journal.complete_step(step)

    model.eval()
    with torch_module.no_grad():
        validation_logits = model(validation_features)
        validation_loss_tensor = loss_fn(validation_logits, validation_labels)
        _assert_training_outputs_gpu(torch_module, validation_logits, validation_loss_tensor)
        validation_loss = float(validation_loss_tensor.item())
        validation_accuracy = float((validation_logits.argmax(dim=1) == validation_labels).float().mean().item())
    _assert_model_and_optimizer_gpu(torch_module, model, optimizer, train_features, train_labels,
                                   validation_features, validation_labels)
    journal.verify_latest_checkpoint()
    report = {"schema": "graphreader.gpu-numeric-v5-train.report.v1", "run_id": run_id,
              "recipe_sha256": RECIPE_SHA256,
              "registration_core_sha256": registration_core_sha256(admitted.registration),
              "review_receipt_sha256": admitted.registration["review_receipt_sha256"],
              "input_bundle_sha256": admitted.pair.input_bundle_sha256,
              "source_bundle_sha256": source_bundle_sha, "gpu": {"device": DEVICE,
              "name": EXPECTED_GPU_NAME, "architecture": EXPECTED_GPU_ARCH,
              "hip": torch_module.version.hip}, "optimizer": "AdamW",
              "steps_completed": journal.completed_steps, "maximum_steps": RUN_BUDGET_STEPS,
              "checkpoint_sha256": file_sha256(admitted.output_root / "checkpoint.pt"),
              "training_components": int(train_labels.shape[0]),
              "validation_components": int(validation_labels.shape[0]),
              "training_class_counts": {str(index): int((train_labels == index).sum().item()) for index in range(14)},
              "validation_class_counts": {str(index): int((validation_labels == index).sum().item()) for index in range(14)},
              "source_bindings": admitted.pair.source_bindings,
              "loss_start": losses[0], "loss_end": losses[-1], "validation_loss": validation_loss,
              "validation_component_accuracy": validation_accuracy,
              "training_executed": True, "training_approved": False, "production_approved": False,
              "promotion_allowed": False,
              "acceptance": "No model acceptance or promotion gate is registered; validation metrics are diagnostic only."}
    _atomic_bytes(admitted.output_root / "report.json",
                  (json.dumps(report, sort_keys=True, indent=2) + "\n").encode("utf-8"))
    return admitted.output_root / "report.json"
