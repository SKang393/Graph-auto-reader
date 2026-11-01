# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Single preregistered GPU confidence experiment on controlled train patches."""
from __future__ import annotations

from contextlib import ExitStack
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import random
import time

import numpy as np
import torch
from torch.nn import functional as F

from ml.markers.gate_seal import source_bundle_sha256, verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate
from ..confidence_spatial_v35.model import SpatialCenterNet, frozen_spatial_features
from ..real_positive_context_v1 import runner_foundation as prior
from ..real_positive_context_v1.checkpoint_writer import write_candidate_checkpoint
from ..real_positive_context_v1.model import RealPositiveCenterNet, V37_CHECKPOINT_SHA256
from ..real_positive_context_v1.objective import _require_training_gpu
from ..real_positive_context_v1.positive_archive import _open_read_only_no_writers
from ..real_positive_preservation_v1.component_source import ComponentNegativeStore, verify_v37_model
from ..real_positive_preservation_v1.runner import REQUIRED_SOURCES as PREVIOUS_SOURCES
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from .baseline import DATASET_SHA, ROOT, RUN

REVISION = "marker-center-controlled-overlay-v1"
LOCAL = Path("ml/markers/center/controlled_overlay_confidence_v1")
CONFIG = LOCAL / "p1.json"
PROTOCOL = LOCAL / "protocol.json"
SOURCES = tuple(dict.fromkeys((*PREVIOUS_SOURCES, LOCAL / "__init__.py", LOCAL / "baseline.py",
                               LOCAL / "train.py", PROTOCOL)))
AUDIT_SHA = "866dea7af54ab5c87f7c63fb37540ed94cef61195fcffd40754edb1fc351f080"
BASE = ROOT / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt"
OUT = ROOT / "artifacts/goal22-runs/controlled-overlay-confidence-v1"
SEED, EPOCHS, BATCH_SIZE, STEPS = 20261101, 4, 32, 28
COMPONENT_BATCH = 256


def checked(stream, expected: str) -> bytes:
    raw = stream.read()
    if sha256(raw).hexdigest() != expected:
        raise ValueError("Bound training input hash differs")
    return raw


