# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One canonical, GPU-only synthetic axis-ranker training attempt."""
from __future__ import annotations

from contextlib import ExitStack
from hashlib import sha256
import json
import os
from pathlib import Path
import random
import time

from ml.axis.gpu_candidate_ranker_v1 import trainer_v3 as previous
from ml.axis.gpu_candidate_ranker_v1.contracts import Recipe, parse_groups, schedule
from ml.markers.gate_seal import source_bundle_sha256, verify_bound_source_snapshot
from ml.markers.training_budget import (
    acquire_training_candidate,
    complete_training_candidate,
)

ROOT = Path(__file__).resolve().parents[3]
LOCAL = Path("ml/axis/residual_candidate_ranker_v1")
TASK = "axis-candidate-ranker"
REVISION = "axis-candidate-residual-gpu-v1"
CANDIDATE = "P1"
CONFIG = LOCAL / "p1.json"
PROTOCOL = LOCAL / "protocol.json"
OUT = ROOT / "artifacts/goal22-runs/axis-residual-ranker-v1"
GROUPS = previous.GROUPS
DATA_RECEIPT = previous.DATA_RECEIPT
GROUP_SHA = previous.GROUP_SHA
RECEIPT_SHA = previous.RECEIPT_SHA
TARGET_SHA = previous.TARGET_SHA
SCALE = previous.SCALE
SEED = 20261101
EPOCHS = 4
STEPS = 372
LEARNING_RATE = 0.001
WEIGHT_DECAY = 0.0001
TEMPERATURE = 0.01
RESIDUAL_SCALE = 0.05
RESIDUAL_PENALTY = 0.1
SOURCES = tuple(dict.fromkeys((*previous.SOURCES, LOCAL / "__init__.py", LOCAL / "runner.py", PROTOCOL)))


def canonical(value: dict) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()


def write_new(path: Path, raw: bytes) -> None:
    with path.open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


def fixed_config(bundle_sha256: str) -> dict:
    return {
        "schema": "graphreader.axis-residual-ranker-config.v1", "task": TASK,
        "revision": REVISION, "candidate_id": CANDIDATE, "groups_path": GROUPS.as_posix(),
        "groups_sha256": GROUP_SHA, "data_receipt_path": DATA_RECEIPT.as_posix(),
        "data_receipt_sha256": RECEIPT_SHA, "target_sha256": TARGET_SHA,
        "group_count": 93, "candidate_count": 27776, "seed": SEED, "epochs": EPOCHS,
        "optimizer_steps": STEPS, "learning_rate": LEARNING_RATE, "weight_decay": WEIGHT_DECAY,
        "target_temperature": TEMPERATURE, "feature_scale": list(SCALE),
        "architecture": "native-score-plus-tanh-residual-17-32-1",
        "residual_scale": RESIDUAL_SCALE, "residual_penalty": RESIDUAL_PENALTY,
        "expected_runner_source_bundle_sha256": bundle_sha256,
        "checkpoint_path": "artifacts/goal22-runs/axis-residual-ranker-v1/checkpoint.json",
        "dev_read": False, "reserved_read": False, "production_approved": False,
    }


class StepJournal:
    """An exclusive, durable run intent prevents silent replay after a crash."""

    def __init__(self, directory: Path, expected: int) -> None:
        if type(expected) is not int or not 0 < expected <= 1_000_000:
            raise ValueError("A positive fixed step budget is required")
        self.path = directory / "optimizer-journal.jsonl"
        self.completed = 0
        self.pending = False
        self.expected = expected
        write_new(directory / "optimizer-run-intent.json", canonical({
            "schema": "graphreader.axis-residual-optimizer-intent.v1",
            "expected_steps": expected, "resume_authorized": False}))
        write_new(self.path, canonical({"event": "run_opened", "expected_steps": expected}))

    def _append(self, event: str) -> None:
        with self.path.open("ab") as stream:
            stream.write(canonical({"event": event, "step": self.completed + 1}))
            stream.flush()
            os.fsync(stream.fileno())

    def begin(self) -> None:
        if self.pending or self.completed >= self.expected:
            raise RuntimeError("Optimizer step is outside the admitted budget")
        self._append("step_intent")
        self.pending = True

    def commit(self) -> None:
        if not self.pending:
            raise RuntimeError("Optimizer step has no intent")
        self._append("step_completed")
        self.completed += 1
        self.pending = False


