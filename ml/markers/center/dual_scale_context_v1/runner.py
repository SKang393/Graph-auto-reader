# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One preregistered family-only context trial, guarded by the canonical ledger.

The canonical ledger currently does not authorize this candidate. Do not add
authorization until independent review of all sources and the proposal.
"""
from __future__ import annotations

import argparse
import ctypes
import importlib.metadata
import json
from io import BytesIO
import os
from pathlib import Path
import random
import subprocess

import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn.functional as functional

from ml.markers.gate_seal import sha256_file, verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ..confidence_bce_v33.runner import validate_export_parity
from ..confidence_reflection_v37.runner import RUNNER_SOURCES as V37_SOURCES
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ..shape_coverage_cache import unpack_scene, write_json
from .evaluation import evaluate
from .model import DualScaleContextNet
from .prepare import PROJECT, SOURCE, verified_bytes
from .training_data import load

REVISION = "marker-center-dual-scale-context-v1"
CONFIG = Path("ml/markers/center/dual_scale_context_v1/p1.json")
SOURCES = tuple(dict.fromkeys((*V37_SOURCES, *(Path(p) for p in (
    "ml/markers/training_budget.py", "ml/markers/gate_seal.py", "ml/policy/evidence_policy.py",
    "ml/markers/center/shape_coverage_evaluation.py", "ml/markers/center/shape_coverage_cache.py",
    "tools/gpu-training/Run-GpuPython.ps1", "tools/gpu-training/requirements.lock.txt")),
    *(Path("ml/markers/center/dual_scale_context_v1") / name for name in (
        "__init__.py", "model.py", "crops.py", "prepare.py", "verify_family_runtime.py",
        "training_data.py", "evaluation.py", "runner.py", "protocol.json")))))
BASE = "artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt"
BASE_SHA = "2daeedb92fdfb6c425cca6035040ca0e857c6d6bb1ba548281729fee71c24cd4"
BASE_REPORT = "artifacts/goal22-runs/center-confidence-reflection-v37/P1/report.json"
BASE_REPORT_SHA = "cedef52cbae22a095e81db2797bf7d378ea702b9c6185c1088441e7417a0730d"
DEV = "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt"
DEV_SHA = "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41"
STEPS = 2264


def require_gpu(model: DualScaleContextNet | None = None, *tensors: torch.Tensor) -> None:
    if not torch.cuda.is_available() or not torch.version.hip:
        raise RuntimeError("ROCm GPU is required; CPU training is forbidden")
    device = torch.device("cuda:0")
    properties = torch.cuda.get_device_properties(device)
    if properties.name != "AMD Radeon RX 6600 XT" or properties.gcnArchName.split(":")[0] != "gfx1032":
        raise RuntimeError("The required RX 6600 XT gfx1032 is not cuda:0")
    if model is not None and any(p.device != device for p in model.parameters()):
        raise RuntimeError("Every model parameter must remain on cuda:0")
    if any(t.device != device for t in tensors):
        raise RuntimeError("The optimizer batch, loss, and gradients must remain on cuda:0")


def validate_config(config: dict) -> None:
    bundle = config.get("expected_runner_source_bundle_sha256")
    if not isinstance(bundle, str) or len(bundle) != 64 or any(c not in "0123456789abcdef" for c in bundle):
        raise ValueError("Candidate source bundle identity is missing")
    expected = {"task": "marker-center", "revision": REVISION, "candidate_id": "P1", "seed": 20261030,
                "train_sources": 20, "train_panels": 28, "base_rows": 9053, "views": 4,
                "epochs": 8, "batch_size": 128, "optimizer_steps": STEPS, "learning_rate": .0003,
                "weight_decay": .0001, "gradient_norm_limit": 5., "operating_threshold": .1,
                "trainable_parameters": 6449, "base_checkpoint_sha256": BASE_SHA,
                "development_sha256": DEV_SHA, "private_reads": 0, "sealed_reads": 0,
                "production_approval": False, "training_device": "RX 6600 XT gfx1032 cuda:0",
                "expected_runner_source_bundle_sha256": bundle}
    if config != expected:
        raise ValueError("Fixed candidate configuration changed")


def configure_project_git() -> None:
    """Authorize this checkout only in this process, without global Git edits."""
    count = int(os.environ.get("GIT_CONFIG_COUNT", "0"))
    os.environ[f"GIT_CONFIG_KEY_{count}"] = "safe.directory"
    os.environ[f"GIT_CONFIG_VALUE_{count}"] = PROJECT.as_posix()
    os.environ["GIT_CONFIG_COUNT"] = str(count + 1)
    result = subprocess.run(["git", "rev-parse", "HEAD"], cwd=PROJECT,
                            check=True, capture_output=True, text=True,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if len(result.stdout.strip()) != 40:
        raise RuntimeError("Project checkout does not have a valid base commit")


def runtime_environment() -> dict:
    miopen = ctypes.CDLL(str(PROJECT / "tools/gpu-training/venv/Lib/site-packages/_rocm_sdk_libraries/bin/MIOpen.dll"))
    major, minor, patch = ctypes.c_size_t(), ctypes.c_size_t(), ctypes.c_size_t()
    if miopen.miopenGetVersion(ctypes.byref(major), ctypes.byref(minor), ctypes.byref(patch)) != 0:
        raise RuntimeError("Unable to read MIOpen version")
    command = "$x=Get-CimInstance Win32_VideoController | Where-Object Name -eq 'AMD Radeon RX 6600 XT'; if (@($x).Count -ne 1) { exit 1 }; $x.DriverVersion"
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                            check=True, capture_output=True, text=True,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    driver = result.stdout.strip()
    if not driver:
        raise RuntimeError("Unable to identify the GPU driver version")
    return {"torch": torch.__version__, "hip": torch.version.hip,
            "rocm_package": importlib.metadata.version("rocm"),
            "miopen": f"{major.value}.{minor.value}.{patch.value}", "windows_driver": driver,
            "device_name": torch.cuda.get_device_name(0),
            "device_architecture": torch.cuda.get_device_properties(0).gcnArchName,
            "deterministic_algorithms": torch.are_deterministic_algorithms_enabled(),
            "cudnn_deterministic": torch.backends.cudnn.deterministic,
            "cudnn_benchmark": torch.backends.cudnn.benchmark,
            "bitwise_reproducibility_guaranteed": False}


def train(model: DualScaleContextNet, contexts: torch.Tensor, labels: torch.Tensor, cancel: Path) -> dict:
    device = torch.device("cuda:0")
    model.to(device).train()
    require_gpu(model)
    if sum(p.numel() for p in model.context.parameters()) != 6449:
        raise ValueError("Trainable context parameter count changed")
    if any(p.requires_grad for p in model.local.parameters()):
        raise ValueError("Local model must remain entirely frozen")
    frozen = {name: value.detach().clone() for name, value in model.local.state_dict().items()}
    optimizer = torch.optim.AdamW(model.context.parameters(), lr=.0003, weight_decay=.0001)
    order_rng = torch.Generator(device="cpu").manual_seed(20261030)
    steps = 0
    losses = []
    for epoch in range(8):
        order = torch.randperm(9053 * 4, generator=order_rng)
        for start in range(0, len(order), 128):
            if cancel.exists():
                raise RuntimeError("Training cancelled before completion")
            chosen = order[start:start+128]
            indices = chosen.remainder(9053)
            views = torch.div(chosen, 9053, rounding_mode="floor")
            batch = contexts.index_select(0, indices).to(device)
            target = labels.index_select(0, indices).to(device)
            for view in range(1, 4):
                select = views == view
                if select.any():
                    rows = select.nonzero().flatten().to(device)
                    dims = {1: (-1,), 2: (-2,), 3: (-2, -1)}[view]
                    batch.index_copy_(0, rows, batch.index_select(0, rows).flip(dims))
            require_gpu(model, batch, target)
            optimizer.zero_grad(set_to_none=True)
            logits, _ = model.logits_and_geometry(batch)
            loss = functional.binary_cross_entropy_with_logits(logits, target)
            if not torch.isfinite(loss):
                raise RuntimeError("Non-finite training loss")
            loss.backward()
            gradients = [p.grad for p in model.context.parameters() if p.grad is not None]
            if len(gradients) != len(list(model.context.parameters())):
                raise RuntimeError("Missing context gradient")
            require_gpu(model, batch, target, loss, *gradients)
            torch.nn.utils.clip_grad_norm_(model.context.parameters(), 5., error_if_nonfinite=True)
            # Validate the physical device immediately before every optimizer step.
            if cancel.exists():
                raise RuntimeError("Training cancelled before completion")
            require_gpu(model, batch, target, loss, *gradients)
            optimizer.step()
            steps += 1
            losses.append(float(loss.detach().cpu()))
        if any(not torch.equal(value, frozen[name]) for name, value in model.local.state_dict().items()):
            raise RuntimeError("Frozen local model or normalization state changed")
    if steps != STEPS:
        raise RuntimeError("Fixed optimizer step count changed")
    return {"optimizer_steps": steps, "epochs": 8, "base_rows": 9053, "views": 4,
            "final_batch_loss": losses[-1], "device_name": torch.cuda.get_device_name(0),
            "device_architecture": torch.cuda.get_device_properties(0).gcnArchName,
            "cpu_optimizer_steps": 0, "retained_local_state_unchanged": True}


def run(output: Path) -> dict:
    config = json.loads((PROJECT / CONFIG).read_text(encoding="utf-8"))
    validate_config(config)
    require_gpu()
    environment = runtime_environment()
    output = output.resolve()
    allowed = (PROJECT / "ml/markers/center/dual_scale_context_v1").resolve()
    if not output.is_relative_to(allowed) or output.exists():
        raise ValueError("Output must be a new directory within the owned candidate folder")
    configure_project_git()
    # This acquisition fails closed until the lead has reviewed and registered P1.
    authorization = acquire_training_candidate(PROJECT, task="marker-center", revision=REVISION,
        candidate_id="P1", config_path=CONFIG, runner_source_paths=SOURCES)
    try:
        output.mkdir(parents=True, exist_ok=False)
        torch.manual_seed(20261030)
        random.seed(20261030)
        np.random.seed(20261030)
        contexts, labels, provenance = load()
        base = ScaleClassifierNet(ModelConfig())
        local = SpatialCenterNet(base)
        local.load_state_dict(torch.load(BytesIO(verified_bytes(BASE, BASE_SHA)), map_location="cpu", weights_only=True)["state_dict"])
        local.eval()
        model = DualScaleContextNet(local)
        with torch.inference_mode():
            sample = contexts[:16]
            if not torch.equal(model(sample), local(sample[:, :, 16:49, 16:49])):
                raise RuntimeError("Zero-context initializer differs from V37")
        training = train(model, contexts, labels, output / "CANCEL")
        del contexts, labels
        model.cpu().eval()
        packed = torch.load(BytesIO(verified_bytes(DEV, DEV_SHA)), map_location="cpu", weights_only=True)
        if set(packed) != {"component", "family"} or {
            scope: (len(rows), sum(len(row["metadata"]["centers"]) for row in rows))
            for scope, rows in packed.items()} != {"component": (167, 2004), "family": (9, 206)}:
            raise RuntimeError("Development denominator changed")
        scenes = {scope: tuple(unpack_scene(row, expected_split={"component": "dev", "family": "validation"}[scope])
                               for row in rows) for scope, rows in packed.items()}
        checkpoint, exported = output / "marker-center.pt", output / "marker-center.onnx"
        torch.save({"state_dict": model.state_dict(), "revision": REVISION, "base_checkpoint_sha256": BASE_SHA}, checkpoint)
        torch.onnx.export(model, torch.zeros(1, 3, 65, 65), exported,
                          input_names=["candidate_patches"], output_names=["candidate_predictions"],
                          dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
                          opset_version=18, dynamo=False)
        onnx.checker.check_model(onnx.load(exported))
        options = ort.SessionOptions()
        options.intra_op_num_threads = 12
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
        session = ort.InferenceSession(str(exported), sess_options=options, providers=["CPUExecutionProvider"])
        development = evaluate(SOURCE, scenes, model, session, WorkBudget(output / "CANCEL"), output)
        validate_export_parity(development["parity"])
        previous = json.loads(verified_bytes(BASE_REPORT, BASE_REPORT_SHA).decode("utf-8-sig"))
        for scope in ("component", "family"):
            observed = development["results"][scope]["baseline_balanced"]["0.1"]
            expected = previous["development"]["results"][scope]["candidate_balanced"]["0.1"]
            for key in ("scene_count", "truth_count", "true_positives", "false_positives", "false_negatives",
                        "duplicate_count", "prohibited_structure_hits"):
                if observed[key] != expected[key]:
                    raise RuntimeError(f"Frozen V37 development baseline did not reproduce: {scope} {key}")
        if development["geometry_maximum_absolute_change"] > 1e-5:
            raise RuntimeError("Frozen local geometry changed")
        verify_bound_source_snapshot(PROJECT, authorization.snapshot_path, authorization.binding["source_snapshot_sha256"])
        report = {"task": "marker-center", "revision": REVISION, "candidate_id": "P1",
                  "status": "dev_pass" if development["clears_shared_dev_bars"] else "failed_dev_unconsumed",
                  "binding": authorization.binding, "training": training, "development": development,
                  "runtime_environment": environment, "family_source_provenance": provenance,
                  "checkpoint_sha256": sha256_file(checkpoint), "onnx_sha256": sha256_file(exported),
                  "private_reads": 0, "sealed_reads": 0, "production_approval": False,
                  "release_eligible": False, "population_confound": "family-only 20 source images"}
        write_json(output / "report.json", report)
        complete_training_candidate(authorization, status=report["status"], report_sha256=sha256_file(output / "report.json"))
        return report
    except BaseException as error:
        void_candidate(authorization, error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(run(args.output), indent=2, sort_keys=True))
