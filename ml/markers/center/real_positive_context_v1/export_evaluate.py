# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Export and evaluate a fixed candidate on pinned synthetic development only.

This does not train, consume a sealed set, promote a model, or validate real
inputs. The historical evaluator uses procedural masks in its fixtures. Inputs
must be bound by SHA-256 and outputs stay in a new project artifacts directory.
"""
from __future__ import annotations

import argparse
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import re

import numpy as np
import onnx
import onnxruntime as ort
import torch

from ml.markers.classifier.native_context_v3.runner import WorkBudget
from .. import shape_coverage_evaluation as evaluator
from ..confidence_bce_v33.runner import validate_export_parity
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..dual_scale_context_v1.prepare import SOURCE, verified_bytes
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ..shape_coverage_cache import unpack_scene
from .checkpoint_writer import _ALLOWED_BASES, _MAX_CHECKPOINT_BYTES, _cpu_state, _project_file
from .model import OPERATING_THRESHOLD, V37_CHECKPOINT_SHA256, RealPositiveCenterNet
from .positive_archive import _open_read_only_no_writers

ROOT = Path(__file__).resolve().parents[4]
DEV = "artifacts/goal22-runs/center-shape-coverage-cache-v3/dev.pt"
DEV_SHA = "98662114b6e0b5e581b9ded142a51d304a65914881aeda0d75c74b84cc857e41"
V37_REPORT = "artifacts/goal22-runs/center-confidence-reflection-v37/P1/report.json"
V37_REPORT_SHA = "cedef52cbae22a095e81db2797bf7d378ea702b9c6185c1088441e7417a0730d"
DENOMINATOR = {"component": (167, 2004), "family": (9, 206)}


def require_gpu() -> torch.device:
    if not torch.cuda.is_available() or not torch.version.hip:
        raise RuntimeError("ROCm RX 6600 XT is required")
    device = torch.device("cuda:0")
    props = torch.cuda.get_device_properties(device)
    if props.name != "AMD Radeon RX 6600 XT" or props.gcnArchName.split(":")[0] != "gfx1032":
        raise RuntimeError("Expected RX 6600 XT gfx1032 on cuda:0")
    return device


def _read_bound(path: Path, expected: str) -> bytes:
    if re.fullmatch(r"[0-9a-f]{64}", expected) is None:
        raise ValueError("Expected checksum is invalid")
    with _open_read_only_no_writers(path) as stream:
        raw = stream.read(_MAX_CHECKPOINT_BYTES + 1)
    if not raw or len(raw) > _MAX_CHECKPOINT_BYTES or sha256(raw).hexdigest() != expected:
        raise ValueError("Checkpoint checksum or size changed")
    return raw


def load_candidate(checkpoint: Path, expected_sha256: str, base_path: Path) -> RealPositiveCenterNet:
    checkpoint = _project_file(checkpoint, existing=True)
    base_path = _project_file(base_path, existing=True)
    if base_path not in _ALLOWED_BASES or checkpoint == base_path:
        raise ValueError("Expected a separate candidate and approved V37 base")
    base = torch.load(BytesIO(_read_bound(base_path, V37_CHECKPOINT_SHA256)), map_location="cpu", weights_only=True)
    payload = torch.load(BytesIO(_read_bound(checkpoint, expected_sha256)), map_location="cpu", weights_only=True)
    if (not isinstance(base, dict) or not isinstance(base.get("state_dict"), dict) or
            not isinstance(payload, dict) or set(payload) != {"format", "revision", "candidate_id", "base_checkpoint_sha256", "state_dict"} or
            payload["format"] != "graphreader.real-positive-candidate.v1" or
            payload["base_checkpoint_sha256"] != V37_CHECKPOINT_SHA256 or
            not isinstance(payload["revision"], str) or not isinstance(payload["candidate_id"], str) or
            not isinstance(payload["state_dict"], dict)):
        raise ValueError("Candidate checkpoint contract invalid")
    local = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
    local.load_state_dict(base["state_dict"], strict=True)
    candidate = RealPositiveCenterNet(local)
    candidate.load_state_dict(payload["state_dict"], strict=True)
    _cpu_state(candidate, base["state_dict"])
    return candidate.eval()


class GpuEvaluationModel:
    """Adapter for the unchanged V37 evaluator's CPU-tensor call convention."""
    def __init__(self, model: RealPositiveCenterNet, device: torch.device):
        self.model = model.eval().to(device)
        self.device = device
        if any(p.device != device for p in model.parameters()):
            raise RuntimeError("Candidate parameters are not on the selected GPU")
        self.geometry_maximum_absolute_change = 0.0
        self.rows = 0

    def __call__(self, patches: torch.Tensor) -> torch.Tensor:
        if patches.dtype != torch.float32 or patches.ndim != 4 or tuple(patches.shape[1:]) != (3, 33, 33):
            raise ValueError("Expected NCHW float32 33-pixel patches")
        with torch.inference_mode():
            batch = patches.to(self.device)
            candidate = self.model(batch)
            retained = self.model.local(batch)
            difference = torch.max(torch.abs(candidate[:, 1:] - retained[:, 1:])).item()
            if not np.isfinite(difference) or difference > 1e-5:
                raise RuntimeError("Frozen geometry changed")
            self.geometry_maximum_absolute_change = max(self.geometry_maximum_absolute_change, difference)
            self.rows += len(batch)
            return candidate.cpu()


