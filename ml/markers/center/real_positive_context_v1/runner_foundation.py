# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-only mixed-supervision prerequisites with a fail-closed training seam.

An optimizer step requires a reviewed producer for real positive crops and
their runtime OCR/artifact planes, an exact preregistered recipe, and canonical
ledger admission. No such producer or admitted candidate exists in this module.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
from hashlib import sha256
from io import BytesIO
import inspect
import json
import os
from pathlib import Path
import random
from threading import Lock

import numpy as np
import torch
from torch import Tensor

from ml.markers.gate_seal import source_bundle_sha256, verify_bound_source_snapshot
from ml.markers.training_budget import CANONICAL_LEDGER_PATH, TrainingAuthorization, complete_training_candidate
from .model import OPERATING_THRESHOLD, V37_CHECKPOINT_SHA256, RealPositiveCenterNet
from .objective import SyntheticLabels, _require_training_gpu, mixed_confidence_loss
from .checkpoint_writer import write_candidate_checkpoint

V37_FEATURE_REPORT = Path("artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json")
V37_FEATURE_REPORT_SHA256 = "284ebfa305b7b827c59383711122b1668398460ddde2489007252335ee55f500"
V37_ORIGINAL_MODEL_SHA256 = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
V37_TRANSFORMS = ("identity", "horizontal", "vertical", "both")
V37_BASE_ROWS = {"component": 35838, "family": 9053, "coverage": 42747,
                 "crowded": 182614, "crowded_mined_negative": 6366}
PROJECT_ROOT = Path(__file__).resolve().parents[4]


@dataclass(frozen=True)
class DraftRecipe:
    """Unapproved proposal, not a registered training configuration."""

    seed: int = 20261002
    synthetic_batch_size: int = 128
    real_positive_batch_size: int = 16
    real_positive_weight: float = 0.25
    learning_rate: float = 0.0003
    weight_decay: float = 0.0001
    epochs: int = 8
    threshold: float = OPERATING_THRESHOLD


DRAFT_RECIPE = DraftRecipe()


def _sha256_payload(raw: bytes, expected: str, description: str) -> None:
    if (type(expected) is not str or len(expected) != 64 or
            any(character not in "0123456789abcdef" for character in expected) or
            sha256(raw).hexdigest() != expected):
        raise ValueError(f"{description} is missing or does not match its bound SHA-256")


def _inside_project(project_root: Path, path: Path) -> Path:
    root, resolved = project_root.resolve(strict=True), path.resolve(strict=True)
    if resolved == root or not resolved.is_relative_to(root):
        raise ValueError("The bound input must remain inside the project folder")
    if not resolved.is_file():
        raise ValueError("The bound input is not a regular file")
    return resolved


def _v37_report(raw: bytes, expected_sha256: str) -> dict:
    """Verify the existing report structure without reading any dataset."""
    _sha256_payload(raw, expected_sha256, "V37 synthetic training report")
    try:
        value = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise ValueError("V37 training report is not valid UTF-8 JSON") from error
    if (not isinstance(value, dict) or value.get("status") != "fixed_reflection_training_features_measured" or
            value.get("feature_width") != 768 or value.get("transforms") != list(V37_TRANSFORMS) or
            value.get("original_training_rows") != 276618 or value.get("training_rows") != 1106472 or
            value.get("all_original_rows_and_labels_retained") is not True or
            value.get("model_parameters_and_buffers_unchanged") is not True or
            value.get("raw_reconstruction_max_absolute_error") != 0 or
            value.get("model", {}).get("sha256") != V37_ORIGINAL_MODEL_SHA256 or
            any(value.get(key) != 0 for key in ("optimizer_steps", "private_reads", "sealed_reads", "development_tensor_reads"))):
        raise ValueError("V37 synthetic report does not describe the frozen train-only population")
    groups = value.get("groups")
    expected = [f"{scope}:{transform}" for scope in V37_BASE_ROWS for transform in V37_TRANSFORMS]
    if (not isinstance(groups, list) or [item.get("scope") for item in groups if isinstance(item, dict)] != expected or
            any(group.get("rows") != V37_BASE_ROWS[group["scope"].split(":")[0]] for group in groups)):
        raise ValueError("V37 synthetic group order or row counts changed")
    return value


