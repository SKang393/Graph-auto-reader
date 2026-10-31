# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered GPU runner; no CLI, source acquisition, or authorization."""
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
from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import SyntheticLabels, _require_training_gpu
from ..real_positive_context_v1.registered_source import RegisteredMixedSource
from ..real_positive_preservation_v1 import runner as preservation
from ..real_positive_preservation_v1.component_source import ComponentNegativeStore, VIEWS
from ..real_positive_preservation_v1.objective import preservation_loss
from ..real_positive_preservation_v1.schedule import ComponentSchedule, ROWS_PER_VIEW, TOTAL_STEPS, BATCHES_PER_EPOCH, EPOCHS
from .objective import directional_penalties
from .schedule import (ALGORITHM, BATCH_SIZE, COMPONENT_ROWS, FAMILY_ROWS, SEED,
                       DirectionalSchedule)
from .source import SyntheticTrainFeatures

REVISION = "marker-center-preservation-bidirectional-v1"
LOCAL = Path("ml/markers/center/preservation_bidirectional_v1")
PROTOCOL = LOCAL / "protocol.json"  # Intentionally absent until a future reviewed preregistration.
REQUIRED_SOURCES = tuple(dict.fromkeys((*preservation.REQUIRED_SOURCES, *(LOCAL / item for item in
    ("__init__.py", "source.py", "objective.py", "schedule.py", "runner.py", "protocol.json")))))
COMPONENT_LABELS = "e744ba10fddc380ac6edd71d4e8b94f98de8ae3e35c8c47e2e3f3228d299e536"
FAMILY_LABELS = "75fc6d9cf16f4064ef98330226acf52a684311d124d2516e0c335f286029bd9a"
FAMILY_FEATURES = {
    "identity": "c6026d49a76adfefde6a490324852e972ced51fa60da99d5a1d1e13993352f91",
    "horizontal": "8b9e3f48282af730ae89878cc9c466b76902bec873241b78164550dd461b9673",
    "vertical": "5287e280248de727af9e774900e2fc8d682dae120423477c11cdc146535a871e",
    "both": "3245c14a7adc491cf4de2c979828c733eb26b335bf6f30da9903ee32dd38e82e",
}


def expected_directional_config() -> dict:
    return {"algorithm": ALGORITHM, "seed": SEED, "batch_size": BATCH_SIZE,
            "epochs": EPOCHS, "batches_per_epoch": BATCHES_PER_EPOCH, "total_steps": TOTAL_STEPS, "views": list(VIEWS),
            "component_rows_per_view": COMPONENT_ROWS, "family_rows_per_view": FAMILY_ROWS,
            "component_weight": 1.0, "family_weight": 1.0,
            "feature_report_sha256": prior.V37_FEATURE_REPORT_SHA256,
            "backbone_sha256": preservation.COMPONENT_BACKBONE_SHA256,
            "component_label_sha256": COMPONENT_LABELS, "family_label_sha256": FAMILY_LABELS,
            "component_feature_sha256_by_view": dict(preservation.COMPONENT_FEATURES),
            "family_feature_sha256_by_view": dict(FAMILY_FEATURES)}


def _validate_admission(config: dict, authorization: TrainingAuthorization) -> None:
    directional = config.get("directional_preservation")
    component = config.get("component_retention")
    if (config.get("revision") != REVISION or config.get("candidate_id") != "P1" or
            type(component) is not dict or component != preservation.expected_component_config() or
            type(component.get("weight")) is not float or
            any(type(component.get(name)) is not int for name in
                ("seed", "batch_size", "rows_per_view", "batches_per_epoch", "epochs")) or
            type(directional) is not dict or directional != expected_directional_config() or
            type(directional.get("component_weight")) is not float or type(directional.get("family_weight")) is not float or
            any(type(directional.get(name)) is not int for name in
                ("seed", "batch_size", "epochs", "batches_per_epoch", "total_steps",
                 "component_rows_per_view", "family_rows_per_view")) or
            type(config.get("optimizer_steps_expected")) is not int or config["optimizer_steps_expected"] != TOTAL_STEPS):
        raise RuntimeError("The bidirectional recipe is not exactly preregistered")
    supplied = authorization.binding.get("runner_source_paths")
    if not isinstance(supplied, list) or not {p.as_posix() for p in REQUIRED_SOURCES}.issubset(set(supplied)):
        raise RuntimeError("The bidirectional implementation is absent from the immutable source bundle")
    ledger_path = prior._inside_project(prior.PROJECT_ROOT, prior.PROJECT_ROOT / CANONICAL_LEDGER_PATH)
    ledger = json.loads(ledger_path.read_bytes())
    entries = [entry for entry in ledger.get("revisions", []) if isinstance(entry, dict) and
               entry.get("task") == "marker-center" and entry.get("revision") == REVISION]
    if len(entries) != 1:
        raise RuntimeError("The bidirectional protocol is not uniquely preregistered")
    entry = entries[0]
    path, digest = entry.get("protocol_path"), entry.get("protocol_sha256")
    if path != PROTOCOL.as_posix() or path not in supplied:
        raise RuntimeError("The admitted bidirectional protocol is not snapshot-bound")
    protocol = prior._inside_project(prior.PROJECT_ROOT, prior.PROJECT_ROOT / path).read_bytes()
    prior._sha256_payload(protocol, digest, "Bidirectional protocol")


