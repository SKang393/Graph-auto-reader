# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Isolated, preregistered GPU runner; this module registers no candidate."""
from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
import random

import numpy as np
import torch

from ml.markers.training_budget import CANONICAL_LEDGER_PATH, TrainingAuthorization, complete_training_candidate
from ..real_positive_context_v1 import runner_foundation as prior
from ..real_positive_context_v1.checkpoint_writer import write_candidate_checkpoint
from ..real_positive_context_v1.entrypoint import RUNNER_SOURCES as PRIOR_SOURCES
from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import SyntheticLabels, _require_training_gpu
from ..real_positive_context_v1.registered_source import RegisteredMixedSource
from .component_source import ComponentNegativeStore, VIEWS
from .objective import COMPONENT_RETENTION_WEIGHT, preservation_loss
from .schedule import ALGORITHM, BATCHES_PER_EPOCH, BATCH_SIZE, EPOCHS, ROWS_PER_VIEW, SEED, TOTAL_STEPS, ComponentSchedule

REVISION = "marker-center-real-positive-preservation-v1"
COMPONENT_FEATURES = {
    "identity": "b400b41aea707210c1d2b790b816492bd7d367457a0acbec03b26d1429b74257",
    "horizontal": "c55bb3ec591dc59f959cc76e5e4034fa265bc4cd4a7b14a53fd3e62d7fbde749",
    "vertical": "51d54f52346488908b4633cad93c2363e88bfa263af96bcd32f0fbeb55086b50",
    "both": "00929e0badf4a9d051082bde8552b2be5d575626cd77fa8926c3a8ad7c5e8309",
}
COMPONENT_LABEL_SHA256 = "e744ba10fddc380ac6edd71d4e8b94f98de8ae3e35c8c47e2e3f3228d299e536"
COMPONENT_BACKBONE_SHA256 = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
LOCAL = Path("ml/markers/center/real_positive_preservation_v1")
REQUIRED_SOURCES = tuple(dict.fromkeys((*PRIOR_SOURCES, *(LOCAL / item for item in
    ("__init__.py", "component_source.py", "objective.py", "schedule.py", "runner.py", "entrypoint.py", "protocol.json")))))


def expected_component_config() -> dict:
    return {"algorithm": ALGORITHM, "seed": SEED, "batch_size": BATCH_SIZE,
            "rows_per_view": ROWS_PER_VIEW, "batches_per_epoch": BATCHES_PER_EPOCH,
            "epochs": EPOCHS, "weight": COMPONENT_RETENTION_WEIGHT,
            "feature_report_sha256": prior.V37_FEATURE_REPORT_SHA256,
            "feature_backbone_sha256": COMPONENT_BACKBONE_SHA256,
            "label_sha256": COMPONENT_LABEL_SHA256, "feature_sha256_by_view": dict(COMPONENT_FEATURES)}


def _validate_admission(config: dict, authorization: TrainingAuthorization) -> None:
    retention = config.get("component_retention")
    if (config.get("revision") != REVISION or config.get("candidate_id") != "P1" or
            retention != expected_component_config() or type(retention) is not dict or
            type(retention.get("weight")) is not float or
            any(type(retention.get(name)) is not int for name in
                ("seed", "batch_size", "rows_per_view", "batches_per_epoch", "epochs")) or
            config.get("optimizer_steps_expected") != TOTAL_STEPS):
        raise RuntimeError("The preservation recipe is not exactly preregistered")
    supplied = authorization.binding.get("runner_source_paths")
    if not isinstance(supplied, list) or not {p.as_posix() for p in REQUIRED_SOURCES}.issubset(set(supplied)):
        raise RuntimeError("The preservation implementation is absent from the immutable source bundle")
    ledger_path = prior._inside_project(prior.PROJECT_ROOT, prior.PROJECT_ROOT / CANONICAL_LEDGER_PATH)
    ledger = json.loads(ledger_path.read_bytes())
    entries = [entry for entry in ledger.get("revisions", []) if isinstance(entry, dict) and
               entry.get("task") == "marker-center" and entry.get("revision") == REVISION]
    if len(entries) != 1:
        raise RuntimeError("The preservation protocol is not uniquely preregistered")
    entry = entries[0]
    path, expected = entry.get("protocol_path"), entry.get("protocol_sha256")
    if path != (LOCAL / "protocol.json").as_posix() or path not in supplied:
        raise RuntimeError("The admitted preservation protocol is not snapshot-bound")
    protocol = prior._inside_project(prior.PROJECT_ROOT, prior.PROJECT_ROOT / path).read_bytes()
    prior._sha256_payload(protocol, expected, "Preservation protocol")