def load_bound_synthetic_labels(project_root: Path, data_root: Path, scope: str) -> SyntheticLabels:
    """Load only a label array named by the independently fixed V37 report.

    Synthetic label integrity follows the frozen report and file hashes. It
    does not authenticate any real positive crop or approve this draft recipe.
    """
    data_root = data_root.resolve(strict=True)
    project_root = project_root.resolve(strict=True)
    if not data_root.is_relative_to(project_root):
        raise ValueError("Synthetic data root must be a project-local directory")
    report_path = _inside_project(project_root, data_root / V37_FEATURE_REPORT)
    report = _v37_report(report_path.read_bytes(), V37_FEATURE_REPORT_SHA256)
    if scope not in V37_BASE_ROWS:
        raise ValueError("Unknown V37 synthetic training scope")
    group = next(item for item in report["groups"] if item["scope"] == f"{scope}:identity")
    labels_ref = group.get("labels")
    if not isinstance(labels_ref, dict) or not isinstance(labels_ref.get("path"), str):
        raise ValueError("Frozen group has no label-file binding")
    label_path = _inside_project(project_root, data_root / labels_ref["path"])
    label_raw = label_path.read_bytes()
    _sha256_payload(label_raw, labels_ref.get("sha256"), "Synthetic label array")
    try:
        labels = np.load(BytesIO(label_raw), allow_pickle=False)
    except (OSError, ValueError) as error:
        raise ValueError("Synthetic labels are not an allowed NumPy array") from error
    if (labels.dtype != np.float32 or labels.shape != (group["rows"],) or
            not np.isfinite(labels).all() or not np.isin(labels, (0, 1)).all() or
            int((labels == 1).sum()) != group.get("positive") or
            int((labels == 0).sum()) != group.get("negative")):
        raise ValueError("Synthetic label values or counts differ from the bound train group")
    values = labels.astype(np.uint8).tobytes()
    return SyntheticLabels(values, sha256(values).hexdigest())


def require_gpu_model(model: RealPositiveCenterNet, *batches: Tensor, gradients: bool = False) -> None:
    """Validate all model/batch/gradient residency before any future step."""
    if not isinstance(model, RealPositiveCenterNet) or not batches:
        raise TypeError("The registered model and at least one batch are required")
    _require_training_gpu(*batches)
    device = torch.device("cuda:0")
    if (any(parameter.device != device for parameter in model.parameters()) or
            any(buffer.device != device for buffer in model.buffers())):
        raise RuntimeError("All parameters and buffers must remain on the RX 6600 XT")
    if (model.local.training or any(parameter.requires_grad for parameter in model.local.parameters()) or
            any(not parameter.requires_grad for parameter in model.residual.parameters())):
        raise RuntimeError("The V37 model must be frozen and only the residual may train")
    for batch in batches:
        if (batch.dtype != torch.float32 or batch.ndim != 4 or batch.shape[1:] != (3, 33, 33) or
                batch.shape[0] == 0 or not torch.isfinite(batch).all() or
                (batch < 0).any() or (batch > 1).any()):
            raise ValueError("Batches must contain finite GPU float32 runtime-channel patches")
    if gradients and any(parameter.grad is None or parameter.grad.device != device or
                         not torch.isfinite(parameter.grad).all() for parameter in model.residual.parameters()):
        raise RuntimeError("Every residual gradient must be finite and on the RX 6600 XT")