def run() -> str:
    from ml.axis.candidate_ranker_foundation_v1.train_labels_v2 import lock, safe
    import torch

    if OUT.exists():
        raise FileExistsError("A prior attempt exists; optimizer replay is not allowed")
    seal = ROOT / "ml/markers/training-seals" / TASK / REVISION / CANDIDATE
    if (seal / "optimizer-run-intent.json").exists():
        raise RuntimeError("An optimizer run has already been claimed")
    with ExitStack() as stack:
        for path in (ROOT / CONFIG, ROOT / GROUPS, ROOT / DATA_RECEIPT,
                     ROOT / "ml/markers/training-budgets/production-repair-v1.json",
                     *(ROOT / source for source in SOURCES)):
            lock(stack, path)
        config_raw = (ROOT / CONFIG).read_bytes()
        if json.loads(config_raw) != fixed_config(source_bundle_sha256(ROOT, SOURCES)):
            raise ValueError("The exact preregistered recipe differs")
        groups_raw = (ROOT / GROUPS).read_bytes()
        receipt_raw = (ROOT / DATA_RECEIPT).read_bytes()
        if sha256(groups_raw).hexdigest() != GROUP_SHA or sha256(receipt_raw).hexdigest() != RECEIPT_SHA:
            raise ValueError("Training inputs differ from the frozen synthetic receipt")
        previous.validate_data_receipt(json.loads(receipt_raw))
        document = json.loads(groups_raw)
        allowed = frozenset((group["source_sha256"], group["panel_sha256"]) for group in document["groups"])
        recipe = Recipe(SEED, EPOCHS, LEARNING_RATE, GROUP_SHA)
        groups = parse_groups(groups_raw, recipe, allowed)
        order = schedule(recipe, len(groups))
        if len(groups) != 93 or sum(len(group["rows"]) for group in groups) != 27776 or len(order) != STEPS:
            raise ValueError("Training group or optimizer-step denominator differs")
        previous.require_gpu(torch)
        torch.use_deterministic_algorithms(True)
        random.seed(SEED)
        torch.manual_seed(SEED)
        torch.cuda.manual_seed_all(SEED)
        safe(OUT.parent)
        authorization = acquire_training_candidate(
            ROOT, task=TASK, revision=REVISION, candidate_id=CANDIDATE,
            config_path=CONFIG, runner_source_paths=SOURCES)
        journal = None
        finalized = False
        try:
            OUT.mkdir()
            journal = StepJournal(authorization.directory, STEPS)
            device = torch.device("cuda:0")

            class ResidualRanker(torch.nn.Module):
                def __init__(self) -> None:
                    super().__init__()
                    self.hidden = torch.nn.Linear(17, 32, device=device)
                    self.output = torch.nn.Linear(32, 1, device=device)
                    self.register_buffer("feature_scale", torch.tensor(SCALE, dtype=torch.float32, device=device))
                    torch.nn.init.zeros_(self.output.weight)
                    torch.nn.init.zeros_(self.output.bias)

                def forward(self, features):
                    delta = RESIDUAL_SCALE * self.output(torch.tanh(self.hidden(features * self.feature_scale))).squeeze(-1)
                    return features[:, 0] + delta, delta

            model = ResidualRanker()
            optimizer = torch.optim.AdamW(model.parameters(), lr=LEARNING_RATE, weight_decay=WEIGHT_DECAY,
                                          capturable=True, foreach=False, fused=False)
            previous.require_gpu(torch, *model.parameters(), *model.buffers())
            if optimizer.state or any(bool(torch.count_nonzero(p).item()) for p in model.output.parameters()):
                raise RuntimeError("The residual or optimizer is not freshly initialized")
            started, total_loss = time.perf_counter(), 0.0
            for index, group_index in enumerate(order):
                verify_bound_source_snapshot(ROOT, authorization.snapshot_path,
                                             authorization.binding["source_snapshot_sha256"])
                previous.check_optimizer(torch, model, optimizer)
                rows = groups[group_index]["rows"]
                features = torch.tensor([row["features"] for row in rows], dtype=torch.float32, device=device)
                costs = torch.tensor([row["target_error"] for row in rows], dtype=torch.float32, device=device)
                previous.require_gpu(torch, features, costs, *model.parameters(), *model.buffers())
                journal.begin()
                optimizer.zero_grad(set_to_none=True)
                logits, delta = model(features)
                target = torch.softmax(-costs / TEMPERATURE, dim=0)
                loss = -(target * torch.log_softmax(logits, dim=0)).sum() + RESIDUAL_PENALTY * delta.square().mean()
                previous.require_gpu(torch, logits, delta, target, loss)
                if not bool(torch.isfinite(loss).item()):
                    raise RuntimeError("Training loss is not finite on the GPU")
                loss.backward()
                gradients = [parameter.grad for parameter in model.parameters()]
                if any(gradient is None for gradient in gradients):
                    raise RuntimeError("A model gradient is missing")
                previous.require_gpu(torch, *gradients)
                if any(not bool(torch.isfinite(gradient).all().item()) for gradient in gradients):
                    raise RuntimeError("A model gradient is not finite")
                previous.check_optimizer(torch, model, optimizer)
                optimizer.step()
                previous.check_optimizer(torch, model, optimizer)
                if any(not bool(torch.isfinite(parameter).all().item()) for parameter in model.parameters()):
                    raise RuntimeError("A model parameter is not finite")
                torch.cuda.synchronize(device)
                journal.commit()
                total_loss += float(loss.detach().item())
                if (index + 1) % 93 == 0:
                    print(f"completed_steps={index + 1}/{STEPS}", flush=True)
            if journal.completed != STEPS or journal.pending:
                raise RuntimeError("Optimizer journal is incomplete")
            previous.require_gpu(torch, *model.parameters(), *model.buffers())
            checkpoint = {
                "schema": "graphreader.axis-residual-ranker-checkpoint.v1",
                "config_sha256": sha256(config_raw).hexdigest(),
                "state_dict": {name: tensor.detach().cpu().tolist() for name, tensor in model.state_dict().items()},
                "residual_scale": RESIDUAL_SCALE, "production_approved": False,
            }
            checkpoint_raw = canonical(checkpoint)
            write_new(OUT / "checkpoint.json", checkpoint_raw)
            report = {
                "schema": "graphreader.axis-residual-ranker-training-report.v1",
                "status": "training_complete_unapproved", "config_sha256": sha256(config_raw).hexdigest(),
                "groups_sha256": GROUP_SHA, "targets_sha256": TARGET_SHA,
                "checkpoint_sha256": sha256(checkpoint_raw).hexdigest(), "steps": journal.completed,
                "mean_loss": total_loss / STEPS, "elapsed_seconds": time.perf_counter() - started,
                "device": torch.cuda.get_device_name(0), "architecture": torch.cuda.get_device_properties(0).gcnArchName,
                "cuda_index": 0, "hip": str(torch.version.hip), "cpu_fallback": False,
                "development_reads": 0, "sealed_reads": 0, "production_approved": False,
            }
            raw = canonical(report)
            write_new(OUT / "report.json", raw)
            write_new(authorization.directory / "trainer-outcome.json", raw)
            complete_training_candidate(authorization, status=report["status"], report_sha256=sha256(raw).hexdigest())
            finalized = True
            return sha256(raw).hexdigest()
        except BaseException as error:
            failure = canonical({
                "schema": "graphreader.axis-residual-ranker-failure.v1", "status": "failed_or_unfinalized",
                "completed_steps": journal.completed if journal else 0,
                "pending_step": journal.pending if journal else False,
                "exception_type": type(error).__name__, "exception_sha256": sha256(str(error).encode()).hexdigest(),
                "production_approved": False, "resume_authorized": False,
            })
            try:
                write_new(authorization.directory / "trainer-failure.json", failure)
                if not finalized and not (authorization.directory / "result.json").exists():
                    complete_training_candidate(authorization, status="failed_training_incomplete",
                                                report_sha256=sha256(failure).hexdigest())
            except BaseException as final_error:
                error.add_note("Could not finalize the failed attempt: " + type(final_error).__name__)
            raise


if __name__ == "__main__":
    print("training_report_sha256=" + run())