def _train_step(model, optimizer, source, authorization, journal, component_schedule, stores) -> float:
    config = prior._require_bound_source(authorization, source)
    _validate_admission(config, authorization)
    prior._require_reviewed_schedule(config, source)
    if not isinstance(journal, prior._RunJournal):
        raise RuntimeError("Optimizer steps require the single-use durable run journal")
    step = journal.begin_step(authorization, TOTAL_STEPS)
    if (source.verify_input_binding(config["real_positive_binding_sha256"], prior.V37_FEATURE_REPORT_SHA256) is not True or
            source.verify_model(model, prior.V37_CHECKPOINT_SHA256) is not True):
        raise RuntimeError("Source or frozen V37 identity changed")
    batch = source.next_gpu()
    if batch.plan.step != step or not isinstance(batch.labels, SyntheticLabels) or source.verify_batch(batch) is not True:
        raise RuntimeError("Mixed batch is not the scheduled authenticated input")
    if (not 0 < len(batch.synthetic) <= prior.DRAFT_RECIPE.synthetic_batch_size or
            not 0 < len(batch.real_positive) <= prior.DRAFT_RECIPE.real_positive_batch_size or
            len(batch.labels.values) != len(batch.synthetic)):
        raise ValueError("Mixed batch is outside the fixed recipe")
    component = component_schedule.reserve(step)
    prior.require_gpu_model(model, batch.synthetic, batch.real_positive)
    prior._require_optimizer(model, optimizer)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    losses = preservation_loss(model, batch.synthetic, batch.labels, batch.real_positive,
                               stores[component.view], component.indices)
    if losses.total.device != torch.device("cuda:0") or not torch.isfinite(losses.total):
        raise RuntimeError("Preservation loss is not finite on the required GPU")
    losses.total.backward()
    prior._require_bound_source(authorization, source)
    if (source.verify_batch(batch) is not True or source.verify_model(model, prior.V37_CHECKPOINT_SHA256) is not True or
            source.verify_input_binding(config["real_positive_binding_sha256"], prior.V37_FEATURE_REPORT_SHA256) is not True):
        raise RuntimeError("Verified inputs changed before the optimizer step")
    prior.require_gpu_model(model, batch.synthetic, batch.real_positive, gradients=True)
    prior._require_optimizer(model, optimizer)
    optimizer.step()
    torch.cuda.synchronize(torch.device("cuda:0"))
    prior._require_optimizer(model, optimizer)
    journal.commit_step(step)
    source.commit(batch.plan)
    component_schedule.commit(component)
    return float(losses.total.detach().cpu())


def run_training(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer,
                 source: RegisteredMixedSource, authorization: TrainingAuthorization) -> dict:
    """Execute only after independent preregistration and canonical acquisition.

    Caller must provide the already-admitted source, model and optimizer. This
    module deliberately has no CLI, default source or automatic authorization.
    """
    config = prior._require_bound_source(authorization, source)
    _validate_admission(config, authorization)
    prior._require_reviewed_schedule(config, source)
    if type(source) is not RegisteredMixedSource or not isinstance(model, RealPositiveCenterNet):
        raise RuntimeError("Canonical source and V37 residual model are required")
    _require_training_gpu(*model.parameters(), *model.buffers())
    prior._require_fresh_state(model, optimizer)
    if source.verify_model(model, prior.V37_CHECKPOINT_SHA256) is not True:
        raise RuntimeError("Frozen V37 checkpoint is unverified")
    stores = {view: ComponentNegativeStore.open_authenticated(view) for view in VIEWS}
    if any(store.fixture or store.view != view or store.count != ROWS_PER_VIEW for view, store in stores.items()):
        raise RuntimeError("Component negatives are not the authenticated fixed populations")
    random.seed(prior.DRAFT_RECIPE.seed)
    np.random.seed(prior.DRAFT_RECIPE.seed)
    torch.manual_seed(prior.DRAFT_RECIPE.seed)
    torch.use_deterministic_algorithms(True)
    schedule = ComponentSchedule()
    journal = prior._RunJournal.claim(authorization, TOTAL_STEPS)
    total = 0.0
    try:
        if source.begin_fixed_run(prior.DRAFT_RECIPE, authorization) is not True:
            raise RuntimeError("The mixed-source schedule did not start")
        for _ in range(TOTAL_STEPS):
            total += _train_step(model, optimizer, source, authorization, journal, schedule, stores)
        if journal.completed != TOTAL_STEPS or journal.pending is not None or schedule.completed != TOTAL_STEPS or source.schedule.completed_steps != TOTAL_STEPS:
            raise RuntimeError("A completed schedule or optimizer count differs")
        source_path, destination = config.get("source_checkpoint_path"), config.get("candidate_checkpoint_path")
        if not isinstance(source_path, str) or not isinstance(destination, str) or Path(source_path).is_absolute() or Path(destination).is_absolute():
            raise RuntimeError("Admitted checkpoint paths are required")
        receipt = write_candidate_checkpoint(model, base_path=prior.PROJECT_ROOT / source_path,
            destination=prior.PROJECT_ROOT / destination, revision=REVISION, candidate_id="P1")
    except BaseException as error:
        journal.close()
        failure = {"schema": "graphreader.preservation-run-outcome.v1", "status": "failed_training_incomplete",
                   "expected_steps": TOTAL_STEPS, "completed_steps": journal.completed, "pending_step": journal.pending,
                   "resume_authorized": False, "exception_type": type(error).__name__,
                   "exception_sha256": sha256(str(error).encode("utf-8")).hexdigest(),
                   "production_approval": False, "release_eligible": False}
        try:
            encoded = prior._write_new_durable(authorization.directory / "optimizer-run-outcome.json", failure)
            complete_training_candidate(authorization, status=failure["status"], report_sha256=sha256(encoded).hexdigest())
        except Exception as finalization_error:
            error.add_note("Candidate failure finalization also failed: " + type(finalization_error).__name__)
        raise
    journal.close()
    result = {"schema": "graphreader.preservation-run-outcome.v1", "status": "training_complete_unapproved",
              "expected_steps": TOTAL_STEPS, "completed_steps": journal.completed, "resume_authorized": False,
              "mean_loss": total / TOTAL_STEPS, "checkpoint_path": receipt.relative_path, "checkpoint_sha256": receipt.sha256,
              "production_approval": False, "release_eligible": False}
    encoded = prior._write_new_durable(authorization.directory / "optimizer-run-outcome.json", result)
    complete_training_candidate(authorization, status=result["status"], report_sha256=sha256(encoded).hexdigest())
    return result