def synthetic_gradient_probe(model: RealPositiveCenterNet, synthetic: Tensor, labels: SyntheticLabels,
                             positive_control: Tensor) -> float:
    """Exercise GPU gradient wiring with synthetic controls, without an optimizer.

    Both tensor inputs must be generated synthetic test fixtures. This helper
    cannot authenticate, load, classify, or authorize real-project data.
    """
    require_gpu_model(model, synthetic, positive_control)
    if len(labels.values) != len(synthetic) or len(positive_control) == 0:
        raise ValueError("Synthetic probe inputs and labels have incompatible lengths")
    model.zero_grad(set_to_none=True)
    model.train()
    logits, _ = model.logits_and_geometry(synthetic)
    controls, _ = model.logits_and_geometry(positive_control)
    loss = mixed_confidence_loss(logits, labels, controls, real_weight=DRAFT_RECIPE.real_positive_weight)
    loss.backward()
    require_gpu_model(model, synthetic, positive_control, gradients=True)
    return float(loss.detach().cpu())


def _require_bound_source(authorization: TrainingAuthorization, source: object) -> dict:
    """Require the existing canonical admission and a snapshot-bound adapter.

    The future adapter is responsible for checking that each positive was
    produced by NormalizedMarkerProposalPreprocessor for a reviewed labeled
    center and that synthetic labels match their pinned source.  This function
    binds the adapter code, but does not assert that unreviewed code is valid.
    """
    if (not isinstance(authorization, TrainingAuthorization) or authorization.repo_root is None or
            authorization.snapshot_path is None or source is None):
        raise RuntimeError("BLOCKED: authenticated real-positive source and registered candidate are missing")
    root = authorization.repo_root.resolve(strict=True)
    if root != PROJECT_ROOT:
        raise RuntimeError("The candidate must be admitted from this project folder")
    binding = authorization.binding
    candidate = binding.get("candidate_id")
    revision = binding.get("revision")
    if (not isinstance(candidate, str) or not candidate.startswith("P") or
            not candidate[1:].isdigit() or not isinstance(revision, str) or
            not revision or any(char in revision for char in "/\\")):
        raise RuntimeError("Canonical candidate identity is invalid")
    expected_directory = root / "ml/markers/training-seals/marker-center" / revision / candidate
    if authorization.directory.resolve() != expected_directory.resolve():
        raise RuntimeError("The candidate admission is not in the canonical seal directory")
    snapshot = _inside_project(root, authorization.snapshot_path)
    opened = _inside_project(root, authorization.opened_path)
    if snapshot != (expected_directory / "source-snapshot.json").resolve() or opened != (expected_directory / "opened.json").resolve():
        raise RuntimeError("The candidate admission paths are not canonical")
    if (authorization.directory.joinpath("void.json").exists() or
            authorization.directory.joinpath("result.json").exists() or
            authorization.consumed_path.exists()):
        raise RuntimeError("Training candidate is no longer open for a train-only run")
    try:
        opened_value = json.loads(opened.read_bytes())
    except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError("Canonical training admission is unreadable") from error
    if (not isinstance(opened_value, dict) or opened_value.get("status") != "opened" or
            opened_value.get("binding") != authorization.binding or
            authorization.binding.get("task") != "marker-center"):
        raise RuntimeError("Canonical training admission does not match the supplied candidate")
    ledger_raw = _inside_project(root, root / CANONICAL_LEDGER_PATH).read_bytes()
    _sha256_payload(ledger_raw, binding.get("training_budget_ledger_sha256"), "Canonical training ledger")
    try:
        ledger = json.loads(ledger_raw)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError("Canonical training ledger is invalid") from error
    entries = [entry for entry in ledger.get("revisions", []) if isinstance(entry, dict) and
               entry.get("task") == "marker-center" and entry.get("revision") == revision]
    if len(entries) != 1:
        raise RuntimeError("The candidate is not uniquely preregistered")
    entry = entries[0]
    if (entry.get("execution_authorized") is not True or entry.get("authorized_candidate_id") != candidate or
            entry.get("status") != f"candidate_{int(candidate[1:])}_preregistered" or
            entry.get("preregistered_candidate_ids") != [candidate] or
            candidate in entry.get("consumed_candidate_ids", []) or
            entry.get("sealed_runs_authorized") != 0):
        raise RuntimeError("The canonical ledger does not authorize this train-only candidate")
    verify_bound_source_snapshot(root, snapshot, authorization.binding.get("source_snapshot_sha256"))
    snapshot_value = json.loads(snapshot.read_bytes())
    if (snapshot_value.get("identity") != {"task": "marker-center", "revision": revision, "candidate_id": candidate} or
            snapshot_value.get("preregistered_ledger_entry") != entry):
        raise RuntimeError("The source snapshot does not match the canonical candidate")
    try:
        source_file = inspect.getsourcefile(type(source))
    except (TypeError, OSError) as error:
        raise RuntimeError("The source adapter has no inspectable project source") from error
    if not source_file:
        raise RuntimeError("The source adapter must have a project-local source file")
    relative = _inside_project(root, Path(source_file)).relative_to(root).as_posix()
    source_paths = authorization.binding.get("runner_source_paths")
    snapshot_rows = snapshot_value.get("sources")
    if (not isinstance(source_paths, list) or not source_paths or
            any(not isinstance(path, str) for path in source_paths) or
            relative not in source_paths or not isinstance(snapshot_rows, list) or
            not any(isinstance(row, dict) and row.get("path") == relative for row in snapshot_rows)):
        raise RuntimeError("The source adapter is not part of the preregistered source bundle")
    if source_bundle_sha256(root, [Path(path) for path in source_paths]) != binding.get("runner_source_bundle_sha256"):
        raise RuntimeError("The training source bundle differs from the canonical admission")
    config_path = authorization.binding.get("candidate_config_path")
    if not isinstance(config_path, str):
        raise RuntimeError("The candidate has no bound configuration")
    paths, hashes = entry.get("candidate_config_paths"), entry.get("candidate_config_sha256")
    if (not isinstance(paths, dict) or not isinstance(hashes, dict) or
            paths.get(candidate) != config_path or hashes.get(candidate) != binding.get("candidate_config_sha256")):
        raise RuntimeError("The candidate configuration is not bound by the canonical ledger")
    config_raw = _inside_project(root, root / config_path).read_bytes()
    _sha256_payload(config_raw, authorization.binding.get("candidate_config_sha256"), "Candidate configuration")
    try:
        config = json.loads(config_raw)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError("The candidate configuration is invalid") from error
    if (not isinstance(config, dict) or config.get("recipe") != asdict(DRAFT_RECIPE) or
            config.get("task") != authorization.binding.get("task") or
            config.get("revision") != authorization.binding.get("revision") or
            config.get("candidate_id") != authorization.binding.get("candidate_id") or
            type(config.get("optimizer_steps_expected")) is not int or
            not 0 < config["optimizer_steps_expected"] <= 1_000_000 or
            config.get("optimizer_steps_expected") != entry.get("optimizer_steps_expected") or
            config.get("expected_runner_source_bundle_sha256") != binding.get("runner_source_bundle_sha256") or
            config.get("synthetic_feature_report_sha256") != V37_FEATURE_REPORT_SHA256 or
            config.get("source_checkpoint_sha256") != V37_CHECKPOINT_SHA256 or
            not isinstance(config.get("real_positive_binding_sha256"), str) or
            len(config["real_positive_binding_sha256"]) != 64 or
            any(char not in "0123456789abcdef" for char in config["real_positive_binding_sha256"]) or
            config.get("production_approval") is not False or
            config.get("sealed_runs_authorized") != 0):
        raise RuntimeError("Candidate configuration is not the reviewed fixed train-only recipe")
    return config