def validate_build(patches: np.ndarray, labels: np.ndarray, groups: np.ndarray,
                   indices: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    if (patches.shape != (214, 3, 33, 33) or patches.dtype != np.float32 or
            labels.shape != (214,) or groups.shape != (214,) or
            indices.shape != (194,) or indices.dtype != np.int64 or
            len(set(indices.tolist())) != 194 or indices.min() < 0 or indices.max() >= 214 or
            not np.isfinite(patches).all() or patches.min() < 0 or patches.max() > 1 or
            not np.isin(labels, (0, 1)).all()):
        raise ValueError("Controlled training arrays differ from their fixed contract")
    selected, target = patches[indices].copy(), labels[indices].astype(np.float32)
    ink = {sha256(patch[0].tobytes(order="C")).digest() for patch in selected}
    if len(ink) != 194 or int(target.sum()) != 98 or len(set(groups[indices].tolist())) != 10:
        raise ValueError("Controlled selection differs from the overlap audit")
    return selected, target


def fixed_schedule(count: int, component_count: int):
    if count != 194 or component_count != 32580:
        raise ValueError("Training population changed")
    rng = random.Random(SEED)
    for _ in range(EPOCHS):
        order = list(range(count))
        rng.shuffle(order)
        for offset in range(0, count, BATCH_SIZE):
            yield order[offset:offset + BATCH_SIZE], tuple(rng.sample(range(component_count), COMPONENT_BATCH))


def check_optimizer(model: RealPositiveCenterNet, optimizer: torch.optim.Optimizer) -> None:
    if not isinstance(optimizer, torch.optim.AdamW) or len(optimizer.param_groups) != 1:
        raise RuntimeError("Expected the fixed AdamW optimizer")
    group = optimizer.param_groups[0]
    if (group["lr"] != 0.0001 or group["weight_decay"] != 0.0001 or group["capturable"] is not True or
            {id(item) for item in group["params"]} != {id(item) for item in model.residual.parameters()}):
        raise RuntimeError("Optimizer recipe or parameters changed")
    if any(isinstance(value, torch.Tensor) and value.device != torch.device("cuda:0")
           for state in optimizer.state.values() for value in state.values()):
        raise RuntimeError("Optimizer state left the RX 6600 XT")


def main() -> None:
    _require_training_gpu()
    if OUT.exists():
        raise FileExistsError("Candidate output already exists")
    with ExitStack() as stack:
        # Pin admission and all implementation files for the entire run.
        locked = {path: stack.enter_context(_open_read_only_no_writers(ROOT / path))
                  for path in (prior.CANONICAL_LEDGER_PATH, CONFIG, *SOURCES)}
        config = json.loads(locked[CONFIG].read())
        if (config.get("recipe") != {"seed": SEED, "epochs": EPOCHS, "batch_size": BATCH_SIZE,
                                     "steps": STEPS, "component_batch": COMPONENT_BATCH,
                                     "learning_rate": 0.0001, "weight_decay": 0.0001,
                                     "component_upward_penalty": 1.0, "controlled_shift_penalty": 0.1} or
                config.get("dataset_sha256") != DATASET_SHA or config.get("overlap_audit_sha256") != AUDIT_SHA or
                config.get("source_checkpoint_sha256") != V37_CHECKPOINT_SHA256 or
                config.get("sealed_runs_authorized") != 0 or config.get("production_approval") is not False or
                config.get("expected_runner_source_bundle_sha256") != source_bundle_sha256(ROOT, SOURCES)):
            raise ValueError("Fixed exploratory configuration differs")
        dataset_dir = RUN / "controlled-domain-dataset-v1"
        audit_dir = RUN / "controlled-overlap-audit-v1"
        def read(path: Path, expected: str) -> bytes:
            return checked(stack.enter_context(_open_read_only_no_writers(path)), expected)
        dataset = json.loads(read(dataset_dir / "report.json", DATASET_SHA))
        audit = json.loads(read(audit_dir / "report.json", AUDIT_SHA))
        if (audit.get("dataset_sha256") != DATASET_SHA or audit.get("optimizer_steps") != 0 or
                audit.get("sealed_payload_reads") != 0 or dataset.get("native_unmarked_content_labeled_negative") is not False):
            raise ValueError("Controlled input provenance differs")
        entry = next(item for item in dataset["partitions"] if item["partition"] == "internal_train_build")
        arrays = {name: np.load(BytesIO(read(dataset_dir / "internal_train_build" / f"{name}.npy",
                                             entry["files"][name])), allow_pickle=False)
                  for name in ("patches", "labels", "groups")}
        indices = np.load(BytesIO(read(audit_dir / "build_indices.npy", audit["files"]["build_indices"])), allow_pickle=False)
        patches, labels = validate_build(arrays["patches"], arrays["labels"], arrays["groups"], indices)
        raw = read(BASE, V37_CHECKPOINT_SHA256)
        state = torch.load(BytesIO(raw), map_location="cpu", weights_only=True)["state_dict"]
        local = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
        local.load_state_dict(state, strict=True)
        model = RealPositiveCenterNet(local).to(torch.device("cuda:0"))
        verify_v37_model(model)
        component = ComponentNegativeStore.open_authenticated("identity")
        if component.fixture or component.count != 32580:
            raise ValueError("Synthetic component training population differs")
        batches = list(fixed_schedule(len(patches), component.count))
        if len(batches) != STEPS:
            raise ValueError("Fixed optimizer schedule differs")
        torch.manual_seed(SEED)
        torch.use_deterministic_algorithms(True)
        inputs = torch.from_numpy(patches).to("cuda:0")
        targets = torch.from_numpy(labels).to("cuda:0")
        prior.require_gpu_model(model, inputs)
        _require_training_gpu(targets)
        with torch.no_grad():
            features = frozen_spatial_features(model.local.base, inputs)
            retained_logits = model.local.confidence(features)
        _require_training_gpu(features, retained_logits)
        optimizer = torch.optim.AdamW(model.residual.parameters(), lr=0.0001, weight_decay=0.0001, capturable=True)
        check_optimizer(model, optimizer)
        if optimizer.state or any(torch.count_nonzero(p).item() for p in model.residual.parameters()):
            raise RuntimeError("Candidate is not freshly initialized")
        authorization = acquire_training_candidate(ROOT, task="marker-center", revision=REVISION,
            candidate_id="P1", config_path=CONFIG, runner_source_paths=SOURCES)
        journal = prior._RunJournal.claim(authorization, STEPS)
        started, total = time.perf_counter(), 0.0
        try:
            model.train()
            for batch_indices, component_indices in batches:
                step = journal.begin_step(authorization, STEPS)
                x, y, base = features[batch_indices], targets[batch_indices], retained_logits[batch_indices]
                negatives = component.gpu_batch(component_indices)
                _require_training_gpu(x, y, base, negatives, *model.parameters())
                check_optimizer(model, optimizer)
                optimizer.zero_grad(set_to_none=True)
                shift = model.residual(x).squeeze(-1)
                negative_shift = model.residual(negatives).squeeze(-1)
                loss = (F.binary_cross_entropy_with_logits(base + shift, y) +
                        F.relu(negative_shift).square().mean() + 0.1 * shift.square().mean())
                if loss.device != torch.device("cuda:0") or not torch.isfinite(loss):
                    raise RuntimeError("Training loss is not finite on the RX 6600 XT")
                loss.backward()
                if any(p.grad is None or p.grad.device != torch.device("cuda:0") or
                       not torch.isfinite(p.grad).all() for p in model.residual.parameters()):
                    raise RuntimeError("Residual gradient is missing or off GPU")
                optimizer.step()
                torch.cuda.synchronize()
                check_optimizer(model, optimizer)
                journal.commit_step(step)
                total += float(loss.detach().cpu())
            if journal.completed != STEPS or journal.pending is not None:
                raise RuntimeError("Optimizer journal is incomplete")
            verify_v37_model(model)
            verify_bound_source_snapshot(ROOT, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
            OUT.mkdir(parents=True, exist_ok=False)
            receipt = write_candidate_checkpoint(model, base_path=BASE, destination=OUT / "marker-center.pt",
                                                  revision=REVISION, candidate_id="P1")
            result = {"schema": "graphreader.controlled-overlay-training.v1", "status": "training_complete_unapproved",
                      "steps": journal.completed, "elapsed_seconds": time.perf_counter() - started,
                      "mean_loss": total / STEPS, "checkpoint_sha256": receipt.sha256,
                      "checkpoint_path": receipt.relative_path, "device": torch.cuda.get_device_name(0),
                      "architecture": torch.cuda.get_device_properties(0).gcnArchName,
                      "cpu_fallback": False, "holdout_reads": 0, "development_reads": 0,
                      "sealed_payload_reads": 0, "production_approval": False}
        except BaseException as error:
            journal.close()
            result = {"schema": "graphreader.controlled-overlay-training.v1", "status": "failed_training_incomplete",
                      "completed_steps": journal.completed, "pending_step": journal.pending,
                      "exception_type": type(error).__name__, "exception_sha256": sha256(str(error).encode()).hexdigest(),
                      "production_approval": False}
            encoded = prior._write_new_durable(authorization.directory / "optimizer-run-outcome.json", result)
            complete_training_candidate(authorization, status=result["status"], report_sha256=sha256(encoded).hexdigest())
            raise
        journal.close()
        encoded = prior._write_new_durable(authorization.directory / "optimizer-run-outcome.json", result)
        complete_training_candidate(authorization, status=result["status"], report_sha256=sha256(encoded).hexdigest())
        print(json.dumps({**result, "outcome_sha256": sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