def _validate_directional_stores(positive: dict, negative: dict) -> None:
    if set(positive) != set(VIEWS) or set(negative) != set(VIEWS):
        raise RuntimeError("All four directional feature views are required")
    for view in VIEWS:
        for store, cohort, rows, label, labels_sha, features_sha in (
            (positive[view], "component_positive", COMPONENT_ROWS, 1, COMPONENT_LABELS, preservation.COMPONENT_FEATURES[view]),
            (negative[view], "family_negative", FAMILY_ROWS, 0, FAMILY_LABELS, FAMILY_FEATURES[view]),
        ):
            expected = {"report_sha256": prior.V37_FEATURE_REPORT_SHA256, "v37_sha256": prior.V37_CHECKPOINT_SHA256,
                        "backbone_sha256": preservation.COMPONENT_BACKBONE_SHA256, "feature_sha256": features_sha,
                        "labels_sha256": labels_sha, "scope": cohort.split("_")[0], "view": view,
                        "anchor_label": label, "rows": rows}
            if (type(store) is not SyntheticTrainFeatures or store.fixture or store.cohort != cohort or
                    store.view != view or store.count != rows or store.binding != expected):
                raise RuntimeError("Directional train cohort does not match its authenticated identity")


def _train_step(model, optimizer, source, authorization, journal, component_schedule,
                directional_schedule, component_stores, positive, negative) -> float:
    config = prior._require_bound_source(authorization, source)
    _validate_admission(config, authorization)
    prior._require_reviewed_schedule(config, source)
    _validate_directional_stores(positive, negative)
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
    directional = directional_schedule.reserve(step)
    if directional.view != component.view or directional.step != step:
        raise RuntimeError("Independent synthetic schedules are not aligned")
    prior.require_gpu_model(model, batch.synthetic, batch.real_positive)
    prior._require_optimizer(model, optimizer)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    existing = preservation_loss(model, batch.synthetic, batch.labels, batch.real_positive,
                                 component_stores[component.view], component.indices)
    added = directional_penalties(model, positive[directional.view], directional.component_indices,
                                  negative[directional.view], directional.family_indices,
                                  component_weight=1.0, family_weight=1.0)
    loss = existing.total + added.weighted_total
    if loss.device != torch.device("cuda:0") or not torch.isfinite(loss):
        raise RuntimeError("The combined loss is not finite on the required GPU")
    loss.backward()
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
    directional_schedule.commit(directional)
    return float(loss.detach().cpu())


def run_training(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer,
                 source: RegisteredMixedSource, authorization: TrainingAuthorization) -> dict:
    """Run only after a separate canonical protocol, source snapshot and admission exist."""
    config = prior._require_bound_source(authorization, source)
    _validate_admission(config, authorization)
    prior._require_reviewed_schedule(config, source)
    if type(source) is not RegisteredMixedSource or not isinstance(model, RealPositiveCenterNet):
        raise RuntimeError("Canonical source and V37 residual model are required")
    _require_training_gpu(*model.parameters(), *model.buffers())
    prior._require_fresh_state(model, optimizer)
    if source.verify_model(model, prior.V37_CHECKPOINT_SHA256) is not True:
        raise RuntimeError("Frozen V37 checkpoint is unverified")
    components = {view: ComponentNegativeStore.open_authenticated(view) for view in VIEWS}
    positive = {view: SyntheticTrainFeatures.load("component_positive", view) for view in VIEWS}
    negative = {view: SyntheticTrainFeatures.load("family_negative", view) for view in VIEWS}
    if any(store.fixture or store.view != view or store.count != ROWS_PER_VIEW for view, store in components.items()):
        raise RuntimeError("Component negatives are not the authenticated fixed populations")
    _validate_directional_stores(positive, negative)
    random.seed(prior.DRAFT_RECIPE.seed)
    np.random.seed(prior.DRAFT_RECIPE.seed)
    torch.manual_seed(prior.DRAFT_RECIPE.seed)
    torch.use_deterministic_algorithms(True)
    component_schedule, directional_schedule = ComponentSchedule(), DirectionalSchedule()
    journal = prior._RunJournal.claim(authorization, TOTAL_STEPS)
    total = 0.0
    try:
        if source.begin_fixed_run(prior.DRAFT_RECIPE, authorization) is not True:
            raise RuntimeError("The mixed-source schedule did not start")
        for _ in range(TOTAL_STEPS):
            total += _train_step(model, optimizer, source, authorization, journal,
                                 component_schedule, directional_schedule, components, positive, negative)
        if (journal.completed != TOTAL_STEPS or journal.pending is not None or
                component_schedule.completed != TOTAL_STEPS or directional_schedule.completed != TOTAL_STEPS or
                source.schedule.completed_steps != TOTAL_STEPS):
            raise RuntimeError("A completed schedule or optimizer count differs")
        source_path, destination = config.get("source_checkpoint_path"), config.get("candidate_checkpoint_path")
        if not isinstance(source_path, str) or not isinstance(destination, str) or Path(source_path).is_absolute() or Path(destination).is_absolute():
            raise RuntimeError("Admitted checkpoint paths are required")
        receipt = write_candidate_checkpoint(model, base_path=prior.PROJECT_ROOT / source_path,
            destination=prior.PROJECT_ROOT / destination, revision=REVISION, candidate_id="P1")
    except BaseException as error:
        journal.close()
        failure = {"schema": "graphreader.bidirectional-preservation-run-outcome.v1", "status": "failed_training_incomplete",
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
    result = {"schema": "graphreader.bidirectional-preservation-run-outcome.v1", "status": "training_complete_unapproved",
              "expected_steps": TOTAL_STEPS, "completed_steps": journal.completed, "resume_authorized": False,
              "mean_loss": total / TOTAL_STEPS, "checkpoint_path": receipt.relative_path, "checkpoint_sha256": receipt.sha256,
              "production_approval": False, "release_eligible": False}
    encoded = prior._write_new_durable(authorization.directory / "optimizer-run-outcome.json", result)
    complete_training_candidate(authorization, status=result["status"], report_sha256=sha256(encoded).hexdigest())
    return result