def _require_optimizer(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer) -> None:
    if not isinstance(optimizer, torch.optim.AdamW):
        raise TypeError("The fixed recipe requires AdamW")
    if any(group.get("lr") != DRAFT_RECIPE.learning_rate or
           group.get("weight_decay") != DRAFT_RECIPE.weight_decay or
           group.get("capturable") is not True for group in optimizer.param_groups):
        raise RuntimeError("The optimizer differs from the GPU-only fixed AdamW recipe")
    expected = {id(parameter) for parameter in model.residual.parameters()}
    actual = [parameter for group in optimizer.param_groups for parameter in group["params"]]
    if len(actual) != len(expected) or {id(parameter) for parameter in actual} != expected:
        raise RuntimeError("The optimizer must own only the confidence residual parameters")
    if any(isinstance(value, Tensor) and value.device != torch.device("cuda:0")
           for state in optimizer.state.values() for value in state.values()):
        raise RuntimeError("Optimizer state tensors must remain on the RX 6600 XT")


def _require_fresh_state(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer) -> None:
    """A candidate is a fresh run, not a silent checkpoint continuation."""
    _require_optimizer(model, optimizer)
    if optimizer.state or any(parameter.grad is not None for parameter in model.parameters()):
        raise RuntimeError("Fresh training requires empty optimizer state and no existing gradients")
    if any(torch.count_nonzero(parameter).item() != 0 for parameter in model.residual.parameters()):
        raise RuntimeError("Fresh training requires the zero-initialized confidence residual")


