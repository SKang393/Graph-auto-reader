# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cache frozen-trunk features and verify step-zero parity on captured inputs.

The caller first authenticates the synthetic source/capture chain. This module
accepts only a hash-bound, model-free train/dev capture and never optimizes.
"""
from __future__ import annotations

from hashlib import sha256
import json
import math
from pathlib import Path
import time
from typing import Any

import numpy as np

from ml.ocr.official_bakeoff import frozen_trunk_head as head_api

TOLERANCE = 1e-5
SCHEMA = "graphreader.official-head-captured-step-zero-parity.v1"


class CapturedFeatureError(ValueError):
    """A capture, feature, or parity invariant failed."""


def _digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in items:
        if key in result:
            raise CapturedFeatureError("Duplicate JSON field")
        result[key] = value
    return result


def _bound(path: Path, expected: str) -> bytes:
    if (not isinstance(expected, str) or len(expected) != 64
            or any(c not in "0123456789abcdef" for c in expected)):
        raise CapturedFeatureError("Invalid SHA-256")
    payload = path.read_bytes()
    if sha256(payload).hexdigest() != expected:
        raise CapturedFeatureError("Bound input bytes changed")
    return payload


def _inside(root: Path, path: Path) -> Path:
    result = (root / path).resolve()
    if not result.is_relative_to(root):
        raise CapturedFeatureError("Artifact path escapes its root")
    return result


def load_capture_tensors(
    capture_path: Path, expected_sha256: str,
) -> tuple[dict[str, Any], tuple[tuple[dict[str, Any], np.ndarray], ...]]:
    """Authenticate the complete fixed 28/9 capture before any model loading."""
    payload = _bound(capture_path, expected_sha256)
    report = json.loads(payload, object_pairs_hook=_pairs)
    fixed = {
        "schema": "graphreader.official-head-tensor-capture-report.v1",
        "scope": "project-owned-synthetic-train-dev-model-free",
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "truth_used_by_capture": False, "model_inference": False,
        "training_input_ready": False, "production_approved": False,
        "detector_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
        "maximum_side_length": 960, "dimension_multiple": 128,
        "panel_count": 37, "failed_panel_count": 0,
    }
    if not isinstance(report, dict) or any(
        type(report.get(key)) is not type(value) or report.get(key) != value
        for key, value in fixed.items()
    ):
        raise CapturedFeatureError("Capture scope or fixed inventory changed")
    rows = report.get("panels")
    if not isinstance(rows, list) or len(rows) != 37:
        raise CapturedFeatureError("Capture must include every panel")
    seen: set[str] = set()
    splits = {"train": 0, "validation": 0}
    tensors = []
    for row in rows:
        if not isinstance(row, dict) or row.get("split") not in splits:
            raise CapturedFeatureError("Capture split changed")
        panel_id = row.get("panel_id")
        if (not isinstance(panel_id, str) or not panel_id or panel_id in seen
                or Path(panel_id).name != panel_id or any(c in panel_id for c in "/\\:")):
            raise CapturedFeatureError("Capture panel identity is unsafe or repeated")
        seen.add(panel_id)
        splits[row["split"]] += 1
        tensor = row.get("tensor")
        if not isinstance(tensor, dict):
            raise CapturedFeatureError("Capture tensor is missing")
        shape = tensor.get("shape")
        if (not isinstance(shape, list) or len(shape) != 4
                or any(type(v) is not int for v in shape) or shape[:2] != [1, 3]
                or any(v < 128 or v > 1024 or v % 128 for v in shape[2:])):
            raise CapturedFeatureError("Capture tensor shape changed")
        name = tensor.get("file")
        if not isinstance(name, str) or Path(name).is_absolute():
            raise CapturedFeatureError("Capture tensor path must be relative")
        path = _inside(capture_path.resolve().parent, Path(name))
        raw = _bound(path, tensor.get("sha256"))
        if (type(tensor.get("byte_count")) is not int
                or len(raw) != tensor["byte_count"] or len(raw) != math.prod(shape) * 4
                or tensor.get("dtype") != "float32-le"
                or tensor.get("input_name") != head_api.MODEL_INPUT_NAME
                or tensor.get("output_name") != head_api.MODEL_OUTPUT_NAME):
            raise CapturedFeatureError("Capture tensor contract changed")
        values = np.frombuffer(raw, dtype="<f4").reshape(shape).copy()
        if not np.isfinite(values).all():
            raise CapturedFeatureError("Capture tensor contains non-finite values")
        tensors.append((row, values))
    if splits != {"train": 28, "validation": 9}:
        raise CapturedFeatureError("Capture split inventory changed")
    return report, tuple(tensors)


def capture_features(
    *, repository_root: Path, parent_model: Path, capture_report: Path,
    capture_sha256: str, output_directory: Path,
) -> dict[str, Any]:
    """Run reviewed CPU feature extraction with the V43 step-zero recipe."""
    import onnxruntime as ort
    import torch

    root = repository_root.resolve()
    parent = _inside(root, parent_model)
    capture = _inside(root, capture_report)
    output = _inside(root, output_directory)
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise CapturedFeatureError("Use a new artifacts output directory")
    _, tensors = load_capture_tensors(capture, capture_sha256)
    _bound(parent, head_api.REVIEWED_DETECTOR_SHA256)
    helper_path = Path(__file__).resolve()
    source_path = Path(head_api.__file__).resolve()
    helper_sha, source_sha = _digest(helper_path), _digest(source_path)
    output.mkdir(parents=True, exist_ok=False)
    started = time.perf_counter()
    previous = (torch.get_num_threads(), torch.backends.mkldnn.enabled,
                torch.are_deterministic_algorithms_enabled(),
                torch.is_deterministic_algorithms_warn_only_enabled())
    try:
        torch.set_num_threads(1)
        torch.backends.mkldnn.enabled = False
        torch.use_deterministic_algorithms(True)
        bundle = head_api.extract_frozen_trunk_head(parent)
        feature_path = output / "frozen-trunk.onnx"
        feature_sha = head_api.write_frozen_trunk_model(parent, feature_path)
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        full = ort.InferenceSession(str(parent), options, providers=["CPUExecutionProvider"])
        trunk = ort.InferenceSession(str(feature_path), options, providers=["CPUExecutionProvider"])
        bundle.head.eval()
        rows = []
        with torch.inference_mode():
            for panel, values in tensors:
                expected = full.run([head_api.MODEL_OUTPUT_NAME], {head_api.MODEL_INPUT_NAME: values})[0]
                features = trunk.run([head_api.FEATURE_OUTPUT_NAME], {head_api.MODEL_INPUT_NAME: values})[0]
                required = (1, head_api.FEATURE_CHANNELS, values.shape[2] // 4, values.shape[3] // 4)
                if features.shape != required or not np.isfinite(features).all():
                    raise CapturedFeatureError("Frozen feature shape or values changed")
                actual = bundle.head(torch.from_numpy(np.ascontiguousarray(features))).numpy()
                if (expected.shape != (1, 1, values.shape[2], values.shape[3])
                        or actual.shape != expected.shape or not np.isfinite(expected).all()
                        or not np.isfinite(actual).all()):
                    raise CapturedFeatureError("Full detector/head outputs are incompatible")
                raw = np.ascontiguousarray(features, dtype="<f4").tobytes()
                name = panel["panel_id"] + ".features.f32"
                with (output / name).open("xb") as stream:
                    stream.write(raw)
                rows.append({
                    "split": panel["split"], "panel_id": panel["panel_id"],
                    "input_sha256": panel["tensor"]["sha256"], "shape": list(values.shape),
                    "feature_file": name, "feature_shape": list(features.shape),
                    "feature_sha256": sha256(raw).hexdigest(),
                    "maximum_absolute_error": float(np.max(np.abs(expected - actual))),
                })
        patch = head_api.patch_head_constants(parent, output / "no-op-detector.onnx", bundle.head)
        if patch.output_sha256 != head_api.REVIEWED_DETECTOR_SHA256 or patch.changed_constants:
            raise CapturedFeatureError("No-op export differs from reviewed parent")
        if (_digest(helper_path) != helper_sha or _digest(source_path) != source_sha
                or _digest(capture) != capture_sha256 or _digest(parent) != head_api.REVIEWED_DETECTOR_SHA256):
            raise CapturedFeatureError("Execution source changed during capture")
        maximum = max(row["maximum_absolute_error"] for row in rows)
        report = {
            "schema": SCHEMA, "source_model_sha256": head_api.REVIEWED_DETECTOR_SHA256,
            "source_code_sha256": source_sha, "helper_sha256": helper_sha,
            "capture_report_sha256": capture_sha256, "feature_model_sha256": feature_sha,
            "no_op_model_sha256": patch.output_sha256, "provider": "CPUExecutionProvider",
            "onnxruntime_version": ort.__version__, "torch_version": torch.__version__,
            "cpu_threads": 1, "torch_mkldnn_enabled": False, "graph_optimization": "ORT_ENABLE_ALL",
            "panels": rows, "panel_count": len(rows), "maximum_absolute_error": maximum,
            "tolerance": TOLERANCE, "passed": maximum <= TOLERANCE,
            "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
            "elapsed_ms": (time.perf_counter() - started) * 1000,
        }
        with (output / "report.json").open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(report, stream, indent=2, allow_nan=False)
            stream.write("\n")
        return report
    finally:
        torch.set_num_threads(previous[0])
        torch.backends.mkldnn.enabled = previous[1]
        torch.use_deterministic_algorithms(previous[2], warn_only=previous[3])