def export_model(model: RealPositiveCenterNet, destination: Path, device: torch.device) -> ort.InferenceSession:
    model.eval().to(device)
    if destination.exists():
        raise FileExistsError("ONNX output already exists")
    with torch.inference_mode():
        torch.onnx.export(model, torch.zeros(1, 3, 33, 33, device=device), str(destination),
            input_names=["candidate_patches"], output_names=["candidate_predictions"],
            dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
            opset_version=18, dynamo=False)
    onnx.checker.check_model(onnx.load(str(destination)))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    return ort.InferenceSession(str(destination), sess_options=options, providers=["CPUExecutionProvider"])


def run(checkpoint: Path, checkpoint_sha256: str, base_path: Path, output: Path) -> dict:
    device = require_gpu()
    candidate = load_candidate(checkpoint, checkpoint_sha256, base_path)
    output = _project_file(output, existing=False)
    output.mkdir(exist_ok=False)
    torch.set_num_threads(1)
    packed = torch.load(BytesIO(verified_bytes(DEV, DEV_SHA)), map_location="cpu", weights_only=True)
    if (not isinstance(packed, dict) or set(packed) != set(DENOMINATOR) or
        {scope: (len(rows), sum(len(row["metadata"]["centers"]) for row in rows)) for scope, rows in packed.items()} != DENOMINATOR):
        raise ValueError("Frozen development denominator changed")
    scenes = {scope: tuple(unpack_scene(row, expected_split={"component": "dev", "family": "validation"}[scope])
                           for row in rows) for scope, rows in packed.items()}
    export = output / "marker-center.onnx"
    session = export_model(candidate, export, device)
    gpu_model = GpuEvaluationModel(candidate, device)
    development = evaluator.evaluate(SOURCE, scenes, gpu_model, session, WorkBudget(output / "CANCEL"), output,
                                     operating_threshold=OPERATING_THRESHOLD)
    validate_export_parity(development["parity"])
    historical_report, historical_cache = evaluator.historical._load_v27_cache(SOURCE)
    historical_geometry_maximum = 0.0
    for record in development["records"]:
        scope, index = record["scope"], record["scene_index"]
        entry = historical_report[scope + "_dev"]["cache_manifest"][index]
        original = historical_cache[entry["v27_candidate_predictions_key"]]
        actual = np.load(output / f"{scope}-{index:03d}-predictions.npy", allow_pickle=False)
        if actual.shape != original.shape:
            raise ValueError("Historical geometry denominator changed")
        difference = float(np.abs(original[:, 1:] - actual[:, 1:]).max())
        if not np.isfinite(difference) or difference > 1e-5:
            raise ValueError("ONNX geometry differs from historical frozen geometry")
        historical_geometry_maximum = max(historical_geometry_maximum, difference)
    if gpu_model.rows != development["parity"]["rows"] or not gpu_model.rows:
        raise ValueError("GPU and ONNX development denominators differ")
    reference = json.loads(verified_bytes(V37_REPORT, V37_REPORT_SHA).decode("utf-8-sig"))
    baseline = {scope: reference["development"]["results"][scope]["candidate_balanced"]["0.1"]
                for scope in ("component", "family")}
    verified_bytes(DEV, DEV_SHA)
    _read_bound(_project_file(checkpoint, existing=True), checkpoint_sha256)
    _read_bound(_project_file(base_path, existing=True), V37_CHECKPOINT_SHA256)
    report = {"status": "dev_pass" if development["clears_shared_dev_bars"] else "failed_dev_unconsumed",
        "checkpoint_sha256": checkpoint_sha256, "base_checkpoint_sha256": V37_CHECKPOINT_SHA256,
        "development_sha256": DEV_SHA, "v37_report_sha256": V37_REPORT_SHA,
        "v37_reference_at_0_1": baseline, "onnx_sha256": sha256(export.read_bytes()).hexdigest(),
        "device": "AMD Radeon RX 6600 XT gfx1032", "onnx_provider": "CPUExecutionProvider",
        "geometry_maximum_absolute_change": gpu_model.geometry_maximum_absolute_change,
        "onnx_historical_geometry_maximum_absolute_change": historical_geometry_maximum,
        "operating_threshold": OPERATING_THRESHOLD, "development": development,
        "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
        "production_approval": False, "release_eligible": False,
        "limitations": ["Synthetic historical component masks are procedural and do not validate real runtime inputs."]}
    (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2), encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--checkpoint-sha256", required=True)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.checkpoint, args.checkpoint_sha256, args.base, args.output)
    print(json.dumps({"status": report["status"], "onnx_sha256": report["onnx_sha256"]}))


if __name__ == "__main__":
    main()