def _require_reviewed_schedule(config: dict, source: object) -> None:
    """Require the admitted source's independently constructed fixed schedule."""
    from .registered_source import RegisteredMixedSource
    from .schedule import FixedSchedule
    if (type(source) is not RegisteredMixedSource or source._closed or source._config != config or
            type(source.schedule) is not FixedSchedule or
            source.schedule.total_steps != config.get("optimizer_steps_expected") or
            not 0 <= source.schedule.completed_steps <= source.schedule.total_steps):
        raise RuntimeError("BLOCKED: authenticated source and independently validated per-epoch schedule are required")


def _write_new_durable(path: Path, payload: dict) -> bytes:
    encoded = (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    with path.open("xb") as stream:
        stream.write(encoded)
        stream.flush()
        os.fsync(stream.fileno())
    return encoded


class _RunJournal:
    """One single-use optimizer run with crash-ambiguous steps blocked.

    The durable run intent is exclusive and remains after completion or a
    failure, including canonical void/reopen operations. A crash after an
    intent can never trigger silent replay because a new claim is refused.
    """

    def __init__(self, authorization: TrainingAuthorization, expected: int) -> None:
        self.authorization = authorization
        self.expected = expected
        self.completed = 0
        self.pending: int | None = None
        self.closed = False
        self._lock = Lock()
        self._journal = authorization.directory / "optimizer-journal.jsonl"
        self._intent = authorization.directory / "optimizer-run-intent.json"
        self._intent_sha256: str | None = None

    @classmethod
    def claim(cls, authorization: TrainingAuthorization, expected: int) -> "_RunJournal":
        if type(expected) is not int or not 0 < expected <= 1_000_000:
            raise ValueError("A finite preregistered optimizer step count is required")
        journal = cls(authorization, expected)
        intent = _write_new_durable(journal._intent, {
            "schema": "graphreader.optimizer-run-intent.v1", "expected_steps": expected,
            "binding_sha256": sha256(json.dumps(authorization.binding, sort_keys=True,
                                                separators=(",", ":")).encode("utf-8")).hexdigest(),
            "resume_authorized": False,
        })
        _write_new_durable(journal._journal, {"event": "run_opened", "expected_steps": expected})
        journal._intent_sha256 = sha256(intent).hexdigest()
        return journal

    def _append(self, payload: dict) -> None:
        encoded = (json.dumps(payload, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        descriptor = os.open(self._journal, os.O_WRONLY | os.O_APPEND)
        with os.fdopen(descriptor, "ab") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())

    def begin_step(self, authorization: TrainingAuthorization, expected: int) -> int:
        with self._lock:
            if (self.closed or authorization is not self.authorization or expected != self.expected or
                    self.pending is not None or self.completed >= self.expected):
                raise RuntimeError("Optimizer step is outside the single-use admitted run")
            if (self._intent_sha256 is None or not self._intent.is_file() or not self._journal.is_file() or
                    sha256(self._intent.read_bytes()).hexdigest() != self._intent_sha256):
                raise RuntimeError("The durable optimizer run intent is missing or changed")
            self.pending = self.completed + 1
            self._append({"event": "step_intent", "step": self.pending})
            return self.pending

    def commit_step(self, step: int) -> None:
        with self._lock:
            if self.closed or self.pending != step or step != self.completed + 1:
                raise RuntimeError("Optimizer step accounting is inconsistent")
            self._append({"event": "step_completed", "step": step})
            self.completed = step
            self.pending = None

    def close(self) -> None:
        with self._lock:
            self.closed = True


def _train_one_verified_batch(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer,
                              source: object, authorization: TrainingAuthorization,
                              journal: _RunJournal) -> float:
    """Train one admitted batch from a separately reviewed bound adapter.

    The admitted source must provide a schedule-bound GPU batch and verify its
    actual values before forward and again before the optimizer step.
    """
    config = _require_bound_source(authorization, source)
    _require_reviewed_schedule(config, source)
    if not isinstance(journal, _RunJournal):
        raise RuntimeError("Optimizer steps require an active single-use run journal")
    step = journal.begin_step(authorization, config["optimizer_steps_expected"])
    if (not callable(getattr(source, "next_gpu", None)) or
            not callable(getattr(source, "verify_batch", None)) or
            not callable(getattr(source, "commit", None)) or
            not callable(getattr(source, "verify_model", None)) or
            not callable(getattr(source, "verify_input_binding", None))):
        raise RuntimeError("The admitted source lacks the verified crop adapter")
    if source.verify_input_binding(config["real_positive_binding_sha256"], V37_FEATURE_REPORT_SHA256) is not True:
        raise RuntimeError("The candidate synthetic and real inputs are not bound to its fixed configuration")
    if source.verify_model(model, V37_CHECKPOINT_SHA256) is not True:
        raise RuntimeError("The retained V37 checkpoint is unverified")
    batch = source.next_gpu()
    if getattr(getattr(batch, "plan", None), "step", None) != step:
        raise RuntimeError("The scheduled batch does not match the journal step")
    synthetic, labels, real_positives = batch.synthetic, batch.labels, batch.real_positive
    if not isinstance(labels, SyntheticLabels) or source.verify_batch(batch) is not True:
        raise RuntimeError("Synthetic labels or real positive-center provenance are unverified")
    require_gpu_model(model, synthetic, real_positives)
    if (not 0 < len(synthetic) <= DRAFT_RECIPE.synthetic_batch_size or
            not 0 < len(real_positives) <= DRAFT_RECIPE.real_positive_batch_size or
            len(labels.values) != len(synthetic)):
        raise ValueError("The verified batch exceeds the registered fixed recipe")
    _require_optimizer(model, optimizer)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    synthetic_logits, _ = model.logits_and_geometry(synthetic)
    real_logits, _ = model.logits_and_geometry(real_positives)
    loss = mixed_confidence_loss(synthetic_logits, labels, real_logits,
                                 real_weight=DRAFT_RECIPE.real_positive_weight)
    if loss.device != torch.device("cuda:0") or not torch.isfinite(loss):
        raise RuntimeError("The GPU training loss is invalid")
    loss.backward()
    _require_bound_source(authorization, source)
    if (source.verify_batch(batch) is not True or source.verify_model(model, V37_CHECKPOINT_SHA256) is not True or
            source.verify_input_binding(config["real_positive_binding_sha256"], V37_FEATURE_REPORT_SHA256) is not True):
        raise RuntimeError("Verified source data changed before the optimizer step")
    _require_bound_source(authorization, source)
    require_gpu_model(model, synthetic, real_positives, gradients=True)
    _require_optimizer(model, optimizer)
    optimizer.step()
    torch.cuda.synchronize(torch.device("cuda:0"))
    _require_optimizer(model, optimizer)
    journal.commit_step(step)
    source.commit(batch.plan)
    return float(loss.detach().cpu())


def run_training(model: RealPositiveCenterNet | None = None, optimizer: torch.optim.Optimizer | None = None,
                 source: object | None = None, authorization: TrainingAuthorization | None = None) -> dict:
    """Execute a fixed admitted run when a real verified source is available.

    No default source or canonical admission exists in this foundation. A
    later separately reviewed adapter must bind the fixed candidate config and
    source snapshot, deterministic iteration, and real-center provenance.
    """
    config = _require_bound_source(authorization, source)  # type: ignore[arg-type]
    if not isinstance(model, RealPositiveCenterNet) or not isinstance(optimizer, torch.optim.AdamW):
        raise RuntimeError("The admitted GPU model and optimizer are missing")
    _require_training_gpu()
    device = torch.device("cuda:0")
    if (any(parameter.device != device for parameter in model.parameters()) or
            any(buffer.device != device for buffer in model.buffers())):
        raise RuntimeError("All model state must remain on the RX 6600 XT")
    _require_optimizer(model, optimizer)
    if (not callable(getattr(source, "verify_model", None)) or
            source.verify_model(model, V37_CHECKPOINT_SHA256) is not True):
        raise RuntimeError("The retained V37 checkpoint is unverified")
    _require_fresh_state(model, optimizer)
    _require_reviewed_schedule(config, source)
    if not callable(getattr(source, "begin_fixed_run", None)):
        raise RuntimeError("The admitted source cannot initialize deterministic ordering")
    random.seed(DRAFT_RECIPE.seed)
    np.random.seed(DRAFT_RECIPE.seed)
    torch.manual_seed(DRAFT_RECIPE.seed)
    torch.use_deterministic_algorithms(True)
    steps = config["optimizer_steps_expected"]
    journal = _RunJournal.claim(authorization, steps)  # type: ignore[arg-type]
    total_loss = 0.0
    try:
        if source.begin_fixed_run(DRAFT_RECIPE, authorization) is not True:
            raise RuntimeError("The source did not confirm its fixed training split and ordering")
        for _ in range(steps):
            total_loss += _train_one_verified_batch(model, optimizer, source, authorization, journal)  # type: ignore[arg-type]
        if journal.completed != steps or journal.pending is not None:
            raise RuntimeError("The completed optimizer step count differs from the admitted schedule")
        source_path = config.get("source_checkpoint_path")
        destination_path = config.get("candidate_checkpoint_path")
        if (not isinstance(source_path, str) or not isinstance(destination_path, str) or
                Path(source_path).is_absolute() or Path(destination_path).is_absolute()):
            raise RuntimeError("Admitted source and destination checkpoint paths are required")
        receipt = write_candidate_checkpoint(
            model, base_path=PROJECT_ROOT / source_path, destination=PROJECT_ROOT / destination_path,
            revision=authorization.binding["revision"], candidate_id=authorization.binding["candidate_id"])
    except BaseException as error:
        journal.close()
        failure = {"schema": "graphreader.optimizer-run-outcome.v1", "status": "failed_training_incomplete",
                   "expected_steps": steps, "completed_steps": journal.completed,
                   "pending_step": journal.pending, "resume_authorized": False,
                   "exception_type": type(error).__name__,
                   "exception_sha256": sha256(str(error).encode("utf-8")).hexdigest(),
                   "production_approval": False, "release_eligible": False}
        try:
            encoded = _write_new_durable(authorization.directory / "optimizer-run-outcome.json", failure)  # type: ignore[union-attr]
            complete_training_candidate(authorization, status=failure["status"], report_sha256=sha256(encoded).hexdigest())  # type: ignore[arg-type]
        except Exception as finalization_error:
            error.add_note(f"Candidate failure finalization also failed: {type(finalization_error).__name__}")
        raise
    journal.close()
    result = {"schema": "graphreader.optimizer-run-outcome.v1", "status": "training_complete_unapproved",
              "expected_steps": steps, "completed_steps": journal.completed, "resume_authorized": False,
              "mean_loss": total_loss / steps, "checkpoint_path": receipt.relative_path,
              "checkpoint_sha256": receipt.sha256, "production_approval": False, "release_eligible": False}
    encoded = _write_new_durable(authorization.directory / "optimizer-run-outcome.json", result)  # type: ignore[union-attr]
    complete_training_candidate(authorization, status=result["status"], report_sha256=sha256(encoded).hexdigest())  # type: ignore[arg-type]
    return result
